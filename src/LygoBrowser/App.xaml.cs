using System.Net.Http;
using System.Text;
using System.Windows;

namespace LygoBrowser;

/// <summary>
/// Entry point. The browser is single-instance: a second launch (from the Manager, a shortcut or
/// `lygo://`) forwards its URL to the running window instead of opening a rival instance that
/// would lose the bridge port.
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A throw on a background thread must never take the whole browser down.
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("dispatcher: " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("domain: " + (args.ExceptionObject as Exception)?.Message);
        var url = StartupUrl(e.Args);
        if (url != null && ForwardToRunning(url)) { Shutdown(); return; }

        var window = new MainWindow(url);
        MainWindow = window;
        window.Show();
        base.OnStartup(e);
    }

    /// <summary>First http(s) or lygo:// argument, normalised. Null when nothing usable was passed.</summary>
    private static string StartupUrl(string[] args)
    {
        foreach (var raw in args ?? Array.Empty<string>())
        {
            var value = (raw ?? "").Trim().Trim('"');
            if (value.Length == 0 || value.StartsWith("--")) continue;
            if (value.StartsWith("lygo://", StringComparison.OrdinalIgnoreCase)) return null; // just "open"
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return value;
            if (value.Contains('.') && !value.Contains(' '))
                return "https://" + value.TrimStart('/');
        }
        return null;
    }

    private static bool BridgeIsUp()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(700) };
            using var response = http.GetAsync("http://127.0.0.1:" + Paths.BridgePort + "/api/health")
                                      .GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    /// <summary>Hand the URL to the instance that already owns the bridge. True when it took it.</summary>
    private static bool ForwardToRunning(string url)
    {
        if (!BridgeIsUp()) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var request = new HttpRequestMessage(
                HttpMethod.Post, "http://127.0.0.1:" + Paths.BridgePort + "/api/agent/open");
            request.Headers.TryAddWithoutValidation("X-LYGO-Token", Paths.Token());
            request.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { url, newTab = true }),
                Encoding.UTF8, "application/json");
            using var response = http.Send(request);
            Log.Info($"forwarded to running instance: {url} -> {(int)response.StatusCode}");
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Log.Warn("forward failed: " + ex.Message);
            return false;
        }
    }
}
