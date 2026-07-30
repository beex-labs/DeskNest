using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WpfRectangle=System.Windows.Shapes.Rectangle;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using Colors=System.Windows.Media.Colors;
using IoPath=System.IO.Path;

namespace BeeX.DeskNest;

sealed partial class ScreenCaptureOverlay
{
    void StartTranslate()
    {
        if(pinned)return;
        if(selectionRect.Width<4||selectionRect.Height<4)return;
        // OCR 側車未安裝：先彈安裝對話框（可掛後台）；留窗等到裝完則直接續接翻譯
        if(!OcrSidecarService.IsAvailable&&!OcrInstallerService.ShowInstallDialog(language))return;
        translateMode=true;
        translateCts?.Cancel();
        translateCts=new System.Threading.CancellationTokenSource();
        translatedRect=selectionRect;
        _ = RunTranslateAsync(translateCts.Token);
    }

    /// <summary>框选原位翻译：选区内铺蒙版→识别→并行翻译→贴合替换，全程可取消。</summary>
    async System.Threading.Tasks.Task RunTranslateAsync(System.Threading.CancellationToken token)
    {
        var region=selectionRect;
        ShowTranslateMask(region,L("翻譯中…"));
        string path;
        try
        {
            var crop=CropRaw(region);
            if(crop==null){ClearTranslation();return;}
            path=IoPath.Combine(IoPath.GetTempPath(),$"BeeX_Translate_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            translateTempPath=path;
            using var fs=File.Create(path);
            var enc=new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(crop));
            enc.Save(fs);
        }
        catch{ClearTranslation();return;}

        try
        {
            var blocks=await OcrSidecarService.RecognizeTextWithPositionsAsync(path);
            if(token.IsCancellationRequested)return;
            // 过滤界面图标误识（爱心→O、播放键→m 等）：只保留"有意义文字"块，图标不翻译不覆盖
            blocks=blocks?.Where(IsMeaningfulTextBlock).ToList();
            if(blocks==null||blocks.Count==0){ShowTranslateMask(region,L("未辨識到文字"));return;}
            var target=ResolveTranslateTarget(blocks);
            var translated=await TranslateOverlayWindow.TranslateBlocksAsync(blocks,target,token);
            if(token.IsCancellationRequested)return;
            // 翻译期间用户又移动了选区则丢弃本次结果（由新任务负责）
            if(region!=selectionRect)return;
            RenderTranslationBlocks(region,translated);
        }
        catch(OperationCanceledException){}
        catch(Exception ex)
        {
            if(!token.IsCancellationRequested)ShowTranslateMask(region,L("辨識失敗")+"："+ex.Message);
        }
    }

    /// <summary>不带输出样式的原始选区裁剪（用于识别，保证坐标与像素一一对应）。</summary>
    BitmapSource? CropRaw(Rect region)
    {
        var pv=translationLayer.Visibility;var ps=selection.Visibility;var pt=toolbar.Visibility;
        var ptip=sizeTip.Visibility;var pmask=dimMask.Visibility;var pbg=canvas.Background;
        var hs=handles.Select(h=>h.Visibility).ToArray();
        translationLayer.Visibility=Visibility.Collapsed;
        selection.Visibility=Visibility.Collapsed;toolbar.Visibility=Visibility.Collapsed;
        sizeTip.Visibility=Visibility.Collapsed;dimMask.Visibility=Visibility.Collapsed;
        canvas.Background=Brushes.Transparent;
        foreach(var h in handles)h.Visibility=Visibility.Collapsed;
        try
        {
            UpdateLayout();
            var scaleX=source.PixelWidth/CW;var scaleY=source.PixelHeight/CH;
            var fw=Math.Max(1,(int)Math.Ceiling(CW*scaleX));var fh=Math.Max(1,(int)Math.Ceiling(CH*scaleY));
            var render=new RenderTargetBitmap(fw,fh,96*scaleX,96*scaleY,PixelFormats.Pbgra32);
            render.Render(canvas);
            var offX=Canvas.GetLeft(canvas);if(double.IsNaN(offX))offX=0;
            var offY=Canvas.GetTop(canvas);if(double.IsNaN(offY))offY=0;
            var x=Math.Clamp((int)Math.Floor((region.X+offX)*scaleX),0,fw-1);
            var y=Math.Clamp((int)Math.Floor((region.Y+offY)*scaleY),0,fh-1);
            var w=Math.Min(Math.Max(1,(int)Math.Ceiling(region.Width*scaleX)),fw-x);
            var h=Math.Min(Math.Max(1,(int)Math.Ceiling(region.Height*scaleY)),fh-y);
            return new CroppedBitmap(render,new Int32Rect(x,y,w,h));
        }
        catch{return null;}
        finally
        {
            translationLayer.Visibility=pv;selection.Visibility=ps;toolbar.Visibility=pt;
            sizeTip.Visibility=ptip;dimMask.Visibility=pmask;canvas.Background=pbg;
            for(var i=0;i<handles.Count&&i<hs.Length;i++)handles[i].Visibility=hs[i];
        }
    }

    /// <summary>铺一层半透明蒙版并居中显示状态文字（翻译中/失败）。</summary>
    void ShowTranslateMask(Rect region,string status)
    {
        translationLayer.Children.Clear();
        var mask=new WpfRectangle{Width=region.Width,Height=region.Height,Fill=new SolidColorBrush(Color.FromArgb(150,13,19,33))};
        Canvas.SetLeft(mask,region.X);Canvas.SetTop(mask,region.Y);
        translationLayer.Children.Add(mask);
        var tip=new TextBlock{Text=status,Foreground=Brushes.White,FontSize=15,FontWeight=FontWeights.SemiBold,TextAlignment=TextAlignment.Center,Width=region.Width,TextWrapping=TextWrapping.Wrap};
        Canvas.SetLeft(tip,region.X);Canvas.SetTop(tip,region.Y+Math.Max(0,region.Height/2-14));
        translationLayer.Children.Add(tip);
        translationLayer.Visibility=Visibility.Visible;
    }

    void ClearTranslation()
    {
        translationLayer.Children.Clear();
        translationLayer.Visibility=Visibility.Collapsed;
    }

    /// <summary>原位渲染译文：采样背景色遮盖原文 + 自适应字号叠加译文。</summary>
    void RenderTranslationBlocks(Rect region,List<TranslatedBlock> blocks)
    {
        translationLayer.Children.Clear();
        var scaleX=source.PixelWidth/CW;var scaleY=source.PixelHeight/CH;
        foreach(var b in blocks)
        {
            // 块（识别 PNG 像素系）→ 画布坐标（DIU），DPI 自动正确
            double cx=region.X+b.X/scaleX;
            double cy=region.Y+b.Y/scaleY;
            double cw=Math.Max(1,b.Width/scaleX);
            double ch=Math.Max(1,b.Height/scaleY);
            var bg=SampleBlockBackground(scaleX,scaleY,region,b);
            var cover=new WpfRectangle{Width=cw,Height=ch,Fill=new SolidColorBrush(bg)};
            Canvas.SetLeft(cover,cx);Canvas.SetTop(cover,cy);
            translationLayer.Children.Add(cover);

            var fg=(0.299*bg.R+0.587*bg.G+0.114*bg.B)>128?Colors.Black:Colors.White;
            var text=new TextBlock
            {
                Text=b.TranslatedText,
                Foreground=new SolidColorBrush(fg),
                Width=cw,
                TextWrapping=TextWrapping.Wrap,
                TextAlignment=TextAlignment.Left,
                FontSize=FitFontSize(b.TranslatedText,cw,ch),
                FontFamily=new System.Windows.Media.FontFamily(annotationFontFamily)
            };
            Canvas.SetLeft(text,cx);Canvas.SetTop(text,cy);
            translationLayer.Children.Add(text);
        }
        translationLayer.Visibility=Visibility.Visible;
    }

    /// <summary>从截图像素采样块外围背景色（取四角均值，简单可靠）。</summary>
    Color SampleBlockBackground(double scaleX,double scaleY,Rect region,TranslatedBlock b)
    {
        if(srcPixels==null)return Color.FromRgb(255,255,255);
        // 用识别 PNG 内坐标换算回整屏截图像素坐标
        int sx=Math.Clamp((int)((region.X+b.X/scaleX)*scaleX),0,source.PixelWidth-1);
        int sy=Math.Clamp((int)((region.Y+b.Y/scaleY)*scaleY),0,source.PixelHeight-1);
        int sw=Math.Max(1,(int)b.Width);int sh=Math.Max(1,(int)b.Height);
        int ex=Math.Clamp(sx+sw,sx+1,source.PixelWidth);int ey=Math.Clamp(sy+sh,sy+1,source.PixelHeight);
        long sr=0,sg=0,sb=0;int cnt=0;
        foreach(var(x,y) in new[]{(sx,Math.Max(0,sy-2)),(ex-1,Math.Max(0,sy-2)),(sx,Math.Min(source.PixelHeight-1,ey+1)),(ex-1,Math.Min(source.PixelHeight-1,ey+1))})
        {
            int o=y*srcStride+x*4;if(o<0||o+2>=srcPixels.Length)continue;
            sb+=srcPixels[o];sg+=srcPixels[o+1];sr+=srcPixels[o+2];cnt++;
        }
        if(cnt==0)return Color.FromRgb(255,255,255);
        return Color.FromRgb((byte)(sr/cnt),(byte)(sg/cnt),(byte)(sb/cnt));
    }

    /// <summary>判断一个识别块是否是"有意义文字"（过滤界面图标误识出的单字母/单符号噪声）。</summary>
    static bool IsMeaningfulTextBlock(OcrTextBlock b)
    {
        var t=(b.Text??"").Trim();
        if(t.Length==0)return false;
        // 统计有意义字符：字母 / 数字 / CJK
        int useful=0;
        foreach(var c in t)
            if(char.IsLetterOrDigit(c)||c is >= '\u3400' and <= '\u9fff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af')
                useful++;
        // 图标误识几乎都是单个字母/符号（爱心→O、播放→m、logo→G）；≥2 个有意义字符才翻译
        // 例外：时间/编号如 "05" "03:37" 含多位数字会保留
        return useful>=2;
    }

    /// <summary>
    /// 解析翻译目标语言：设置=具体语言则固定；设置=自动则按"识别内容多数语种"与软件语言决定——
    /// 内容多数是软件语言→翻成英文；内容多数是英文→翻成软件语言。
    /// </summary>
    string ResolveTranslateTarget(List<OcrTextBlock> blocks)
    {
        string appLang=language??"zh-TW";
        bool softwareChinese=appLang.StartsWith("zh",StringComparison.OrdinalIgnoreCase);
        string softwareLang=softwareChinese?(appLang.Equals("zh-CN",StringComparison.OrdinalIgnoreCase)?"zh-CN":"zh-TW"):"en";
        string setting=UserConfigHelper.ReadTranslateTarget();
        if(setting is "zh") return softwareChinese?softwareLang:"zh-CN";
        if(setting is "en" or "ja" or "ko") return setting;
        // auto：统计中文块与英文块数量
        int chinese=0,latin=0;
        foreach(var b in blocks)
        {
            var t=(b.Text??"");
            if(t.Any(c=>c is >= '\u3400' and <= '\u9fff'))chinese++;
            else if(t.Any(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z'))latin++;
        }
        string foreign=softwareChinese?"en":"zh-CN";
        bool majorityIsSoftware=softwareChinese?chinese>=latin:latin>=chinese;
        return majorityIsSoftware?foreign:softwareLang;
    }

    /// <summary>自适应字号：在块宽换行下缩小字号直到总高不超过块高（下限 8）。</summary>
    double FitFontSize(string text,double boxW,double boxH)
    {
        if(string.IsNullOrEmpty(text)||boxW<4||boxH<4)return 12;
        double size=Math.Max(8,Math.Min(boxH*0.85,boxH));
        var typeface=new Typeface(new System.Windows.Media.FontFamily(annotationFontFamily),FontStyles.Normal,FontWeights.Normal,FontStretches.Normal);
        var dpi=VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for(;size>=8;size-=0.5)
        {
            var ft=new FormattedText(text,System.Globalization.CultureInfo.CurrentCulture,System.Windows.FlowDirection.LeftToRight,typeface,size,Brushes.Black,dpi){MaxTextWidth=boxW,Trimming=TextTrimming.None};
            if(ft.Height<=boxH+0.5)return size;
        }
        return 8;
    }
}
