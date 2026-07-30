using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BeeX.DeskNest;

/// <summary>
/// 用 WASAPI 同時採集「系統聲音（環回）」與「麥克風」，各自寫入獨立 WAV，
/// 之後交由 ffmpeg 與影片混合。任一路不可用時自動跳過，不影響另一路與影片。
/// </summary>
public sealed class AudioCapture : IDisposable
{
    WasapiLoopbackCapture? sys;
    WasapiCapture? mic;
    WaveFileWriter? sysWriter, micWriter;

    public string? SystemWavPath { get; private set; }
    public string? MicWavPath { get; private set; }

    /// <summary>暫停期間丟棄音頻寫入，使音頻時長與（同樣暫停的）影片一致。</summary>
    public volatile bool Paused;

    public void Start(string dir)
    {
        try
        {
            var path=Path.Combine(dir,"sys.wav");
            sys=new WasapiLoopbackCapture();
            sysWriter=new WaveFileWriter(path,sys.WaveFormat);
            sys.DataAvailable+=(_,e)=>{ try{ if(!Paused) sysWriter?.Write(e.Buffer,0,e.BytesRecorded); }catch{} };
            sys.StartRecording();
            SystemWavPath=path;
        }
        catch { SystemWavPath=null; try{sys?.Dispose();}catch{} sys=null; try{sysWriter?.Dispose();}catch{} sysWriter=null; }

        try
        {
            var path=Path.Combine(dir,"mic.wav");
            mic=new WasapiCapture();
            micWriter=new WaveFileWriter(path,mic.WaveFormat);
            mic.DataAvailable+=(_,e)=>{ try{ if(!Paused) micWriter?.Write(e.Buffer,0,e.BytesRecorded); }catch{} };
            mic.StartRecording();
            MicWavPath=path;
        }
        catch { MicWavPath=null; try{mic?.Dispose();}catch{} mic=null; try{micWriter?.Dispose();}catch{} micWriter=null; }
    }

    public void Stop()
    {
        try{ sys?.StopRecording(); }catch{}
        try{ mic?.StopRecording(); }catch{}
        System.Threading.Thread.Sleep(200);
        try{ sysWriter?.Dispose(); }catch{} sysWriter=null;
        try{ micWriter?.Dispose(); }catch{} micWriter=null;
    }

    public bool HasAnyAudio => !string.IsNullOrEmpty(SystemWavPath)||!string.IsNullOrEmpty(MicWavPath);

    public void Dispose()
    {
        try{ sys?.Dispose(); }catch{} sys=null;
        try{ mic?.Dispose(); }catch{} mic=null;
        try{ sysWriter?.Dispose(); }catch{} sysWriter=null;
        try{ micWriter?.Dispose(); }catch{} micWriter=null;
    }
}
