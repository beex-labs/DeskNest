using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfBrushes=System.Windows.Media.Brushes;
using WpfColor=System.Windows.Media.Color;
using WpfContextMenu=System.Windows.Controls.ContextMenu;
using WpfImage=System.Windows.Controls.Image;
using WpfMenuItem=System.Windows.Controls.MenuItem;

namespace BeeX.DeskNest;

    public sealed class FloatingBallWindow : Window
    {
        const double BallSize=58;
        const double ShadowPad=20;
        const double WinSize=BallSize+ShadowPad*2;
        readonly DeskNestService service;
        readonly Border shell;
        readonly System.Windows.Threading.DispatcherTimer singleClickTimer=new(){Interval=TimeSpan.FromMilliseconds(230)};
        System.Windows.Point dragStart;
        bool dragging;
        bool suppressClickOpen;

        [DllImport("gdi32.dll")] static extern IntPtr CreateEllipticRgn(int x1,int y1,int x2,int y2);

    public FloatingBallWindow(DeskNestService service)
    {
        this.service=service;
        Width=WinSize;Height=WinSize;MinWidth=WinSize;MinHeight=WinSize;
        WindowStyle=WindowStyle.None;
        AllowsTransparency=true;
        Background=WpfBrushes.Transparent;
        ResizeMode=ResizeMode.NoResize;
        ShowInTaskbar=false;
        Topmost=true;
        Focusable=false;
        var logo=new WpfImage{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")),Width=34,Height=34,Stretch=Stretch.Uniform};
        shell=new Border{Width=BallSize,Height=BallSize,HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,CornerRadius=new CornerRadius(BallSize/2),Background=new SolidColorBrush(WpfColor.FromArgb(212,13,19,33)),BorderBrush=new SolidColorBrush(WpfColor.FromArgb(150,255,138,0)),BorderThickness=new Thickness(1),Child=logo};
        shell.Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=18,ShadowDepth=4,Opacity=.28,Color=WpfColor.FromRgb(13,19,33)};
        // 外層透明邊距讓圓形陰影完整鋪開（不再被方形視窗裁成方形陰影）；空白邊距 Background=null 點擊穿透
        Content=new Grid{Background=WpfBrushes.Transparent,Children={shell}};
        SourceInitialized+=(_,_)=>{WindowRegionHelper.HideFromAltTab(this);WindowRegionHelper.DisableSystemShadow(this);ApplyCircularRegion();};
        Loaded+=(_,_)=>{WindowRegionHelper.DisableSystemShadow(this);ApplyCircularRegion();Place();ApplyPreferences();};
        singleClickTimer.Tick+=(_,_)=>{singleClickTimer.Stop();service.ShowControl();};
        MouseLeftButtonDown+=OnLeftDown;
        MouseMove+=OnMove;
        MouseLeftButtonUp+=OnLeftUp;
        MouseRightButtonUp+=(_,e)=>{ShowMenu();e.Handled=true;};
    }

    /// <summary>
    /// 使用 GDI 橢圓區域讓 DWM 系統陰影呈圓形（配合 AllowsTransparency 透明背景）。
    /// SetWindowRgn 會接管 hRgn 所有權，無需手動 DeleteObject。
    /// </summary>
    void ApplyCircularRegion()
    {
        var hwnd=new WindowInteropHelper(this).Handle;
        if(hwnd==IntPtr.Zero)return;
        var dpi=VisualTreeHelper.GetDpi(this);
        var px=(int)Math.Ceiling(WinSize*Math.Max(dpi.DpiScaleX,dpi.DpiScaleY));
        var hRgn=CreateEllipticRgn(0,0,px,px);
        if(hRgn!=IntPtr.Zero)
            SetWindowRgn(hwnd,hRgn,true);
    }

    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hWnd,IntPtr hRgn,bool bRedraw);

    void Place()
    {
        var work=SystemParameters.WorkArea;
        Left=!double.IsFinite(service.State.FloatingBallLeft)||service.State.FloatingBallLeft<0?work.Right-Width-28:Math.Clamp(service.State.FloatingBallLeft,work.Left,work.Right-Width);
        Top=!double.IsFinite(service.State.FloatingBallTop)||service.State.FloatingBallTop<0?work.Bottom-Height-96:Math.Clamp(service.State.FloatingBallTop,work.Top,work.Bottom-Height);
    }

    void OnLeftDown(object sender,MouseButtonEventArgs e)
    {
        dragStart=e.GetPosition(this);
        dragging=false;
        CaptureMouse();
        if(e.ClickCount>=2){singleClickTimer.Stop();suppressClickOpen=true;service.ToggleAll();e.Handled=true;}
    }

    void OnMove(object sender,System.Windows.Input.MouseEventArgs e)
    {
        if(e.LeftButton!=MouseButtonState.Pressed||!IsMouseCaptured)return;
        var p=e.GetPosition(this);
        var delta=p-dragStart;
        if(Math.Abs(delta.X)+Math.Abs(delta.Y)<3)return;
        dragging=true;
        Left+=delta.X;
        Top+=delta.Y;
        SnapInsideWorkArea();
    }

    void OnLeftUp(object sender,MouseButtonEventArgs e)
    {
        if(IsMouseCaptured)ReleaseMouseCapture();
        if(dragging)
        {
            SnapToEdgeIfNeeded();
            service.State.FloatingBallLeft=Left;
            service.State.FloatingBallTop=Top;
            service.Save();
        }
        else if(!suppressClickOpen&&e.ClickCount<2){singleClickTimer.Stop();singleClickTimer.Start();}
        dragging=false;
        suppressClickOpen=false;
        e.Handled=true;
    }

    void SnapInsideWorkArea()
    {
        var work=SystemParameters.WorkArea;
        Left=Math.Clamp(Left,work.Left,work.Right-Width);
        Top=Math.Clamp(Top,work.Top,work.Bottom-Height);
    }

    void SnapToEdgeIfNeeded()
    {
        if(!service.State.FloatingBallSnapToEdge)return;
        var work=SystemParameters.WorkArea;
        var leftDistance=Math.Abs(Left-work.Left);
        var rightDistance=Math.Abs(work.Right-(Left+Width));
        Left=leftDistance<=rightDistance?work.Left+8:work.Right-Width-8;
        Top=Math.Clamp(Top,work.Top+8,work.Bottom-Height-8);
    }

    public void ApplyPreferences()
    {
        Topmost=true;
        Opacity=1;
        var dark=service.State.Theme=="Dark";
        var honey=service.State.Theme=="Honey";
        var alpha=(byte)Math.Clamp(service.EffectiveFloatingBallOpacity()*255,45,255);
        var background=dark?WpfColor.FromArgb(alpha,13,19,33):honey?WpfColor.FromArgb(alpha,255,244,222):WpfColor.FromArgb(alpha,245,247,250);
        shell.Background=new SolidColorBrush(background);
        shell.BorderBrush=new SolidColorBrush(WpfColor.FromArgb(150,255,138,0));
    }

    void ShowMenu()
    {
        string T(string text)=>Localization.T(text,service.State.Language);
        var menu=new WpfContextMenu();
        WpfMenuItem Item(string text,Action action){var item=new WpfMenuItem{Header=T(text)};item.Click+=(_,_)=>{menu.IsOpen=false;action();};return item;}
        StyleMenu(menu);
        menu.Items.Add(Item("主控制台",service.ShowControl));
        menu.Items.Add(Item("設定",service.ShowSettings));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("一鍵整理桌面布局",service.ArrangeDesktopLayout));
        menu.Items.Add(Item("顯示 / 隱藏全部",service.ToggleAll));
        menu.Items.Add(Item("摺疊／展開全部",service.ToggleCollapseAll));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("立即截圖",()=>service.CaptureScreen()));
        menu.Items.Add(Item("視窗透明",service.ShowWindowTransparency));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("今日關閉懸浮球",service.HideFloatingBallForToday));
        menu.Items.Add(Item("關閉懸浮球",()=>service.SetFloatingBallVisible(false)));
        menu.PlacementTarget=this;
        menu.Placement=System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen=true;
    }

    void StyleMenu(WpfContextMenu menu)
    {
        var dark=service.State.Theme=="Dark";
        var honey=service.State.Theme=="Honey";
        menu.Background=dark?new SolidColorBrush(WpfColor.FromRgb(22,29,45)):honey?new SolidColorBrush(WpfColor.FromRgb(255,244,222)):new SolidColorBrush(WpfColor.FromRgb(250,251,252));
        menu.Foreground=dark?WpfBrushes.White:new SolidColorBrush(WpfColor.FromRgb(13,19,33));
        menu.BorderBrush=new SolidColorBrush(WpfColor.FromArgb(120,255,138,0));
    }
}
