using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes=System.Windows.Media.Brushes;
using Brush=System.Windows.Media.Brush;
using Color=System.Windows.Media.Color;
using Cursors=System.Windows.Input.Cursors;
using Button=System.Windows.Controls.Button;
using Orientation=System.Windows.Controls.Orientation;
using IoPath=System.IO.Path;
using Clipboard=System.Windows.Clipboard;

namespace BeeX.DeskNest;

/// <summary>
/// 錄製後的簡易剪輯工具條：預覽窗貼在錄製區域位置循環播放，工具條提供
/// 修剪滑軌（截取起點/終點+播放頭）、播放/暫停、倍速、格式（MP4/GIF/WebP）、導出、複製、重置。
/// 完整剪輯器（VideoEditorWindow）代碼保留但不從此入口打開。
/// </summary>
public sealed class QuickTrimWindow : Window
{
    readonly string srcPath;
    readonly string language;
    readonly Rect region;
    readonly bool hasAudio;

    double duration;          // 影片總長（秒）
    double trimStart, trimEnd; // 截取起點/終點（秒）
    double speed=1.0;
    static readonly double[] Speeds={0.5,0.75,1.0,1.25,1.5,2.0};
    static readonly string[] Formats={"MP4","GIF","WebP"};
    int formatIdx;
    bool playing, exporting;

    Window? previewWin;
    readonly MediaElement media=new(){LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Manual,ScrubbingEnabled=true,Stretch=Stretch.Uniform};
    System.Windows.Threading.DispatcherTimer? tick;

    // 工具條 UI
    Border rootBar=new();
    StackPanel ctrlRow=new(){Orientation=Orientation.Horizontal};
    Button playBtn=new(), speedBtn=new(), formatBtn=new();
    readonly TextBlock timeText=new(){Foreground=Brushes.White,FontSize=14,FontWeight=FontWeights.SemiBold,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,8,0)};

    // 修剪滑軌
    const double TrackW=460, TrackPad=6;
    readonly Canvas track=new(){Width=TrackW,Height=26,Background=Brushes.Transparent};
    readonly Border trackBg=new(){Height=6,CornerRadius=new CornerRadius(3),Background=new SolidColorBrush(Color.FromArgb(90,255,255,255))};
    readonly Border rangeFill=new(){Height=6,CornerRadius=new CornerRadius(3),Background=new SolidColorBrush(Color.FromRgb(255,138,0))};
    readonly Border startHandle=new(){Width=9,Height=20,CornerRadius=new CornerRadius(3),Background=new SolidColorBrush(Color.FromRgb(255,138,0)),BorderBrush=Brushes.White,BorderThickness=new Thickness(1),Cursor=Cursors.SizeWE};
    readonly Border endHandle=new(){Width=9,Height=20,CornerRadius=new CornerRadius(3),Background=new SolidColorBrush(Color.FromRgb(255,138,0)),BorderBrush=Brushes.White,BorderThickness=new Thickness(1),Cursor=Cursors.SizeWE};
    readonly Border playhead=new(){Width=2,Height=14,Background=Brushes.White,IsHitTestVisible=false};
    enum Drag{None,Start,End,Seek}
    Drag drag=Drag.None;

    string L(string v)=>Localization.T(v,language);

    public QuickTrimWindow(string srcPath,Rect region,string language)
    {
        this.srcPath=srcPath;this.region=region;this.language=language;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=true;
        AllowsTransparency=true;Background=Brushes.Transparent;SizeToContent=SizeToContent.WidthAndHeight;ShowActivated=true;
        var probe=FfmpegService.Probe(srcPath);
        duration=Math.Max(0.1,probe.duration);
        hasAudio=probe.hasAudio;
        trimStart=0;trimEnd=duration;
        BuildUi();
        Loaded+=(_,_)=>{PlaceBar();ShowPreview();StartTick();};
        Closed+=(_,_)=>Teardown();
    }

    void BuildUi()
    {
        // 修剪滑軌
        track.Children.Add(trackBg);track.Children.Add(rangeFill);
        track.Children.Add(playhead);track.Children.Add(startHandle);track.Children.Add(endHandle);
        Canvas.SetTop(trackBg,10);Canvas.SetTop(rangeFill,10);Canvas.SetTop(playhead,6);Canvas.SetTop(startHandle,3);Canvas.SetTop(endHandle,3);
        trackBg.Width=TrackW-TrackPad*2;Canvas.SetLeft(trackBg,TrackPad);
        startHandle.ToolTip=L("截取起點");endHandle.ToolTip=L("截取終點");
        track.MouseLeftButtonDown+=OnTrackDown;
        track.MouseMove+=OnTrackMove;
        track.MouseLeftButtonUp+=OnTrackUp;

        // 控制行
        playBtn=IconBtn("player-play",L("播放/暫停"),TogglePlay);
        speedBtn=TextBtn("1.0X",CycleSpeed);
        formatBtn=TextBtn("MP4",CycleFormat);
        var copyBtn=IconBtn("copy",L("導出並複製到剪貼板"),()=>Export(copyAfter:true));
        var saveBtn=IconBtn("download",L("導出保存"),()=>Export(copyAfter:false));
        var resetBtn=IconBtn("restore",L("重置修剪"),ResetTrim);
        var closeBtn=IconBtn("x",L("關閉（保留原始錄製檔）"),Close);
        var grip=new TextBlock{Text="✥",Foreground=Brushes.White,FontSize=16,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,0,4,0),Cursor=Cursors.SizeAll,ToolTip=L("拖動移動工具條")};
        grip.MouseLeftButtonDown+=(_,e)=>{try{DragMove();}catch{}e.Handled=true;};

        ctrlRow.Children.Add(playBtn);
        ctrlRow.Children.Add(timeText);
        ctrlRow.Children.Add(Sep());
        ctrlRow.Children.Add(speedBtn);
        ctrlRow.Children.Add(formatBtn);
        ctrlRow.Children.Add(Sep());
        ctrlRow.Children.Add(saveBtn);
        ctrlRow.Children.Add(copyBtn);
        ctrlRow.Children.Add(resetBtn);
        ctrlRow.Children.Add(closeBtn);
        ctrlRow.Children.Add(grip);

        var col=new StackPanel();
        col.Children.Add(new Border{Child=track,Margin=new Thickness(2,2,2,4)});
        col.Children.Add(ctrlRow);
        rootBar=new Border
        {
            CornerRadius=new CornerRadius(11),Padding=new Thickness(8,6,8,6),
            Background=new SolidColorBrush(Color.FromArgb(236,13,19,33)),
            BorderBrush=new SolidColorBrush(Color.FromArgb(150,255,138,0)),BorderThickness=new Thickness(1),
            Child=col
        };
        rootBar.MouseLeftButtonDown+=(_,e)=>{if(!e.Handled)try{DragMove();}catch{}};
        Content=rootBar;
        LayoutTrack();
    }

    static UIElement Sep()=>new Border{Width=1,Height=20,Background=new SolidColorBrush(Color.FromArgb(70,255,255,255)),Margin=new Thickness(6,0,6,0),VerticalAlignment=VerticalAlignment.Center};

    Button IconBtn(string icon,string tip,Action onClick)
    {
        var img=new System.Windows.Controls.Image{Width=18,Height=18,Source=SvgIcon.Load(icon,18,Brushes.White)};
        var b=new Button{Content=img,Height=30,Width=34,Margin=new Thickness(2),Padding=new Thickness(0),Background=new SolidColorBrush(Color.FromArgb(90,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand,ToolTip=tip};
        b.Click+=(_,e)=>{onClick();e.Handled=true;};
        return b;
    }

    Button TextBtn(string text,Action onClick)
    {
        var b=new Button{Content=new TextBlock{Text=text,Foreground=Brushes.White,FontWeight=FontWeights.SemiBold},Height=30,MinWidth=44,Margin=new Thickness(2),Padding=new Thickness(8,0,8,0),Background=new SolidColorBrush(Color.FromArgb(90,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        b.Click+=(_,e)=>{onClick();e.Handled=true;};
        return b;
    }

    // ===== 預覽 =====

    void ShowPreview()
    {
        media.MediaOpened+=(_,_)=>
        {
            if(media.NaturalDuration.HasTimeSpan)
            {
                var d=media.NaturalDuration.TimeSpan.TotalSeconds;
                if(d>0.1){if(Math.Abs(trimEnd-duration)<0.01)trimEnd=d;duration=d;LayoutTrack();}
            }
            media.Pause();media.Position=TimeSpan.Zero; // 顯示首幀
        };
        media.MediaEnded+=(_,_)=>{if(playing){media.Position=TimeSpan.FromSeconds(trimStart);media.Play();}};
        media.Source=new Uri(srcPath);
        media.Play(); // 觸發 MediaOpened 後立即暫停
        var host=new Border{BorderBrush=new SolidColorBrush(Color.FromArgb(235,255,138,0)),BorderThickness=new Thickness(2),Background=Brushes.Black,Child=media};
        host.MouseLeftButtonDown+=(_,e)=>{TogglePlay();e.Handled=true;};
        previewWin=new Window
        {
            WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Topmost=true,
            AllowsTransparency=true,Background=Brushes.Transparent,ShowActivated=false,
            Left=region.X,Top=region.Y,Width=Math.Max(80,region.Width),Height=Math.Max(60,region.Height),
            Content=host,Owner=this
        };
        previewWin.Show();
        Activate();
    }

    void StartTick()
    {
        tick=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(80)};
        tick.Tick+=(_,_)=>
        {
            var pos=media.Position.TotalSeconds;
            if(playing&&pos>=trimEnd-0.04){media.Position=TimeSpan.FromSeconds(trimStart);pos=trimStart;}
            if(drag==Drag.None)MovePlayhead(pos);
            timeText.Text=$"{Fmt(pos)}/{Fmt(duration)}";
        };
        tick.Start();
    }

    static string Fmt(double s){if(s<0)s=0;var t=TimeSpan.FromSeconds(s);return $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";}

    void TogglePlay()
    {
        if(exporting)return;
        playing=!playing;
        if(playing)
        {
            var pos=media.Position.TotalSeconds;
            if(pos<trimStart-0.05||pos>=trimEnd-0.05)media.Position=TimeSpan.FromSeconds(trimStart);
            media.SpeedRatio=speed;
            media.Play();
        }
        else media.Pause();
        if(playBtn.Content is System.Windows.Controls.Image img)img.Source=SvgIcon.Load(playing?"player-pause":"player-play",18,Brushes.White);
    }

    void CycleSpeed()
    {
        var i=Array.IndexOf(Speeds,speed);speed=Speeds[(i+1)%Speeds.Length];
        media.SpeedRatio=speed;
        speedBtn.Content=new TextBlock{Text=$"{speed:0.0#}X",Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};
    }

    void CycleFormat()
    {
        formatIdx=(formatIdx+1)%Formats.Length;
        formatBtn.Content=new TextBlock{Text=Formats[formatIdx],Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};
    }

    void ResetTrim()
    {
        trimStart=0;trimEnd=duration;speed=1.0;media.SpeedRatio=1.0;
        speedBtn.Content=new TextBlock{Text="1.0X",Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};
        LayoutTrack();
    }

    // ===== 修剪滑軌 =====

    double XOf(double t)=>TrackPad+Math.Clamp(t/duration,0,1)*(TrackW-TrackPad*2);
    double TOf(double x)=>Math.Clamp((x-TrackPad)/(TrackW-TrackPad*2),0,1)*duration;

    void LayoutTrack()
    {
        var xs=XOf(trimStart);var xe=XOf(trimEnd);
        Canvas.SetLeft(rangeFill,xs);rangeFill.Width=Math.Max(0,xe-xs);
        Canvas.SetLeft(startHandle,xs-startHandle.Width/2);
        Canvas.SetLeft(endHandle,xe-endHandle.Width/2);
        MovePlayhead(media.Position.TotalSeconds);
    }

    void MovePlayhead(double t)=>Canvas.SetLeft(playhead,XOf(Math.Clamp(t,trimStart,trimEnd))-1);

    void OnTrackDown(object sender,System.Windows.Input.MouseButtonEventArgs e)
    {
        if(exporting)return;
        var x=e.GetPosition(track).X;
        var ds=Math.Abs(x-XOf(trimStart));var de=Math.Abs(x-XOf(trimEnd));
        if(ds<=8&&ds<=de)drag=Drag.Start;
        else if(de<=8)drag=Drag.End;
        else{drag=Drag.Seek;SeekTo(TOf(x));}
        track.CaptureMouse();e.Handled=true;
    }

    void OnTrackMove(object sender,System.Windows.Input.MouseEventArgs e)
    {
        if(drag==Drag.None)return;
        var t=TOf(e.GetPosition(track).X);
        switch(drag)
        {
            case Drag.Start:trimStart=Math.Clamp(t,0,trimEnd-0.1);break;
            case Drag.End:trimEnd=Math.Clamp(t,trimStart+0.1,duration);break;
            case Drag.Seek:SeekTo(t);break;
        }
        LayoutTrack();
    }

    void OnTrackUp(object sender,System.Windows.Input.MouseButtonEventArgs e)
    {
        if(drag==Drag.Start)SeekTo(trimStart);
        else if(drag==Drag.End)SeekTo(Math.Max(trimStart,trimEnd-0.1));
        drag=Drag.None;
        track.ReleaseMouseCapture();
    }

    void SeekTo(double t)
    {
        media.Position=TimeSpan.FromSeconds(Math.Clamp(t,0,duration));
        MovePlayhead(t);
    }

    // ===== 導出 =====

    void Export(bool copyAfter)
    {
        if(exporting)return;
        var inv=System.Globalization.CultureInfo.InvariantCulture;
        bool fullRange=trimStart<0.05&&trimEnd>duration-0.05;
        string fmt=Formats[formatIdx];
        // 全片段 + 原速 + MP4：無需處理，直接使用原始檔
        if(fullRange&&Math.Abs(speed-1.0)<0.001&&fmt=="MP4"){Finish(srcPath,copyAfter);return;}
        var dir=IoPath.GetDirectoryName(srcPath)??".";
        var stem=IoPath.GetFileNameWithoutExtension(srcPath);
        var ext=fmt=="MP4"?".mp4":fmt=="GIF"?".gif":".webp";
        var dst=IoPath.Combine(dir,$"{stem}_trim{ext}");
        var ss=trimStart.ToString("0.###",inv);var to=trimEnd.ToString("0.###",inv);
        var sp=speed.ToString("0.###",inv);
        string setpts=Math.Abs(speed-1.0)<0.001?"":$"setpts=PTS/{sp},";
        string args=fmt switch
        {
            "GIF"=>$"-y -ss {ss} -to {to} -i \"{srcPath}\" -vf \"{setpts}fps=15,scale='min(720,iw)':-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" -an \"{dst}\"",
            "WebP"=>$"-y -ss {ss} -to {to} -i \"{srcPath}\" -vf \"{setpts}fps=20,scale='min(720,iw)':-2\" -c:v libwebp -q:v 75 -loop 0 -an \"{dst}\"",
            _=>BuildMp4Args(ss,to,sp,dst)
        };
        exporting=true;playing=false;media.Pause();
        if(playBtn.Content is System.Windows.Controls.Image img)img.Source=SvgIcon.Load("player-play",18,Brushes.White);
        ctrlRow.IsEnabled=false;track.IsEnabled=false;
        timeText.Text=L("處理中…");
        System.Threading.Tasks.Task.Run(()=>
        {
            var code=FfmpegService.RunToEnd(args);
            Dispatcher.Invoke(()=>
            {
                exporting=false;ctrlRow.IsEnabled=true;track.IsEnabled=true;
                if(code==0&&File.Exists(dst))Finish(dst,copyAfter);
                else System.Windows.MessageBox.Show(L("導出失敗。"),"BeeX DeskNest",MessageBoxButton.OK,MessageBoxImage.Warning);
            });
        });
    }

    string BuildMp4Args(string ss,string to,string sp,string dst)
    {
        bool reSpeed=Math.Abs(speed-1.0)>=0.001;
        string vf=reSpeed?$" -vf \"setpts=PTS/{sp}\"":"";
        string af=hasAudio?(reSpeed?$" -af \"atempo={sp}\" -c:a aac -b:a 192k":" -c:a aac -b:a 192k"):" -an";
        return $"-y -ss {ss} -to {to} -i \"{srcPath}\"{vf}{af} -c:v libx264 -preset veryfast -pix_fmt yuv420p -movflags +faststart \"{dst}\"";
    }

    void Finish(string outPath,bool copyAfter)
    {
        if(copyAfter)
        {
            try{var files=new System.Collections.Specialized.StringCollection();files.Add(outPath);Clipboard.SetFileDropList(files);}catch{}
        }
        try{Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{outPath}\""){UseShellExecute=true});}catch{}
        Close();
    }

    // ===== 佈局/收尾 =====

    void PlaceBar()
    {
        UpdateLayout();
        var vsB=SystemParameters.VirtualScreenTop+SystemParameters.VirtualScreenHeight;
        var vsR=SystemParameters.VirtualScreenLeft+SystemParameters.VirtualScreenWidth;
        var x=region.X;var y=region.Bottom+8;
        if(y+ActualHeight>vsB)y=Math.Max(SystemParameters.VirtualScreenTop+4,region.Y-ActualHeight-8);
        Left=Math.Max(SystemParameters.VirtualScreenLeft+4,Math.Min(x,vsR-ActualWidth-4));
        Top=y;
    }

    void Teardown()
    {
        tick?.Stop();tick=null;
        try{media.Stop();media.Close();}catch{}
        try{previewWin?.Close();}catch{}previewWin=null;
    }
}
