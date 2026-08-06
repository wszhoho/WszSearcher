namespace WszSearcher.Core.Localization;

/// <summary>状态消息资源 key 常量——与语言资源文件（Resources/Languages/*.xaml）中的 Status.* 键一一对应</summary>
public static class StatusKeys
{
    // SearchService
    public const string LoadingFileNameIndex = "Status.LoadingFileNameIndex";
    public const string ContentIndexReady = "Status.ContentIndexReady";
    public const string ContentIndexNotBuilt = "Status.ContentIndexNotBuilt";
    public const string ContentIndexCompleteNoBackfill = "Status.ContentIndexCompleteNoBackfill";
    public const string BackfillingMissingIndex = "Status.BackfillingMissingIndex";
    public const string BackfillComplete = "Status.BackfillComplete";
    public const string BackfillCancelled = "Status.BackfillCancelled";
    public const string BuildingFileNameIndex = "Status.BuildingFileNameIndex";
    public const string BuildingContentIndex = "Status.BuildingContentIndex";
    public const string ContentIndexBuildFailed = "Status.ContentIndexBuildFailed";
    public const string ContentIndexCancelled = "Status.ContentIndexCancelled";
    public const string ContentIndexRebuildFailed = "Status.ContentIndexRebuildFailed";
    public const string RebuildComplete = "Status.RebuildComplete";
    public const string IndexCancelledFileName = "Status.IndexCancelledFileName";
    public const string IndexCancelledContent = "Status.IndexCancelledContent";

    // FileNameSearchProvider
    public const string FileNameIndexReady = "Status.FileNameIndexReady";
    public const string FileNameScanCancelled = "Status.FileNameScanCancelled";
    public const string FileNameIndexFailed = "Status.FileNameIndexFailed";
    public const string FileWatcherStartFailed = "Status.FileWatcherStartFailed";
    public const string FileWatcherError = "Status.FileWatcherError";

    // UsnFileScanner
    public const string UsnUnavailableFallback = "Status.UsnUnavailableFallback";
    public const string UsnNotAvailable = "Status.UsnNotAvailable";
    public const string EnumeratingMft = "Status.EnumeratingMft";
    public const string ScannedRecords = "Status.ScannedRecords";
    public const string MftScanComplete = "Status.MftScanComplete";
    public const string ResolvingPaths = "Status.ResolvingPaths";
    public const string ScanComplete = "Status.ScanComplete";

    // ContentIndexer
    public const string NoFilesToIndex = "Status.NoFilesToIndex";
    public const string ContentIndexingProgress = "Status.ContentIndexingProgress";
    public const string ContentIndexComplete = "Status.ContentIndexComplete";

    // PreviewService
    public const string PreviewFileNotFound = "Status.PreviewFileNotFound";
    public const string PreviewLoadFailed = "Status.PreviewLoadFailed";
    public const string PreviewBinaryNotSupported = "Status.PreviewBinaryNotSupported";
    public const string PreviewFileTooLarge = "Status.PreviewFileTooLarge";
    public const string PreviewCancelled = "Status.PreviewCancelled";
}
