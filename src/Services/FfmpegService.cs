using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace BeeX.DeskNest;

/// <summary>
/// ffmpeg 解析與調用。優先順序：在線安裝目錄（FfmpegInstallerService）→ 舊版本地緩存 → 嵌入資源解壓（舊版兼容）→ 程序目錄旁 → 系統 PATH。
/// ffmpeg.exe 不再內置，缺失時由 FfmpegInstallerService 引導用戶按需下載。
/// </summary>
public static class FfmpegService
{
    static string? cachedPath;
    static readonly object gate=new();

    public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BeeX DeskNest","ffmpeg");

    /// <summary>安裝/刪除 ffmpeg 後重置路徑緩存，下次調用重新探測。</summary>
    public static void Invalidate(){lock(gate)cachedPath=null;}

    /// <summary>返回可用的 ffmpeg.exe 路徑；找不到返回 null。</summary>
    public static string? EnsurePath()
    {
        lock(gate)
        {
            if(cachedPath!=null&&File.Exists(cachedPath))return cachedPath;
            var installed=FfmpegInstallerService.ExePath;
            if(File.Exists(installed)&&new FileInfo(installed).Length>1_000_000){cachedPath=installed;return cachedPath;}
            var cache=Path.Combine(CacheDir,"ffmpeg.exe");
            if(File.Exists(cache)&&new FileInfo(cache).Length>1_000_000){cachedPath=cache;return cachedPath;}
            try
            {
                var asm=Assembly.GetExecutingAssembly();
                var resName=asm.GetManifestResourceNames().FirstOrDefault(n=>n.EndsWith("ffmpeg.exe",StringComparison.OrdinalIgnoreCase));
                if(resName!=null)
                {
                    using var stream=asm.GetManifestResourceStream(resName);
                    if(stream!=null&&stream.Length>1_000_000)
                    {
                        Directory.CreateDirectory(CacheDir);
                        var tmp=cache+".tmp";
                        using(var fs=File.Create(tmp))stream.CopyTo(fs);
                        if(File.Exists(cache))File.Delete(cache);
                        File.Move(tmp,cache);
                        cachedPath=cache;return cachedPath;
                    }
                }
            }
            catch{}
            try
            {
                var dir=Path.GetDirectoryName(Environment.ProcessPath)??AppContext.BaseDirectory;
                foreach(var p in new[]{Path.Combine(dir,"ffmpeg.exe"),Path.Combine(dir,"Assets","ffmpeg","ffmpeg.exe")})
                    if(File.Exists(p)){cachedPath=p;return cachedPath;}
            }
            catch{}
            try
            {
                foreach(var dir in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator))
                {
                    if(string.IsNullOrWhiteSpace(dir))continue;
                    var p=Path.Combine(dir.Trim(),"ffmpeg.exe");
                    if(File.Exists(p)){cachedPath=p;return cachedPath;}
                }
            }
            catch{}
            return null;
        }
    }

    public static bool IsAvailable => EnsurePath()!=null;

    /// <summary>啟動一個持續運行的 ffmpeg 進程（如錄製）。redirectStdin=true 時可寫入 'q' 優雅停止。</summary>
    public static Process? Start(string args,bool redirectStdin=false)
    {
        var exe=EnsurePath();
        if(exe==null)return null;
        var psi=new ProcessStartInfo
        {
            FileName=exe,
            Arguments=args,
            UseShellExecute=false,
            CreateNoWindow=true,
            RedirectStandardInput=redirectStdin,
            RedirectStandardError=true,
            RedirectStandardOutput=true
        };
        var proc=new Process{StartInfo=psi,EnableRaisingEvents=true};
        proc.OutputDataReceived+=(_,_)=>{};
        proc.ErrorDataReceived+=(_,_)=>{};
        if(!proc.Start())return null;
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        return proc;
    }

    /// <summary>啟動一個以 stdin 讀取 rawvideo(bgra) 幀、編碼為 MP4 的 ffmpeg 進程。用自抓幀管線餵幀。</summary>
    public static Process? StartRawEncoder(int w,int h,int fps,string outPath)
    {
        var args=$"-y -f rawvideo -pixel_format bgra -video_size {w}x{h} -framerate {fps} -i pipe:0 -an -c:v libx264 -preset veryfast -pix_fmt yuv420p -movflags +faststart \"{outPath}\"";
        return Start(args,redirectStdin:true);
    }

    /// <summary>同步運行 ffmpeg 到結束，返回退出碼（找不到 ffmpeg 返回 -1）。</summary>
    public static int RunToEnd(string args)
    {
        var proc=Start(args,false);
        if(proc==null)return -1;
        proc.WaitForExit();
        var code=proc.ExitCode;
        proc.Dispose();
        return code;
    }

    /// <summary>向錄製進程發送 'q' 以優雅結束並完成檔案封裝。</summary>
    public static void GracefulStop(Process? proc)
    {
        if(proc==null||proc.HasExited)return;
        try{proc.StandardInput.Write("q");proc.StandardInput.Flush();}catch{}
        try{if(!proc.WaitForExit(4000)){proc.Kill();}}catch{}
    }

    /// <summary>用 ffmpeg -i 讀取媒體資訊（時長/是否有音軌/畫面尺寸）。找不到返回默認。</summary>
    public static (double duration,bool hasAudio,int w,int h) Probe(string file)
    {
        var exe=EnsurePath();
        double dur=0;bool aud=false;int w=0,h=0;
        if(exe==null||!File.Exists(file))return (0,false,0,0);
        try
        {
            var psi=new ProcessStartInfo{FileName=exe,Arguments=$"-i \"{file}\"",UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true};
            using var proc=Process.Start(psi);
            if(proc==null)return (0,false,0,0);
            var err=proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            var md=System.Text.RegularExpressions.Regex.Match(err,@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
            if(md.Success)dur=int.Parse(md.Groups[1].Value)*3600+int.Parse(md.Groups[2].Value)*60+double.Parse(md.Groups[3].Value,System.Globalization.CultureInfo.InvariantCulture);
            aud=System.Text.RegularExpressions.Regex.IsMatch(err,@"Stream #\d+:\d+.*: Audio:");
            var mv=System.Text.RegularExpressions.Regex.Match(err,@"Video:.*?,\s*(\d{2,5})x(\d{2,5})");
            if(mv.Success){w=int.Parse(mv.Groups[1].Value);h=int.Parse(mv.Groups[2].Value);}
        }
        catch{}
        return (dur,aud,w,h);
    }

    /// <summary>一次 ffmpeg 調用在 [inSec,inSec+dur] 內均勻抽取 count 張縮圖（高 height），返回已生成的檔案路徑。</summary>
    public static List<string> ExtractThumbs(string src,double inSec,double dur,int count,int height,string outDir,string prefix)
    {
        var res=new List<string>();
        if(EnsurePath()==null||dur<=0.01||count<1)return res;
        var inv=System.Globalization.CultureInfo.InvariantCulture;
        try
        {
            Directory.CreateDirectory(outDir);
            double fps=Math.Max(0.05,count/dur);
            string pat=Path.Combine(outDir,prefix+"_%03d.png");
            RunToEnd($"-y -ss {inSec.ToString("0.###",inv)} -t {dur.ToString("0.###",inv)} -i \"{src}\" -vf \"fps={fps.ToString("0.####",inv)},scale=-1:{height}\" -frames:v {count} \"{pat}\"");
            for(int i=1;i<=count;i++){var p=Path.Combine(outDir,$"{prefix}_{i:000}.png");if(File.Exists(p))res.Add(p);}
        }
        catch{}
        return res;
    }

}
