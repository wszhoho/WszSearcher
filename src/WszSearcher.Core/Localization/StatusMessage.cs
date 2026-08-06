namespace WszSearcher.Core.Localization;

/// <summary>本地化状态消息——事件只携带 key 与参数，由 UI 层负责翻译显示</summary>
public sealed class StatusMessage
{
    public StatusMessage(string key, params object?[] args)
    {
        Key = key;
        Args = args;
    }

    /// <summary>资源 key（Lang/Status 前缀）</summary>
    public string Key { get; }

    /// <summary>格式化参数（可空）</summary>
    public object?[] Args { get; }
}
