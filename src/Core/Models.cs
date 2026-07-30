using System.Text.Json.Serialization;

namespace BeeX.DeskNest;
public enum NestKind { Note, Todo, Folder, ManagedFiles, Capture, Music, Clock, Screenshot, Weather, Tags, SystemMonitor, Deadline, Countdown, Launcher, WorkTimer }
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
    /// <summary>彩蛋遊戲臨時平台標記：啟動加載時無條件清掃，防止遊戲中斷後殘留成真實便箋</summary>
    public bool IsEasterEggTemp { get; set; }
}
public sealed class TodoItem { public Guid Id { get; set; } = Guid.NewGuid(); public string Text { get; set; } = ""; public bool Done { get; set; } public string Color { get; set; } = "#FF8A00"; public DateTime? DueAt { get; set; } public DateTime? SnoozeUntil { get; set; } public bool ReminderDismissed { get; set; } public List<int> ReminderOffsets { get; set; } = [1440,0]; public List<int> SentReminderOffsets { get; set; } = []; public bool DeadlineNotice2DaysSent { get; set; } public bool DeadlineNotice1DaySent { get; set; } public string Repeat { get; set; } = "不重複"; public List<string> Attachments { get; set; } = []; [JsonIgnore] public string DeadlineCountdown { get { if(!DueAt.HasValue)return "";var lang=Localization.CurrentLanguage;var span=DueAt.Value-DateTime.Now;if(span<=TimeSpan.Zero){var overdue=-span;return overdue.TotalDays>=1?Localization.Format("已逾期 {0} 天",lang,(int)overdue.TotalDays):Localization.Format("已逾期 {0} 小時",lang,Math.Max(1,(int)overdue.TotalHours));}if(span.TotalDays>=1)return Localization.Format("剩餘 {0} 天 {1} 小時",lang,(int)span.TotalDays,span.Hours);if(span.TotalHours>=1)return Localization.Format("剩餘 {0} 小時 {1} 分",lang,(int)span.TotalHours,span.Minutes);return Localization.Format("剩餘 {0} 分鐘",lang,Math.Max(1,span.Minutes));} } }
public sealed class CaptureItem { public Guid Id { get; set; } = Guid.NewGuid(); public string Text { get; set; } = ""; public DateTime CreatedAt { get; set; } = DateTime.Now; public string ImagePath { get; set; } = ""; public bool Pinned { get; set; } public string Paper { get; set; } = "White"; public string Source { get; set; } = "Manual"; public string MarkdownPath { get; set; } = ""; }
public sealed class TagItem { public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = ""; public string Color { get; set; } = "#FF8A00"; public DateTime CreatedAt { get; set; } = DateTime.Now; }
public sealed class CountdownItem { public Guid Id { get; set; } = Guid.NewGuid(); public string Title { get; set; } = "重要日子"; public DateTime Date { get; set; } = DateTime.Today.AddDays(30); public string Color { get; set; } = "#FF8A00"; public bool Annual { get; set; } public string FontFamily { get; set; } = ""; public double FontSize { get; set; } public string FontColor { get; set; } = ""; }
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
    /// <summary>快捷鍵默認值 V2 一次性遷移：顯示/隱藏回歸 Ctrl+Alt+B，截圖翻譯改為 Ctrl+Alt+Q（用戶自定義值不動）</summary>
    public bool HotkeyDefaultsV2Migrated { get; set; }
    public bool UseSharedWidgetBackground { get; set; }
    public bool HeaderPinDefaultMigrated { get; set; }
    /// <summary>已完成引導模式的機器指紋（MachineGuid）清單：每台新電腦首啟觸發一次引導，之後不再觸發；資料目錄搬到新電腦也會重新引導</summary>
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
    /// <summary>截圖默認保存格式（png/jpg/bmp/gif/tiff），覆蓋层內仍可單次切換</summary>
    public string CaptureDefaultFormat { get; set; } = "png";
    /// <summary>保存截圖時同時複製到剪貼板</summary>
    public bool CaptureCopyOnSave { get; set; }
    /// <summary>錄屏默認幀率與默認倒數秒數（錄屏工具條內仍可單次切換）</summary>
    public int RecordingDefaultFps { get; set; } = 30;
    public int RecordingCountdownSec { get; set; }
    /// <summary>隨記保留上限，超出自動刪除最舊</summary>
    public int CaptureLimit { get; set; } = 100;
    /// <summary>天氣自動刷新間隔（分鐘），0 表示不自動刷新</summary>
    public int WeatherRefreshMinutes { get; set; } = 30;
    /// <summary>新建待辦的默認提醒提前量（分鐘，0=準時）</summary>
    public List<int> TodoDefaultReminderOffsets { get; set; } = [1440,0];
    public Dictionary<string,string> Hotkeys { get; set; } = new() { ["Note"]="",["Todo"]="",["MapFolder"]="",["Managed"]="",["CaptureFolder"]="",["QuickNote"]="",["Music"]="",["Clock"]="",["Screenshot"]="Ctrl + Alt + A",["ToggleAll"]="Ctrl + Alt + B",["CollapseAll"]="",["Weather"]="",["PinText"]="Ctrl + Alt + T",["MinimizeTransparent"]="Alt + X",["TranslateScreenshot"]="Ctrl + Alt + Q" };
    public List<string> ToolButtonOrder { get; set; } = [];
    public Dictionary<string,bool> ToolButtonVisibility { get; set; } = [];
    public Dictionary<string,bool> ToolButtonMultiOpen { get; set; } = [];
    public string Language { get; set; } = "zh-TW";
}
