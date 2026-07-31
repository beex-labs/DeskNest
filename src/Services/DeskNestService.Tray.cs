using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace BeeX.DeskNest;

public sealed partial class DeskNestService
{
    static Drawing.Icon LoadTrayIcon()=>App.CreateTrayIcon();
    void Tray_MouseUp(object? sender,Forms.MouseEventArgs e)
    {
        if(e.Button==Forms.MouseButtons.Right)System.Windows.Application.Current.Dispatcher.Invoke(ShowTrayMenu);
        else if(e.Button==Forms.MouseButtons.Left)System.Windows.Application.Current.Dispatcher.Invoke(ShowControl);
    }
    void ApplyTrayTheme(){if(trayMenu!=null)StyleTrayMenu(trayMenu);}
    void ShowTrayMenu()
    {
        trayMenu?.IsOpen=false;
        var menu=BuildTrayMenu();
        StyleTrayMenu(menu);
        trayMenu=menu;
        menu.Closed+=(_,_)=>{if(ReferenceEquals(trayMenu,menu))trayMenu=null;};
        ShowTrayContextMenu(menu);
    }
    // The WPF context menu for the tray must first bring one of this process's windows to the foreground, otherwise clicking outside the menu will not close it automatically.
    public void ShowTrayContextMenu(System.Windows.Controls.ContextMenu menu)
    {
        var handle=EnsureMenuActivator();
        if(handle!=IntPtr.Zero)SetForegroundWindow(handle);
        menu.PlacementTarget=menuActivator;
        menu.Placement=System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen=true;
    }
    IntPtr EnsureMenuActivator()
    {
        if(menuActivator==null)
        {
            menuActivator=new Window{Width=1,Height=1,Left=-32000,Top=-32000,WindowStyle=WindowStyle.None,ShowInTaskbar=false,ShowActivated=false,AllowsTransparency=true,Background=System.Windows.Media.Brushes.Transparent,ResizeMode=ResizeMode.NoResize,Topmost=true};
            menuActivator.Show();
        }
        return new WindowInteropHelper(menuActivator).Handle;
    }
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
    System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        string L(string zhTw,string zhCn,string en)=>State.Language=="zh-CN"?zhCn:State.Language=="en-US"?en:zhTw;
        var menu=new System.Windows.Controls.ContextMenu();
        System.Windows.Controls.MenuItem Item(string text,Action action){var item=new System.Windows.Controls.MenuItem{Header=text};item.Click+=(_,_)=>{menu.IsOpen=false;action();};return item;}
        void Header(string text){menu.Items.Add(new System.Windows.Controls.MenuItem{Header=text,IsEnabled=false,FontWeight=FontWeights.SemiBold});}
        menu.Items.Add(Item(L("開啟控制台","打开控制台","Open dashboard"),ShowControl));
        menu.Items.Add(Item(L("設定","设置","Settings"),ShowSettings));
        menu.Items.Add(new System.Windows.Controls.Separator());
        Header(L("快速操作","快速操作","Quick actions"));
        menu.Items.Add(Item(L("一鍵整理桌面布局","一键整理桌面布局","Arrange desktop layout"),ArrangeDesktopLayout));
        menu.Items.Add(Item(L("快速啟動    Ctrl+Q","快速启动    Ctrl+Q","Quick launcher    Ctrl+Q"),ShowSearchPalette));
        menu.Items.Add(Item(L("顯示 / 隱藏全部","显示 / 隐藏全部","Show / hide all"),ToggleAll));
        menu.Items.Add(Item(L("摺疊 / 展開全部","折叠 / 展开全部","Collapse / expand all"),ToggleCollapseAll));
        menu.Items.Add(Item(L("視窗透明","窗口透明","Window transparency"),ShowWindowTransparency));
        menu.Items.Add(Item(L("系統清理","系统清理","System cleaner"),ShowCleaner));
        menu.Items.Add(Item(L("桌面壁紙","桌面壁纸","Live wallpaper"),ShowWallpaperGallery));
        menu.Items.Add(new System.Windows.Controls.Separator());
        Header(L("新增格子","新增格子","New widget"));
        menu.Items.Add(Item(L("便箋","便笺","Note"),()=>Add(NestKind.Note)));
        menu.Items.Add(Item(L("待辦","待办","Todo"),()=>Add(NestKind.Todo)));
        menu.Items.Add(Item(L("隨記","随记","Journal"),()=>Add(NestKind.Capture)));
        menu.Items.Add(Item(L("音樂","音乐","Music"),()=>Add(NestKind.Music)));
        menu.Items.Add(Item(L("時鐘","时钟","Clock"),()=>Add(NestKind.Clock)));
        menu.Items.Add(Item(L("天氣","天气","Weather"),()=>Add(NestKind.Weather)));
        menu.Items.Add(Item(L("收納格子","收纳格子","Storage box"),AddManagedFiles));
        menu.Items.Add(Item(L("映射資料夾","映射文件夹","Map folder"),AddFolder));
        menu.Items.Add(Item(L("標籤","标签","Tags"),()=>Add(NestKind.Tags)));
        menu.Items.Add(Item(L("日程倒數","日程倒数","Countdown"),()=>Add(NestKind.Countdown)));
        menu.Items.Add(Item(L("上下班提醒","上下班提醒","Work timer"),()=>Add(NestKind.WorkTimer)));
        menu.Items.Add(Item(L("系統監控","系统监控","System monitor"),()=>Add(NestKind.SystemMonitor)));
        menu.Items.Add(new System.Windows.Controls.Separator());
        Header(L("截圖與資料夾","截图与文件夹","Capture & folders"));
        menu.Items.Add(Item(L("立即截圖    Ctrl+Alt+A","立即截图    Ctrl+Alt+A","Region capture    Ctrl+Alt+A"),()=>CaptureScreen()));
        menu.Items.Add(Item(L("開啟截圖文件夾","打开截图文件夹","Open screenshots folder"),OpenCaptureFolder));
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(Item(L("結束 BeeX DeskNest","退出 BeeX DeskNest","Exit BeeX DeskNest"),Exit));
        return menu;
    }
    void StyleTrayMenu(System.Windows.Controls.ContextMenu menu)
    {
        var dark=State.Theme=="Dark";var honey=State.Theme=="Honey";
        menu.Background=dark?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(22,29,45)):honey?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,244,222)):new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250,251,252));
        menu.Foreground=dark?System.Windows.Media.Brushes.White:new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13,19,33));
        menu.BorderBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(120,255,138,0));
    }
    void ApplyTrayLanguage(string lang){if(tray?.ContextMenuStrip==null)return;var entries=new[]{("開啟控制台","打开控制台","Open dashboard"),("設定","设置","Settings"),("新增便箋","新增便笺","New note"),("新增待辦","新增待办","New todo"),("映射資料夾","映射文件夹","Map folder"),("新增隨記","新增随记","New journal"),("新增音樂","新增音乐","New music"),("新增時鐘","新增时钟","New clock"),("新增收納格子","新增收纳格子","New storage box"),("新增天氣","新增天气","New weather"),("區域截圖    Ctrl+Alt+A","区域截图    Ctrl+Alt+A","Region capture    Ctrl+Alt+A"),("顯示 / 隱藏全部","显示 / 隐藏全部","Show / hide all"),("結束 BeeX DeskNest","退出 BeeX DeskNest","Exit BeeX DeskNest")};foreach(System.Windows.Forms.ToolStripItem item in tray.ContextMenuStrip.Items){var e=entries.FirstOrDefault(x=>x.Item1==item.Text||x.Item2==item.Text||x.Item3==item.Text);if(e!=default)item.Text=lang=="zh-CN"?e.Item2:lang=="en-US"?e.Item3:e.Item1;}}
}

sealed class HotkeyWindow : NativeWindow, IDisposable
{
    const int WM_HOTKEY=0x0312;const uint MOD_ALT=1,MOD_CONTROL=2,MOD_SHIFT=4,MOD_WIN=8;readonly Dictionary<int,Action> actions=[];readonly List<int> registered=[];
    [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd,int id,uint mods,uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd,int id);
    public HotkeyWindow(Dictionary<string,(string Shortcut,Action Action)> commands) {CreateHandle(new System.Windows.Forms.CreateParams());var id=1;foreach(var command in commands.Values){if(TryParse(command.Shortcut,out var modifiers,out var key)&&RegisterHotKey(Handle,id,modifiers,(uint)key)){actions[id]=command.Action;registered.Add(id);id++;}}}
    static bool TryParse(string shortcut,out uint modifiers,out Forms.Keys key){modifiers=0;key=Forms.Keys.None;if(string.IsNullOrWhiteSpace(shortcut))return false;var parts=shortcut.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries);foreach(var part in parts[..^1])modifiers|=part.ToLowerInvariant() switch{"ctrl"=>MOD_CONTROL,"alt"=>MOD_ALT,"shift"=>MOD_SHIFT,"win"=>MOD_WIN,_=>0};var keyName=parts[^1]=="Return"?"Enter":parts[^1];if(!Enum.TryParse(keyName,true,out key))return false;var functionKey=key>=Forms.Keys.F1&&key<=Forms.Keys.F12;return modifiers!=0||functionKey;}
    protected override void WndProc(ref System.Windows.Forms.Message m) {if(m.Msg==WM_HOTKEY&&actions.TryGetValue(m.WParam.ToInt32(),out var action))action();base.WndProc(ref m);}
    public void Dispose() {foreach(var id in registered)UnregisterHotKey(Handle,id);DestroyHandle();}
}

sealed class BeeXTrayColorTable(System.Drawing.Color background,System.Drawing.Color foreground):System.Windows.Forms.ProfessionalColorTable
{
    readonly System.Drawing.Color orange=System.Drawing.Color.FromArgb(255,138,0);
    public override System.Drawing.Color ToolStripDropDownBackground=>background;
    public override System.Drawing.Color ImageMarginGradientBegin=>background;
    public override System.Drawing.Color ImageMarginGradientMiddle=>background;
    public override System.Drawing.Color ImageMarginGradientEnd=>background;
    public override System.Drawing.Color MenuItemSelected=>orange;
    public override System.Drawing.Color MenuItemBorder=>orange;
    public override System.Drawing.Color MenuItemSelectedGradientBegin=>orange;
    public override System.Drawing.Color MenuItemSelectedGradientEnd=>orange;
    public override System.Drawing.Color SeparatorDark=>System.Drawing.Color.FromArgb(foreground.A/2,foreground);
    public override System.Drawing.Color SeparatorLight=>background;
    public override System.Drawing.Color ToolStripBorder=>System.Drawing.Color.FromArgb(255,138,0);
}
