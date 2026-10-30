using System.IO;

namespace LygoBrowser;

/// <summary>Resolves the install root and the data/web folders. No writes here.</summary>
public static class Paths
{
    public const string BridgePort = "9337";
    public const string CdpPort = "9333";

    private static string _root;

    /// <summary>App root: env override, else nearest ancestor holding data\bible\bookmarks.json.</summary>
    public static string Root
    {
        get
        {
            if (_root != null) return _root;
            var env = Environment.GetEnvironmentVariable("LYGO_BROWSER_ROOT");
            if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
                return _root = Path.GetFullPath(env);

            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "data", "bible", "bookmarks.json")))
                    return _root = dir.FullName;
            }
            // Public/portable default: per-user app data, no developer path baked in.
            var local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LYGO", "Browser");
            Directory.CreateDirectory(local);
            return _root = local;
        }
    }

    public static string Data => Path.Combine(Root, "data");
    public static string BibleDir => Path.Combine(Data, "bible");
    public static string BibleFile => Path.Combine(BibleDir, "bookmarks.json");
    public static string ArchiveDir => Path.Combine(Data, "archive");
    public static string VaultDir => Path.Combine(Data, "vault");
    public static string VaultFile => Path.Combine(VaultDir, "vault.dat");
    public static string StateDir => Path.Combine(Data, "state");
    public static string TokenFile => Path.Combine(StateDir, "bridge.token");
    public static string ProfileDir => Path.Combine(Data, "profile");
    public static string ToolsDir => Path.Combine(Root, "tools");

    /// <summary>Web assets: prefer the copy next to the exe, else the source tree.</summary>
    public static string WebDir
    {
        get
        {
            var local = Path.Combine(AppContext.BaseDirectory, "web");
            if (Directory.Exists(local)) return local;
            var src = Path.Combine(Root, "src", "LygoBrowser", "web");
            return Directory.Exists(src) ? src : local;
        }
    }

    public static void EnsureDirs()
    {
        foreach (var d in new[] { BibleDir, ArchiveDir, VaultDir, StateDir, ProfileDir })
            Directory.CreateDirectory(d);
    }

    /// <summary>Per-install bridge token. Generated once, readable only by this user.</summary>
    public static string Token()
    {
        Directory.CreateDirectory(StateDir);
        if (File.Exists(TokenFile))
        {
            var t = File.ReadAllText(TokenFile).Trim();
            if (t.Length >= 16) return t;
        }
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray())
                  + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        File.WriteAllText(TokenFile, token);
        return token;
    }
}
