using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace BeeX.DeskNest;

public sealed partial class DeskNestService
{
    public void Save()
    {
        SanitizeState();
        Directory.CreateDirectory(dataDir); var tmp = StateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(State, json)); File.Move(tmp, StateFile, true);
    }
    public void SaveSoon()
    {
        saveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        saveTimer.Tick -= SaveTimer_Tick;
        saveTimer.Tick += SaveTimer_Tick;
        saveTimer.Stop();
        saveTimer.Start();
    }
    void SaveTimer_Tick(object? sender,EventArgs e){saveTimer?.Stop();Save();}
    void Load() { var migrated=false;try { if (File.Exists(StateFile)) State = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StateFile), json) ?? new();State.Nests.RemoveAll(n=>n.Kind==NestKind.Deadline);State.Nests.RemoveAll(n=>n.IsEasterEggTemp||(n.Kind==NestKind.Note&&string.IsNullOrWhiteSpace(n.Content)&&(n.Title.StartsWith("蜂巢平台",StringComparison.Ordinal)||n.Title.StartsWith("Hive platform",StringComparison.Ordinal))));// 清掃彩蛋臨時平台殘留（含早期無標記版本）：遊戲中進程被殺/崩潰時 SaveSoon 可能已把它們寫進存檔// 清理无效 Id 并按 Id 去重，避免 Open() 早返回导致部分组件永远没有窗口实例
foreach(var nest in State.Nests)if(nest.Id==Guid.Empty)nest.Id=Guid.NewGuid();State.Nests=State.Nests.GroupBy(n=>n.Id).Select(g=>g.First()).ToList();if(string.IsNullOrWhiteSpace(State.GlobalFontFamily)){State.GlobalFontFamily="Microsoft JhengHei UI";migrated=true;}if(string.IsNullOrWhiteSpace(State.GlobalFontColor)){State.GlobalFontColor=State.Theme=="Dark"?"#FFFFFF":"#0D1321";migrated=true;}if(string.IsNullOrWhiteSpace(State.InterfaceFontFamily)){State.InterfaceFontFamily=State.GlobalFontFamily;migrated=true;}if(State.InterfaceFontSize<=0){State.InterfaceFontSize=State.GlobalFontSize;migrated=true;}if(string.IsNullOrWhiteSpace(State.ContentFontFamily)){State.ContentFontFamily=State.GlobalFontFamily;migrated=true;}if(State.ContentFontSize<=0){State.ContentFontSize=State.GlobalFontSize;migrated=true;}if(string.IsNullOrWhiteSpace(State.ThemePreset)){State.ThemePreset=State.Theme=="Dark"?"Dark":"Clear";migrated=true;}if(State.FloatingBallOpacity<=0){State.FloatingBallOpacity=State.WidgetOpacity;migrated=true;}if(MigrateDefaultTitles())migrated=true;if(!State.Hotkeys.ContainsKey("MinimizeTransparent")){State.Hotkeys["MinimizeTransparent"]="Alt + X";migrated=true;}if(!State.HotkeyDefaultsV2Migrated){var ta=State.Hotkeys.GetValueOrDefault("ToggleAll","");if(string.IsNullOrWhiteSpace(ta)||ta=="Ctrl + Alt + Q")State.Hotkeys["ToggleAll"]="Ctrl + Alt + B";var ts=State.Hotkeys.GetValueOrDefault("TranslateScreenshot","");if(string.IsNullOrWhiteSpace(ts)||ts=="Ctrl + Alt + S")State.Hotkeys["TranslateScreenshot"]="Ctrl + Alt + Q";State.HotkeyDefaultsV2Migrated=true;migrated=true;}if(!State.HeaderPinDefaultMigrated){foreach(var nest in State.Nests)nest.HeaderPinned=false;State.HeaderPinDefaultMigrated=true;migrated=true;}State.WidgetOpacity=Math.Clamp(State.WidgetOpacity,0,1);foreach(var nest in State.Nests){nest.Opacity=State.WidgetOpacity;nest.FontFamily=ContentFontFamily();nest.FontSize=nest.Kind==NestKind.WorkTimer?Math.Max(20,ContentFontSize()):ContentFontSize();nest.FontColor=State.GlobalFontColor;}SanitizeState(); } catch { State = new(){HeaderPinDefaultMigrated=true};migrated=true; }Localization.CurrentLanguage=State.Language;if(migrated)Save(); }
    void SanitizeState()
    {
        State.WidgetOpacity=Finite(State.WidgetOpacity,.5,0,1);
        State.GlobalFontSize=Finite(State.GlobalFontSize,14,10,36);
        State.InterfaceFontSize=Finite(State.InterfaceFontSize>0?State.InterfaceFontSize:State.GlobalFontSize,State.GlobalFontSize,10,28);
        State.ContentFontSize=Finite(State.ContentFontSize>0?State.ContentFontSize:State.GlobalFontSize,State.GlobalFontSize,10,36);
        State.CornerRadius=Finite(State.CornerRadius,18,0,48);
        State.IconSize=Finite(State.IconSize,30,12,96);
        State.ItemSpacing=Finite(State.ItemSpacing,10,0,48);
        State.WidgetGridSize=Finite(State.WidgetGridSize,20,10,100);
        State.FloatingBallOpacity=Finite(State.FloatingBallOpacity>0?State.FloatingBallOpacity:State.WidgetOpacity,State.WidgetOpacity,.2,1);
        State.FloatingBallLeft=double.IsFinite(State.FloatingBallLeft)?State.FloatingBallLeft:-1;
        State.FloatingBallTop=double.IsFinite(State.FloatingBallTop)?State.FloatingBallTop:-1;
        foreach(var nest in State.Nests)
        {
            nest.Left=Finite(nest.Left,80,-20000,20000);
            nest.Top=Finite(nest.Top,80,-20000,20000);
            nest.Width=Finite(nest.Width,340,120,5000);
            nest.Height=Finite(nest.Height,300,64,5000);
            nest.Opacity=Finite(nest.Opacity,State.WidgetOpacity,0,1);
            nest.FontSize=Finite(nest.FontSize,State.GlobalFontSize,10,48);
            nest.Latitude=Finite(nest.Latitude,22.5431,-90,90);
            nest.Longitude=Finite(nest.Longitude,114.0579,-180,180);
            nest.MusicLyricsOffsetSeconds=Finite(nest.MusicLyricsOffsetSeconds,0,-30,30);
            foreach(var item in nest.Countdowns)item.FontSize=Finite(item.FontSize,0,0,60);
        }
    }
    internal static double Finite(double value,double fallback,double min,double max)=>double.IsFinite(value)?Math.Clamp(value,min,max):fallback;
    bool MigrateDefaultTitles()
    {
        var changed=false;
        var map=new Dictionary<NestKind,Dictionary<string,string>>
        {
            [NestKind.Note]=new(){["BeeX 便箋"]="便箋",["靈感便箋"]="便箋",["靈感箋"]="便箋"},
            [NestKind.Todo]=new(){["今日待辦"]="待辦",["BeeX 待辦"]="待辦",["今日任務板"]="待辦",["任務板"]="待辦"},
            [NestKind.Capture]=new(){["Capture"]="隨記",["Quick Capture"]="隨記",["拾光記"]="隨記"},
            [NestKind.Music]=new(){["音樂控制"]="音樂",["系統媒體控制"]="音樂",["聲音卡"]="音樂"},
            [NestKind.Clock]=new(){["現在時間"]="時鐘",["時刻牌"]="時鐘"},
            [NestKind.Weather]=new(){["BeeX 天氣"]="天氣",["氣象牌"]="天氣"},
            [NestKind.Tags]=new(){["星標集"]="標籤"},
            [NestKind.SystemMonitor]=new(){["脈衝儀表"]="系統監控"},
            [NestKind.Countdown]=new(){["倒數日"]="日程倒數"},
            [NestKind.Launcher]=new(){["星門搜尋"]="快速啟動"},
            [NestKind.WorkTimer]=new(){["牛馬下班鐘"]="上下班提醒",["下班羅盤"]="上下班提醒",["牛馬班表"]="上下班提醒"}
        };
        foreach(var nest in State.Nests)
        {
            if(map.TryGetValue(nest.Kind,out var names)&&names.TryGetValue(nest.Title,out var next)&&nest.Title!=next){nest.Title=next;changed=true;}
        }
        return changed;
    }
}
