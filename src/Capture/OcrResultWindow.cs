using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Image=System.Windows.Controls.Image;
using Color=System.Windows.Media.Color;
using Brushes=System.Windows.Media.Brushes;
using Button=System.Windows.Controls.Button;
using Orientation=System.Windows.Controls.Orientation;
using Clipboard=System.Windows.Clipboard;
using Cursors=System.Windows.Input.Cursors;
using HorizontalAlignment=System.Windows.HorizontalAlignment;
using VerticalAlignment=System.Windows.VerticalAlignment;
using WpfTextBox=System.Windows.Controls.TextBox;
using WpfContextMenu=System.Windows.Controls.ContextMenu;
using WpfMenuItem=System.Windows.Controls.MenuItem;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;

namespace BeeX.DeskNest;

/// <summary>
/// 截圖 OCR 結果視窗：左側截圖預覽、右側辨識結果；右下角「複製並關閉」，
/// Shift+C 立即複製並關閉，Esc 直接關閉。
/// 文字與公式不分模式：先跑文字 OCR，再用數學特徵啟發式自動判別是否為公式，
/// 是則追加公式辨識輸出 LaTeX，失敗自動回退文字結果。
/// </summary>
public sealed class OcrResultWindow : Window
{
    enum LayoutMode{Auto,RemoveLineBreaks,MultiLine,RemoveSpaces}
    enum PunctMode{Original,HalfWidth,FullWidth}
    enum LangMode{Follow,Simplified,Traditional,English,Japanese}

    // 会话内记住用户选择，新开窗口沿用
    static LayoutMode layoutMode=LayoutMode.Auto;
    static PunctMode punctMode=PunctMode.Original;
    static LangMode langMode=LangMode.Follow;

    readonly string imagePath;
    readonly string language;
    readonly WpfTextBox resultBox;
    readonly TextBlock statusText;
    readonly Button installBtn;
    readonly Button copyCloseBtn;
    readonly Button layoutBtn;
    readonly Button langBtn;
    string rawText="";
    bool isFormulaResult;
    bool isTableResult;
    List<string[]> tableRows=[];
    List<(int R1,int C1,int R2,int C2)> tableMerges=[];
    readonly Button exportExcelBtn;
    bool busy;
    bool hasResult;
    string tableHtml="";
    Microsoft.Web.WebView2.Wpf.WebView2? tableWeb;
    Task? tableWebInit;

    static readonly SolidColorBrush Surface=new(Color.FromArgb(244,13,19,33));
    static readonly SolidColorBrush BorderOrange=new(Color.FromArgb(150,255,138,0));
    static readonly SolidColorBrush Accent=new(Color.FromRgb(255,138,0));
    static readonly SolidColorBrush SubtleBtn=new(Color.FromArgb(60,255,255,255));
    static readonly SolidColorBrush PanelBg=new(Color.FromArgb(70,0,0,0));

    string L(string value)=>Localization.T(value,language);

    public OcrResultWindow(string imagePath,string language)
    {
        this.imagePath=imagePath;
        this.language=language;
        Title=L("截圖辨識");
        Width=940;Height=540;MinWidth=620;MinHeight=360;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        ResizeMode=ResizeMode.CanResizeWithGrip;ShowInTaskbar=true;Topmost=true;

        // 顶部不留内边距，让 65px 标题栏贴齐窗口顶边（与其他组件统一）
        var border=new Border{CornerRadius=new CornerRadius(14),Background=Surface,BorderBrush=BorderOrange,BorderThickness=new Thickness(1),Padding=new Thickness(16,0,16,16),SnapsToDevicePixels=true};
        var root=new Grid();
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});

        // 标题栏：logo + 标题居左，关闭按钮居右（留足边距避免被圆角裁切）；高度按物理 65px 换算，并在显示/跨屏后随 DPI 刷新
        var header=new Grid{Margin=new Thickness(0,0,0,12),MinHeight=TitleBarMetrics.Dip(this)};
        Loaded+=(_,_)=>header.MinHeight=TitleBarMetrics.Dip(this);
        DpiChanged+=(_,_)=>header.MinHeight=TitleBarMetrics.Dip(this);
        var brand=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        brand.Children.Add(new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")),Width=24,Height=24});
        brand.Children.Add(new TextBlock{Text=L("截圖辨識"),FontSize=17,FontWeight=FontWeights.SemiBold,Foreground=Brushes.White,Margin=new Thickness(10,0,0,0),VerticalAlignment=VerticalAlignment.Center});
        header.Children.Add(brand);
        var closeBtn=new Button
        {
            Content=new TextBlock{Text="✕",FontSize=14,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center},
            Width=36,Height=30,Padding=new Thickness(0),Margin=new Thickness(0,0,2,0),
            HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Center,
            HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center,
            Background=SubtleBtn,Foreground=Brushes.White,BorderThickness=new Thickness(0),Cursor=Cursors.Hand
        };
        closeBtn.Click+=(_,_)=>Close();
        header.Children.Add(closeBtn);
        root.Children.Add(header);

        // 主体：左图右文
        var body=new Grid();Grid.SetRow(body,1);
        body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(14)});
        body.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});

        var preview=new Image{Stretch=Stretch.Uniform,StretchDirection=StretchDirection.DownOnly,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        try
        {
            var bitmap=new BitmapImage();
            bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(imagePath);bitmap.EndInit();bitmap.Freeze();
            preview.Source=bitmap;
        }
        catch{}
        // 图片完整适配面板展示，不用滚动条；右键可复制图片
        var imagePanel=new Border{Background=PanelBg,CornerRadius=new CornerRadius(10),Padding=new Thickness(8),Child=preview};
        var imageMenu=new WpfContextMenu{Background=new SolidColorBrush(Color.FromArgb(236,13,19,33)),Foreground=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromArgb(160,255,138,0)),BorderThickness=new Thickness(1)};
        var copyImageItem=new WpfMenuItem{Header=L("複製圖片"),Foreground=Brushes.White};
        copyImageItem.Click+=(_,_)=>
        {
            if(preview.Source is not BitmapSource source)return;
            for(var attempt=0;attempt<8;attempt++)
            {
                try{Clipboard.SetImage(source);statusText.Text=L("圖片已複製到剪貼板");return;}
                catch{System.Threading.Thread.Sleep(60);}
            }
        };
        imageMenu.Items.Add(copyImageItem);
        imagePanel.ContextMenu=imageMenu;
        body.Children.Add(imagePanel);

        var right=new Grid();Grid.SetColumn(right,2);
        right.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        right.RowDefinitions.Add(new RowDefinition());

        // 文字/公式自动判别，无需模式按钮；此行仅在未安装 OCR 元件时显示安装入口
        installBtn=new Button{Content=L("安裝 OCR 辨識"),MinWidth=118,Height=30,Margin=new Thickness(0,0,0,8),HorizontalAlignment=HorizontalAlignment.Left,Background=Accent,Foreground=Brushes.White,BorderThickness=new Thickness(0),Cursor=Cursors.Hand,FontWeight=FontWeights.SemiBold,Visibility=Visibility.Collapsed};
        installBtn.Click+=async (_,_)=>await InstallAsync();
        right.Children.Add(installBtn);

        resultBox=new WpfTextBox
        {
            AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,IsReadOnly=false,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            Background=PanelBg,Foreground=Brushes.White,CaretBrush=Brushes.White,
            BorderBrush=new SolidColorBrush(Color.FromArgb(70,255,255,255)),BorderThickness=new Thickness(1),
            Padding=new Thickness(10),FontSize=14
        };
        Grid.SetRow(resultBox,1);
        right.Children.Add(resultBox);
        // WebView2 for table rendering (hidden until table result arrives)
        tableWeb=new Microsoft.Web.WebView2.Wpf.WebView2{Visibility=Visibility.Collapsed,DefaultBackgroundColor=System.Drawing.Color.FromArgb(244,13,19,33)};
        Grid.SetRow(tableWeb,1);
        right.Children.Add(tableWeb);
        body.Children.Add(right);
        root.Children.Add(body);

        // 底栏：快捷键提示居左；右下角依次为 排版設置、辨識語言、複製並關閉
        var footer=new Grid{Margin=new Thickness(0,12,0,0)};
        Grid.SetRow(footer,2);
        statusText=new TextBlock{Text=L("Shift+C 複製並關閉 · Esc 關閉"),Foreground=new SolidColorBrush(Color.FromArgb(150,255,255,255)),VerticalAlignment=VerticalAlignment.Center,HorizontalAlignment=HorizontalAlignment.Left};
        footer.Children.Add(statusText);
        var footerRight=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var formulaBtn=new Button{Content=BtnContent(L("獲取公式"),"math-function",14,iconFirst:true),MinWidth=96,Height=34,Margin=new Thickness(0,0,8,0),Padding=new Thickness(10,0,10,0),Background=SubtleBtn,Foreground=Brushes.White,BorderThickness=new Thickness(0),Cursor=Cursors.Hand,ToolTip=L("偵測失敗時可手動按公式重新辨識")};
        formulaBtn.Click+=async (_,_)=>await ForceFormulaAsync();
        footerRight.Children.Add(formulaBtn);
        var tableBtn=new Button{Content=BtnContent(L("獲取表格"),"table",14,iconFirst:true),MinWidth=96,Height=34,Margin=new Thickness(0,0,8,0),Padding=new Thickness(10,0,10,0),Background=SubtleBtn,Foreground=Brushes.White,BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        tableBtn.Click+=async (_,_)=>await ForceTableAsync();
        footerRight.Children.Add(tableBtn);
        exportExcelBtn=new Button{Content=L("導出 Excel"),MinWidth=96,Height=34,Margin=new Thickness(0,0,8,0),Padding=new Thickness(10,0,10,0),Background=SubtleBtn,Foreground=Brushes.White,BorderThickness=new Thickness(0),Cursor=Cursors.Hand,Visibility=Visibility.Collapsed};
        exportExcelBtn.Click+=(_,_)=>ExportExcel();
        layoutBtn=new Button{Content=BtnContent(L("排版"),"chevron-up",12),MinWidth=76,Height=34,Margin=new Thickness(0,0,8,0),Padding=new Thickness(10,0,10,0),Background=SubtleBtn,Foreground=Brushes.White,BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        layoutBtn.Click+=(_,_)=>ShowDropUp(layoutBtn,BuildLayoutMenu());
        footerRight.Children.Add(layoutBtn);
        langBtn=new Button{Content=BtnContent(L("辨識語言"),"chevron-up",12),MinWidth=92,Height=34,Margin=new Thickness(0,0,8,0),Padding=new Thickness(10,0,10,0),Background=SubtleBtn,Foreground=Brushes.White,BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        langBtn.Click+=(_,_)=>ShowDropUp(langBtn,BuildLanguageMenu());
        footerRight.Children.Add(langBtn);
        footerRight.Children.Add(exportExcelBtn);
        copyCloseBtn=new Button{Content=L("複製並關閉"),MinWidth=118,Height=34,Padding=new Thickness(14,0,14,0),Background=Accent,Foreground=Brushes.White,BorderThickness=new Thickness(0),Cursor=Cursors.Hand,FontWeight=FontWeights.SemiBold};
        copyCloseBtn.Click+=(_,_)=>CopyAndClose();
        footerRight.Children.Add(copyCloseBtn);
        footer.Children.Add(footerRight);
        root.Children.Add(footer);

        border.Child=root;
        Content=border;

        // 整窗空白区域均可拖动（自动排除按钮/文本框/滚动条等交互控件）；
        // 之前只挂在标题栏 Grid 上，无背景的 Grid 仅子元素处响应命中测试，导致只有零星区域能拖
        PreviewMouseLeftButtonDown+=(_,e)=>
        {
            if(e.ClickCount!=1||IsInteractive(e.OriginalSource as DependencyObject))return;
            try{DragMove();}catch{}
        };
        PreviewKeyDown+=OnPreviewKey;
        Closed+=(_,_)=>{try{if(File.Exists(this.imagePath))File.Delete(this.imagePath);}catch{}tableWeb?.Dispose();};

        if(OcrSidecarService.IsAvailable)
        {
            _=RecognizeAutoAsync();
            _=ShowUpdateHintAsync();
        }
        else
        {
            ShowInstallPrompt();
        }
    }

    static bool IsInteractive(DependencyObject? current)
    {
        while(current!=null)
        {
            if(current is System.Windows.Controls.Primitives.ButtonBase or WpfTextBox or System.Windows.Controls.Primitives.ScrollBar or System.Windows.Controls.Primitives.Thumb)return true;
            current=VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    /// <summary>按钮内容：文字 + 内置 tabler 图标（不依赖外部图标库）。</summary>
    static StackPanel BtnContent(string text,string icon,double iconSize,bool iconFirst=false)
    {
        var panel=new StackPanel{Orientation=Orientation.Horizontal,IsHitTestVisible=false};
        var image=new Image{Source=SvgIcon.Load(icon,iconSize,Brushes.White),Width=iconSize,Height=iconSize,VerticalAlignment=VerticalAlignment.Center};
        var label=new TextBlock{Text=text,Foreground=Brushes.White,VerticalAlignment=VerticalAlignment.Center};
        if(iconFirst){image.Margin=new Thickness(0,0,6,0);panel.Children.Add(image);panel.Children.Add(label);}
        else{image.Margin=new Thickness(6,1,0,0);panel.Children.Add(label);panel.Children.Add(image);}
        return panel;
    }

    /// <summary>手动按公式重新辨識：自动判别漏掉公式时的兜底入口。</summary>
    async Task ForceFormulaAsync()
    {
        if(busy)return;
        if(!OcrSidecarService.IsAvailable){ShowInstallPrompt();return;}
        busy=true;
        resultBox.IsReadOnly=true;
        copyCloseBtn.IsEnabled=false;
        resultBox.Text=L("偵測到數學公式，正在辨識…");
        try
        {
            var latex=(await OcrSidecarService.RecognizeFormulaAsync(imagePath)).Trim();
            if(latex.Length>0)
            {
                rawText=latex;
                isFormulaResult=true;
                isTableResult=false;
                exportExcelBtn.Visibility=Visibility.Collapsed;
                hasResult=true;
                ShowTableWebView(false);
                RenderResult();
            }
            else
            {
                resultBox.Text=L("未辨識到文字");
            }
        }
        catch(Exception ex)
        {
            resultBox.Text=L("辨識失敗")+"："+ex.Message;
        }
        finally
        {
            busy=false;
            resultBox.IsReadOnly=false;
            copyCloseBtn.IsEnabled=true;
        }
    }

    /// <summary>手动按表格辨識：侧车返回 HTML 表格，用 WebView2 渲染带边框的表格，并启用 Excel 导出。</summary>
    async Task ForceTableAsync()
    {
        if(busy)return;
        if(!OcrSidecarService.IsAvailable){ShowInstallPrompt();return;}
        busy=true;
        resultBox.IsReadOnly=true;
        copyCloseBtn.IsEnabled=false;
        resultBox.Text=L("表格辨識中…");
        try
        {
            var html=(await OcrSidecarService.RecognizeTableAsync(imagePath)).Trim();
            var(grid,merges)=ParseHtmlTable(html);
            if(grid.Count==0)
            {
                ShowTableWebView(false);
                resultBox.Text=L("未偵測到表格");
                return;
            }
            tableRows=grid;
            tableMerges=merges;
            tableHtml=html;
            rawText=ToMarkdown(grid);
            isTableResult=true;
            isFormulaResult=false;
            hasResult=true;
            exportExcelBtn.Visibility=Visibility.Visible;
            ShowTableWebView(true);
            RenderResult();
        }
        catch(Exception ex)
        {
            ShowTableWebView(false);
            resultBox.Text=L("辨識失敗")+"："+ex.Message;
        }
        finally
        {
            busy=false;
            resultBox.IsReadOnly=false;
            copyCloseBtn.IsEnabled=true;
        }
    }

    /// <summary>解析带 colspan/rowspan 的 HTML 表格：展开成完整网格 + 合并区域列表（供 Excel mergeCells）。</summary>
    static (List<string[]> Grid,List<(int R1,int C1,int R2,int C2)> Merges) ParseHtmlTable(string html)
    {
        var rowsRaw=new List<List<(string Text,int Cs,int Rs)>>();
        foreach(Match tr in Regex.Matches(html,@"<tr>(.*?)</tr>",RegexOptions.Singleline))
        {
            var row=new List<(string,int,int)>();
            foreach(Match td in Regex.Matches(tr.Groups[1].Value,@"<td([^>]*)>(.*?)</td>",RegexOptions.Singleline))
            {
                var attrs=td.Groups[1].Value;
                var text=System.Net.WebUtility.HtmlDecode(Regex.Replace(td.Groups[2].Value,"<[^>]+>","")).Trim();
                row.Add((text,AttrInt(attrs,"colspan"),AttrInt(attrs,"rowspan")));
            }
            rowsRaw.Add(row);
        }

        int rowCount=rowsRaw.Count;
        var values=new Dictionary<(int,int),string>();
        var occupied=new HashSet<(int,int)>();
        var merges=new List<(int,int,int,int)>();
        int maxCol=0;
        for(int r=0;r<rowCount;r++)
        {
            int c=0;
            foreach(var(text,cs,rs)in rowsRaw[r])
            {
                while(occupied.Contains((r,c)))c++;
                values[(r,c)]=text;
                int r2=Math.Min(rowCount,r+rs)-1,c2=c+cs-1;
                for(int rr=r;rr<=r2;rr++)for(int cc=c;cc<=c2;cc++)occupied.Add((rr,cc));
                if(r2>r||c2>c)merges.Add((r,c,r2,c2));
                maxCol=Math.Max(maxCol,c2+1);
                c=c2+1;
            }
        }

        var grid=new List<string[]>();
        for(int r=0;r<rowCount;r++)
        {
            var arr=new string[maxCol];
            for(int c=0;c<maxCol;c++)arr[c]=values.TryGetValue((r,c),out var v)?v:"";
            grid.Add(arr);
        }
        return(grid,merges);
    }

    static int AttrInt(string attrs,string name)
    {
        var match=Regex.Match(attrs,name+@"=""(\d+)""");
        return match.Success?Math.Max(1,int.Parse(match.Groups[1].Value)):1;
    }

    static string ToMarkdown(List<string[]> rows)
    {
        var columns=rows.Max(r=>r.Length);
        var builder=new StringBuilder();
        for(var i=0;i<rows.Count;i++)
        {
            var cells=Enumerable.Range(0,columns).Select(c=>c<rows[i].Length?rows[i][c].Replace("|","\\|"):"");
            builder.Append("| ").Append(string.Join(" | ",cells)).AppendLine(" |");
            if(i==0)builder.Append("|").Append(string.Concat(Enumerable.Repeat(" --- |",columns))).AppendLine();
        }
        return builder.ToString().TrimEnd();
    }

    void ExportExcel()
    {
        if(tableRows.Count==0)return;
        var dialog=new Microsoft.Win32.SaveFileDialog
        {
            Filter="Excel (*.xlsx)|*.xlsx",
            FileName=$"BeeX_Table_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
        };
        if(dialog.ShowDialog(this)!=true)return;
        try
        {
            ExcelExporter.Save(dialog.FileName,tableRows,tableMerges);
            statusText.Text=L("已導出 Excel")+"："+dialog.FileName;
        }
        catch(Exception ex)
        {
            statusText.Text=L("導出失敗。")+ex.Message;
        }
    }

    void OnPreviewKey(object sender,KeyEventArgs e)
    {
        // 中文输入法开启时按键被吞成 ImeProcessed，还原真实键值保证 Shift+C 可用
        var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
        if(key==Key.Escape){e.Handled=true;Close();return;}
        if(key==Key.C&&Keyboard.Modifiers==ModifierKeys.Shift){e.Handled=true;CopyAndClose();}
    }

    static bool updateChecked;

    /// <summary>每次运行只检测一次更新；有新包时把安装按钮作为“更新”入口显示（不弹窗不打断）。</summary>
    async Task ShowUpdateHintAsync()
    {
        if(updateChecked)return;
        updateChecked=true;
        try
        {
            if(await OcrInstallerService.CheckUpdateAsync())
            {
                installBtn.Content=L("更新 OCR 元件");
                installBtn.Visibility=Visibility.Visible;
            }
        }
        catch{}
    }

    void ShowInstallPrompt()
    {
        installBtn.Visibility=Visibility.Visible;
        resultBox.IsReadOnly=true;
        resultBox.Text=L("首次使用需下載 OCR 辨識元件（約 600 MB，僅需一次），之後完全離線使用。元件會安裝到 BeeX 資料目錄，不影響主程式。");
    }

    bool installHooked;

    /// <summary>安裝/更新 OCR 元件：交給後台安裝器，窗口只訂閱進度展示——隨時可關窗，下載繼續，完成後全局非模態通知。</summary>
    async Task InstallAsync()
    {
        if(busy)return;
        busy=true;
        installBtn.IsEnabled=false;
        resultBox.IsReadOnly=true;
        if(!installHooked)
        {
            installHooked=true;
            Action<(string Phase,int Percent)> onProgress=p=>resultBox.Text=p.Phase=="extract"?L("正在解壓安裝…"):L("正在下載 OCR 元件…")+(p.Percent>=0?" "+p.Percent+"%":"");
            Action<Exception?> onFinished=async ex=>
            {
                busy=false;
                if(ex==null)
                {
                    installBtn.Visibility=Visibility.Collapsed;
                    OcrSidecarService.WarmUp();
                    await RecognizeAutoAsync();
                }
                else
                {
                    resultBox.Text=L("安裝失敗")+"："+ex.Message+Environment.NewLine+L("請檢查網路後重試。");
                    installBtn.IsEnabled=true;
                }
            };
            OcrInstallerService.ProgressChanged+=onProgress;
            OcrInstallerService.InstallFinished+=onFinished;
            Closed+=(_,_)=>{OcrInstallerService.ProgressChanged-=onProgress;OcrInstallerService.InstallFinished-=onFinished;};
        }
        resultBox.Text=L("正在下載 OCR 元件…");
        OcrInstallerService.StartBackgroundInstall(language);
        await Task.CompletedTask;
    }

    /// <summary>统一识别流程：文字 OCR → 数学特征判别 → 需要时追加公式识别，失败回退文字。</summary>
    async Task RecognizeAutoAsync()
    {
        busy=true;
        hasResult=false;
        resultBox.IsReadOnly=true;
        resultBox.Text=L("辨識中…");
        copyCloseBtn.IsEnabled=false;
        try
        {
            string text;
            try
            {
                text=(await OcrSidecarService.RecognizeTextAsync(imagePath)).TrimEnd();
            }
            catch(Exception ex)
            {
                resultBox.Text=L("辨識失敗")+"："+ex.Message;
                return;
            }

            if(LooksLikeFormula(text))
            {
                resultBox.Text=L("偵測到數學公式，正在辨識…");
                try
                {
                    var latex=(await OcrSidecarService.RecognizeFormulaAsync(imagePath)).Trim();
                    if(latex.Length>0)
                    {
                        rawText=latex;
                        isFormulaResult=true;
                        isTableResult=false;
                        exportExcelBtn.Visibility=Visibility.Collapsed;
                        hasResult=true;
                        ShowTableWebView(false);
                        RenderResult();
                        return;
                    }
                }
                catch
                {
                    // 公式侧车失败不打断流程，回退文字结果
                }
            }

            rawText=text;
            isFormulaResult=false;
            isTableResult=false;
            exportExcelBtn.Visibility=Visibility.Collapsed;
            hasResult=text.Length>0;
            ShowTableWebView(false);
            if(hasResult)RenderResult();
            else resultBox.Text=L("未辨識到文字");
        }
        finally
        {
            busy=false;
            resultBox.IsReadOnly=false;
            copyCloseBtn.IsEnabled=true;
        }
    }

    /// <summary>
    /// 数学公式启发式：截图更像公式而非普通文字时返回 true。
    /// 依据：文字 OCR 为空（纯符号图形识别不出）、数学符号密度、上下标/分式/等式等 LaTeX 常见形态；
    /// 含 CJK 长句或多行普通文本时视为文字。
    /// </summary>
    internal static bool LooksLikeFormula(string text)
    {
        var trimmed=text.Trim();
        if(trimmed.Length==0)return true;            // 文字 OCR 拿不到内容，很可能是纯公式/符号
        if(trimmed.Length>160)return false;           // 长文本必是普通文字

        var lines=trimmed.Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
        if(lines.Length>4)return false;

        var cjk=0;var mathSymbols=0;var letters=0;var digits=0;
        foreach(var c in trimmed)
        {
            if(c is >= '\u3400' and <= '\u9fff')cjk++;
            else if("∫∑∏√±×÷≤≥≠≈∞∂∇πθλμσφψωΔΩΓΦαβγδε∈∀∃⊂⊆∪∩→⇒↔ℝℤℕ′″^".Contains(c))mathSymbols++;
            else if(char.IsLetter(c))letters++;
            else if(char.IsDigit(c))digits++;
        }

        // 中文占比高/代码特征 → 普通文字（优先排除，避免后面规则误判）
        if(cjk>2)return false;
        if(Regex.IsMatch(trimmed,@"\b(var|public|class|void|return|await|function|def|if|for|while)\b"))return false;
        if(trimmed.Contains(';')||trimmed.Contains("=>")||trimmed.Contains("://"))return false;

        if(mathSymbols>=2)return true;
        // 单个数学符号常见于公式被文字 OCR 误读后的残留（如 ∃→"3"、ℤ→"Z"），
        // 搭配等号且文本很短时仍判为公式
        if(mathSymbols>=1&&trimmed.Contains('=')&&trimmed.Length<=80&&lines.Length<=2)return true;

        // 等式/分式/上下标形态：如 y=ax^2+bx+c、f(x)=1/2、a_i=b_j
        var structural=Regex.Matches(trimmed,@"[=^_/(){}\[\]|]").Count;
        if(structural>=3&&digits+letters>0&&letters<=trimmed.Length*0.6)
        {
            return true;
        }

        return false;
    }

    /// <summary>把排版/标点/语言设置应用到原始识别文本后刷新显示；LaTeX 公式结果不做内容变换，
    /// 仅按 LaTeX 自身的换行结构美化显示（空白对 LaTeX 语义无影响，复制出去仍是合法公式）。</summary>
    void RenderResult()
    {
        if(!hasResult)return;
        if(isFormulaResult){resultBox.Text=FormatLatexForDisplay(rawText);return;}
        if(isTableResult){_ =RenderTableAsync();return;} // 表格用 WebView2 渲染 HTML，复制时仍用 Markdown

        var text=rawText;
        text=layoutMode switch
        {
            LayoutMode.RemoveLineBreaks=>JoinLines(text),
            LayoutMode.MultiLine=>string.Join(Environment.NewLine,SplitLines(text)),
            LayoutMode.RemoveSpaces=>text.Replace(" ","").Replace("\u3000",""),
            _=>text
        };
        text=punctMode switch
        {
            PunctMode.HalfWidth=>ToHalfWidthPunctuation(text),
            PunctMode.FullWidth=>ToFullWidthPunctuation(text),
            _=>text
        };
        text=EffectiveLangMode() switch
        {
            LangMode.Simplified=>ChineseVariantConverter.ToSimplified(text),
            LangMode.Traditional=>ChineseVariantConverter.ToTraditional(text),
            _=>text
        };
        resultBox.Text=text;
    }

    /// <summary>切换表格 WebView2 与文字 TextBox 的可见性。</summary>
    void ShowTableWebView(bool show)
    {
        if(show)
        {
            tableWeb.Visibility=Visibility.Visible;
            resultBox.Visibility=Visibility.Collapsed;
        }
        else
        {
            tableWeb.Visibility=Visibility.Collapsed;
            resultBox.Visibility=Visibility.Visible;
        }
    }

    /// <summary>初始化表格 WebView2（懒加载，只执行一次）。</summary>
    async Task InitTableWebViewAsync()
    {
        if(tableWebInit!=null){await tableWebInit;return;}
        try
        {
            tableWebInit=InitAsync();
            await tableWebInit;
        }
        catch
        {
            tableWebInit=null;
            ShowTableWebView(false);
            throw;
        }
        async Task InitAsync()
        {
            var userData=Path.Combine(Path.GetTempPath(),"BeeX_OCR_TableWV2");
            Directory.CreateDirectory(userData);
            var env=await CoreWebView2Environment.CreateAsync(null,userData);
            await tableWeb.EnsureCoreWebView2Async(env);
            var core=tableWeb.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled=false;
            core.Settings.AreBrowserAcceleratorKeysEnabled=false;
            core.Settings.IsStatusBarEnabled=false;
            core.Settings.IsZoomControlEnabled=false;
            core.Settings.AreDevToolsEnabled=false;
        }
    }

    /// <summary>在 WebView2 中渲染表格 HTML（带深色主题 CSS 边框）。</summary>
    async Task RenderTableAsync()
    {
        try
        {
            await InitTableWebViewAsync();
            tableWeb.NavigateToString(WrapTableHtml(tableHtml));
        }
        catch
        {
            // WebView2 初始化失败时回退到 Markdown 文本显示
            ShowTableWebView(false);
            resultBox.Text=rawText;
        }
    }

    /// <summary>将表格 HTML 包裹为带深色主题 CSS 样式的完整 HTML 文档。</summary>
    static string WrapTableHtml(string tableHtml)
    {
        const string Head="<!DOCTYPE html><html><head><meta charset=\"utf-8\"><style>"
            +"body{margin:0;padding:10px;background:#0d1321;color:#e8e8e8;font-family:'Microsoft JhengHei','Segoe UI',sans-serif;font-size:14px;}"
            +"table{border:2px solid #8899aa;border-collapse:collapse;margin:4px 0;width:100%;}"
            +"th,td{border:1.5px solid #667788;padding:6px 10px;text-align:left;}"
            +"th{background:#1a2535;font-weight:600;}"
            +"tr:nth-child(even) td{background:#111b28;}"
            +"tr:hover td{background:#1e2d3d;}"
            +"</style></head><body>";
        return Head+tableHtml+"</body></html>";
    }

    /// <summary>
    /// LaTeX 显示美化：`\\` 是 LaTeX 的换行符，在其后插入真换行；
    /// \begin/\end 环境边界各自成行。只加空白不改内容，LaTeX 语义不变。
    /// </summary>
    internal static string FormatLatexForDisplay(string latex)
    {
        var text=Regex.Replace(latex,@"\\\\\s*","\\\\"+Environment.NewLine);
        text=Regex.Replace(text,@"\s*(\\begin\{[^}]*\})",Environment.NewLine+"$1"+Environment.NewLine);
        text=Regex.Replace(text,@"\s*(\\end\{[^}]*\})\s*",Environment.NewLine+"$1"+Environment.NewLine);
        return Regex.Replace(text,@"(\r?\n){3,}",Environment.NewLine+Environment.NewLine).Trim();
    }

    LangMode EffectiveLangMode()
    {
        if(langMode!=LangMode.Follow)return langMode;
        return language switch
        {
            "zh-CN"=>LangMode.Simplified,
            "zh-TW"=>LangMode.Traditional,
            _=>LangMode.English // 英/日等界面语言不做简繁转换
        };
    }

    void ShowDropUp(Button target,WpfContextMenu menu)
    {
        menu.PlacementTarget=target;
        menu.Placement=System.Windows.Controls.Primitives.PlacementMode.Top;
        menu.IsOpen=true;
    }

    static WpfContextMenu NewMenu()=>new(){Background=new SolidColorBrush(Color.FromArgb(236,13,19,33)),Foreground=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromArgb(160,255,138,0)),BorderThickness=new Thickness(1)};

    WpfMenuItem CheckItem(string title,bool isChecked,Action onSelect)
    {
        var item=new WpfMenuItem{Header=L(title),IsCheckable=true,IsChecked=isChecked,Foreground=Brushes.White};
        item.Click+=(_,_)=>{onSelect();RenderResult();};
        return item;
    }

    WpfContextMenu BuildLayoutMenu()
    {
        var menu=NewMenu();
        menu.Items.Add(CheckItem("自動",layoutMode==LayoutMode.Auto,()=>layoutMode=LayoutMode.Auto));
        menu.Items.Add(CheckItem("移除換行符",layoutMode==LayoutMode.RemoveLineBreaks,()=>layoutMode=LayoutMode.RemoveLineBreaks));
        menu.Items.Add(CheckItem("多行",layoutMode==LayoutMode.MultiLine,()=>layoutMode=LayoutMode.MultiLine));
        menu.Items.Add(CheckItem("去掉空格",layoutMode==LayoutMode.RemoveSpaces,()=>layoutMode=LayoutMode.RemoveSpaces));
        // WPF 原生子菜单弹出面板是系统白底，白字不可见；改用手动二级上拉（同款深色主题）
        var punct=new WpfMenuItem{Header=L("標點")+(punctMode!=PunctMode.Original?" ✓":"")+"  ▸",Foreground=Brushes.White,StaysOpenOnClick=false};
        punct.Click+=(_,_)=>
        {
            var sub=BuildPunctMenu();
            sub.Placement=System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            sub.IsOpen=true;
        };
        menu.Items.Add(punct);
        return menu;
    }

    /// <summary>标点二级菜单：半角/全角互斥，再次点击已勾选项 = 取消勾选（恢复原标点）。</summary>
    WpfContextMenu BuildPunctMenu()
    {
        var menu=NewMenu();
        menu.Items.Add(CheckItem("半角",punctMode==PunctMode.HalfWidth,()=>punctMode=punctMode==PunctMode.HalfWidth?PunctMode.Original:PunctMode.HalfWidth));
        menu.Items.Add(CheckItem("全角",punctMode==PunctMode.FullWidth,()=>punctMode=punctMode==PunctMode.FullWidth?PunctMode.Original:PunctMode.FullWidth));
        return menu;
    }

    WpfContextMenu BuildLanguageMenu()
    {
        var menu=NewMenu();
        menu.Items.Add(CheckItem("跟隨軟體",langMode==LangMode.Follow,()=>langMode=LangMode.Follow));
        menu.Items.Add(CheckItem("簡體",langMode==LangMode.Simplified,()=>langMode=LangMode.Simplified));
        menu.Items.Add(CheckItem("繁體",langMode==LangMode.Traditional,()=>langMode=LangMode.Traditional));
        menu.Items.Add(CheckItem("英語",langMode==LangMode.English,()=>langMode=LangMode.English));
        menu.Items.Add(CheckItem("日語",langMode==LangMode.Japanese,()=>langMode=LangMode.Japanese));
        return menu;
    }

    static string[] SplitLines(string text)=>text.Replace("\r\n","\n").Split('\n',StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);

    /// <summary>合并换行：中文之间直接相连，拉丁单词之间补空格。</summary>
    static string JoinLines(string text)
    {
        var builder=new StringBuilder();
        foreach(var line in SplitLines(text))
        {
            if(builder.Length>0)
            {
                var prev=builder[^1];var next=line[0];
                if(!(IsCjkChar(prev)||IsCjkChar(next)))builder.Append(' ');
            }
            builder.Append(line);
        }
        return builder.ToString();
    }

    static bool IsCjkChar(char c)=>c is >= '\u3000' and <= '\u9fff' or >= '\uff00' and <= '\uffef';

    static readonly (char Full,char Half)[] PunctPairs=
    [
        ('，',','),('。','.'),('！','!'),('？','?'),('：',':'),('；',';'),('（','('),('）',')'),
        ('【','['),('】',']'),('「','"'),('」','"'),('『','\''),('』','\''),('、',','),('“','"'),('”','"'),
        ('‘','\''),('’','\''),('－','-'),('～','~'),('　',' ')
    ];

    static string ToHalfWidthPunctuation(string text)
    {
        var builder=new StringBuilder(text);
        foreach(var(full,half)in PunctPairs)builder.Replace(full,half);
        return builder.ToString();
    }

    static string ToFullWidthPunctuation(string text)
    {
        // 先处理需要上下文的：数字中间的 . 和 , 保留（小数点/千分位）
        text=Regex.Replace(text,@"(?<!\d)\.(?!\d)","。");
        text=Regex.Replace(text,@"(?<!\d),(?!\d)","，");
        var builder=new StringBuilder(text);
        foreach(var(full,half)in PunctPairs)
        {
            if(half is '.' or ',' or '"' or '\'' or ' ')continue; // 引号方向性/空格不反向替换
            builder.Replace(half,full);
        }
        return builder.ToString();
    }

    /// <summary>简繁转换：Windows 原生 LCMapStringEx，零外部依赖（字级映射，足够 OCR 场景使用）。</summary>
    static class ChineseVariantConverter
    {
        const uint LcmapSimplifiedChinese=0x02000000;
        const uint LcmapTraditionalChinese=0x04000000;

        public static string ToSimplified(string text)=>Map(text,LcmapSimplifiedChinese);
        public static string ToTraditional(string text)=>Map(text,LcmapTraditionalChinese);

        static string Map(string text,uint flag)
        {
            if(string.IsNullOrEmpty(text))return text;
            try
            {
                var buffer=new StringBuilder(text.Length*2);
                var written=LCMapStringEx("zh-CN",flag,text,text.Length,buffer,buffer.Capacity,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero);
                return written>0?buffer.ToString(0,written):text;
            }
            catch{return text;}
        }

        [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
        static extern int LCMapStringEx(string lpLocaleName,uint dwMapFlags,string lpSrcStr,int cchSrc,StringBuilder lpDestStr,int cchDest,IntPtr lpVersionInformation,IntPtr lpReserved,IntPtr sortHandle);
    }

    void CopyAndClose()
    {
        var text=resultBox.Text;
        if(hasResult&&!busy&&!string.IsNullOrWhiteSpace(text))
        {
            for(var attempt=0;attempt<8;attempt++)
            {
                try{Clipboard.SetText(text);break;}
                catch{System.Threading.Thread.Sleep(60);}
            }
        }
        Close();
    }
}
