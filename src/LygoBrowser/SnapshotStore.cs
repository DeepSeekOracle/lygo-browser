using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;

namespace LygoBrowser;

public sealed class SnapEntry
{
    public string Id { get; set; } = "";
    public string At { get; set; } = "";
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string File { get; set; } = "";      // relative to data\vault\screenshots
    public string Thumb { get; set; } = "";
    public int W { get; set; }
    public int H { get; set; }
    public long Bytes { get; set; }
    public string Mode { get; set; } = "viewport";
}

/// <summary>
/// Screenshot vault: every snap lands in data\vault\screenshots\&lt;date&gt;\ with a thumbnail and a
/// ledger entry, so they stay organised by day instead of piling up in one folder.
/// </summary>
public static class SnapshotStore
{
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Root => Path.Combine(Paths.VaultDir, "screenshots");
    private static string Ledger => Path.Combine(Paths.StateDir, "snaps.json");

    public static List<SnapEntry> All()
    {
        lock (Gate)
        {
            try
            {
                if (!File.Exists(Ledger)) return new List<SnapEntry>();
                return JsonSerializer.Deserialize<List<SnapEntry>>(File.ReadAllText(Ledger, Encoding.UTF8), Opts)
                       ?? new List<SnapEntry>();
            }
            catch (Exception ex)
            {
                Log.Warn("snaps load: " + ex.Message);
                return new List<SnapEntry>();
            }
        }
    }

    private static void Save(List<SnapEntry> items)
    {
        try
        {
            Directory.CreateDirectory(Paths.StateDir);
            File.WriteAllText(Ledger, JsonSerializer.Serialize(items, Opts), new UTF8Encoding(false));
        }
        catch (Exception ex) { Log.Warn("snaps save: " + ex.Message); }
    }

    public static SnapEntry Add(string url, string title, byte[] png, string mode)
    {
        var now = DateTime.Now;
        var day = now.ToString("yyyy-MM-dd");
        var dir = Path.Combine(Root, day);
        Directory.CreateDirectory(dir);

        var id = "snap_" + Guid.NewGuid().ToString("N")[..10];
        var stem = Safe(title) + "-" + now.ToString("HHmmss") + "-" + id[5..];
        var file = Path.Combine(day, stem + ".png");
        var thumbFile = Path.Combine(day, stem + "-thumb.png");
        File.WriteAllBytes(Path.Combine(Root, file), png);

        int width = 0, height = 0;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = new MemoryStream(png);
            bmp.EndInit();
            width = bmp.PixelWidth;
            height = bmp.PixelHeight;
            var thumb = new TransformedBitmap(bmp, new System.Windows.Media.ScaleTransform(0.25, 0.25));
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(thumb));
            using var fs = File.Create(Path.Combine(Root, thumbFile));
            encoder.Save(fs);
        }
        catch (Exception ex) { Log.Warn("snap thumb: " + ex.Message); }

        var entry = new SnapEntry
        {
            Id = id,
            At = BibleStore.NowIso(),
            Url = url ?? "",
            Title = string.IsNullOrWhiteSpace(title) ? (url ?? "snap") : title,
            File = file.Replace('\\', '/'),
            Thumb = thumbFile.Replace('\\', '/'),
            W = width,
            H = height,
            Bytes = png.LongLength,
            Mode = mode
        };

        lock (Gate)
        {
            var items = All();
            items.Insert(0, entry);
            Save(items);
        }
        return entry;
    }

    public static bool Remove(string id)
    {
        lock (Gate)
        {
            var items = All();
            var entry = items.FirstOrDefault(s => s.Id == id);
            if (entry == null) return false;
            foreach (var rel in new[] { entry.File, entry.Thumb })
            {
                try
                {
                    var full = Path.Combine(Root, rel.Replace('/', '\\'));
                    if (File.Exists(full)) File.Delete(full);
                }
                catch (Exception ex) { Log.Warn("snap delete: " + ex.Message); }
            }
            items.Remove(entry);
            Save(items);
            return true;
        }
    }

    private static string Safe(string title)
    {
        var raw = (title ?? "snap").ToLowerInvariant();
        var clean = new System.Text.StringBuilder();
        foreach (var c in raw)
            clean.Append(char.IsLetterOrDigit(c) && c < 128 ? c : '-');
        var text = System.Text.RegularExpressions.Regex.Replace(clean.ToString(), "-{2,}", "-").Trim('-');
        if (text.Length > 60) text = text[..60].Trim('-');
        return text.Length == 0 ? "snap" : text;
    }
}
