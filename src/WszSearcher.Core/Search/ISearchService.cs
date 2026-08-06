using WszSearcher.Core.Localization;
using WszSearcher.Core.Models;

namespace WszSearcher.Core.Search;

/// <summary>搜索服务接口——统一文件名搜索与内容搜索</summary>
public interface ISearchService
{
    /// <summary>搜索结果变更事件</summary>
    event Action<IReadOnlyList<SearchResult>>? ResultsUpdated;

    /// <summary>搜索状态变更事件</summary>
    event Action<SearchStatus>? StatusChanged;

    /// <summary>索引状态消息（携带资源 key 与参数，由 UI 层翻译显示）</summary>
    event Action<StatusMessage>? StatusMessage;

    /// <summary>索引进度（已处理文件数）</summary>
    event Action<int>? ProgressChanged;

    /// <summary>实时索引更新完成事件（文件增删改同步到索引后触发，UI 用于自动刷新结果）</summary>
    event Action? IndexUpdated;

    /// <summary>异步搜索</summary>
    Task SearchAsync(string query, CancellationToken ct = default);

    /// <summary>初始化（首次全量建索引）</summary>
    Task InitializeAsync();

    /// <summary>快速初始化：仅扫文件名，内容索引从磁盘恢复（启动时用）</summary>
    Task QuickInitAsync();

    /// <summary>重建索引（清空后重新扫描+索引）</summary>
    Task RebuildIndexAsync();

    /// <summary>取消正在进行的索引构建</summary>
    void CancelIndex();

    /// <summary>设置内容索引扫描路径</summary>
    void SetIndexPaths(List<string> paths);

    /// <summary>设置内容索引文件后缀</summary>
    void SetContentExtensions(List<string> extensions);

    /// <summary>设置排除目录模式（*\node_modules 等），扫描与 watcher 事件过滤共用</summary>
    void SetExcludePaths(List<string> patterns);

    /// <summary>当前搜索状态</summary>
    SearchStatus Status { get; }

    /// <summary>文件名索引中的文件总数</summary>
    int FileNameIndexCount { get; }

    /// <summary>内容索引中的文档总数</summary>
    int ContentIndexCount { get; }
}

public enum SearchStatus
{
    Idle,
    Indexing,
    Searching,
    Ready
}
