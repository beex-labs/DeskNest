using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Web.WebView2.Core;
using BeexWrite.Services;
using BeexWrite.ViewModels;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using ListBox = System.Windows.Controls.ListBox;
using MessageBox = System.Windows.MessageBox;

namespace BeexWrite;

/// <summary>
/// Embedded BeexWrite editor window used by the DeskNest capture widget:
/// a single-document Markdown editor (no file tree) that follows the host
/// theme and suspends the host's global hotkeys while focused.
/// </summary>
public partial class MarkdownNoteWindow : Window
{
    private const string VirtualHost = "appassets.beexwrite";

    private readonly MainViewModel _vm;
    private readonly EditorBridge _bridge;
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;

    private static bool _localesEnsured;
    private bool _hotkeysSuspended;

    /// <summary>Raised after the document has been written to disk (save or auto-save).</summary>
    public event Action<string>? DocumentSaved;

    /// <summary>The note file this window edits.</summary>
    public string NoteFilePath { get; }

    public MarkdownNoteWindow(string filePath)
    {
        NoteFilePath = filePath;

        _settings = new SettingsService();
        _settings.Load();

        var shortcuts = new ShortcutsService(_settings.SettingsDirectory);
        shortcuts.Load();

        if (!_localesEnsured)
        {
            Localization.Strings.EnsureDefaultLocales(_settings.SettingsDirectory);
            _localesEnsured = true;
        }
        // Language follows the DeskNest host setting.
        var locale = WriteHost.HostLocale?.Invoke() ?? "en";
        Localization.Strings.Instance.LoadLocale(_settings.SettingsDirectory, locale);

        _theme = new ThemeService();
        _bridge = new EditorBridge();
        var files = new FileService();
        var export = new ExportService();

        _vm = new MainViewModel(_bridge, files, _settings, _theme, export, shortcuts)
        {
            PendingFile = filePath
        };
        _vm.DocumentSaved += path => DocumentSaved?.Invoke(path);
        DataContext = _vm;

        InitializeComponent();
        _vm.HostWindow = this;
        ThemeService.Attach(this);
        _theme.Apply("host");

        // Prevent white flash in dark mode: before WebView2 renders its first frame it shows DefaultBackgroundColor (pure white by default),
        // and the page only switches to dark after the setTheme message -- so preset the backdrop by theme, and show the
        // editor area only after the editor is ready and the theme is applied (before that, the window's own Bx backdrop shows).
        ApplyWebViewBackdrop();
        Web.Visibility = Visibility.Hidden;
        _bridge.Ready += async (_, _) =>
        {
            await System.Threading.Tasks.Task.Delay(60); // give the page one frame to apply theme styles
            await Dispatcher.InvokeAsync(() => Web.Visibility = Visibility.Visible);
        };

        // Seed the (hidden) file tree with the notes folder so sidebar search
        // and quick-open work across all capture notes.
        try
        {
            if (Directory.Exists(WriteHost.NotesDirectory))
                _vm.FileTree.Add(new FileNode(WriteHost.NotesDirectory, true, showNonMarkdown: false));
        }
        catch { }

        try
        {
            Icon = new BitmapImage(new Uri("pack://application:,,,/Write/Assets/branding/BeeX.ico"));
        }
        catch
        {
            // Non-fatal if the icon fails to load.
        }

        var s = _settings.Settings;
        Width = Math.Max(MinWidth, Math.Min(s.WindowWidth, SystemParameters.WorkArea.Width));
        Height = Math.Max(MinHeight, Math.Min(s.WindowHeight, SystemParameters.WorkArea.Height));
        if (s.WindowMaximized) WindowState = WindowState.Maximized;

        ApplySidebarState(_vm.SidebarVisible);
        _vm.PropertyChanged += OnViewModelPropertyChanged;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        // Title bar renders at a physical 65px regardless of DPI scaling (suite-wide rule).
        Loaded += (_, _) => ApplyTitleBarPhysicalHeight();
        DpiChanged += (_, _) => ApplyTitleBarPhysicalHeight();
        Closing += OnClosing;
        Closed += (_, _) => ResumeHostHotkeys();
        StateChanged += (_, _) => UpdateMaxButtonGlyph();

        // While the editor is focused, DeskNest's global hotkeys must not fire.
        Activated += (_, _) => SuspendHostHotkeys();
        Deactivated += (_, _) => ResumeHostHotkeys();
    }

    private void SuspendHostHotkeys()
    {
        if (_hotkeysSuspended) return;
        _hotkeysSuspended = true;
        try { WriteHost.SuspendHostHotkeys?.Invoke(); } catch { }
    }

    private void ResumeHostHotkeys()
    {
        if (!_hotkeysSuspended) return;
        _hotkeysSuspended = false;
        try { WriteHost.ResumeHostHotkeys?.Invoke(); } catch { }
    }

    /// <summary>Called by the host when the DeskNest theme changes.</summary>
    public void RefreshHostTheme()
    {
        _theme.Apply("host");
        ApplyWebViewBackdrop();
        if (_bridge.IsAttached) _bridge.SetTheme(_theme.EffectiveTheme);
    }

    /// <summary>Keeps the WebView2 pre-render surface in sync with the Bx palette.</summary>
    private void ApplyWebViewBackdrop()
    {
        Web.DefaultBackgroundColor = _theme.EffectiveTheme == "dark"
            ? System.Drawing.Color.FromArgb(0x1E, 0x1F, 0x22)   // Bx.WindowBackground (Dark)
            : System.Drawing.Color.White;
    }

    // ---- WM_GETMINMAXINFO: constrain maximized window to work area (respects taskbar) ----

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WindowProc);
    }

    /// <summary>Converts the suite-wide 65 physical px title bar into DIP for the current monitor (visual + drag caption zone).</summary>
    private void ApplyTitleBarPhysicalHeight()
    {
        var dip = BeeX.DeskNest.TitleBarMetrics.Dip(this);
        TitleBarGrid.Height = dip;
        if (WindowChrome.GetWindowChrome(this) is { } chrome) chrome.CaptionHeight = dip;
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // WM_GETMINMAXINFO = 0x0024
        if (msg == 0x0024)
        {
            WmGetMinMaxInfo(hwnd, lParam);
            handled = true;
        }
        return IntPtr.Zero;
    }

    private static void WmGetMinMaxInfo(IntPtr hwnd, IntPtr lParam)
    {
        var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref mi))
            return;

        // Constrain the maximized window position & size to the work area (excludes taskbar).
        mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
        mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
        mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
        mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;

        Marshal.StructureToPtr(mmi, lParam, true);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await InitializeWebViewAsync();
    }

    private async System.Threading.Tasks.Task InitializeWebViewAsync()
    {
        try
        {
            var userData = Path.Combine(WriteHost.DataDirectory, "WebView2");
            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(null, userData);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            // Editor bundle is embedded in the exe; extract (once per build) to a real
            // folder — the Chromium virtual host can only serve files from disk.
            var wwwroot = await System.Threading.Tasks.Task.Run(WriteHost.EnsureWebAssets);
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost, wwwroot, CoreWebView2HostResourceAccessKind.Allow);

            core.Settings.AreBrowserAcceleratorKeysEnabled = true; // allow F12 for DevTools
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDevToolsEnabled = true; // always enabled for diagnostics (F12)

            // Auto-grant clipboard read so "Paste as Plain Text" (navigator.clipboard.readText) works.
            core.PermissionRequested += (_, args) =>
            {
                if (args.PermissionKind == CoreWebView2PermissionKind.ClipboardRead)
                    args.State = CoreWebView2PermissionState.Allow;
            };

            _bridge.Attach(core);

            // Wire host-side print (Ctrl+Shift+P from editor).
            _vm.PrintHandler = () => Dispatcher.Invoke(() => OnPrint(this, new RoutedEventArgs()));

            // Long-image export: navigate to rendered HTML → DevTools full-page screenshot → navigate back.
            _vm.LongImageExportHandler = async (htmlPath, outputPath) =>
            {
                var tcs = new System.Threading.Tasks.TaskCompletionSource();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                cts.Token.Register(() => tcs.TrySetCanceled());
                void onNav(object? s, CoreWebView2NavigationCompletedEventArgs e) { core.NavigationCompleted -= onNav; tcs.TrySetResult(); }
                core.NavigationCompleted += onNav;
                core.Navigate(new Uri(htmlPath).AbsoluteUri);
                try
                {
                    await tcs.Task;
                    await System.Threading.Tasks.Task.Delay(500); // let images/diagrams render

                    // DevTools Protocol full-page screenshot (captureBeyondViewport).
                    var resultJson = await core.CallDevToolsProtocolMethodAsync(
                        "Page.captureScreenshot",
                        "{\"format\":\"png\",\"captureBeyondViewport\":true}");
                    using var doc = System.Text.Json.JsonDocument.Parse(resultJson);
                    if (doc.RootElement.TryGetProperty("data", out var dataProp))
                    {
                        var bytes = Convert.FromBase64String(dataProp.GetString() ?? "");
                        await File.WriteAllBytesAsync(outputPath, bytes);
                    }
                }
                finally
                {
                    // Always navigate back to the editor, even on timeout/cancellation.
                    core.NavigationCompleted -= onNav;
                    var tcs2 = new System.Threading.Tasks.TaskCompletionSource();
                    void onNav2(object? s, CoreWebView2NavigationCompletedEventArgs e) { core.NavigationCompleted -= onNav2; tcs2.TrySetResult(); }
                    core.NavigationCompleted += onNav2;
                    core.Navigate($"https://{VirtualHost}/index.html");
                    await tcs2.Task;
                }
            };

            // PDF export: navigate to rendered HTML → PrintToPdfAsync → navigate back.
            _vm.PdfExportHandler = async (htmlPath, outputPath) =>
            {
                var tcs = new System.Threading.Tasks.TaskCompletionSource();
                void onNav(object? s, CoreWebView2NavigationCompletedEventArgs e) { core.NavigationCompleted -= onNav; tcs.TrySetResult(); }
                core.NavigationCompleted += onNav;
                core.Navigate(new Uri(htmlPath).AbsoluteUri);
                await tcs.Task;
                await System.Threading.Tasks.Task.Delay(400);
                await core.PrintToPdfAsync(outputPath);
                var tcs2 = new System.Threading.Tasks.TaskCompletionSource();
                void onNav2(object? s, CoreWebView2NavigationCompletedEventArgs e) { core.NavigationCompleted -= onNav2; tcs2.TrySetResult(); }
                core.NavigationCompleted += onNav2;
                core.Navigate($"https://{VirtualHost}/index.html");
                await tcs2.Task;
            };

            core.Navigate($"https://{VirtualHost}/index.html");
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Failed to initialise the editor (WebView2).\n\n" + ex.Message +
                "\n\nMake sure the WebView2 Runtime is installed.",
                "BeexWrite", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SidebarVisible))
            ApplySidebarState(_vm.SidebarVisible);
        if (e.PropertyName == nameof(MainViewModel.IsFullScreen))
            ApplyFullScreen(_vm.IsFullScreen);
    }

    private void ApplySidebarState(bool visible)
    {
        SidebarColumn.Width = visible ? new GridLength(_lastSidebarWidth, GridUnitType.Pixel) : new GridLength(20);
        SplitterColumn.Width = visible ? new GridLength(1) : new GridLength(0);
        SidebarPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        Splitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarExpandBtn.Visibility = visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private double _lastSidebarWidth = 240;

    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var width = SidebarColumn.Width.Value;
        if (width < 60)
        {
            // Dragged too small → collapse
            _vm.SidebarVisible = false;
        }
        else
        {
            _lastSidebarWidth = width;
        }
    }

    // ---- sidebar interactions ----------------------------------------------

    private void OnOutlineSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: Models.OutlineEntry entry })
        {
            _vm.GoToOutlineCommand.Execute(entry);
        }
    }

    private void OnSearchResultSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: ViewModels.SearchHit hit })
        {
            _vm.OpenSearchHitCommand.Execute(hit);
        }
    }

    private void OnSidebarTabChanged(object sender, RoutedEventArgs e)
    {
        if (PanelOutline is null || PanelSearch is null) return; // not yet initialized
        PanelOutline.Visibility = TabOutline.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSearch.Visibility = TabSearch.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- drag-drop file open ------------------------------------------------

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnFileDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            var path = files[0];
            if (File.Exists(path))
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext is ".md" or ".markdown" or ".txt" or ".mkd" or ".mdown" or ".mdwn"
                    or ".textbundle" or ".textpack")
                {
                    _vm.OpenPath(path);
                }
            }
        }
    }

    // ---- window chrome ------------------------------------------------------

    private WindowState _preFullScreenState = WindowState.Normal;
    private double _preLeft, _preTop, _preWidth, _preHeight;

    private void ApplyFullScreen(bool full)
    {
        var titleBar = this.FindName("TitleBarGrid") as UIElement;
        if (full)
        {
            _preFullScreenState = WindowState;
            _preLeft = Left; _preTop = Top; _preWidth = Width; _preHeight = Height;
            if (titleBar != null) titleBar.Visibility = Visibility.Collapsed;
            // WM_GETMINMAXINFO constrains Maximized to the work area, so the window
            // will NOT go behind the taskbar. Just maximize.
            WindowState = WindowState.Maximized;
        }
        else
        {
            if (titleBar != null) titleBar.Visibility = Visibility.Visible;
            WindowState = _preFullScreenState;
            if (_preFullScreenState == WindowState.Normal)
            {
                Left = _preLeft; Top = _preTop; Width = _preWidth; Height = _preHeight;
            }
        }
    }

    private static class NativeMethods
    {
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private async void OnPrint(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = Web.CoreWebView2.Environment.CreatePrintSettings();
            await Web.CoreWebView2.PrintAsync(settings);
        }
        catch { /* cancelled or unavailable */ }
    }

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaxButtonGlyph()
    {
        // Segoe MDL2: restore = E923, maximize = E922.
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private bool _closeConfirmed;
    private bool _closingInProgress;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        // Re-entry guard: ignore close requests while an async close is in flight.
        if (_closingInProgress) { e.Cancel = true; return; }

        // Only prompt if editing a saved file with unsaved changes.
        if (!_closeConfirmed && _vm.FilePath is not null && _vm.HasUnsavedChanges)
        {
            e.Cancel = true;
            _closingInProgress = true;
            try
            {
                var dlg = new Views.ConfirmDialog(
                    Localization.Strings.Instance.UnsavedPrompt,
                    Localization.Strings.Instance.BtnSave,
                    Localization.Strings.Instance.BtnCancel,
                    Localization.Strings.Instance.BtnDontSave)
                { Owner = this };
                dlg.ShowDialog();

                switch (dlg.Result)
                {
                    case Views.ConfirmDialog.ConfirmResult.Confirm: // Save
                        await _vm.SaveCommand.ExecuteAsync(null);
                        if (_vm.HasUnsavedChanges) return; // save failed — stay open
                        break;
                    case Views.ConfirmDialog.ConfirmResult.Third: // Don't Save
                        _vm.DiscardRecoveryDraft(); // discarded changes must not resurrect via draft
                        break;
                    default: // Cancel
                        return;
                }
                _closeConfirmed = true;
            }
            finally
            {
                _closingInProgress = false;
            }
            Close();
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        _vm.PersistWindow(
            maximized ? RestoreBounds.Width : Width,
            maximized ? RestoreBounds.Height : Height,
            maximized);
        _vm.OnCleanExit();
    }
}
