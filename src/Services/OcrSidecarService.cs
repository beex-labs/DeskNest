using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using IoPath=System.IO.Path;

namespace BeeX.DeskNest;

/// <summary>
/// 管理 OCR 双侧车进程（ocr\BeeX_OCR.exe 文字、ocr\BeeX_Formula.exe 公式）。
/// 文字侧车用 MKL 运行时（oneDNN 加速），公式侧车用 openblas 运行时
/// （PP-FormulaNet 在 oneDNN 内核下必崩），两者共用 ocr\models 模型目录。
/// 侧车常驻以复用已加载的模型；行协议见 BeeX_OCR 的 OcrCli.RunServeLoop。
/// </summary>
static class OcrSidecarService
{
    sealed class Sidecar(string exeName,string serveRole)
    {
        public readonly object Gate=new();
        // AppData 安装目录优先（在线安装），兼容 exe 旁的 ocr\ 外挂目录（手动部署）
        public string ExePath
        {
            get
            {
                var installed=IoPath.Combine(OcrInstallerService.InstallRoot,exeName);
                if(File.Exists(installed))return installed;
                return IoPath.Combine(AppContext.BaseDirectory,"ocr",exeName);
            }
        }
        public string ServeRole=>serveRole;
        public Process? Process;
        public StreamWriter? Input;
        public StreamReader? Output;
        public bool Ready;
    }

    static readonly Sidecar OcrSidecar=new("BeeX_OCR.exe","ocr");
    static readonly Sidecar FormulaSidecar=new("BeeX_Formula.exe","formula");

    public static bool IsAvailable=>File.Exists(OcrSidecar.ExePath);

    public static Task<string> RecognizeTextAsync(string imagePath)=>RequestAsync(OcrSidecar,"OCR",imagePath);
    public static Task<string> RecognizeFormulaAsync(string imagePath)=>RequestAsync(FormulaSidecar,"FORMULA",imagePath);
    public static Task<string> RecognizeTableAsync(string imagePath)=>RequestAsync(OcrSidecar,"TABLE",imagePath);

    public static Task<List<OcrTextBlock>> RecognizeTextWithPositionsAsync(string imagePath)
    {
        return Task.Run(()=>
        {
            lock(OcrSidecar.Gate)
            {
                EnsureStarted(OcrSidecar);
                var resultPath=IoPath.Combine(IoPath.GetTempPath(),$"BeeX_OCRPOS_{Guid.NewGuid():N}.txt");
                try
                {
                    OcrSidecar.Input!.WriteLine("OCRPOS\t"+imagePath+"\t"+resultPath);
                    var response=OcrSidecar.Output!.ReadLine();
                    if(response==null){Stop(OcrSidecar);throw new InvalidOperationException("OCR 元件意外結束。");}
                    if(response.StartsWith("ERR\t",StringComparison.Ordinal))throw new InvalidOperationException(response[4..]);
                    if(!response.StartsWith("OK",StringComparison.Ordinal)){Stop(OcrSidecar);throw new InvalidOperationException("OCR 元件回應異常："+response);}
                    var json=File.ReadAllText(resultPath);
                    var blocks=new List<OcrTextBlock>();
                    using var doc=JsonDocument.Parse(json);
                    if(doc.RootElement.TryGetProperty("regions",out var regions))
                    {
                        foreach(var r in regions.EnumerateArray())
                        {
                            blocks.Add(new OcrTextBlock
                            {
                                X=r.GetProperty("x").GetDouble(),
                                Y=r.GetProperty("y").GetDouble(),
                                Width=r.GetProperty("w").GetDouble(),
                                Height=r.GetProperty("h").GetDouble(),
                                Text=r.GetProperty("text").GetString()??""
                            });
                        }
                    }
                    return blocks;
                }
                finally
                {
                    try{if(File.Exists(resultPath))File.Delete(resultPath);}catch{}
                }
            }
        });
    }

    static Task<string> RequestAsync(Sidecar sidecar,string command,string imagePath)
    {
        return Task.Run(()=>
        {
            lock(sidecar.Gate)
            {
                EnsureStarted(sidecar);
                var resultPath=IoPath.Combine(IoPath.GetTempPath(),$"BeeX_OCR_result_{Guid.NewGuid():N}.txt");
                try
                {
                    sidecar.Input!.WriteLine(command+"\t"+imagePath+"\t"+resultPath);
                    var response=sidecar.Output!.ReadLine();
                    if(response==null){Stop(sidecar);throw new InvalidOperationException("OCR 元件意外結束。");}
                    if(response.StartsWith("ERR\t",StringComparison.Ordinal))throw new InvalidOperationException(response[4..]);
                    if(!response.StartsWith("OK",StringComparison.Ordinal)){Stop(sidecar);throw new InvalidOperationException("OCR 元件回應異常："+response);}
                    return File.ReadAllText(resultPath);
                }
                finally
                {
                    try{if(File.Exists(resultPath))File.Delete(resultPath);}catch{}
                }
            }
        });
    }

    /// <summary>后台预热文字侧车（截图框选期间加载模型）；公式侧车按需启动。</summary>
    public static void WarmUp()
    {
        if(!File.Exists(OcrSidecar.ExePath))return;
        Task.Run(()=>{try{lock(OcrSidecar.Gate)EnsureStarted(OcrSidecar);}catch{}});
    }

    static void EnsureStarted(Sidecar sidecar)
    {
        if(sidecar.Process!=null&&!sidecar.Process.HasExited&&sidecar.Ready)return;
        Stop(sidecar);
        if(!File.Exists(sidecar.ExePath))throw new InvalidOperationException($"找不到 OCR 元件（{IoPath.GetFileName(sidecar.ExePath)}），請先安裝 OCR 辨識。");

        var info=new ProcessStartInfo
        {
            FileName=sidecar.ExePath,
            Arguments="--serve --serve-role "+sidecar.ServeRole,
            UseShellExecute=false,
            CreateNoWindow=true,
            RedirectStandardInput=true,
            RedirectStandardOutput=true,
            StandardInputEncoding=new UTF8Encoding(false),
            StandardOutputEncoding=new UTF8Encoding(false),
            WorkingDirectory=IoPath.GetDirectoryName(sidecar.ExePath)!
        };
        var process=Process.Start(info)??throw new InvalidOperationException("OCR 元件啟動失敗。");
        // 等待 READY（首次加载模型约数秒）
        var line=process.StandardOutput.ReadLine();
        if(line==null||!line.Contains("READY",StringComparison.Ordinal))
        {
            try{process.Kill(entireProcessTree:true);}catch{}
            throw new InvalidOperationException("OCR 元件初始化失敗。");
        }
        sidecar.Process=process;
        sidecar.Input=process.StandardInput;
        sidecar.Output=process.StandardOutput;
        sidecar.Ready=true;
    }

    static void Stop(Sidecar sidecar)
    {
        sidecar.Ready=false;
        try{sidecar.Input?.WriteLine("EXIT");}catch{}
        try{sidecar.Input?.Dispose();}catch{}
        try{sidecar.Output?.Dispose();}catch{}
        try
        {
            if(sidecar.Process!=null&&!sidecar.Process.HasExited&&!sidecar.Process.WaitForExit(1500))sidecar.Process.Kill(entireProcessTree:true);
        }
        catch{}
        try{sidecar.Process?.Dispose();}catch{}
        sidecar.Process=null;sidecar.Input=null;sidecar.Output=null;
    }

    public static void Shutdown()
    {
        lock(OcrSidecar.Gate)Stop(OcrSidecar);
        lock(FormulaSidecar.Gate)Stop(FormulaSidecar);
    }
}
