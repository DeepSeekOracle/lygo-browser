using System.IO;
using System.Text;
using System.Text.Json;

namespace LygoBrowser;

/// <summary>User settings, persisted as JSON. Missing keys fall back to the defaults below.</summary>
public sealed class Settings
{
    public string Homepage { get; set; } = "https://excavationpro.ca/";
    public string SearchEngine { get; set; } = "google";     // google | ddg | brave | bible
    public bool RestoreSession { get; set; } = true;
    public string DownloadDir { get; set; } = "";
    public bool Spellcheck { get; set; } = true;
    public bool AskBeforeDownload { get; set; } = false;

    public string ResolvedDownloadDir =>
        string.IsNullOrWhiteSpace(DownloadDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LYGO Browser")
            : DownloadDir;

    public string SearchUrl(string query)
    {
        var q = Uri.EscapeDataString(query);
        return SearchEngine switch
        {
            "ddg" => "https://duckduckgo.com/?q=" + q,
            "brave" => "https://search.brave.com/search?q=" + q,
            "bible" => "http://127.0.0.1:9337/bible?q=" + q,
            _ => "https://www.google.com/search?q=" + q
        };
    }
}

public static class SettingsStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string FilePath => Path.Combine(Paths.StateDir, "settings.json");

    public static Settings Load()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new Settings();
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath, Encoding.UTF8), Opts)
                       ?? new Settings();
            }
            catch (Exception ex)
            {
                Log.Warn("settings load: " + ex.Message);
                return new Settings();
            }
        }
    }

    public static void Save(Settings s)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Paths.StateDir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s, Opts), new UTF8Encoding(false));
        }
    }
}

/// <summary>Append-only browsing history (JSONL). Capped and rotated so it cannot grow forever.</summary>
public sealed class HistoryEntry
{
    public string Ts { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
}

public static class HistoryStore
{
    private const long MaxBytes = 8 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(Paths.StateDir, "history.jsonl");

    public static void Add(string url, string title)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        if (url.Contains("127.0.0.1:9337")) return;          // our own HUD is not history
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Paths.StateDir);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", true);
                var line = JsonSerializer.Serialize(new HistoryEntry
                {
                    Ts = BibleStore.NowIso(),
                    Url = url,
                    Title = title ?? ""
                });
                File.AppendAllText(FilePath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("history add: " + ex.Message); }
        }
    }

    public static List<HistoryEntry> Recent(int limit = 500)
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(FilePath)) return new List<HistoryEntry>();
                var lines = File.ReadLines(FilePath).TakeLast(Math.Clamp(limit, 1, 20000));
                var list = new List<HistoryEntry>();
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try { list.Add(JsonSerializer.Deserialize<HistoryEntry>(line) ?? new HistoryEntry()); }
                    catch { }
                }
                return list;
            }
            catch { return new List<HistoryEntry>(); }
        }
    }

    public static List<HistoryEntry> Search(string query, int limit = 200)
    {
        var terms = (query ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var all = Recent(5000);
        if (terms.Length == 0) return all.TakeLast(limit).Reverse().ToList();
        return all.Where(h =>
                terms.All(t => (h.Url + " " + h.Title).ToLowerInvariant().Contains(t)))
            .TakeLast(limit).Reverse().ToList();
    }

    public static void Clear()
    {
        lock (Gate)
        {
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }
    }

    public static int Count() => Recent(20000).Count;
}

/// <summary>Download ledger: in-memory live state plus an append-only JSONL record.</summary>
public sealed class DownloadEntry
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
    public string Path { get; set; } = "";
    public long Bytes { get; set; }
    public long Total { get; set; }
    public string State { get; set; } = "in-progress";
    public string Started { get; set; } = "";
}

public static class DownloadStore
{
    private static readonly object Gate = new();
    private static Dictionary<string, DownloadEntry> _map;
    private static string FilePath => Path.Combine(Paths.StateDir, "downloads.jsonl");
    private const int MaxEntries = 500;

    /// <summary>Load once per process. Anything left "in-progress" belongs to a dead run.</summary>
    private static void EnsureLoaded()
    {
        if (_map != null) return;
        _map = new Dictionary<string, DownloadEntry>();
        try
        {
            if (File.Exists(FilePath))
            {
                foreach (var line in File.ReadLines(FilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    try
                    {
                        var entry = JsonSerializer.Deserialize<DownloadEntry>(line);
                        if (entry?.Id != null) _map[entry.Id] = entry;
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { Log.Warn("downloads load: " + ex.Message); }

        foreach (var entry in _map.Values.Where(e => e.State == "in-progress"))
            entry.State = "interrupted";
    }

    private static void Flush()
    {
        try
        {
            Directory.CreateDirectory(Paths.StateDir);
            var lines = _map.Values.OrderBy(e => e.Started).TakeLast(MaxEntries)
                           .Select(e => JsonSerializer.Serialize(e));
            File.WriteAllLines(FilePath, lines, new UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Warn("downloads save: " + ex.Message); }
    }

    public static void Track(DownloadEntry entry)
    {
        lock (Gate)
        {
            EnsureLoaded();
            _map[entry.Id] = entry;
            Flush();
        }
    }

    public static void Update(DownloadEntry entry)
    {
        lock (Gate)
        {
            EnsureLoaded();
            if (!_map.TryGetValue(entry.Id, out var existing)) return;
            existing.Bytes = entry.Bytes;
            existing.Total = entry.Total;
            existing.State = entry.State;
            Flush();
        }
    }

    public static List<DownloadEntry> All()
    {
        lock (Gate)
        {
            EnsureLoaded();
            return _map.Values.OrderByDescending(e => e.Started).ToList();
        }
    }

    public static int Active() => All().Count(d => d.State == "in-progress");
}
