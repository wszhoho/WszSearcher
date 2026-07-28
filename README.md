# WszSearcher

Windows 极速文件搜索工具，Everything 级别的文件名匹配 + 中文全文搜索。

## 功能

- **毫秒级文件名搜索** — 基于 NTFS USN Journal 直接读取 MFT，无需遍历目录
- **中文全文搜索** — Lucene.NET 倒排索引 + jieba 中文分词
- **全局热键** — `Alt+Space` 呼出/隐藏，支持自定义快捷键
- **浮动预览** — 选中结果自动弹出预览窗口，吸附主窗口右侧
- **深色/浅色/跟随系统** — 三档主题实时切换
- **系统托盘** — 最小化到托盘，开机自启

## 技术栈

| 组件 | 用途 |
|------|------|
| C# .NET 10 + WPF | 桌面应用框架 |
| Lucene.NET 3.0.3 | 全文检索引擎（倒排索引） |
| jieba.NET | 中文分词（前缀词典 + HMM） |
| PdfPig | PDF 文档文本提取 |
| DocumentFormat.OpenXml | Office 文档（DOCX/XLSX/PPTX）解析 |
| Hardcodet.NotifyIcon.Wpf | 系统托盘图标 |
| CommunityToolkit.Mvvm | MVVM 架构（ObservableObject + RelayCommand） |

## 项目结构

```
src/
├── WszSearcher.Core/                # 核心库（无 UI 依赖）
│   ├── Analysis/JiebaAnalyzer.cs    # jieba 分词 → Lucene Analyzer 适配
│   ├── ContentSearch/
│   │   ├── ContentIndexer.cs        # Lucene 索引构建（全量+增量）
│   │   ├── ContentSearcher.cs       # Lucene 查询（多字段+中文分词）
│   │   └── Parsers/                 # 文档解析器
│   │       ├── IDocumentParser.cs   # 解析器接口
│   │       ├── TextParser.cs        # 文本/代码文件（UTF-8/GBK 自动检测）
│   │       ├── OfficeParser.cs      # DOCX/XLSX/PPTX
│   │       ├── PdfParser.cs         # PDF
│   │       └── ParserRegistry.cs    # 解析器注册与路由
│   ├── FileNameSearch/
│   │   ├── UsnFileScanner.cs        # NTFS USN Journal 扫描（MFT 枚举）
│   │   ├── FileNameIndex.cs         # 内存索引（前缀/包含/路径搜索）
│   │   ├── FileNameSearchProvider.cs # 调度器（扫描+索引+FileSystemWatcher）
│   │   └── FileRecord.cs           # 文件记录模型
│   ├── Native/UsnApi.cs            # Win32 P/Invoke（DeviceIoControl/USN）
│   ├── Preview/
│   │   ├── PreviewService.cs       # 预览服务（文本/代码/图片/Office）
│   │   └── PreviewResult.cs        # 预览结果模型
│   ├── Search/
│   │   ├── ISearchService.cs       # 搜索服务接口
│   │   └── SearchService.cs        # 搜索编排（文件名+内容合并去重）
│   └── Models/SearchResult.cs      # 搜索结果模型
│
└── WszSearcher/                     # WPF 前端
    ├── App.xaml/.cs                 # 启动入口、托盘菜单、主题切换
    ├── MainWindow.xaml/.cs          # 主搜索窗口（无边框、WM_NCHITTEST 拖动）
    ├── Converters/                  # 值转换器
    ├── Services/
    │   ├── AppSettings.cs           # 设置持久化（%LocalAppData%）
    │   ├── GlobalHotkeyService.cs   # Win32 RegisterHotKey 封装
    │   └── ThemeManager.cs          # 动态主题切换
    ├── Styles/Theme.xaml            # 主题资源字典（颜色/画笔/样式）
    ├── ViewModels/
    │   ├── MainViewModel.cs         # 主窗口 VM（搜索/防抖/预览/展开）
    │   ├── SettingsViewModel.cs     # 设置窗口 VM（索引/快捷键/主题）
    │   └── SearchResultViewModel.cs # 搜索结果 VM
    ├── Views/
    │   ├── SettingsWindow.xaml/.cs  # 设置窗口
    │   ├── AboutWindow.xaml/.cs     # 关于窗口
    │   └── PreviewWindow.xaml/.cs   # 浮动预览窗口（吸附）
    └── Resources/                   # 图标、jieba 词典数据
```

## 工作原理

### 文件名搜索（USN Journal）

1. 通过 `CreateFile("\\\\.\\C:")` 打开卷设备
2. 使用 `DeviceIoControl(FSCTL_ENUM_USN_DATA)` 枚举 MFT 中所有文件的 USN 记录
3. 解析 `USN_RECORD_V2` 结构体，通过父 FRN 递归还原完整路径
4. 构建内存索引 `FileNameIndex`（前缀 → 包含 → 路径，三级匹配）
5. `FileSystemWatcher` 监听变更，保持索引实时更新

### 内容搜索（Lucene）

1. `EnumerateFilesFromPaths()` 按设置路径遍历目录
2. `ParserRegistry` 根据扩展名路由到对应解析器提取文本
3. Lucene `IndexWriter` + `JiebaAnalyzer` 构建倒排索引
4. `MultiFieldQueryParser` 同时搜索 filename + content 字段
5. 结果与文件名搜索结果合并去重，按索引路径过滤

## 运行环境

- **操作系统**：Windows 10 1809+ / Windows 11 / Windows Server 2019+
- **运行时**：[.NET 10 Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)（桌面运行时）
- **文件系统**：NTFS（USN Journal 特性依赖）
- **权限**：管理员权限下可获得 Everything 级扫描速度；普通权限自动降级为目录遍历

## 构建

```bash
# Debug
dotnet build

# Release 单文件发布
dotnet publish -c Release
```

## 许可

MIT License.
