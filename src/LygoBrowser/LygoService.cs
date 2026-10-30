using System.IO;
using System.Text;
using System.Text.Json;

namespace LygoBrowser;

/// <summary>
/// The local face of the browser: the HUD pages, the Bible API, the vault API and the agent
/// API all live on 127.0.0.1:9337. Reads are open on loopback; every mutation needs the
/// per-install token (data/state/bridge.token) which the HUD receives injected.
/// </summary>
public sealed class LygoService : IDisposable
{
    private readonly IBrowserHost _host;
    private readonly HttpServer _server;
    private readonly string _token;

    public int Port { get; }

    public LygoService(IBrowserHost host, int port = 9337)
    {
        _host = host;
        Port = port;
        _token = Paths.Token();
        _server = new HttpServer(port, Route);
    }

    public void Start()
    {
        _server.Start();
        Log.Info($"bridge listening http://127.0.0.1:{Port}/");
    }

    public void Dispose() => _server.Dispose();

    // ------------------------------------------------------------------ routing

    /// <summary>Only the local LYGO consoles may read our API from their own origin.</summary>
    private static readonly string[] AllowedOrigins =
    {
        "http://127.0.0.1:8790", "http://localhost:8790",
        "http://127.0.0.1:8796", "http://localhost:8796",
        "http://127.0.0.1:9641", "http://localhost:9641"
    };

    /// <summary>Reject a Host that is not our own loopback name (blocks DNS rebinding).</summary>
    private static bool HostAllowed(HttpRequest req)
    {
        if (!req.Headers.TryGetValue("Host", out var host) || string.IsNullOrWhiteSpace(host)) return true;
        var name = host.Split(':')[0].Trim().ToLowerInvariant();
        return name is "127.0.0.1" or "localhost" or "[::1]" or "::1";
    }

    private HttpResponse Route(HttpRequest req)
    {
        try
        {
            if (!HostAllowed(req)) return HttpResponse.Error("host not allowed", 403);
            if (req.Method == "POST" && !Authorized(req))
                return HttpResponse.Error("bad or missing X-LYGO-Token", 401);

            var path = req.Path.TrimEnd('/');
            var response = Dispatch(req, path.Length == 0 ? "/" : path);

            // CORS is scoped: no "*" any more, so a random website cannot read the Bible.
            if (req.Headers.TryGetValue("Origin", out var origin))
            {
                var match = AllowedOrigins.FirstOrDefault(o =>
                    string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));
                if (match != null) response.Headers["Access-Control-Allow-Origin"] = match;
                response.Headers["Vary"] = "Origin";
            }
            return response;
        }
        catch (Exception ex)
        {
            Log.Error("route " + req.Path + ": " + ex.Message);
            return HttpResponse.Error(ex.Message, 500);
        }
    }

    private HttpResponse Dispatch(HttpRequest req, string path) => path switch
    {
                "/api/health" => HttpResponse.Json(Health()),
                "/api/links" => HttpResponse.Text(LinkProbe.Snapshot(), "application/json; charset=utf-8"),
                "/api/agent/chat" => AgentChat(req),
                "/api/snap" => Snap(req),
                "/api/snaps" => HttpResponse.Json(new { items = SnapshotStore.All() }),
                "/api/snaps/delete" => SnapDelete(req),
                "/api/cookies" => HttpResponse.Json(new { count = _host.CookieCountAsync().GetAwaiter().GetResult() }),
                "/api/cookies/clear" => HttpResponse.Json(new { ok = _host.ClearCookiesAsync().GetAwaiter().GetResult() }),
                "/api/bible" => HttpResponse.Json(new
                {
                    folders = BibleStore.Load().Folders,
                    records = BibleStore.Load().Records.Where(r => r.Status == "active").ToList()
                }),
                "/api/bible/search" => Search(req),
                "/api/bible/stats" => HttpResponse.Json(BibleStore.Stats()),
                "/api/bible/add" => BibleAdd(req),
                "/api/bible/update" => BibleUpdate(req),
                "/api/bible/archive" => BibleArchive(req),
                "/api/bible/fav" => BibleFav(req),
                "/api/archive" => HttpResponse.Json(new { items = ArchiveItems() }),
                "/api/history" => History(req),
                "/api/history/clear" => ClearHistory(),
                "/api/downloads" => HttpResponse.Json(new { items = _host.DownloadsList() }),
                "/api/settings" => SettingsRoute(req),
                "/api/page/pdf" => Pdf(req),
                "/api/vault" => HttpResponse.Json(new { items = VaultStore.List() }),
                "/api/vault/set" => VaultSet(req),
                "/api/vault/reveal" => VaultReveal(req),
                "/api/vault/remove" => VaultRemove(req),
                "/api/vault/fill" => VaultFill(req),
                "/api/agent/status" => HttpResponse.Json(new
                {
                    tabs = _host.Tabs(), cdpPort = Paths.CdpPort, engine = _host.EngineVersion()
                }),
                "/api/agent/open" => AgentOpen(req),
                "/api/open" => AgentOpen(req),
                "/api/agent/snapshot" => AgentSnapshot(req),
                "/api/agent/screenshot" => AgentScreenshot(req),
                "/api/agent/html" => AgentHtml(req),
                "/api/agent/eval" => AgentEval(req),
                "/api/agent/click" => AgentClick(req),
                "/api/agent/type" => AgentType(req),
                "/api/agent/find" => AgentFind(req),
                "/api/agent/zoom" => AgentZoom(req),
                "/api/agent/close" => AgentClose(req),
        _ => ServeFile(path)
    };

    private bool Authorized(HttpRequest req) =>
        req.Headers.TryGetValue("X-LYGO-Token", out var t)
        && string.Equals(t, _token, StringComparison.Ordinal);

    // ------------------------------------------------------------------ api impl

    private object Health() => new
    {
        ok = true,
        name = "LYGO Browser",
        version = "1.9.1",
        cdp = Paths.CdpPort,
        bridge = Port.ToString(),
        archiveCount = Directory.Exists(Paths.ArchiveDir)
            ? Directory.GetDirectories(Paths.ArchiveDir).Length : 0,
        vaultCount = VaultStore.Count(),
        recordCount = BibleStore.Load().Records.Count(r => r.Status == "active"),
        historyCount = HistoryStore.Count(),
        downloadCount = _host.DownloadsList().Count,
        engine = _host.EngineVersion(),
        root = Paths.Root
    };

    private HttpResponse History(HttpRequest req)
    {
        var limit = int.TryParse(req.QueryValue("limit", "300"), out var l) ? l : 300;
        var q = req.QueryValue("q");
        var items = _host.HistoryEntries(Math.Clamp(limit, 1, 5000));
        if (!string.IsNullOrWhiteSpace(q))
        {
            var terms = q.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            items = items.Where(h => terms.All(t =>
                (h.Url + " " + h.Title).ToLowerInvariant().Contains(t))).ToList();
        }
        return HttpResponse.Json(new { items = Enumerable.Reverse(items).ToList() });
    }

    private static HttpResponse ClearHistory()
    {
        HistoryStore.Clear();
        return HttpResponse.Json(new { ok = true });
    }

    private HttpResponse SettingsRoute(HttpRequest req)
    {
        if (req.Method == "GET") return HttpResponse.Json(_host.CurrentSettings());
        Settings incoming;
        try
        {
            incoming = System.Text.Json.JsonSerializer.Deserialize<Settings>(
                string.IsNullOrWhiteSpace(req.Body) ? "{}" : req.Body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new Settings();
        }
        catch (Exception ex) { return HttpResponse.Error("bad settings: " + ex.Message); }
        _host.ApplySettings(incoming);
        return HttpResponse.Json(new { ok = true, settings = _host.CurrentSettings() });
    }

    private HttpResponse Pdf(HttpRequest req)
    {
        var path = _host.SavePdfAsync(TabId(req)).GetAwaiter().GetResult();
        return path == null
            ? HttpResponse.Error("pdf export failed", 500)
            : HttpResponse.Json(new { ok = true, path });
    }

    private HttpResponse AgentHtml(HttpRequest req)
    {
        var html = _host.HtmlAsync(TabId(req)).GetAwaiter().GetResult();
        return HttpResponse.Json(new { ok = true, html, bytes = html.Length });
    }

    private HttpResponse AgentEval(HttpRequest req)
    {
        using var doc = Parse(req);
        var js = Str(doc.RootElement, "js");
        if (string.IsNullOrWhiteSpace(js)) return HttpResponse.Error("js required");
        Log.Info("agent eval " + js.Length + " chars");
        var result = _host.EvalAsync(js, TabId(req)).GetAwaiter().GetResult();
        return HttpResponse.Json(new { ok = true, result });
    }

    private static HttpResponse Search(HttpRequest req)
    {
        var q = req.QueryValue("q");
        var limit = int.TryParse(req.QueryValue("limit", "40"), out var l) ? l : 40;
        var hits = BibleStore.Search(q, limit).Select(h => h.Record).ToList();
        return HttpResponse.Json(new { hits });
    }

    private static HttpResponse BibleAdd(HttpRequest req)
    {
        using var doc = Parse(req);
        var root = doc.RootElement;
        var url = Str(root, "url");
        if (string.IsNullOrWhiteSpace(url)) return HttpResponse.Error("url required");
        var tags = Str(root, "tags");
        var res = BibleStore.Add(
            url,
            Str(root, "title"),
            Str(root, "folder"),
            tags.Split(',', StringSplitOptions.RemoveEmptyEntries),
            Str(root, "note"),
            string.IsNullOrWhiteSpace(Str(root, "source")) ? "manual" : Str(root, "source"));
        return HttpResponse.Json(new { ok = true, status = res.Status, record = res.Record });
    }

    private static HttpResponse BibleUpdate(HttpRequest req)
    {
        using var doc = Parse(req);
        var root = doc.RootElement;
        var rec = BibleStore.Update(Str(root, "id"), StrOrNull(root, "title"), Str(root, "folder"),
                                    StrOrNull(root, "tags"), StrOrNull(root, "note"), StrOrNull(root, "status"));
        return rec == null ? HttpResponse.Error("no such record", 404) : HttpResponse.Json(new { ok = true, record = rec });
    }

    private HttpResponse BibleArchive(HttpRequest req)
    {
        using var doc = Parse(req);
        var root = doc.RootElement;
        var id = Str(root, "id");
        var url = Str(root, "url");
        var rec = BibleStore.Load().Records.FirstOrDefault(r => r.Id == id)
                  ?? BibleStore.Load().Records.FirstOrDefault(r => r.Url == BibleStore.Normalize(url));
        if (rec == null) return HttpResponse.Error("record not found", 404);

        var result = _host.ArchiveAsync(rec.Url, rec.Title, rec.Id).GetAwaiter().GetResult();
        if (result == null) return HttpResponse.Json(new { ok = false, error = "capture failed" });
        return HttpResponse.Json(result);
    }

    private static object[] ArchiveItems()
    {
        if (!Directory.Exists(Paths.ArchiveDir)) return Array.Empty<object>();
        var byId = BibleStore.Load().Records.ToDictionary(r => r.Id, r => r);
        var items = new List<object>();
        foreach (var dir in Directory.GetDirectories(Paths.ArchiveDir))
        {
            var id = Path.GetFileName(dir);
            var metaPath = Path.Combine(dir, "meta.json");
            string title = id, url = "", at = "", sha = "";
            long bytes = 0;
            if (File.Exists(metaPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                    var r = doc.RootElement;
                    title = r.TryGetProperty("title", out var t) ? t.GetString() : title;
                    url = r.TryGetProperty("url", out var u) ? u.GetString() : "";
                    at = r.TryGetProperty("capturedAt", out var c) ? c.GetString() : "";
                    sha = r.TryGetProperty("sha256", out var s) ? s.GetString() : "";
                    bytes = r.TryGetProperty("bytes", out var b) ? b.GetInt64() : 0;
                }
                catch { }
            }
            var rec = byId.TryGetValue(id, out var found) ? found : null;
            items.Add(new
            {
                id,
                title = string.IsNullOrWhiteSpace(title) ? (rec?.Title ?? id) : title,
                url = string.IsNullOrWhiteSpace(url) ? (rec?.Url ?? "") : url,
                at, bytes, sha256 = sha,
                shot = File.Exists(Path.Combine(dir, "shot.png")) ? $"archive/{id}/shot.png" : null,
                md = File.Exists(Path.Combine(dir, "page.md")) ? $"archive/{id}/page.md" : null,
                html = File.Exists(Path.Combine(dir, "page.html")) ? $"archive/{id}/page.html" : null
            });
        }
        return items.OrderByDescending(i => ((dynamic)i).at).ToArray();
    }

    private static HttpResponse VaultSet(HttpRequest req)
    {
        using var doc = Parse(req);
        var root = doc.RootElement;
        var item = VaultStore.Set(Str(root, "id"), Str(root, "label"), Str(root, "user"),
                                  Str(root, "url"), Str(root, "notes"), Str(root, "secret"));
        return HttpResponse.Json(new { ok = true, id = item.Id });
    }

    private static HttpResponse VaultReveal(HttpRequest req)
    {
        using var doc = Parse(req);
        var item = VaultStore.Get(Str(doc.RootElement, "id"));
        if (item == null) return HttpResponse.Error("no such credential", 404);
        Log.Info($"vault reveal {item.Id}");
        return HttpResponse.Json(new { ok = true, secret = item.Secret });
    }

    private static HttpResponse VaultRemove(HttpRequest req)
    {
        using var doc = Parse(req);
        var ok = VaultStore.Remove(Str(doc.RootElement, "id"));
        return HttpResponse.Json(new { ok });
    }

    /// <summary>
    /// Explicit, token-gated fill of the ACTIVE tab. Refuses when the tab's host does not match
    /// the credential's host unless the caller passes force:true - a misfire here types a secret
    /// into the wrong site.
    /// </summary>
    private HttpResponse VaultFill(HttpRequest req)
    {
        using var doc = Parse(req);
        var item = VaultStore.Get(Str(doc.RootElement, "id"));
        if (item == null) return HttpResponse.Error("no such credential", 404);
        if (string.IsNullOrEmpty(item.Secret)) return HttpResponse.Error("no secret stored", 400);

        var force = doc.RootElement.TryGetProperty("force", out var f) && f.ValueKind == JsonValueKind.True;
        var active = _host.Tabs().FirstOrDefault(t => t.Active);
        var activeIsWeb = active != null
                          && (active.Url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                              || active.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase));
        if (!activeIsWeb)
            return HttpResponse.Json(new
            {
                ok = false,
                error = "no web page is active — open the site you want to sign in to first"
            });
        if (!force && !string.IsNullOrWhiteSpace(item.Url))
        {
            var want = Bookmark.DomainOf(item.Url);
            var have = Bookmark.DomainOf(active.Url);
            if (have.Length > 0 && have != want)
                return HttpResponse.Json(new
                {
                    ok = false,
                    error = $"active tab is {have}; this credential is for {want} (pass force to override)"
                });
        }

        var raw = _host.EvalAsync(BuildFillScript(item.User, item.Secret), null).GetAwaiter().GetResult();
        var filled = raw != null && raw.Contains('1');
        Log.Info($"vault fill {item.Id} filled={filled}");
        return HttpResponse.Json(new { ok = filled, filled, tab = active?.Url });
    }

    private static string BuildFillScript(string user, string secret)
    {
        var u = JsonSerializer.Serialize(user ?? "");
        var p = JsonSerializer.Serialize(secret ?? "");
        return "(function(){"
             + "function set(el,v){if(!el||v==='')return false;el.focus();el.value=v;"
             + "el.dispatchEvent(new Event('input',{bubbles:true}));"
             + "el.dispatchEvent(new Event('change',{bubbles:true}));return true;}"
             + "var pw=document.querySelector('input[type=password]');if(!pw)return '0';"
             + "var form=pw.form||document;"
             + "var uf=form.querySelector('input[type=email],input[autocomplete=username],"
             + "input[name*=user i],input[name*=email i],input[id*=user i],input[type=text]');"
             + "var a=set(uf," + u + ");var b=set(pw," + p + ");"
             + "return (a||b)?'1':'0';})()";
    }

    private HttpResponse AgentOpen(HttpRequest req)
    {
        using var doc = Parse(req);
        var url = Str(doc.RootElement, "url");
        if (string.IsNullOrWhiteSpace(url)) return HttpResponse.Error("url required");
        var newTab = !doc.RootElement.TryGetProperty("newTab", out var nt) || nt.ValueKind != JsonValueKind.False;
        var result = _host.OpenAsync(url, newTab).GetAwaiter().GetResult();
        return HttpResponse.Json(new { ok = true, url = result });
    }

    private HttpResponse AgentSnapshot(HttpRequest req)
    {
        var tabId = TabId(req);
        return HttpResponse.Json(new { ok = true, snapshot = _host.SnapshotAsync(tabId).GetAwaiter().GetResult() });
    }

    private HttpResponse AgentScreenshot(HttpRequest req)
    {
        var path = _host.ScreenshotAsync(TabId(req)).GetAwaiter().GetResult();
        return path == null ? HttpResponse.Error("screenshot failed", 500) : HttpResponse.Json(new { ok = true, path });
    }

    private HttpResponse AgentClick(HttpRequest req)
    {
        using var doc = Parse(req);
        var ok = _host.ClickAsync(Str(doc.RootElement, "selector"), TabId(req)).GetAwaiter().GetResult();
        return HttpResponse.Json(new { ok });
    }

    private HttpResponse AgentType(HttpRequest req)
    {
        using var doc = Parse(req);
        var ok = _host.TypeAsync(Str(doc.RootElement, "selector"), Str(doc.RootElement, "text"),
                                 TabId(req)).GetAwaiter().GetResult();
        return HttpResponse.Json(new { ok });
    }

    /// <summary>Chat with the console brain (falling back to the bridges). Token-gated: it can be slow.</summary>
    private HttpResponse AgentChat(HttpRequest req)
    {
        using var doc = Parse(req);
        var root = doc.RootElement;
        var brain = Str(root, "brain");
        var maxTokens = root.TryGetProperty("maxTokens", out var mt) && mt.TryGetInt32(out var m) ? m : 700;
        var messages = new List<(string Role, string Content)>();
        if (root.TryGetProperty("messages", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var content = Str(item, "content");
                if (content.Length == 0) continue;
                var role = Str(item, "role");
                messages.Add((role.Length == 0 ? "user" : role, content));
            }
        }
        if (messages.Count == 0) return HttpResponse.Error("messages[] required");
        messages.Insert(0, ("system", ChatProxy.Describe()));
        var reply = ChatProxy.Ask(messages, brain, maxTokens);
        return HttpResponse.Json(new
        {
            ok = reply.Ok, brain = reply.Brain, model = reply.Model, reply = reply.Reply,
            ms = reply.Ms, totalMs = reply.TotalMs, receipt = reply.Receipt,
            error = reply.Error, tried = reply.Tried, note = reply.Note
        });
    }

    /// <summary>Capture the active tab into the Shot Vault.</summary>
    private HttpResponse Snap(HttpRequest req)
    {
        var mode = req.QueryValue("mode", "viewport");
        if (!string.IsNullOrWhiteSpace(req.Body))
        {
            try
            {
                using var doc = JsonDocument.Parse(req.Body);
                if (doc.RootElement.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String)
                    mode = m.GetString() ?? mode;
            }
            catch { /* body is optional */ }
        }
        return HttpResponse.Json(_host.SnapAsync(mode, TabId(req)).GetAwaiter().GetResult());
    }

    private static HttpResponse SnapDelete(HttpRequest req)
    {
        using var doc = Parse(req);
        var ok = SnapshotStore.Remove(Str(doc.RootElement, "id"));
        return HttpResponse.Json(new { ok });
    }

    /// <summary>Pin/unpin a link on the bookmark bar (agents use this too).</summary>
    private HttpResponse BibleFav(HttpRequest req)
    {
        using var doc = Parse(req);
        var url = Str(doc.RootElement, "url");
        if (string.IsNullOrWhiteSpace(url)) return HttpResponse.Error("url required");
        var on = !doc.RootElement.TryGetProperty("on", out var flag)
                 || flag.ValueKind != JsonValueKind.False;
        var state = BibleStore.SetFavourite(url, Str(doc.RootElement, "title"), on);
        _host.RefreshBookmarks();
        return HttpResponse.Json(new { ok = true, fav = state, count = BibleStore.Favourites().Count });
    }

    private HttpResponse AgentFind(HttpRequest req)    {
        using var doc = Parse(req);
        var text = Str(doc.RootElement, "text");
        if (string.IsNullOrWhiteSpace(text)) return HttpResponse.Error("text required");
        var forward = !doc.RootElement.TryGetProperty("forward", out var f)
                      || f.ValueKind != JsonValueKind.False;
        return HttpResponse.Json(_host.FindAsync(text, forward, TabId(req)).GetAwaiter().GetResult());
    }

    private HttpResponse AgentZoom(HttpRequest req)
    {
        using var doc = Parse(req);
        var factor = 1.0;
        if (doc.RootElement.TryGetProperty("factor", out var f) && f.TryGetDouble(out var d)) factor = d;
        return HttpResponse.Json(_host.SetZoomAsync(factor, TabId(req)).GetAwaiter().GetResult());
    }

    private HttpResponse AgentClose(HttpRequest req)    {
        using var doc = Parse(req);
        var id = doc.RootElement.TryGetProperty("tab", out var t) && t.TryGetInt32(out var n) ? n : -1;
        if (id <= 0) return HttpResponse.Error("tab required");
        return HttpResponse.Json(new { ok = _host.CloseTabAsync(id).GetAwaiter().GetResult() });
    }

    // ------------------------------------------------------------------ static files

    private static HttpResponse ServeFile(string path)
    {
        if (path.StartsWith("/archive/", StringComparison.OrdinalIgnoreCase))
            return ServeArchive(path);
        if (path.StartsWith("/shots/", StringComparison.OrdinalIgnoreCase))
            return ServeShot(path);

        var name = path switch
        {
            "/" => "index.html",
            "/bible" => "bible.html",
            "/vault" => "vault.html",
            "/archive" => "archive.html",
            "/history" => "history.html",
            "/downloads" => "downloads.html",
            "/settings" => "settings.html",
            "/agent" => "agent.html",
            "/snaps" => "snaps.html",
            _ => path.TrimStart('/')
        };
        if (name.Contains("..")) return HttpResponse.Error("bad path", 400);

        var root = Path.GetFullPath(Paths.WebDir);
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            return HttpResponse.Error("not found: " + name, 404);

        var bytes = File.ReadAllBytes(full);
        var type = Mime(name);
        if (name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            var html = Encoding.UTF8.GetString(bytes);
            var injection = $"<script>window.LYGO_TOKEN={JsonSerializer.Serialize(Paths.Token())};" +
                            $"window.LYGO_CDP={JsonSerializer.Serialize(Paths.CdpPort)};</script></head>";
            var idx = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            html = idx >= 0 ? html[..idx] + injection + html[(idx + 7)..] : injection + html;
            return HttpResponse.Text(html, "text/html; charset=utf-8");
        }
        return HttpResponse.Bytes(bytes, type);
    }

    private static HttpResponse ServeArchive(string path)
    {
        var parts = path.TrimStart('/').Split('/');
        if (parts.Length != 3) return HttpResponse.Error("bad archive path", 400);
        var id = parts[1];
        var file = parts[2];
        if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^bm_[0-9a-f]{10}$")) return HttpResponse.Error("bad id", 400);
        if (file.Contains("..")) return HttpResponse.Error("bad file", 400);
        var full = Path.GetFullPath(Path.Combine(Paths.ArchiveDir, id, file));
        if (!full.StartsWith(Path.GetFullPath(Paths.ArchiveDir), StringComparison.OrdinalIgnoreCase))
            return HttpResponse.Error("bad path", 400);
        if (!File.Exists(full)) return HttpResponse.Error("not archived: " + file, 404);
        return HttpResponse.Bytes(File.ReadAllBytes(full), Mime(file));
    }

    /// <summary>Serve a shot from data\vault\screenshots\&lt;date&gt;\&lt;file&gt; without leaving the vault.</summary>
    private static HttpResponse ServeShot(string path)
    {
        var parts = path.TrimStart('/').Split('/');
        if (parts.Length != 3) return HttpResponse.Error("bad shot path", 400);
        var day = parts[1];
        var file = parts[2];
        if (!System.Text.RegularExpressions.Regex.IsMatch(day, @"^\d{4}-\d{2}-\d{2}$"))
            return HttpResponse.Error("bad day", 400);
        if (file.Contains("..") || file.Contains('/') || file.Contains('\\'))
            return HttpResponse.Error("bad file", 400);
        var root = Path.GetFullPath(SnapshotStore.Root);
        var full = Path.GetFullPath(Path.Combine(root, day, file));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            return HttpResponse.Error("bad path", 400);
        if (!File.Exists(full)) return HttpResponse.Error("no such shot", 404);
        return HttpResponse.Bytes(File.ReadAllBytes(full), Mime(file));
    }

    private static string Mime(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".md" => "text/markdown; charset=utf-8",
            ".txt" => "text/plain; charset=utf-8",
            ".csv" => "text/csv; charset=utf-8",
            ".woff2" => "font/woff2",
            _ => "application/octet-stream"
        };
    }

    // ------------------------------------------------------------------ helpers

    private static JsonDocument Parse(HttpRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Body)) return JsonDocument.Parse("{}");
        return JsonDocument.Parse(req.Body);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static string StrOrNull(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? TabId(HttpRequest req)
    {
        if (!string.IsNullOrWhiteSpace(req.Body))
        {
            try
            {
                using var doc = JsonDocument.Parse(req.Body);
                if (doc.RootElement.TryGetProperty("tab", out var t) && t.TryGetInt32(out var n)) return n;
            }
            catch { }
        }
        return int.TryParse(req.QueryValue("tab"), out var q) ? q : null;
    }
}
