using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace BeeX.DeskNest;

static class WindowRegionHelper
{
    const int GwlExStyle=-20;
    const long WsExToolWindow=0x00000080L;
    const long WsExAppWindow=0x00040000L;
    const long WsExWindowEdge=0x00000100L;
    // DWM window attribute: disabling NC rendering removes the square system shadow triggered by WindowChrome
    const int DWMWA_NCRENDERING_POLICY=2;
    const int DWMNCRP_DISABLED=2;
    const int DWMWA_WINDOW_CORNER_PREFERENCE=33;
    const int DWMWCP_ROUND=2;
    const int DWMWCP_DONOTROUND=1;
    const int DWMWA_BORDER_COLOR=34;
    [DllImport("gdi32.dll")] static extern IntPtr CreateRoundRectRgn(int left,int top,int right,int bottom,int widthEllipse,int heightEllipse);
    [DllImport("user32.dll")] static extern int SetWindowRgn(IntPtr hwnd,IntPtr region,bool redraw);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr64(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr64(IntPtr hwnd,int index,IntPtr value);
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")] static extern int GetWindowLong32(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongW")] static extern int SetWindowLong32(IntPtr hwnd,int index,int value);
    [DllImport("dwmapi.dll",PreserveSig=false)] static extern void DwmSetWindowAttribute(IntPtr hwnd,int attr,ref int value,int cbAttribute);

    public static void DisableSystemShadow(Window window)
    {
        var hwnd=new WindowInteropHelper(window).Handle;
        if(hwnd==IntPtr.Zero)return;
        try
        {
            var policy=DWMNCRP_DISABLED;
            DwmSetWindowAttribute(hwnd,DWMWA_NCRENDERING_POLICY,ref policy,sizeof(int));
            // Tell DWM not to add its own rounded corners/system shadow to the window rectangle -- the content already has visual rounding from the Border,
            // otherwise the square right angles of DWM's system shadow poke out beyond the rounded content (the collapsed widget's "extra corners", the floating window's square shadow).
            var corner=DWMWCP_DONOTROUND;
            DwmSetWindowAttribute(hwnd,DWMWA_WINDOW_CORNER_PREFERENCE,ref corner,sizeof(int));
            // Set the DWM border color to "none" so Win11 does not add a 1px gray outline around the WindowChrome edge
            var borderColor=unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            try{DwmSetWindowAttribute(hwnd,DWMWA_BORDER_COLOR,ref borderColor,sizeof(int));}catch{}
        }
        catch{}
    }

    public static void HideFromAltTab(Window window)
    {
        var hwnd=new WindowInteropHelper(window).Handle;
        if(hwnd==IntPtr.Zero)return;
        if(IntPtr.Size==8){var style=GetWindowLongPtr64(hwnd,GwlExStyle).ToInt64();style=(style|WsExToolWindow)&~WsExAppWindow&~WsExWindowEdge;SetWindowLongPtr64(hwnd,GwlExStyle,new IntPtr(style));}
        else{var style=GetWindowLong32(hwnd,GwlExStyle);style=(style|(int)WsExToolWindow)&~(int)WsExAppWindow&~(int)WsExWindowEdge;SetWindowLong32(hwnd,GwlExStyle,style);}
    }

    public static void Apply(Window window,double radius)
    {
        if(!window.IsLoaded||window.ActualWidth<=0||window.ActualHeight<=0)return;
        ApplyVisualClip(window,radius);
        // A transparent window (AllowsTransparency) gets perfect rounded corners just from the Border's visual rounding;
        // applying a GDI rounded region (SetWindowRgn) on top instead exposes small square tips at the four corners because the curve and the +1 size do not match.
        if(window.AllowsTransparency)return;
        var dpi=VisualTreeHelper.GetDpi(window);
        var width=Math.Max(1,(int)Math.Ceiling(window.ActualWidth*dpi.DpiScaleX));
        var height=Math.Max(1,(int)Math.Ceiling(window.ActualHeight*dpi.DpiScaleY));
        var diameter=Math.Max(1,(int)Math.Round(radius*2*Math.Max(dpi.DpiScaleX,dpi.DpiScaleY)));
        // The right/bottom coordinates of GDI CreateRoundRectRgn are exclusive; +1 to avoid clipping the 1px outline on the right and bottom edges
        var region=CreateRoundRectRgn(0,0,width+1,height+1,diameter,diameter);
        if(region==IntPtr.Zero)return;
        if(SetWindowRgn(new WindowInteropHelper(window).Handle,region,true)==0)DeleteObject(region);
    }

    static void ApplyVisualClip(Window window,double radius)
    {
        // A transparent window (AllowsTransparency) gets perfect rounded clipping just from Border.CornerRadius;
        // a RectangleGeometry Clip uses a different anti-aliasing algorithm than the Border and can never align at the pixel level,
        // instead causing corner bumps or content leakage -- so skip it entirely.
        if(window.AllowsTransparency)return;
        if(window.Content is not Border border||border.ActualWidth<=0||border.ActualHeight<=0)return;
        border.Clip=new RectangleGeometry(new Rect(0,0,border.ActualWidth,border.ActualHeight),Math.Max(0,radius),Math.Max(0,radius));
    }

    public static void ApplyDeferred(Window window,double radius)
    {
        Apply(window,radius);
        window.Dispatcher.BeginInvoke(DispatcherPriority.Render,new Action(()=>Apply(window,radius)));
        window.Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>Apply(window,radius)));
    }

    public static void StyleCaptionButtons(DependencyObject root)
        => StyleCaptionButtons(root, root is Window window ? window.Foreground : Brushes.White);

    public static void AddResizeHitTest(Window window,double zone=36)
    {
        if(new WindowInteropHelper(window).Handle==IntPtr.Zero)return;
        HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook((IntPtr hwnd,int msg,IntPtr wParam,IntPtr lParam,ref bool handled)=>
        {
            const int WM_NCHITTEST=0x0084,HTBOTTOMRIGHT=17;
            if(msg!=WM_NCHITTEST||window.ResizeMode==ResizeMode.NoResize)return IntPtr.Zero;
            var raw=lParam.ToInt64();
            var point=window.PointFromScreen(new System.Windows.Point((short)(raw&0xffff),(short)((raw>>16)&0xffff)));
            if(point.X>=window.ActualWidth-zone&&point.Y>=window.ActualHeight-zone){handled=true;return (IntPtr)HTBOTTOMRIGHT;}
            return IntPtr.Zero;
        });
    }

    static void StyleCaptionButtons(DependencyObject root, Brush foreground)
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is System.Windows.Controls.Button button&&button.Content is string content&&(content=="-"||content=="−"||content=="×"||content=="✕"||content=="\uE921"||content=="\uE8BB"))
            {
                var isMinimize=content is "-" or "−" or "\uE921";
                button.Width=40;button.Height=40;button.Padding=new Thickness(0);button.Margin=new Thickness(0);
                button.BorderThickness=new Thickness(0);button.HorizontalContentAlignment=System.Windows.HorizontalAlignment.Center;
                button.VerticalContentAlignment=VerticalAlignment.Center;button.FontFamily=new System.Windows.Media.FontFamily("Segoe UI Symbol");
                button.FontSize=isMinimize?16:14;button.Content=isMinimize?"−":"×";button.Background=Brushes.Transparent;button.Foreground=foreground;
            }
            StyleCaptionButtons(child,foreground);
        }
    }
}
