using System.IO;

namespace LygoBrowser;

/// <summary>Tiny rolling file log. Never logs secrets: callers must redact.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string FilePath => Path.Combine(Paths.StateDir, "browser.log");

    public static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Paths.StateDir);
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level,-5} {message}{Environment.NewLine}";
                System.IO.File.AppendAllText(FilePath, line);
            }
        }
        catch { /* logging must never throw */ }
    }

    public static void Info(string m) => Write("INFO", m);
    public static void Warn(string m) => Write("WARN", m);
    public static void Error(string m) => Write("ERR", m);
}
