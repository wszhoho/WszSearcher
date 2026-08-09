# WszSearcher

A blazing-fast Windows file search tool built on USN Journal + Lucene.NET. Millisecond-level file name indexing, combined with Chinese full-text search and pinyin fuzzy matching.

## Features

- **Millisecond file name search** — reads the MFT directly via the NTFS USN Journal, no directory traversal
- **Millisecond Chinese full-text search** — Lucene.NET inverted index + jieba word segmentation + ToolGood.Words pinyin
- **Pinyin fuzzy search** — supports pinyin initials (`wj` → `文件`) and full pinyin (`wenjian` → `文件`)
- **Multiple drives and directories** — cross-drive, multi-directory indexing
- **Very low memory footprint** — automatically releases memory after searching; typical resident memory ~4–70 MB physical
- **Global hotkey** — default `Alt+Space` to show/hide, customizable with conflict detection
- **Floating preview** — a preview window pops up for the selected result, docks to the right of the main window, and can be detached and re-docked
- **Preview highlighting** — search terms highlighted in yellow, ▲▼ buttons navigate between matches, one-click copy of full text
- **Context menu** — open file / open file location / copy file / copy file name
- **System tray** — minimizes to the tray, supports launch at startup
- **Incremental watching** — FileSystemWatcher keeps the index up to date in real time
- **Single-instance guard** — only one process is allowed to run
- **Portable & green** — all settings are stored next to the executable, nothing is written to system directories

<img width="651" height="324" alt="1" src="https://github.com/user-attachments/assets/e9a648ba-e616-4356-90c7-8dcb9be6478d" />
<img width="918" height="456" alt="2" src="https://github.com/user-attachments/assets/566d1535-703e-4334-9d6c-5a7f34dcc875" />
<img width="619" height="71" alt="1" src="https://github.com/user-attachments/assets/8de6c14a-ffb1-464f-abca-ea47e9f3bf6f" />


## Tech Stack

| Component | Purpose |
|-----------|---------|
| C# .NET 10 + WPF | Desktop application framework |
| Win32 USN Journal API | Millisecond NTFS MFT scanning |
| Lucene.NET 3.0.3 | Full-text search engine (inverted index) |
| jieba.NET | Chinese word segmentation (prefix dictionary + HMM) |
| ToolGood.Words | Pinyin initials / full pinyin conversion |
| PdfPig | PDF text extraction |
| DocumentFormat.OpenXml | Office document (DOCX/XLSX/PPTX) parsing |
| Hardcodet.NotifyIcon.Wpf | System tray icon |
| CommunityToolkit.Mvvm | MVVM architecture (ObservableObject + RelayCommand) |

## Project Structure

```
src/
├── WszSearcher.Core/                # Core library (no UI dependency)
│   ├── Analysis/                    # Chinese word segmentation & pinyin
│   │   ├── JiebaAnalyzer.cs        # jieba → Lucene Analyzer adapter
│   │   └── PinyinHelper.cs         # ToolGood.Words wrapper (initials/full pinyin)
│   ├── ContentSearch/
│   │   ├── ContentIndexer.cs        # Lucene index building (full parallel + incremental)
│   │   ├── ContentSearcher.cs       # Lucene query (multi-field + Chinese segmentation)
│   │   └── Parsers/                 # Document parsers
│   │       ├── IDocumentParser.cs   # Parser interface
│   │       ├── TextParser.cs        # Text/code files (UTF-8/GBK auto detection)
│   │       ├── OfficeParser.cs      # DOCX/XLSX/PPTX
│   │       ├── PdfParser.cs         # PDF
│   │       └── ParserRegistry.cs    # Parser registration & routing
│   ├── FileNameSearch/
│   │   ├── UsnFileScanner.cs        # NTFS USN Journal scanning (MFT enumeration)
│   │   ├── FileNameIndex.cs         # In-memory index (prefix/contains/path/pinyin search)
│   │   ├── FileNameSearchProvider.cs # Coordinator (multi-drive scan + index + FSW watching)
│   │   └── FileRecord.cs           # File record model
│   ├── Native/UsnApi.cs            # Win32 P/Invoke (DeviceIoControl/USN)
│   ├── Preview/
│   │   ├── PreviewService.cs       # Preview service (text/code/image/Office)
│   │   └── PreviewResult.cs        # Preview result model
│   ├── Search/
│   │   ├── ISearchService.cs       # Search service interface
│   │   └── SearchService.cs        # Search orchestration (file name + content merge/dedupe)
│   └── Models/SearchResult.cs      # Search result model
│
└── WszSearcher/                     # WPF frontend
    ├── App.xaml/.cs                 # Entry point, tray menu, auto-start, single-instance mutex
    ├── MainWindow.xaml/.cs          # Main search window (borderless, WM_NCHITTEST dragging)
    ├── Converters/                  # Value converters
    ├── Services/
    │   ├── AppSettings.cs           # Settings persistence (next to the executable)
    │   └── GlobalHotkeyService.cs   # Win32 RegisterHotKey wrapper + conflict detection
    ├── Styles/Theme.xaml            # Style resource dictionary
    ├── ViewModels/
    │   ├── MainViewModel.cs         # Main window VM (search/debounce/preview/expand)
    │   ├── SettingsViewModel.cs     # Settings window VM (index paths/extensions/hotkey)
    │   └── SearchResultViewModel.cs # Search result VM
    ├── Views/
    │   ├── SettingsWindow.xaml/.cs  # Settings window (index management + stop button)
    │   ├── AboutWindow.xaml/.cs     # About window
    │   └── PreviewWindow.xaml/.cs   # Floating preview window (docking + highlight + navigation)
    └── Resources/                   # Icons, jieba dictionary data
```

## How It Works

### File Name Search (USN Journal)

1. Open the volume device via `CreateFile("\\\\.\\drive:")`
2. Enumerate USN records of all files in the MFT using `DeviceIoControl(FSCTL_ENUM_USN_DATA)`
3. Parse the `USN_RECORD_V2` structure and reconstruct full paths recursively via parent FRN
4. For cross-drive scans, open each volume separately and merge the results
5. Build the in-memory `FileNameIndex` (prefix → contains → path → pinyin, four-level matching)
6. Each drive gets its own `FileSystemWatcher` to keep the index up to date in real time

### Content Search (Lucene)

1. `EnumerateFilesFromPaths()` walks the directories under the configured paths
2. `ParserRegistry` routes to the matching parser by extension to extract text
3. Lucene `IndexWriter` + `JiebaAnalyzer` build the inverted index (`Parallel.ForEach` parallelization)
4. `MultiFieldQueryParser` searches the filename + content fields simultaneously
5. Results are merged and deduplicated with the file name search results, then filtered by index paths

## Requirements

- **OS**: Windows 10 1607+ (LTSC/Enterprise) / Windows 11 / Windows Server 2012 R2+
- **Runtime**: [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- **File system**: NTFS (depends on the USN Journal feature)
- **Permissions**: millisecond scanning speed under administrator privileges; falls back to directory traversal automatically without them

## Build

```bash
# Debug
dotnet build

# Release
dotnet build -c Release
```

## License

MIT License.
