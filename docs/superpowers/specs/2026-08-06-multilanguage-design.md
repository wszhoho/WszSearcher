# WszSearcher 多语言支持设计

日期：2026-08-06
状态：已批准（用户）

## 目标

支持简体中文 / 繁体中文 / 英文三种语言，运行时动态切换，语言选择持久化到 settings.json。

## 背景现状

- 全部 UI 文本为简体中文硬编码：XAML ~70 条（6 个文件）、UI 层 .cs ~20 条、Core 层 ~40 条状态消息。
- 零本地化基础设施（无 resx、无 x:Uid、无 CultureInfo 使用）。
- `ThemeManager` 提供成熟的"覆盖 Application.Resources 键值 + DynamicResource 引用"动态切换模式（ThemeManager.cs:40-89），直接照搬。
- `AppSettings` 用 System.Text.Json 序列化到 exe 同目录 settings.json，Load 对缺字段有兜底（AppSettings.cs:59-66），新增 Language 字段天然兼容。
- Core 层零 UI 依赖，不得引入 WPF 或 NuGet 运行时依赖。

## 设计决策（用户已确认）

1. **切换方式**：运行时动态切换（DynamicResource 自动刷新）。
2. **Core 层消息**：事件改发资源 key，UI 层负责翻译显示。
3. **繁中翻译**：开发期用 OpenCC 简→繁自动转换 + 人工校对，不引入运行时依赖。

## 架构

```
语言资源（3 个 ResourceDictionary：zh-CN / zh-TW / en）
        ↑ 动态切换（LanguageManager.ChangeLanguage）
XAML 全部改 {DynamicResource Lang.xxx}
Core 层事件改发 StatusMessage{Key, Args} → UI 层 LanguageManager.Get(key, args) 翻译
AppSettings.Language 持久化（默认 zh-CN）
```

## 组件

### 1. 语言资源文件（新增 3 个）

`src/WszSearcher/Resources/Languages/{zh-CN,zh-TW,en}.xaml`，每份约 130 个 key：

- `Lang.*` 前缀：静态 UI 文本（窗口标题、按钮、菜单、ToolTip、占位符、设置分组）
- `Status.*` 前缀：Core 动态状态消息，值含 `{0}` `{1}` 格式化占位符
- key 缺失回退：当前语言 → zh-CN → 显示 key 本身（防白屏）

### 2. LanguageManager（新增 Services/LanguageManager.cs）

- `ChangeLanguage(string culture)`：替换 Application.Resources 中的语言字典，DynamicResource 自动刷新全部绑定
- `Get(string key, params object[] args)`：取值 + string.Format，三级回退
- 启动时在 App.xaml.cs 加载设置后应用

### 3. XAML 改造（6 个文件）

所有硬编码文本 → `{DynamicResource Lang.xxx}`。MainWindow.xaml:170-172 三段 Run（"找到 X 个结果"）改 StringFormat 绑定。App.xaml 托盘菜单 4 项 + ToolTip 一并处理。

### 4. Core 层 key 化（4 个文件 + 接口）

- 新增 `Core/Localization/StatusMessage.cs`：`{ string Key; object?[] Args; }`
- 事件签名 `Action<string>` → `Action<StatusMessage>`：
  - `ISearchService.cs:15` StatusMessage
  - `FileNameSearchProvider.cs:45` StatusMessage
  - `ContentIndexer.cs:28` StatusChanged
  - `UsnFileScanner.cs:18` StatusChanged
- 约 40 处调用点改发 key；PreviewService 的预览错误/截断消息同样 key 化
- Core 层零新增依赖、不碰 WPF

### 5. 设置持久化 + UI

- `AppSettings.cs` 加 `Language` 属性（默认 "zh-CN"）
- SettingsWindow 新增"语言"下拉（简体中文 / 繁體中文 / English），选择即保存并立即切换

### 6. 翻译细节

- zh-TW：开发期一次性 OpenCC 转换（脚本生成，产物入库），人工校对核心术语（索引/搜索/快捷键/托盘/预览）
- en：人工翻译
- 不译：日期格式 `MM/dd HH:mm`、B/KB/MB/GB 单位、快捷键键名（Space/Tab 等 ASCII）

## 验证

- `dotnet build -c Release` 0 错误
- 三种语言启动 + 运行时切换冒烟测试（托盘菜单、设置窗口、索引状态消息）
- key 缺失回退机制测试

## 改动文件清单

新增：
- `src/WszSearcher/Resources/Languages/zh-CN.xaml`
- `src/WszSearcher/Resources/Languages/zh-TW.xaml`
- `src/WszSearcher/Resources/Languages/en.xaml`
- `src/WszSearcher/Services/LanguageManager.cs`
- `src/WszSearcher.Core/Localization/StatusMessage.cs`

修改：
- `src/WszSearcher/App.xaml`、`App.xaml.cs`
- `src/WszSearcher/ViewModels/MainViewModel.cs`、`SettingsViewModel.cs`
- `src/WszSearcher/Views/{MainWindow,SettingsWindow,AboutWindow,PreviewWindow}.xaml`
- `src/WszSearcher/Services/AppSettings.cs`
- `src/WszSearcher.Core/Search/{ISearchService,SearchService}.cs`
- `src/WszSearcher.Core/FileNameSearch/{FileNameSearchProvider,UsnFileScanner}.cs`
- `src/WszSearcher.Core/ContentSearch/ContentIndexer.cs`
- `src/WszSearcher.Core/Preview/PreviewService.cs`
