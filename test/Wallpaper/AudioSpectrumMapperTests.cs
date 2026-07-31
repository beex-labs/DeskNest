using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Wallpaper;

public class AudioSpectrumMapperTests
{
    const int FftSize = 2048;
    const int SampleRate = 48000;
    const int Bands = 64;

    // ---- band edge construction ----

    [Fact]
    public void BuildBandEdges_ReturnsMonotonicEdges_CoveringEveryBand()
    {
        var edges = AudioSpectrumMapper.BuildBandEdges(FftSize, SampleRate, Bands);
        edges.Should().HaveCount(Bands + 1);
        for (var i = 1; i <= Bands; i++)
            edges[i].Should().BeGreaterThan(edges[i - 1], $"band {i - 1} must span at least one bin");
        edges[^1].Should().BeLessThanOrEqualTo(FftSize / 2);
    }

    [Fact]
    public void BuildBandEdges_LowSampleRate_StillMonotonic()
    {
        var edges = AudioSpectrumMapper.BuildBandEdges(FftSize, 8000, Bands);
        for (var i = 1; i <= Bands; i++)
            edges[i].Should().BeGreaterThan(edges[i - 1]);
    }

    // ---- magnitude -> band mapping ----

    [Fact]
    public void MapBands_SilentInput_AllZero()
    {
        var edges = AudioSpectrumMapper.BuildBandEdges(FftSize, SampleRate, Bands);
        var target = new float[Bands];
        AudioSpectrumMapper.MapBands(new float[FftSize / 2], edges, target);
        target.Should().OnlyContain(v => v == 0f);
    }

    [Fact]
    public void MapBands_SingleBinSpike_LandsInExactlyOneBand()
    {
        var edges = AudioSpectrumMapper.BuildBandEdges(FftSize, SampleRate, Bands);
        var magnitudes = new float[FftSize / 2];
        // Put energy into the middle of band 10 only.
        var bin = (edges[10] + edges[11]) / 2;
        magnitudes[bin] = 0.5f;
        var target = new float[Bands];
        AudioSpectrumMapper.MapBands(magnitudes, edges, target);
        target[10].Should().BeGreaterThan(0f);
        for (var i = 0; i < Bands; i++)
            if (i != 10) target[i].Should().Be(0f, $"band {i} received no energy");
    }

    [Fact]
    public void MapBands_OutputClampedToOne()
    {
        var edges = AudioSpectrumMapper.BuildBandEdges(FftSize, SampleRate, Bands);
        var magnitudes = new float[FftSize / 2];
        Array.Fill(magnitudes, 100f);
        var target = new float[Bands];
        AudioSpectrumMapper.MapBands(magnitudes, edges, target);
        target.Should().OnlyContain(v => v == 1f);
    }

    // ---- attack/decay smoothing ----

    [Fact]
    public void Smooth_RisesFasterThanItFalls()
    {
        var smoothed = new float[1];
        AudioSpectrumMapper.Smooth([1f], smoothed);          // attack step from 0 toward 1
        var rise = smoothed[0];
        rise.Should().BeGreaterThan(0.5f);

        var falling = new float[] { 1f };
        AudioSpectrumMapper.Smooth([0f], falling);           // decay step from 1 toward 0
        var fallAmount = 1f - falling[0];
        fallAmount.Should().BeLessThan(rise, "decay must be slower than attack");
    }

    // ---- beat detection ----

    [Fact]
    public void BeatDetector_QuietInput_NeverFires()
    {
        var detector = new BeatDetector();
        for (var i = 0; i < 200; i++)
            detector.Update(0.01f).Should().BeFalse();
    }

    [Fact]
    public void BeatDetector_EnergySpikeAboveAverage_Fires()
    {
        var detector = new BeatDetector();
        for (var i = 0; i < 32; i++) detector.Update(0.1f); // establish baseline
        detector.Update(0.6f).Should().BeTrue("a 6x spike above the running average is a beat");
    }

    [Fact]
    public void BeatDetector_RefractoryWindow_SuppressesDoubleTrigger()
    {
        var detector = new BeatDetector(refractoryUpdates: 8);
        for (var i = 0; i < 32; i++) detector.Update(0.1f);
        detector.Update(0.6f).Should().BeTrue();
        for (var i = 0; i < 8; i++)
            detector.Update(0.6f).Should().BeFalse("the refractory window suppresses immediate re-triggers");
    }

    [Fact]
    public void BeatDetector_SteadyLoudInput_DoesNotKeepFiring()
    {
        var detector = new BeatDetector();
        for (var i = 0; i < 40; i++) detector.Update(0.5f);
        // After the running average catches up, constant energy is no longer a spike.
        var fired = false;
        for (var i = 0; i < 40; i++) fired |= detector.Update(0.5f);
        fired.Should().BeFalse("steady energy is not a beat onset");
    }
}
