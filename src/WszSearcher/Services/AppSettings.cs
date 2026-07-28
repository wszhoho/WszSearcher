using System.IO;
using System.Text;
using System.Text.Json;

namespace WszSearcher.Services;

/// <summary>应用设置——持久化到 exe 同目录（绿色版，不侵入系统）</summary>
public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Path.GetDirectoryName(AppContext.BaseDirectory) ?? ".",
        "settings.json");
    private static readonly object _saveLock = new(); // 防止并发写入

    // ─── 可持久化属性 ───

    public double WindowWidth { get; set; } = 560;
    public double WindowHeight { get; set; } = 560;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    /// <summary>主题：0=浅色 1=深色 2=跟随系统</summary>
    public int Theme { get; set; } = 2;

    /// <summary>索引路径列表</summary>
    public List<string> IndexPaths { get; set; } = [];

    /// <summary>排除路径模式列表</summary>
    public List<string> ExcludePaths { get; set; } = ["*\\node_modules", "*\\.git", "*\\AppData", "*\\$RECYCLE.BIN"];

    /// <summary>全文索引的文件后缀（不含点）</summary>
    public List<string> ContentIndexExtensions { get; set; } =
    [
        "txt", "md", "csv", "log", "json", "xml", "yaml", "yml",
        "cs", "js", "ts", "html", "css", "py", "cpp", "c", "h",
        "pdf", "docx", "xlsx", "pptx",
        "ini", "cfg", "config", "java", "rs", "go", "php"
    ];

    /// <summary>开机自启</summary>
    public bool AutoStart { get; set; }

    /// <summary>最大搜索结果数</summary>
    public int MaxResults { get; set; } = 50;

    /// <summary>全局快捷键修饰键（Win32 MOD_* 组合：1=Alt, 2=Ctrl, 4=Shift, 8=Win）</summary>
    public uint HotkeyModifiers { get; set; } = 1; // MOD_ALT

    /// <summary>全局快捷键虚拟键码（默认 VK_SPACE=0x20）</summary>
    public uint HotkeyKey { get; set; } = 0x20; // VK_SPACE

    // ─── 加载/保存 ───

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath, Encoding.UTF8);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            lock (_saveLock)
            {
                var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(SettingsPath, json, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存设置失败: {ex.Message}");
        }
    }
}
