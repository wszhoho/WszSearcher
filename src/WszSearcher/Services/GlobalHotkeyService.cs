using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace WszSearcher.Services;

/// <summary>
/// 全局快捷键服务——注册 Win32 全局热键
/// 支持自定义修饰键和按键，支持冲突检测
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    // ─── Win32 API ───
    private const int WM_HOTKEY = 0x0312;

    // ─── 修饰键常量 ───
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>将修饰键枚举值转为可读字符串</summary>
    public static string ModifiersToString(uint mods)
    {
        var parts = new List<string>();
        if ((mods & MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & MOD_WIN) != 0) parts.Add("Win");
        return string.Join("+", parts);
    }

    /// <summary>将虚拟键码转为可读字符串</summary>
    public static string KeyToString(uint vk)
    {
        return vk switch
        {
            0x20 => "Space",
            >= 0x30 and <= 0x39 => ((char)vk).ToString(),      // 0-9
            >= 0x41 and <= 0x5A => ((char)vk).ToString(),      // A-Z
            >= 0x70 and <= 0x7B => $"F{vk - 0x6F}",            // F1-F12
            _ => $"VK_{vk:X2}"
        };
    }

    private readonly Window _window;
    private readonly int _hotkeyId;
    private HwndSource? _source;
    private uint _modifiers;
    private uint _key;
    private bool _registered;
    private bool _disposed;
    private bool _loadHandlerSubscribed;

    /// <summary>热键触发事件</summary>
    public event Action? HotkeyPressed;

    /// <summary>当前快捷键修饰键</summary>
    public uint Modifiers => _modifiers;
    /// <summary>当前快捷键按键</summary>
    public uint Key => _key;
    /// <summary>格式化后的快捷键文本</summary>
    public string HotkeyText => $"{ModifiersToString(_modifiers)} + {KeyToString(_key)}";

    public GlobalHotkeyService(Window window, uint modifiers = MOD_ALT, uint key = 0x20, int hotkeyId = 1)
    {
        _window = window;
        _hotkeyId = hotkeyId;
        _modifiers = modifiers;
        _key = key;
    }

    /// <summary>注册全局热键</summary>
    public bool Register()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(GlobalHotkeyService));
        if (_registered) return true;

        _source = PresentationSource.FromVisual(_window) as HwndSource;
        if (_source is null)
        {
            if (!_loadHandlerSubscribed)
            {
                _window.Loaded += OnWindowLoaded;
                _loadHandlerSubscribed = true;
            }
            return true;
        }

        return DoRegister();
    }

    /// <summary>使用新快捷键重新注册（先注销旧的）</summary>
    public bool Reregister(uint modifiers, uint key)
    {
        if (_registered)
        {
            _source?.RemoveHook(WndProc);
            var h = _source?.Handle ?? IntPtr.Zero;
            if (h != IntPtr.Zero) UnregisterHotKey(h, _hotkeyId);
            _registered = false;
        }

        _modifiers = modifiers;
        _key = key;
        return DoRegister();
    }

    /// <summary>检测快捷键是否冲突（不实际注册）</summary>
    public static bool CheckConflict(IntPtr hwnd, uint modifiers, uint key, int id = 1)
    {
        if (hwnd == IntPtr.Zero) return true; // 无效句柄

        // 尝试注册 → 如果失败则是冲突
        if (!RegisterHotKey(hwnd, id, modifiers | MOD_NOREPEAT, key))
            return true;

        // 注册成功 → 立即注销 → 不冲突
        UnregisterHotKey(hwnd, id);
        return false;
    }

    private void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        _window.Loaded -= OnWindowLoaded;
        _loadHandlerSubscribed = false;
        if (_disposed) return;

        _source = PresentationSource.FromVisual(_window) as HwndSource;
        DoRegister();
    }

    private bool DoRegister()
    {
        if (_source is null || _registered) return false;

        var handle = _source.Handle;
        var result = RegisterHotKey(handle, _hotkeyId, _modifiers | MOD_NOREPEAT, _key);

        if (!result)
        {
            var error = Marshal.GetLastWin32Error();
            System.Diagnostics.Debug.WriteLine($"RegisterHotKey failed: error {error}");
            return false;
        }

        _source.AddHook(WndProc);
        _registered = true;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _hotkeyId)
        {
            HotkeyPressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_loadHandlerSubscribed)
        {
            _window.Loaded -= OnWindowLoaded;
            _loadHandlerSubscribed = false;
        }

        if (_source is not null && _registered)
        {
            _source.RemoveHook(WndProc);
            var handle = _source.Handle;
            if (handle != IntPtr.Zero)
                UnregisterHotKey(handle, _hotkeyId);
            _registered = false;
        }
        _source = null;
    }
}
