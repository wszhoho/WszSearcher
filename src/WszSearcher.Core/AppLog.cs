using System.IO;

namespace WszSearcher.Core;

/// <summary>共享日志</summary>
public static class AppLog
{
    private static readonly string Dir = Path.GetDirectoryName(AppContext.BaseDirectory) ?? ".";

    public static void Info(string category, string msg) => Write(category, msg);
    public static void Warn(string category, string msg) => Write(category, $"[WARN] {msg}");

    private static void Write(string category, string msg)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss} {msg}";
            var path = Path.Combine(Dir, $"wszs_{category}.txt");
            File.AppendAllText(path, line + Environment.NewLine);
            System.Diagnostics.Debug.WriteLine($"[{category}] {line}");
        }
        catch { }
    }
}
