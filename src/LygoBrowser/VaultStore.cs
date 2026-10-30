using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LygoBrowser;

/// <summary>
/// Credential vault. Records are sealed with DPAPI (CurrentUser) so another Windows account
/// cannot read them, and the file on disk never contains plaintext. Nothing is ever autofilled
/// or sent anywhere; reveal is an explicit, token-gated action.
/// </summary>
public static class VaultStore
{
    private static readonly object Gate = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LYGO-BROWSER-VAULT-v1");
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static List<VaultItem> Read()
    {
        lock (Gate)
        {
            if (!File.Exists(Paths.VaultFile)) return new List<VaultItem>();
            try
            {
                var sealedBytes = File.ReadAllBytes(Paths.VaultFile);
                var plain = ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<List<VaultItem>>(plain, Opts) ?? new List<VaultItem>();
            }
            catch (Exception ex)
            {
                Log.Error("vault read failed: " + ex.GetType().Name);
                return new List<VaultItem>();
            }
        }
    }

    private static void Write(List<VaultItem> items)
    {
        lock (Gate)
        {
            Paths.EnsureDirs();
            var plain = JsonSerializer.SerializeToUtf8Bytes(items, Opts);
            var sealedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var tmp = Paths.VaultFile + ".tmp";
            File.WriteAllBytes(tmp, sealedBytes);
            if (File.Exists(Paths.VaultFile)) File.Replace(tmp, Paths.VaultFile, null, true);
            else File.Move(tmp, Paths.VaultFile);
        }
    }

    /// <summary>Labels and metadata only - never secrets.</summary>
    public static List<VaultItem> List() => Read()
        .Select(i => new VaultItem
        {
            Id = i.Id, Label = i.Label, User = i.User, Url = i.Url,
            Notes = i.Notes, Updated = i.Updated, Secret = ""
        })
        .OrderBy(i => i.Label, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static VaultItem Get(string id) => Read().FirstOrDefault(i => i.Id == id);

    public static VaultItem Set(string id, string label, string user, string url, string notes, string secret)
    {
        var items = Read();
        var item = string.IsNullOrWhiteSpace(id) ? null : items.FirstOrDefault(i => i.Id == id);
        if (item == null)
        {
            item = new VaultItem { Id = "vt_" + Guid.NewGuid().ToString("N")[..10] };
            items.Add(item);
        }
        if (label != null) item.Label = label.Trim();
        if (user != null) item.User = user.Trim();
        if (url != null) item.Url = url.Trim();
        if (notes != null) item.Notes = notes;
        if (!string.IsNullOrEmpty(secret)) item.Secret = secret;   // empty keeps the existing secret
        item.Updated = BibleStore.NowIso();
        Write(items);
        Log.Info($"vault set {item.Id} label={item.Label}");
        return item;
    }

    public static bool Remove(string id)
    {
        var items = Read();
        var removed = items.RemoveAll(i => i.Id == id) > 0;
        if (removed) Write(items);
        return removed;
    }

    public static int Count() => Read().Count;
}
