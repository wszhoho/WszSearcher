using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SysColor = System.Windows.Media.Color;

namespace WszSearcher.Services;

/// <summary>主题管理——通过替换资源字典中的 Color 键实现深色/浅色切换</summary>
public static class ThemeManager
{
    public static bool IsDarkMode { get; private set; }

    public static void ToggleTheme() => SetTheme(!IsDarkMode);

    public static void FollowSystemTheme()
    {
        try
        {
            const string regKey = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
            var value = Microsoft.Win32.Registry.GetValue(regKey, "AppsUseLightTheme", 1);
            var isLight = value is int i && i == 1;
            SetTheme(!isLight);
        }
        catch { SetTheme(false); }
    }

    /// <summary>设置浅色/深色模式（自动调度到 UI 线程）</summary>
    public static void SetTheme(bool darkMode)
    {
        // 确保在 UI 线程执行（防止从非 UI 线程调用时崩溃）
        var app = System.Windows.Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => SetThemeInternal(darkMode));
            return;
        }
        SetThemeInternal(darkMode);
    }

    private static void SetThemeInternal(bool darkMode)
    {
        IsDarkMode = darkMode;

        var app = System.Windows.Application.Current;
        // 更新 Application 层资源
        ApplyColors(app?.Resources, darkMode);

        // 更新所有已打开窗口的本地资源
        if (app is not null)
        {
            foreach (System.Windows.Window window in app.Windows)
            {
                if (window.Resources != app.Resources)
                    ApplyColors(window.Resources, darkMode);
            }
        }
    }

    /// <summary>向指定 ResourceDictionary 写入主题颜色</summary>
    private static void ApplyColors(ResourceDictionary? resources, bool darkMode)
    {
        if (resources is null) return;

        if (darkMode)
        {
            resources["BgColor"] = SysColor.FromRgb(0x1E, 0x1E, 0x1E);
            resources["SurfaceColor"] = SysColor.FromRgb(0x2D, 0x2D, 0x2D);
            resources["BorderColor"] = SysColor.FromRgb(0x3E, 0x3E, 0x3E);
            resources["TextPrimaryColor"] = SysColor.FromRgb(0xE0, 0xE0, 0xE0);
            resources["TextSecondaryColor"] = SysColor.FromRgb(0x99, 0x99, 0x99);
            resources["HighlightColor"] = SysColor.FromRgb(0xFF, 0xE4, 0xB3);
            resources["ListSelectedBrush"] = new SolidColorBrush(SysColor.FromRgb(0x2D, 0x3A, 0x4A));
            resources["ListHoverBrush"] = new SolidColorBrush(SysColor.FromRgb(0x3A, 0x3A, 0x3A));
        }
        else
        {
            resources["BgColor"] = SysColor.FromRgb(0xF5, 0xF5, 0xF5);
            resources["SurfaceColor"] = SysColor.FromRgb(0xFF, 0xFF, 0xFF);
            resources["BorderColor"] = SysColor.FromRgb(0xE0, 0xE0, 0xE0);
            resources["TextPrimaryColor"] = SysColor.FromRgb(0x1A, 0x1A, 0x1A);
            resources["TextSecondaryColor"] = SysColor.FromRgb(0x66, 0x66, 0x66);
            resources["HighlightColor"] = SysColor.FromRgb(0xFF, 0xE4, 0xB3);
            resources["ListSelectedBrush"] = new SolidColorBrush(SysColor.FromRgb(0xE5, 0xF3, 0xFF));
            resources["ListHoverBrush"] = new SolidColorBrush(SysColor.FromRgb(0xF0, 0xF7, 0xFF));
        }

        resources["PrimaryColor"] = SysColor.FromRgb(0x00, 0x78, 0xD4);
        resources["PrimaryHoverColor"] = SysColor.FromRgb(0x10, 0x6E, 0xBE);
    }
}
