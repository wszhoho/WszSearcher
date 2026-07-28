using CommunityToolkit.Mvvm.ComponentModel;
using WszSearcher.Core.Models;

namespace WszSearcher.ViewModels;

/// <summary>搜索结果项的 ViewModel</summary>
public partial class SearchResultViewModel : ObservableObject
{
    public SearchResultViewModel(SearchResult model)
    {
        Model = model;
        _fileName = model.FileName;
        _fullPath = model.FullPath;
        _directory = model.Directory;
        _fileSize = FormatFileSize(model.FileSize);
        _lastModified = model.LastModified;
        _resultType = model.ResultType;
        _matchSnippet = model.MatchSnippet;
        _extension = model.Extension;
    }

    public SearchResult Model { get; }

    [ObservableProperty]
    private string _fileName;

    [ObservableProperty]
    private string _fullPath;

    [ObservableProperty]
    private string _directory;

    [ObservableProperty]
    private string _fileSize;

    [ObservableProperty]
    private DateTime _lastModified;

    [ObservableProperty]
    private SearchResultType _resultType;

    [ObservableProperty]
    private string _matchSnippet;

    [ObservableProperty]
    private string _extension;

    public bool IsContentMatch => ResultType == SearchResultType.Content;
    public bool IsFileNameMatch => ResultType == SearchResultType.FileName;

    private static string FormatFileSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
        _ => $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB"
    };
}
