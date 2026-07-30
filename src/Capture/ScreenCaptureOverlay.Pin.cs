using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WpfRectangle=System.Windows.Shapes.Rectangle;
using WpfContextMenu=System.Windows.Controls.ContextMenu;
using WpfMenuItem=System.Windows.Controls.MenuItem;
using WpfSeparator=System.Windows.Controls.Separator;
using Brushes=System.Windows.Media.Brushes;
using Cursors=System.Windows.Input.Cursors;
using Color=System.Windows.Media.Color;
using Orientation=System.Windows.Controls.Orientation;

namespace BeeX.DeskNest;

sealed partial class ScreenCaptureOverlay
{
    void Pin()
    {
        if(pinned)return;
        if(selectionRect.Width<4||selectionRect.Height<4)return;
        pinned=true;
        annotationTool=AnnotationTool.Select;
        activeAnnotation=null;
        ClearAnnResizer();
        HideLoupe();HideBrushRing();
        selection.Visibility=Visibility.Collapsed;
        dimMask.Visibility=Visibility.Collapsed;
        sizeTip.Visibility=Visibility.Collapsed;
        sideBar.Visibility=Visibility.Collapsed;
        foreach(var h in handles)h.Visibility=Visibility.Collapsed;
        canvas.Background=Brushes.Transparent;
        Cursor=Cursors.Arrow;
        var imgW=selectionRect.Width;var imgH=selectionRect.Height;
        canvas.Clip=new RectangleGeometry(new Rect(selectionRect.X,selectionRect.Y,imgW,imgH));
        Canvas.SetLeft(canvas,-selectionRect.X);
        Canvas.SetTop(canvas,-selectionRect.Y);
        Left=SystemParameters.VirtualScreenLeft+selectionRect.X;
        Top=SystemParameters.VirtualScreenTop+selectionRect.Y;
        Width=imgW;Height=imgH;
        pinGlow=new WpfRectangle{Width=imgW,Height=imgH,Stroke=new SolidColorBrush(Color.FromRgb(255,138,0)),Fill=null,IsHitTestVisible=false};
        Canvas.SetLeft(pinGlow,0);Canvas.SetTop(pinGlow,0);
        System.Windows.Controls.Panel.SetZIndex(pinGlow,60);
        outerCanvas.Children.Add(pinGlow);
        ApplyPinEdge();
        outerCanvas.Children.Remove(toolbar);
        outerCanvas.Children.Remove(secondaryBar);
        toolbar.HorizontalAlignment=System.Windows.HorizontalAlignment.Left;
        secondaryBar.HorizontalAlignment=System.Windows.HorizontalAlignment.Left;
        secondaryBar.Margin=new Thickness(0,6,0,0);
        var col=new StackPanel{Orientation=Orientation.Vertical};
        col.Children.Add(toolbar);
        col.Children.Add(secondaryBar);
        pinBar=new Window{WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Topmost=true,AllowsTransparency=true,Background=Brushes.Transparent,SizeToContent=SizeToContent.WidthAndHeight,ShowActivated=false,Content=col};
        toolbar.Visibility=Visibility.Visible;
        UpdateToolHighlights();
        RefreshSecondaryBar();
        pinBar.Show();
        PositionPinBar();
    }

    void PositionPinBar()
    {
        if(pinBar==null||!pinBar.IsVisible)return;
        pinBar.UpdateLayout();
        var bw=pinBar.ActualWidth;var bh=pinBar.ActualHeight;
        var imgW=selectionRect.Width*pinScale;var imgH=selectionRect.Height*pinScale;
        var vsL=SystemParameters.VirtualScreenLeft;var vsT=SystemParameters.VirtualScreenTop;
        var vsR=vsL+SystemParameters.VirtualScreenWidth;var vsB=vsT+SystemParameters.VirtualScreenHeight;
        var x=Left+(imgW-bw)/2;
        var y=Top+imgH+8;
        if(y+bh>vsB)y=Top-bh-8;
        x=Math.Max(vsL+2,Math.Min(x,vsR-bw-2));
        y=Math.Max(vsT+2,Math.Min(y,vsB-bh-2));
        pinBar.Left=x;pinBar.Top=y;
    }

    void ZoomPin(int delta)
    {
        var ns=Math.Clamp(Math.Round((pinScale+(delta>0?0.1:-0.1))*100)/100,0.1,5.0);
        if(Math.Abs(ns-pinScale)<0.001)return;
        var imgW=selectionRect.Width;var imgH=selectionRect.Height;
        var oldW=imgW*pinScale;var oldH=imgH*pinScale;
        var newW=imgW*ns;var newH=imgH*ns;
        Left-=(newW-oldW)/2;Top-=(newH-oldH)/2;
        Width=newW;Height=newH;
        pinScale=ns;
        outerCanvas.RenderTransform=new ScaleTransform(ns,ns);
        PositionPinBar();
    }

    void ApplyPinEdge()
    {
        if(pinGlow==null)return;
        switch(pinEdge)
        {
            case PinEdge.Glow:pinGlow.Visibility=Visibility.Visible;pinGlow.StrokeThickness=4;pinGlow.Effect=new System.Windows.Media.Effects.BlurEffect{Radius=16};break;
            case PinEdge.Border:pinGlow.Visibility=Visibility.Visible;pinGlow.StrokeThickness=3;pinGlow.Effect=null;break;
            default:pinGlow.Visibility=Visibility.Collapsed;break;
        }
    }

    void ShowPinMenu()
    {
        var menu=new WpfContextMenu{Background=new SolidColorBrush(Color.FromArgb(236,13,19,33)),Foreground=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromArgb(160,255,138,0)),BorderThickness=new Thickness(1)};
        var vis=pinBar!=null&&pinBar.IsVisible;
        var toggle=new WpfMenuItem{Header=vis?L("關閉工具列"):L("打開工具列"),Foreground=Brushes.White};
        toggle.Click+=(_,_)=>TogglePinBar();
        var close=new WpfMenuItem{Header=L("關閉盯住"),Foreground=Brushes.White};
        close.Click+=(_,_)=>Close();
        var eGlow=new WpfMenuItem{Header=L("邊緣：光暈"),IsCheckable=true,IsChecked=pinEdge==PinEdge.Glow,Foreground=Brushes.White};
        eGlow.Click+=(_,_)=>{pinEdge=PinEdge.Glow;ApplyPinEdge();};
        var eBorder=new WpfMenuItem{Header=L("邊緣：邊框"),IsCheckable=true,IsChecked=pinEdge==PinEdge.Border,Foreground=Brushes.White};
        eBorder.Click+=(_,_)=>{pinEdge=PinEdge.Border;ApplyPinEdge();};
        var eNone=new WpfMenuItem{Header=L("邊緣：無"),IsCheckable=true,IsChecked=pinEdge==PinEdge.None,Foreground=Brushes.White};
        eNone.Click+=(_,_)=>{pinEdge=PinEdge.None;ApplyPinEdge();};
        menu.Items.Add(toggle);menu.Items.Add(new WpfSeparator());menu.Items.Add(eGlow);menu.Items.Add(eBorder);menu.Items.Add(eNone);menu.Items.Add(new WpfSeparator());menu.Items.Add(close);
        menu.Placement=System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.PlacementTarget=this;
        menu.IsOpen=true;
    }

    void TogglePinBar()
    {
        if(pinBar==null)return;
        if(pinBar.IsVisible){pinBar.Hide();annotationTool=AnnotationTool.Select;UpdateToolHighlights();HideBrushRing();Cursor=Cursors.Arrow;}
        else{pinBar.Show();PositionPinBar();}
    }
}
