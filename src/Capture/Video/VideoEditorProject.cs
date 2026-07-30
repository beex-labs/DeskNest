using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Color=System.Windows.Media.Color;
using ContextMenu=System.Windows.Controls.ContextMenu;
using MenuItem=System.Windows.Controls.MenuItem;
using Separator=System.Windows.Controls.Separator;
using IoPath=System.IO.Path;

namespace BeeX.DeskNest;

/// <summary>剪輯器專案管理：新建/打開/保存/另存、自動保存、恢復、恢復默認設置（JSON DTO 序列化）。</summary>
public sealed partial class VideoEditorWindow
{
    sealed class ClipDto{public string Source="";public double SrcDuration;public bool HasAudio;public int SrcW,SrcH;public double In,Out,Speed=1;public bool PreservePitch=true,MuteAfterSpeed;public int Rotate;public bool FlipH,FlipV;public double CropL,CropT,CropR,CropB,Exposure,Temperature,TintV,Shadows,Highlights,Sharpen,Brightness,Contrast=1,Saturation=1;public bool Mute;public double Volume=1,FadeIn,FadeOut;public bool Denoise;}
    sealed class TextDto{public string Text="";public string Font="Microsoft YaHei";public int Size=42;public byte R=255,G=255,B=255;public bool Bold=true;public double NX=0.5,NY=0.86,Start,End=99999;public bool Hidden;}
    sealed class ImgDto{public string Path="";public double NX=0.82,NY=0.06,Scale=1,Opacity=0.9,Start,End=99999;public bool Hidden,IsLogo;}
    sealed class ProjDto{public List<ClipDto> Clips=new();public List<TextDto> Texts=new();public List<ImgDto> Imgs=new();public int TransitionType;public double TransitionDur=0.5;public string? Bgm;public double BgmVolume=0.8,BgmDur;public bool ShowTimestamp;public string ExpFormat="mp4";public int ExpScaleH,ExpFps=30,ExpQuality=1;}

    DispatcherTimer? autosaveTimer;
    static string AutosavePath => IoPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BeeX DeskNest","editor_autosave.beexproj");

    void EnsureAutosave()
    {
        if(autosaveTimer!=null)return;
        autosaveTimer=new DispatcherTimer{Interval=TimeSpan.FromSeconds(30)};
        autosaveTimer.Tick+=(_,_)=>{try{Directory.CreateDirectory(IoPath.GetDirectoryName(AutosavePath)!);File.WriteAllText(AutosavePath,ProjectToJson());}catch{}};
        autosaveTimer.Start();
    }

    string ProjectToJson()
    {
        var p=new ProjDto{TransitionType=transitionType,TransitionDur=transitionDur,Bgm=bgmPath,BgmVolume=bgmVolume,BgmDur=bgmDur,ShowTimestamp=showTimestamp,ExpFormat=expFormat,ExpScaleH=expScaleH,ExpFps=expFps,ExpQuality=expQuality};
        foreach(var c in clips)p.Clips.Add(new ClipDto{Source=c.Source,SrcDuration=c.SrcDuration,HasAudio=c.HasAudio,SrcW=c.SrcW,SrcH=c.SrcH,In=c.In,Out=c.Out,Speed=c.Speed,PreservePitch=c.PreservePitch,MuteAfterSpeed=c.MuteAfterSpeed,Rotate=c.Rotate,FlipH=c.FlipH,FlipV=c.FlipV,CropL=c.CropL,CropT=c.CropT,CropR=c.CropR,CropB=c.CropB,Exposure=c.Exposure,Temperature=c.Temperature,TintV=c.TintV,Shadows=c.Shadows,Highlights=c.Highlights,Sharpen=c.Sharpen,Brightness=c.Brightness,Contrast=c.Contrast,Saturation=c.Saturation,Mute=c.Mute,Volume=c.Volume,FadeIn=c.FadeIn,FadeOut=c.FadeOut,Denoise=c.Denoise});
        foreach(var t in texts)p.Texts.Add(new TextDto{Text=t.Text,Font=t.Font,Size=t.Size,R=t.Col.R,G=t.Col.G,B=t.Col.B,Bold=t.Bold,NX=t.NX,NY=t.NY,Start=t.Start,End=t.End,Hidden=t.Hidden});
        foreach(var im in imgs)p.Imgs.Add(new ImgDto{Path=im.Path,NX=im.NX,NY=im.NY,Scale=im.Scale,Opacity=im.Opacity,Start=im.Start,End=im.End,Hidden=im.Hidden,IsLogo=im.IsLogo});
        return JsonSerializer.Serialize(p);
    }

    void LoadProjectJson(string json)
    {
        ProjDto? p;try{p=JsonSerializer.Deserialize<ProjDto>(json);}catch{return;}
        if(p==null)return;
        clips.Clear();texts.Clear();imgs.Clear();sel=null;
        foreach(var d in p.Clips)clips.Add(new EditClip{Source=d.Source,SrcDuration=d.SrcDuration,HasAudio=d.HasAudio,SrcW=d.SrcW,SrcH=d.SrcH,In=d.In,Out=d.Out,Speed=d.Speed,PreservePitch=d.PreservePitch,MuteAfterSpeed=d.MuteAfterSpeed,Rotate=d.Rotate,FlipH=d.FlipH,FlipV=d.FlipV,CropL=d.CropL,CropT=d.CropT,CropR=d.CropR,CropB=d.CropB,Exposure=d.Exposure,Temperature=d.Temperature,TintV=d.TintV,Shadows=d.Shadows,Highlights=d.Highlights,Sharpen=d.Sharpen,Brightness=d.Brightness,Contrast=d.Contrast,Saturation=d.Saturation,Mute=d.Mute,Volume=d.Volume,FadeIn=d.FadeIn,FadeOut=d.FadeOut,Denoise=d.Denoise});
        foreach(var d in p.Texts)texts.Add(new TextOv{Text=d.Text,Font=d.Font,Size=d.Size,Col=Color.FromRgb(d.R,d.G,d.B),Bold=d.Bold,NX=d.NX,NY=d.NY,Start=d.Start,End=d.End,Hidden=d.Hidden});
        foreach(var d in p.Imgs)imgs.Add(new ImgOv{Path=d.Path,NX=d.NX,NY=d.NY,Scale=d.Scale,Opacity=d.Opacity,Start=d.Start,End=d.End,Hidden=d.Hidden,IsLogo=d.IsLogo});
        transitionType=p.TransitionType;transitionDur=p.TransitionDur;bgmPath=p.Bgm;bgmVolume=p.BgmVolume;bgmDur=p.BgmDur;showTimestamp=p.ShowTimestamp;
        expFormat=p.ExpFormat;expScaleH=p.ExpScaleH;expFps=p.ExpFps;expQuality=p.ExpQuality;
        sel=clips.Count>0?clips[0]:null;
        RebuildTimeline();
        foreach(var c in clips)GenerateThumbs(c);
        RefreshOverlayList();RenderOverlays();BuildPropPanel();
        LoadAt(0,false);
    }

    void ProjectMenu()
    {
        var m=new ContextMenu();
        m.Items.Add(MI("新建項目",NewProject));
        m.Items.Add(MI("打開項目…",OpenProject));
        m.Items.Add(MI("保存項目",()=>SaveProject(false)));
        m.Items.Add(MI("另存為…",()=>SaveProject(true)));
        m.Items.Add(new Separator());
        m.Items.Add(MI("恢復自動保存",RecoverAutosave));
        m.Items.Add(MI("恢復默認設置",ResetDefaults));
        m.IsOpen=true;
    }
    static MenuItem MI(string h,Action a){var mi=new MenuItem{Header=h};mi.Click+=(_,_)=>a();return mi;}

    void NewProject()
    {
        if(clips.Count>0&&System.Windows.MessageBox.Show(this,"新建將清空當前項目，是否繼續？","BeeX",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;
        Snapshot();clips.Clear();texts.Clear();imgs.Clear();sel=null;bgmPath=null;projectPath=null;
        RebuildTimeline();RefreshOverlayList();RenderOverlays();LoadAt(0,false);
    }
    void OpenProject()
    {
        var dlg=new Microsoft.Win32.OpenFileDialog{Filter="BeeX 項目|*.beexproj;*.json|所有檔案|*.*"};
        if(dlg.ShowDialog(this)!=true)return;
        try{LoadProjectJson(File.ReadAllText(dlg.FileName));projectPath=dlg.FileName;}catch{System.Windows.MessageBox.Show(this,"打開項目失敗。");}
    }
    void SaveProject(bool saveAs)
    {
        string? path=projectPath;
        if(saveAs||string.IsNullOrEmpty(path))
        {
            var dlg=new Microsoft.Win32.SaveFileDialog{Filter="BeeX 項目|*.beexproj",FileName=$"BeeX_Project_{DateTime.Now:yyyyMMdd_HHmmss}.beexproj",InitialDirectory=outputDir};
            if(dlg.ShowDialog(this)!=true)return;path=dlg.FileName;
        }
        try{File.WriteAllText(path!,ProjectToJson());projectPath=path;System.Windows.MessageBox.Show(this,"已保存項目。");}
        catch{System.Windows.MessageBox.Show(this,"保存失敗。");}
    }
    void RecoverAutosave()
    {
        if(!File.Exists(AutosavePath)){System.Windows.MessageBox.Show(this,"沒有可恢復的自動保存。");return;}
        try{LoadProjectJson(File.ReadAllText(AutosavePath));}catch{System.Windows.MessageBox.Show(this,"恢復失敗。");}
    }
    void ResetDefaults()
    {
        transitionType=0;transitionDur=0.5;expFormat="mp4";expScaleH=0;expFps=30;expQuality=1;expBitrateK=0;showTimestamp=false;bgmPath=null;bgmVolume=0.8;
        BuildPropPanel();RenderOverlays();
    }
}
