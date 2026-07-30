using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Brushes=System.Windows.Media.Brushes;
using Brush=System.Windows.Media.Brush;
using Color=System.Windows.Media.Color;
using Cursors=System.Windows.Input.Cursors;
using Button=System.Windows.Controls.Button;
using Orientation=System.Windows.Controls.Orientation;
using MediaState=System.Windows.Controls.MediaState;
using CheckBox=System.Windows.Controls.CheckBox;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;
using Image=System.Windows.Controls.Image;
using Panel=System.Windows.Controls.Panel;
using Point=System.Windows.Point;
using Size=System.Windows.Size;
using IoPath=System.IO.Path;

namespace BeeX.DeskNest;

/// <summary>
/// 非線性剪輯器：膠片式時間軸（幀縮圖鋪滿、滾輪縮放、播放頭拖動、兩端修剪把手、播放頭分割），
/// 全時間軸連續播放（跨片段自動切源），淡入淡出即時預覽；片段變速/旋轉/翻轉/調色、文字/浮水印，
/// 由 ffmpeg 統一導出。旋轉/翻轉即時預覽；調色/變速於導出套用（預覽引擎限制）。
/// </summary>
public sealed partial class VideoEditorWindow : Window
{
    public sealed class EditClip
    {
        public string Source="";
        public double SrcDuration; public bool HasAudio; public int SrcW,SrcH;
        public double In,Out;
        // 速度
        public double Speed=1; public bool PreservePitch=true; public bool MuteAfterSpeed;
        // 畫面
        public int Rotate; public bool FlipH,FlipV;
        public double CropL,CropT,CropR,CropB;   // 0..0.9 各邊裁切比例
        // 調色
        public double Exposure; public double Temperature; public double TintV; public double Shadows; public double Highlights; public double Sharpen;
        public double Brightness; public double Contrast=1; public double Saturation=1;
        // 音頻
        public bool Mute; public double Volume=1; public double FadeIn; public double FadeOut; public bool Denoise;
        public double OutDuration=>Math.Max(0.02,(Out-In)/Math.Max(0.02,Speed));
        public string Name=>IoPath.GetFileNameWithoutExtension(Source);
        public List<ImageSource> Thumbs=new(); public bool ThumbsReady; public string ThumbKey="";
        public EditClip Copy(){ var c=(EditClip)MemberwiseClone(); c.Thumbs=new(); c.ThumbsReady=false; c.ThumbKey=""; return c; }
    }
    public sealed class TextOv{ public string Text="文字"; public string Font="Microsoft YaHei"; public int Size=42; public Color Col=Colors.White; public bool Bold=true; public double NX=0.5,NY=0.86; public double Start,End=99999; public bool Hidden; }
    public sealed class ImgOv{ public string Path=""; public double NX=0.82,NY=0.06; public double Scale=1; public double Opacity=0.9; public double Start,End=99999; public bool Hidden; public bool IsLogo; }

    readonly List<EditClip> clips=new();
    readonly List<TextOv> texts=new();
    readonly List<ImgOv> imgs=new();
    EditClip? sel;

    // 轉場 / 背景音樂 / 時間戳
    int transitionType;            // 0=無 1=疊化
    double transitionDur=0.5;
    string? bgmPath; double bgmVolume=0.8; double bgmIn; double bgmDur;
    bool showTimestamp;
    // 導出設定
    string expFormat="mp4"; int expScaleH; int expFps=30; int expQuality=1; string? expPath; int expBitrateK;
    // 專案
    string? projectPath;

    readonly string outputDir;
    readonly string language;
    readonly string thumbDir;
    string L(string v)=>Localization.T(v,language);

    MediaElement player=new(){LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Manual,ScrubbingEnabled=true,Stretch=Stretch.Uniform};
    Canvas overlayCanvas=new();
    System.Windows.Shapes.Rectangle fadeRect=new(){Fill=Brushes.Black,Opacity=0,IsHitTestVisible=false};
    System.Windows.Shapes.Rectangle colorRect=new(){Opacity=0,IsHitTestVisible=false};
    TextBlock timeText=new(){Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0),MinWidth=110};
    Button playBtn=new();
    Image? playIcon;
    WrapPanel propPanel=new();

    // 時間軸
    ScrollViewer timelineScroll=new(){HorizontalScrollBarVisibility=ScrollBarVisibility.Auto,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,Background=new SolidColorBrush(Color.FromRgb(16,23,40))};
    Canvas timelineCanvas=new(){Height=104,Background=Brushes.Transparent};
    System.Windows.Shapes.Line playhead=new(){Stroke=Brushes.White,StrokeThickness=2,Y1=0,Y2=104};
    System.Windows.Shapes.Polygon playheadKnob=new(){Fill=Brushes.White};
    Border durBadge=new(){Background=new SolidColorBrush(Color.FromArgb(210,40,40,40)),CornerRadius=new CornerRadius(9),Padding=new Thickness(8,2,8,2),VerticalAlignment=VerticalAlignment.Center};
    TextBlock durText=new(){Foreground=Brushes.White,FontSize=12,FontWeight=FontWeights.Bold};
    const double StripH=76, StripTop=14;
    double pps=60;                 // 像素/秒（滾輪縮放）
    double globalPos;              // 全域播放頭（秒）
    double totalDur;

    Border exportOverlay=new(){Background=new SolidColorBrush(Color.FromArgb(210,10,15,26)),Visibility=Visibility.Collapsed};
    TextBlock exportText=new(){Foreground=Brushes.White,FontSize=18,HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};

    DispatcherTimer playTimer=new(){Interval=TimeSpan.FromMilliseconds(40)};
    bool playing, draggingHead;
    int curIdx=-1; double pendingPos=-1; bool pendingPlay;
    System.Windows.Threading.DispatcherUnhandledExceptionEventHandler? dispHandler;
    bool errShown;

    public VideoEditorWindow(string firstClip,string outputDir,string language)
    {
        this.outputDir=outputDir;this.language=language;
        thumbDir=IoPath.Combine(IoPath.GetTempPath(),"BeeX_EditThumbs_"+DateTime.Now.ToString("yyyyMMddHHmmss"));
        Title=L("BeeX 剪輯器");Width=1200;Height=800;MinWidth=860;MinHeight=560;WindowStartupLocation=WindowStartupLocation.CenterScreen;
        Background=new SolidColorBrush(Color.FromRgb(13,19,33));
        BuildUi();
        playTimer.Tick+=(_,_)=>{try{OnTick();}catch{}};
        player.MediaOpened+=(_,_)=>{try{OnMediaOpened();}catch{}};
        player.MediaEnded+=(_,_)=>{try{OnMediaEnded();}catch{}};
        player.MediaFailed+=(_,_)=>{ /* 忽略解碼失敗，保持存活 */ };
        SizeChanged+=(_,_)=>{try{RenderOverlays();}catch{}};
        dispHandler=(s,e)=>{ e.Handled=true; if(!errShown){errShown=true;try{System.Windows.MessageBox.Show(this,"剪輯器發生錯誤（已攔截，程式未崩潰）：\n"+e.Exception.Message);}catch{}} };
        try{System.Windows.Application.Current.DispatcherUnhandledException+=dispHandler;}catch{}
        Loaded+=(_,_)=>{ try{ if(!string.IsNullOrEmpty(firstClip)&&File.Exists(firstClip))AddClipFile(firstClip,true); }catch(Exception ex){try{System.Windows.MessageBox.Show(this,"載入片段失敗："+ex.Message);}catch{}} };
    }

    void BuildUi()
    {
        var grid=new Grid();
        grid.RowDefinitions.Add(new RowDefinition{Height=new GridLength(1,GridUnitType.Star)});
        grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        grid.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});

        var previewHost=new Grid{Background=Brushes.Black,ClipToBounds=true};
        previewHost.Children.Add(new Viewbox{Stretch=Stretch.Uniform,Child=player});
        previewHost.Children.Add(colorRect);
        previewHost.Children.Add(fadeRect);
        previewHost.Children.Add(overlayCanvas);
        Grid.SetRow(previewHost,0);grid.Children.Add(previewHost);

        // 傳輸列（自動換行）
        playIcon=Ico("player-play");
        playBtn=new Button{Content=playIcon,Height=32,MinWidth=40,Margin=new Thickness(3,2,3,2),Padding=new Thickness(10,0,10,0),Background=new SolidColorBrush(Color.FromArgb(90,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        playBtn.Click+=(_,_)=>TogglePlay();
        var fbBtn=IconBtn("player-skip-back",null,()=>FrameStep(-1));
        var ffBtn=IconBtn("player-skip-forward",null,()=>FrameStep(1));
        var splitBtn=IconBtn("scissors","分割",SplitAtPlayhead);
        var delBtn=IconBtn("trash","刪除",DeleteSelected);
        var dupBtn=IconBtn("copy","複製",DuplicateClip);
        var mergeBtn=IconBtn("arrow-merge","合併",MergeSelected);
        var lBtn=IconBtn("chevron-left",null,()=>MoveSelected(-1));
        var rBtn=IconBtn("chevron-right",null,()=>MoveSelected(1));
        var addBtn=IconBtn("movie","導入",ImportClip);
        var undoBtn=IconBtn("arrow-back-up","撤銷",Undo);
        var redoBtn=IconBtn("arrow-forward-up","重做",Redo);
        var zoomIn=IconBtn("zoom-in",null,()=>Zoom(1.3));
        var zoomOut=IconBtn("zoom-out",null,()=>Zoom(1/1.3));
        var projBtn=IconBtn("folder","專案",ProjectMenu);
        durBadge.Child=durText;
        var transport=new WrapPanel{Margin=new Thickness(8,6,8,4)};
        foreach(var c in new UIElement[]{playBtn,fbBtn,ffBtn,timeText,splitBtn,delBtn,dupBtn,mergeBtn,lBtn,rBtn,addBtn,undoBtn,redoBtn,zoomOut,zoomIn,projBtn,durBadge})transport.Children.Add(c);
        Grid.SetRow(transport,1);grid.Children.Add(transport);

        // 膠片時間軸
        timelineCanvas.Children.Add(playhead);
        playheadKnob.Points=new PointCollection{new Point(-6,0),new Point(6,0),new Point(0,10)};
        timelineCanvas.Children.Add(playheadKnob);
        timelineScroll.Content=timelineCanvas;
        timelineScroll.Height=124;timelineScroll.Margin=new Thickness(8,0,8,0);
        timelineCanvas.MouseLeftButtonDown+=TimelineDown;
        timelineCanvas.MouseMove+=TimelineMove;
        timelineCanvas.MouseLeftButtonUp+=TimelineUp;
        timelineCanvas.MouseWheel+=TimelineWheel;
        Grid.SetRow(timelineScroll,2);grid.Children.Add(timelineScroll);

        var propScroll=new ScrollViewer{HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=214,Content=propPanel,Margin=new Thickness(8,4,8,8)};
        propPanel.Margin=new Thickness(2);
        BuildPropPanel();
        Grid.SetRow(propScroll,3);grid.Children.Add(propScroll);

        exportOverlay.Child=exportText;
        var rootGrid=new Grid();rootGrid.Children.Add(grid);rootGrid.Children.Add(exportOverlay);
        Content=rootGrid;
    }

    void BuildPropPanel(){ propPanel.Children.Clear(); propPanel.Children.Add(BuildTabs()); }

    Border Group(string title,UIElement[] items)
    {
        var sp=new WrapPanel();foreach(var it in items)sp.Children.Add(it);
        var outer=new StackPanel();
        outer.Children.Add(new TextBlock{Text=title,Foreground=new SolidColorBrush(Color.FromArgb(180,255,255,255)),FontSize=11,Margin=new Thickness(6,0,0,2)});
        outer.Children.Add(sp);
        return new Border{BorderBrush=new SolidColorBrush(Color.FromArgb(60,255,255,255)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(8),Padding=new Thickness(6),Margin=new Thickness(4,2,4,2),Child=outer};
    }
    static TextBlock Lbl(string t)=>new(){Text=t,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,0,2,0)};

    FrameworkElement MakeSlider(string name,double min,double max,double val,Action<double> onChg,out Slider slider)
    {
        var s=new Slider{Minimum=min,Maximum=max,Value=val,Width=104,VerticalAlignment=VerticalAlignment.Center};
        var tb=new TextBlock{Foreground=Brushes.White,FontSize=11,Width=64,VerticalAlignment=VerticalAlignment.Center,Text=$"{name} {val:0.##}"};
        s.ValueChanged+=(_,e)=>{tb.Text=$"{name} {e.NewValue:0.##}";onChg(e.NewValue);};
        var sp=new StackPanel{Orientation=Orientation.Horizontal};sp.Children.Add(tb);sp.Children.Add(s);
        slider=s;return sp;
    }

    Button Mk(string text,Action onClick)
    {
        var b=new Button{Content=new TextBlock{Text=text,Foreground=Brushes.White},Height=32,MinWidth=42,Margin=new Thickness(3,2,3,2),Padding=new Thickness(9,0,9,0),Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(90,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        b.Click+=(_,_)=>onClick();
        return b;
    }

    // tabler SVG 圖標按鈕（圖標已內置到 Assets/Icons，不依賴外部 tabler 資料夾）
    Image Ico(string name,double size=18)=>new(){Source=SvgIcon.Load(name,size,Brushes.White),Width=size,Height=size,VerticalAlignment=VerticalAlignment.Center};
    Button IconBtn(string icon,string? text,Action onClick,double iconSize=18)
    {
        var sp=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        var img=Ico(icon,iconSize);img.Margin=string.IsNullOrEmpty(text)?new Thickness(0):new Thickness(0,0,4,0);
        sp.Children.Add(img);
        if(!string.IsNullOrEmpty(text))sp.Children.Add(new TextBlock{Text=text,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,FontSize=12});
        var b=new Button{Content=sp,Height=32,MinWidth=34,Margin=new Thickness(3,2,3,2),Padding=new Thickness(8,0,8,0),Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(90,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        b.Click+=(_,_)=>onClick();
        return b;
    }

    // ---- 片段管理 ----
    void ImportClip()
    {
        var dlg=new Microsoft.Win32.OpenFileDialog{Filter="影片|*.mp4;*.mov;*.mkv;*.avi;*.webm|所有檔案|*.*"};
        if(dlg.ShowDialog(this)==true)AddClipFile(dlg.FileName,false);
    }
    void AddClipFile(string path,bool select)
    {
        var c=new EditClip{Source=path,In=0,Out=0};
        clips.Add(c);
        if(select||sel==null)sel=c;
        RebuildTimeline();
        // 後台探測時長/音軌/尺寸，避免阻塞 UI 造成白屏
        System.Threading.Tasks.Task.Run(()=>
        {
            var (dur,aud,w,h)=FfmpegService.Probe(path);
            Dispatcher.Invoke(()=>
            {
                try
                {
                    c.SrcDuration=dur;c.HasAudio=aud;c.SrcW=w;c.SrcH=h;
                    if(c.Out<=0)c.Out=dur>0?dur:0;
                    RebuildTimeline();GenerateThumbs(c);
                    if(sel==c)LoadAt(0,false);
                }
                catch{}
            });
        });
    }
    void DeleteSelected()
    {
        if(sel==null)return;Snapshot();int i=clips.IndexOf(sel);clips.Remove(sel);sel=clips.Count>0?clips[Math.Min(i,clips.Count-1)]:null;
        RebuildTimeline();LoadAt(0,false);
    }
    void MoveSelected(int dir)
    {
        if(sel==null)return;int i=clips.IndexOf(sel);int j=i+dir;if(j<0||j>=clips.Count)return;
        Snapshot();(clips[i],clips[j])=(clips[j],clips[i]);RebuildTimeline();
    }
    void SplitAtPlayhead()
    {
        int idx=ClipAt(globalPos,out double local);if(idx<0)return;var c=clips[idx];
        double srcCut=c.In+local*c.Speed;
        if(srcCut<=c.In+0.05||srcCut>=c.Out-0.05)return;
        Snapshot();
        var right=c.Copy();right.In=srcCut;c.Out=srcCut;
        clips.Insert(idx+1,right);RebuildTimeline();GenerateThumbs(right);GenerateThumbs(c);
    }

    double ClipStart(int idx){double s=0;for(int i=0;i<idx&&i<clips.Count;i++)s+=clips[i].OutDuration;return s;}
    int ClipAt(double g,out double local)
    {
        double s=0;for(int i=0;i<clips.Count;i++){double d=clips[i].OutDuration;if(g<s+d||i==clips.Count-1){local=Math.Max(0,Math.Min(d,g-s));return i;}s+=d;}
        local=0;return clips.Count-1;
    }

    // ---- 播放（跨片段連續） ----
    void TogglePlay(){ if(playing)Pause();else Play(); }
    void Play()
    {
        if(clips.Count==0)return;
        if(globalPos>=totalDur-0.05)globalPos=0;
        playing=true;if(playIcon!=null)playIcon.Source=SvgIcon.Load("player-pause",18,Brushes.White);
        LoadAt(globalPos,true);playTimer.Start();
    }
    void Pause(){ playing=false;if(playIcon!=null)playIcon.Source=SvgIcon.Load("player-play",18,Brushes.White);try{player.Pause();}catch{}playTimer.Stop(); }

    void LoadAt(double g,bool autoplay)
    {
        if(clips.Count==0){try{player.Source=null;}catch{}return;}
        g=Math.Max(0,Math.Min(totalDur,g));globalPos=g;
        int idx=ClipAt(g,out double local);var c=clips[idx];
        sel=c;curIdx=idx;
        double srcPos=c.In+local*c.Speed;
        try
        {
            var uri=new Uri(c.Source);
            player.SpeedRatio=Math.Max(0.25,c.Speed);
            if(player.Source==null||player.Source.OriginalString!=uri.OriginalString)
            {
                pendingPos=srcPos;pendingPlay=autoplay;player.Source=uri;
            }
            else
            {
                player.Position=TimeSpan.FromSeconds(srcPos);
                if(autoplay)player.Play();else player.Pause();
            }
        }catch{}
        ApplyPreviewTransform();UpdatePlayhead();UpdateSelectionVisual();SyncPropPanel();
    }

    void OnMediaOpened()
    {
        if(curIdx>=0&&curIdx<clips.Count){var c=clips[curIdx];if(c.SrcDuration<=0&&player.NaturalDuration.HasTimeSpan){c.SrcDuration=player.NaturalDuration.TimeSpan.TotalSeconds;if(c.Out<=0)c.Out=c.SrcDuration;RebuildTimeline();}}
        if(pendingPos>=0){try{player.Position=TimeSpan.FromSeconds(pendingPos);}catch{}if(pendingPlay)try{player.Play();}catch{}else try{player.Pause();}catch{}pendingPos=-1;}
        ApplyPreviewTransform();
    }
    void OnMediaEnded()=>AdvanceClip();
    void AdvanceClip()
    {
        if(curIdx+1<clips.Count){globalPos=ClipStart(curIdx+1)+0.001;LoadAt(globalPos,playing);}
        else{Pause();globalPos=totalDur;UpdatePlayhead();}
    }

    void OnTick()
    {
        if(!playing||curIdx<0||curIdx>=clips.Count)return;
        var c=clips[curIdx];double pos=player.Position.TotalSeconds;
        if(pos>=c.Out-0.04){AdvanceClip();return;}
        double local=(pos-c.In)/Math.Max(0.02,c.Speed);
        globalPos=ClipStart(curIdx)+Math.Max(0,local);
        UpdatePlayhead();UpdateFade();
        timeText.Text=$"{Fmt(globalPos)} / {Fmt(totalDur)}";
    }
    static string Fmt(double s){s=Math.Max(0,s);return $"{(int)(s/60):00}:{(int)(s%60):00}.{(int)((s%1)*10)}";}

    // ---- 預覽變換 / 淡入淡出 / 調色 ----
    void ApplyPreviewTransform()
    {
        if(curIdx<0||curIdx>=clips.Count)return;var c=clips[curIdx];
        var tg=new TransformGroup();
        tg.Children.Add(new ScaleTransform(c.FlipH?-1:1,c.FlipV?-1:1));
        tg.Children.Add(new RotateTransform(c.Rotate));
        player.RenderTransformOrigin=new Point(0.5,0.5);player.RenderTransform=tg;
        ApplyColorPreview();RenderOverlays();UpdateFade();
    }
    void ApplyColorPreview()
    {
        if(curIdx<0||curIdx>=clips.Count){colorRect.Opacity=0;return;}
        double b=clips[curIdx].Brightness;
        colorRect.Fill=b>=0?Brushes.White:Brushes.Black;colorRect.Opacity=Math.Min(0.65,Math.Abs(b)*0.65);
    }
    void UpdateFade()
    {
        if(transitionType==0||curIdx<0||curIdx>=clips.Count){fadeRect.Opacity=0;return;}
        var c=clips[curIdx];double local=globalPos-ClipStart(curIdx);double od=c.OutDuration;double F=Math.Max(0.1,transitionDur);
        double op=0;
        if(curIdx>0&&local<F)op=Math.Max(op,(F-local)/F);
        if(curIdx<clips.Count-1&&local>od-F)op=Math.Max(op,(local-(od-F))/F);
        fadeRect.Opacity=Math.Max(0,Math.Min(1,op));
    }

    // ---- 疊加預覽 ----
    void RenderOverlays()
    {
        overlayCanvas.Children.Clear();
        double pw=overlayCanvas.ActualWidth,ph=overlayCanvas.ActualHeight;if(pw<2||ph<2)return;
        foreach(var t in texts)
        {
            if(t.Hidden)continue;
            var tb=new TextBlock{Text=t.Text,Foreground=new SolidColorBrush(t.Col),FontSize=Math.Max(10,t.Size*ph/720.0),FontWeight=t.Bold?FontWeights.Bold:FontWeights.Normal};
            try{tb.FontFamily=new System.Windows.Media.FontFamily(t.Font);}catch{}
            tb.Measure(new Size(pw,ph));
            Canvas.SetLeft(tb,t.NX*pw-tb.DesiredSize.Width/2);Canvas.SetTop(tb,t.NY*ph-tb.DesiredSize.Height/2);
            overlayCanvas.Children.Add(tb);
        }
        foreach(var im in imgs)
        {
            if(im.Hidden)continue;
            try{var img=new Image{Source=new BitmapImage(new Uri(im.Path)),Width=pw*0.18*im.Scale,Stretch=Stretch.Uniform,Opacity=im.Opacity};
                img.Measure(new Size(pw,ph));
                Canvas.SetLeft(img,im.NX*pw-img.DesiredSize.Width/2);Canvas.SetTop(img,im.NY*ph-img.DesiredSize.Height/2);
                overlayCanvas.Children.Add(img);}catch{}
        }
        if(showTimestamp)
        {
            var ts=new TextBlock{Text=Fmt(globalPos),Foreground=Brushes.White,FontWeight=FontWeights.Bold,FontSize=Math.Max(10,22*ph/720.0),Background=new SolidColorBrush(Color.FromArgb(120,0,0,0)),Padding=new Thickness(4,1,4,1)};
            Canvas.SetLeft(ts,ph*0.02);Canvas.SetTop(ts,ph*0.02);overlayCanvas.Children.Add(ts);
        }
    }

    void AddText()
    {
        Snapshot();
        texts.Add(new TextOv{Text="雙擊編輯文字",NX=0.5,NY=0.86,Start=0,End=totalDur>0?totalDur:99999});
        RefreshOverlayList();RenderOverlays();
    }
    void AddWatermark()
    {
        var dlg=new Microsoft.Win32.OpenFileDialog{Filter="圖片|*.png;*.jpg;*.jpeg;*.bmp"};
        if(dlg.ShowDialog(this)!=true)return;
        Snapshot();
        imgs.Add(new ImgOv{Path=dlg.FileName,NX=0.82,NY=0.12,Scale=1,Opacity=0.9,Start=0,End=totalDur>0?totalDur:99999});
        RefreshOverlayList();RenderOverlays();
    }
    string? InputBox(string prompt)
    {
        var w=new Window{Width=380,Height=170,WindowStyle=WindowStyle.ToolWindow,WindowStartupLocation=WindowStartupLocation.CenterOwner,Owner=this,Title=prompt,Background=new SolidColorBrush(Color.FromRgb(24,32,52))};
        var tb=new TextBox{Margin=new Thickness(12),FontSize=15,MinHeight=30};
        var ok=new Button{Content=L("確定"),Width=80,Height=30,Margin=new Thickness(6),IsDefault=true};
        var sp=new StackPanel{Margin=new Thickness(8)};
        sp.Children.Add(new TextBlock{Text=prompt,Foreground=Brushes.White,Margin=new Thickness(6)});sp.Children.Add(tb);
        var row=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=System.Windows.HorizontalAlignment.Right};row.Children.Add(ok);sp.Children.Add(row);
        w.Content=sp;string? res=null;ok.Click+=(_,_)=>{res=tb.Text;w.DialogResult=true;};w.ShowDialog();return res;
    }

    void SyncPropPanel()
    {
        if(sel==null)return;
        SyncTabs();
    }

    protected override void OnClosed(EventArgs e)
    {
        try{if(dispHandler!=null)System.Windows.Application.Current.DispatcherUnhandledException-=dispHandler;}catch{}
        try{autosaveTimer?.Stop();}catch{}
        try{playTimer.Stop();player.Close();}catch{}
        try{if(Directory.Exists(thumbDir))Directory.Delete(thumbDir,true);}catch{}
        base.OnClosed(e);
    }
}
