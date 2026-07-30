using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfBrushes=System.Windows.Media.Brushes;
using WpfColor=System.Windows.Media.Color;
using WpfImage=System.Windows.Controls.Image;
using WpfPoint=System.Windows.Point;
using Border=System.Windows.Controls.Border;
using Canvas=System.Windows.Controls.Canvas;
using Grid=System.Windows.Controls.Grid;
using TextBlock=System.Windows.Controls.TextBlock;
using Ellipse=System.Windows.Shapes.Ellipse;
using Rectangle=System.Windows.Shapes.Rectangle;
using StackPanel=System.Windows.Controls.StackPanel;

namespace BeeX.DeskNest;

partial class EasterEggGame
{
    void SyncVisuals()
    {
        if(ballShell==null)return;
        Canvas.SetLeft(ballShell,ballX-vLeft);Canvas.SetTop(ballShell,ballY-vTop);
        // 球體隨水平速度滾動，增強馬里奧式運動反饋
        ballSpin.Angle+=velX*.14*.016*60;
    }

    // 勝利吸入動效：懸浮球旋轉 720° 同時縮小並被拉進黑洞中心，隨後彩帶慶祝
    void StepWinAnim(double dt)
    {
        winAnimT+=dt;var t=Math.Min(1,winAnimT/.65);var ease=t*t;
        ballX=winStartX+(holeCX-BallSize/2d-winStartX)*ease;ballY=winStartY+(holeCY-BallSize/2d-winStartY)*ease;
        Canvas.SetLeft(ballShell!,ballX-vLeft);Canvas.SetTop(ballShell!,ballY-vTop);
        ballSpin.Angle=t*720;ballScale.ScaleX=ballScale.ScaleY=Math.Max(0,1-ease);
        if(t>=1){phase=Phase.Celebrate;celebrateT=0;SpawnCelebration();}
    }

    /// <summary>彩帶雨 + 橫幅：140 條彩帶自橫幅上方炸開飄落，橫幅居中顯示「恭喜你！手指康復運動 xx 秒」</summary>
    void SpawnCelebration()
    {
        var work=SystemParameters.WorkArea;
        var cx=work.Left+work.Width/2-vLeft;var cy=work.Top+work.Height/2-vTop;
        WpfColor[] palette=[WpfColor.FromRgb(255,138,0),WpfColor.FromRgb(255,196,0),WpfColor.FromRgb(255,84,84),WpfColor.FromRgb(84,180,255),WpfColor.FromRgb(120,220,120),WpfColor.FromRgb(190,120,255),WpfColor.FromRgb(255,120,200)];
        for(var i=0;i<140;i++)
        {
            var r=new Ribbon{X=cx+rng.Next(-260,261),Y=cy-180+rng.Next(-40,41),Vx=rng.Next(-420,421),Vy=rng.Next(-680,-160),Rot=rng.Next(360),Vr=rng.Next(-540,541)};
            r.Visual=new Rectangle{Width=7+rng.NextDouble()*4,Height=13+rng.NextDouble()*6,RadiusX=2,RadiusY=2,Fill=new SolidColorBrush(palette[rng.Next(palette.Length)]),RenderTransformOrigin=new WpfPoint(.5,.5),RenderTransform=new RotateTransform(r.Rot)};
            Canvas.SetLeft(r.Visual,r.X);Canvas.SetTop(r.Visual,r.Y);
            canvas!.Children.Add(r.Visual);ribbons.Add(r);
        }
        var banner=new Border{CornerRadius=new CornerRadius(18),Padding=new Thickness(38,26,38,26),BorderThickness=new Thickness(1.5),BorderBrush=new SolidColorBrush(WpfColor.FromArgb(220,255,138,0)),Background=new SolidColorBrush(WpfColor.FromArgb(242,13,19,33)),Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=32,ShadowDepth=0,Opacity=.5,Color=WpfColor.FromRgb(255,138,0)}};
        var stack=new StackPanel();
        stack.Children.Add(new TextBlock{Text="🎉 "+L("恭喜你")+" 🎉",FontSize=34,FontWeight=FontWeights.Bold,Foreground=new SolidColorBrush(WpfColor.FromRgb(255,196,0)),HorizontalAlignment=System.Windows.HorizontalAlignment.Center});
        stack.Children.Add(new TextBlock{Text=F("手指康復運動 {0} 秒",winSeconds),FontSize=24,FontWeight=FontWeights.SemiBold,Foreground=WpfBrushes.White,Margin=new Thickness(0,12,0,0),HorizontalAlignment=System.Windows.HorizontalAlignment.Center});
        stack.Children.Add(new TextBlock{Text=F("金幣 {0} 枚",coinCount),FontSize=17,Foreground=new SolidColorBrush(WpfColor.FromRgb(198,205,218)),Margin=new Thickness(0,8,0,0),HorizontalAlignment=System.Windows.HorizontalAlignment.Center});
        banner.Child=stack;
        banner.Measure(new System.Windows.Size(double.PositiveInfinity,double.PositiveInfinity));
        Canvas.SetLeft(banner,cx-banner.DesiredSize.Width/2);Canvas.SetTop(banner,cy-banner.DesiredSize.Height/2-40);
        banner.RenderTransformOrigin=new WpfPoint(.5,.5);
        var pop=new ScaleTransform(0.4,0.4);banner.RenderTransform=pop;
        pop.BeginAnimation(ScaleTransform.ScaleXProperty,new System.Windows.Media.Animation.DoubleAnimation(0.4,1,TimeSpan.FromMilliseconds(360)){EasingFunction=new System.Windows.Media.Animation.BackEase{EasingMode=System.Windows.Media.Animation.EasingMode.EaseOut,Amplitude=.8}});
        pop.BeginAnimation(ScaleTransform.ScaleYProperty,new System.Windows.Media.Animation.DoubleAnimation(0.4,1,TimeSpan.FromMilliseconds(360)){EasingFunction=new System.Windows.Media.Animation.BackEase{EasingMode=System.Windows.Media.Animation.EasingMode.EaseOut,Amplitude=.8}});
        canvas!.Children.Add(banner);
        if(hud!=null)hud.Visibility=Visibility.Collapsed;
    }

    void StepCelebrate(double dt)
    {
        celebrateT+=dt;
        foreach(var r in ribbons)
        {
            r.Vy+=1050*dt;r.Vx*=1-Math.Min(1,.9*dt);
            r.X+=r.Vx*dt;r.Y+=r.Vy*dt;r.Rot+=r.Vr*dt;
            Canvas.SetLeft(r.Visual,r.X);Canvas.SetTop(r.Visual,r.Y);
            ((RotateTransform)r.Visual.RenderTransform).Angle=r.Rot;
        }
        if(celebrateT>=3.6)End(EggResult.Won);
    }

    /// <summary>全屏遊戲畫布：覆蓋整個虛擬桌面的透明置頂窗口，WS_EX_TRANSPARENT 點擊穿透不擋桌面操作</summary>
    void BuildOverlay()
    {
        canvas=new Canvas();
        overlay=new Window{Left=vLeft,Top=vTop,Width=vRight-vLeft,Height=vBottom-vTop,WindowStyle=WindowStyle.None,AllowsTransparency=true,Background=WpfBrushes.Transparent,ShowInTaskbar=false,Topmost=true,Focusable=false,ResizeMode=ResizeMode.NoResize,ShowActivated=false,IsHitTestVisible=false,Content=canvas};
        overlay.SourceInitialized+=(_,_)=>
        {
            WindowRegionHelper.HideFromAltTab(overlay);
            var hwnd=new WindowInteropHelper(overlay).Handle;
            const int GwlExStyle=-20;const long WsExTransparent=0x20,WsExLayered=0x80000;
            var style=GetWindowLongPtr(hwnd,GwlExStyle).ToInt64();
            SetWindowLongPtr(hwnd,GwlExStyle,new IntPtr(style|WsExTransparent|WsExLayered));
        };
    }

    // 遊戲球：復刻懸浮球外觀（深藏藍圓 + 蜂蜜橙描邊 + BeeX Logo），畫在畫布上、隨速度滾動
    void BuildBallVisual()
    {
        var logo=new WpfImage{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")),Width=34,Height=34,Stretch=Stretch.Uniform};
        ballShell=new Border{Width=BallSize,Height=BallSize,CornerRadius=new CornerRadius(BallSize/2d),Background=new SolidColorBrush(WpfColor.FromArgb(232,13,19,33)),BorderBrush=new SolidColorBrush(WpfColor.FromArgb(200,255,138,0)),BorderThickness=new Thickness(1.5),Child=logo,RenderTransformOrigin=new WpfPoint(.5,.5)};
        var tg=new TransformGroup();tg.Children.Add(ballSpin);tg.Children.Add(ballScale);
        ballShell.RenderTransform=tg;
        canvas!.Children.Add(ballShell);
    }

    // 黑洞：深色徑向漸變 + 兩圈虛線光環 + 純黑核心，整體持續旋轉形成漩渦特效
    void BuildHoleVisual(double x,double y)
    {
        var g=new Grid{Width=84,Height=84,RenderTransformOrigin=new WpfPoint(.5,.5)};
        g.Children.Add(new Ellipse{Width=84,Height=84,Fill=new RadialGradientBrush{GradientStops={new GradientStop(WpfColor.FromArgb(255,5,5,12),0),new GradientStop(WpfColor.FromArgb(235,20,8,40),.55),new GradientStop(WpfColor.FromArgb(0,20,8,40),1)}}});
        g.Children.Add(new Ellipse{Width=66,Height=66,Stroke=new SolidColorBrush(WpfColor.FromArgb(210,255,138,0)),StrokeThickness=3,StrokeDashArray=new DoubleCollection{7,5},HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
        g.Children.Add(new Ellipse{Width=46,Height=46,Stroke=new SolidColorBrush(WpfColor.FromArgb(190,150,90,255)),StrokeThickness=2.5,StrokeDashArray=new DoubleCollection{5,4},HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
        g.Children.Add(new Ellipse{Width=22,Height=22,Fill=WpfBrushes.Black,HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
        var spin=new RotateTransform();g.RenderTransform=spin;
        spin.BeginAnimation(RotateTransform.AngleProperty,new System.Windows.Media.Animation.DoubleAnimation(0,360,TimeSpan.FromSeconds(1.4)){RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever});
        Canvas.SetLeft(g,x-vLeft);Canvas.SetTop(g,y-vTop);
        canvas!.Children.Add(g);
    }

    // 金幣：金色圓片 + 高光內圈，水平壓縮循環動畫模擬旋轉
    void AddCoin(double x,double y)
    {
        var g=new Grid{Width=CoinSize,Height=CoinSize,RenderTransformOrigin=new WpfPoint(.5,.5)};
        g.Children.Add(new Ellipse{Width=CoinSize,Height=CoinSize,Fill=new SolidColorBrush(WpfColor.FromRgb(255,196,0)),Stroke=new SolidColorBrush(WpfColor.FromRgb(214,148,0)),StrokeThickness=2});
        g.Children.Add(new Ellipse{Width=CoinSize-9,Height=CoinSize-9,Stroke=new SolidColorBrush(WpfColor.FromArgb(220,255,240,170)),StrokeThickness=1.6,HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center});
        var flip=new ScaleTransform(1,1);g.RenderTransform=flip;
        flip.BeginAnimation(ScaleTransform.ScaleXProperty,new System.Windows.Media.Animation.DoubleAnimation(1,.14,TimeSpan.FromSeconds(.55)){AutoReverse=true,RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever});
        Canvas.SetLeft(g,x-CoinSize/2d-vLeft);Canvas.SetTop(g,y-CoinSize/2d-vTop);
        canvas!.Children.Add(g);
        coins.Add(new Coin{X=x,Y=y,Visual=g});
    }

    // 蜂賊：琥珀圓身 + 黑條紋 + 撲翼，沿平台頂面來回巡邏
    Grid BuildFoeVisual()
    {
        var g=new Grid{Width=FoeSize,Height=FoeSize};
        g.Children.Add(new Ellipse{Width=FoeSize,Height=FoeSize-6,Fill=new SolidColorBrush(WpfColor.FromRgb(255,170,40)),Stroke=new SolidColorBrush(WpfColor.FromRgb(90,60,10)),StrokeThickness=1.5,VerticalAlignment=VerticalAlignment.Bottom});
        g.Children.Add(new Rectangle{Width=5,Height=FoeSize-12,Fill=new SolidColorBrush(WpfColor.FromRgb(35,28,16)),HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(0,0,0,3),RadiusX=2,RadiusY=2});
        g.Children.Add(new Rectangle{Width=5,Height=FoeSize-16,Fill=new SolidColorBrush(WpfColor.FromRgb(35,28,16)),HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Bottom,Margin=new Thickness(14,0,0,3),RadiusX=2,RadiusY=2});
        g.Children.Add(new Ellipse{Width=6,Height=6,Fill=WpfBrushes.White,HorizontalAlignment=System.Windows.HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(5,10,0,0)});
        g.Children.Add(new Ellipse{Width=3,Height=3,Fill=WpfBrushes.Black,HorizontalAlignment=System.Windows.HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Margin=new Thickness(6.5,11.5,0,0)});
        var wing=new Ellipse{Width=14,Height=9,Fill=new SolidColorBrush(WpfColor.FromArgb(170,220,235,255)),HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Top,RenderTransformOrigin=new WpfPoint(.5,1)};
        var flap=new ScaleTransform(1,1);wing.RenderTransform=flap;
        flap.BeginAnimation(ScaleTransform.ScaleYProperty,new System.Windows.Media.Animation.DoubleAnimation(1,.35,TimeSpan.FromSeconds(.12)){AutoReverse=true,RepeatBehavior=System.Windows.Media.Animation.RepeatBehavior.Forever});
        g.Children.Add(wing);
        return g;
    }

    // HUD：主屏頂部居中的計時 + 金幣計數，遊戲即「手指康復運動」秒錶
    void BuildHud()
    {
        var work=SystemParameters.WorkArea;
        hud=new TextBlock{FontSize=17,FontWeight=FontWeights.SemiBold,Foreground=WpfBrushes.White,Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=8,ShadowDepth=0,Opacity=.85,Color=WpfColor.FromRgb(13,19,33)}};
        var shell=new Border{CornerRadius=new CornerRadius(14),Padding=new Thickness(18,8,18,8),Background=new SolidColorBrush(WpfColor.FromArgb(190,13,19,33)),BorderBrush=new SolidColorBrush(WpfColor.FromArgb(160,255,138,0)),BorderThickness=new Thickness(1),Child=hud};
        Canvas.SetLeft(shell,work.Left+work.Width/2-160-vLeft);Canvas.SetTop(shell,work.Top+16-vTop);
        shell.Width=320;hud.HorizontalAlignment=System.Windows.HorizontalAlignment.Center;
        canvas!.Children.Add(shell);
    }
}
