using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Brushes=System.Windows.Media.Brushes;
using Brush=System.Windows.Media.Brush;
using Color=System.Windows.Media.Color;
using Cursors=System.Windows.Input.Cursors;
using Button=System.Windows.Controls.Button;
using Orientation=System.Windows.Controls.Orientation;
using IoPath=System.IO.Path;

namespace BeeX.DeskNest;

/// <summary>
/// 區域錄屏控制條：從截圖框選後啟動。影片用自抓幀管線（GDI BitBlt→ffmpeg rawvideo 管道）錄製，
/// 支持 FPS 選擇、開始倒計時、暫停/繼續；聲音（系統+麥克風）用 WASAPI 採集，停止時 ffmpeg 合成 MP4 或轉 GIF。
/// </summary>
public sealed class RecordingController : Window
{
    const int GWL_EXSTYLE=-20, WS_EX_TRANSPARENT=0x20, WS_EX_LAYERED=0x80000, WS_EX_TOOLWINDOW=0x80;
    const uint WDA_EXCLUDEFROMCAPTURE=0x11;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd,int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd,int index,int value);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd,uint dwAffinity);

    int px,py;
    readonly int pw,ph;
    Rect diu;
    readonly string outputDir;
    readonly string language;
    Rect moveBaseDiu; int moveBasePx, moveBasePy;

    Window? marker;
    Border? markerBorder;
    Window? countdownWin;
    Process? ff;
    AudioCapture? audio;
    ScreenFrameCapturer? capturer;
    string tempDir="";
    string tempVideo="";
    bool useGif;
    bool finished;
    bool paused;
    // 設定頁可改的默認值（由 DeskNestService 同步）；工具條内仍可單次循環切換
    public static int DefaultFps=30;
    public static int DefaultCountdownSec;
    int fps=FpsList.Contains(DefaultFps)?DefaultFps:30;
    int countdownSec=Math.Clamp(DefaultCountdownSec,0,10);
    static readonly int[] FpsList={5,10,15,24,30,48,60};
    readonly System.Diagnostics.Stopwatch recSw=new();
    System.Windows.Threading.DispatcherTimer? timer;

    Border prepPanel=new(), recPanel=new();
    TextBlock timerText=new(){Foreground=Brushes.White,FontSize=15,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,10,0)};
    Button formatBtn=new(), fpsBtn=new(), delayBtn=new(), pauseBtn=new(), widthBtn=new(), dashBtn=new();
    RecordAnnotationLayer? annLayer;
    RecordTool curTool=RecordTool.None;
    readonly Dictionary<RecordTool,Button> toolBtns=new();
    Color annColor=Color.FromRgb(255,59,48);
    double annWidth=4;
    int annDash;
    System.Windows.Controls.CheckBox editorChk=new();
    bool openEditorAfter;

    string L(string v)=>Localization.T(v,language);

    public RecordingController(int px,int py,int pw,int ph,Rect diu,string outputDir,string language)
    {
        this.px=px;this.py=py;this.pw=pw&~1;this.ph=ph&~1;this.diu=diu;this.outputDir=outputDir;this.language=language;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=true;
        AllowsTransparency=true;Background=Brushes.Transparent;SizeToContent=SizeToContent.WidthAndHeight;ShowActivated=false;
        BuildUi();
        Loaded+=(_,_)=>{PlaceBar();EnsureMarker();};
    }

    void BuildUi()
    {
        // 預備態
        var startBtn=Btn("● "+L("開始錄製"),new SolidColorBrush(Color.FromRgb(239,68,68)),Begin);
        fpsBtn=Btn($"{fps} FPS",new SolidColorBrush(Color.FromArgb(90,255,255,255)),CycleFps);
        formatBtn=Btn("MP4",new SolidColorBrush(Color.FromArgb(90,255,255,255)),ToggleFormat);
        delayBtn=Btn(L("延遲")+$":{countdownSec}s",new SolidColorBrush(Color.FromArgb(90,255,255,255)),CycleDelay);
        var cancelBtn=Btn("✕ "+L("取消"),new SolidColorBrush(Color.FromArgb(90,255,255,255)),()=>Cancel());
        // 完整剪輯器（VideoEditorWindow）暫不開放；勾選後錄製完成打開簡易剪輯工具條（QuickTrimWindow）。
        editorChk=new System.Windows.Controls.CheckBox{Content=new TextBlock{Text=L("錄製後編輯"),Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center},Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,6,0),IsChecked=false};
        editorChk.Checked+=(_,_)=>openEditorAfter=true;
        editorChk.Unchecked+=(_,_)=>openEditorAfter=false;
        var prepRow=new StackPanel{Orientation=Orientation.Horizontal};
        prepRow.Children.Add(startBtn);prepRow.Children.Add(fpsBtn);prepRow.Children.Add(formatBtn);prepRow.Children.Add(delayBtn);prepRow.Children.Add(editorChk);prepRow.Children.Add(cancelBtn);
        prepPanel=Bar(prepRow);

        // 錄製態
        pauseBtn=Btn("⏸ "+L("暫停"),new SolidColorBrush(Color.FromArgb(90,255,255,255)),TogglePause);
        var stopBtn=Btn("■ "+L("停止"),new SolidColorBrush(Color.FromRgb(239,68,68)),()=>Stop());
        var recRow=new StackPanel{Orientation=Orientation.Horizontal};
        var dot=new System.Windows.Shapes.Ellipse{Width=10,Height=10,Fill=new SolidColorBrush(Color.FromRgb(239,68,68)),VerticalAlignment=System.Windows.VerticalAlignment.Center,Margin=new Thickness(8,0,6,0)};
        recRow.Children.Add(dot);recRow.Children.Add(timerText);recRow.Children.Add(pauseBtn);recRow.Children.Add(stopBtn);
        var recCol=new StackPanel();
        recCol.Children.Add(recRow);
        recCol.Children.Add(BuildToolRow());
        recPanel=Bar(recCol);
        recPanel.Visibility=Visibility.Collapsed;

        var root=new Grid();
        root.Children.Add(prepPanel);root.Children.Add(recPanel);
        Content=root;
    }

    UIElement BuildToolRow()
    {
        var row=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,6,0,0)};
        row.Children.Add(ToolBtn(L("畫筆"),RecordTool.Pen));
        row.Children.Add(ToolBtn(L("螢光"),RecordTool.Highlighter));
        row.Children.Add(ToolBtn(L("線"),RecordTool.Line));
        row.Children.Add(ToolBtn(L("箭頭"),RecordTool.Arrow));
        row.Children.Add(ToolBtn(L("矩形"),RecordTool.Rect));
        row.Children.Add(ToolBtn(L("橢圓"),RecordTool.Ellipse));
        row.Children.Add(ToolBtn(L("序號"),RecordTool.Number));
        row.Children.Add(ToolBtn(L("橡皮"),RecordTool.Eraser));
        row.Children.Add(ToolBtn(L("馬賽克"),RecordTool.Mosaic));
        row.Children.Add(ToolBtn(L("取色"),RecordTool.Picker));
        row.Children.Add(ToolBtn(L("移動"),RecordTool.Move));
        foreach(var c in new[]{Color.FromRgb(255,59,48),Color.FromRgb(255,204,0),Color.FromRgb(52,199,89),Color.FromRgb(0,122,255),Color.FromRgb(255,255,255),Color.FromRgb(20,20,20)})
            row.Children.Add(ColorSwatch(c));
        dashBtn=Btn("—",new SolidColorBrush(Color.FromArgb(90,255,255,255)),CycleDash);
        row.Children.Add(dashBtn);
        widthBtn=Btn(L("粗細")+":4",new SolidColorBrush(Color.FromArgb(90,255,255,255)),CycleWidth);
        row.Children.Add(widthBtn);
        row.Children.Add(Btn("↶ "+L("撤銷"),new SolidColorBrush(Color.FromArgb(90,255,255,255)),()=>annLayer?.Undo()));
        return row;
    }

    Button ToolBtn(string label,RecordTool t)
    {
        var b=new Button{Content=new TextBlock{Text=label,Foreground=Brushes.White},Height=30,MinWidth=38,Margin=new Thickness(2),Padding=new Thickness(6,0,6,0),Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(90,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        b.Click+=(_,_)=>SelectRecTool(t);
        toolBtns[t]=b;
        return b;
    }

    void SelectRecTool(RecordTool t)
    {
        if(curTool==t)t=RecordTool.None;
        curTool=t;
        annLayer?.SetTool(t);
        foreach(var kv in toolBtns)kv.Value.Background=new SolidColorBrush(kv.Key==t?Color.FromRgb(255,138,0):Color.FromArgb(90,255,255,255));
    }

    Border ColorSwatch(Color c)
    {
        var sw=new Border{Width=22,Height=22,CornerRadius=new CornerRadius(11),Margin=new Thickness(2),Background=new SolidColorBrush(c),BorderBrush=new SolidColorBrush(Color.FromArgb(150,255,255,255)),BorderThickness=new Thickness(1),Cursor=Cursors.Hand,VerticalAlignment=System.Windows.VerticalAlignment.Center};
        sw.MouseLeftButtonDown+=(_,e)=>{annColor=c;annLayer?.SetColor(c);e.Handled=true;};
        return sw;
    }

    void OnColorPicked(Color c)
    {
        annColor=c;annLayer?.SetColor(c);
        SelectRecTool(RecordTool.None);
    }

    // 錄製中拖動「移動」選框：用絕對光標位移（避免窗口跟隨產生反饋抖動），同步紅框/標注層/抓幀 region。
    void OnRegionMoved(double dxDiu,double dyDiu)
    {
        double vsL=SystemParameters.VirtualScreenLeft,vsT=SystemParameters.VirtualScreenTop,vsW=SystemParameters.VirtualScreenWidth,vsH=SystemParameters.VirtualScreenHeight;
        double nx=Math.Clamp(moveBaseDiu.X+dxDiu,vsL,Math.Max(vsL,vsL+vsW-diu.Width));
        double ny=Math.Clamp(moveBaseDiu.Y+dyDiu,vsT,Math.Max(vsT,vsT+vsH-diu.Height));
        diu=new Rect(nx,ny,diu.Width,diu.Height);
        double sx=pw/Math.Max(1,diu.Width),sy=ph/Math.Max(1,diu.Height);
        px=moveBasePx+(int)Math.Round((nx-moveBaseDiu.X)*sx);
        py=moveBasePy+(int)Math.Round((ny-moveBaseDiu.Y)*sy);
        if(capturer!=null){capturer.RegionX=px;capturer.RegionY=py;}
        if(marker!=null){marker.Left=nx;marker.Top=ny;}
        annLayer?.MoveTo(diu);
        annLayer?.SetRegionOrigin(px,py);
    }

    void CycleWidth()
    {
        int[] ws={2,4,8,12};var i=Array.IndexOf(ws,(int)annWidth);i=(i+1)%ws.Length;annWidth=ws[i];
        annLayer?.SetWidth(annWidth);
        widthBtn.Content=new TextBlock{Text=L("粗細")+$":{(int)annWidth}",Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};
    }

    void CycleDash()
    {
        annDash=(annDash+1)%4;
        annLayer?.SetDash(annDash);
        dashBtn.Content=new TextBlock{Text=annDash switch{1=>"- -",2=>"— —",3=>"···",_=>"—"},Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};
    }

    void CycleFps()
    {
        var i=Array.IndexOf(FpsList,fps);i=(i+1)%FpsList.Length;fps=FpsList[i];
        fpsBtn.Content=new TextBlock{Text=$"{fps} FPS",Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};
    }

    void CycleDelay()
    {
        int[] d={0,3,5,10};var i=Array.IndexOf(d,countdownSec);i=(i+1)%d.Length;countdownSec=d[i];
        delayBtn.Content=new TextBlock{Text=L("延遲")+$":{countdownSec}s",Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};
    }

    void TogglePause()
    {
        paused=!paused;
        if(capturer!=null)capturer.Paused=paused;
        if(audio!=null)audio.Paused=paused;
        if(paused)recSw.Stop();else recSw.Start();
        pauseBtn.Content=new TextBlock{Text=paused?"▶ "+L("繼續"):"⏸ "+L("暫停"),Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};
        if(marker?.Content is Border mb)mb.BorderBrush=new SolidColorBrush(paused?Color.FromArgb(230,255,193,7):Color.FromArgb(230,239,68,68));
    }

    static Border Bar(UIElement child)=>new()
    {
        CornerRadius=new CornerRadius(11),Padding=new Thickness(6),
        Background=new SolidColorBrush(Color.FromArgb(236,13,19,33)),
        BorderBrush=new SolidColorBrush(Color.FromArgb(150,255,138,0)),BorderThickness=new Thickness(1),
        Child=child
    };

    Button Btn(string text,Brush bg,Action onClick)
    {
        var b=new Button{Content=new TextBlock{Text=text,Foreground=Brushes.White,FontWeight=FontWeights.SemiBold},Height=34,MinWidth=48,Margin=new Thickness(3),Padding=new Thickness(10,0,10,0),Foreground=Brushes.White,Background=bg,BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        b.Click+=(_,_)=>onClick();
        return b;
    }

    void ToggleFormat()
    {
        useGif=!useGif;
        formatBtn.Content=new TextBlock{Text=useGif?"GIF":"MP4",Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};
    }

    void PlaceBar()
    {
        UpdateLayout();
        var vsB=SystemParameters.VirtualScreenTop+SystemParameters.VirtualScreenHeight;
        var vsR=SystemParameters.VirtualScreenLeft+SystemParameters.VirtualScreenWidth;
        var x=diu.X;var y=diu.Bottom+8;
        if(y+ActualHeight>vsB)y=Math.Max(SystemParameters.VirtualScreenTop+4,diu.Y-ActualHeight-8);
        Left=Math.Max(SystemParameters.VirtualScreenLeft+4,Math.Min(x,vsR-ActualWidth-4));
        Top=y;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try{SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle,WDA_EXCLUDEFROMCAPTURE);}catch{}
    }

    void EnsureMarker()
    {
        if(marker!=null)return;
        markerBorder=new Border{BorderBrush=new SolidColorBrush(Color.FromArgb(235,255,138,0)),BorderThickness=new Thickness(3),Background=Brushes.Transparent};
        marker=new Window
        {
            WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Topmost=true,
            AllowsTransparency=true,Background=Brushes.Transparent,ShowActivated=false,IsHitTestVisible=false,
            Left=diu.X,Top=diu.Y,Width=Math.Max(1,diu.Width),Height=Math.Max(1,diu.Height),
            Content=markerBorder
        };
        marker.SourceInitialized+=(_,_)=>
        {
            var h=new WindowInteropHelper(marker).Handle;
            var ex=GetWindowLong(h,GWL_EXSTYLE);
            SetWindowLong(h,GWL_EXSTYLE,ex|WS_EX_TRANSPARENT|WS_EX_LAYERED|WS_EX_TOOLWINDOW);
            try{SetWindowDisplayAffinity(h,WDA_EXCLUDEFROMCAPTURE);}catch{}
        };
        marker.Show();
    }

    void Begin()
    {
        if(!FfmpegService.IsAvailable)
        {
            // ffmpeg 不再內置：缺失時彈下載對話框，裝好後直接繼續錄製流程
            if(!FfmpegInstallerService.ShowInstallDialog(language))return;
        }
        if(pw<2||ph<2){Cancel();return;}
        prepPanel.Visibility=Visibility.Collapsed;
        if(countdownSec>0)StartCountdown();else StartCapture();
    }

    void StartCountdown()
    {
        int remain=countdownSec;
        var text=new TextBlock{Text=remain.ToString(),Foreground=Brushes.White,FontSize=96,FontWeight=FontWeights.Bold,HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=System.Windows.VerticalAlignment.Center};
        countdownWin=new Window{WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Topmost=true,AllowsTransparency=true,Background=Brushes.Transparent,ShowActivated=false,IsHitTestVisible=false,
            Left=diu.X,Top=diu.Y,Width=Math.Max(1,diu.Width),Height=Math.Max(1,diu.Height),
            Content=new Border{Background=new SolidColorBrush(Color.FromArgb(70,0,0,0)),Child=text}};
        countdownWin.SourceInitialized+=(_,_)=>{try{SetWindowDisplayAffinity(new WindowInteropHelper(countdownWin).Handle,WDA_EXCLUDEFROMCAPTURE);}catch{}};
        countdownWin.Show();
        var t=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromSeconds(1)};
        t.Tick+=(_,_)=>{remain--;if(remain<=0){t.Stop();try{countdownWin?.Close();}catch{}countdownWin=null;if(!finished)StartCapture();}else text.Text=remain.ToString();};
        t.Start();
    }

    void StartCapture()
    {
        tempDir=IoPath.Combine(IoPath.GetTempPath(),"BeeX_Rec_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(tempDir);
        tempVideo=IoPath.Combine(tempDir,"video.mp4");
        ff=FfmpegService.StartRawEncoder(pw,ph,fps,tempVideo);
        if(ff==null||ff.HasExited){Cancel();return;}
        annLayer=new RecordAnnotationLayer(diu,pw,ph);
        annLayer.SetColor(annColor);annLayer.SetWidth(annWidth);annLayer.SetDash(annDash);
        annLayer.SetRegionOrigin(px,py);
        annLayer.WidthChanged+=w=>{annWidth=w;widthBtn.Content=new TextBlock{Text=L("粗細")+$":{(int)w}",Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};};
        annLayer.ColorPicked+=OnColorPicked;
        annLayer.RegionMoveBegin+=()=>{moveBaseDiu=diu;moveBasePx=px;moveBasePy=py;};
        annLayer.RegionMoved+=OnRegionMoved;
        annLayer.Show();
        capturer=new ScreenFrameCapturer(px,py,pw,ph,fps);
        capturer.ProcessFrame=annLayer.ProcessFrame;
        capturer.Start(ff.StandardInput.BaseStream);
        if(!useGif){audio=new AudioCapture();audio.Start(tempDir);}
        recSw.Restart();
        recPanel.Visibility=Visibility.Visible;
        timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(250)};
        timer.Tick+=(_,_)=>{var t=recSw.Elapsed;timerText.Text=$"{(int)t.TotalMinutes:00}:{t.Seconds:00}";};
        timer.Start();
        if(markerBorder!=null)markerBorder.BorderBrush=new SolidColorBrush(Color.FromArgb(235,239,68,68));
        if(marker!=null){marker.Topmost=false;marker.Topmost=true;}
        PlaceBar();
    }

    void Stop()
    {
        if(finished)return;finished=true;
        timer?.Stop();recSw.Stop();
        try{marker?.Close();}catch{}
        try{countdownWin?.Close();}catch{}
        timerText.Text=L("處理中…");
        recPanel.IsEnabled=false;
        capturer?.Stop();
        try{ff?.StandardInput.Close();}catch{}
        try{if(ff!=null&&!ff.WaitForExit(8000))ff.Kill();}catch{}
        try{ff?.Dispose();}catch{}ff=null;
        audio?.Stop();
        var sys=audio?.SystemWavPath;var mic=audio?.MicWavPath;
        try{annLayer?.Close();}catch{}annLayer=null;
        System.Threading.Tasks.Task.Run(()=>
        {
            string? outPath=null;
            try{outPath=Finalize(sys,mic);}catch{}
            Dispatcher.Invoke(()=>
            {
                audio?.Dispose();
                CleanupTemp();
                if(!string.IsNullOrEmpty(outPath)&&File.Exists(outPath))
                {
                    if(openEditorAfter&&outPath!.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase))
                    {
                        // 打開簡易剪輯工具條（完整剪輯器 VideoEditorWindow 保留代碼但不從此入口打開）
                        try{new QuickTrimWindow(outPath,diu,language).Show();}
                        catch{try{Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{outPath}\""){UseShellExecute=true});}catch{}}
                    }
                    else try{Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{outPath}\""){UseShellExecute=true});}catch{}
                }
                else System.Windows.MessageBox.Show(L("錄製失敗或無有效內容。"),"BeeX DeskNest",MessageBoxButton.OK,MessageBoxImage.Warning);
                Close();
            });
        });
    }

    static bool ValidWav(string? p)=>!string.IsNullOrEmpty(p)&&File.Exists(p)&&new FileInfo(p).Length>1024;

    string? Finalize(string? sys,string? mic)
    {
        if(!File.Exists(tempVideo)||new FileInfo(tempVideo).Length<1024)return null;
        // 錄屏統一落到 BeeX 根目錄 Recordings，不再從截圖目錄父級推導
        var recordDir=BeeXPaths.RecordingsDir;
        Directory.CreateDirectory(recordDir);
        var stamp=DateTime.Now.ToString("yyyyMMdd_HHmmss");
        if(useGif)
        {
            var gif=IoPath.Combine(recordDir,$"BeeX_Record_{stamp}.gif");
            var vf="fps=15,scale='min(720,iw)':-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse";
            var code=FfmpegService.RunToEnd($"-y -i \"{tempVideo}\" -vf \"{vf}\" \"{gif}\"");
            return code==0&&File.Exists(gif)?gif:null;
        }
        var mp4=IoPath.Combine(recordDir,$"BeeX_Record_{stamp}.mp4");
        var hasSys=ValidWav(sys);var hasMic=ValidWav(mic);
        string args;
        if(hasSys&&hasMic)
            args=$"-y -i \"{tempVideo}\" -i \"{sys}\" -i \"{mic}\" -filter_complex \"[1:a][2:a]amix=inputs=2:duration=first:dropout_transition=0[a]\" -map 0:v -map \"[a]\" -c:v copy -c:a aac -b:a 192k -shortest \"{mp4}\"";
        else if(hasSys||hasMic)
            args=$"-y -i \"{tempVideo}\" -i \"{(hasSys?sys:mic)}\" -map 0:v -map 1:a -c:v copy -c:a aac -b:a 192k -shortest \"{mp4}\"";
        else
            args=$"-y -i \"{tempVideo}\" -c copy \"{mp4}\"";
        var c=FfmpegService.RunToEnd(args);
        if(c==0&&File.Exists(mp4))return mp4;
        try{File.Copy(tempVideo,mp4,true);return mp4;}catch{return null;}
    }

    void CleanupTemp(){try{if(Directory.Exists(tempDir))Directory.Delete(tempDir,true);}catch{}}

    void Cancel()
    {
        if(finished)return;finished=true;
        timer?.Stop();
        try{marker?.Close();}catch{}
        try{countdownWin?.Close();}catch{}
        try{annLayer?.Close();}catch{}annLayer=null;
        capturer?.Stop();
        if(ff!=null){try{ff.StandardInput.Close();}catch{}try{if(!ff.WaitForExit(3000))ff.Kill();}catch{}try{ff.Dispose();}catch{}ff=null;}
        audio?.Stop();audio?.Dispose();
        CleanupTemp();
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        try{marker?.Close();}catch{}
        try{countdownWin?.Close();}catch{}
        try{annLayer?.Close();}catch{}
        try{capturer?.Stop();}catch{}
        base.OnClosed(e);
    }
}
