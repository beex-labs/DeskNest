using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Button = System.Windows.Controls.Button;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using ComboBoxItem = System.Windows.Controls.ComboBoxItem;
using TextBlock = System.Windows.Controls.TextBlock;
using StackPanel = System.Windows.Controls.StackPanel;
using WrapPanel = System.Windows.Controls.WrapPanel;
using ScrollViewer = System.Windows.Controls.ScrollViewer;
using ScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility;
using DockPanel = System.Windows.Controls.DockPanel;
using Dock = System.Windows.Controls.Dock;
using Grid = System.Windows.Controls.Grid;
using ColumnDefinition = System.Windows.Controls.ColumnDefinition;
using Border = System.Windows.Controls.Border;
using Orientation = System.Windows.Controls.Orientation;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using ImageSource = System.Windows.Media.ImageSource;
using Stretch = System.Windows.Media.Stretch;
using Cursors = System.Windows.Input.Cursors;
using MouseButtonState = System.Windows.Input.MouseButtonState;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using Slider = System.Windows.Controls.Slider;

namespace BeeX.DeskNest;

/// <summary>
/// The wallpaper gallery: imports video/image wallpapers into the library, shows them as thumbnails, assigns one per
/// monitor, and toggles the engine on or off. Every change is persisted and applied to the running engine immediately.
/// </summary>
public sealed class WallpaperGalleryWindow : Window
{
    readonly DeskNestService service;
    AppState State => service.State;
    string L(string zhTw, string zhCn, string en) => State.Language == "zh-CN" ? zhCn : State.Language == "en-US" ? en : zhTw;

    readonly bool dark, honey;
    readonly Brush surface, foreground, cardBrush, accent;
    readonly WrapPanel libraryPanel = new() { Margin = new Thickness(0, 6, 0, 0) };
    readonly StackPanel monitorPanel = new();
    CheckBox enableToggle = null!;

    static readonly string[] VideoExt = { ".mp4", ".webm", ".mkv", ".mov", ".avi", ".m4v" };
    static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };

    public WallpaperGalleryWindow(DeskNestService service)
    {
        this.service = service;
        dark = State.Theme == "Dark"; honey = State.Theme == "Honey";
        surface = new SolidColorBrush(dark ? Color.FromRgb(13, 19, 33) : honey ? Color.FromRgb(255, 244, 222) : Color.FromRgb(245, 247, 250));
        foreground = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(13, 19, 33));
        cardBrush = new SolidColorBrush(Color.FromArgb(dark ? (byte)40 : (byte)235, dark ? (byte)255 : (byte)255, dark ? (byte)255 : (byte)255, dark ? (byte)255 : (byte)255));
        accent = new SolidColorBrush(Color.FromRgb(255, 138, 0));
        BuildUi();
        RefreshLibrary();
        RefreshMonitors();
    }

    void BuildUi()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Width = 760; Height = 580;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = L("桌面壁紙", "桌面壁纸", "Live wallpaper");

        var body = new DockPanel { LastChildFill = true };

        var header = new Grid { Height = 48, Margin = new Thickness(16, 4, 8, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) DragMove(); };
        var title = new TextBlock { Text = L("桌面壁紙", "桌面壁纸", "Live wallpaper"), FontSize = 16, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = foreground };
        header.Children.Add(title);
        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        enableToggle = new CheckBox { Content = L("啟用", "启用", "Enabled"), IsChecked = State.WallpaperEnabled, VerticalAlignment = VerticalAlignment.Center, Foreground = foreground, Margin = new Thickness(0, 0, 12, 0) };
        enableToggle.Checked += (_, _) => ToggleEnabled();
        enableToggle.Unchecked += (_, _) => ToggleEnabled();
        var close = new Button { Content = "×", Width = 36, Height = 36, FontSize = 16, Background = Brushes.Transparent, Foreground = foreground, BorderThickness = new Thickness(0), Cursor = Cursors.Hand };
        close.Click += (_, _) => Close();
        headerActions.Children.Add(enableToggle);
        headerActions.Children.Add(close);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);
        DockPanel.SetDock(header, Dock.Top);
        body.Children.Add(header);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(16, 8, 16, 16) };
        var content = new StackPanel();
        content.Children.Add(SectionHeader(L("顯示器", "显示器", "Monitors")));
        content.Children.Add(monitorPanel);

        var libRow = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        libRow.ColumnDefinitions.Add(new ColumnDefinition());
        libRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        libRow.Children.Add(SectionHeader(L("壁紙庫", "壁纸库", "Library")));
        var import = MakeButton(L("＋ 導入壁紙", "＋ 导入壁纸", "＋ Import"));
        import.Click += (_, _) => Import();
        Grid.SetColumn(import, 1);
        libRow.Children.Add(import);
        content.Children.Add(libRow);
        content.Children.Add(libraryPanel);

        content.Children.Add(new Border { Height = 14 });
        content.Children.Add(SectionHeader(L("引擎設定", "引擎设置", "Engine")));
        content.Children.Add(ToggleRow(L("被完全遮擋時暫停渲染", "被完全遮挡时暂停渲染", "Pause when fully covered"), State.WallpaperPauseWhenOccluded, v => { State.WallpaperPauseWhenOccluded = v; SaveOnly(); }));
        content.Children.Add(ToggleRow(L("使用電池時暫停", "使用电池时暂停", "Pause on battery"), State.WallpaperPauseOnBattery, v => { State.WallpaperPauseOnBattery = v; SaveOnly(); }));
        content.Children.Add(ToggleRow(L("全螢幕應用時靜音", "全屏应用时静音", "Mute during fullscreen apps"), State.WallpaperMuteOnFullscreen, v => { State.WallpaperMuteOnFullscreen = v; SaveOnly(); }));
        content.Children.Add(ToggleRow(L("允許音頻響應", "允许音频响应", "Allow audio reaction"), State.WallpaperAudioReactive, v => { State.WallpaperAudioReactive = v; SaveOnly(); }));
        content.Children.Add(SliderRow(L("幀率上限", "帧率上限", "Frame-rate cap"), 10, 240, State.WallpaperFpsCap, " fps", v => { State.WallpaperFpsCap = (int)v; SaveOnly(); }));
        content.Children.Add(SliderRow(L("音量", "音量", "Volume"), 0, 100, State.WallpaperGlobalVolume * 100, " %", v => { State.WallpaperGlobalVolume = v / 100; SaveOnly(); }));

        scroll.Content = content;
        body.Children.Add(scroll);

        Content = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = surface,
            BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 138, 0)),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = body
        };
    }

    TextBlock SectionHeader(string text) => new() { Text = text, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = foreground, Margin = new Thickness(0, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center };

    Button MakeButton(string text) => new()
    {
        Content = text,
        Foreground = Brushes.White,
        Background = accent,
        BorderThickness = new Thickness(0),
        Padding = new Thickness(12, 6, 12, 6),
        Cursor = Cursors.Hand,
        VerticalAlignment = VerticalAlignment.Center
    };

    void ToggleEnabled()
    {
        State.WallpaperEnabled = enableToggle.IsChecked == true;
        ApplyAndSave();
    }

    void RefreshMonitors()
    {
        monitorPanel.Children.Clear();
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
            var label = new TextBlock
            {
                Text = (screen.Primary ? L("主螢幕", "主屏幕", "Primary") : L("擴展屏", "扩展屏", "Extended")) + $"  {screen.Bounds.Width}×{screen.Bounds.Height}",
                Width = 220,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center
            };
            var combo = new ComboBox { Width = 240, Foreground = foreground };
            combo.Items.Add(new ComboBoxItem { Content = L("無", "无", "None"), Tag = Guid.Empty });
            foreach (var w in State.WallpaperLibrary) combo.Items.Add(new ComboBoxItem { Content = w.Name, Tag = w.Id });
            var currentId = State.WallpaperPerMonitor.TryGetValue(screen.DeviceName, out var g) ? g : Guid.Empty;
            combo.SelectedIndex = 0;
            for (var i = 0; i < combo.Items.Count; i++)
                if (combo.Items[i] is ComboBoxItem ci && ci.Tag is Guid id && id == currentId) { combo.SelectedIndex = i; break; }
            var device = screen.DeviceName;
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem ci && ci.Tag is Guid id) AssignMonitor(device, id); };
            row.Children.Add(label);
            row.Children.Add(combo);
            monitorPanel.Children.Add(row);
        }
    }

    void RefreshLibrary()
    {
        libraryPanel.Children.Clear();
        if (State.WallpaperLibrary.Count == 0)
        {
            libraryPanel.Children.Add(new TextBlock { Text = L("尚無壁紙，點擊「導入壁紙」添加。", "尚无壁纸，点击“导入壁纸”添加。", "No wallpapers yet. Click Import to add one."), Foreground = foreground, Opacity = 0.7, Margin = new Thickness(2, 8, 2, 8) });
            return;
        }
        foreach (var item in State.WallpaperLibrary)
            libraryPanel.Children.Add(BuildCard(item));
    }

    Border BuildCard(WallpaperItem item)
    {
        var stack = new StackPanel();
        var thumb = new System.Windows.Controls.Image { Width = 150, Height = 84, Stretch = Stretch.UniformToFill };
        var source = LoadThumb(item);
        if (source != null) thumb.Source = source;
        var thumbHost = new Border { Width = 150, Height = 84, CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), ClipToBounds = true, Child = thumb };
        stack.Children.Add(thumbHost);
        stack.Children.Add(new TextBlock { Text = item.Name, Foreground = foreground, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150, Margin = new Thickness(2, 4, 2, 0) });
        var kindText = item.Kind == WallpaperKind.Video ? L("影片", "视频", "Video") : item.Kind == WallpaperKind.Image ? L("圖片", "图片", "Image") : L("網頁", "网页", "Web");
        var footer = new Grid();
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.Children.Add(new TextBlock { Text = kindText, Foreground = foreground, Opacity = 0.6, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        var del = new Button { Content = L("移除", "移除", "Remove"), FontSize = 11, Background = Brushes.Transparent, Foreground = accent, BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Padding = new Thickness(2, 0, 2, 0) };
        del.Click += (_, _) => Delete(item);
        Grid.SetColumn(del, 1);
        footer.Children.Add(del);
        stack.Children.Add(footer);
        return new Border { Width = 150, Margin = new Thickness(0, 0, 12, 12), Padding = new Thickness(8), CornerRadius = new CornerRadius(10), Background = cardBrush, Child = stack };
    }

    ImageSource? LoadThumb(WallpaperItem item)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(item.Thumb) ? (item.Kind == WallpaperKind.Image ? item.Path : "") : item.Thumb;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 300;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    void Import()
    {
        var filter = L("壁紙檔案", "壁纸文件", "Wallpaper files") + "|*.mp4;*.webm;*.mkv;*.mov;*.avi;*.m4v;*.jpg;*.jpeg;*.png;*.gif;*.bmp;*.webp";
        var dialog = new OpenFileDialog { Multiselect = true, Filter = filter };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var src in dialog.FileNames) ImportOne(src);
        service.Save();
        RefreshLibrary();
        RefreshMonitors();
    }

    void ImportOne(string src)
    {
        try
        {
            var ext = Path.GetExtension(src).ToLowerInvariant();
            var kind = Array.IndexOf(ImageExt, ext) >= 0 ? WallpaperKind.Image : WallpaperKind.Video;
            var item = new WallpaperItem { Kind = kind, Name = Path.GetFileNameWithoutExtension(src), PlaybackRate = 1 };
            var dir = Path.Combine(BeeXPaths.WallpapersDir, item.Id.ToString("N"));
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, Path.GetFileName(src));
            File.Copy(src, dest, true);
            item.Path = dest;
            item.Thumb = MakeThumb(item, dir);
            State.WallpaperLibrary.Add(item);
        }
        catch { }
    }

    static string MakeThumb(WallpaperItem item, string dir)
    {
        try
        {
            if (item.Kind == WallpaperKind.Image) return item.Path;
            var thumbs = FfmpegService.ExtractThumbs(item.Path, 0.2, 0.4, 1, 360, dir, "thumb");
            return thumbs.Count > 0 ? thumbs[0] : "";
        }
        catch { return ""; }
    }

    void Delete(WallpaperItem item)
    {
        State.WallpaperLibrary.Remove(item);
        foreach (var key in State.WallpaperPerMonitor.Where(kv => kv.Value == item.Id).Select(kv => kv.Key).ToList())
            State.WallpaperPerMonitor.Remove(key);
        try
        {
            var dir = Path.Combine(BeeXPaths.WallpapersDir, item.Id.ToString("N"));
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
        ApplyAndSave();
        RefreshLibrary();
        RefreshMonitors();
    }

    void AssignMonitor(string device, Guid id)
    {
        if (id == Guid.Empty) State.WallpaperPerMonitor.Remove(device);
        else State.WallpaperPerMonitor[device] = id;
        ApplyAndSave();
    }

    void ApplyAndSave()
    {
        service.Save();
        service.Wallpaper?.ApplyPreferences();
    }

    // Persists a live-read setting (frame rate, pause rules, volume) without rebuilding surfaces; the governor picks it up on its next tick.
    void SaveOnly() => service.Save();

    CheckBox ToggleRow(string label, bool value, Action<bool> onChange)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Foreground = foreground, Margin = new Thickness(0, 4, 0, 4) };
        box.Checked += (_, _) => onChange(true);
        box.Unchecked += (_, _) => onChange(false);
        return box;
    }

    FrameworkElement SliderRow(string label, double min, double max, double initial, string suffix, Action<double> onChange)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var caption = new TextBlock { Text = label, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(initial, min, max), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
        var value = new TextBlock { Foreground = foreground, VerticalAlignment = VerticalAlignment.Center, MinWidth = 46, Text = FormatValue(slider.Value, suffix) };
        slider.ValueChanged += (_, e) => { value.Text = FormatValue(e.NewValue, suffix); onChange(e.NewValue); };
        Grid.SetColumn(slider, 1);
        Grid.SetColumn(value, 2);
        row.Children.Add(caption);
        row.Children.Add(slider);
        row.Children.Add(value);
        return row;
    }

    static string FormatValue(double value, string suffix) => $"{(int)Math.Round(value)}{suffix}";
}
