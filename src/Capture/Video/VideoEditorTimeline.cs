using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using Image=System.Windows.Controls.Image;
using Point=System.Windows.Point;

namespace BeeX.DeskNest;

/// <summary>Editor Film Timeline: Full-screen thumbnails, scroll wheel zoom, playhead drag/split, and trim handles at both ends. </summary>
public sealed partial class VideoEditorWindow
{
    const double StripLeft=12;
    int gripMode; bool gripDragged;
    readonly Dictionary<EditClip,Border> clipBorders=new();

    void RebuildTimeline()
    {
        totalDur=clips.Sum(c=>c.OutDuration);
        durText.Text=$"{totalDur:0.0} 秒";
        timelineCanvas.Children.Clear();clipBorders.Clear();
        double stripRight=StripLeft+totalDur*pps;
        double vw=timelineScroll.ViewportWidth>0?timelineScroll.ViewportWidth:900;
        timelineCanvas.Width=Math.Max(stripRight+StripLeft+24,vw);

        for(int i=0;i<clips.Count;i++)
        {
            var c=clips[i];double x=StripLeft+ClipStart(i)*pps,w=Math.Max(6,c.OutDuration*pps);
            var host=new Canvas{Width=w,Height=StripH,ClipToBounds=true,Background=new SolidColorBrush(Color.FromRgb(40,52,80))};
            double aspect=(c.SrcW>0&&c.SrcH>0)?(double)c.SrcW/c.SrcH:16.0/9.0;
            double tw=Math.Max(24,StripH*aspect);
            int n=Math.Max(1,(int)Math.Ceiling(w/tw));
            if(c.Thumbs.Count>0)
                for(int j=0;j<n;j++)
                {
                    int ti=(n==1)?0:(int)Math.Round((double)j*(c.Thumbs.Count-1)/(n-1));
                    ti=Math.Clamp(ti,0,c.Thumbs.Count-1);
                    var img=new Image{Width=tw,Height=StripH,Stretch=Stretch.UniformToFill,ClipToBounds=true,Source=c.Thumbs[ti]};
                    Canvas.SetLeft(img,j*tw);Canvas.SetTop(img,0);host.Children.Add(img);
                }
            var lbl=new TextBlock{Text=$"{c.Name} · {c.OutDuration:0.0}s"+(c.Speed!=1?$" · {c.Speed}x":"")+(c.Rotate!=0?$" · {c.Rotate}°":""),Foreground=Brushes.White,FontSize=10,Background=new SolidColorBrush(Color.FromArgb(130,0,0,0)),Padding=new Thickness(3,0,3,0),TextTrimming=TextTrimming.CharacterEllipsis,MaxWidth=Math.Max(20,w)};
            Canvas.SetLeft(lbl,0);Canvas.SetTop(lbl,0);host.Children.Add(lbl);
            var border=new Border{Width=w,Height=StripH,Child=host,BorderBrush=new SolidColorBrush(c==sel?Color.FromRgb(255,138,0):Color.FromArgb(120,255,255,255)),BorderThickness=new Thickness(c==sel?2:1),CornerRadius=new CornerRadius(3),IsHitTestVisible=false};
            Canvas.SetLeft(border,x);Canvas.SetTop(border,StripTop);timelineCanvas.Children.Add(border);
            clipBorders[c]=border;
        }

        AddGrip(StripLeft,true);
        AddGrip(stripRight,false);
        timelineCanvas.Children.Add(playhead);
        timelineCanvas.Children.Add(playheadKnob);
        UpdatePlayhead();
    }

    void AddGrip(double x,bool left)
    {
        var bars=new TextBlock{Text="⋮⋮",Foreground=Brushes.White,HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,FontSize=11};
        var g=new Border{Width=12,Height=StripH+8,Background=new SolidColorBrush(Color.FromRgb(28,28,28)),CornerRadius=new CornerRadius(3),BorderBrush=new SolidColorBrush(Color.FromArgb(170,255,255,255)),BorderThickness=new Thickness(1),Child=bars,IsHitTestVisible=false};
        Canvas.SetLeft(g,left?x-12:x);Canvas.SetTop(g,StripTop-4);
        timelineCanvas.Children.Add(g);
    }

    void UpdatePlayhead()
    {
        double x=StripLeft+globalPos*pps;
        playhead.X1=playhead.X2=x;playhead.Y1=StripTop-4;playhead.Y2=StripTop+StripH+4;
        playheadKnob.Points=new PointCollection{new Point(x-6,StripTop-8),new Point(x+6,StripTop-8),new Point(x,StripTop+3)};
        double vo=timelineScroll.HorizontalOffset,vw=timelineScroll.ViewportWidth;
        if(vw>0){ if(x<vo+16)timelineScroll.ScrollToHorizontalOffset(Math.Max(0,x-40)); else if(x>vo+vw-16)timelineScroll.ScrollToHorizontalOffset(x-vw+40); }
    }

    void UpdateSelectionVisual()
    {
        foreach(var kv in clipBorders)
        {
            bool s=kv.Key==sel;
            kv.Value.BorderBrush=new SolidColorBrush(s?Color.FromRgb(255,138,0):Color.FromArgb(120,255,255,255));
            kv.Value.BorderThickness=new Thickness(s?2:1);
        }
    }

    void Zoom(double factor)
    {
        pps=Math.Clamp(pps*factor,8,4000);
        RebuildTimeline();
    }

    void TimelineWheel(object s,System.Windows.Input.MouseWheelEventArgs e)
    {
        Zoom(e.Delta>0?1.25:1/1.25);e.Handled=true;
    }

    void TimelineDown(object s,System.Windows.Input.MouseButtonEventArgs e)
    {
        double x=e.GetPosition(timelineCanvas).X;
        double stripRight=StripLeft+totalDur*pps;
        if(clips.Count>0&&x>=StripLeft-16&&x<=StripLeft+6){gripMode=1;gripDragged=false;timelineCanvas.CaptureMouse();e.Handled=true;return;}
        if(clips.Count>0&&x>=stripRight-6&&x<=stripRight+16){gripMode=2;gripDragged=false;timelineCanvas.CaptureMouse();e.Handled=true;return;}
        gripMode=0;draggingHead=true;timelineCanvas.CaptureMouse();
        SeekPx(x);
        e.Handled=true;
    }

    void TimelineMove(object s,System.Windows.Input.MouseEventArgs e)
    {
        if(e.LeftButton!=System.Windows.Input.MouseButtonState.Pressed)return;
        double x=e.GetPosition(timelineCanvas).X;
        if(gripMode==1&&clips.Count>0)
        {
            var c=clips[0];double trim=(x-StripLeft)/pps;double newDur=Math.Max(0.1,c.OutDuration-trim);
            c.In=Math.Max(0,c.Out-newDur*c.Speed);gripDragged=true;RebuildTimeline();
        }
        else if(gripMode==2&&clips.Count>0)
        {
            var c=clips[^1];double target=(x-StripLeft)/pps;double newDur=Math.Max(0.1,target-ClipStart(clips.Count-1));
            double maxOut=c.SrcDuration>0?c.SrcDuration:c.In+newDur*c.Speed;
            c.Out=Math.Min(maxOut,c.In+newDur*c.Speed);gripDragged=true;RebuildTimeline();
        }
        else if(draggingHead)SeekPx(x);
    }

    void TimelineUp(object s,System.Windows.Input.MouseButtonEventArgs e)
    {
        if(timelineCanvas.IsMouseCaptured)timelineCanvas.ReleaseMouseCapture();
        if(gripDragged)
        {
            if(clips.Count>0){GenerateThumbs(clips[0]);GenerateThumbs(clips[^1]);}
            SyncPropPanel();
        }
        gripMode=0;gripDragged=false;draggingHead=false;
    }

    void SeekPx(double x)
    {
        double g=Math.Max(0,Math.Min(totalDur,(x-StripLeft)/pps));
        globalPos=g;
        int idx=ClipAt(g,out _);if(idx>=0)sel=clips[idx];
        LoadAt(g,false);
    }

    void GenerateThumbs(EditClip c)
    {
        string key=$"{c.Source}|{c.In:0.###}|{c.Out:0.###}";
        if(c.ThumbsReady&&c.ThumbKey==key)return;
        c.ThumbKey=key;
        double dur=Math.Max(0.1,c.Out-c.In);
        if(dur<=0.11&&c.SrcDuration>0)dur=c.SrcDuration;
        int count=Math.Clamp((int)Math.Ceiling(dur*1.5),4,24);
        string prefix="t_"+Guid.NewGuid().ToString("N").Substring(0,8);
        double inSec=c.In;string src=c.Source;
        System.Threading.Tasks.Task.Run(()=>
        {
            var paths=FfmpegService.ExtractThumbs(src,inSec,dur,count,(int)StripH,thumbDir,prefix);
            var list=new List<ImageSource>();
            foreach(var p in paths)
            {
                try{var bi=new BitmapImage();bi.BeginInit();bi.CacheOption=BitmapCacheOption.OnLoad;bi.UriSource=new Uri(p);bi.EndInit();bi.Freeze();list.Add(bi);}catch{}
            }
            Dispatcher.Invoke(()=>
            {
                if(c.ThumbKey==key){c.Thumbs.Clear();c.Thumbs.AddRange(list);c.ThumbsReady=true;RebuildTimeline();}
            });
        });
    }
}
