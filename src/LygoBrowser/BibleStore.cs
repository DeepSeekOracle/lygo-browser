using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LygoBrowser;

/// <summary>
/// The Bookmark Bible store. Files are the source of truth; every call re-reads so a Python
/// tool writing behind our back is picked up immediately. Writes are atomic (temp + replace).
/// </summary>
public static class BibleStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex Tracking = new(
        @"^(utm_|gclid|fbclid|mc_|ref_|igshid|si$|s$|_ga|yclid|srsltid)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static readonly (string Id, string Name, string Desc)[] CanonFolders =
    {
        ("00_START_HERE", "00 · START HERE", "The way in: home, search, archive, instructions."),
        ("01_LYGO", "01 · LYGO", "The LYGO network, console, forge, signal, champions, books."),
        ("02_AGENTS", "02 · AGENTS", "Agent social networks, registries and launchpads."),
        ("03_DOCS", "03 · DOCS", "OpenClaw, browser engines, MCP, automation, references."),
        ("04_RESEARCH", "04 · RESEARCH", "Search engines, archives, papers, models, code."),
        ("05_MEDIA", "05 · MEDIA", "Video, audio, books, storefronts, music platforms."),
        ("06_OPS", "06 · OPS", "Explorers, dashboards, tokens, exchanges."),
        ("07_WEBSITES", "07 · WEBSITES", "Domains Justin owns."),
        ("08_TOOLS", "08 · TOOLS", "Free APIs and utilities with no paid key."),
        ("09_QUEUE", "09 · QUEUE", "Inbox: unfiled links, history finds, things to sort later."),
    };

    private static readonly (string Folder, string Tag, string[] Domains)[] DomainRules =
    {
        ("07_WEBSITES", "owned", new[]
        {
            "chatagent.ca", "excavationpro.ca", "eternalhaven.ca", "bpmfinder.ca",
            "asiancoastline.com", "justinhelmer.ca", "deepseekoracle.com", "deepseekoracle.github.io"
        }),
        ("02_AGENTS", "agent", new[]
        {
            "moltx.io", "moltbook.com", "clawchat.app", "4claw.org", "clawhub.ai", "clawdhub.com",
            "clawstr.com", "moltroad.com", "moltmatch.com", "molti-verse.com", "molthunt.com",
            "moltycombinator.com", "reelclaw.com", "clawchess.critical-hit.org", "clawn.ch",
            "bankr.bot", "agentgram.site", "clawverse.app", "onlymolts.app", "openclaw.ai"
        }),
        ("06_OPS", "ops", new[]
        {
            "basescan.org", "base.blockscout.com", "dexscreener.com", "clanker.world",
            "app.virtuals.io", "app.uniswap.org", "crypto.com"
        }),
        ("05_MEDIA", "media", new[]
        {
            "youtube.com", "youtu.be", "rumble.com", "audius.co", "lulu.com", "patreon.com",
            "distrokid.com", "bandlab.com", "twitch.tv", "spotify.com"
        }),
        ("04_RESEARCH", "research", new[]
        {
            "google.com", "bing.com", "duckduckgo.com", "arxiv.org", "web.archive.org", "archive.ph",
            "archive.org", "huggingface.co", "github.com", "semanticscholar.org", "openalex.org",
            "paperswithcode.com", "wikipedia.org", "grokipedia.com", "perplexity.ai", "x.ai",
            "search.brave.com", "startpage.com", "scholar.google.com", "nature.com", "openreview.net"
        }),
    };

    private static bool HostMatches(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    public static (string Folder, List<string> Tags) Classify(string url)
    {
        var host = Bookmark.DomainOf(url);
        if (host == "127.0.0.1" || host == "localhost")
            return ("00_START_HERE", new List<string> { "local" });
        if (host.EndsWith(".github.io", StringComparison.Ordinal))
            return ("07_WEBSITES", new List<string> { "owned" });
        foreach (var rule in DomainRules)
        {
            if (rule.Domains.Any(d => HostMatches(host, d)))
                return (rule.Folder, new List<string> { rule.Tag });
        }
        if (host == "x.com" || host == "twitter.com")
            return ("02_AGENTS", new List<string> { "x" });
        return ("09_QUEUE", new List<string>());
    }

    // ------------------------------------------------------------------ read / write

    public static BibleDoc Load()
    {
        lock (Gate)
        {
            if (!File.Exists(Paths.BibleFile))
                return new BibleDoc { Updated = NowIso(), Folders = DefaultFolders() };
            try
            {
                var json = File.ReadAllText(Paths.BibleFile, Encoding.UTF8);
                var doc = JsonSerializer.Deserialize<BibleDoc>(json, ReadOpts) ?? new BibleDoc();
                if (doc.Folders.Count == 0) doc.Folders = DefaultFolders();
                doc.Records ??= new List<Bookmark>();
                foreach (var r in doc.Records)
                {
                    r.Tags ??= new List<string>();
                    r.Refs ??= new List<string>();
                    r.Archived ??= new ArchiveState();
                }
                return doc;
            }
            catch (Exception ex)
            {
                Log.Warn("bible load failed: " + ex.Message);
                return new BibleDoc { Updated = NowIso(), Folders = DefaultFolders() };
            }
        }
    }

    public static void Save(BibleDoc doc)
    {
        lock (Gate)
        {
            Paths.EnsureDirs();
            doc.Updated = NowIso();
            if (doc.Folders == null || doc.Folders.Count == 0) doc.Folders = DefaultFolders();
            var json = JsonSerializer.Serialize(doc, WriteOpts);
            var tmp = Paths.BibleFile + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(Paths.BibleFile)) File.Replace(tmp, Paths.BibleFile, null, true);
            else File.Move(tmp, Paths.BibleFile);
        }
    }

    public static List<BibleFolder> DefaultFolders() =>
        CanonFolders.Select(f => new BibleFolder { Id = f.Id, Name = f.Name, Desc = f.Desc }).ToList();

    // ------------------------------------------------------------------ identity

    public static string Normalize(string url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) return "";
        if (!Regex.IsMatch(url, @"^[a-z][a-z0-9+.\-]*://", RegexOptions.IgnoreCase))
            url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;

        var scheme = u.Scheme.ToLowerInvariant();
        // Authority, not Host: the port is part of the identity (127.0.0.1:9641 != 127.0.0.1:8796).
        var authority = u.Authority.ToLowerInvariant();
        if (scheme == "https" && authority.EndsWith(":443")) authority = authority[..^4];
        if (scheme == "http" && authority.EndsWith(":80")) authority = authority[..^3];
        var path = Regex.Replace(u.AbsolutePath.Length == 0 ? "/" : u.AbsolutePath, "/{2,}", "/");
        if (path.Length > 1 && path.EndsWith("/")) path = path[..^1];

        return $"{scheme}://{authority}{path}{KeepQuery(u.Query)}";
    }

    /// <summary>Drop tracking parameters but keep the rest of the query verbatim.</summary>
    private static string KeepQuery(string rawQuery)
    {
        var query = (rawQuery ?? "").TrimStart('?');
        if (query.Length == 0) return "";
        var kept = new List<string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = pair.Split('=')[0];
            if (Tracking.IsMatch(Uri.UnescapeDataString(key))) continue;
            kept.Add(pair);
        }
        return kept.Count > 0 ? "?" + string.Join("&", kept) : "";
    }

    public static string RecordId(string url)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(Normalize(url)));
        return "bm_" + Convert.ToHexString(bytes)[..10].ToLowerInvariant();
    }

    // ------------------------------------------------------------------ mutations

    public sealed record AddResult(string Status, Bookmark Record);

    public static AddResult Add(string url, string title, string folder, IEnumerable<string> tags,
                                string note, string source = "manual")
    {
        var doc = Load();
        var norm = Normalize(url);
        var id = RecordId(norm);
        var (autoFolder, autoTags) = Classify(norm);
        var tokens = autoTags.Concat(tags ?? Enumerable.Empty<string>());
        var existing = doc.Records.FirstOrDefault(r => r.Id == id);

        if (existing != null)
        {
            var changed = MergeInto(existing, title, folder, note, tokens);
            if (changed) Save(doc);
            return new AddResult(changed ? "updated" : "dupe", existing);
        }

        var rec = new Bookmark
        {
            Id = id,
            Url = norm,
            Title = string.IsNullOrWhiteSpace(title) ? norm : title.Trim(),
            Folder = string.IsNullOrWhiteSpace(folder) ? autoFolder : folder,
            Note = note ?? "",
            Source = source,
            Added = NowIso(),
            Status = "active",
        };
        MergeTags(rec, tokens);
        doc.Records.Add(rec);
        Save(doc);
        return new AddResult("added", rec);
    }

    /// <summary>Fill only the blanks on an existing record; returns true when anything changed.</summary>
    private static bool MergeInto(Bookmark existing, string title, string folder, string note,
                                  IEnumerable<string> tokens)
    {
        var changed = false;
        var titleIsPlaceholder = existing.Title == existing.Url || existing.Title.Length == 0;
        if (!string.IsNullOrWhiteSpace(title) && titleIsPlaceholder)
        {
            existing.Title = title.Trim();
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(folder) && existing.Folder == "09_QUEUE" && folder != "09_QUEUE")
        {
            existing.Folder = folder;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(note) && existing.Note.Length == 0)
        {
            existing.Note = note;
            changed = true;
        }
        return MergeTags(existing, tokens) || changed;
    }

    private static bool MergeTags(Bookmark rec, IEnumerable<string> tokens)
    {
        var changed = false;
        foreach (var token in tokens)
        {
            var tag = (token ?? "").Trim().ToLowerInvariant();
            if (tag.Length == 0 || rec.Tags.Contains(tag)) continue;
            rec.Tags.Add(tag);
            changed = true;
        }
        return changed;
    }

    // ------------------------------------------------------------------ favourites (the bookmark bar)

    /// <summary>Records pinned to the bookmark bar, newest first.</summary>
    public static List<Bookmark> Favourites(int max = 14) =>
        Load().Records
              .Where(r => r.Status == "active" && r.Tags.Contains("fav"))
              .OrderByDescending(r => r.Added)
              .Take(max)
              .ToList();

    /// <summary>Pin or unpin a URL. Pinning a page that is not in the Bible files it first.</summary>
    public static bool SetFavourite(string url, string title, bool on)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var norm = Normalize(url);                       // strip #fragment / tracking before lookup
        if (on) Add(norm, title, "", null, "", "human"); // idempotent: adds only when missing

        var doc = Load();
        var rec = doc.Records.FirstOrDefault(r => r.Id == RecordId(norm));
        if (rec == null) return false;
        var has = rec.Tags.Contains("fav");
        if (on && !has) rec.Tags.Add("fav");
        if (!on && has) rec.Tags.Remove("fav");
        if (on != has) Save(doc);
        return on;
    }

    public static bool IsFavourite(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var id = RecordId(Normalize(url));
        return Load().Records.Any(r => r.Id == id && r.Status == "active" && r.Tags.Contains("fav"));
    }

    public static Bookmark Update(string id, string title, string folder, string tags, string note, string status)    {
        var doc = Load();
        var rec = doc.Records.FirstOrDefault(r => r.Id == id);
        if (rec == null) return null;
        if (title != null) rec.Title = title.Trim();
        if (!string.IsNullOrWhiteSpace(folder) && CanonFolders.Any(f => f.Id == folder)) rec.Folder = folder;
        if (tags != null)
            rec.Tags = tags.Split(',', StringSplitOptions.RemoveEmptyEntries)
                           .Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).Distinct().ToList();
        if (note != null) rec.Note = note;
        if (!string.IsNullOrWhiteSpace(status)) rec.Status = status;
        Save(doc);
        return rec;
    }

    public static bool MarkArchived(string id, ArchiveState state, string hash)
    {
        var doc = Load();
        var rec = doc.Records.FirstOrDefault(r => r.Id == id);
        if (rec == null) return false;
        rec.Archived = state;
        rec.Hash = hash;
        Save(doc);
        return true;
    }

    // ------------------------------------------------------------------ query

    public static int Score(Bookmark r, string[] terms, bool strict)
    {
        var title = r.Title.ToLowerInvariant();
        var url = r.Url.ToLowerInvariant();
        var folder = r.Folder.ToLowerInvariant();
        var note = r.Note.ToLowerInvariant();
        var tags = r.Tags.Select(t => t.ToLowerInvariant()).ToList();
        int total = 0;
        foreach (var t in terms)
        {
            if (title.Contains(t)) total += 6;
            else if (tags.Contains(t)) total += 4;
            else if (folder.Contains(t)) total += 3;
            else if (url.Contains(t)) total += 2;
            else if (note.Contains(t)) total += 1;
            else if (strict) return 0;
        }
        return total;
    }

    public static List<(int Score, Bookmark Record)> Search(string query, int limit = 40)
    {
        var doc = Load();
        var terms = (query ?? "").ToLowerInvariant()
                                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var live = doc.Records.Where(r => r.Status == "active").ToList();
        var hits = live.Select(r => (Score: Score(r, terms, true), Record: r)).Where(h => h.Score > 0).ToList();
        if (hits.Count == 0)
            hits = live.Select(r => (Score: Score(r, terms, false), Record: r)).Where(h => h.Score > 0).ToList();
        return hits.OrderByDescending(h => h.Score).ThenByDescending(h => h.Record.Visits)
                   .Take(Math.Clamp(limit, 1, 500)).ToList();
    }

    public static object Stats()
    {
        var doc = Load();
        var live = doc.Records.Where(r => r.Status == "active").ToList();
        var byFolder = CanonFolders.ToDictionary(f => f.Id,
            f => live.Count(r => r.Folder == f.Id));
        return new
        {
            records = live.Count,
            domains = live.Select(r => r.Domain).Distinct().Count(),
            archived = live.Count(r => r.IsArchived),
            byFolder
        };
    }

    public static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
}
