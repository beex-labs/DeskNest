using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace BeeX.DeskNest;

/// <summary>
/// Uses WASAPI to capture the system sound (loopback) and the microphone at the same time, writing each to a separate WAV,
/// which ffmpeg later mixes with the video. If either stream is unavailable it is skipped automatically without affecting the other stream or the video.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    WasapiLoopbackCapture? sys;
    WasapiCapture? mic;
    WaveFileWriter? sysWriter, micWriter;

    public string? SystemWavPath { get; private set; }
    public string? MicWavPath { get; private set; }

    /// <summary>Discards audio writes while paused so the audio duration matches the (equally paused) video.</summary>
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
