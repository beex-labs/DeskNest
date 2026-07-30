using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using IoPath=System.IO.Path;
using Brushes=System.Windows.Media.Brushes;
using Button=System.Windows.Controls.Button;
using Color=System.Windows.Media.Color;
using Orientation=System.Windows.Controls.Orientation;
using HorizontalAlignment=System.Windows.HorizontalAlignment;

namespace BeeX.DeskNest;

/// <summary>
/// OCR 元件在线安装器：从 beex-ocr 仓库的 GitHub Release 下载固定名安装包，
/// 解压到 BeeX 根目录 Components\beex-ocr。
/// 主程序 exe 不内置任何 OCR 依赖，体积零增长；安装仅首次需要，之后完全离线。
/// </summary>
static class OcrInstallerService
{
    const string DownloadUrl="https://github.com/beex-labs/beex-ocr/releases/latest/download/beex-ocr-win-x64.zip";

    public static string InstallRoot=>BeeXPaths.OcrDir;

    /// <summary>安装完成的判据：两个侧车 exe 和模型目录齐全。</summary>
    public static bool IsInstalled=>
        File.Exists(IoPath.Combine(InstallRoot,"BeeX_OCR.exe"))&&
        File.Exists(IoPath.Combine(InstallRoot,"BeeX_Formula.exe"))&&
        Directory.Exists(IoPath.Combine(InstallRoot,"models"));

    static string StampPath=>InstallRoot+".stamp";

    // ---- 後台安裝管理：下載掛後台不佔用前台（窗口可關），完成/失敗以非模態通知回報 ----
    static Task? installTask;
    static (string Phase,int Percent) lastProgress=("download",-1);
    public static bool Installing=>installTask is {IsCompleted:false};
    public static (string Phase,int Percent) LastProgress=>lastProgress;
    public static event Action<(string Phase,int Percent)>? ProgressChanged;
    public static event Action<Exception?>? InstallFinished;

    /// <summary>在 UI 線程調用：啟動後台下載/更新（已在下載則忽略），進度與完成事件在 UI 線程回調。</summary>
    public static void StartBackgroundInstall(string language)
    {
        if(Installing)return;
        var progress=new Progress<(string Phase,int Percent)>(p=>{lastProgress=p;ProgressChanged?.Invoke(p);});
        installTask=RunBackgroundAsync(progress,language);
    }

    static async Task RunBackgroundAsync(IProgress<(string Phase,int Percent)> progress,string language)
    {
        try
        {
            await InstallAsync(progress);
            InstallFinished?.Invoke(null);
            BeeXDialog.Notify(null,Localization.T("安裝 OCR 辨識",language),Localization.T("OCR 元件安裝完成，可以開始使用截圖辨識與翻譯。",language),new AppState{Language=language});
        }
        catch(Exception ex)
        {
            InstallFinished?.Invoke(ex);
            BeeXDialog.Notify(null,Localization.T("安裝失敗",language),ex.Message+Environment.NewLine+Localization.T("請檢查網路後重試。",language),new AppState{Language=language});
        }
    }

    /// <summary>安裝對話框（說明 → 啟動後台下載 → 可關窗掛後台）；若用戶留在窗內等到完成則返回 true 可直接續接原流程（如翻譯）。</summary>
    public static bool ShowInstallDialog(string language)
    {
        if(OcrSidecarService.IsAvailable)return true;
        string T(string v)=>Localization.T(v,language);
        string ProgressText((string Phase,int Percent) p)=>p.Phase=="extract"?T("正在解壓安裝…"):T("正在下載 OCR 元件…")+(p.Percent>=0?" "+p.Percent+"%":"");
        var foreground=new SolidColorBrush(Color.FromRgb(13,19,33));
        var dialog=new Window{Title=T("安裝 OCR 辨識"),Width=450,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterScreen,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,Topmost=true};
        var border=new Border{CornerRadius=new CornerRadius(16),Background=new SolidColorBrush(Color.FromRgb(250,251,252)),BorderBrush=new SolidColorBrush(Color.FromArgb(115,255,138,0)),BorderThickness=new Thickness(1),Padding=new Thickness(24)};
        var root=new Grid();
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var heading=new TextBlock{Text=T("安裝 OCR 辨識"),Foreground=foreground,FontSize=20,FontWeight=FontWeights.SemiBold};
        var body=new TextBlock{Text=T("首次使用需下載 OCR 辨識元件（約 600 MB，僅需一次），之後完全離線使用。元件會安裝到 BeeX 資料目錄，不影響主程式。"),Foreground=new SolidColorBrush(Color.FromRgb(77,87,104)),FontSize=14,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,18)};
        Grid.SetRow(body,1);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content=T("取消"),MinWidth=88,Background=new SolidColorBrush(Color.FromRgb(255,243,229)),Foreground=foreground};
        var download=new Button{Content=T("安裝 OCR 辨識"),MinWidth=130,Margin=new Thickness(8,0,0,0),Background=new SolidColorBrush(Color.FromRgb(255,138,0)),Foreground=Brushes.White};
        void EnterDownloadingUi(){download.Visibility=Visibility.Collapsed;cancel.Content=T("後台繼續");body.Text=ProgressText(LastProgress);}
        Action<(string Phase,int Percent)> onProgress=p=>body.Text=ProgressText(p);
        Action<Exception?> onFinished=ex=>
        {
            if(ex==null){dialog.DialogResult=true;dialog.Close();return;}
            body.Text=T("安裝失敗")+"："+ex.Message+Environment.NewLine+T("請檢查網路後重試。");
            download.Visibility=Visibility.Visible;download.IsEnabled=true;cancel.Content=T("取消");
        };
        ProgressChanged+=onProgress;InstallFinished+=onFinished;
        dialog.Closed+=(_,_)=>{ProgressChanged-=onProgress;InstallFinished-=onFinished;};
        // 關窗掛後台時提示用戶可在設定頁查看進度
        void CloseToBackground(){var backgrounded=Installing;dialog.DialogResult=false;dialog.Close();if(backgrounded)BeeXDialog.Notify(null,T("安裝 OCR 辨識"),T("下載已轉入後台，可前往 設定 → 診斷與維護 查看進度。"),new AppState{Language=language});}
        cancel.Click+=(_,_)=>CloseToBackground();
        download.Click+=(_,_)=>{StartBackgroundInstall(language);EnterDownloadingUi();};
        if(Installing)EnterDownloadingUi();
        actions.Children.Add(cancel);actions.Children.Add(download);
        Grid.SetRow(actions,2);
        root.Children.Add(heading);root.Children.Add(body);root.Children.Add(actions);
        border.Child=root;dialog.Content=border;
        border.MouseLeftButtonDown+=(_,e)=>{if(e.LeftButton==System.Windows.Input.MouseButtonState.Pressed)try{dialog.DragMove();}catch{}};
        dialog.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape)CloseToBackground();};
        return dialog.ShowDialog()==true&&OcrSidecarService.IsAvailable;
    }

    /// <summary>
    /// 更新检测：用 GitHub Release 附件的 Last-Modified/ETag 做指纹，
    /// 安装时记录、之后 HEAD 请求对比；发新包只需重传 zip，无需改版本号/打 tag。
    /// 旧安装无指纹时不打扰（返回 false）。
    /// </summary>
    public static async Task<bool> CheckUpdateAsync()
    {
        if(!IsInstalled)return false;
        try
        {
            var local=File.Exists(StampPath)?(await File.ReadAllTextAsync(StampPath)).Trim():"";
            if(local.Length==0)return false;
            using var http=new HttpClient{Timeout=TimeSpan.FromSeconds(10)};
            http.DefaultRequestHeaders.UserAgent.ParseAdd("BeeX-DeskNest");
            using var request=new HttpRequestMessage(HttpMethod.Head,DownloadUrl);
            using var response=await http.SendAsync(request);
            if(!response.IsSuccessStatusCode)return false;
            var remote=ReadStamp(response);
            return remote.Length>0&&remote!=local;
        }
        catch{return false;}
    }

    static string ReadStamp(HttpResponseMessage response)=>
        response.Content.Headers.LastModified?.UtcDateTime.ToString("O")??response.Headers.ETag?.Tag??"";

    /// <summary>下载并安装 OCR 元件。progress: (阶段文案, 0-100 进度，-1 表示不确定)。</summary>
    public static async Task InstallAsync(IProgress<(string Phase,int Percent)> progress,CancellationToken cancellation=default)
    {
        var tempZip=IoPath.Combine(IoPath.GetTempPath(),$"beex-ocr-install-{Guid.NewGuid():N}.zip");
        var tempDir=InstallRoot+".installing";
        var stamp="";
        try
        {
            using(var http=new HttpClient{Timeout=TimeSpan.FromMinutes(30)})
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BeeX-DeskNest");
                using var response=await http.GetAsync(DownloadUrl,HttpCompletionOption.ResponseHeadersRead,cancellation);
                response.EnsureSuccessStatusCode();
                stamp=ReadStamp(response);
                var total=response.Content.Headers.ContentLength??-1L;
                await using var source=await response.Content.ReadAsStreamAsync(cancellation);
                await using var target=File.Create(tempZip);
                var buffer=new byte[1<<20];
                long written=0;int read;int lastPercent=-1;
                while((read=await source.ReadAsync(buffer,cancellation))>0)
                {
                    await target.WriteAsync(buffer.AsMemory(0,read),cancellation);
                    written+=read;
                    var percent=total>0?(int)(written*100/total):-1;
                    if(percent!=lastPercent){lastPercent=percent;progress.Report(("download",percent));}
                }
            }

            progress.Report(("extract",-1));
            if(Directory.Exists(tempDir))Directory.Delete(tempDir,recursive:true);
            ZipFile.ExtractToDirectory(tempZip,tempDir);

            // 校验后原子替换旧安装
            if(!File.Exists(IoPath.Combine(tempDir,"BeeX_OCR.exe"))||
               !File.Exists(IoPath.Combine(tempDir,"BeeX_Formula.exe"))||
               !Directory.Exists(IoPath.Combine(tempDir,"models")))
            {
                throw new InvalidOperationException("安裝包內容不完整，請稍後重試。");
            }

            OcrSidecarService.Shutdown();
            if(Directory.Exists(InstallRoot))Directory.Delete(InstallRoot,recursive:true);
            Directory.CreateDirectory(IoPath.GetDirectoryName(InstallRoot)!);
            Directory.Move(tempDir,InstallRoot);
            try{await File.WriteAllTextAsync(StampPath,stamp,cancellation);}catch{}
        }
        finally
        {
            try{if(File.Exists(tempZip))File.Delete(tempZip);}catch{}
            try{if(Directory.Exists(tempDir))Directory.Delete(tempDir,recursive:true);}catch{}
        }
    }
}
