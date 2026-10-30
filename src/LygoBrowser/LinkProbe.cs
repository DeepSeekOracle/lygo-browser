using System.Net.Http;
using System.Text.Json;

namespace LygoBrowser;

/// <summary>
/// Live state of the LYGO services the browser is wired to. Probed server-side so the HUD never
/// trips over CORS, and cached for a couple of seconds so polling is cheap.
/// </summary>
public static class LinkProbe
{
    private sealed record Endpoint(string Id, string Label, string Url, string Kind, int TimeoutMs);

    private static readonly Endpoint[] Endpoints =
    {
        new("console", "CONSOLE", "http://127.0.0.1:9641/api/health", "console", 3000),
        new("forge", "FORGE", "http://127.0.0.1:8796/api/status", "forge", 8000),
        new("manager", "MANAGER", "http://127.0.0.1:8790/", "plain", 2500),
        new("bridge", "BRIDGE", "http://127.0.0.1:8081/health", "plain", 2000),
        new("nvidia", "NVIDIA", "http://127.0.0.1:8082/health", "plain", 2000),
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };
    private static readonly object Gate = new();
    private static string _cached;
    private static DateTime _cachedAt = DateTime.MinValue;

    /// <summary>JSON snapshot of every link plus the browser's own identity.</summary>
    public static string Snapshot(bool force = false)
    {
        lock (Gate)
        {
            if (!force && _cached != null && (DateTime.UtcNow - _cachedAt).TotalSeconds < 2.5)
                return _cached;
        }

        var results = new Dictionary<string, object>();
        var tasks = Endpoints.Select(e => ProbeAsync(e)).ToArray();
        Task.WaitAll(tasks);

        foreach (var task in tasks)
        {
            var probe = task.Result;
            results[probe.Id] = probe;
        }

        results["browser"] = new
        {
            up = true,
            label = "BROWSER",
            detail = "1.9.1 · engine " + "WebView2",
            url = "http://127.0.0.1:" + Paths.BridgePort + "/",
            ms = 0
        };

        var payload = JsonSerializer.Serialize(new
        {
            at = BibleStore.NowIso(),
            links = results,
            upCount = results.Values.Count(v => v is LinkProbeResult p && p.Up) + 1
        }, HttpResponse.JsonOpts);

        lock (Gate) { _cached = payload; _cachedAt = DateTime.UtcNow; }
        return payload;
    }



    private static async Task<LinkProbeResult> ProbeAsync(Endpoint endpoint)
    {
        var started = DateTime.UtcNow;
        try
        {
            using var cts = new CancellationTokenSource(endpoint.TimeoutMs);
            using var response = await Http.GetAsync(endpoint.Url, cts.Token);
            var body = await response.Content.ReadAsStringAsync();
            var ms = (long)(DateTime.UtcNow - started).TotalMilliseconds;
            if (!response.IsSuccessStatusCode)
                return new LinkProbeResult(endpoint.Id, endpoint.Label, false, "HTTP " + (int)response.StatusCode,
                                 endpoint.Url, ms);
            return new LinkProbeResult(endpoint.Id, endpoint.Label, true, Detail(endpoint.Kind, body),
                             endpoint.Url, ms);
        }
        catch (Exception ex)
        {
            var ms = (long)(DateTime.UtcNow - started).TotalMilliseconds;
            return new LinkProbeResult(endpoint.Id, endpoint.Label, false,
                             ex is TaskCanceledException || ex is System.OperationCanceledException ? "timeout" : "offline", endpoint.Url, ms);
        }
    }

    private static string Detail(string kind, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (kind == "console")
            {
                var build = Str(root, "build", "?");
                var brain = Str(root, "brain", "?");
                var model = Str(root, "selected", "");
                return $"{build} · brain {brain}{(model.Length > 0 ? " · " + model : "")}";
            }
            if (kind == "forge")
            {
                var version = Str(root, "version", "?");
                var consoleUp = root.TryGetProperty("console", out var c)
                                && c.TryGetProperty("up", out var u) && u.ValueKind == JsonValueKind.True;
                return $"{version} · console {(consoleUp ? "up" : "down")}";
            }
            if (root.TryGetProperty("status", out var status)) return Str(root, "status", "ok");
            return "ok";
        }
        catch
        {
            return "up";
        }
    }

    private static string Str(JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? (value.GetString() ?? fallback)
            : fallback;
}

/// <summary>One probed service. Public so System.Text.Json can serialise it.</summary>
public sealed class LinkProbeResult
{
    public string Id { get; init; } = "";
    public string Label { get; init; } = "";
    public bool Up { get; init; }
    public string Detail { get; init; } = "";
    public string Url { get; init; } = "";
    public long Ms { get; init; }

    public LinkProbeResult(string id, string label, bool up, string detail, string url, long ms)
    {
        Id = id; Label = label; Up = up; Detail = detail; Url = url; Ms = ms;
    }
}