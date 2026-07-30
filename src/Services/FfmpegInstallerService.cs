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
/// ffmpeg 元件在線安裝器：不再隨主程式內置（便攜版體積歸零），首次使用錄屏/剪輯時
/// 或在設定頁按需下載，僅抽取 ffmpeg.exe 保存到 BeeX 根目錄 Components\ffmpeg，
/// 與 OCR 側車（OcrInstallerService）同一套按需下載範式。
/// </summary>
static class FfmpegInstallerService
{
    const string DownloadUrl="https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip";

    public static string InstallRoot=>BeeXPaths.FfmpegDir;

    public static string ExePath=>IoPath.Combine(InstallRoot,"ffmpeg.exe");

    public static bool IsInstalled=>File.Exists(ExePath)&&new FileInfo(ExePath).Length>1_000_000;

    // ---- 後台安裝管理：下載掛後台不佔用前台，完成/失敗以非模態通知回報 ----
    static Task? installTask;
    static (string Phase,int Percent) lastProgress=("download",-1);
    public static bool Installing=>installTask is {IsCompleted:false};
    public static (string Phase,int Percent) LastProgress=>lastProgress;
    public static event Action<(string Phase,int Percent)>? ProgressChanged;
    public static event Action<Exception?>? InstallFinished;

    /// <summary>在 UI 線程調用：啟動後台下載（已在下載/已安裝則忽略），進度與完成事件在 UI 線程回調。</summary>
    public static void StartBackgroundInstall(string language)
    {
        if(Installing||IsInstalled)return;
        var progress=new Progress<(string Phase,int Percent)>(p=>{lastProgress=p;ProgressChanged?.Invoke(p);});
        installTask=RunBackgroundAsync(progress,language);
    }

    static async Task RunBackgroundAsync(IProgress<(string Phase,int Percent)> progress,string language)
    {
        try
        {
            await InstallAsync(progress);
            InstallFinished?.Invoke(null);
            BeeXDialog.Notify(null,Localization.T("下載 ffmpeg 元件",language),Localization.T("ffmpeg 元件安裝完成，錄屏與剪輯功能已可用。",language),new AppState{Language=language});
        }
        catch(Exception ex)
        {
            InstallFinished?.Invoke(ex);
            BeeXDialog.Notify(null,Localization.T("安裝失敗",language),ex.Message+Environment.NewLine+Localization.T("請檢查網路後重試。",language),new AppState{Language=language});
        }
    }

    /// <summary>下載官方構建 zip，僅抽取 ffmpeg.exe 到安裝目錄。progress:(階段, 0-100，-1 表示不確定)。</summary>
    public static async Task InstallAsync(IProgress<(string Phase,int Percent)> progress,CancellationToken cancellation=default)
    {
        var tempZip=IoPath.Combine(IoPath.GetTempPath(),$"beex-ffmpeg-{Guid.NewGuid():N}.zip");
        try
        {
            using(var http=new HttpClient{Timeout=TimeSpan.FromMinutes(30)})
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("BeeX-DeskNest");
                using var response=await http.GetAsync(DownloadUrl,HttpCompletionOption.ResponseHeadersRead,cancellation);
                response.EnsureSuccessStatusCode();
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
            await Task.Run(()=>
            {
                using var zip=ZipFile.OpenRead(tempZip);
                var entry=zip.Entries.FirstOrDefault(e=>e.Name.Equals("ffmpeg.exe",StringComparison.OrdinalIgnoreCase))
                    ??throw new InvalidOperationException("安裝包內容不完整，請稍後重試。");
                Directory.CreateDirectory(InstallRoot);
                var tmp=ExePath+".tmp";
                entry.ExtractToFile(tmp,overwrite:true);
                if(File.Exists(ExePath))File.Delete(ExePath);
                File.Move(tmp,ExePath);
            },cancellation);
            FfmpegService.Invalidate();
            if(!IsInstalled)throw new InvalidOperationException("安裝包內容不完整，請稍後重試。");
        }
        finally
        {
            try{if(File.Exists(tempZip))File.Delete(tempZip);}catch{}
        }
    }

    /// <summary>下載對話框（說明 → 啟動後台下載 → 可關窗掛後台）；若用戶留在窗內等到完成則返回 true 可直接續接原流程。</summary>
    public static bool ShowInstallDialog(string language)
    {
        if(FfmpegService.IsAvailable)return true;
        string T(string v)=>Localization.T(v,language);
        string ProgressText((string Phase,int Percent) p)=>p.Phase=="extract"?T("正在解壓安裝…"):T("正在下載 ffmpeg 元件…")+(p.Percent>=0?" "+p.Percent+"%":"");
        var foreground=new SolidColorBrush(Color.FromRgb(13,19,33));
        var dialog=new Window{Title=T("下載 ffmpeg 元件"),Width=450,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterScreen,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,Topmost=true};
        var border=new Border{CornerRadius=new CornerRadius(16),Background=new SolidColorBrush(Color.FromRgb(250,251,252)),BorderBrush=new SolidColorBrush(Color.FromArgb(115,255,138,0)),BorderThickness=new Thickness(1),Padding=new Thickness(24)};
        var root=new Grid();
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var heading=new TextBlock{Text=T("下載 ffmpeg 元件"),Foreground=foreground,FontSize=20,FontWeight=FontWeights.SemiBold};
        var body=new TextBlock{Text=T("錄屏、GIF 與視頻剪輯需要 ffmpeg 元件（約 90 MB，僅需一次）。下載後保存到 BeeX 資料目錄，不影響主程式。"),Foreground=new SolidColorBrush(Color.FromRgb(77,87,104)),FontSize=14,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,18)};
        Grid.SetRow(body,1);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content=T("取消"),MinWidth=88,Background=new SolidColorBrush(Color.FromRgb(255,243,229)),Foreground=foreground};
        var download=new Button{Content=T("下載 ffmpeg 元件"),MinWidth=130,Margin=new Thickness(8,0,0,0),Background=new SolidColorBrush(Color.FromRgb(255,138,0)),Foreground=Brushes.White};
        // 後台下載：對話框只是進度視窗，隨時可關；留在窗內等到完成則返回 true 續接原流程
        void EnterDownloadingUi(){download.Visibility=Visibility.Collapsed;cancel.Content=T("後台繼續");body.Text=ProgressText(LastProgress);}
        Action<(string Phase,int Percent)> onProgress=p=>body.Text=ProgressText(p);
        Action<Exception?>? onFinished=null;
        onFinished=ex=>
        {
            if(ex==null){dialog.DialogResult=true;dialog.Close();return;}
            body.Text=T("安裝失敗")+"："+ex.Message+Environment.NewLine+T("請檢查網路後重試。");
            download.Visibility=Visibility.Visible;download.IsEnabled=true;cancel.Content=T("取消");
        };
        ProgressChanged+=onProgress;InstallFinished+=onFinished;
        dialog.Closed+=(_,_)=>{ProgressChanged-=onProgress;InstallFinished-=onFinished;};
        // 關窗掛後台時提示用戶可在設定頁查看進度
        void CloseToBackground(){var backgrounded=Installing;dialog.DialogResult=false;dialog.Close();if(backgrounded)BeeXDialog.Notify(null,T("下載 ffmpeg 元件"),T("下載已轉入後台，可前往 設定 → 診斷與維護 查看進度。"),new AppState{Language=language});}
        cancel.Click+=(_,_)=>CloseToBackground();
        download.Click+=(_,_)=>{StartBackgroundInstall(language);EnterDownloadingUi();};
        if(Installing)EnterDownloadingUi();
        actions.Children.Add(cancel);actions.Children.Add(download);
        Grid.SetRow(actions,2);
        root.Children.Add(heading);root.Children.Add(body);root.Children.Add(actions);
        border.Child=root;dialog.Content=border;
        border.MouseLeftButtonDown+=(_,e)=>{if(e.LeftButton==System.Windows.Input.MouseButtonState.Pressed)try{dialog.DragMove();}catch{}};
        dialog.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape)CloseToBackground();};
        return dialog.ShowDialog()==true&&FfmpegService.IsAvailable;
    }
}
