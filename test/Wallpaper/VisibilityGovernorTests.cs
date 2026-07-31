using System.Drawing;
using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Wallpaper;

public class VisibilityGovernorTests
{
    static readonly Rectangle Monitor = new(0, 0, 1000, 1000); // area = 1,000,000

    // ---- occlusion area math ----

    [Fact]
    public void UncoveredArea_NoOccluders_IsFullArea()
    {
        VisibilityGovernor.UncoveredArea(Monitor, Array.Empty<Rectangle>()).Should().Be(1_000_000);
        VisibilityGovernor.VisibleFraction(Monitor, Array.Empty<Rectangle>()).Should().Be(1.0);
    }

    [Fact]
    public void UncoveredArea_FullCover_IsZero()
    {
        var occ = new[] { new Rectangle(0, 0, 1000, 1000) };
        VisibilityGovernor.UncoveredArea(Monitor, occ).Should().Be(0);
        VisibilityGovernor.VisibleFraction(Monitor, occ).Should().Be(0.0);
    }

    [Fact]
    public void UncoveredArea_HalfCover_IsHalf()
    {
        var occ = new[] { new Rectangle(0, 0, 1000, 500) };
        VisibilityGovernor.VisibleFraction(Monitor, occ).Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void UncoveredArea_OverlappingOccluders_AreNotDoubleCounted()
    {
        // Two columns that overlap in the middle still cover the whole width, leaving nothing visible.
        var occ = new[] { new Rectangle(0, 0, 600, 1000), new Rectangle(400, 0, 600, 1000) };
        VisibilityGovernor.UncoveredArea(Monitor, occ).Should().Be(0);
    }

    [Fact]
    public void UncoveredArea_TwoColumnsWithGap_LeavesMiddleVisible()
    {
        // Left column [0,300) and right column [700,1000) leave the middle 400px-wide strip uncovered.
        var occ = new[] { new Rectangle(0, 0, 300, 1000), new Rectangle(700, 0, 300, 1000) };
        VisibilityGovernor.UncoveredArea(Monitor, occ).Should().Be(400_000);
        VisibilityGovernor.VisibleFraction(Monitor, occ).Should().BeApproximately(0.4, 1e-9);
    }

    [Fact]
    public void UncoveredArea_OccluderOutsideMonitor_IsIgnored()
    {
        var occ = new[] { new Rectangle(2000, 2000, 100, 100) };
        VisibilityGovernor.UncoveredArea(Monitor, occ).Should().Be(1_000_000);
    }

    [Fact]
    public void UncoveredArea_OccluderPartlyOutside_CountsOnlyIntersection()
    {
        // Clipped to the monitor this covers a 200x200 corner (40,000), leaving 960,000 visible.
        var occ = new[] { new Rectangle(-100, -100, 300, 300) };
        VisibilityGovernor.UncoveredArea(Monitor, occ).Should().Be(960_000);
    }

    [Fact]
    public void VisibleFraction_DegenerateMonitor_IsZero()
    {
        VisibilityGovernor.VisibleFraction(new Rectangle(0, 0, 0, 0), Array.Empty<Rectangle>()).Should().Be(0.0);
    }

    // ---- frame-rate decision table ----

    [Fact]
    public void TargetFps_Fullscreen_Pauses()
        => VisibilityGovernor.TargetFps(0.5, fullscreen: true, onBattery: false, batterySaver: false, pauseWhenOccluded: true, pauseOnBattery: true, fpsCap: 60, refreshHz: 60).Should().Be(0);

    [Fact]
    public void TargetFps_FullyCovered_PausesWhenEnabled()
        => VisibilityGovernor.TargetFps(0.0, false, false, false, pauseWhenOccluded: true, pauseOnBattery: true, 60, 60).Should().Be(0);

    [Fact]
    public void TargetFps_FullyCovered_RendersWhenPauseDisabled()
        => VisibilityGovernor.TargetFps(0.0, false, false, false, pauseWhenOccluded: false, pauseOnBattery: true, 60, 60).Should().Be(60);

    [Fact]
    public void TargetFps_OnBattery_PausesWhenEnabled()
        => VisibilityGovernor.TargetFps(0.5, false, onBattery: true, false, true, pauseOnBattery: true, 60, 60).Should().Be(0);

    [Fact]
    public void TargetFps_OnBattery_RendersWhenPauseDisabled()
        => VisibilityGovernor.TargetFps(0.5, false, onBattery: true, false, true, pauseOnBattery: false, 60, 60).Should().Be(60);

    [Fact]
    public void TargetFps_BatterySaver_AlwaysPauses()
        => VisibilityGovernor.TargetFps(1.0, false, false, batterySaver: true, false, false, 60, 60).Should().Be(0);

    [Fact]
    public void TargetFps_Visible_IsCappedByTheLowerOfCapAndRefresh()
    {
        VisibilityGovernor.TargetFps(1.0, false, false, false, true, true, fpsCap: 30, refreshHz: 144).Should().Be(30);
        VisibilityGovernor.TargetFps(1.0, false, false, false, true, true, fpsCap: 120, refreshHz: 60).Should().Be(60);
    }

    [Fact]
    public void TargetFps_UnknownRefresh_FallsBackToSixty()
        => VisibilityGovernor.TargetFps(1.0, false, false, false, true, true, fpsCap: 240, refreshHz: 0).Should().Be(60);
}
