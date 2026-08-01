using System.Runtime.InteropServices;

namespace WszSearcher.Core.Native;

/// <summary>进程内存相关 Win32 API P/Invoke 封装</summary>
public static class MemoryApi
{
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(
        IntPtr hProcess,
        IntPtr dwMinimumWorkingSetSize,
        IntPtr dwMaximumWorkingSetSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>清空进程工作集——把空闲物理页换出到页面文件，任务管理器"内存(活动)"列回落</summary>
    public static void EmptyWorkingSet()
    {
        try
        {
            // -1 表示移除工作集下限/上限，Windows 会将当前可换出的物理页全部换出
            SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch
        {
            // 非关键路径，失败忽略
        }
    }
}
