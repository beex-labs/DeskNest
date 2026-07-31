using System.Text.Json;
using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Wallpaper;

public class WallpaperModelsTests
{
    [Fact]
    public void WallpaperItem_DefaultValues()
    {
        var item = new WallpaperItem();
        item.Id.Should().NotBe(Guid.Empty);
        item.Kind.Should().Be(WallpaperKind.Video);
        item.Path.Should().BeEmpty();
        item.Name.Should().BeEmpty();
        item.Thumb.Should().BeEmpty();
        item.Volume.Should().Be(1);
        item.PlaybackRate.Should().Be(1);
        item.AudioReactive.Should().BeFalse();
        item.Interactive.Should().BeFalse();
        item.Props.Should().BeEmpty();
    }

    [Fact]
    public void AppState_WallpaperDefaults()
    {
        var state = new AppState();
        state.WallpaperEnabled.Should().BeFalse();
        state.WallpaperLibrary.Should().BeEmpty();
        state.WallpaperPerMonitor.Should().BeEmpty();
        state.WallpaperFpsCap.Should().Be(60);
        state.WallpaperPauseWhenOccluded.Should().BeTrue();
        state.WallpaperPauseOnBattery.Should().BeTrue();
        state.WallpaperMuteOnFullscreen.Should().BeTrue();
        state.WallpaperGlobalVolume.Should().Be(0);
        state.WallpaperAudioReactive.Should().BeTrue();
    }

    [Fact]
    public void WallpaperItem_JsonRoundTrip()
    {
        var original = new WallpaperItem
        {
            Kind = WallpaperKind.Shader,
            Path = @"D:\BeeX\Wallpapers\a\clip.mp4",
            Name = "霓虹",
            Thumb = @"D:\BeeX\Wallpapers\a\thumb_001.png",
            Volume = 0.8,
            PlaybackRate = 1.5,
            AudioReactive = true,
            Interactive = true,
        };
        original.Props["hue"] = "220";

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<WallpaperItem>(json);

        restored.Should().NotBeNull();
        restored!.Id.Should().Be(original.Id);
        restored.Kind.Should().Be(WallpaperKind.Shader);
        restored.Path.Should().Be(original.Path);
        restored.Name.Should().Be("霓虹");
        restored.Thumb.Should().Be(original.Thumb);
        restored.Volume.Should().Be(0.8);
        restored.PlaybackRate.Should().Be(1.5);
        restored.AudioReactive.Should().BeTrue();
        restored.Interactive.Should().BeTrue();
        restored.Props.Should().ContainKey("hue").WhoseValue.Should().Be("220");
    }

    [Fact]
    public void AppState_WallpaperJsonRoundTrip()
    {
        var item = new WallpaperItem { Kind = WallpaperKind.Video, Name = "Ocean" };
        var original = new AppState
        {
            WallpaperEnabled = true,
            WallpaperFpsCap = 120,
            WallpaperPauseWhenOccluded = false,
            WallpaperPauseOnBattery = false,
            WallpaperMuteOnFullscreen = false,
            WallpaperGlobalVolume = 0.5,
            WallpaperAudioReactive = false,
        };
        original.WallpaperLibrary.Add(item);
        original.WallpaperPerMonitor[@"\\.\DISPLAY1"] = item.Id;

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<AppState>(json);

        restored.Should().NotBeNull();
        restored!.WallpaperEnabled.Should().BeTrue();
        restored.WallpaperFpsCap.Should().Be(120);
        restored.WallpaperPauseWhenOccluded.Should().BeFalse();
        restored.WallpaperPauseOnBattery.Should().BeFalse();
        restored.WallpaperMuteOnFullscreen.Should().BeFalse();
        restored.WallpaperGlobalVolume.Should().Be(0.5);
        restored.WallpaperAudioReactive.Should().BeFalse();
        restored.WallpaperLibrary.Should().HaveCount(1);
        restored.WallpaperLibrary[0].Name.Should().Be("Ocean");
        restored.WallpaperPerMonitor.Should().ContainKey(@"\\.\DISPLAY1").WhoseValue.Should().Be(item.Id);
    }

    [Fact]
    public void WallpaperKind_AllValues_RoundTrip()
    {
        foreach (WallpaperKind kind in Enum.GetValues<WallpaperKind>())
        {
            var item = new WallpaperItem { Kind = kind };
            var restored = JsonSerializer.Deserialize<WallpaperItem>(JsonSerializer.Serialize(item));
            restored!.Kind.Should().Be(kind);
        }
    }

    // The Finite clamp is what SanitizeState applies to wallpaper volume, playback rate and fps cap.
    [Fact]
    public void Finite_ClampsOutOfRangeAndReplacesNonFinite()
    {
        DeskNestService.Finite(0.8, 0, 0, 1).Should().Be(0.8);
        DeskNestService.Finite(5, 1, 0.25, 4).Should().Be(4);          // above max
        DeskNestService.Finite(0.1, 1, 0.25, 4).Should().Be(0.25);     // below min
        DeskNestService.Finite(double.NaN, 1, 0.25, 4).Should().Be(1); // non-finite falls back
        DeskNestService.Finite(double.PositiveInfinity, 60, 10, 240).Should().Be(60);
    }
}
