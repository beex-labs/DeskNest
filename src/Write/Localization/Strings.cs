using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeexWrite.Localization;

/// <summary>
/// Centralised UI strings for the BeexWrite shell. Defaults to English; call
/// <see cref="LoadLocale"/> with a culture code (e.g. "zh-CN") to switch at
/// runtime. Place translation JSON files in %AppData%/BeexWrite/locales/.
/// </summary>
public sealed partial class Strings : ObservableObject
{
    public static Strings Instance { get; } = new();

    // ---- File menu ----
    [ObservableProperty] private string _menuFile = "_File";
    [ObservableProperty] private string _menuNew = "New";
    [ObservableProperty] private string _menuNewWindow = "New Window";
    [ObservableProperty] private string _menuOpen = "Open...";
    [ObservableProperty] private string _menuOpenFolder = "Open Folder...";
    [ObservableProperty] private string _menuQuickOpen = "Quick Open...";
    [ObservableProperty] private string _menuOpenRecent = "Open Recent";
    [ObservableProperty] private string _menuOpenRecentFolder = "Open Recent Folder";
    [ObservableProperty] private string _menuReopenClosed = "Reopen Closed";
    [ObservableProperty] private string _menuSave = "Save";
    [ObservableProperty] private string _menuSaveAs = "Save As...";
    [ObservableProperty] private string _menuImport = "Import";
    [ObservableProperty] private string _menuImportPandoc = "Word / RTF / ODT / HTML / EPUB (Pandoc)...";
    [ObservableProperty] private string _menuExport = "Export";
    [ObservableProperty] private string _menuExportHtml = "HTML (with styles)...";
    [ObservableProperty] private string _menuExportHtmlPlain = "HTML (without styles)...";
    [ObservableProperty] private string _menuExportPdf = "PDF...";
    [ObservableProperty] private string _menuExportLongImage = "Long Image (PNG)...";
    [ObservableProperty] private string _menuExportPandoc = "Word / RTF / ODT / EPUB / LaTeX (Pandoc)...";
    [ObservableProperty] private string _menuImportTextBundle = "TextBundle / TextPack...";
    [ObservableProperty] private string _menuDownloadImages = "Download Remote Images";
    [ObservableProperty] private string _menuPreferences = "Preferences...";
    [ObservableProperty] private string _menuPrint = "Print...";
    [ObservableProperty] private string _menuExit = "Exit";

    // ---- Edit menu ----
    [ObservableProperty] private string _menuEdit = "_Edit";
    [ObservableProperty] private string _menuUndo = "Undo";
    [ObservableProperty] private string _menuRedo = "Redo";
    [ObservableProperty] private string _menuCut = "Cut";
    [ObservableProperty] private string _menuCopy = "Copy";
    [ObservableProperty] private string _menuPaste = "Paste";
    [ObservableProperty] private string _menuCopyAsMarkdown = "Copy as Markdown";
    [ObservableProperty] private string _menuCopyAsHtml = "Copy as HTML";
    [ObservableProperty] private string _menuPasteAsPlain = "Paste as Plain Text";
    [ObservableProperty] private string _menuSelectAll = "Select All";
    [ObservableProperty] private string _menuFind = "Find";
    [ObservableProperty] private string _menuReplace = "Replace";

    // ---- Paragraph menu ----
    [ObservableProperty] private string _menuParagraph = "_Paragraph";
    [ObservableProperty] private string _menuHeading1 = "Heading 1";
    [ObservableProperty] private string _menuHeading2 = "Heading 2";
    [ObservableProperty] private string _menuHeading3 = "Heading 3";
    [ObservableProperty] private string _menuHeading4 = "Heading 4";
    [ObservableProperty] private string _menuHeading5 = "Heading 5";
    [ObservableProperty] private string _menuHeading6 = "Heading 6";
    [ObservableProperty] private string _menuParagraphNormal = "Paragraph";
    [ObservableProperty] private string _menuPromoteHeading = "Promote Heading";
    [ObservableProperty] private string _menuDemoteHeading = "Demote Heading";
    [ObservableProperty] private string _menuOrderedList = "Ordered List";
    [ObservableProperty] private string _menuUnorderedList = "Unordered List";
    [ObservableProperty] private string _menuTaskList = "Task List";
    [ObservableProperty] private string _menuQuote = "Quote";
    [ObservableProperty] private string _menuCodeFence = "Code Fence";
    [ObservableProperty] private string _menuMathBlock = "Math Block";
    [ObservableProperty] private string _menuMermaid = "Mermaid Diagram";
    [ObservableProperty] private string _menuTable = "Table";
    [ObservableProperty] private string _menuHorizontalRule = "Horizontal Rule";

    // ---- Format menu ----
    [ObservableProperty] private string _menuFormat = "F_ormat";
    [ObservableProperty] private string _menuBold = "Bold";
    [ObservableProperty] private string _menuItalic = "Italic";
    [ObservableProperty] private string _menuUnderline = "Underline";
    [ObservableProperty] private string _menuStrikethrough = "Strikethrough";
    [ObservableProperty] private string _menuHighlight = "Highlight";
    [ObservableProperty] private string _menuInlineCode = "Inline Code";
    [ObservableProperty] private string _menuSuperscript = "Superscript";
    [ObservableProperty] private string _menuSubscript = "Subscript";
    [ObservableProperty] private string _menuInsertLink = "Insert Link";
    [ObservableProperty] private string _menuInsertImage = "Insert Image";
    [ObservableProperty] private string _menuHardLineBreak = "Hard Line Break";
    [ObservableProperty] private string _menuClearFormat = "Clear Format";

    // ---- View menu ----
    [ObservableProperty] private string _menuView = "_View";
    [ObservableProperty] private string _menuToggleSidebar = "Toggle Sidebar";
    [ObservableProperty] private string _menuSourceCodeMode = "Source Code Mode";
    [ObservableProperty] private string _menuFocusMode = "Focus Mode";
    [ObservableProperty] private string _menuTypewriterMode = "Typewriter Mode";
    [ObservableProperty] private string _menuZoomIn = "Zoom In";
    [ObservableProperty] private string _menuZoomOut = "Zoom Out";
    [ObservableProperty] private string _menuResetZoom = "Reset Zoom";
    [ObservableProperty] private string _menuRefreshRendering = "Refresh Rendering";
    [ObservableProperty] private string _menuToggleStatusBar = "Toggle Status Bar";
    [ObservableProperty] private string _menuSortFilesBy = "Sort Files By";
    [ObservableProperty] private string _menuSortName = "Name";
    [ObservableProperty] private string _menuSortModified = "Modified Time";
    [ObservableProperty] private string _menuSortCreated = "Created Time";
    [ObservableProperty] private string _menuFullScreen = "Full Screen";
    [ObservableProperty] private string _menuAlwaysOnTop = "Always on Top";

    // ---- Theme menu ----
    [ObservableProperty] private string _menuTheme = "_Theme";
    [ObservableProperty] private string _menuFollowSystem = "Follow System";
    [ObservableProperty] private string _menuLight = "Light";
    [ObservableProperty] private string _menuDark = "Dark";
    [ObservableProperty] private string _menuOpenThemeFolder = "Open Theme Folder";

    // ---- Help menu ----
    [ObservableProperty] private string _menuHelp = "_Help";
    [ObservableProperty] private string _menuKeyboardShortcuts = "Keyboard Shortcuts";
    [ObservableProperty] private string _menuFeatureTracker = "Feature Tracker";
    [ObservableProperty] private string _menuAbout = "About BeexWrite";

    // ---- Sidebar ----
    [ObservableProperty] private string _sidebarFiles = "Files";
    [ObservableProperty] private string _sidebarOutline = "Outline";
    [ObservableProperty] private string _sidebarSearch = "Search";
    [ObservableProperty] private string _sidebarOpenFolder = "Open a folder to browse files";

    // ---- Status bar ----
    [ObservableProperty] private string _statusWords = "words";
    [ObservableProperty] private string _statusChars = "chars";
    [ObservableProperty] private string _statusLines = "lines";
    [ObservableProperty] private string _statusMinRead = "min read";

    // ---- Dialogs ----
    [ObservableProperty] private string _preferencesTitle = "Preferences";
    [ObservableProperty] private string _shortcutsTitle = "Keyboard Shortcuts";
    [ObservableProperty] private string _quickOpenTitle = "Quick Open";
    [ObservableProperty] private string _insertTableTitle = "Insert Table";
    [ObservableProperty] private string _insertTableRows = "Rows";
    [ObservableProperty] private string _insertTableColumns = "Columns";
    [ObservableProperty] private string _btnInsert = "Insert";
    [ObservableProperty] private string _aboutTitle = "About BeexWrite";
    [ObservableProperty] private string _aboutDescription = "A modern Markdown editor for Windows";
    [ObservableProperty] private string _aboutVersion = "Version";
    [ObservableProperty] private string _unsavedPrompt = "You have unsaved changes. Save before continuing?";
    [ObservableProperty] private string _msgNoRemoteImages = "No remote images found.";
    [ObservableProperty] private string _msgImagesDownloaded = "Downloaded {0}/{1} images to assets/.";
    [ObservableProperty] private string _msgPandocRequired = "Pandoc is not installed. Please install it from https://pandoc.org and ensure it is on your PATH.";
    [ObservableProperty] private string _btnSave = "Save";
    [ObservableProperty] private string _btnDontSave = "Don't Save";
    [ObservableProperty] private string _btnCancel = "Cancel";
    [ObservableProperty] private string _btnOk = "OK";
    [ObservableProperty] private string _btnClose = "Close";

    // ---- Dialog titles & messages ----
    [ObservableProperty] private string _dlgSaveAs = "Save As";
    [ObservableProperty] private string _dlgExportHtml = "Export to HTML";
    [ObservableProperty] private string _dlgExportPdf = "Export PDF";
    [ObservableProperty] private string _dlgExportHtmlPlain = "Export to HTML (unstyled)";
    [ObservableProperty] private string _dlgExportLongImage = "Export Long Image";
    [ObservableProperty] private string _dlgExportPandoc = "Export via Pandoc";
    [ObservableProperty] private string _dlgImportPandoc = "Import via Pandoc";
    [ObservableProperty] private string _dlgImportTextBundle = "Import TextBundle/TextPack";
    [ObservableProperty] private string _dlgRename = "Rename";
    [ObservableProperty] private string _dlgRenamePrompt = "New name:";
    [ObservableProperty] private string _dlgNewFile = "New File";
    [ObservableProperty] private string _dlgNewFilePrompt = "File name:";
    [ObservableProperty] private string _dlgNewFolder = "New Folder";
    [ObservableProperty] private string _dlgNewFolderPrompt = "Folder name:";
    [ObservableProperty] private string _msgLongImageHandlerMissing = "Long image export handler not available.";
    [ObservableProperty] private string _msgPdfHandlerMissing = "PDF export handler not available.";
    [ObservableProperty] private string _msgDeleteConfirm = "Delete \"{0}\"? This cannot be undone.";
    [ObservableProperty] private string _msgFeatureTracker = "Feature tracker is available at docs/FEATURES.md in the project repository.";
    [ObservableProperty] private string _msgPandocExportFailed = "Pandoc export failed. Check the output format and try again.";
    [ObservableProperty] private string _msgPandocImportFailed = "Pandoc import failed.";
    [ObservableProperty] private string _msgTextBundleFailed = "Could not import TextBundle/TextPack.";
    [ObservableProperty] private string _shortcutsEditHint = "Edit shortcuts.json in AppData to customize shortcuts.";
    [ObservableProperty] private string _shortcutsColCommand = "Command";
    [ObservableProperty] private string _shortcutsColShortcut = "Shortcut";

    // ---- Status bar / tooltips ----
    [ObservableProperty] private string _statusSource = "Source";
    [ObservableProperty] private string _statusSel = "sel";
    [ObservableProperty] private string _tipMinimize = "Minimize";
    [ObservableProperty] private string _tipMaximize = "Maximize";
    [ObservableProperty] private string _tipClose = "Close";
    [ObservableProperty] private string _tipToggleSidebar = "Toggle sidebar";
    [ObservableProperty] private string _tipShowSidebar = "Show sidebar";
    [ObservableProperty] private string _tipSearchBox = "Search in the opened folder (Enter). Prefix with #tag to find tags.";

    // ---- Preferences export tab ----
    [ObservableProperty] private string _prefPaperSize = "Paper size:";
    [ObservableProperty] private string _prefMargin = "Margin:";
    [ObservableProperty] private string _prefBookmarks = "Include PDF bookmarks (headings)";
    [ObservableProperty] private string _prefExportNote = "PDF export uses WebView2. Word/RTF/EPUB/LaTeX requires Pandoc on PATH.";

    // ---- Context menu ----
    [ObservableProperty] private string _ctxOpen = "Open";
    [ObservableProperty] private string _ctxNewFile = "New File...";
    [ObservableProperty] private string _ctxNewFolder = "New Folder...";
    [ObservableProperty] private string _ctxRename = "Rename...";
    [ObservableProperty] private string _ctxDuplicate = "Duplicate";
    [ObservableProperty] private string _ctxDelete = "Delete";
    [ObservableProperty] private string _ctxInsertAsLink = "Insert as Link";
    [ObservableProperty] private string _ctxCopyPath = "Copy Path";
    [ObservableProperty] private string _ctxRevealInExplorer = "Reveal in Explorer";
    [ObservableProperty] private string _ctxRefresh = "Refresh";

    // ---- Search panel ----
    [ObservableProperty] private string _searchRegex = "Regex";
    [ObservableProperty] private string _searchBtn = "Search";

    // ---- Preferences tabs ----
    [ObservableProperty] private string _prefGeneral = "General";
    [ObservableProperty] private string _prefAppearance = "Appearance";
    [ObservableProperty] private string _prefEditor = "Editor";
    [ObservableProperty] private string _prefExport = "Export";
    [ObservableProperty] private string _prefAutoSave = "Enable auto-save";
    [ObservableProperty] private string _prefAutoSaveInterval = "Auto-save interval (seconds):";
    [ObservableProperty] private string _prefShowSidebar = "Show sidebar on startup";
    [ObservableProperty] private string _prefShowStatusBar = "Show status bar on startup";
    [ObservableProperty] private string _prefTheme = "Theme:";
    [ObservableProperty] private string _prefEditorWidth = "Editor width (px):";
    [ObservableProperty] private string _prefSourceMode = "Start in source-code mode";
    [ObservableProperty] private string _prefFocusMode = "Start in focus mode";
    [ObservableProperty] private string _prefTypewriterMode = "Start in typewriter mode";
    [ObservableProperty] private string _prefLanguage = "Language:";
    [ObservableProperty] private string _prefLangFollowSystem = "Follow System";
    [ObservableProperty] private string _prefLangEn = "English";
    [ObservableProperty] private string _prefLangZhCN = "简体中文";
    [ObservableProperty] private string _prefLangZhTW = "繁體中文";

    /// <summary>Loads translations from a JSON file (keys match property names without the underscore prefix).</summary>
    public void LoadLocale(string settingsDir, string locale)
    {
        // Reset to English defaults first
        ResetToDefaults();
        if (string.IsNullOrWhiteSpace(locale) || locale == "en") return;
        var path = Path.Combine(settingsDir, "locales", $"{locale}.json");
        if (!File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (dict is null) return;
            ApplyDict(dict);
        }
        catch { }
    }

    /// <summary>Resets all strings back to their English defaults.</summary>
    private void ResetToDefaults()
    {
        var defaults = new Strings();
        foreach (var prop in GetType().GetProperties())
        {
            if (prop.CanWrite && prop.PropertyType == typeof(string) && prop.Name != nameof(Instance))
            {
                var defaultVal = prop.GetValue(defaults);
                prop.SetValue(this, defaultVal);
            }
        }
    }

    private void ApplyDict(Dictionary<string, string> dict)
    {
        foreach (var (key, value) in dict)
        {
            var prop = GetType().GetProperty(key);
            if (prop is not null && prop.CanWrite && prop.PropertyType == typeof(string))
                prop.SetValue(this, value);
        }
    }

    /// <summary>Bump when built-in locale content changes; files with a matching
    /// version are left untouched so user edits survive restarts.</summary>
    private const string LocaleVersion = "4";

    /// <summary>Creates/upgrades the default locale JSON files. Existing files are only
    /// overwritten when their __version is older than the app's built-in locale version,
    /// so user customisations persist across launches.</summary>
    public static void EnsureDefaultLocales(string settingsDir)
    {
        var dir = Path.Combine(settingsDir, "locales");
        Directory.CreateDirectory(dir);
        EnsureZhCN(dir);
        EnsureZhTW(dir);
    }

    private static bool IsLocaleCurrent(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return dict is not null && dict.TryGetValue("__version", out var v) && v == LocaleVersion;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Backs up an existing locale file before a version upgrade overwrites it,
    /// so user customisations are never silently destroyed.</summary>
    private static void BackupLocale(string path)
    {
        try
        {
            if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);
        }
        catch { }
    }

    private static void EnsureZhCN(string dir)
    {
        var path = Path.Combine(dir, "zh-CN.json");
        if (IsLocaleCurrent(path)) return;
        BackupLocale(path);
        var d = new Dictionary<string, string>
        {
            ["__version"] = LocaleVersion,
            // File
            ["MenuFile"] = "文件(_F)", ["MenuNew"] = "新建", ["MenuNewWindow"] = "新窗口",
            ["MenuOpen"] = "打开...", ["MenuOpenFolder"] = "打开文件夹...",
            ["MenuQuickOpen"] = "快速打开...", ["MenuOpenRecent"] = "最近打开",
            ["MenuOpenRecentFolder"] = "最近打开的文件夹",
            ["MenuReopenClosed"] = "重新打开已关闭", ["MenuSave"] = "保存",
            ["MenuSaveAs"] = "另存为...", ["MenuImport"] = "导入",
            ["MenuImportPandoc"] = "Word / RTF / ODT / HTML / EPUB (Pandoc)...",
            ["MenuExport"] = "导出", ["MenuExportHtml"] = "HTML（含样式）...",
            ["MenuExportHtmlPlain"] = "HTML（无样式）...",
            ["MenuExportPdf"] = "PDF...", ["MenuExportLongImage"] = "长图（PNG）...",
            ["MenuExportPandoc"] = "Word / RTF / ODT / EPUB / LaTeX (Pandoc)...",
            ["MenuImportTextBundle"] = "TextBundle / TextPack...", ["MenuDownloadImages"] = "下载远程图片",
            ["MenuPreferences"] = "偏好设置...", ["MenuPrint"] = "打印...", ["MenuExit"] = "退出",
            // Edit
            ["MenuEdit"] = "编辑(_E)", ["MenuUndo"] = "撤销", ["MenuRedo"] = "重做",
            ["MenuCut"] = "剪切", ["MenuCopy"] = "复制", ["MenuPaste"] = "粘贴",
            ["MenuCopyAsMarkdown"] = "复制为 Markdown", ["MenuCopyAsHtml"] = "复制为 HTML",
            ["MenuPasteAsPlain"] = "粘贴为纯文本", ["MenuSelectAll"] = "全选",
            ["MenuFind"] = "查找", ["MenuReplace"] = "替换",
            // Paragraph
            ["MenuParagraph"] = "段落(_P)", ["MenuHeading1"] = "一级标题", ["MenuHeading2"] = "二级标题",
            ["MenuHeading3"] = "三级标题", ["MenuHeading4"] = "四级标题", ["MenuHeading5"] = "五级标题",
            ["MenuHeading6"] = "六级标题", ["MenuParagraphNormal"] = "正文",
            ["MenuPromoteHeading"] = "提升标题级别", ["MenuDemoteHeading"] = "降低标题级别",
            ["MenuOrderedList"] = "有序列表", ["MenuUnorderedList"] = "无序列表",
            ["MenuTaskList"] = "任务列表", ["MenuQuote"] = "引用",
            ["MenuCodeFence"] = "代码块", ["MenuMathBlock"] = "数学公式块",
            ["MenuMermaid"] = "Mermaid 图表", ["MenuTable"] = "表格", ["MenuHorizontalRule"] = "分割线",
            // Format
            ["MenuFormat"] = "格式(_O)", ["MenuBold"] = "粗体", ["MenuItalic"] = "斜体",
            ["MenuUnderline"] = "下划线", ["MenuStrikethrough"] = "删除线",
            ["MenuHighlight"] = "高亮", ["MenuInlineCode"] = "行内代码",
            ["MenuSuperscript"] = "上标", ["MenuSubscript"] = "下标",
            ["MenuInsertLink"] = "插入链接", ["MenuInsertImage"] = "插入图片",
            ["MenuHardLineBreak"] = "硬换行", ["MenuClearFormat"] = "清除格式",
            // View
            ["MenuView"] = "视图(_V)", ["MenuToggleSidebar"] = "切换侧边栏",
            ["MenuSourceCodeMode"] = "源码模式", ["MenuFocusMode"] = "专注模式",
            ["MenuTypewriterMode"] = "打字机模式", ["MenuZoomIn"] = "放大",
            ["MenuZoomOut"] = "缩小", ["MenuResetZoom"] = "重置缩放",
            ["MenuRefreshRendering"] = "刷新渲染", ["MenuToggleStatusBar"] = "切换状态栏",
            ["MenuSortFilesBy"] = "文件排序", ["MenuSortName"] = "名称",
            ["MenuSortModified"] = "修改时间", ["MenuSortCreated"] = "创建时间",
            ["MenuFullScreen"] = "全屏", ["MenuAlwaysOnTop"] = "窗口置顶",
            // Theme
            ["MenuTheme"] = "主题(_T)", ["MenuFollowSystem"] = "跟随系统",
            ["MenuLight"] = "浅色", ["MenuDark"] = "深色", ["MenuOpenThemeFolder"] = "打开主题文件夹",
            // Help
            ["MenuHelp"] = "帮助(_H)", ["MenuKeyboardShortcuts"] = "快捷键",
            ["MenuFeatureTracker"] = "功能追踪", ["MenuAbout"] = "关于 BeexWrite",
            // Sidebar
            ["SidebarFiles"] = "文件", ["SidebarOutline"] = "大纲", ["SidebarSearch"] = "搜索",
            ["SidebarOpenFolder"] = "打开文件夹以浏览文件",
            // Status bar
            ["StatusWords"] = "字", ["StatusChars"] = "字符", ["StatusLines"] = "行", ["StatusMinRead"] = "分钟阅读",
            // Dialogs
            ["PreferencesTitle"] = "偏好设置", ["ShortcutsTitle"] = "快捷键",
            ["QuickOpenTitle"] = "快速打开", ["InsertTableTitle"] = "插入表格",
            ["InsertTableRows"] = "行数", ["InsertTableColumns"] = "列数", ["BtnInsert"] = "插入",
            ["AboutTitle"] = "关于 BeexWrite", ["AboutDescription"] = "一款现代化的 Windows Markdown 编辑器",
            ["AboutVersion"] = "版本",
            ["UnsavedPrompt"] = "有未保存的更改，是否先保存？",
            ["MsgNoRemoteImages"] = "未找到远程图片。",
            ["MsgImagesDownloaded"] = "已下载 {0}/{1} 张图片到 assets/ 目录。",
            ["MsgPandocRequired"] = "未安装 Pandoc。请从 https://pandoc.org 安装并确保其在 PATH 中。",
            ["BtnSave"] = "保存", ["BtnDontSave"] = "不保存", ["BtnCancel"] = "取消", ["BtnOk"] = "确定", ["BtnClose"] = "关闭",
            // Dialog titles & messages
            ["DlgSaveAs"] = "另存为", ["DlgExportHtml"] = "导出 HTML", ["DlgExportPdf"] = "导出 PDF",
            ["DlgExportHtmlPlain"] = "导出 HTML（无样式）", ["DlgExportLongImage"] = "导出长图",
            ["DlgExportPandoc"] = "通过 Pandoc 导出", ["DlgImportPandoc"] = "通过 Pandoc 导入",
            ["DlgImportTextBundle"] = "导入 TextBundle/TextPack",
            ["DlgRename"] = "重命名", ["DlgRenamePrompt"] = "新名称：",
            ["DlgNewFile"] = "新建文件", ["DlgNewFilePrompt"] = "文件名：",
            ["DlgNewFolder"] = "新建文件夹", ["DlgNewFolderPrompt"] = "文件夹名：",
            ["MsgLongImageHandlerMissing"] = "长图导出功能不可用。",
            ["MsgPdfHandlerMissing"] = "PDF 导出功能不可用。",
            ["MsgDeleteConfirm"] = "删除 \"{0}\"？此操作无法撤销。",
            ["MsgFeatureTracker"] = "功能跟踪文档位于项目仓库的 docs/FEATURES.md。",
            ["MsgPandocExportFailed"] = "Pandoc 导出失败，请检查输出格式后重试。",
            ["MsgPandocImportFailed"] = "Pandoc 导入失败。",
            ["MsgTextBundleFailed"] = "无法导入 TextBundle/TextPack。",
            ["ShortcutsEditHint"] = "编辑 AppData 中的 shortcuts.json 可自定义快捷键。",
            ["ShortcutsColCommand"] = "命令", ["ShortcutsColShortcut"] = "快捷键",
            // Status bar / tooltips
            ["StatusSource"] = "源码", ["StatusSel"] = "选中",
            ["TipMinimize"] = "最小化", ["TipMaximize"] = "最大化", ["TipClose"] = "关闭", ["TipToggleSidebar"] = "切换侧边栏",
            ["TipShowSidebar"] = "显示侧边栏",
            ["TipSearchBox"] = "在已打开的文件夹中搜索（回车）。前缀 #标签 可搜索标签。",
            // Preferences export
            ["PrefPaperSize"] = "纸张大小：", ["PrefMargin"] = "页边距：", ["PrefBookmarks"] = "包含 PDF 书签（标题）",
            ["PrefExportNote"] = "PDF 导出使用 WebView2。Word/RTF/EPUB/LaTeX 需要 PATH 中安装 Pandoc。",
            // Context menu
            ["CtxOpen"] = "打开", ["CtxNewFile"] = "新建文件...", ["CtxNewFolder"] = "新建文件夹...",
            ["CtxRename"] = "重命名...", ["CtxDuplicate"] = "复制副本", ["CtxDelete"] = "删除",
            ["CtxInsertAsLink"] = "插入为链接", ["CtxCopyPath"] = "复制路径",
            ["CtxRevealInExplorer"] = "在资源管理器中显示", ["CtxRefresh"] = "刷新",
            // Search
            ["SearchRegex"] = "正则", ["SearchBtn"] = "搜索",
            // Preferences
            ["PrefGeneral"] = "通用", ["PrefAppearance"] = "外观", ["PrefEditor"] = "编辑器", ["PrefExport"] = "导出",
            ["PrefAutoSave"] = "启用自动保存", ["PrefAutoSaveInterval"] = "自动保存间隔（秒）：",
            ["PrefShowSidebar"] = "启动时显示侧边栏", ["PrefShowStatusBar"] = "启动时显示状态栏",
            ["PrefTheme"] = "主题：", ["PrefEditorWidth"] = "编辑器宽度（像素）：",
            ["PrefSourceMode"] = "启动时使用源码模式", ["PrefFocusMode"] = "启动时使用专注模式",
            ["PrefTypewriterMode"] = "启动时使用打字机模式",
            ["PrefLanguage"] = "语言：", ["PrefLangFollowSystem"] = "跟随系统",
            ["PrefLangEn"] = "English", ["PrefLangZhCN"] = "简体中文", ["PrefLangZhTW"] = "繁體中文"
        };
        File.WriteAllText(path, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void EnsureZhTW(string dir)
    {
        var path = Path.Combine(dir, "zh-TW.json");
        if (IsLocaleCurrent(path)) return;
        BackupLocale(path);
        var d = new Dictionary<string, string>
        {
            ["__version"] = LocaleVersion,
            // File
            ["MenuFile"] = "檔案(_F)", ["MenuNew"] = "新增", ["MenuNewWindow"] = "新視窗",
            ["MenuOpen"] = "開啟...", ["MenuOpenFolder"] = "開啟資料夾...",
            ["MenuQuickOpen"] = "快速開啟...", ["MenuOpenRecent"] = "最近開啟",
            ["MenuOpenRecentFolder"] = "最近開啟的資料夾",
            ["MenuReopenClosed"] = "重新開啟已關閉", ["MenuSave"] = "儲存",
            ["MenuSaveAs"] = "另存新檔...", ["MenuImport"] = "匯入",
            ["MenuImportPandoc"] = "Word / RTF / ODT / HTML / EPUB (Pandoc)...",
            ["MenuExport"] = "匯出", ["MenuExportHtml"] = "HTML（含樣式）...",
            ["MenuExportHtmlPlain"] = "HTML（無樣式）...",
            ["MenuExportPdf"] = "PDF...", ["MenuExportLongImage"] = "長圖（PNG）...",
            ["MenuExportPandoc"] = "Word / RTF / ODT / EPUB / LaTeX (Pandoc)...",
            ["MenuImportTextBundle"] = "TextBundle / TextPack...", ["MenuDownloadImages"] = "下載遠端圖片",
            ["MenuPreferences"] = "偏好設定...", ["MenuPrint"] = "列印...", ["MenuExit"] = "結束",
            // Edit
            ["MenuEdit"] = "編輯(_E)", ["MenuUndo"] = "復原", ["MenuRedo"] = "重做",
            ["MenuCut"] = "剪下", ["MenuCopy"] = "複製", ["MenuPaste"] = "貼上",
            ["MenuCopyAsMarkdown"] = "複製為 Markdown", ["MenuCopyAsHtml"] = "複製為 HTML",
            ["MenuPasteAsPlain"] = "貼上為純文字", ["MenuSelectAll"] = "全選",
            ["MenuFind"] = "尋找", ["MenuReplace"] = "取代",
            // Paragraph
            ["MenuParagraph"] = "段落(_P)", ["MenuHeading1"] = "標題 1", ["MenuHeading2"] = "標題 2",
            ["MenuHeading3"] = "標題 3", ["MenuHeading4"] = "標題 4", ["MenuHeading5"] = "標題 5",
            ["MenuHeading6"] = "標題 6", ["MenuParagraphNormal"] = "內文",
            ["MenuPromoteHeading"] = "提升標題層級", ["MenuDemoteHeading"] = "降低標題層級",
            ["MenuOrderedList"] = "有序清單", ["MenuUnorderedList"] = "無序清單",
            ["MenuTaskList"] = "任務清單", ["MenuQuote"] = "引用",
            ["MenuCodeFence"] = "程式碼區塊", ["MenuMathBlock"] = "數學公式區塊",
            ["MenuMermaid"] = "Mermaid 圖表", ["MenuTable"] = "表格", ["MenuHorizontalRule"] = "分隔線",
            // Format
            ["MenuFormat"] = "格式(_O)", ["MenuBold"] = "粗體", ["MenuItalic"] = "斜體",
            ["MenuUnderline"] = "底線", ["MenuStrikethrough"] = "刪除線",
            ["MenuHighlight"] = "螢光標記", ["MenuInlineCode"] = "行內程式碼",
            ["MenuSuperscript"] = "上標", ["MenuSubscript"] = "下標",
            ["MenuInsertLink"] = "插入連結", ["MenuInsertImage"] = "插入圖片",
            ["MenuHardLineBreak"] = "硬換行", ["MenuClearFormat"] = "清除格式",
            // View
            ["MenuView"] = "檢視(_V)", ["MenuToggleSidebar"] = "切換側邊欄",
            ["MenuSourceCodeMode"] = "原始碼模式", ["MenuFocusMode"] = "專注模式",
            ["MenuTypewriterMode"] = "打字機模式", ["MenuZoomIn"] = "放大",
            ["MenuZoomOut"] = "縮小", ["MenuResetZoom"] = "重設縮放",
            ["MenuRefreshRendering"] = "重新整理渲染", ["MenuToggleStatusBar"] = "切換狀態列",
            ["MenuSortFilesBy"] = "檔案排序", ["MenuSortName"] = "名稱",
            ["MenuSortModified"] = "修改時間", ["MenuSortCreated"] = "建立時間",
            ["MenuFullScreen"] = "全螢幕", ["MenuAlwaysOnTop"] = "視窗置頂",
            // Theme
            ["MenuTheme"] = "佈景主題(_T)", ["MenuFollowSystem"] = "跟隨系統",
            ["MenuLight"] = "淺色", ["MenuDark"] = "深色", ["MenuOpenThemeFolder"] = "開啟佈景主題資料夾",
            // Help
            ["MenuHelp"] = "說明(_H)", ["MenuKeyboardShortcuts"] = "鍵盤快速鍵",
            ["MenuFeatureTracker"] = "功能追蹤", ["MenuAbout"] = "關於 BeexWrite",
            // Sidebar
            ["SidebarFiles"] = "檔案", ["SidebarOutline"] = "大綱", ["SidebarSearch"] = "搜尋",
            ["SidebarOpenFolder"] = "開啟資料夾以瀏覽檔案",
            // Status bar
            ["StatusWords"] = "字", ["StatusChars"] = "字元", ["StatusLines"] = "行", ["StatusMinRead"] = "分鐘閱讀",
            // Dialogs
            ["PreferencesTitle"] = "偏好設定", ["ShortcutsTitle"] = "鍵盤快速鍵",
            ["QuickOpenTitle"] = "快速開啟", ["InsertTableTitle"] = "插入表格",
            ["InsertTableRows"] = "列數", ["InsertTableColumns"] = "欄數", ["BtnInsert"] = "插入",
            ["AboutTitle"] = "關於 BeexWrite", ["AboutDescription"] = "一款現代化的 Windows Markdown 編輯器",
            ["AboutVersion"] = "版本",
            ["UnsavedPrompt"] = "有未儲存的變更，是否先儲存？",
            ["MsgNoRemoteImages"] = "未找到遠端圖片。",
            ["MsgImagesDownloaded"] = "已下載 {0}/{1} 張圖片到 assets/ 目錄。",
            ["MsgPandocRequired"] = "未安裝 Pandoc。請從 https://pandoc.org 安裝並確保其在 PATH 中。",
            ["BtnSave"] = "儲存", ["BtnDontSave"] = "不儲存", ["BtnCancel"] = "取消", ["BtnOk"] = "確定", ["BtnClose"] = "關閉",
            // Dialog titles & messages
            ["DlgSaveAs"] = "另存新檔", ["DlgExportHtml"] = "匯出 HTML", ["DlgExportPdf"] = "匯出 PDF",
            ["DlgExportHtmlPlain"] = "匯出 HTML（無樣式）", ["DlgExportLongImage"] = "匯出長圖",
            ["DlgExportPandoc"] = "透過 Pandoc 匯出", ["DlgImportPandoc"] = "透過 Pandoc 匯入",
            ["DlgImportTextBundle"] = "匯入 TextBundle/TextPack",
            ["DlgRename"] = "重新命名", ["DlgRenamePrompt"] = "新名稱：",
            ["DlgNewFile"] = "新增檔案", ["DlgNewFilePrompt"] = "檔案名稱：",
            ["DlgNewFolder"] = "新增資料夾", ["DlgNewFolderPrompt"] = "資料夾名稱：",
            ["MsgLongImageHandlerMissing"] = "長圖匯出功能不可用。",
            ["MsgPdfHandlerMissing"] = "PDF 匯出功能不可用。",
            ["MsgDeleteConfirm"] = "刪除 \"{0}\"？此操作無法復原。",
            ["MsgFeatureTracker"] = "功能追蹤文件位於專案儲存庫的 docs/FEATURES.md。",
            ["MsgPandocExportFailed"] = "Pandoc 匯出失敗，請檢查輸出格式後重試。",
            ["MsgPandocImportFailed"] = "Pandoc 匯入失敗。",
            ["MsgTextBundleFailed"] = "無法匯入 TextBundle/TextPack。",
            ["ShortcutsEditHint"] = "編輯 AppData 中的 shortcuts.json 可自訂快速鍵。",
            ["ShortcutsColCommand"] = "命令", ["ShortcutsColShortcut"] = "快速鍵",
            // Status bar / tooltips
            ["StatusSource"] = "原始碼", ["StatusSel"] = "選取",
            ["TipMinimize"] = "最小化", ["TipMaximize"] = "最大化", ["TipClose"] = "關閉", ["TipToggleSidebar"] = "切換側邊欄",
            ["TipShowSidebar"] = "顯示側邊欄",
            ["TipSearchBox"] = "在已開啟的資料夾中搜尋（Enter）。前綴 #標籤 可搜尋標籤。",
            // Preferences export
            ["PrefPaperSize"] = "紙張大小：", ["PrefMargin"] = "頁邊距：", ["PrefBookmarks"] = "包含 PDF 書籤（標題）",
            ["PrefExportNote"] = "PDF 匯出使用 WebView2。Word/RTF/EPUB/LaTeX 需要 PATH 中安裝 Pandoc。",
            // Context menu
            ["CtxOpen"] = "開啟", ["CtxNewFile"] = "新增檔案...", ["CtxNewFolder"] = "新增資料夾...",
            ["CtxRename"] = "重新命名...", ["CtxDuplicate"] = "複製副本", ["CtxDelete"] = "刪除",
            ["CtxInsertAsLink"] = "插入為連結", ["CtxCopyPath"] = "複製路徑",
            ["CtxRevealInExplorer"] = "在檔案總管中顯示", ["CtxRefresh"] = "重新整理",
            // Search
            ["SearchRegex"] = "正規表示式", ["SearchBtn"] = "搜尋",
            // Preferences
            ["PrefGeneral"] = "一般", ["PrefAppearance"] = "外觀", ["PrefEditor"] = "編輯器", ["PrefExport"] = "匯出",
            ["PrefAutoSave"] = "啟用自動儲存", ["PrefAutoSaveInterval"] = "自動儲存間隔（秒）：",
            ["PrefShowSidebar"] = "啟動時顯示側邊欄", ["PrefShowStatusBar"] = "啟動時顯示狀態列",
            ["PrefTheme"] = "佈景主題：", ["PrefEditorWidth"] = "編輯器寬度（像素）：",
            ["PrefSourceMode"] = "啟動時使用原始碼模式", ["PrefFocusMode"] = "啟動時使用專注模式",
            ["PrefTypewriterMode"] = "啟動時使用打字機模式",
            ["PrefLanguage"] = "語言：", ["PrefLangFollowSystem"] = "跟隨系統",
            ["PrefLangEn"] = "English", ["PrefLangZhCN"] = "简体中文", ["PrefLangZhTW"] = "繁體中文"
        };
        File.WriteAllText(path, JsonSerializer.Serialize(d, new JsonSerializerOptions { WriteIndented = true }));
    }
}
