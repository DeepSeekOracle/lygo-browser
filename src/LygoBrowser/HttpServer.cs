using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LygoBrowser;

public sealed class HttpRequest
{
    public string Method { get; init; } = "GET";
    public string Path { get; init; } = "/";
    public Dictionary<string, string> Query { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string Body { get; init; } = "";

    public string QueryValue(string key, string fallback = "") =>
        Query.TryGetValue(key, out var v) ? v : fallback;
}

public sealed class HttpResponse
{
    public int Status { get; set; } = 200;
    public string ContentType { get; set; } = "application/json; charset=utf-8";
    public byte[] Body { get; set; } = Array.Empty<byte>();
    public Dictionary<string, string> Headers { get; } = new();

    public static HttpResponse Json(object payload, int status = 200)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(payload, JsonOpts);
        return new HttpResponse
        {
            Status = status,
            ContentType = "application/json; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(json)
        };
    }

    public static HttpResponse Text(string text, string type = "text/plain; charset=utf-8", int status = 200)
        => new() { Status = status, ContentType = type, Body = Encoding.UTF8.GetBytes(text) };

    public static HttpResponse Bytes(byte[] data, string type)
        => new() { Status = 200, ContentType = type, Body = data };

    public static HttpResponse Error(string message, int status = 400)
        => Json(new { ok = false, error = message }, status);

    public static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}

/// <summary>
/// Minimal HTTP/1.1 server on a loopback TcpListener. Chosen over HttpListener because
/// http.sys needs a URL reservation (admin) for non-localhost prefixes; a raw socket does not.
/// One request per connection, Connection: close.
/// </summary>
public sealed class HttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<HttpRequest, HttpResponse> _handler;
    private volatile bool _running;

    public HttpServer(int port, Func<HttpRequest, HttpResponse> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        _running = true;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (_running)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { if (!_running) return; continue; }
            _ = Task.Run(() => Handle(client));
        }
    }

    private void Handle(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 15000;
                client.SendTimeout = 15000;
                using var stream = client.GetStream();
                var request = ReadRequest(stream);
                if (request == null) return;
                HttpResponse response;
                try { response = _handler(request) ?? HttpResponse.Error("no handler", 500); }
                catch (Exception ex)
                {
                    Log.Error("handler: " + ex);
                    response = HttpResponse.Error("internal error: " + ex.Message, 500);
                }
                WriteResponse(stream, response);
            }
            catch (Exception ex)
            {
                Log.Warn("http connection: " + ex.GetType().Name);
            }
        }
    }

    private static HttpRequest ReadRequest(NetworkStream stream)
    {
        var block = ReadHeaderBlock(stream, 64 * 1024);
        if (string.IsNullOrEmpty(block)) return null;

        var lines = block.Split("\r\n", StringSplitOptions.None);
        var (method, path, rawQuery) = ParseRequestLine(lines[0]);
        if (method == null) return null;

        var headers = ParseHeaders(lines);
        var body = ReadBody(stream, headers);

        var request = new HttpRequest { Method = method, Path = path, Body = body };
        foreach (var kv in headers) request.Headers[kv.Key] = kv.Value;
        ParseQuery(rawQuery, request);
        return request;
    }

    private static (string Method, string Path, string Query) ParseRequestLine(string line)
    {
        var parts = line.Split(' ');
        if (parts.Length < 2) return (null, null, null);
        var target = parts[1];
        var qIndex = target.IndexOf('?');
        var path = qIndex >= 0 ? target[..qIndex] : target;
        var query = qIndex >= 0 ? target[(qIndex + 1)..] : "";
        if (path.Contains('%')) path = Uri.UnescapeDataString(path);
        return (parts[0].ToUpperInvariant(), path, query);
    }

    private static Dictionary<string, string> ParseHeaders(string[] lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var idx = line.IndexOf(':');
            if (idx <= 0) continue;
            headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        return headers;
    }

    private static string ReadBody(NetworkStream stream, Dictionary<string, string> headers)
    {
        if (!headers.TryGetValue("Content-Length", out var lenText)) return "";
        if (!int.TryParse(lenText, out var len) || len <= 0 || len >= 32 * 1024 * 1024) return "";
        var buf = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = stream.Read(buf, read, len - read);
            if (n <= 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buf, 0, read);
    }

    private static void ParseQuery(string rawQuery, HttpRequest request)
    {
        foreach (var pair in rawQuery.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq >= 0 ? pair[..eq] : pair;
            var value = eq >= 0 ? pair[(eq + 1)..] : "";
            request.Query[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
    }

    private static string ReadHeaderBlock(NetworkStream stream, int cap)
    {
        var sb = new StringBuilder();
        var one = new byte[1];
        while (sb.Length < cap)
        {
            int n;
            try { n = stream.Read(one, 0, 1); } catch { return null; }
            if (n <= 0) break;
            sb.Append((char)one[0]);
            if (sb.Length >= 4 && sb.ToString(sb.Length - 4, 4) == "\r\n\r\n")
                return sb.ToString();
        }
        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static void WriteResponse(NetworkStream stream, HttpResponse response)
    {
        var head = new StringBuilder();
        head.Append($"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n");
        head.Append($"Content-Type: {response.ContentType}\r\n");
        head.Append($"Content-Length: {response.Body.Length}\r\n");
        head.Append("Cache-Control: no-store\r\n");
        head.Append("X-LYGO-Browser: 1.0\r\n");
        // CORS is emitted per response in LygoService, scoped to the LYGO consoles only.
        foreach (var kv in response.Headers) head.Append($"{kv.Key}: {kv.Value}\r\n");
        head.Append("Connection: close\r\n\r\n");
        var headBytes = Encoding.ASCII.GetBytes(head.ToString());
        stream.Write(headBytes, 0, headBytes.Length);
        if (response.Body.Length > 0) stream.Write(response.Body, 0, response.Body.Length);
        stream.Flush();
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK", 201 => "Created", 400 => "Bad Request", 401 => "Unauthorized",
        403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed",
        409 => "Conflict", 500 => "Internal Server Error", _ => "OK"
    };

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
    }
}
