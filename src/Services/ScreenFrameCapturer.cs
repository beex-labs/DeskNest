using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using Drawing=System.Drawing;
using Imaging=System.Drawing.Imaging;

namespace BeeX.DeskNest;

/// <summary>
/// Self-grabbed frame recording: a background thread grabs the current region via GDI at the target fps (BitBlt excludes WDA_EXCLUDEFROMCAPTURE windows)
/// and writes the raw BGRA frames into ffmpeg's stdin pipe for encoding. It paces by "effective time": while paused it writes no frames and does not advance the beat,
/// so pauses are collapsed and stay in sync with the audio duration. The region coordinates can change during recording (used by the movable selection feature).
/// </summary>
public sealed class ScreenFrameCapturer : IDisposable
{
    readonly int w,h,fps;
    public volatile int RegionX;
    public volatile int RegionY;
    public volatile bool Paused;
    public Action<Drawing.Bitmap>? ProcessFrame;   // Per-frame post-processing after grab: mosaic pixelation + annotation overlay
    volatile bool running;
    Thread? thread;
    Stream? outStream;

    public ScreenFrameCapturer(int x,int y,int w,int h,int fps)
    {
        RegionX=x;RegionY=y;this.w=Math.Max(2,w);this.h=Math.Max(2,h);this.fps=Math.Clamp(fps,1,120);
    }

    public void Start(Stream ffmpegStdin)
    {
        outStream=ffmpegStdin;running=true;
        thread=new Thread(Loop){IsBackground=true,Priority=ThreadPriority.AboveNormal};
        thread.Start();
    }

    void Loop()
    {
        int frameBytes=w*4*h;
        var buffer=new byte[frameBytes];
        using var bmp=new Drawing.Bitmap(w,h,Imaging.PixelFormat.Format32bppArgb);
        using var g=Drawing.Graphics.FromImage(bmp);
        var sw=Stopwatch.StartNew();
        double pausedMs=0,interval=1000.0/fps;long? pauseStart=null,frameIndex=0;
        while(running)
        {
            if(Paused)
            {
                pauseStart??=sw.ElapsedMilliseconds;
                Thread.Sleep(10);
                continue;
            }
            if(pauseStart!=null){pausedMs+=sw.ElapsedMilliseconds-pauseStart.Value;pauseStart=null;}
            double activeNow=sw.Elapsed.TotalMilliseconds-pausedMs;
            double target=frameIndex.Value*interval;
            if(activeNow<target){Thread.Sleep(Math.Max(1,(int)(target-activeNow)));continue;}
            try{g.CopyFromScreen(RegionX,RegionY,0,0,new Drawing.Size(w,h),Drawing.CopyPixelOperation.SourceCopy);}catch{}
            try{ProcessFrame?.Invoke(bmp);}catch{}
            try
            {
                var data=bmp.LockBits(new Drawing.Rectangle(0,0,w,h),Imaging.ImageLockMode.ReadOnly,Imaging.PixelFormat.Format32bppArgb);
                Marshal.Copy(data.Scan0,buffer,0,frameBytes);
                bmp.UnlockBits(data);
                outStream!.Write(buffer,0,frameBytes);
            }
            catch{break;}
            frameIndex++;
        }
        try{outStream?.Flush();}catch{}
    }

    public void Stop()
    {
        running=false;
        try{thread?.Join(2000);}catch{}
    }

    public void Dispose()=>Stop();
}
