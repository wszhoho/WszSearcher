using System.Windows;
using Application = System.Windows.Application;

namespace WszSearcher.Services;

/// <summary>
/// 语言管理器——运行时动态切换 UI 语言（简体中文/繁体中文/英文）
/// 与 ThemeManager 同模式：替换 Application.Resources 中的语言字典，
/// XAML 侧全部使用 {DynamicResource Lang.xxx} 引用，切换时自动刷新
/// </summary>
public static class LanguageManager
{
    /// <summary>支持的语言（与 Resources/Languages/*.xaml 对应）</summary>
    public static readonly string[] Supported = ["zh-CN", "zh-TW", "en"];

    /// <summary>默认语言（缺省与 key 回退目标）</summary>
    public const string DefaultCulture = "zh-CN";

    private static readonly Dictionary<string, ResourceDictionary> _dictionaries = [];
    private static ResourceDictionary? _current;

    /// <summary>当前语言代码（zh-CN / zh-TW / en）</summary>
    public static string CurrentCulture { get; private set; } = DefaultCulture;

    /// <summary>语言切换事件（供代码动态刷新的 UI 订阅）</summary>
    public static event Action? LanguageChanged;

    /// <summary>应用语言：替换 Application.Resources 中的语言字典，DynamicResource 自动刷新全部绑定</summary>
    public static void ChangeLanguage(string culture)
    {
        if (!Supported.Contains(culture)) culture = DefaultCulture;
        var dict = GetDictionary(culture);
        var app = Application.Current;
        if (app is null) // 应用尚未完全初始化时只记录目标语言
        {
            CurrentCulture = culture;
            return;
        }
        if (_current is not null)
            app.Resources.MergedDictionaries.Remove(_current);
        app.Resources.MergedDictionaries.Add(dict);
        _current = dict;
        CurrentCulture = culture;
        LanguageChanged?.Invoke();
    }

    /// <summary>取本地化文本，支持 {0} 格式化；key 缺失时回退 zh-CN，再缺失返回 key 本身（防白屏）</summary>
    public static string Get(string key, params object?[] args)
    {
        // _current 为 null（ChangeLanguage 尚未调用，如单实例检测）时也回退到默认字典，避免返回裸 key
        var value = _current is null ? LookupFrom(DefaultCulture, key) : Lookup(key);
        if (value is null && CurrentCulture != DefaultCulture)
            value = LookupFrom(DefaultCulture, key);
        value ??= key;
        // 用 InvariantCulture 格式化，避免 {1:F1} 等数字格式随系统区域出现逗号小数点
        return args.Length > 0 ? string.Format(System.Globalization.CultureInfo.InvariantCulture, value, args) : value;
    }

    /// <summary>仅取当前语言文本，不做回退（供需要精确判断的场景）</summary>
    public static string? TryGet(string key)
        => _current?.Contains(key) == true ? _current[key] as string : null;

    private static ResourceDictionary GetDictionary(string culture)
    {
        if (!_dictionaries.TryGetValue(culture, out var dict))
        {
            // 与 Theme.xaml 同模式：Page 编译进程序集，相对 URI 解析为 pack URI
            dict = new ResourceDictionary
            {
                Source = new Uri($"/Resources/Languages/{culture}.xaml", UriKind.Relative)
            };
            _dictionaries[culture] = dict;
        }
        return dict;
    }

    private static string? Lookup(string key)
        => _current?.Contains(key) == true ? _current[key] as string : null;

    private static string? LookupFrom(string culture, string key)
    {
        var dict = GetDictionary(culture);
        return dict.Contains(key) ? dict[key] as string : null;
    }
}
