using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes=System.Windows.Media.Brushes;
using Brush=System.Windows.Media.Brush;
using Color=System.Windows.Media.Color;
using Cursors=System.Windows.Input.Cursors;
using Button=System.Windows.Controls.Button;
using CheckBox=System.Windows.Controls.CheckBox;
using ComboBox=System.Windows.Controls.ComboBox;
using TextBox=System.Windows.Controls.TextBox;
using ListBox=System.Windows.Controls.ListBox;
using TabControl=System.Windows.Controls.TabControl;
using TabItem=System.Windows.Controls.TabItem;
using Orientation=System.Windows.Controls.Orientation;
using ColorConverter=System.Windows.Media.ColorConverter;
using IoPath=System.IO.Path;

namespace BeeX.DeskNest;

/// <summary>Clip Editor Properties Panel (tabs) + Undo/Redo + Copy/Merge/Frame-by-Frame + Overlay Object Management. </summary>
public sealed partial class VideoEditorWindow
{
    bool syncing;
    readonly List<Action> syncers=new();
    readonly List<string> undoStack=new(), redoStack=new();
    ListBox ovList=new();
    StackPanel ovEditPanel=new();
    static readonly Brush Dark=new SolidColorBrush(Color.FromRgb(20,28,48));

    // ---- Undo/Redo (Entire Project JSON Snapshot) ----
    void Snapshot(){ try{ undoStack.Add(ProjectToJson()); if(undoStack.Count>50)undoStack.RemoveAt(0); redoStack.Clear(); }catch{} }
    void Undo(){ if(undoStack.Count==0)return; try{ redoStack.Add(ProjectToJson()); var s=undoStack[^1];undoStack.RemoveAt(undoStack.Count-1); LoadProjectJson(s); }catch{} }
    void Redo(){ if(redoStack.Count==0)return; try{ undoStack.Add(ProjectToJson()); var s=redoStack[^1];redoStack.RemoveAt(redoStack.Count-1); LoadProjectJson(s); }catch{} }

    void FrameStep(int dir){ double step=1.0/Math.Max(1,expFps); globalPos=Math.Max(0,Math.Min(totalDur,globalPos+dir*step)); LoadAt(globalPos,false); }
    void DuplicateClip(){ if(sel==null)return;Snapshot();int i=clips.IndexOf(sel);var c=sel.Copy();clips.Insert(i+1,c);RebuildTimeline();GenerateThumbs(c); }
    void MergeSelected()
    {
        if(sel==null)return;int i=clips.IndexOf(sel);if(i+1>=clips.Count)return;var b=clips[i+1];
        if(b.Source!=sel.Source||Math.Abs(b.In-sel.Out)>0.1){System.Windows.MessageBox.Show(this,"只能合併相鄰且連續的同源片段。");return;}
        Snapshot();sel.Out=b.Out;clips.RemoveAt(i+1);RebuildTimeline();GenerateThumbs(sel);
    }

    // ---- Page Break ----
    FrameworkElement BuildTabs()
    {
        syncers.Clear();
        var tc=new TabControl{Background=Brushes.Transparent,BorderThickness=new Thickness(0),Height=196,MinWidth=760};
        tc.Items.Add(Tab("速度",SpeedTab()));
        tc.Items.Add(Tab("畫面",PictureTab()));
        tc.Items.Add(Tab("調色",ColorTab()));
        tc.Items.Add(Tab("音頻",AudioTab()));
        tc.Items.Add(Tab("文字/浮水印",TextTab()));
        tc.Items.Add(Tab("轉場",TransitionTab()));
        tc.Items.Add(Tab("導出",ExportTab()));
        SyncTabs();
        EnsureAutosave();
        return tc;
    }
    TabItem Tab(string h,UIElement content)=>new(){Header=h,Content=new ScrollViewer{VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Background=Dark,Content=content}};
    WrapPanel Wrap()=>new(){Margin=new Thickness(6),Background=Dark};

    void SyncTabs(){ syncing=true; foreach(var a in syncers){try{a();}catch{}} syncing=false; }

    // Binding Assistance
    FrameworkElement ClipSlider(string name,double min,double max,Func<EditClip,double> get,Action<EditClip,double> set,bool live=false)
    {
        var s=new Slider{Minimum=min,Maximum=max,Width=138,VerticalAlignment=VerticalAlignment.Center};
        var tb=new TextBlock{Foreground=Brushes.White,FontSize=11,Width=82,VerticalAlignment=VerticalAlignment.Center};
        s.ValueChanged+=(_,e)=>{tb.Text=$"{name} {e.NewValue:0.##}";if(syncing||sel==null)return;set(sel,e.NewValue);if(live)ApplyColorPreview();};
        syncers.Add(()=>{double v=sel!=null?get(sel):min;s.Value=Math.Max(min,Math.Min(max,v));tb.Text=$"{name} {v:0.##}";});
        var sp=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(4,2,4,2)};sp.Children.Add(tb);sp.Children.Add(s);return sp;
    }
    FrameworkElement ClipCheck(string label,Func<EditClip,bool> get,Action<EditClip,bool> set)
    {
        var c=new CheckBox{Content=label,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,2,8,2)};
        c.Checked+=(_,_)=>{if(!syncing&&sel!=null)set(sel,true);};
        c.Unchecked+=(_,_)=>{if(!syncing&&sel!=null)set(sel,false);};
        syncers.Add(()=>{if(sel!=null)c.IsChecked=get(sel);});
        return c;
    }
    FrameworkElement GlobalSlider(string name,double min,double max,Func<double> get,Action<double> set)
    {
        var s=new Slider{Minimum=min,Maximum=max,Width=138,VerticalAlignment=VerticalAlignment.Center};
        var tb=new TextBlock{Foreground=Brushes.White,FontSize=11,Width=90,VerticalAlignment=VerticalAlignment.Center};
        s.ValueChanged+=(_,e)=>{tb.Text=$"{name} {e.NewValue:0.##}";if(!syncing)set(e.NewValue);};
        syncers.Add(()=>{double v=get();s.Value=Math.Max(min,Math.Min(max,v));tb.Text=$"{name} {v:0.##}";});
        var sp=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(4,2,4,2)};sp.Children.Add(tb);sp.Children.Add(s);return sp;
    }
    FrameworkElement GlobalCheck(string label,Func<bool> get,Action<bool> set)
    {
        var c=new CheckBox{Content=label,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,2,8,2)};
        c.Checked+=(_,_)=>{if(!syncing){set(true);RenderOverlays();}};
        c.Unchecked+=(_,_)=>{if(!syncing){set(false);RenderOverlays();}};
        syncers.Add(()=>c.IsChecked=get());
        return c;
    }

    UIElement SpeedTab()
    {
        var w=Wrap();
        w.Children.Add(Txt("速度"));
        var speeds=new[]{0.25,0.5,1,1.5,2,4};
        var cb=new ComboBox{Width=80,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,2,4,2)};
        foreach(var sp in speeds)cb.Items.Add($"{sp}x");
        cb.SelectionChanged+=(_,_)=>{if(syncing||sel==null||cb.SelectedIndex<0)return;Snapshot();sel.Speed=speeds[cb.SelectedIndex];RebuildTimeline();};
        syncers.Add(()=>{if(sel!=null){int k=Array.IndexOf(speeds,sel.Speed);cb.SelectedIndex=k>=0?k:2;}});
        w.Children.Add(cb);
        w.Children.Add(ClipCheck("保持音調",c=>c.PreservePitch,(c,v)=>c.PreservePitch=v));
        w.Children.Add(ClipCheck("變速後靜音",c=>c.MuteAfterSpeed,(c,v)=>c.MuteAfterSpeed=v));
        w.Children.Add(IconBtn("copy","套用到全部",()=>{if(sel!=null){Snapshot();foreach(var c in clips)c.Speed=sel.Speed;RebuildTimeline();}}));
        return w;
    }
    UIElement PictureTab()
    {
        var w=Wrap();
        var rots=new[]{0,90,180,270};
        var cb=new ComboBox{Width=90,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,2,4,2)};
        foreach(var r in rots)cb.Items.Add($"{r}°");
        cb.SelectionChanged+=(_,_)=>{if(syncing||sel==null||cb.SelectedIndex<0)return;sel.Rotate=rots[cb.SelectedIndex];ApplyPreviewTransform();RebuildTimeline();};
        syncers.Add(()=>{if(sel!=null){int k=Array.IndexOf(rots,sel.Rotate);cb.SelectedIndex=k>=0?k:0;}});
        w.Children.Add(Txt("旋轉"));w.Children.Add(cb);
        w.Children.Add(ClipCheck("水平翻轉",c=>c.FlipH,(c,v)=>{c.FlipH=v;ApplyPreviewTransform();}));
        w.Children.Add(ClipCheck("垂直翻轉",c=>c.FlipV,(c,v)=>{c.FlipV=v;ApplyPreviewTransform();}));
        w.Children.Add(ClipSlider("裁左",0,0.9,c=>c.CropL,(c,v)=>c.CropL=v));
        w.Children.Add(ClipSlider("裁上",0,0.9,c=>c.CropT,(c,v)=>c.CropT=v));
        w.Children.Add(ClipSlider("裁右",0,0.9,c=>c.CropR,(c,v)=>c.CropR=v));
        w.Children.Add(ClipSlider("裁下",0,0.9,c=>c.CropB,(c,v)=>c.CropB=v));
        w.Children.Add(IconBtn("restore","恢復原始",()=>{if(sel!=null){Snapshot();sel.Rotate=0;sel.FlipH=sel.FlipV=false;sel.CropL=sel.CropT=sel.CropR=sel.CropB=0;ApplyPreviewTransform();RebuildTimeline();SyncTabs();}}));
        return w;
    }
    UIElement ColorTab()
    {
        var w=Wrap();
        w.Children.Add(ClipSlider("曝光",-1,1,c=>c.Exposure,(c,v)=>c.Exposure=v));
        w.Children.Add(ClipSlider("色溫",-1,1,c=>c.Temperature,(c,v)=>c.Temperature=v));
        w.Children.Add(ClipSlider("色調",-1,1,c=>c.TintV,(c,v)=>c.TintV=v));
        w.Children.Add(ClipSlider("陰影",-1,1,c=>c.Shadows,(c,v)=>c.Shadows=v));
        w.Children.Add(ClipSlider("高光",-1,1,c=>c.Highlights,(c,v)=>c.Highlights=v));
        w.Children.Add(ClipSlider("銳化",0,3,c=>c.Sharpen,(c,v)=>c.Sharpen=v));
        w.Children.Add(ClipSlider("亮度",-1,1,c=>c.Brightness,(c,v)=>c.Brightness=v,true));
        w.Children.Add(ClipSlider("對比",0,2,c=>c.Contrast,(c,v)=>c.Contrast=v));
        w.Children.Add(ClipSlider("飽和",0,3,c=>c.Saturation,(c,v)=>c.Saturation=v));
        w.Children.Add(IconBtn("restore","重置",()=>{if(sel!=null){Snapshot();sel.Exposure=sel.Temperature=sel.TintV=sel.Shadows=sel.Highlights=sel.Sharpen=0;sel.Brightness=0;sel.Contrast=1;sel.Saturation=1;ApplyColorPreview();SyncTabs();}}));
        w.Children.Add(IconBtn("copy","套用到全部",()=>{if(sel!=null){Snapshot();foreach(var c in clips){c.Exposure=sel.Exposure;c.Temperature=sel.Temperature;c.TintV=sel.TintV;c.Shadows=sel.Shadows;c.Highlights=sel.Highlights;c.Sharpen=sel.Sharpen;c.Brightness=sel.Brightness;c.Contrast=sel.Contrast;c.Saturation=sel.Saturation;}}}));
        return w;
    }
    UIElement AudioTab()
    {
        var w=Wrap();
        w.Children.Add(ClipCheck("原聲靜音",c=>c.Mute,(c,v)=>c.Mute=v));
        w.Children.Add(ClipSlider("音量",0,2,c=>c.Volume,(c,v)=>c.Volume=v));
        w.Children.Add(ClipSlider("淡入",0,5,c=>c.FadeIn,(c,v)=>c.FadeIn=v));
        w.Children.Add(ClipSlider("淡出",0,5,c=>c.FadeOut,(c,v)=>c.FadeOut=v));
        w.Children.Add(ClipCheck("基礎降噪",c=>c.Denoise,(c,v)=>c.Denoise=v));
        w.Children.Add(IconBtn("music","背景音樂",PickBgm));
        w.Children.Add(IconBtn("x","移除BGM",()=>{Snapshot();bgmPath=null;}));
        w.Children.Add(GlobalSlider("BGM音量",0,2,()=>bgmVolume,v=>bgmVolume=v));
        return w;
    }
    void PickBgm()
    {
        var dlg=new Microsoft.Win32.OpenFileDialog{Filter="音頻|*.mp3;*.wav;*.m4a;*.aac;*.flac|所有檔案|*.*"};
        if(dlg.ShowDialog(this)!=true)return;Snapshot();bgmPath=dlg.FileName;
        var (d,_,_,_)=FfmpegService.Probe(bgmPath);bgmDur=d;bgmIn=0;
    }
    UIElement TextTab()
    {
        var w=new StackPanel{Margin=new Thickness(6),Background=Dark};
        var row=new WrapPanel();
        row.Children.Add(IconBtn("typography","文字",AddText));
        row.Children.Add(IconBtn("photo","浮水印",AddWatermark));
        row.Children.Add(IconBtn("star","Logo",AddLogo));
        row.Children.Add(GlobalCheck("時間戳",()=>showTimestamp,v=>showTimestamp=v));
        row.Children.Add(IconBtn("trash","刪除",DeleteOverlay));
        row.Children.Add(IconBtn("eye","顯隱",ToggleOverlayHidden));
        row.Children.Add(IconBtn("trash","清空",()=>{Snapshot();texts.Clear();imgs.Clear();RefreshOverlayList();RenderOverlays();}));
        w.Children.Add(row);
        ovList=new ListBox{Height=56,Background=new SolidColorBrush(Color.FromRgb(28,36,58)),Foreground=Brushes.White,BorderThickness=new Thickness(0),Margin=new Thickness(0,4,0,4)};
        ovList.SelectionChanged+=(_,_)=>BuildOvEdit();
        w.Children.Add(ovList);
        ovEditPanel=new StackPanel();w.Children.Add(ovEditPanel);
        RefreshOverlayList();
        return w;
    }
    void AddLogo()
    {
        var dlg=new Microsoft.Win32.OpenFileDialog{Filter="圖片|*.png;*.jpg;*.jpeg;*.bmp"};
        if(dlg.ShowDialog(this)!=true)return;Snapshot();
        imgs.Add(new ImgOv{Path=dlg.FileName,NX=0.12,NY=0.1,Scale=0.8,Opacity=0.9,Start=0,End=totalDur>0?totalDur:99999,IsLogo=true});
        RefreshOverlayList();RenderOverlays();
    }
    void RefreshOverlayList()
    {
        if(ovList==null)return;ovList.Items.Clear();
        foreach(var t in texts)ovList.Items.Add((t.Hidden?"[隱] ":"")+"文字："+(t.Text.Length>12?t.Text.Substring(0,12):t.Text));
        foreach(var im in imgs)ovList.Items.Add((im.Hidden?"[隱] ":"")+(im.IsLogo?"Logo：":"浮水印：")+IoPath.GetFileName(im.Path));
        BuildOvEdit();
    }
    object? SelectedOverlay()
    {
        int i=ovList.SelectedIndex;if(i<0)return null;
        if(i<texts.Count)return texts[i];i-=texts.Count;return i<imgs.Count?imgs[i]:null;
    }
    void DeleteOverlay(){var o=SelectedOverlay();if(o==null)return;Snapshot();if(o is TextOv t)texts.Remove(t);else if(o is ImgOv im)imgs.Remove(im);RefreshOverlayList();RenderOverlays();}
    void ToggleOverlayHidden(){var o=SelectedOverlay();if(o is TextOv t)t.Hidden=!t.Hidden;else if(o is ImgOv im)im.Hidden=!im.Hidden;RefreshOverlayList();RenderOverlays();}
    void BuildOvEdit()
    {
        ovEditPanel.Children.Clear();var o=SelectedOverlay();if(o==null)return;
        var w=new WrapPanel();
        if(o is TextOv t)
        {
            var txt=new TextBox{Text=t.Text,Width=160,Margin=new Thickness(4,2,4,2)};
            txt.TextChanged+=(_,_)=>{t.Text=txt.Text;RenderOverlays();};
            w.Children.Add(Txt("文字"));w.Children.Add(txt);
            w.Children.Add(OvSlider("字號",8,120,t.Size,v=>{t.Size=(int)v;RenderOverlays();}));
            w.Children.Add(OvSlider("X",0,1,t.NX,v=>{t.NX=v;RenderOverlays();}));
            w.Children.Add(OvSlider("Y",0,1,t.NY,v=>{t.NY=v;RenderOverlays();}));
            w.Children.Add(OvSlider("起(秒)",0,Math.Max(1,totalDur),t.Start,v=>t.Start=v));
            w.Children.Add(OvSlider("止(秒)",0,Math.Max(1,totalDur),Math.Min(t.End,Math.Max(1,totalDur)),v=>t.End=v));
            foreach(var c in new[]{Colors.White,Colors.Black,Colors.Red,Colors.Yellow,(Color)ColorConverter.ConvertFromString("#34C759"),(Color)ColorConverter.ConvertFromString("#0A84FF")})
            {
                var cc=c;var b=new Button{Width=20,Height=20,Background=new SolidColorBrush(c),Margin=new Thickness(2),BorderThickness=new Thickness(1),Cursor=Cursors.Hand};
                b.Click+=(_,_)=>{t.Col=cc;RenderOverlays();};w.Children.Add(b);
            }
        }
        else if(o is ImgOv im)
        {
            w.Children.Add(OvSlider("大小",0.1,3,im.Scale,v=>{im.Scale=v;RenderOverlays();}));
            w.Children.Add(OvSlider("透明",0,1,im.Opacity,v=>{im.Opacity=v;RenderOverlays();}));
            w.Children.Add(OvSlider("X",0,1,im.NX,v=>{im.NX=v;RenderOverlays();}));
            w.Children.Add(OvSlider("Y",0,1,im.NY,v=>{im.NY=v;RenderOverlays();}));
            w.Children.Add(OvSlider("起(秒)",0,Math.Max(1,totalDur),im.Start,v=>im.Start=v));
            w.Children.Add(OvSlider("止(秒)",0,Math.Max(1,totalDur),Math.Min(im.End,Math.Max(1,totalDur)),v=>im.End=v));
        }
        ovEditPanel.Children.Add(w);
    }
    FrameworkElement OvSlider(string name,double min,double max,double val,Action<double> onChg)
    {
        var s=new Slider{Minimum=min,Maximum=max,Value=Math.Max(min,Math.Min(max,val)),Width=120,VerticalAlignment=VerticalAlignment.Center};
        var tb=new TextBlock{Foreground=Brushes.White,FontSize=11,Width=64,VerticalAlignment=VerticalAlignment.Center,Text=$"{name} {val:0.##}"};
        s.ValueChanged+=(_,e)=>{tb.Text=$"{name} {e.NewValue:0.##}";onChg(e.NewValue);};
        var sp=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(4,2,4,2)};sp.Children.Add(tb);sp.Children.Add(s);return sp;
    }
    UIElement TransitionTab()
    {
        var w=Wrap();
        var cb=new ComboBox{Width=90,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,2,4,2)};
        cb.Items.Add("無轉場");cb.Items.Add("疊化");
        cb.SelectionChanged+=(_,_)=>{if(syncing)return;transitionType=cb.SelectedIndex;UpdateFade();};
        syncers.Add(()=>cb.SelectedIndex=transitionType);
        w.Children.Add(Txt("轉場"));w.Children.Add(cb);
        w.Children.Add(GlobalSlider("轉場時長",0.2,2,()=>transitionDur,v=>transitionDur=v));
        w.Children.Add(Txt("（逐片段統一套用）"));
        return w;
    }
    static TextBlock Txt(string t)=>new(){Text=t,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(6,2,2,2)};
}
