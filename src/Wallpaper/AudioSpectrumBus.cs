using NAudio.Dsp;
using NAudio.Wave;

namespace BeeX.DeskNest;

/// <summary>
/// Single system-wide audio spectrum source for every audio-reactive wallpaper: one WASAPI loopback capture feeds a
/// Hann-windowed FFT whose magnitudes are folded into 64 log bands plus a beat flag and an overall level. Events fire
/// on the capture thread; the wallpaper service marshals them to the UI. The capture self-restarts when the device
/// stream dies (default output change, driver reset), following the same defensive style as <see cref="AudioCapture"/>.
/// </summary>
sealed class AudioSpectrumBus : IDisposable
{
    public const int BandCount = 64;
    const int FftSize = 2048;
    const int FftOrder = 11; // 2^11 = 2048
    const int MinIntervalMs = 16;

    WasapiLoopbackCapture? capture;
    int[] edges = [];
    readonly float[] window = new float[FftSize];
    readonly float[] hann = new float[FftSize];
    readonly Complex[] fft = new Complex[FftSize];
    readonly float[] magnitudes = new float[FftSize / 2];
    readonly float[] rawBands = new float[BandCount];
    readonly float[] bands = new float[BandCount];
    readonly BeatDetector beat = new();
    readonly object gate = new();
    float[] pending = [];
    int pendingCount;
    long lastEmitTicks;
    volatile bool running;

    /// <summary>Bands (64 values 0-1), beat onset flag, overall level (0-1). Raised on the capture thread.</summary>
    public event Action<float[], bool, float>? SpectrumReady;

    /// <summary>Skips analysis/dispatch while every wallpaper surface is paused, keeping the idle cost near zero.</summary>
    public volatile bool Muted;

    public bool IsRunning => running;

    public void Start()
    {
        if (running) return;
        running = true;
        for (var i = 0; i < FftSize; i++) hann[i] = (float)FastFourierTransform.HannWindow(i, FftSize);
        StartCapture();
    }

    public void Stop()
    {
        running = false;
        var c = capture;
        capture = null;
        if (c != null)
        {
            try { c.StopRecording(); } catch { }
            try { c.Dispose(); } catch { }
        }
    }

    void StartCapture()
    {
        if (!running) return;
        try
        {
            var c = new WasapiLoopbackCapture();
            edges = AudioSpectrumMapper.BuildBandEdges(FftSize, c.WaveFormat.SampleRate, BandCount);
            pending = new float[FftSize * 4];
            pendingCount = 0;
            c.DataAvailable += OnData;
            // Device unplugged / default output switched: tear down and retry against the new default device.
            c.RecordingStopped += (_, _) =>
            {
                if (!running || !ReferenceEquals(capture, c)) return;
                try { c.Dispose(); } catch { }
                capture = null;
                Task.Delay(1000).ContinueWith(_ => { if (running && capture == null) StartCapture(); });
            };
            capture = c;
            c.StartRecording();
        }
        catch
        {
            // No output device right now — retry later instead of failing the whole engine.
            Task.Delay(5000).ContinueWith(_ => { if (running && capture == null) StartCapture(); });
        }
    }

    void OnData(object? sender, WaveInEventArgs e)
    {
        if (!running || Muted) return;
        var format = (sender as WasapiLoopbackCapture)?.WaveFormat;
        if (format == null || format.BitsPerSample != 32) return;
        var channels = Math.Max(format.Channels, 1);
        lock (gate)
        {
            // Fold interleaved float frames to mono and append to the sliding buffer.
            var frames = e.BytesRecorded / 4 / channels;
            for (var f = 0; f < frames; f++)
            {
                float sum = 0;
                for (var ch = 0; ch < channels; ch++) sum += BitConverter.ToSingle(e.Buffer, (f * channels + ch) * 4);
                if (pendingCount == pending.Length)
                {
                    Array.Copy(pending, pending.Length - FftSize, pending, 0, FftSize);
                    pendingCount = FftSize;
                }
                pending[pendingCount++] = sum / channels;
            }
            if (pendingCount < FftSize) return;
            var now = Environment.TickCount64;
            if (now - lastEmitTicks < MinIntervalMs) return;
            lastEmitTicks = now;
            Array.Copy(pending, pendingCount - FftSize, window, 0, FftSize);
        }
        Analyze();
    }

    void Analyze()
    {
        float sq = 0;
        for (var i = 0; i < FftSize; i++)
        {
            sq += window[i] * window[i];
            fft[i].X = window[i] * hann[i];
            fft[i].Y = 0;
        }
        FastFourierTransform.FFT(true, FftOrder, fft);
        for (var i = 0; i < magnitudes.Length; i++)
            magnitudes[i] = MathF.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y);

        AudioSpectrumMapper.MapBands(magnitudes, edges, rawBands);
        AudioSpectrumMapper.Smooth(rawBands, bands);

        float low = 0;
        for (var i = 0; i < 8; i++) low += bands[i];
        var isBeat = beat.Update(low / 8);
        var level = Math.Clamp(MathF.Sqrt(sq / FftSize) * 4f, 0f, 1f);
        SpectrumReady?.Invoke(bands, isBeat, level);
    }

    public void Dispose() => Stop();
}
