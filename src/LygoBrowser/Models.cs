using System.Text.Json.Serialization;

namespace LygoBrowser;

/// <summary>One folder in the Bookmark Bible taxonomy.</summary>
public sealed class BibleFolder
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("desc")] public string Desc { get; set; } = "";
}

/// <summary>Archive state for a record. Nulls mean "not archived".</summary>
public sealed class ArchiveState
{
    [JsonPropertyName("at")] public string At { get; set; }
    [JsonPropertyName("html")] public string Html { get; set; }
    [JsonPropertyName("md")] public string Md { get; set; }
    [JsonPropertyName("shot")] public string Shot { get; set; }
    [JsonPropertyName("bytes")] public long Bytes { get; set; }
}

/// <summary>A single bookmark record. Append-mostly: retire, never delete.</summary>
public sealed class Bookmark
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("folder")] public string Folder { get; set; } = "09_QUEUE";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "manual";
    [JsonPropertyName("added")] public string Added { get; set; } = "";
    [JsonPropertyName("visits")] public int Visits { get; set; }
    [JsonPropertyName("status")] public string Status { get; set; } = "active";
    [JsonPropertyName("archived")] public ArchiveState Archived { get; set; } = new();
    [JsonPropertyName("hash")] public string Hash { get; set; }
    [JsonPropertyName("refs")] public List<string> Refs { get; set; } = new();

    [JsonIgnore] public bool IsArchived => !string.IsNullOrEmpty(Archived?.At);
    [JsonIgnore] public string Domain => DomainOf(Url);

    public static string DomainOf(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            return host.StartsWith("www.") ? host[4..] : host;
        }
        catch { return ""; }
    }
}

/// <summary>The whole Bible document, as stored on disk.</summary>
public sealed class BibleDoc
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("updated")] public string Updated { get; set; } = "";
    [JsonPropertyName("folders")] public List<BibleFolder> Folders { get; set; } = new();
    [JsonPropertyName("records")] public List<Bookmark> Records { get; set; } = new();
}

/// <summary>A sealed credential record. The secret is only inside the DPAPI blob.</summary>
public sealed class VaultItem
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string User { get; set; } = "";
    public string Url { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Updated { get; set; } = "";
    public string Secret { get; set; } = "";
}

public sealed class TabInfo
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public bool Active { get; set; }
}
