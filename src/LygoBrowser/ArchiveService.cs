using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace LygoBrowser;

/// <summary>What the browser window must provide to the service. Implemented by MainWindow.</summary>
public interface IBrowserHost
{
    Task<string> OpenAsync(string url, bool newTab);
    List<TabInfo> Tabs();
    Task<string> GetTextAsync(int? tabId);
    Task<object> SnapshotAsync(int? tabId);
    Task<string> HtmlAsync(int? tabId);
    Task<string> EvalAsync(string js, int? tabId);
    Task<string> ScreenshotAsync(int? tabId);
    Task<bool> ClickAsync(string selector, int? tabId);
    Task<bool> TypeAsync(string selector, string text, int? tabId);
    Task<bool> CloseTabAsync(int tabId);
    Task<object> ArchiveAsync(string url, string title, string id);
    Task<object> FindAsync(string text, bool forward, int? tabId);
    Task<object> SnapAsync(string mode, int? tabId);
    void RefreshBookmarks();
    Task<int> CookieCountAsync();
    Task<bool> ClearCookiesAsync();
    Task<object> SetZoomAsync(double factor, int? tabId);
    Task<string> SavePdfAsync(int? tabId);
    List<HistoryEntry> HistoryEntries(int limit);
    List<DownloadEntry> DownloadsList();
    Settings CurrentSettings();
    void ApplySettings(Settings s);
    string EngineVersion();
}

/// <summary>
/// Captures a page from a live WebView2: raw HTML, text, a full-page PNG and a hash.
/// Raw source is kept (never only a summary) so references stay checkable years later.
/// </summary>
public static class ArchiveService
{
    public sealed class Result
    {
        public ArchiveState State { get; init; }
        public string Sha256 { get; init; }
        public List<string> Files { get; init; }
        public long Bytes { get; init; }
    }

    public static async Task<Result> CaptureAsync(WebView2 view, string url, string title, string id)
    {
        var core = view.CoreWebView2 ?? throw new InvalidOperationException("webview not ready");

        var htmlJson = await core.ExecuteScriptAsync("document.documentElement.outerHTML");
        var html = DecodeJsString(htmlJson);
        var textJson = await core.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
        var text = DecodeJsString(textJson);

        var png = await CaptureFullPageAsync(core);

        var dir = Path.Combine(Paths.ArchiveDir, id);
        Directory.CreateDirectory(dir);

        var htmlPath = Path.Combine(dir, "page.html");
        var textPath = Path.Combine(dir, "page.txt");
        var mdPath = Path.Combine(dir, "page.md");
        var shotPath = Path.Combine(dir, "shot.png");
        var metaPath = Path.Combine(dir, "meta.json");

        var htmlBytes = Encoding.UTF8.GetBytes(html);
        File.WriteAllBytes(htmlPath, htmlBytes);
        await File.WriteAllTextAsync(textPath, text ?? "", new UTF8Encoding(false));
        await File.WriteAllTextAsync(mdPath,
            $"# {title}\n\n> archived from {url} on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z\n> " +
            $"raw source: page.html · text: page.txt\n\n{text}\n", new UTF8Encoding(false));
        if (png != null && png.Length > 0)
        {
            File.WriteAllBytes(shotPath, png);
            TryWriteThumb(shotPath, Path.Combine(dir, "thumb.png"), 520);
        }

        var sha = Convert.ToHexString(SHA256.HashData(htmlBytes)).ToLowerInvariant();
        var meta = new
        {
            id, url, title, sha256 = sha, bytes = htmlBytes.LongLength,
            capturedAt = BibleStore.NowIso(),
            files = new[] { "page.html", "page.txt", "page.md", "shot.png", "thumb.png" },
            engine = "WebView2"
        };
        await File.WriteAllTextAsync(metaPath,
            JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        var state = new ArchiveState
        {
            At = BibleStore.NowIso(),
            Html = $"archive/{id}/page.html",
            Md = $"archive/{id}/page.md",
            Shot = (png != null && png.Length > 0) ? $"archive/{id}/shot.png" : null,
            Bytes = htmlBytes.LongLength
        };
        return new Result
        {
            State = state,
            Sha256 = sha,
            Bytes = htmlBytes.LongLength,
            Files = new List<string> { "page.html", "page.txt", "page.md", "shot.png", "meta.json" }
        };
    }

    /// <summary>Decode-downscaled preview so the archive grid does not load megabyte PNGs.</summary>
    private static void TryWriteThumb(string sourcePng, string destPng, int width)
    {
        try
        {
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(sourcePng);
            bmp.DecodePixelWidth = width;
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            bmp.EndInit();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var fs = File.Create(destPng);
            encoder.Save(fs);
            bmp.Freeze();
        }
        catch (Exception ex)
        {
            Log.Warn("thumb failed: " + ex.Message);
        }
    }

    /// <summary>Full-page PNG via CDP, capped so a 30k-pixel page cannot blow up the archive.</summary>
    public static async Task<byte[]> CaptureFullPageAsync(CoreWebView2 core)
    {
        try
        {
            double width = 1280, height = 800;
            var metrics = await core.CallDevToolsProtocolMethodAsync("Page.getLayoutMetrics", "{}");
            using (var doc = JsonDocument.Parse(metrics))
            {
                if (doc.RootElement.TryGetProperty("cssContentSize", out var css))
                {
                    width = css.GetProperty("width").GetDouble();
                    height = css.GetProperty("height").GetDouble();
                }
            }
            width = Math.Clamp(width, 200, 4000);
            height = Math.Clamp(height, 200, 12000);
            var args = JsonSerializer.Serialize(new
            {
                format = "png",
                captureBeyondViewport = true,
                clip = new { x = 0, y = 0, width, height, scale = 1 }
            });
            var result = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args);
            return ExtractBase64(result);
        }
        catch (Exception ex)
        {
            Log.Warn("screenshot failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Viewport-only PNG (what the operator is looking at right now).</summary>
    public static async Task<byte[]> CaptureViewportAsync(CoreWebView2 core)
    {
        try
        {
            var result = await core.CallDevToolsProtocolMethodAsync("Page.captureScreenshot",
                "{\"format\":\"png\"}");
            return ExtractBase64(result);
        }
        catch (Exception ex)
        {
            Log.Warn("viewport shot failed: " + ex.Message);
            return null;
        }
    }

    private static byte[] ExtractBase64(string cdpJson)
    {
        using var doc = JsonDocument.Parse(cdpJson);
        if (!doc.RootElement.TryGetProperty("data", out var data)) return null;
        var b64 = data.GetString();
        return string.IsNullOrEmpty(b64) ? null : Convert.FromBase64String(b64);
    }

    private static string DecodeJsString(string json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        try { return JsonSerializer.Deserialize<string>(json) ?? ""; }
        catch { return json; }
    }
}
