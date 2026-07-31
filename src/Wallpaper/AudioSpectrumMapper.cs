namespace BeeX.DeskNest;

/// <summary>
/// Pure math for turning FFT magnitudes into the compact spectrum wallpapers consume: logarithmically spaced
/// frequency bands with attack/decay smoothing, plus an energy-history beat detector. No audio or UI dependencies,
/// fully unit-testable; the live capture side lives in <see cref="AudioSpectrumBus"/>.
/// </summary>
static class AudioSpectrumMapper
{
    /// <summary>
    /// Bin index boundaries for <paramref name="bandCount"/> log-spaced bands between <paramref name="minHz"/> and
    /// <paramref name="maxHz"/>. Returns bandCount+1 ascending edges; every band spans at least one bin.
    /// </summary>
    public static int[] BuildBandEdges(int fftSize, int sampleRate, int bandCount, double minHz = 40, double maxHz = 16000)
    {
        var edges = new int[bandCount + 1];
        var maxBin = fftSize / 2;
        maxHz = Math.Min(maxHz, sampleRate / 2.0);
        for (var i = 0; i <= bandCount; i++)
        {
            var hz = minHz * Math.Pow(maxHz / minHz, (double)i / bandCount);
            var bin = (int)Math.Round(hz * fftSize / sampleRate);
            edges[i] = Math.Clamp(bin, 0, maxBin);
        }
        // Force strict monotonicity so low bands never collapse to zero width.
        for (var i = 1; i <= bandCount; i++)
            if (edges[i] <= edges[i - 1]) edges[i] = Math.Min(edges[i - 1] + 1, maxBin);
        for (var i = bandCount - 1; i >= 0; i--)
            if (edges[i] >= edges[i + 1]) edges[i] = Math.Max(edges[i + 1] - 1, 0);
        return edges;
    }

    /// <summary>Averages magnitudes into bands and compresses to 0-1 (sqrt curve, fixed gain so output is deterministic).</summary>
    public static void MapBands(float[] magnitudes, int[] edges, float[] target, float gain = 2.5f)
    {
        for (var band = 0; band < target.Length; band++)
        {
            var from = edges[band];
            var to = Math.Max(edges[band + 1], from + 1);
            float sum = 0;
            for (var bin = from; bin < to && bin < magnitudes.Length; bin++) sum += magnitudes[bin];
            var avg = sum / (to - from);
            target[band] = Math.Clamp((float)Math.Sqrt(avg) * gain, 0f, 1f);
        }
    }

    /// <summary>Attack/decay envelope: rises quickly toward louder values, falls slowly, which reads better visually.</summary>
    public static void Smooth(float[] current, float[] smoothed, float attack = 0.6f, float decay = 0.15f)
    {
        for (var i = 0; i < smoothed.Length; i++)
        {
            var k = current[i] > smoothed[i] ? attack : decay;
            smoothed[i] += (current[i] - smoothed[i]) * k;
        }
    }
}

/// <summary>
/// Energy-history beat detector: reports a beat when the low-frequency energy spikes above its recent average.
/// A refractory window keeps one physical beat from firing multiple times.
/// </summary>
sealed class BeatDetector
{
    readonly float[] history;
    readonly float threshold;
    readonly float floor;
    readonly int refractory;
    int filled, cursor, cooldown;

    public BeatDetector(int historyLength = 32, float threshold = 1.4f, float floor = 0.05f, int refractoryUpdates = 8)
    {
        history = new float[Math.Max(historyLength, 2)];
        this.threshold = threshold;
        this.floor = floor;
        refractory = refractoryUpdates;
    }

    /// <summary>Feeds one low-band energy sample (0-1); true when this update is a beat onset.</summary>
    public bool Update(float energy)
    {
        float avg = 0;
        if (filled > 0)
        {
            for (var i = 0; i < filled; i++) avg += history[i];
            avg /= filled;
        }
        history[cursor] = energy;
        cursor = (cursor + 1) % history.Length;
        filled = Math.Min(filled + 1, history.Length);

        if (cooldown > 0) { cooldown--; return false; }
        var beat = filled >= history.Length / 2 && energy > floor && energy > avg * threshold;
        if (beat) cooldown = refractory;
        return beat;
    }
}
