using Microsoft.Win32;
using System.IO;
using System.Text.Json;
using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Security.Cryptography;
using System.Windows.Media.Imaging;

namespace BeeX.DeskNest;
public sealed partial class DeskNestService : IDisposable
{
    readonly string dataDir = BeeXPaths.DataDir;
    readonly Dictionary<Guid, WidgetWindow> windows = [];
    readonly JsonSerializerOptions json = new() { WriteIndented = true };
    Forms.NotifyIcon? tray;
    System.Windows.Controls.ContextMenu? trayMenu;
    ControlWindow? control;
    SettingsWindow? settings;
    FloatingBallWindow? floatingBall;
    HotkeyWindow? hotkey;
    int hotkeySuspendCount;
    readonly Dictionary<string,BeexWrite.MarkdownNoteWindow> noteWindows = new(StringComparer.OrdinalIgnoreCase);
    Window? menuActivator;
    WindowTransparencyWindow? transparencyWindow;
    BeeXCleaner.CleanerWindow? cleanerWindow;
    SearchPaletteWindow? searchPalette;
    /// <summary>自研全盤文件索引（MFT/USN，Everything 原理復刻），供 Ctrl+Q 統一搜索窗使用</summary>
    public FileIndexService FileIndex { get; } = new();
    public WindowTransparencyService Transparency { get; } = new();
    DispatcherTimer? clipboardTimer,saveTimer;string lastClipboard="",lastClipboardImageHash="",lastClipboardFileSignature="";readonly HashSet<Guid> reminded=[];bool sessionLocked;
    public AppState State { get; private set; } = new();
    string StateFile => Path.Combine(dataDir, "state.json");
    public string ImageLibraryDirectory=>BeeXPaths.Root;
    public string ClipboardImageDirectory=>string.IsNullOrWhiteSpace(State.ClipboardImageDirectory)?BeeXPaths.ClipboardDir:State.ClipboardImageDirectory;
    public string ScreenshotDirectory=>string.IsNullOrWhiteSpace(State.ScreenshotDirectory)?BeeXPaths.ScreenshotsDir:State.ScreenshotDirectory;

    public void Start(bool showControlOnStartup=true)
    {
        Directory.CreateDirectory(dataDir); Load();
        SyncRuntimeDefaults();
        InitWriteHost();
        SystemEvents.SessionSwitch+=SessionSwitch;
        tray = new Forms.NotifyIcon { Icon = LoadTrayIcon(), Text = "BeeX DeskNest", Visible = true };
        tray.DoubleClick+=(_,_)=>ShowControl();
        tray.MouseUp+=Tray_MouseUp;
        ApplyTrayTheme();
        ApplyFloatingBallVisibility();
        RebuildHotkeys();
        FileIndex.Start();
        clipboardTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(800)};clipboardTimer.Tick+=(_,_)=>{PollClipboard();CheckReminders();};clipboardTimer.Start();
        foreach (var nest in State.Nests) Open(nest);
        if(NeedsOnboarding())ShowOnboarding();
        else if(showControlOnStartup)ShowControl();
    }
    public void ShowControl(){if(control is null||!control.IsLoaded)control=new ControlWindow(this);control.RefreshList();control.RefreshFeatures();Localization.Apply(control,State.Language);control.Show();control.Activate();}
    public void ShowSettings(){if(settings is null||!settings.IsLoaded)settings=new SettingsWindow(this);settings.LoadState();settings.Show();settings.Activate();}
    public void ShowWindowTransparency(){if(transparencyWindow is null||!transparencyWindow.IsLoaded)transparencyWindow=new WindowTransparencyWindow(this,Transparency);transparencyWindow.ShowTool();}
    /// <summary>Ctrl+Q 全局統一搜索窗（原快速啟動格子的繼任者）</summary>
    public void ShowSearchPalette(){if(searchPalette is null||!searchPalette.IsLoaded)searchPalette=new SearchPaletteWindow(this);searchPalette.ShowPalette();}
    // BeeX 系统清理组件：卸载程序、清理 HKLM / Program Files 残留需要管理员权限.
    // 已提权则进程内打开；未提权则以管理员身份重新拉起自身 --cleaner 独立模式；UAC 取消则退回非提权打开。
    public void ShowCleaner()
    {
        if(IsElevated())
        {
            OpenCleanerInProcess();
            return;
        }
        var exe=Environment.ProcessPath;
        if(!string.IsNullOrEmpty(exe))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo{FileName=exe,Arguments="--cleaner",UseShellExecute=true,Verb="runas"});
                return;
            }
            catch(System.ComponentModel.Win32Exception){/* 用户取消了 UAC，退回进程内打开 */}
            catch{}
        }
        OpenCleanerInProcess();
    }
    void OpenCleanerInProcess()
    {
        if(cleanerWindow is null||!cleanerWindow.IsLoaded)cleanerWindow=new BeeXCleaner.CleanerWindow();
        cleanerWindow.Show();
        if(cleanerWindow.WindowState==WindowState.Minimized)cleanerWindow.WindowState=WindowState.Normal;
        cleanerWindow.Activate();
    }
    static bool IsElevated()
    {
        try
        {
            using var identity=System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch{return false;}
    }
    public void MinimizeAllTransparentWindows(){if(Transparency.HasModifiedWindows)Transparency.MinimizeAllTransparent();}
    public void ApplyPreferences(bool persist=true){SyncRuntimeDefaults();control?.ApplyPreferences();control?.RefreshFeatures();settings?.ApplyPreferences();foreach(var nest in State.Nests){nest.FontFamily=ContentFontFamily();nest.FontSize=nest.Kind==NestKind.WorkTimer?Math.Max(20,ContentFontSize()):ContentFontSize();nest.FontColor=State.GlobalFontColor;}foreach(var w in windows.Values)w.ApplyPreferences();if(control!=null){Localization.ApplyFont(control,InterfaceFontFamily(),InterfaceFontSize());WindowRegionHelper.ApplyDeferred(control,State.CornerRadius);}if(settings!=null){Localization.ApplyFont(settings,InterfaceFontFamily(),InterfaceFontSize());WindowRegionHelper.ApplyDeferred(settings,State.CornerRadius);}ApplyTrayTheme();ApplyFloatingBallVisibility();if(floatingBall is { IsLoaded:true })floatingBall.ApplyPreferences();if(transparencyWindow is { IsLoaded:true })transparencyWindow.ApplyTheme();foreach(var w in noteWindows.Values.ToList())w.RefreshHostTheme();if(persist)Save();}
    /// <summary>把設定頁的默認值同步給截圖覆蓋層/錄屏工具等靜態消費點</summary>
    void SyncRuntimeDefaults(){ScreenCaptureOverlay.DefaultFormat=State.CaptureDefaultFormat;ScreenCaptureOverlay.CopyOnSave=State.CaptureCopyOnSave;RecordingController.DefaultFps=State.RecordingDefaultFps;RecordingController.DefaultCountdownSec=State.RecordingCountdownSec;}
    public void SetLanguage(string language){State.Language=language;Localization.CurrentLanguage=language;control?.RefreshLanguage();settings?.RefreshLanguage();foreach(var w in windows.Values)w.RefreshLanguage();ApplyTrayLanguage(language);if(noteWindows.Count>0)try{BeexWrite.Localization.Strings.Instance.LoadLocale(BeexWrite.WriteHost.WriteDataDirectory,WriteLocale());}catch{}Save();}
    public void SetHotkey(string command,string shortcut){foreach(var key in State.Hotkeys.Keys.ToList())if(key!=command&&State.Hotkeys[key].Equals(shortcut,StringComparison.OrdinalIgnoreCase))State.Hotkeys[key]="";State.Hotkeys[command]=shortcut;RebuildHotkeys();control?.RefreshShortcutTooltips();Save();}
    public void RefreshWidgets(){foreach(var w in windows.Values)w.RefreshData();}
    public string InterfaceFontFamily()=>string.IsNullOrWhiteSpace(State.InterfaceFontFamily)?State.GlobalFontFamily:State.InterfaceFontFamily;
    public double InterfaceFontSize()=>State.InterfaceFontSize>0?State.InterfaceFontSize:State.GlobalFontSize;
    public string ContentFontFamily()=>string.IsNullOrWhiteSpace(State.ContentFontFamily)?State.GlobalFontFamily:State.ContentFontFamily;
    public double ContentFontSize()=>State.ContentFontSize>0?State.ContentFontSize:State.GlobalFontSize;
    public double EffectiveFloatingBallOpacity()=>State.FloatingBallOpacity>0?State.FloatingBallOpacity:State.WidgetOpacity;
    public void ApplyThemePreset(string preset)
    {
        State.ThemePreset=preset;
        switch(preset)
        {
            case "Dark":
                State.Theme="Dark";State.WidgetOpacity=.62;State.GlobalFontColor="#FFFFFF";State.CornerRadius=18;break;
            case "Work":
                State.Theme="Acrylic";State.WidgetOpacity=.72;State.GlobalFontColor="#111827";State.CornerRadius=12;break;
            case "Music":
                State.Theme="Dark";State.WidgetOpacity=.54;State.GlobalFontColor="#FFFFFF";State.CornerRadius=22;break;
            case "Focus":
                State.Theme="Acrylic";State.WidgetOpacity=.82;State.GlobalFontColor="#0D1321";State.CornerRadius=10;break;
            default:
                State.Theme="Acrylic";State.WidgetOpacity=.5;State.GlobalFontColor="#0D1321";State.CornerRadius=18;break;
        }
        State.FloatingBallOpacity=State.WidgetOpacity;
        ApplyPreferences();
    }
    // ---- 引導模式：每台新電腦（機器指紋不在 state.json 記錄中）首啟觸發一次，完成或跳過後永不再觸發 ----
    public static string MachineFingerprint(){try{using var key=Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");if(key?.GetValue("MachineGuid") is string guid&&!string.IsNullOrWhiteSpace(guid))return guid.Trim();}catch{}return Environment.MachineName+"|"+Environment.UserName;}
    bool NeedsOnboarding()=>!State.OnboardingSeenMachines.Contains(MachineFingerprint(),StringComparer.OrdinalIgnoreCase);
    void MarkOnboardingSeen(){var id=MachineFingerprint();if(!State.OnboardingSeenMachines.Contains(id,StringComparer.OrdinalIgnoreCase))State.OnboardingSeenMachines.Add(id);Save();}
    void ShowOnboarding(){var wizard=new OnboardingWindow(this);wizard.Show();wizard.Activate();}
    /// <summary>引導可選的格子種類：完成時只收斂這些種類，其餘種類（映射資料夾等）不受引導影響</summary>
    public static readonly NestKind[] OnboardingKinds=[NestKind.Todo,NestKind.Music,NestKind.Weather,NestKind.Clock,NestKind.Note,NestKind.Capture];
    /// <summary>引導完成：套用語言/主題；勾選的種類補建並顯示，未勾選的既有格子只隱藏不刪除（保護遷移用戶資料），之後打開主控制台。</summary>
    public void CompleteOnboarding(string language,string theme,IReadOnlyCollection<NestKind> starters)
    {
        MarkOnboardingSeen();
        if(language!=State.Language)SetLanguage(language);
        if(theme!=State.Theme)
        {
            State.Theme=theme;
            State.ThemePreset=theme=="Dark"?"Dark":"Clear";
            State.GlobalFontColor=theme=="Dark"?"#FFFFFF":"#0D1321";
            foreach(var nest in State.Nests)nest.Skin=theme;
            ApplyPreferences();
        }
        foreach(var kind in starters)if(!State.Nests.Any(n=>n.Kind==kind))Add(kind);
        foreach(var nest in State.Nests.Where(n=>OnboardingKinds.Contains(n.Kind)).ToList())SetVisible(nest,starters.Contains(nest.Kind));
        if(starters.Count>0)ArrangeDesktopLayout();
        ShowControl();
    }
    /// <summary>跳過引導：只記錄本機已看過，不建立任何格子。</summary>
    public void SkipOnboarding(){MarkOnboardingSeen();ShowControl();}
    public void ArrangeDesktopLayout()
    {
        var targets=State.Nests.Where(n=>n.IsVisible&&!n.Locked).ToList();
        if(targets.Count==0)return;
        ArrangeModelsIntoWorkArea(targets);
        foreach(var nest in targets)
        {
            Open(nest);
            if(!windows.TryGetValue(nest.Id,out var w))continue;
            w.Left=nest.Left;w.Top=nest.Top;w.Width=nest.Width;w.Height=nest.Height;w.ApplyCollapseState(false);
        }
        Save();
        control?.RefreshList();
    }
    void ArrangeModelsIntoWorkArea(List<NestModel> targets)
    {
        var work=SystemParameters.WorkArea;
        var margin=18d;
        var gap=Math.Clamp(State.WidgetGridSize>0?State.WidgetGridSize:20,12,32);
        var x=work.Left+margin;
        var y=work.Top+margin;
        var rowHeight=0d;
        foreach(var nest in targets.OrderBy(n=>n.Kind==NestKind.Todo?0:n.Kind==NestKind.Music?1:n.Kind==NestKind.Weather?2:n.Kind==NestKind.Clock?3:4).ThenBy(n=>n.CreatedAt))
        {
            var min=MinimumSizeFor(nest.Kind,nest.IsCollapsed);
            var maxWidth=Math.Max(min.Width,work.Width-margin*2);
            var width=Math.Min(Math.Max(nest.Width,min.Width),Math.Min(maxWidth,520));
            var height=Math.Min(Math.Max(nest.IsCollapsed?64:nest.Height,min.Height),Math.Max(min.Height,work.Height-margin*2));
            if(x+width>work.Right-margin&&x>work.Left+margin)
            {
                x=work.Left+margin;
                y+=rowHeight+gap;
                rowHeight=0;
            }
            if(y+height>work.Bottom-margin)
            {
                y=work.Top+margin;
                x+=width+gap;
            }
            nest.Left=SnapValue(Math.Clamp(x,work.Left+margin,Math.Max(work.Left+margin,work.Right-width-margin)),gap);
            nest.Top=SnapValue(Math.Clamp(y,work.Top+margin,Math.Max(work.Top+margin,work.Bottom-height-margin)),gap);
            nest.Width=width;
            if(!nest.IsCollapsed)nest.Height=height;
            rowHeight=Math.Max(rowHeight,height);
            x+=width+gap;
        }
    }
    static double SnapValue(double value,double grid)=>Math.Round(value/grid)*grid;
    static System.Windows.Size MinimumSizeFor(NestKind kind,bool collapsed)=>collapsed?new System.Windows.Size(240,64):kind switch
    {
        NestKind.Todo=>new System.Windows.Size(390,300),
        NestKind.Capture=>new System.Windows.Size(390,260),
        NestKind.Music=>new System.Windows.Size(280,220),
        NestKind.Weather=>new System.Windows.Size(260,190),
        NestKind.Clock=>new System.Windows.Size(260,200),
        NestKind.Folder or NestKind.ManagedFiles=>new System.Windows.Size(280,180),
        NestKind.SystemMonitor=>new System.Windows.Size(320,260),
        _=>new System.Windows.Size(260,180)
    };
    public bool FloatingBallHiddenToday()=>State.FloatingBallHiddenUntil.HasValue&&State.FloatingBallHiddenUntil.Value>DateTime.Now;
    public void ApplyFloatingBallVisibility()
    {
        var shouldShow=State.ShowFloatingBall&&!FloatingBallHiddenToday();
        if(shouldShow)
        {
            if(floatingBall is null||!floatingBall.IsLoaded)floatingBall=new FloatingBallWindow(this);
            floatingBall.Show();
            floatingBall.ApplyPreferences();
        }
        else if(floatingBall is { IsLoaded:true })floatingBall.Hide();
    }
    public void SetFloatingBallVisible(bool visible){State.ShowFloatingBall=visible;if(visible)State.FloatingBallHiddenUntil=null;ApplyFloatingBallVisibility();Save();}
    public void HideFloatingBallForToday(){State.FloatingBallHiddenUntil=DateTime.Today.AddDays(1);ApplyFloatingBallVisibility();Save();}
    public void StartEasterEgg(){if(EasterEggGame.Running)return;new EasterEggGame(this).Start();}
    internal WidgetWindow? WindowOf(NestModel n)=>windows.GetValueOrDefault(n.Id);
    /// <summary>彩蛋臨時平台：繞過單實例聚焦直接建 Note 格子，遊戲結束後由 Remove 刪除</summary>
    internal NestModel AddEasterEggPlatform(int index){var n=new NestModel{Kind=NestKind.Note,Title=Localization.T("蜂巢平台",State.Language)+" "+(index+1),IsEasterEggTemp=true,Skin=State.Theme,Opacity=State.WidgetOpacity,FontFamily=ContentFontFamily(),FontSize=ContentFontSize(),FontColor=State.GlobalFontColor,Left=200+index*30,Top=200+index*30,Width=300,Height=200};State.Nests.Add(n);Open(n);return n;}
    internal void HideFloatingBallForGame(){if(floatingBall is {IsLoaded:true})floatingBall.Hide();}
    internal void EnsureEasterEggEntry(){control?.EnsureEasterEggButton();}
    void RebuildHotkeys(){hotkey?.Dispose();hotkey=null;if(hotkeySuspendCount>0)return;var actions=new Dictionary<string,(string Shortcut,Action Action)>{["Note"]=(State.Hotkeys.GetValueOrDefault("Note",""),()=>Add(NestKind.Note)),["Todo"]=(State.Hotkeys.GetValueOrDefault("Todo",""),()=>Add(NestKind.Todo)),["MapFolder"]=(State.Hotkeys.GetValueOrDefault("MapFolder",""),AddFolder),["Managed"]=(State.Hotkeys.GetValueOrDefault("Managed",""),AddManagedFiles),["CaptureFolder"]=(State.Hotkeys.GetValueOrDefault("CaptureFolder",""),OpenCaptureFolder),["QuickNote"]=(State.Hotkeys.GetValueOrDefault("QuickNote",""),()=>Add(NestKind.Capture)),["Music"]=(State.Hotkeys.GetValueOrDefault("Music",""),()=>Add(NestKind.Music)),["Clock"]=(State.Hotkeys.GetValueOrDefault("Clock",""),()=>Add(NestKind.Clock)),["Screenshot"]=(State.Hotkeys.GetValueOrDefault("Screenshot","Ctrl + Alt + A"),()=>CaptureScreen()),["ToggleAll"]=(State.Hotkeys.GetValueOrDefault("ToggleAll","Ctrl + Alt + B"),ToggleAll),["CollapseAll"]=(State.Hotkeys.GetValueOrDefault("CollapseAll",""),ToggleCollapseAll),["Weather"]=(State.Hotkeys.GetValueOrDefault("Weather",""),()=>Add(NestKind.Weather)),["PinText"]=(State.Hotkeys.GetValueOrDefault("PinText","Ctrl + Alt + T"),PinClipboardText),["MinimizeTransparent"]=(State.Hotkeys.GetValueOrDefault("MinimizeTransparent","Alt + X"),MinimizeAllTransparentWindows),["TranslateScreenshot"]=(State.Hotkeys.GetValueOrDefault("TranslateScreenshot","Ctrl + Alt + Q"),CaptureScreenForTranslation),["Launcher"]=(State.Hotkeys.GetValueOrDefault("Launcher","Ctrl + Q"),ShowSearchPalette)};hotkey=new HotkeyWindow(actions);}
    /// <summary>供編輯器等焦點窗口使用：暫時註銷全局熱鍵，避免與編輯器快捷鍵衝突（引用計數）。</summary>
    public void SuspendGlobalHotkeys(){hotkeySuspendCount++;if(hotkeySuspendCount==1)RebuildHotkeys();}
    public void ResumeGlobalHotkeys(){if(hotkeySuspendCount==0)return;hotkeySuspendCount--;if(hotkeySuspendCount==0)RebuildHotkeys();}

    // ---- BeexWrite Markdown 隨記筆記 ----
    public string NotesDirectory=>BeexWrite.WriteHost.NotesDirectory;
    string WriteLocale()=>State.Language switch{"zh-CN"=>"zh-CN","en-US"=>"en",_=>"zh-TW"};
    void InitWriteHost()
    {
        BeexWrite.WriteHost.IsHostDark=()=>State.Theme=="Dark";
        BeexWrite.WriteHost.HostLocale=WriteLocale;
        BeexWrite.WriteHost.SuspendHostHotkeys=SuspendGlobalHotkeys;
        BeexWrite.WriteHost.ResumeHostHotkeys=ResumeGlobalHotkeys;
    }
    /// <summary>在筆記目錄創建一個空的 Markdown 筆記文件，返回完整路徑。</summary>
    public string CreateMarkdownNote()
    {
        Directory.CreateDirectory(NotesDirectory);
        var path=Path.Combine(NotesDirectory,$"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.md");
        File.WriteAllText(path,"");
        return path;
    }
    /// <summary>打開（或激活已打開的）Markdown 筆記編輯窗口；同一筆記只允許一個窗口。</summary>
    public void OpenMarkdownNote(string path,Action<string>? onSaved=null,Action? onClosed=null)
    {
        if(noteWindows.TryGetValue(path,out var existing)){if(existing.WindowState==WindowState.Minimized)existing.WindowState=WindowState.Normal;existing.Activate();return;}
        var w=new BeexWrite.MarkdownNoteWindow(path);
        noteWindows[path]=w;
        if(onSaved!=null)w.DocumentSaved+=saved=>onSaved(saved);
        w.Closed+=(_,_)=>{noteWindows.Remove(path);onClosed?.Invoke();};
        w.Show();
        w.Activate();
    }
    /// <summary>讀取 Markdown 文檔首個非空行作為列表預覽（剥離標題/列表等前綴）。</summary>
    public static string MarkdownFirstLine(string path)
    {
        try
        {
            foreach(var raw in File.ReadLines(path))
            {
                var line=raw.Trim();
                if(line.Length==0)continue;
                if(line is "---" or "```")continue;
                line=System.Text.RegularExpressions.Regex.Replace(line,@"^(#{1,6}\s+|>\s+|[-*+]\s+\[[ xX]\]\s+|[-*+]\s+|\d+[.)]\s+)","");
                line=System.Text.RegularExpressions.Regex.Replace(line,@"[*_`~]","");
                if(line.Length>120)line=line[..120];
                if(line.Length>0)return line;
            }
        }
        catch{}
        return "";
    }
    public void ResetPreferences(){var keepStartup=State.StartWithWindows;State.WidgetOpacity=.5;State.Theme="Acrylic";State.ThemePreset="Clear";State.GlobalFontFamily="Microsoft JhengHei UI";State.GlobalFontSize=14;State.GlobalFontColor="#0D1321";State.InterfaceFontFamily=State.GlobalFontFamily;State.InterfaceFontSize=14;State.ContentFontFamily=State.GlobalFontFamily;State.ContentFontSize=14;State.CornerRadius=18;State.IconSize=30;State.ItemSpacing=10;State.ShowFileExtensions=true;State.ShowFloatingBall=true;State.FloatingBallSnapToEdge=true;State.ShowCollapsedLogo=true;State.ShowCollapsedMusicPlayerLogo=true;State.FloatingBallOpacity=State.WidgetOpacity;State.FloatingBallHiddenUntil=null;State.StartWithWindows=keepStartup;ApplyPreferences();}
    static string? MultiOpenKey(NestKind kind)=>kind switch{NestKind.Note=>"Note",NestKind.Todo=>"Todo",NestKind.Folder=>"MapFolder",NestKind.ManagedFiles=>"Managed",NestKind.Capture=>"QuickNote",NestKind.Music=>"Music",NestKind.Clock=>"Clock",NestKind.Weather=>"Weather",NestKind.Tags=>"Tags",NestKind.SystemMonitor=>"SystemMonitor",NestKind.Countdown=>"Countdown",NestKind.WorkTimer=>"WorkTimer",_=>null};
    // 默认所有组件单实例：仅当该功能在设置中开启「允许多开」时才可建立多个。
    public bool AllowMultiOpen(NestKind kind){var key=MultiOpenKey(kind);return key==null||State.ToolButtonMultiOpen.GetValueOrDefault(key,false);}
    bool TryFocusSingleton(NestKind kind){if(AllowMultiOpen(kind))return false;var existing=State.Nests.FirstOrDefault(n=>n.Kind==kind);if(existing==null)return false;SetVisible(existing,true);return true;}
    public void Add(NestKind kind)
    {
        if(TryFocusSingleton(kind))return;
        var defaultSize=kind switch{NestKind.SystemMonitor=>(380d,220d),NestKind.Music=>(340d,250d),NestKind.Clock=>(300d,280d),NestKind.Weather=>(300d,300d),NestKind.Screenshot=>(320d,300d),NestKind.Tags=>(320d,290d),NestKind.Note=>(340d,320d),NestKind.Countdown=>(340d,330d),NestKind.WorkTimer=>(360d,360d),NestKind.Todo=>(420d,440d),_=>(340d,360d)};
        var n = new NestModel { Kind = kind, Title = Localization.DefaultTitle(kind), Skin=State.Theme, Opacity=State.WidgetOpacity, FontFamily=ContentFontFamily(), FontSize=kind==NestKind.WorkTimer?Math.Max(20,ContentFontSize()):ContentFontSize(), FontColor=State.GlobalFontColor, Left = 120 + State.Nests.Count * 24, Top = 120 + State.Nests.Count * 24, Width=defaultSize.Item1, Height=defaultSize.Item2 };
        State.Nests.Add(n); Save(); Open(n); control?.RefreshList(); settings?.RefreshFeatureNests();
    }
    public void AddManagedFiles()
    {
        if(TryFocusSingleton(NestKind.ManagedFiles))return;
        var root = BeeXPaths.FileBoxesDir; Directory.CreateDirectory(root);
        var name = "檔案盒 " + (State.Nests.Count(n => n.Kind == NestKind.ManagedFiles) + 1); var folder = Path.Combine(root, name); Directory.CreateDirectory(folder);
        var n = new NestModel { Kind = NestKind.ManagedFiles, Title = name, FolderPath = folder, Skin=State.Theme, Opacity=State.WidgetOpacity, FontFamily=ContentFontFamily(), FontSize=ContentFontSize(), FontColor=State.GlobalFontColor, Left = 150 + State.Nests.Count * 20, Top = 150 + State.Nests.Count * 20 };
        State.Nests.Add(n); Save(); Open(n); control?.RefreshList(); settings?.RefreshFeatureNests();
    }
    public void AddFolder()
    {
        if(TryFocusSingleton(NestKind.Folder))return;
        using var dialog = new Forms.FolderBrowserDialog { Description = "選擇要映射到桌面的資料夾", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        var n = new NestModel { Kind = NestKind.Folder, Title = Path.GetFileName(dialog.SelectedPath), FolderPath = dialog.SelectedPath, Skin=State.Theme, Opacity=State.WidgetOpacity, FontFamily=ContentFontFamily(), FontSize=ContentFontSize(), FontColor=State.GlobalFontColor, Left = 140 + State.Nests.Count * 20, Top = 140 + State.Nests.Count * 20 };
        State.Nests.Add(n); Save(); Open(n); control?.RefreshList(); settings?.RefreshFeatureNests();
    }
    void Open(NestModel n)
    {
        // 字典里若残留已关闭/未加载的僵尸窗口，先清理再重建，避免 Show 无效
        if (windows.TryGetValue(n.Id, out var existing))
        {
            if (!existing.IsLoaded || existing.AllowClose) windows.Remove(n.Id);
            else return;
        }
        var w=new WidgetWindow(this,n);Localization.Apply(w,State.Language);windows[n.Id]=w;if(n.IsVisible)w.Show();
    }
    public void Remove(NestModel n)
    {
        // 按 Id 查找当前真实 model，避免列表项持有陈旧引用时静默失败
        var current = State.Nests.FirstOrDefault(x => x.Id == n.Id);
        if (current == null) { control?.RefreshList(); return; }
        // 锁定组件也允许删除（用户已通过确认对话框表达删除意图）
        current.Locked = false;
        if (windows.Remove(current.Id, out var w)) { w.AllowClose = true; w.Close(); }
        State.Nests.Remove(current); Save(); control?.RefreshList(); settings?.RefreshFeatureNests();
    }
    public void Toggle(NestModel n) { SetVisible(n,!n.IsVisible); }
    public void SetVisible(NestModel n,bool visible){var current=State.Nests.FirstOrDefault(x=>x.Id==n.Id)??n;Open(current);if(!windows.TryGetValue(current.Id,out var w))return;if(visible){w.Show();w.Topmost=true;w.Activate();w.Topmost=current.Pinned;}else w.Hide();current.IsVisible=visible;Save();control?.RefreshList();settings?.RefreshFeatureNests();}
    public void ToggleAll()
    {
        bool show = windows.Values.Any(w => w.IsVisible==false);
        foreach(var pair in windows){
            var nest=State.Nests.FirstOrDefault(n=>n.Id==pair.Key);
            if(nest==null)continue;
            // 锁定组件也参与显隐切换（锁定只防误操作，不阻止显隐）
            if(show){pair.Value.Show();pair.Value.Topmost=true;pair.Value.Activate();pair.Value.Topmost=nest.Pinned;}else pair.Value.Hide();
            nest.IsVisible=show;
        }
        Save();control?.RefreshList();
    }
    public void ToggleCollapseAll()
    {
        if(EasterEggGame.Running)return;
        // 彩蛋：首次點擊「摺疊／展開全部」觸發蜂巢小遊戲（只觸發一次，之後由主控台入口重玩）
        if(!State.EasterEggUnlocked){StartEasterEgg();return;}
        var active=windows.Keys.Select(id=>State.Nests.FirstOrDefault(n=>n.Id==id)).Where(n=>n!=null&&n.IsVisible&&!n.Locked).Cast<NestModel>().ToList();
        if(active.Count==0)return;
        var collapse=active.Any(n=>!n.IsCollapsed);
        foreach(var nest in active){nest.IsCollapsed=collapse;if(windows.TryGetValue(nest.Id,out var window))window.ApplyCollapseState(false);}
        Save();
    }
    void SessionSwitch(object sender,SessionSwitchEventArgs e){if(e.Reason==SessionSwitchReason.SessionLock)sessionLocked=true;else if(e.Reason==SessionSwitchReason.SessionUnlock)sessionLocked=false;}
    public void OpenCaptureFolder(){Directory.CreateDirectory(ScreenshotDirectory);System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",$"\"{ScreenshotDirectory}\""){UseShellExecute=true});}
    public void ClearAll(){foreach(var nest in State.Nests.ToList()){nest.Locked=false;if(windows.Remove(nest.Id,out var w)){w.AllowClose=true;w.Close();}State.Nests.Remove(nest);}Save();control?.RefreshList();settings?.RefreshFeatureNests();}
    public void Reorder(NestModel item,int index){State.Nests.Remove(item);State.Nests.Insert(Math.Clamp(index,0,State.Nests.Count),item);Save();control?.RefreshList();settings?.RefreshFeatureNests();}
    public IEnumerable<Rect> GetWidgetBounds(Guid exceptId)=>windows.Where(x=>x.Key!=exceptId&&x.Value.IsVisible).Select(x=>new Rect(x.Value.Left,x.Value.Top,x.Value.ActualWidth,x.Value.ActualHeight)).ToList();
    public void SetStartup(bool enabled)
    {
        // 主程式已改為 requireAdministrator：提權程式寫 Run 鍵會被 Windows 登入時靜默跳過，
        // 改用計劃任務（/RL HIGHEST）實現開機自啟；同時清掉舊版遺留的 Run 鍵。
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true); key?.DeleteValue("BeeX DeskNest", false); } catch { }
        try
        {
            var info = enabled
                ? new System.Diagnostics.ProcessStartInfo("schtasks.exe", $"/Create /F /RL HIGHEST /SC ONLOGON /TN \"BeeX DeskNest\" /TR \"\\\"{Environment.ProcessPath}\\\" --tray\"")
                : new System.Diagnostics.ProcessStartInfo("schtasks.exe", "/Delete /F /TN \"BeeX DeskNest\"");
            info.UseShellExecute = false; info.CreateNoWindow = true;
            System.Diagnostics.Process.Start(info)?.WaitForExit(5000);
        }
        catch { }
        State.StartWithWindows = enabled; Save();
    }
    public void CaptureScreen(Action? closed=null) { ScreenCaptureOverlay.Begin(ScreenshotDirectory,path => { try{if(System.Windows.Clipboard.ContainsImage()){var image=System.Windows.Clipboard.GetImage();if(image!=null){using var memory=new MemoryStream();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(memory);lastClipboardImageHash=Convert.ToHexString(SHA256.HashData(memory.ToArray()));}}}catch{}var capture=State.Nests.FirstOrDefault(n=>n.Kind==NestKind.Capture);if(capture!=null){capture.Captures.Insert(0,new CaptureItem{Text="螢幕截圖",ImagePath=path,Source="Manual"});TrimCaptures(capture);if(windows.TryGetValue(capture.Id,out var w))w.RefreshData();Save();} },closed,State.Language); }
    // 翻譯截圖：複用普通截圖覆蓋層，但框選完成後自動觸發覆蓋層內建的原位翻譯（保留選框，拖動/縮放自動重譯），不再關窗後另彈貼圖窗口
    public void CaptureScreenForTranslation() { ScreenCaptureOverlay.Begin(ScreenshotDirectory,path => { try{if(System.Windows.Clipboard.ContainsImage()){var image=System.Windows.Clipboard.GetImage();if(image!=null){using var memory=new MemoryStream();var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));encoder.Save(memory);lastClipboardImageHash=Convert.ToHexString(SHA256.HashData(memory.ToArray()));}}}catch{}var capture=State.Nests.FirstOrDefault(n=>n.Kind==NestKind.Capture);if(capture!=null){capture.Captures.Insert(0,new CaptureItem{Text="螢幕截圖",ImagePath=path,Source="Manual"});TrimCaptures(capture);if(windows.TryGetValue(capture.Id,out var w))w.RefreshData();Save();} },closed:null,language:State.Language,autoTranslateOnSelect:true); }
    public void PinClipboardText(){try{if(System.Windows.Clipboard.ContainsText()){var text=System.Windows.Clipboard.GetText();if(!string.IsNullOrWhiteSpace(text))TextPinWindow.Pin(text.Trim());}}catch{}}
    public void Exit() { transparencyWindow?.ShutdownTool();floatingBall?.Close();searchPalette?.Hide();FileIndex.Dispose();foreach (var w in windows.Values) { w.AllowClose = true; w.Close(); } tray!.Visible = false; System.Windows.Application.Current.Shutdown(); }
    public void Dispose() { SystemEvents.SessionSwitch-=SessionSwitch;clipboardTimer?.Stop();hotkey?.Dispose(); FileIndex.Dispose();transparencyWindow?.ShutdownTool();menuActivator?.Close(); floatingBall?.Close();tray?.Dispose(); }
}
