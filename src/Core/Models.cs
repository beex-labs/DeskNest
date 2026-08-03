using System.Text.Json.Serialization;

namespace BeeX.DeskNest;
// The Launcher widget has been removed (replaced by the Ctrl+Q unified search window, SearchPaletteWindow); the enum member is kept so numeric serialization of old saves does not shift, and Load() clears leftover widgets
public enum NestKind { Note, Todo, Folder, ManagedFiles, Capture, Music, Clock, Screenshot, Weather, Tags, SystemMonitor, Deadline, Countdown, Launcher, WorkTimer }
// Render backend: ALL kinds render through WebView2 (media.html hosts imported video/image) because native WPF
// rendering does not present in a wallpaper window reparented under the shell's desktop host.
public enum WallpaperKind { Video, Image, Web, Shader, Scene }
public sealed class NestModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public NestKind Kind { get; set; }
    public string Title { get; set; } = "新小工具";
    public string Content { get; set; } = "";
    public string FolderPath { get; set; } = "";
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 80;
    public double Width { get; set; } = 340;
    public double Height { get; set; } = 360;
    public bool IsVisible { get; set; } = true;
    public List<TodoItem> Todos { get; set; } = [];
    public List<CaptureItem> Captures { get; set; } = [];
    public string Skin { get; set; } = "Acrylic";
    public double Opacity { get; set; } = 0.5;
    public string FontFamily { get; set; } = "Microsoft JhengHei UI";
    public double FontSize { get; set; } = 14;
    public string FontColor { get; set; } = "#0D1321";
    public string TextBackgroundColor { get; set; } = "";
    public string MusicTitleColor { get; set; } = "";
    public string MusicLyricColor { get; set; } = "";
    public string MusicOverlayColor { get; set; } = "";
    public bool Pinned { get; set; }
    public bool Locked { get; set; }
    public bool HeaderPinned { get; set; }
    public bool IsCollapsed { get; set; }
    public string BackgroundImagePath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public List<TagItem> Tags { get; set; } = [];
    public List<CountdownItem> Countdowns { get; set; } = [];
    public string City { get; set; } = "深圳";
    public double Latitude { get; set; } = 22.5431;
    public double Longitude { get; set; } = 114.0579;
    public string WorkStart { get; set; } = "09:00";
    public string WorkEnd { get; set; } = "18:00";
    public List<int> WorkDays { get; set; } = [1,2,3,4,5];
    public DateTime? LastWorkEndAlertDate { get; set; }
    public string MusicDisplayMode { get; set; } = "Cover";
    public double MusicLyricsOffsetSeconds { get; set; }
    /// <summary>Marker for the temporary easter-egg game platform: unconditionally cleaned up on startup load, to prevent leftover notes after the game is interrupted.</summary>
    public bool IsEasterEggTemp { get; set; }
}
public sealed class TodoSubItem { public string Text { get; set; } = ""; public bool Done { get; set; } }
public sealed class TodoItem { public Guid Id { get; set; } = Guid.NewGuid(); public string Text { get; set; } = ""; public bool Done { get; set; } public string Color { get; set; } = "#FF8A00"; public DateTime? DueAt { get; set; } public DateTime? SnoozeUntil { get; set; } public bool ReminderDismissed { get; set; } public List<int> ReminderOffsets { get; set; } = [1440,0]; public List<int> SentReminderOffsets { get; set; } = []; public bool DeadlineNotice2DaysSent { get; set; } public bool DeadlineNotice1DaySent { get; set; } public string Repeat { get; set; } = "不重複"; public List<string> Attachments { get; set; } = []; public List<TodoSubItem> SubItems { get; set; } = []; public bool SubExpanded { get; set; } = true; [JsonIgnore] public string SubSummary { get { if(SubItems.Count==0)return ""; int d=0; foreach(var s in SubItems)if(s.Done)d++; return $"{d}/{SubItems.Count}"; } } [JsonIgnore] public string DeadlineCountdown { get { if(!DueAt.HasValue)return "";var lang=Localization.CurrentLanguage;var span=DueAt.Value-DateTime.Now;if(span<=TimeSpan.Zero){var overdue=-span;return overdue.TotalDays>=1?Localization.Format("已逾期 {0} 天",lang,(int)overdue.TotalDays):Localization.Format("已逾期 {0} 小時",lang,Math.Max(1,(int)overdue.TotalHours));}if(span.TotalDays>=1)return Localization.Format("剩餘 {0} 天 {1} 小時",lang,(int)span.TotalDays,span.Hours);if(span.TotalHours>=1)return Localization.Format("剩餘 {0} 小時 {1} 分",lang,(int)span.TotalHours,span.Minutes);return Localization.Format("剩餘 {0} 分鐘",lang,Math.Max(1,span.Minutes));} } }
public sealed class CaptureItem { public Guid Id { get; set; } = Guid.NewGuid(); public string Text { get; set; } = ""; public DateTime CreatedAt { get; set; } = DateTime.Now; public string ImagePath { get; set; } = ""; public bool Pinned { get; set; } public string Paper { get; set; } = "White"; public string Source { get; set; } = "Manual"; public string MarkdownPath { get; set; } = ""; }
public sealed class TagItem { public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = ""; public string Color { get; set; } = "#FF8A00"; public DateTime CreatedAt { get; set; } = DateTime.Now; }
public sealed class CountdownItem { public Guid Id { get; set; } = Guid.NewGuid(); public string Title { get; set; } = "重要日子"; public DateTime Date { get; set; } = DateTime.Today.AddDays(30); public string Color { get; set; } = "#FF8A00"; public bool Annual { get; set; } public string FontFamily { get; set; } = ""; public double FontSize { get; set; } public string FontColor { get; set; } = ""; }
// A single installed wallpaper: its render kind, source path, cached thumbnail and per-item playback options. Props holds free-form key/value settings consumed by web wallpapers.
public sealed class WallpaperItem { public Guid Id { get; set; } = Guid.NewGuid(); public WallpaperKind Kind { get; set; } public string Path { get; set; } = ""; public string Name { get; set; } = ""; public string Thumb { get; set; } = ""; public double Volume { get; set; } = 1; public double PlaybackRate { get; set; } = 1; public bool AudioReactive { get; set; } public bool Interactive { get; set; } public Dictionary<string,string> Props { get; set; } = []; }
public sealed class AppState
{
    public List<NestModel> Nests { get; set; } = [];
    public bool StartWithWindows { get; set; }
    public double WidgetOpacity { get; set; } = 0.5;
    public string Theme { get; set; } = "Acrylic";
    public string ThemePreset { get; set; } = "Clear";
    public string GlobalFontFamily { get; set; } = "Microsoft JhengHei UI";
    public double GlobalFontSize { get; set; } = 14;
    public string GlobalFontColor { get; set; } = "#0D1321";
    public string InterfaceFontFamily { get; set; } = "";
    public double InterfaceFontSize { get; set; }
    public string ContentFontFamily { get; set; } = "";
    public double ContentFontSize { get; set; }
    public double CornerRadius { get; set; } = 18;
    public double IconSize { get; set; } = 30;
    public double ItemSpacing { get; set; } = 10;
    public bool AlignWidgetsToGrid { get; set; }
    public double WidgetGridSize { get; set; } = 20;
    public bool ShowFileExtensions { get; set; } = true;
    public bool ShowReminderSummary { get; set; } = true;
    public bool ShowCollapsedLogo { get; set; } = true;
    public bool ShowCollapsedMusicPlayerLogo { get; set; } = true;
    public bool EasterEggUnlocked { get; set; }
    /// <summary>Hotkey defaults V2 one-time migration: show/hide reverts to Ctrl+Alt+B, screenshot translation changes to Ctrl+Alt+Q (user-customized values are left untouched).</summary>
    public bool HotkeyDefaultsV2Migrated { get; set; }
    public bool UseSharedWidgetBackground { get; set; }
    public bool HeaderPinDefaultMigrated { get; set; }
    /// <summary>List of machine fingerprints (MachineGuid) that have completed onboarding: each new machine triggers onboarding once on first launch and not again; moving the data directory to a new machine re-triggers onboarding.</summary>
    public List<string> OnboardingSeenMachines { get; set; } = [];
    public bool ShowFloatingBall { get; set; } = true;
    public bool FloatingBallSnapToEdge { get; set; } = true;
    public double FloatingBallOpacity { get; set; } = 0.5;
    public double FloatingBallLeft { get; set; } = -1;
    public double FloatingBallTop { get; set; } = -1;
    public DateTime? FloatingBallHiddenUntil { get; set; }
    public string SharedWidgetBackgroundPath { get; set; } = "";
    public string ClipboardImageDirectory { get; set; } = "";
    public string ScreenshotDirectory { get; set; } = "";
    /// <summary>Default screenshot save format (png/jpg/bmp/gif/tiff); can still be switched per-shot within the overlay.</summary>
    public string CaptureDefaultFormat { get; set; } = "png";
    /// <summary>Also copy to the clipboard when saving a screenshot.</summary>
    public bool CaptureCopyOnSave { get; set; }
    /// <summary>Default recording frame rate and default countdown seconds (can still be switched per-shot within the recording toolbar).</summary>
    public int RecordingDefaultFps { get; set; } = 30;
    public int RecordingCountdownSec { get; set; }
    /// <summary>Upper limit of retained quick notes; the oldest are deleted automatically when exceeded.</summary>
    public int CaptureLimit { get; set; } = 100;
    /// <summary>Weather auto-refresh interval (minutes); 0 means no auto-refresh.</summary>
    public int WeatherRefreshMinutes { get; set; } = 30;
    /// <summary>Default reminder lead time for new todos (minutes, 0 = on time).</summary>
    public List<int> TodoDefaultReminderOffsets { get; set; } = [1440,0];
    public Dictionary<string,string> Hotkeys { get; set; } = new() { ["Note"]="",["Todo"]="",["MapFolder"]="",["Managed"]="",["CaptureFolder"]="",["QuickNote"]="",["Music"]="",["Clock"]="",["Screenshot"]="Ctrl + Alt + A",["ToggleAll"]="Ctrl + Alt + B",["CollapseAll"]="",["Weather"]="",["PinText"]="Ctrl + Alt + T",["MinimizeTransparent"]="Alt + X",["TranslateScreenshot"]="Ctrl + Alt + Q",["Launcher"]="Ctrl + Q" };
    /// <summary>The result most recently run from the Ctrl+Q unified search window (recalled with the !! prefix).</summary>
    public string PaletteLastResult { get; set; } = "";
    /// <summary>Last position the unified search window was dragged to (null means never dragged, so it centers when invoked).</summary>
    public double? PaletteLeft { get; set; }
    public double? PaletteTop { get; set; }
    /// <summary>Whether the Ctrl+Q search window shows the command guide dropdown on empty input (users familiar with the commands can disable it for a cleaner look).</summary>
    public bool ShowSearchPaletteGuide { get; set; } = true;
    public List<string> ToolButtonOrder { get; set; } = [];
    public Dictionary<string,bool> ToolButtonVisibility { get; set; } = [];
    public Dictionary<string,bool> ToolButtonMultiOpen { get; set; } = [];
    public string Language { get; set; } = "zh-TW";
    // ---- Live wallpaper engine ----
    /// <summary>Master switch for the desktop live wallpaper engine.</summary>
    public bool WallpaperEnabled { get; set; }
    /// <summary>Installed wallpaper library entries.</summary>
    public List<WallpaperItem> WallpaperLibrary { get; set; } = [];
    /// <summary>Per-monitor assignment: key is the monitor device name, value is the wallpaper item id.</summary>
    public Dictionary<string,Guid> WallpaperPerMonitor { get; set; } = [];
    /// <summary>Upper frame-rate limit applied to every wallpaper surface.</summary>
    public int WallpaperFpsCap { get; set; } = 60;
    /// <summary>Pause rendering when the wallpaper is fully covered by other windows.</summary>
    public bool WallpaperPauseWhenOccluded { get; set; } = true;
    /// <summary>Pause rendering while the device runs on battery.</summary>
    public bool WallpaperPauseOnBattery { get; set; } = true;
    /// <summary>Mute wallpaper audio while a fullscreen application is in the foreground.</summary>
    public bool WallpaperMuteOnFullscreen { get; set; } = true;
    /// <summary>Global playback volume for video wallpapers (0-1).</summary>
    public double WallpaperGlobalVolume { get; set; }
    /// <summary>Allow wallpapers to react to the system audio spectrum.</summary>
    public bool WallpaperAudioReactive { get; set; } = true;
}
