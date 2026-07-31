using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MediaColor = System.Windows.Media.Color;
using WpfOrientation = System.Windows.Controls.Orientation;
using Image = System.Windows.Controls.Image;
using Control = System.Windows.Controls.Control;

namespace BeeX.DeskNest;

/// <summary>
/// Ctrl+Q Global Search Window (successor to the Quick Launch bar):
/// When the app is launched, only a blank search bar appears; after you type something, a list of drop-down results appears.
/// No prefix = Unified Search (BeeX commands + programs + full-disk file index + direct paths/URLs + formulas);
/// Prefix filtering: = Calculations, @ Commands, . Programs, ? Documents only, / Web pages, ~ Frequently used folders, !! Previous results.
/// File search is powered by our in-house MFT/USN index (FileIndexService), which delivers millisecond-level results across the entire disk.
/// </summary>
public sealed class SearchPaletteWindow : Window
{
    readonly DeskNestService service;
    readonly TextBox input;
    readonly ListBox results;
    readonly TextBlock hint;
    readonly Border card;
    readonly DispatcherTimer delay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    int revision;
    bool dragging;
    static readonly Lazy<List<string>> programShortcuts = new(BuildProgramIndex, true);

    /// <summary>One result: Icon=SVG icon name; when IconPath is not empty, the application uses the actual Shell logo from that file; if extraction fails, it falls back to the default icon; when KeepOpen=true, clicking the menu item fills only the prefix without closing the window. </summary>
    sealed record PaletteResult(string Icon, string Title, string Detail, Action Run, string? IconPath = null, bool KeepOpen = false);

    public SearchPaletteWindow(DeskNestService service)
    {
        this.service = service;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        SizeToContent = SizeToContent.Height;
        // Window width = Card width (640) + transparent margins on both sides (24*2): to allow space for the DropShadow glow to fade naturally into the transparent areas,
        // Not being cropped into four square corners by the window’s rectangular boundaries (this is the real reason for the “four protruding corners”).
        Width = 688;

        input = new TextBox
        {
            FontSize = 16, Padding = new Thickness(10, 4, 10, 4), BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            CaretBrush = new SolidColorBrush(MediaColor.FromRgb(255, 138, 0)),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        input.TextChanged += (_, _) => { delay.Stop(); delay.Start(); };
        input.PreviewKeyDown += Input_KeyDown;

        results = new ListBox
        {
            BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Margin = new Thickness(0, 6, 0, 2), MaxHeight = 420, Visibility = Visibility.Collapsed,
            ItemTemplate = BuildTemplate(), ItemContainerStyle = BuildItemStyle(), Focusable = false
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        results.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (VisualTreeUtils.FindParent<ListBoxItem>(e.OriginalSource as DependencyObject) is { } item)
            { results.SelectedItem = item.DataContext; RunSelection(); e.Handled = true; }
        };

        hint = new TextBlock
        {
            Foreground = new SolidColorBrush(MediaColor.FromRgb(148, 156, 173)), FontSize = 11,
            Margin = new Thickness(4, 2, 4, 0), HorizontalAlignment = HorizontalAlignment.Right,
            Visibility = Visibility.Collapsed
        };

        var stack = new StackPanel();
        stack.Children.Add(input);
        stack.Children.Add(results);
        stack.Children.Add(hint);

        card = new Border
        {
            Background = new SolidColorBrush(MediaColor.FromArgb(246, 13, 19, 33)),
            BorderBrush = new SolidColorBrush(MediaColor.FromArgb(200, 255, 138, 0)),
            BorderThickness = new Thickness(1.5), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(24), Child = stack,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = MediaColor.FromRgb(255, 138, 0), BlurRadius = 20, ShadowDepth = 0, Opacity = .45 }
        };
        Content = card;
        
        delay.Tick += async (_, _) => { delay.Stop(); await RefreshAsync(); };
        Deactivated += (_, _) => { if (!dragging) HidePalette(); };
        // In Windows 11, DWM still adds approximately 8px of system-provided rounded corners/borders to window rectangles with `WindowStyle=None`, which is inconsistent with the 12px rounded corners of the card border,
        // In DWM, the smaller square corners "stick out" beyond the card's rounded corners—set `DisableSystemShadow` to `DONOTROUND` and the border color to `NONE` to eliminate these four protruding corners.
        SourceInitialized += (_, _) => { WindowRegionHelper.HideFromAltTab(this); WindowRegionHelper.DisableSystemShadow(this); };
        // Drag the search pane: Click on a blank area of a card to drag and reposition it (interactive elements such as input fields and result lists are not considered draggable); the position is saved when you release the mouse.
        card.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed || InputHitTestHelper.IsInteractive(e.OriginalSource as DependencyObject)) return;
            dragging = true;
            try { DragMove(); } catch { }
            dragging = false;
            service.State.PaletteLeft = Left; service.State.PaletteTop = Top; service.SaveSoon();
        };
        // There's no need to suspend global hotkeys while the window is in focus—but the input field requires an input method.
        InputMethod.SetIsInputMethodEnabled(input, true);
    }

    string L(string value) => Localization.T(value, service.State.Language);

    /// <summary> Follow app theme (Dark / Honey / Transparent): The card background color and text contrast color are recalculated each time the app is launched </summary>
    void ApplyTheme()
    {
        var theme = service.State.Theme;
        var dark = theme == "Dark";
        var honey = theme == "Honey";
        var surface = dark ? MediaColor.FromRgb(13, 19, 33) : honey ? MediaColor.FromRgb(255, 244, 222) : MediaColor.FromRgb(245, 247, 250);
        var background = new SolidColorBrush(MediaColor.FromArgb(246, surface.R, surface.G, surface.B));
        var foreground = ContrastHelper.TextFor(background, dark ? Brushes.White : new SolidColorBrush(MediaColor.FromRgb(13, 19, 33)));
        card.Background = background;
        input.Foreground = foreground;
        results.Foreground = foreground;
        hint.Foreground = new SolidColorBrush(dark ? MediaColor.FromRgb(198, 205, 218) : MediaColor.FromRgb(102, 112, 133));
        FontFamily = new System.Windows.Media.FontFamily(service.InterfaceFontFamily());
    }

    /// <summary>On activation: Use the last drag position if available; otherwise, position the element at the top 24% of the screen, centered, relative to the mouse cursor; clear the input and set focus. </summary>
    public void ShowPalette()
    {
        ApplyTheme();
        var area = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        // If the last dragged position is still within the visible area of a screen, use that; otherwise, center it (to prevent the window from flying off the screen after unplugging the secondary monitor).
        var restore = service.State.PaletteLeft is double left && service.State.PaletteTop is double top
            && left >= SystemParameters.VirtualScreenLeft - 40 && top >= SystemParameters.VirtualScreenTop - 40
            && left <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80
            && top <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40;
        if (restore) { Left = service.State.PaletteLeft!.Value; Top = service.State.PaletteTop!.Value; }
        else
        {
            Left = area.Left / dpi.DpiScaleX + (area.Width / dpi.DpiScaleX - Width) / 2;
            Top = area.Top / dpi.DpiScaleY + area.Height / dpi.DpiScaleY * 0.24;
        }
        input.Text = "";
        ShowGuideOrEmpty();
        Show();
        Activate();
        input.Focus();
        input.ToolTip = L("輸入以搜索文件、程式、網頁或計算");
        hint.Text = L("↑↓ 選擇 · Enter 開啟 · Esc 清除");
    }

    void HidePalette() { try { Hide(); } catch { } }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e) { e.Cancel = true; HidePalette(); }

    void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down) { results.SelectedIndex = Math.Min(results.Items.Count - 1, results.SelectedIndex + 1); results.ScrollIntoView(results.SelectedItem); e.Handled = true; }
        else if (e.Key == Key.Up) { results.SelectedIndex = Math.Max(0, results.SelectedIndex - 1); results.ScrollIntoView(results.SelectedItem); e.Handled = true; }
        else if (e.Key == Key.Enter) { RunSelection(); e.Handled = true; }
        else if (e.Key == Key.Escape && input.Text.Length > 0) { delay.Stop(); input.Clear(); ShowGuideOrEmpty(); e.Handled = true; }
        else if (e.Key == Key.Escape) { HidePalette(); e.Handled = true; }
    }

    void RunSelection()
    {
        if (results.SelectedItem is not PaletteResult result) return;
        if (result.KeepOpen) { try { result.Run(); } catch { } return; } // Guidance: Enter only the prefix; do not execute, do not close the window
        service.State.PaletteLastResult = result.Title;
        service.SaveSoon();
        HidePalette();
        try { result.Run(); } catch { }
    }

    // When no input is provided: If the guide is enabled, a drop-down list of commands is displayed; otherwise, the area remains blank (controlled by the ShowSearchPaletteGuide setting).
    void ShowGuideOrEmpty() => SetResults(service.State.ShowSearchPaletteGuide ? GuideResults() : []);

    // Enter the prefix and focus: TextChanged triggers RefreshAsync to display results/suggestions for that prefix; the window remains open.
    void FillPrefix(string prefix) { input.Text = prefix; input.CaretIndex = input.Text.Length; input.Focus(); }

    /// <summary>Drop-down command guide when the input is empty (one command per line): Click or press Enter to enter the corresponding prefix instead of executing the command; this can be disabled in settings. </summary>
    List<PaletteResult> GuideResults() =>
    [
        new("search", L("直接輸入即可綜合搜尋"), L("自動識別路徑、網址、程式與文件"), () => { }, KeepOpen: true),
        new("settings", "@ " + L("搜尋 BeeX 設定與功能"), L("例如 @ 截圖、@ 設定"), () => FillPrefix("@"), KeepOpen: true),
        new("rocket", ". " + L("搜尋應用程式"), L("例如 .chrome"), () => FillPrefix("."), KeepOpen: true),
        new("folder", "file " + L("搜尋文件與資料夾"), L("例如 file 報告"), () => FillPrefix("file "), KeepOpen: true),
        new("math-function", "= " + L("計算數學表達式"), L("例如 = 1+1/2(9*8)"), () => FillPrefix("= "), KeepOpen: true),
        new("world", "?? " + L("用瀏覽器搜尋"), L("例如 ?? BeeX DeskNest"), () => FillPrefix("?? "), KeepOpen: true),
        new("database", ": " + L("搜尋註冊表"), L("例如 :Explorer"), () => FillPrefix(":"), KeepOpen: true),
        new("refresh", "!! " + L("最近一次選擇的結果"), L("重新開啟上次選中的項目"), () => FillPrefix("!!"), KeepOpen: true),
    ];

    async Task RefreshAsync()
    {
        try
        {
            var current = ++revision;
            var text = input.Text.Trim();
            if (text.Length == 0) { ShowGuideOrEmpty(); return; }
            List<PaletteResult> list;
            // Prefix Routing (Long/Full-Width Prefixes Checked First): !! Previous Results, ?? Browser, = Calculation, @ Software Settings/Features, . Application, file File/Folder, ~ Frequently Used Folders; all support full-width and half-width characters; if no prefix is specified, use Unified.
            if (text.StartsWith("!!") || text.StartsWith("！！")) list = PreviousResult();
            else if (text.StartsWith("??") || text.StartsWith("？？")) list = WebSearch(text[2..].Trim());
            else if (text.StartsWith('=') || text.StartsWith('＝')) list = Calculate(text[1..]);
            else if (text.StartsWith('@')) list = Filter(BeeXCommands(), text[1..]);
            else if (text.StartsWith('.')) list = await Task.Run(() => SearchPrograms(text[1..].Trim(), 24));
            else if (text.Equals("file", StringComparison.OrdinalIgnoreCase) || text.StartsWith("file ", StringComparison.OrdinalIgnoreCase)) list = await Task.Run(() => SearchFiles(text.Length > 5 ? text[5..].Trim() : "", 24));
            else if (text.StartsWith('~')) list = CommonFolders(text[1..].Trim());
            else if (text.StartsWith(':') || text.StartsWith('：')) list = await Task.Run(() => SearchRegistry(text[1..].Trim(), 24));
            else list = await Task.Run(() => Unified(text));
            if (current != revision) return;
            SetResults(list.Count == 0 ? [new("search", L("沒有找到結果"), L("請嘗試其他關鍵字"), () => { })] : list);
        }
        catch
        {
            SetResults([new("info-circle", L("搜尋失敗"), L("搜尋已安全停止，BeeX 會繼續運行"), () => { })]);
        }
    }

    /// <summary>Unified Search Without Prefix: Direct Path/URL → Formula → BeeX Command → Program → All Files → Web Fallback. </summary>
    List<PaletteResult> Unified(string text)
    {
        var list = new List<PaletteResult>();
        var raw = text.Trim().Trim('"');
        var expanded = Environment.ExpandEnvironmentVariables(raw);
        if (File.Exists(expanded) || Directory.Exists(expanded))
        {
            var name = Path.GetFileName(expanded.TrimEnd(Path.DirectorySeparatorChar));
            var isDir = Directory.Exists(expanded);
            list.Add(new(isDir ? "folder" : "note", name is { Length: > 0 } ? name : expanded, expanded, () => OpenPath(expanded)));
            return list;
        }
        if (TryWebAddress(raw, out var url)) { list.Add(new("world", L("開啟網頁"), url, () => OpenPath(url))); return list; }
        if (LooksLikeExpression(raw) && BeeXExpression.TryEvaluate(raw, out var value))
        {
            var formatted = value.ToString("G12", CultureInfo.CurrentCulture);
            list.Add(new("math-function", formatted, raw + "   ·   " + L("複製"), () => Clipboard.SetText(formatted)));
        }
        list.AddRange(Filter(BeeXCommands(), raw).Take(2));
        list.AddRange(SearchPrograms(raw, 5));
        list.AddRange(SearchFiles(raw, 15));
        list.Add(new("world", $"{L("搜尋")}「{raw}」", L("使用預設瀏覽器搜尋網頁"), () => OpenPath("https://www.bing.com/search?q=" + Uri.EscapeDataString(raw))));
        return list;
    }

    // Only expressions containing numbers (including full-width characters) and operators/parentheses (including full-width characters) are treated as prefix expressions; these are passed to BeeXExpression for normalization before calculation.
    static bool LooksLikeExpression(string text) => text.Length > 0
        && text.Any(c => char.IsDigit(c) || c is >= '０' and <= '９')
        && text.Any(c => c is '+' or '-' or '*' or '/' or '(' or '×' or '÷' or '＋' or '－' or '＊' or '／' or '（' or '−');

    List<PaletteResult> SearchFiles(string query, int limit)
    {
        if (query.Length == 0) return [new("note", L("請輸入文件名稱"), "例如 ? report", () => { })];
        if (service.FileIndex.Available)
        {
            var hits = service.FileIndex.Search(query, limit);
            var list = hits.Select(h => new PaletteResult(h.IsDirectory ? "folder" : "note", h.Name, h.FullPath, () => OpenPath(h.FullPath))).ToList();
            if (!service.FileIndex.Ready) list.Insert(0, new("refresh", L("正在建立文件索引…"), L("結果可能不完整，稍候片刻"), () => { }));
            return list;
        }
        // Index unavailable (extreme case: no NTFS volumes on the entire disk): Fall back to recursively scanning the Desktop, Documents, and Downloads directories
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") }.Distinct().Where(Directory.Exists);
        var found = new List<string>();
        foreach (var root in roots) SafeFind(root, query, found, limit, 4);
        return found.Select(path => new PaletteResult(Directory.Exists(path) ? "folder" : "note", Path.GetFileName(path), path, () => OpenPath(path))).ToList();
    }

    static void SafeFind(string folder, string query, List<string> found, int limit, int depth)
    {
        if (found.Count >= limit || depth < 0) return;
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                if (Path.GetFileName(path).Contains(query, StringComparison.CurrentCultureIgnoreCase)) found.Add(path);
                if (found.Count >= limit) return;
                if (depth > 0 && Directory.Exists(path) && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) SafeFind(path, query, found, limit, depth - 1);
            }
        }
        catch { }
    }

    List<PaletteResult> SearchPrograms(string query, int limit)
    {
        if (query.Length == 0) return [new("rocket", L("請輸入程式名稱"), "例如 . paint", () => { })];
        return programShortcuts.Value
            .Where(path => Path.GetFileNameWithoutExtension(path).Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(limit)
            .Select(path => new PaletteResult("rocket", Path.GetFileNameWithoutExtension(path), L("應用程式"), () => OpenPath(path), path)).ToList();
    }

    static List<string> BuildProgramIndex()
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) };
        var shortcuts = new List<string>();
        foreach (var root in roots.Where(Directory.Exists)) FindShortcuts(root, shortcuts, 5000, 12);
        return shortcuts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void FindShortcuts(string folder, List<string> found, int limit, int depth)
    {
        if (found.Count >= limit || depth < 0) return;
        try { found.AddRange(Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly)); } catch { }
        if (depth == 0) return;
        string[] directories;
        try { directories = Directory.GetDirectories(folder); } catch { return; }
        foreach (var directory in directories)
        {
            try { if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue; } catch { continue; }
            FindShortcuts(directory, found, limit, depth - 1);
            if (found.Count >= limit) return;
        }
    }

    List<PaletteResult> BeeXCommands() =>
    [
        new("settings", "BeeX " + L("設定"), L("調整外觀、快捷鍵與功能格子"), service.ShowSettings),
        new("screenshot", L("立即截圖"), L("開始區域截圖"), () => service.CaptureScreen()),
        new("folder-open", L("截圖文件夾"), L("開啟 BeeX 截圖保存位置"), service.OpenCaptureFolder),
        new("eye", L("顯示 / 隱藏全部"), L("切換全部未鎖定格子"), service.ToggleAll),
        new("layout", L("主控制台"), L("開啟 BeeX DeskNest 控制台"), service.ShowControl),
        new("sparkles", L("系統清理"), L("卸載程式並清理殘留"), service.ShowCleaner),
        new("note", L("新增便箋"), L("建立新的靈感便箋格子"), () => service.Add(NestKind.Note)),
        new("checklist", L("新增待辦"), L("建立新的待辦格子"), () => service.Add(NestKind.Todo)),
        new("calendar", L("新增倒數日"), L("建立新的倒數日格子"), () => service.Add(NestKind.Countdown)),
        new("sun", L("新增天氣"), L("建立新的天氣格子"), () => service.Add(NestKind.Weather)),
        new("music", L("新增音樂控制"), L("建立新的播放器格子"), () => service.Add(NestKind.Music))
    ];

    List<PaletteResult> PreviousResult()
    {
        var previous = service.State.PaletteLastResult;
        if (string.IsNullOrWhiteSpace(previous)) return [new("refresh", L("尚無歷史結果"), L("執行任一結果後會保存在這裡"), () => { })];
        return [new("refresh", previous, L("再次搜尋或開啟"), () => { if (File.Exists(previous) || Directory.Exists(previous)) OpenPath(previous); else { input.Text = previous; input.CaretIndex = previous.Length; } })];
    }

    List<PaletteResult> WebSearch(string query)
    {
        if (query.Length == 0) return [new("world", L("請輸入搜尋內容"), "例如 / BeeX DeskNest", () => { })];
        return [new("world", $"{L("搜尋")}「{query}」", L("使用預設瀏覽器"), () => OpenPath("https://www.bing.com/search?q=" + Uri.EscapeDataString(query)))];
    }

    List<PaletteResult> CommonFolders(string query)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[]
        {
            (L("桌面"), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            (L("下載"), Path.Combine(home, "Downloads")),
            (L("文件"), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            (L("圖片"), Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            (L("音樂"), Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            (L("使用者目錄"), home)
        };
        return folders.Where(x => Directory.Exists(x.Item2) && (string.IsNullOrWhiteSpace(query) || x.Item1.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .Select(x => new PaletteResult("folder", x.Item1, x.Item2, () => OpenPath(x.Item2))).ToList();
    }

    // Registry Search (Read-Only): Searches for keywords based on subkey names under HKCU / HKLM; matching entries are opened using regedit;
    // There are triple limits on the number of nodes, depth, and time to prevent slowdowns during full-table traversal; the process is read-only and does not modify registry data at any point.
    List<PaletteResult> SearchRegistry(string query, int limit)
    {
        if (query.Length == 0) return [new("database", L("請輸入註冊表關鍵字"), "例如 : Explorer", () => { })];
        var results = new List<PaletteResult>();
        var roots = new (string Name, RegistryKey Hive)[] { ("HKEY_CURRENT_USER", Registry.CurrentUser), ("HKEY_LOCAL_MACHINE", Registry.LocalMachine) };
        var sw = Stopwatch.StartNew();
        var visited = 0;
        foreach (var (name, hive) in roots)
        {
            if (results.Count >= limit || sw.ElapsedMilliseconds > 900) break;
            SearchRegistryKey(hive, name, query, results, limit, ref visited, sw, 0);
        }
        return results;
    }

    void SearchRegistryKey(RegistryKey key, string path, string query, List<PaletteResult> results, int limit, ref int visited, Stopwatch sw, int depth)
    {
        if (results.Count >= limit || depth > 7 || visited > 25000 || sw.ElapsedMilliseconds > 900) return;
        string[] subs;
        try { subs = key.GetSubKeyNames(); } catch { return; }
        foreach (var sub in subs)
        {
            if (results.Count >= limit || visited > 25000 || sw.ElapsedMilliseconds > 900) return;
            visited++;
            var childPath = path + "\\" + sub;
            if (sub.Contains(query, StringComparison.OrdinalIgnoreCase))
                results.Add(new("database", sub, childPath, () => OpenRegistry(childPath)));
            try { using var child = key.OpenSubKey(sub); if (child != null) SearchRegistryKey(child, childPath, query, results, limit, ref visited, sw, depth + 1); } catch { }
        }
    }

    // Navigate regedit to the specified key: Only set regedit's own LastKey UI state (standard practice; no data modification), then restart regedit
    static void OpenRegistry(string path)
    {
        try { using var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"); k?.SetValue("LastKey", "Computer\\" + path); } catch { }
        try { Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true }); } catch { }
    }

    List<PaletteResult> Calculate(string expression)
    {
        if (!BeeXExpression.TryEvaluate(expression, out var value)) return [new("math-function", L("輸入有效的算式"), L("支援 + − × ÷ 與括號"), () => { })];
        var formatted = value.ToString("G12", CultureInfo.CurrentCulture);
        return [new("math-function", formatted, expression.Trim(), () => Clipboard.SetText(formatted))];
    }

    List<PaletteResult> Filter(List<PaletteResult> values, string query) => string.IsNullOrWhiteSpace(query) ? values : values.Where(x => x.Title.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) || x.Detail.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();

    static bool TryWebAddress(string text, out string url)
    {
        url = text;
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "https://" + text;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        url = uri.AbsoluteUri;
        return true;
    }

    static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    void SetResults(IEnumerable<PaletteResult> values)
    {
        results.ItemsSource = values.Take(30).ToList();
        var any = results.Items.Count > 0;
        results.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        hint.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        if (any) results.SelectedIndex = 0;
    }

    static DataTemplate BuildTemplate()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, WpfOrientation.Horizontal);
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(2, 3, 2, 3));
        row.SetValue(FrameworkElement.MinHeightProperty, 40d);

        // Icon: The name is parsed by IconConverter on the UI thread into an SVG ImageSource (orange hue, looks great across all themes)
        var icon = new FrameworkElementFactory(typeof(Image));
        icon.SetValue(Image.WidthProperty, 22d);
        icon.SetValue(Image.HeightProperty, 22d);
        icon.SetValue(FrameworkElement.MarginProperty, new Thickness(6, 0, 4, 0));
        icon.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        icon.SetValue(RenderOptions.BitmapScalingModeProperty, BitmapScalingMode.HighQuality);
        icon.SetBinding(Image.SourceProperty, new System.Windows.Data.Binding { Converter = IconConverter.Instance });
        row.AppendChild(icon);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        stack.SetValue(FrameworkElement.MaxWidthProperty, 552d);
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PaletteResult.Title)));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.FontSizeProperty, 14d);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PaletteResult.Detail)));
        detail.SetValue(TextBlock.OpacityProperty, .68);
        detail.SetValue(TextBlock.FontSizeProperty, 11.5d);
        detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(title);
        stack.AppendChild(detail);
        row.AppendChild(stack);

        var template = new DataTemplate(typeof(PaletteResult)) { VisualTree = row };
        return template;
    }

    /// <summary>Soft select/hover highlight: A semi-transparent, rounded-corner orange background replaces the system's default glaring blue highlight; the indentation ensures it doesn't overlap the card's rounded corners</summary>
    static Style BuildItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 0, 4, 0)));
        // Custom Template: Background and Border (8-pixel rounded corners), with Hover and Selected triggers
        var tpl = new ControlTemplate(typeof(ListBoxItem));
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "bd" };
        border.SetValue(Border.BackgroundProperty, new System.Windows.Data.Binding { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent), Path = new PropertyPath(Control.BackgroundProperty) });
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.PaddingProperty, new System.Windows.Data.Binding { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent), Path = new PropertyPath(Control.PaddingProperty) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        border.AppendChild(presenter);
        tpl.VisualTree = border;
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(MediaColor.FromArgb(30, 255, 138, 0))));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(Control.BackgroundProperty, new SolidColorBrush(MediaColor.FromArgb(52, 255, 138, 0))));
        tpl.Triggers.Add(hover);
        tpl.Triggers.Add(selected);
        style.Setters.Add(new Setter(Control.TemplateProperty, tpl));
        return style;
    }

    /// <summary>Icon explanation: For applications (where IconPath is not empty), use the actual Shell logo; for all others, use SVG (orange tint); cache and freeze by key; must be called on the UI thread </summary>
    sealed class IconConverter : System.Windows.Data.IValueConverter
    {
        public static readonly IconConverter Instance = new();
        static readonly System.Windows.Media.Brush accent = CreateAccent();
        readonly Dictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);
        static System.Windows.Media.Brush CreateAccent() { var b = new SolidColorBrush(MediaColor.FromRgb(255, 138, 0)); b.Freeze(); return b; }
        public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        {
            if (value is not PaletteResult result) return null;
            var key = string.IsNullOrEmpty(result.IconPath) ? result.Icon : result.IconPath!;
            if (string.IsNullOrEmpty(key)) return null;
            if (cache.TryGetValue(key, out var cached)) return cached;
            ImageSource? image = null;
            if (!string.IsNullOrEmpty(result.IconPath)) image = TryExtractShellIcon(result.IconPath!); // App: Real Logo
            image ??= SvgIcon.Load(result.Icon, 24, accent);                                          // Other/Extraction Failed: Fallback to SVG
            try { if (image.CanFreeze) image.Freeze(); } catch { }
            cache[key] = image;
            return image;
        }
        public object ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();

        // Use Shell to extract the application icon associated with a file (.lnk/.exe) as the actual logo; the .lnk file will be automatically resolved to its target. If this fails, return null, and the caller should fall back to using an SVG.
        static ImageSource? TryExtractShellIcon(string path)
        {
            var info = new SHFILEINFO();
            var hr = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (hr == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally { DestroyIcon(info.hIcon); }
        }
        const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO { public IntPtr hIcon; public int iIcon; public uint dwAttributes; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName; }
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);
    }
}
