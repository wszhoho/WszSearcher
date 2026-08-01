# WszSearcher

基于 USN Journal + Lucene.NET 的 Windows 极速文件搜索工具。毫秒级文件名索引，配合中文全文搜索和拼音模糊匹配。

## 功能

- **毫秒级文件名搜索** — NTFS USN Journal 直接读取 MFT，无需遍历目录
- **毫秒级中文全文搜索** — Lucene.NET 倒排索引 + jieba 中文分词 + ToolGood.Words 拼音
- **拼音模糊搜索** — 支持拼音首字母（`wj` → `文件`）和全拼（`wenjian` → `文件`）
- **多盘多目录** — 支持跨盘符多目录索引
- **极低的内存占用** — 搜索完成后自动释放内存占用，常态化内存占用约4~70MB物理内存
- **全局热键** — 默认 `Alt+Space` 呼出/隐藏，支持自定义快捷键及冲突检测
- **浮动预览** — 选中结果弹出预览窗口，吸附主窗口右侧、支持拖离拖回
- **预览高亮** — 搜索词黄色高亮，▲▼按钮导航匹配位置，一键复制全文
- **右键菜单** — 打开文件 / 打开文件位置 / 复制文件 / 复制文件名
- **系统托盘** — 最小化到托盘，支持开机自启
- **增量监听** — FileSystemWatcher 自动保持索引实时更新
- **单实例保护** — 只允许一个进程运行
- **便携绿色** — 所有设置存储在 exe 同级目录，不写入系统目录


<img width="651" height="324" alt="1" src="https://github.com/user-attachments/assets/7f64ad09-0be2-4da6-9d59-37bf9965f64e" />
<img width="918" height="456" alt="3" src="https://github.com/user-attachments/assets/0a7a74eb-ee5d-445c-be6b-7a7f4c46987c" />
<img width="619" height="71" alt="1" src="https://github.com/user-attachments/assets/8de6c14a-ffb1-464f-abca-ea47e9f3bf6f" />


## 技术栈

| 组件 | 用途 |
|------|------|
| C# .NET 10 + WPF | 桌面应用框架 |
| Win32 USN Journal API | NTFS MFT 毫秒级扫描 |
| Lucene.NET 3.0.3 | 全文检索引擎（倒排索引） |
| jieba.NET | 中文分词（前缀词典 + HMM） |
| ToolGood.Words | 拼音首字母/全拼转换 |
| PdfPig | PDF 文档文本提取 |
| DocumentFormat.OpenXml | Office 文档（DOCX/XLSX/PPTX）解析 |
| Hardcodet.NotifyIcon.Wpf | 系统托盘图标 |
| CommunityToolkit.Mvvm | MVVM 架构（ObservableObject + RelayCommand） |

## 项目结构

```
src/
├── WszSearcher.Core/                # 核心库（无 UI 依赖）
│   ├── Analysis/                    # 中文分词 & 拼音
│   │   ├── JiebaAnalyzer.cs        # jieba → Lucene Analyzer 适配
│   │   └── PinyinHelper.cs         # ToolGood.Words 封装（首字母/全拼）
│   ├── ContentSearch/
│   │   ├── ContentIndexer.cs        # Lucene 索引构建（全量并行+增量）
│   │   ├── ContentSearcher.cs       # Lucene 查询（多字段+中文分词）
│   │   └── Parsers/                 # 文档解析器
│   │       ├── IDocumentParser.cs   # 解析器接口
│   │       ├── TextParser.cs        # 文本/代码文件（UTF-8/GBK 自动检测）
│   │       ├── OfficeParser.cs      # DOCX/XLSX/PPTX
│   │       ├── PdfParser.cs         # PDF
│   │       └── ParserRegistry.cs    # 解析器注册与路由
│   ├── FileNameSearch/
│   │   ├── UsnFileScanner.cs        # NTFS USN Journal 扫描（MFT 枚举）
│   │   ├── FileNameIndex.cs         # 内存索引（前缀/包含/路径/拼音搜索）
│   │   ├── FileNameSearchProvider.cs # 调度器（多盘扫描+索引+FSW 监听）
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
    ├── App.xaml/.cs                 # 启动入口、托盘菜单、开机自启、单实例互斥
    ├── MainWindow.xaml/.cs          # 主搜索窗口（无边框、WM_NCHITTEST 拖动）
    ├── Converters/                  # 值转换器
    ├── Services/
    │   ├── AppSettings.cs           # 设置持久化（exe 同级）
    │   └── GlobalHotkeyService.cs   # Win32 RegisterHotKey 封装+冲突检测
    ├── Styles/Theme.xaml            # 样式资源字典
    ├── ViewModels/
    │   ├── MainViewModel.cs         # 主窗口 VM（搜索/防抖/预览/展开）
    │   ├── SettingsViewModel.cs     # 设置窗口 VM（索引路径/后缀/快捷键）
    │   └── SearchResultViewModel.cs # 搜索结果 VM
    ├── Views/
    │   ├── SettingsWindow.xaml/.cs  # 设置窗口（索引管理+停止按钮）
    │   ├── AboutWindow.xaml/.cs     # 关于窗口
    │   └── PreviewWindow.xaml/.cs   # 浮动预览窗口（吸附+高亮+导航）
    └── Resources/                   # 图标、jieba 词典数据
```

## 工作原理

### 文件名搜索（USN Journal）

1. 通过 `CreateFile("\\\\.\\C:")` 打开卷设备
2. 使用 `DeviceIoControl(FSCTL_ENUM_USN_DATA)` 枚举 MFT 中所有文件的 USN 记录
3. 解析 `USN_RECORD_V2` 结构体，通过父 FRN 递归还原完整路径
4. 跨盘扫描时分别打开各卷合并结果
5. 构建内存索引 `FileNameIndex`（前缀 → 包含 → 路径 → 拼音，四级匹配）
6. 每个盘符独立 `FileSystemWatcher` 监听变更，保持索引实时更新

### 内容搜索（Lucene）

1. `EnumerateFilesFromPaths()` 按设置路径遍历目录
2. `ParserRegistry` 根据扩展名路由到对应解析器提取文本
3. Lucene `IndexWriter` + `JiebaAnalyzer` 构建倒排索引（`Parallel.ForEach` 并行）
4. `MultiFieldQueryParser` 同时搜索 filename + content 字段
5. 结果与文件名搜索结果合并去重，按索引路径过滤

## 运行环境

- **操作系统**：Windows 10 1607+（LTSC/Enterprise）/ Windows 11 / Windows Server 2012 R2+
- **运行时**：[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- **文件系统**：NTFS（USN Journal 特性依赖）
- **权限**：管理员权限下获得毫秒级扫描速度；普通权限自动降级为目录遍历

## 构建

```bash
# Debug
dotnet build

# Release
dotnet build -c Release
```

## 许可

MIT License.
