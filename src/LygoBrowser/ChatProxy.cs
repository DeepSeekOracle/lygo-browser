using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace LygoBrowser;

/// <summary>
/// The chat brain bridge. The console is the real brain (Hermes + tools); when its provider
/// fails, the same question is answered by the NVIDIA bridge or the local engine, and the reply
/// always says which one actually answered.
/// </summary>
public static class ChatProxy
{
    public sealed class ChatReply
    {
        public bool Ok { get; init; }
        public string Brain { get; init; } = "";
        public string Model { get; init; } = "";
        public string Reply { get; init; } = "";
        public long Ms { get; init; }
        public long TotalMs { get; init; }
        public string Receipt { get; init; } = "";
        public string Error { get; init; } = "";
        public string Note { get; init; } = "";
        public List<string> Tried { get; init; } = new();
    }

    private const string ConsoleUrl = "http://127.0.0.1:9641/api/chat";
    private const string NvidiaUrl = "http://127.0.0.1:8082/v1/chat/completions";
    private const string LocalUrl = "http://127.0.0.1:8081/v1/chat/completions";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(150) };

    /// <summary>A failing provider can hang for minutes; the console attempt is capped so the
    /// chat falls through to a bridge the operator can actually see answering.</summary>
    private static readonly TimeSpan ConsoleBudget = TimeSpan.FromSeconds(30);

    /// <summary>After a provider-level console failure, skip it for a while instead of making
    /// every message wait out the budget. It recovers on its own.</summary>
    private static readonly TimeSpan ConsoleCooldown = TimeSpan.FromMinutes(5);
    private static DateTime _consoleCoolUntil = DateTime.MinValue;
    private static string _consoleLastError = "";

    private static bool ConsoleCooling => DateTime.UtcNow < _consoleCoolUntil;

    /// <summary>Grounding so the brain never confuses the link archive with a religious text.</summary>
    public static string Describe()
    {
        try
        {
            var doc = BibleStore.Load();
            var live = doc.Records.Where(r => r.Status == "active").ToList();
            var archived = live.Count(r => r.IsArchived);
            var folders = string.Join(", ", BibleStore.CanonFolders
                .Select(f => $"{f.Id}={live.Count(r => r.Folder == f.Id)}"));
            return "LYGO CONTEXT (live from this machine, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + "):\n"
                 + "- The Bookmark Bible is Justin Helmer's LOCAL LINK ARCHIVE, not a religious text: "
                 + $"{live.Count} records across 10 folders, {archived} archived. Folders: {folders}.\n"
                 + "- It stores title, url, folder, tags, note, and an archive (raw HTML, text, Markdown, PNG, SHA-256).\n"
                 + "- Ports: console 9641 (local LLM runtime + 103 tools), Forge 8796 (game studio), "
                 + "Manager 8790 (publish dashboard), browser bridge 9337 (this chat), CDP 9333.\n"
                 + "- Agents may open pages, read them, file links and archive them through the bridge API.\n"
                 + "- If something is not in this context and you cannot verify it, say so. Do not guess.";
        }
        catch (Exception ex)
        {
            Log.Warn("chat context: " + ex.Message);
            return "You are the LYGO agent inside the LYGO Browser. Answer factually; say when you do not know.";
        }
    }

    /// <summary>Provider failures are answered as normal text, so they have to be recognised.</summary>
    private static readonly string[] FailureMarkers =
    {
        "rejected your API key", "Invalid API Key", "HTTP 401", "was not processed"
    };

    public static ChatReply Ask(List<(string Role, string Content)> messages, string brain, int maxTokens)
    {
        var tried = new List<string>();
        var total = Stopwatch.StartNew();
        var note = ConsoleCooling
            ? $"console cooling until {_consoleCoolUntil:HH:mm:ss} (last: {_consoleLastError})"
            : "";
        foreach (var target in Order(brain))
        {
            tried.Add(target);
            var watch = Stopwatch.StartNew();
            try
            {
                var reply = target switch
                {
                    "console" => AskConsole(messages),
                    "nvidia" => AskOpenAi("nvidia", NvidiaUrl, Messages(messages), maxTokens),
                    _ => AskOpenAi("local", LocalUrl, Messages(messages), maxTokens)
                };
                watch.Stop();
                if (reply.Ok)
                {
                    reply.Tried.AddRange(tried);
                    return new ChatReply
                    {
                        Ok = true, Brain = reply.Brain, Model = reply.Model, Reply = reply.Reply,
                        Receipt = reply.Receipt, Ms = watch.ElapsedMilliseconds, Note = note,
                        TotalMs = total.ElapsedMilliseconds, Tried = tried
                    };
                }
                Log.Warn($"chat {target}: {reply.Error}");
                if (target == "console")
                {
                    _consoleCoolUntil = DateTime.UtcNow + ConsoleCooldown;
                    _consoleLastError = reply.Error;
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"chat {target}: {ex.GetType().Name} {ex.Message}");
                if (target == "console")
                {
                    _consoleCoolUntil = DateTime.UtcNow + ConsoleCooldown;
                    _consoleLastError = ex is OperationCanceledException
                        ? "timed out after " + (int)ConsoleBudget.TotalSeconds + "s"
                        : ex.Message;
                }
            }
        }
        return new ChatReply
        {
            Ok = false, Brain = "none", Error = "no brain answered — is the console or a bridge up?",
            TotalMs = total.ElapsedMilliseconds, Tried = tried
        };
    }

    private static string[] Order(string brain) => brain switch
    {
        "console" => new[] { "console" },
        "nvidia" => new[] { "nvidia" },
        "local" => new[] { "local" },
        _ => ConsoleCooling ? new[] { "nvidia", "local" } : new[] { "console", "nvidia", "local" }
    };

    private static object Messages(List<(string Role, string Content)> messages) =>
        messages.Select(m => new { role = m.Role, content = m.Content }).ToArray();

    // ------------------------------------------------------------------ console (SSE)

    private static ChatReply AskConsole(List<(string Role, string Content)> messages)
    {
        var payload = JsonSerializer.Serialize(new
        {
            messages = Messages(messages),
            tools = false,
            stream = false
        });
        using var cts = new CancellationTokenSource(ConsoleBudget);
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = Http.PostAsync(ConsoleUrl, content, cts.Token).GetAwaiter().GetResult();
        var body = response.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            return Fail("console", "HTTP " + (int)response.StatusCode);

        var text = new StringBuilder();
        var model = "";
        var receipt = "";
        foreach (var frame in SseFrames(body))
        {
            var type = FrameType(frame);
            if (type == "token")
            {
                if (frame.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                    text.Append(delta.GetString());
            }
            else if (type == "brain")
            {
                model = Str(frame, "model");
            }
            else if (type == "done")
            {
                receipt = Str(frame, "receipt");
                if (model.Length == 0) model = Str(frame, "model");
            }
        }

        var reply = text.ToString().Trim();
        if (reply.Length == 0) return Fail("console", "empty reply");
        if (FailureMarkers.Any(m => reply.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return Fail("console", "provider error: " + Short(reply, 90));
        return new ChatReply
        {
            Ok = true, Brain = "console", Model = model.Length > 0 ? model : "console",
            Reply = reply, Receipt = receipt
        };
    }

    private static IEnumerable<JsonElement> SseFrames(string body)
    {
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var json = line[5..].Trim();
            if (json.Length == 0 || json == "[DONE]") continue;
            JsonElement element;
            try
            {
                using var doc = JsonDocument.Parse(json);
                element = doc.RootElement.Clone();
            }
            catch { continue; }
            yield return element;
        }
    }

    private static string FrameType(JsonElement frame) => Str(frame, "type");

    private static string Str(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    // ------------------------------------------------------------------ OpenAI-compatible

    private static ChatReply AskOpenAi(string brain, string url, object messages, int maxTokens)
    {
        var payload = JsonSerializer.Serialize(new
        {
            model = "lygo-browser",
            messages,
            stream = false,
            max_tokens = Math.Clamp(maxTokens, 256, 8192)
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer local-bridge");
        using var response = Http.Send(request);
        var body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode)
            return Fail(brain, "HTTP " + (int)response.StatusCode + " " + Short(body, 120));

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var choice = root.GetProperty("choices")[0].GetProperty("message");
            var reply = Str(choice, "content");
            var model = Str(root, "model");
            if (string.IsNullOrWhiteSpace(reply)) return Fail(brain, "empty reply");
            return new ChatReply
            {
                Ok = true, Brain = brain, Model = model.Length > 0 ? model : brain, Reply = reply.Trim()
            };
        }
        catch (Exception ex)
        {
            return Fail(brain, "bad json: " + ex.Message);
        }
    }

    private static ChatReply Fail(string brain, string error) =>
        new() { Ok = false, Brain = brain, Error = error };

    private static string Short(string value, int max) =>
        string.IsNullOrEmpty(value) ? "" : (value.Length <= max ? value : value[..max] + "…");
}
