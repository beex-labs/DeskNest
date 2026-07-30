using System.Windows;
using System.Threading;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace BeeX.DeskNest;
public partial class App : System.Windows.Application
{
    const string AppUserModelId = "BeeX.DeskNest.App";
    const int WM_SETICON = 0x0080;
    static readonly List<MemoryStream> iconStreams = [];
    static readonly object iconLock = new();
    static BitmapImage? windowIcon;
    static Drawing.Icon? nativeBigIcon;
    static Drawing.Icon? nativeSmallIcon;
    private DeskNestService? service;
    private Mutex? instanceMutex;
    private EventWaitHandle? shutdownEvent;
    private RegisteredWaitHandle? shutdownRegistration;
    private bool ownsInstance;
    protected override void OnStartup(StartupEventArgs e)
    {
        TrySetAppUserModelId();
        base.OnStartup(e);
        // 后台回收上次安全擦除异常中断（进程被杀/断电）残留的盘根填充目录，恢复被占满的磁盘空间
        _ = Task.Run(BeeXCleaner.Services.FreeSpaceWiper.CleanupLeftoverFillDirs);
        EventManager.RegisterClassHandler(typeof(Window), Window.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window) ApplyBeeXWindowIcon(window);
        }));
        // 以「系统清理」独立模式启动：仅显示 BeeX 清理窗口（通常由主程序以管理员身份重新拉起），
        // 不创建单实例互斥、不启动桌面挂件服务，窗口关闭即退出该进程。
        if(e.Args.Any(x=>string.Equals(x,"--cleaner",StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode=ShutdownMode.OnMainWindowClose;
            var cleaner=new BeeXCleaner.CleanerWindow();
            MainWindow=cleaner;
            cleaner.Show();
            return;
        }
        const string mutexName=@"Local\BeeX.DeskNest.SingleInstance";
        const string eventName=@"Local\BeeX.DeskNest.Shutdown";
        instanceMutex=new Mutex(true,mutexName,out var firstInstance);ownsInstance=firstInstance;
        shutdownEvent=new EventWaitHandle(false,EventResetMode.AutoReset,eventName);
        var olderInstances=FindOtherInstances();
        if(!firstInstance||olderInstances.Count>0)
        {
            var state=LoadSavedAppearance();
            var closeExisting=BeeXDialog.Confirm(null,"BeeX DeskNest","BeeX DeskNest 已在運行。\n\n是否關閉原有實例並重新開啟？",state,"重新開啟",false);
            if(!closeExisting){Shutdown();return;}
            if(!firstInstance)
            {
                shutdownEvent.Set();
                try{ownsInstance=instanceMutex.WaitOne(TimeSpan.FromSeconds(5));}catch(AbandonedMutexException){ownsInstance=true;}
            }
            if(olderInstances.Count>0)CloseOlderInstances(olderInstances);
            if(!ownsInstance)try{ownsInstance=instanceMutex.WaitOne(TimeSpan.FromSeconds(4));}catch(AbandonedMutexException){ownsInstance=true;}
            if(!ownsInstance){BeeXDialog.Alert(null,"BeeX DeskNest","原有實例未能正常關閉，請稍後再試。",state);Shutdown();return;}
        }
        var screenshotRegression=e.Args.Any(x=>string.Equals(x,"--screenshot-regression",StringComparison.OrdinalIgnoreCase));
        // 升級後首次啟動：把舊版散落在 AppData/圖片庫/文檔庫的資料一次性遷入統一 BeeX 根目錄（OCR 約 600MB 跨卷時較慢，帶進度窗）
        RunLegacyMigrationIfNeeded();
        service = new DeskNestService();
        service.Start(!screenshotRegression);
        shutdownRegistration=ThreadPool.RegisterWaitForSingleObject(shutdownEvent,(_,_)=>Dispatcher.BeginInvoke(()=>service?.Exit()),null,Timeout.Infinite,true);
    }
    static List<Process> FindOtherInstances(){var current=Process.GetCurrentProcess();try{return Process.GetProcessesByName(current.ProcessName).Where(p=>p.Id!=current.Id).ToList();}catch{return [];}}
    static AppState LoadSavedAppearance(){try{var path=BeeXPaths.StateFile;return File.Exists(path)?JsonSerializer.Deserialize<AppState>(File.ReadAllText(path))??new():new();}catch{return new();}}
    /// <summary>舊資料一次性遷移：小進度窗 + 後台線程搬移，完成前不可關閉（避免半遷移狀態）。</summary>
    static void RunLegacyMigrationIfNeeded()
    {
        if(!BeeXPaths.NeedsLegacyMigration){BeeXPaths.EnsureLayout();return;}
        var text=new System.Windows.Controls.TextBlock{Text="正在整理 BeeX 資料…",FontSize=14,Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13,19,33)),TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,10,0,0)};
        var heading=new System.Windows.Controls.TextBlock{Text="BeeX DeskNest",FontSize=18,FontWeight=FontWeights.SemiBold,Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13,19,33))};
        var bar=new System.Windows.Controls.ProgressBar{Height=6,IsIndeterminate=true,Margin=new Thickness(0,12,0,0),Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,138,0))};
        var stack=new System.Windows.Controls.StackPanel();stack.Children.Add(heading);stack.Children.Add(text);stack.Children.Add(bar);
        var border=new System.Windows.Controls.Border{CornerRadius=new CornerRadius(14),Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250,251,252)),BorderBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(115,255,138,0)),BorderThickness=new Thickness(1),Padding=new Thickness(24),Child=stack};
        var win=new Window{Width=420,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterScreen,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=System.Windows.Media.Brushes.Transparent,ShowInTaskbar=false,Topmost=true,Content=border};
        var done=false;
        win.Closing+=(_,e)=>{if(!done)e.Cancel=true;};
        win.ContentRendered+=async(_,_)=>
        {
            try{await Task.Run(()=>BeeXPaths.MigrateLegacyIfNeeded(name=>win.Dispatcher.BeginInvoke(()=>text.Text="正在整理 BeeX 資料… "+name)));}
            catch{}
            done=true;win.Close();
        };
        win.ShowDialog();
    }
    static void CloseOlderInstances(IEnumerable<Process> processes){foreach(var process in processes){try{if(process.HasExited)continue;process.CloseMainWindow();if(!process.WaitForExit(1500)){process.Kill(true);process.WaitForExit(3000);}}catch{}finally{process.Dispose();}}}
    static void TrySetAppUserModelId(){try{SetCurrentProcessExplicitAppUserModelID(AppUserModelId);}catch{}}
    static void ApplyBeeXWindowIcon(Window window)
    {
        try
        {
            EnsureBeeXIcons();
            if(window.Icon==null&&windowIcon!=null)window.Icon=windowIcon;
            var hwnd=new WindowInteropHelper(window).Handle;
            if(hwnd==IntPtr.Zero)return;
            if(nativeBigIcon!=null)SendMessage(hwnd,WM_SETICON,(IntPtr)1,nativeBigIcon.Handle);
            if(nativeSmallIcon!=null){SendMessage(hwnd,WM_SETICON,IntPtr.Zero,nativeSmallIcon.Handle);SendMessage(hwnd,WM_SETICON,(IntPtr)2,nativeSmallIcon.Handle);}
        }
        catch{}
    }
    public static Drawing.Icon CreateTrayIcon()
    {
        try
        {
            EnsureBeeXIcons();
            if(nativeSmallIcon!=null)return (Drawing.Icon)nativeSmallIcon.Clone();
            var associated=Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
            if(associated!=null)return associated;
        }
        catch{}
        return Drawing.SystemIcons.Application;
    }
    static void EnsureBeeXIcons()
    {
        lock(iconLock)
        {
            if(windowIcon==null)
            {
                windowIcon=new BitmapImage();
                windowIcon.BeginInit();
                windowIcon.CacheOption=BitmapCacheOption.OnLoad;
                windowIcon.UriSource=new Uri("pack://application:,,,/Assets/BeeX.ico",UriKind.Absolute);
                windowIcon.EndInit();
                windowIcon.Freeze();
            }
            nativeBigIcon??=LoadNativeIcon(256)??LoadNativeIcon(64)??LoadNativeIcon(32);
            nativeSmallIcon??=LoadNativeIcon(32)??LoadNativeIcon(16);
        }
    }
    static Drawing.Icon? LoadNativeIcon(int size)
    {
        try
        {
            var info=GetResourceStream(new Uri("pack://application:,,,/Assets/BeeX.ico",UriKind.Absolute));
            if(info?.Stream==null)return null;
            var memory=new MemoryStream();
            info.Stream.CopyTo(memory);
            memory.Position=0;
            var icon=new Drawing.Icon(memory,size,size);
            iconStreams.Add(memory);
            return icon;
        }
        catch{return null;}
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern int SetCurrentProcessExplicitAppUserModelID(string AppID);
    [DllImport("user32.dll",SetLastError=true)]
    static extern IntPtr SendMessage(IntPtr hWnd,int msg,IntPtr wParam,IntPtr lParam);
    protected override void OnExit(ExitEventArgs e) { OcrSidecarService.Shutdown();shutdownRegistration?.Unregister(null);service?.Dispose();shutdownEvent?.Dispose();if(ownsInstance)try{instanceMutex?.ReleaseMutex();}catch(ApplicationException){}instanceMutex?.Dispose();base.OnExit(e); }
}
