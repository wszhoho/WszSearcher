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
        // 日志已关闭
        // try { File.AppendAllText(Path.Combine(Dir, $"wszs_{category}.txt"), $"{DateTime.Now:HH:mm:ss} {msg}{Environment.NewLine}"); } catch { }
    }
}
