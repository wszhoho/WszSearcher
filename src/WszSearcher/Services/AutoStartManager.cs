using System.Diagnostics;

namespace WszSearcher.Services;

/// <summary>
/// 开机自启管理：通过计划任务实现。
/// 程序 manifest 要求 requireAdministrator（USN 日志扫描需要管理员权限），
/// 注册表 Run 键无法静默提权自启（会弹 UAC 或被拒绝），
/// 因此只能用 schtasks /RL HIGHEST 在登录时静默以最高权限启动。
/// </summary>
public static class AutoStartManager
{
    private const string TaskName = "WszSearcher";

    /// <summary>按开关启用/移除开机自启</summary>
    public static void Set(bool enable)
    {
        if (enable) Enable();
        else Disable();
    }

    /// <summary>创建/更新计划任务：登录时以最高权限静默启动</summary>
    public static void Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;
        RunSchTasks($"/Create /F /SC ONLOGON /TN \"{TaskName}\" /TR \"\\\"{exe}\\\"\" /RL HIGHEST");
    }

    /// <summary>删除计划任务</summary>
    public static void Disable()
    {
        RunSchTasks($"/Delete /F /TN \"{TaskName}\"");
    }

    private static void RunSchTasks(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "schtasks",
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas", // 创建/删除计划任务需提权一次
                WindowStyle = ProcessWindowStyle.Hidden // 隐藏 schtasks 控制台窗口，避免黑色终端闪现
            })?.WaitForExit(5000);
        }
        catch { /* 权限不足时跳过 */ }
    }
}
