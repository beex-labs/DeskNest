using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using TextBox = System.Windows.Controls.TextBox;
using ListBox = System.Windows.Controls.ListBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Brushes = System.Windows.Media.Brushes;
using Clipboard = System.Windows.Clipboard;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using MediaColor = System.Windows.Media.Color;
using WpfOrientation = System.Windows.Controls.Orientation;

namespace BeeX.DeskNest;

public partial class WidgetWindow
{
    Grid? launcherPanel;
    TextBox? launcherInput;
    ListBox? launcherResults;
    TextBlock? launcherHint;
    readonly DispatcherTimer launcherDelay = new() { Interval = TimeSpan.FromMilliseconds(350) };
    int launcherRevision;
    bool launcherQueryDirty;
    double launcherExpandedHeight;
    static readonly Lazy<List<string>> programShortcuts = new(BuildProgramIndex, true);

    sealed record LauncherResult(string Keyword, string Title, string Detail, Action Run)
    {
        public string Display => $"{Keyword}   {Title}\n      {Detail}";
    }

    void BuildLauncherPanel()
    {
        launcherPanel = new Grid { Visibility = Visibility.Collapsed };
        launcherPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        launcherPanel.RowDefinitions.Add(new RowDefinition());
        launcherPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        launcherInput = new TextBox { FontSize = 14, Padding = new Thickness(10, 5, 10, 5), Height = 36, VerticalContentAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, ToolTip = Localization.T("輸入以搜索文件、程式、網頁或計算",service.State.Language), Tag = Localization.T("快速啟動",service.State.Language) };
        launcherInput.TextChanged += (_, _) => { launcherQueryDirty = true; launcherDelay.Stop(); launcherDelay.Start(); };
        launcherInput.PreviewKeyDown += LauncherInputKeyDown;
        launcherInput.GotKeyboardFocus += (_, _) => ShowLauncherOptions(true);
        launcherInput.LostKeyboardFocus += async (_, _) => { await Task.Delay(120); if (launcherPanel?.IsKeyboardFocusWithin != true) ShowLauncherOptions(false); };
        launcherPanel.Children.Add(launcherInput);
        launcherPanel.SizeChanged += (_, _) => UpdateLauncherWidth();

        launcherResults = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, Margin = new Thickness(0, 8, 0, 4), ItemTemplate = BuildLauncherTemplate() };
        launcherResults.MouseDoubleClick += (_, _) => RunLauncherSelection();
        Grid.SetRow(launcherResults, 1);
        launcherPanel.Children.Add(launcherResults);

        launcherHint = new TextBlock { Text = Localization.T("↑↓ 選擇 · Enter 開啟 · Esc 清除",service.State.Language), Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11 };
        Grid.SetRow(launcherHint, 2);
        launcherPanel.Children.Add(launcherHint);
        ContentHost.Children.Add(launcherPanel);
        ApplyLauncherTheme();

        launcherDelay.Tick += async (_, _) =>
        {
            launcherDelay.Stop();
            if (model.Kind != NestKind.Launcher || launcherInput?.IsKeyboardFocusWithin != true || !launcherQueryDirty) return;
            launcherQueryDirty = false;
            await RefreshLauncherAsync();
        };
        SetLauncherResults(GetLauncherHelp());
        launcherExpandedHeight = Math.Max(model.Height, 300);
        ShowLauncherOptions(false);
    }

    void ShowLauncherOptions(bool show)
    {
        if (launcherResults == null || launcherHint == null) return;
        launcherResults.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        launcherHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        launcherPanel!.VerticalAlignment = show ? VerticalAlignment.Stretch : VerticalAlignment.Center;
        if (show && launcherResults.Items.Count == 0) SetLauncherResults(GetLauncherHelp());
        if (model.Kind != NestKind.Launcher) return;
        var wasLoading = loading;
        loading = true;
        try
        {
            if (show) Height = Math.Max(launcherExpandedHeight, 300);
            else
            {
                if (Height > 170) launcherExpandedHeight = Height;
                Height = Math.Max(MinHeight, 150);
            }
        }
        finally { loading = wasLoading; }
    }

    async void LauncherInputKeyDown(object sender, KeyEventArgs e)
    {
        try
        {
            if (launcherResults == null || launcherInput == null) return;
            if (e.Key == Key.Down) { launcherResults.SelectedIndex = Math.Min(launcherResults.Items.Count - 1, launcherResults.SelectedIndex + 1); launcherResults.ScrollIntoView(launcherResults.SelectedItem); e.Handled = true; }
            else if (e.Key == Key.Up) { launcherResults.SelectedIndex = Math.Max(0, launcherResults.SelectedIndex - 1); launcherResults.ScrollIntoView(launcherResults.SelectedItem); e.Handled = true; }
            else if (e.Key == Key.Enter && launcherQueryDirty) { launcherDelay.Stop(); e.Handled = true; launcherQueryDirty = false; await RefreshLauncherAsync(); }
            else if (e.Key == Key.Enter) { RunLauncherSelection(); e.Handled = true; }
            else if (e.Key == Key.Escape && launcherInput.Text.Length > 0) { launcherDelay.Stop(); launcherInput.Clear(); launcherQueryDirty = false;SetLauncherResults(GetLauncherHelp());ShowLauncherOptions(true);e.Handled = true; }
            else if (e.Key == Key.Escape) { ShowLauncherOptions(false); Keyboard.ClearFocus(); e.Handled = true; }
        }
        catch (Exception ex)
        {
            SetLauncherResults([new("!", L("搜尋失敗"), ex is UnauthorizedAccessException ? L("部分程式目錄無法存取") : L("請重新輸入後再試"), () => { })]);
            launcherQueryDirty = false;
            e.Handled = true;
        }
    }

    void UpdateLauncherWidth()
    {
        if (launcherPanel == null || launcherInput == null || launcherResults == null) return;
        var available = Math.Max(220, launcherPanel.ActualWidth - 34);
        var width = Math.Min(440, available);
        launcherInput.Width = width;
        launcherResults.Width = width;
        launcherResults.HorizontalAlignment = HorizontalAlignment.Center;
    }

    void RunLauncherSelection()
    {
        if (launcherResults?.SelectedItem is not LauncherResult result) return;
        model.Content = result.Title;
        service.Save();
        try { result.Run(); } catch { }
    }

    async Task RefreshLauncherAsync()
    {
        try
        {
            if (launcherInput == null) return;
            var revision = ++launcherRevision;
            var text = launcherInput.Text.Trim();
            if (text.Length == 0) { SetLauncherResults(GetLauncherHelp()); return; }
            ShowLauncherOptions(true);
            List<LauncherResult> results;
            if (text.StartsWith("=")) results = Calculate(text[1..]);
            else if (text.StartsWith("!!")) results = PreviousResult();
            else if (text.StartsWith("@")) results = BeeXCommands(text[1..]);
            else if (text.StartsWith("?")) results = await Task.Run(() => SearchFiles(text[1..]));
            else if (text.StartsWith(".")) results = await Task.Run(() => SearchPrograms(text[1..]));
            else if (text.StartsWith("/")) results = WebSearch(text[1..]);
            else if (text.StartsWith("~")) results = CommonFolders(text[1..]);
            else results = DirectInput(text);
            if (revision == launcherRevision) SetLauncherResults(results.Count == 0 ? [new("·", L("沒有找到結果"), L("請嘗試其他關鍵字"), () => { })] : results);
        }
        catch
        {
            SetLauncherResults([new("!", L("搜尋失敗"), L("搜尋已安全停止，BeeX 會繼續運行"), () => { })]);
        }
    }

    void SetLauncherResults(IEnumerable<LauncherResult> values)
    {
        if (launcherResults == null) return;
        launcherResults.ItemsSource = values.Take(30).ToList();
        if (launcherResults.Items.Count > 0) launcherResults.SelectedIndex = 0;
        ApplyLauncherTheme();
    }

    void ApplyLauncherTheme()
    {
        if (launcherInput == null || launcherResults == null || launcherHint == null) return;
        var foreground = Foreground;
        var darkSurface = ContrastHelper.TextFor(RootBorder.Background) is SolidColorBrush brush && brush.Color == Colors.White;
        var inputBackground = new SolidColorBrush(darkSurface ? MediaColor.FromArgb(86, 255, 255, 255) : MediaColor.FromArgb(130, 255, 255, 255));
        launcherInput.Background = inputBackground;
        launcherInput.Foreground = ContrastHelper.TextFor(inputBackground, foreground);
        launcherInput.CaretBrush = launcherInput.Foreground;
        launcherResults.Foreground = foreground;
        launcherHint.Foreground = new SolidColorBrush(darkSurface ? MediaColor.FromRgb(198, 205, 218) : MediaColor.FromRgb(102, 112, 133));
        launcherInput.ToolTip = L("輸入以搜索文件、程式、網頁或計算");
        launcherHint.Text = L("↑↓ 選擇 · Enter 開啟 · Esc 清除");
    }

    static DataTemplate BuildLauncherTemplate()
    {
        var template = new DataTemplate(typeof(LauncherResult));
        template.VisualTree = BuildLauncherTemplateRoot();
        return template;
    }

    static FrameworkElementFactory BuildLauncherTemplateRoot()
    {
        var row = new FrameworkElementFactory(typeof(StackPanel));
        row.SetValue(StackPanel.OrientationProperty, WpfOrientation.Horizontal);
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 5, 4, 5));
        row.SetValue(FrameworkElement.MinHeightProperty, 48d);

        var badge = new FrameworkElementFactory(typeof(Border));
        badge.SetValue(Border.WidthProperty, 34d);
        badge.SetValue(Border.HeightProperty, 34d);
        badge.SetValue(Border.CornerRadiusProperty, new CornerRadius(9));
        badge.SetValue(Border.BackgroundProperty, new SolidColorBrush(MediaColor.FromRgb(255, 138, 0)));
        badge.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        badge.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
        var keyword = new FrameworkElementFactory(typeof(TextBlock));
        keyword.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(LauncherResult.Keyword)));
        keyword.SetValue(TextBlock.ForegroundProperty, Brushes.White);
        keyword.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        keyword.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        keyword.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
        keyword.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        badge.AppendChild(keyword);
        row.AppendChild(badge);

        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(StackPanel.VerticalAlignmentProperty, VerticalAlignment.Center);
        stack.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        var title = new FrameworkElementFactory(typeof(TextBlock));
        title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(LauncherResult.Title)));
        title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
        title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(LauncherResult.Detail)));
        detail.SetValue(TextBlock.OpacityProperty, .72);
        detail.SetValue(TextBlock.FontSizeProperty, 12d);
        detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        stack.AppendChild(title);
        stack.AppendChild(detail);
        row.AppendChild(stack);
        return row;
    }

    List<LauncherResult> GetLauncherHelp() =>
    [
        new("=", L("計算數學公式"), L("例如 = 5*3-2"), () => launcherInput!.Text = "= "),
        new("!!", L("訪問上次選擇的結果"), string.IsNullOrWhiteSpace(model.Content) ? L("尚無歷史結果") : model.Content, () => launcherInput!.Text = "!!"),
        new("?", L("搜尋文件和文件夾"), L("搜尋桌面、文件與下載"), () => launcherInput!.Text = "? "),
        new("@", L("開啟 BeeX 功能與設定"), L("設定、截圖、顯示或隱藏全部"), () => launcherInput!.Text = "@ "),
        new(".", L("搜尋程式"), L("搜尋開始功能表中的應用程式"), () => launcherInput!.Text = ". "),
        new("/", L("搜尋網頁"), L("使用預設瀏覽器搜尋"), () => launcherInput!.Text = "/ "),
        new("~", L("常用文件夾"), L("桌面、下載、文件、圖片與音樂"), () => launcherInput!.Text = "~ "),
        new("↗", L("直接開啟網址或路徑"), L("貼上網址、文件或文件夾路徑"), () => { })
    ];

    List<LauncherResult> PreviousResult()
    {
        if (string.IsNullOrWhiteSpace(model.Content)) return [new("!!", L("尚無歷史結果"), L("執行任一結果後會保存在這裡"), () => { })];
        var previous = model.Content;
        return [new("!!", previous, L("再次搜尋或開啟"), () => { if (File.Exists(previous) || Directory.Exists(previous)) OpenPath(previous); else launcherInput!.Text = previous; })];
    }

    List<LauncherResult> BeeXCommands(string query)
    {
        var commands = new List<LauncherResult>
        {
            new("@", "BeeX "+L("設定"), L("調整外觀、快捷鍵與功能格子"), service.ShowSettings),
            new("@", L("立即截圖"), L("開始區域截圖"), () => service.CaptureScreen()),
            new("@", L("截圖文件夾"), L("開啟 BeeX 截圖保存位置"), service.OpenCaptureFolder),
            new("@", L("顯示 / 隱藏全部"), L("切換全部未鎖定格子"), service.ToggleAll),
            new("@", L("主控制台"), L("開啟 BeeX DeskNest 控制台"), service.ShowControl),
            new("@", L("系統清理"), L("卸載程式並清理殘留"), service.ShowCleaner),
            new("@", L("新增便箋"), L("建立新的靈感便箋格子"), () => service.Add(NestKind.Note)),
            new("@", L("新增待辦"), L("建立新的待辦格子"), () => service.Add(NestKind.Todo)),
            new("@", L("新增倒數日"), L("建立新的倒數日格子"), () => service.Add(NestKind.Countdown)),
            new("@", L("新增天氣"), L("建立新的天氣格子"), () => service.Add(NestKind.Weather)),
            new("@", L("新增音樂控制"), L("建立新的播放器格子"), () => service.Add(NestKind.Music))
        };
        return Filter(commands, query);
    }

    List<LauncherResult> SearchFiles(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return [new("?", L("請輸入文件名稱"), "例如 ? report", () => { })];
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") }.Distinct().Where(Directory.Exists);
        var found = new List<string>();
        foreach (var root in roots) SafeFind(root, query, found, 30, 4);
        return found.Select(path => new LauncherResult("?", Path.GetFileName(path), path, () => OpenPath(path))).ToList();
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

    List<LauncherResult> SearchPrograms(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return [new(".", L("請輸入程式名稱"), "例如 . paint", () => { })];
        return programShortcuts.Value
            .Where(path => Path.GetFileNameWithoutExtension(path).Contains(query, StringComparison.CurrentCultureIgnoreCase)).Take(30)
            .Select(path => new LauncherResult(".", Path.GetFileNameWithoutExtension(path), L("應用程式"), () => OpenPath(path))).ToList();
    }

    static List<string> BuildProgramIndex()
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu) };
        var shortcuts = new List<string>();
        foreach (var root in roots.Where(Directory.Exists)) SafeFindPrograms(root, "", shortcuts, 5000, 12);
        return shortcuts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    static void SafeFindPrograms(string folder, string query, List<string> found, int limit, int depth)
    {
        if (found.Count >= limit || depth < 0) return;
        try
        {
            foreach (var file in Directory.GetFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileNameWithoutExtension(file).Contains(query, StringComparison.CurrentCultureIgnoreCase)) found.Add(file);
                if (found.Count >= limit) return;
            }
        }
        catch { }
        if (depth == 0) return;
        string[] directories;
        try { directories = Directory.GetDirectories(folder); }
        catch { return; }
        foreach (var directory in directories)
        {
            try { if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) continue; }
            catch { continue; }
            SafeFindPrograms(directory, query, found, limit, depth - 1);
            if (found.Count >= limit) return;
        }
    }

    List<LauncherResult> DirectInput(string text)
    {
        var raw = text.Trim().Trim('"');
        var expanded = Environment.ExpandEnvironmentVariables(raw);
        if (File.Exists(expanded) || Directory.Exists(expanded))
            return [new("↗", Path.GetFileName(expanded.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } name ? name : expanded, expanded, () => OpenPath(expanded))];
        if (TryWebAddress(raw, out var url))
            return [new("↗", L("開啟網頁"), url, () => OpenPath(url))];
        var matches = GetLauncherStaticHelp().Where(x => x.Title.Contains(raw, StringComparison.CurrentCultureIgnoreCase) || x.Detail.Contains(raw, StringComparison.CurrentCultureIgnoreCase)).ToList();
        matches.Add(new("/", $"{L("搜尋")}「{raw}」", L("使用預設瀏覽器搜尋網頁"), () => OpenPath("https://www.bing.com/search?q=" + Uri.EscapeDataString(raw))));
        return matches;
    }

    static bool TryWebAddress(string text, out string url)
    {
        url = text;
        if (text.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) url = "https://" + text;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;
        url = uri.AbsoluteUri;
        return true;
    }

    List<LauncherResult> WebSearch(string query)
    {
        query = query.Trim();
        if (query.Length == 0) return [new("/", L("請輸入搜尋內容"), "例如 / BeeX DeskNest", () => { })];
        var value = query;
        return [new("/", $"{L("搜尋")}「{value}」", L("使用預設瀏覽器"), () => OpenPath("https://www.bing.com/search?q=" + Uri.EscapeDataString(value)))];
    }

    List<LauncherResult> CommonFolders(string query)
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
        return folders.Where(x => Directory.Exists(x.Item2) && (string.IsNullOrWhiteSpace(query) || x.Item1.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)))
            .Select(x => new LauncherResult("~", x.Item1, x.Item2, () => OpenPath(x.Item2))).ToList();
    }

    List<LauncherResult> GetLauncherStaticHelp() =>
    [
        new("=", L("計算數學公式"), L("使用 = 開始"), () => { }), new("?", L("搜尋文件和文件夾"), L("使用 ? 開始"), () => { }),
        new("@", L("BeeX 功能與設定"), L("使用 @ 開始"), () => { }), new(".", L("搜尋程式"), L("使用 . 開始"), () => { }),
        new("/", L("搜尋網頁"), L("使用 / 開始"), () => { }), new("~", L("常用文件夾"), L("使用 ~ 開始"), () => { })
    ];

    List<LauncherResult> Filter(List<LauncherResult> values, string query) => string.IsNullOrWhiteSpace(query) ? values : values.Where(x => x.Title.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase) || x.Detail.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
    static void OpenPath(string path) => Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    List<LauncherResult> Calculate(string expression)
    {
        if (!BeeXExpression.TryEvaluate(expression, out var value)) return [new("=", L("輸入有效的算式"), L("支援 + − × ÷ 與括號"), () => { })];
        var result = value.ToString("G12", CultureInfo.CurrentCulture);
        return [new("=", result, expression.Trim(), () => Clipboard.SetText(result))];
    }
}
