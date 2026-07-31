using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using Button=System.Windows.Controls.Button;
using ComboBox=System.Windows.Controls.ComboBox;
using Orientation=System.Windows.Controls.Orientation;
using IoPath=System.IO.Path;

namespace BeeX.DeskNest;

/// <summary>Editor Export: Page Settings (Format/Resolution/Frame Rate/Quality/Path/Estimated Size) + Two Runs of FFmpeg Composition (All Parameters) + Progress/Cancel/Disk Check. </summary>
public sealed partial class VideoEditorWindow
{
    static string F(double d)=>d.ToString("0.###",CultureInfo.InvariantCulture);
    const string FontFile="C\\:/Windows/Fonts/msyh.ttc";
    Process? exportProc; bool exportCancel;
    TextBlock expSizeLbl=new(){Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(8,0,8,0)};
    TextBlock expPathLbl=new(){Foreground=new SolidColorBrush(Color.FromArgb(200,255,255,255)),VerticalAlignment=VerticalAlignment.Center,FontSize=11,MaxWidth=260,TextTrimming=TextTrimming.CharacterEllipsis};

    UIElement ExportTab()
    {
        var w=Wrap();
        var fmt=new ComboBox{Width=78,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,2,4,2)};
        foreach(var f in new[]{"mp4","mov","webm","gif"})fmt.Items.Add(f);
        fmt.SelectedIndex=Math.Max(0,Array.IndexOf(new[]{"mp4","mov","webm","gif"},expFormat));
        fmt.SelectionChanged+=(_,_)=>{expFormat=(string)fmt.SelectedItem;UpdateSizeEst();};
        var res=new ComboBox{Width=96,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,2,4,2)};
        var resH=new[]{0,1080,720,480};foreach(var r in new[]{"原始","1080p","720p","480p"})res.Items.Add(r);
        res.SelectedIndex=Math.Max(0,Array.IndexOf(resH,expScaleH));
        res.SelectionChanged+=(_,_)=>{expScaleH=resH[res.SelectedIndex];UpdateSizeEst();};
        var fps=new ComboBox{Width=72,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,2,4,2)};
        var fpsV=new[]{24,30,60};foreach(var f in fpsV)fps.Items.Add($"{f}fps");
        fps.SelectedIndex=Math.Max(0,Array.IndexOf(fpsV,expFps));
        fps.SelectionChanged+=(_,_)=>{expFps=fpsV[fps.SelectedIndex];UpdateSizeEst();};
        var qual=new ComboBox{Width=96,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(4,2,4,2)};
        foreach(var q in new[]{"原畫預設","高清預設","壓縮預設"})qual.Items.Add(q);
        qual.SelectedIndex=Math.Clamp(expQuality,0,2);
        qual.SelectionChanged+=(_,_)=>{expQuality=qual.SelectedIndex;UpdateSizeEst();};
        var exp=IconBtn("download","開始導出",Export);exp.Background=new SolidColorBrush(Color.FromRgb(255,138,0));exp.MinWidth=110;
        w.Children.Add(Txt("格式"));w.Children.Add(fmt);
        w.Children.Add(Txt("解析度"));w.Children.Add(res);
        w.Children.Add(Txt("幀率"));w.Children.Add(fps);
        w.Children.Add(Txt("品質"));w.Children.Add(qual);
        w.Children.Add(IconBtn("folder","輸出路徑",PickExportPath));w.Children.Add(expPathLbl);
        w.Children.Add(expSizeLbl);w.Children.Add(exp);
        UpdateSizeEst();
        return w;
    }
    void UpdateSizeEst()
    {
        double br=expQuality==0?12000:expQuality==1?6000:2500;
        if(expScaleH>0)br*=Math.Min(1,expScaleH/1080.0);
        double mb=(br+192)*1000/8*Math.Max(0,totalDur)/1e6;
        expSizeLbl.Text=$"預估 {mb:0.0} MB";
        expPathLbl.Text=expPath??"（默認：螢幕錄製資料夾）";
    }
    void PickExportPath()
    {
        var dlg=new Microsoft.Win32.SaveFileDialog{Filter=$"{expFormat}|*.{expFormat}",FileName=$"BeeX_Edit_{DateTime.Now:yyyyMMdd_HHmmss}.{expFormat}",InitialDirectory=outputDir};
        if(dlg.ShowDialog(this)==true){expPath=dlg.FileName;UpdateSizeEst();}
    }

    int Run(string args)
    {
        var p=FfmpegService.Start(args);if(p==null)return -1;
        exportProc=p;p.WaitForExit();int code=exportCancel?-999:p.ExitCode;
        try{p.Dispose();}catch{}exportProc=null;return code;
    }

    void Export()
    {
        if(clips.Count==0){System.Windows.MessageBox.Show(this,"沒有可導出的片段。");return;}
        Pause();
        string ext=expFormat;
        string outPath=expPath??IoPath.Combine(outputDir,$"BeeX_Edit_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}");
        if(!outPath.EndsWith("."+ext,StringComparison.OrdinalIgnoreCase))outPath=IoPath.ChangeExtension(outPath,ext);
        double br=expQuality==0?12000:expQuality==1?6000:2500;if(expScaleH>0)br*=Math.Min(1,expScaleH/1080.0);
        double needMB=(br+192)*1000/8*Math.Max(0,totalDur)/1e6;
        try{var root=IoPath.GetPathRoot(IoPath.GetFullPath(outPath));if(!string.IsNullOrEmpty(root)){var drv=new DriveInfo(root);if(drv.AvailableFreeSpace<(long)(needMB*1.8*1e6)&&System.Windows.MessageBox.Show(this,$"磁碟剩餘空間可能不足（預估需 {needMB:0} MB）。仍要繼續？","BeeX",MessageBoxButton.YesNo)!=MessageBoxResult.Yes)return;}}catch{}

        var snap=clips.Select(c=>c.Copy()).ToList();
        var txts=texts.Where(t=>!t.Hidden).ToList();var images=imgs.Where(i=>!i.Hidden).ToList();
        int tType=transitionType;double tDur=transitionDur;bool ts=showTimestamp;string? bgm=bgmPath;double bgmVol=bgmVolume;
        string fmt=expFormat;int scaleH=expScaleH,fps=expFps,qual=expQuality;
        int baseW=snap[0].SrcW>0?snap[0].SrcW:1280,baseH=snap[0].SrcH>0?snap[0].SrcH:720;
        double aspect=(double)baseW/Math.Max(1,baseH);
        int H=scaleH>0?scaleH:baseH;int W=(int)Math.Round(H*aspect);W-=W%2;H-=H%2;if(W<2)W=1280;if(H<2)H=720;

        exportCancel=false;
        var cancelBtn=new Button{Content=L("取消導出"),Height=32,Margin=new Thickness(0,12,0,0),Padding=new Thickness(14,0,14,0),HorizontalAlignment=System.Windows.HorizontalAlignment.Center};
        cancelBtn.Click+=(_,_)=>{exportCancel=true;try{exportProc?.Kill();}catch{}};
        var panel=new StackPanel{HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        panel.Children.Add(exportText);panel.Children.Add(cancelBtn);
        exportOverlay.Child=panel;exportOverlay.Visibility=Visibility.Visible;exportText.Text="準備導出…";

        string tmp=IoPath.Combine(IoPath.GetTempPath(),"BeeX_Edit_"+DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"));
        Directory.CreateDirectory(tmp);
        System.Threading.Tasks.Task.Run(()=>
        {
            string? result=null,err="";
            try{result=RunExport(snap,txts,images,tType,tDur,ts,bgm,bgmVol,fmt,W,H,fps,qual,tmp,outPath,out err);}
            catch(Exception ex){err=ex.Message;}
            Dispatcher.Invoke(()=>
            {
                exportOverlay.Visibility=Visibility.Collapsed;exportOverlay.Child=exportText;
                try{if(Directory.Exists(tmp))Directory.Delete(tmp,true);}catch{}
                if(exportCancel)System.Windows.MessageBox.Show(this,"已取消導出。");
                else if(!string.IsNullOrEmpty(result)&&File.Exists(result))
                    try{Process.Start(new ProcessStartInfo("explorer.exe",$"/select,\"{result}\""){UseShellExecute=true});}catch{}
                else System.Windows.MessageBox.Show(this,"導出失敗："+(string.IsNullOrEmpty(err)?"ffmpeg 處理未成功":err));
            });
        });
    }

    void Stage(string s)=>Dispatcher.Invoke(()=>exportText.Text=s);

    string? RunExport(List<EditClip> cs,List<TextOv> txts,List<ImgOv> images,int tType,double tDur,bool ts,string? bgm,double bgmVol,
                      string fmt,int W,int H,int FPS,int qual,string tmp,string outPath,out string err)
    {
        err="";int crf=qual==0?18:qual==1?21:26;
        var segs=new List<string>();
        for(int i=0;i<cs.Count;i++)
        {
            if(exportCancel)return null;
            Stage($"處理片段 {i+1}/{cs.Count}…");
            var c=cs[i];double srcDur=Math.Max(0.05,c.Out-c.In);double outDur=c.OutDuration;
            string seg=IoPath.Combine(tmp,$"seg_{i}.mp4");
            // Video Filters
            var vf=new List<string>();
            if(c.CropL+c.CropR<0.95&&c.CropT+c.CropB<0.95&&(c.CropL>0||c.CropR>0||c.CropT>0||c.CropB>0))
                vf.Add($"crop=iw*{F(1-c.CropL-c.CropR)}:ih*{F(1-c.CropT-c.CropB)}:iw*{F(c.CropL)}:ih*{F(c.CropT)}");
            vf.Add($"setpts=PTS/{F(c.Speed)}");
            string rot=c.Rotate switch{90=>"transpose=1",180=>"hflip,vflip",270=>"transpose=2",_=>""};
            if(rot.Length>0)vf.Add(rot);
            if(c.FlipH)vf.Add("hflip");
            if(c.FlipV)vf.Add("vflip");
            vf.Add(ColorFilters(c));
            vf.Add($"scale={W}:{H}:force_original_aspect_ratio=decrease");
            vf.Add($"pad={W}:{H}:(ow-iw)/2:(oh-ih)/2:black");
            vf.Add("setsar=1");vf.Add($"fps={FPS}");
            if(tType==1)
            {
                double fd=Math.Max(0.1,tDur);
                if(i>0)vf.Add($"fade=t=in:st=0:d={F(fd)}");
                if(i<cs.Count-1&&outDur>fd*2)vf.Add($"fade=t=out:st={F(outDur-fd)}:d={F(fd)}");
            }
            string vchain=$"[0:v]{string.Join(",",vf)}[v]";
            bool useAudio=c.HasAudio&&!c.Mute&&!c.MuteAfterSpeed;
            string args;
            if(useAudio)
            {
                var af=new List<string>();
                af.Add($"volume={F(c.Volume)}");
                if(c.PreservePitch)af.Add(AtempoChain(c.Speed));
                else af.Add($"asetrate=44100*{F(c.Speed)},aresample=44100");
                if(c.Denoise)af.Add("afftdn");
                if(c.FadeIn>0.01)af.Add($"afade=t=in:st=0:d={F(c.FadeIn)}");
                if(c.FadeOut>0.01&&outDur>c.FadeOut)af.Add($"afade=t=out:st={F(outDur-c.FadeOut)}:d={F(c.FadeOut)}");
                if(tType==1){double fd=Math.Max(0.1,tDur);if(i>0)af.Add($"afade=t=in:st=0:d={F(fd)}");if(i<cs.Count-1&&outDur>fd*2)af.Add($"afade=t=out:st={F(outDur-fd)}:d={F(fd)}");}
                af.Add("aresample=44100");af.Add("asetpts=PTS-STARTPTS");
                string achain=$"[0:a]{string.Join(",",af)}[a]";
                args=$"-y -ss {F(c.In)} -t {F(srcDur)} -i \"{c.Source}\" -filter_complex \"{vchain};{achain}\" -map \"[v]\" -map \"[a]\" -r {FPS} -c:v libx264 -crf {crf} -preset veryfast -pix_fmt yuv420p -c:a aac -ar 44100 \"{seg}\"";
            }
            else
            {
                args=$"-y -ss {F(c.In)} -t {F(srcDur)} -i \"{c.Source}\" -f lavfi -t {F(outDur)} -i anullsrc=channel_layout=stereo:sample_rate=44100 -filter_complex \"{vchain}\" -map \"[v]\" -map 1:a -shortest -r {FPS} -c:v libx264 -crf {crf} -preset veryfast -pix_fmt yuv420p -c:a aac \"{seg}\"";
            }
            if(Run(args)!=0||!File.Exists(seg)){err="片段處理失敗";return null;}
            segs.Add(seg);
        }
        if(exportCancel)return null;

        Stage("合併片段…");
        string list=IoPath.Combine(tmp,"list.txt");
        File.WriteAllText(list,string.Join("\n",segs.Select(s=>$"file '{s.Replace("\\","/")}'")));
        string concat=IoPath.Combine(tmp,"concat.mp4");
        if(Run($"-y -f concat -safe 0 -i \"{list}\" -c copy \"{concat}\"")!=0||!File.Exists(concat))
            if(Run($"-y -f concat -safe 0 -i \"{list}\" -c:v libx264 -crf {crf} -preset veryfast -pix_fmt yuv420p -c:a aac \"{concat}\"")!=0){err="合併失敗";return null;}

        bool hasOverlay=txts.Count>0||images.Count>0||ts;
        bool hasBgm=!string.IsNullOrEmpty(bgm)&&File.Exists(bgm);
        if(!hasOverlay&&!hasBgm&&fmt=="mp4"){try{File.Copy(concat,outPath,true);return outPath;}catch(Exception ex){err=ex.Message;return null;}}

        if(exportCancel)return null;
        Stage("合成疊加/音樂/輸出…");
        var chains=new List<string>();string cur="[0:v]";bool vFiltered=false;
        var inputs=new System.Text.StringBuilder($"-y -i \"{concat}\"");
        int inIdx=1;
        if(txts.Count>0||ts)
        {
            var dt=new List<string>();
            foreach(var t in txts)dt.Add(DrawText(t));
            if(ts)dt.Add($"drawtext=fontfile='{FontFile}':text='%{{pts\\:hms}}':fontcolor=white:fontsize={Math.Max(14,H/36)}:box=1:boxcolor=0x00000080:x=20:y=20");
            chains.Add($"{cur}{string.Join(",",dt)}[vt]");cur="[vt]";vFiltered=true;
        }
        foreach(var im in images)
        {
            inputs.Append($" -i \"{im.Path}\"");
            chains.Add($"[{inIdx}:v]scale=iw*{F(im.Scale)}:-1,format=rgba,colorchannelmixer=aa={F(im.Opacity)}[wm{inIdx}]");
            string x=$"main_w*{F(im.NX)}-overlay_w/2",y=$"main_h*{F(im.NY)}-overlay_h/2";
            chains.Add($"{cur}[wm{inIdx}]overlay={x}:{y}:enable='between(t,{F(im.Start)},{F(im.End)})'[ov{inIdx}]");
            cur=$"[ov{inIdx}]";vFiltered=true;inIdx++;
        }
        string audioMap;
        if(hasBgm)
        {
            inputs.Append($" -stream_loop -1 -i \"{bgm}\"");int bi=inIdx;inIdx++;
            chains.Add($"[{bi}:a]volume={F(bgmVol)},aresample=44100[bg]");
            chains.Add($"[0:a][bg]amix=inputs=2:duration=first:dropout_transition=0[aout]");
            audioMap="[aout]";
        }
        else audioMap="0:a";
        string vmap=vFiltered?cur:"0:v";
        string vcodec=fmt switch{"webm"=>$"-c:v libvpx-vp9 -crf {crf+8} -b:v 0",_=>$"-c:v libx264 -crf {crf} -preset veryfast -pix_fmt yuv420p"};
        string acodec=fmt=="webm"?"-c:a libopus":"-c:a aac -b:a 192k";
        string fc=chains.Count>0?$" -filter_complex \"{string.Join(";",chains)}\"":"";
        if(fmt=="gif")
        {
            string mid=IoPath.Combine(tmp,"mid.mp4");
            string margs=$"{inputs}{fc} -map \"{vmap}\" -map \"{audioMap}\" {vcodec} {acodec} \"{mid}\"";
            if(Run(margs)!=0||!File.Exists(mid)){err="合成失敗";return null;}
            if(Run($"-y -i \"{mid}\" -vf \"fps=15,scale='min(720,iw)':-2:flags=lanczos,split[s0][s1];[s0]palettegen[p];[s1][p]paletteuse\" \"{outPath}\"")!=0){err="GIF 轉換失敗";return null;}
            return File.Exists(outPath)?outPath:null;
        }
        string finalArgs=$"{inputs}{fc} -map \"{vmap}\" -map \"{audioMap}\" {vcodec} {acodec} -movflags +faststart \"{outPath}\"";
        if(Run(finalArgs)!=0||!File.Exists(outPath))
        {
            try{if(fmt=="mp4"){File.Copy(concat,outPath,true);return outPath;}}catch{}
            err="最終合成失敗";return null;
        }
        return outPath;
    }

    static string ColorFilters(EditClip c)
    {
        var parts=new List<string>();
        double b=Math.Clamp(c.Brightness+c.Exposure*0.5,-1,1);
        double gamma=Math.Clamp(1+c.Shadows*0.4-c.Highlights*0.3,0.3,3);
        parts.Add($"eq=brightness={F(b)}:contrast={F(c.Contrast)}:saturation={F(c.Saturation)}:gamma={F(gamma)}");
        if(Math.Abs(c.Temperature)>0.001||Math.Abs(c.TintV)>0.001)
            parts.Add($"colorbalance=rm={F(c.Temperature*0.3)}:bm={F(-c.Temperature*0.3)}:gm={F(c.TintV*0.3)}");
        if(c.Sharpen>0.01)parts.Add($"unsharp=5:5:{F(c.Sharpen)}:5:5:0");
        return string.Join(",",parts);
    }
    static string AtempoChain(double speed)
    {
        double s=speed;var parts=new List<string>();
        while(s>2.0){parts.Add("atempo=2.0");s/=2.0;}
        while(s<0.5){parts.Add("atempo=0.5");s*=2.0;}
        parts.Add($"atempo={F(s)}");return string.Join(",",parts);
    }
    static string DrawText(TextOv t)
    {
        string col=$"0x{t.Col.R:X2}{t.Col.G:X2}{t.Col.B:X2}";
        return $"drawtext=fontfile='{FontFile}':text='{EscDraw(t.Text)}':fontcolor={col}:fontsize={t.Size}:borderw=2:bordercolor=0x000000:x=(w*{F(t.NX)}-text_w/2):y=(h*{F(t.NY)}-text_h/2):enable='between(t,{F(t.Start)},{F(t.End)})'";
    }
    static string EscDraw(string t)=>t.Replace("\\","\\\\").Replace(":","\\:").Replace("'","\u2019").Replace("%","\\%");
}
