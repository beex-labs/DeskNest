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
        // OCR plugin not installed: First, display the installation dialog box (which can run in the background); keep the window open until installation is complete, then resume the translation.
        if(!OcrSidecarService.IsAvailable&&!OcrInstallerService.ShowInstallDialog(language))return;
        translateMode=true;
        translateCts?.Cancel();
        translateCts=new System.Threading.CancellationTokenSource();
        translatedRect=selectionRect;
        _ = RunTranslateAsync(translateCts.Token);
    }

    /// <summary>Select the text for in-place translation: Apply a mask to the selected area → Recognize → Parallel Translation → Replace with the translated text. You can cancel at any time during the process. </summary>
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
            // Filtering misidentifications of interface icons (heart → O, play button → m, etc.): Only retain "meaningful text" blocks; do not translate or overwrite icons
            blocks=blocks?.Where(IsMeaningfulTextBlock).ToList();
            if(blocks==null||blocks.Count==0){ShowTranslateMask(region,L("未辨識到文字"));return;}
            var target=ResolveTranslateTarget(blocks);
            var translated=await TranslateOverlayWindow.TranslateBlocksAsync(blocks,target,token);
            if(token.IsCancellationRequested)return;
            // If the user moves the selection area during translation, the current result is discarded (and handled by a new task).
            if(region!=selectionRect)return;
            RenderTranslationBlocks(region,translated);
        }
        catch(OperationCanceledException){}
        catch(Exception ex)
        {
            if(!token.IsCancellationRequested)ShowTranslateMask(region,L("辨識失敗")+"："+ex.Message);
        }
    }

    /// <summary>Raw selection cropping without output styling (for identification purposes, to ensure a one-to-one correspondence between coordinates and pixels).</summary>
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

    /// <summary>Apply a semi-transparent overlay and center the status text (Translating/Failed).</summary>
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

    /// <summary>In-place rendering translation: Samples the background color to cover the original text + overlays the translation using an adaptive font size.</summary>
    void RenderTranslationBlocks(Rect region,List<TranslatedBlock> blocks)
    {
        translationLayer.Children.Clear();
        var scaleX=source.PixelWidth/CW;var scaleY=source.PixelHeight/CH;
        foreach(var b in blocks)
        {
            // Blocks (PNG pixel system) → Canvas coordinates (DIU); DPI is automatically corrected
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

    /// <summary>Sample the background color from the area surrounding the image pixel block (calculate the average of the four corners—a simple and reliable method). </summary>
    Color SampleBlockBackground(double scaleX,double scaleY,Rect region,TranslatedBlock b)
    {
        if(srcPixels==null)return Color.FromRgb(255,255,255);
        // Convert PNG coordinates to full-screen pixel coordinates using coordinate recognition
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

    /// <summary>Determines whether a recognized block is "meaningful text" (filters out single-letter or single-symbol noise caused by misrecognition of interface icons).</summary>
    static bool IsMeaningfulTextBlock(OcrTextBlock b)
    {
        var t=(b.Text??"").Trim();
        if(t.Length==0)return false;
        // Statistically Significant Characters: Letters / Numbers / CJK
        int useful=0;
        foreach(var c in t)
            if(char.IsLetterOrDigit(c)||c is >= '\u3400' and <= '\u9fff' or >= '\u3040' and <= '\u30ff' or >= '\uac00' and <= '\ud7af')
                useful++;
        // Icon misrecognition almost always involves a single letter or symbol (heart → O, play → m, logo → G); translation occurs only when there are ≥2 meaningful characters.
        // Exception: Times/numbers containing multiple digits, such as "05" and "03:37," will be retained.
        return useful>=2;
    }

    /// <summary>
    /// Analysis of the target language for translation: If set to a specific language, it remains fixed; if set to "Auto," it is determined based on "the language of the majority of the content" and the software language—
    /// If most of the content is in the software language, translate it into English; if most of the content is in English, translate it into the software language.
    /// </summary>
    string ResolveTranslateTarget(List<OcrTextBlock> blocks)
    {
        string appLang=language??"zh-TW";
        bool softwareChinese=appLang.StartsWith("zh",StringComparison.OrdinalIgnoreCase);
        string softwareLang=softwareChinese?(appLang.Equals("zh-CN",StringComparison.OrdinalIgnoreCase)?"zh-CN":"zh-TW"):"en";
        string setting=UserConfigHelper.ReadTranslateTarget();
        if(setting is "zh") return softwareChinese?softwareLang:"zh-CN";
        if(setting is "en" or "ja" or "ko") return setting;
        // auto: Count the number of Chinese and English blocks
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

    /// <summary>Adaptive font size: When a line breaks within a block, reduce the font size until the total height does not exceed the block height (minimum 8).</summary>
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
