using System.Text.Json;
using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Core;

public class ModelsSerializationTests
{
    // ---- NestModel 默认值 ----

    [Fact]
    public void NestModel_DefaultValues()
    {
        var nest = new NestModel();
        nest.Id.Should().NotBe(Guid.Empty);
        nest.Title.Should().Be("新小工具");
        nest.Content.Should().BeEmpty();
        nest.FolderPath.Should().BeEmpty();
        nest.Left.Should().Be(80);
        nest.Top.Should().Be(80);
        nest.Width.Should().Be(340);
        nest.Height.Should().Be(360);
        nest.IsVisible.Should().BeTrue();
        nest.Todos.Should().BeEmpty();
        nest.Captures.Should().BeEmpty();
        nest.Skin.Should().Be("Acrylic");
        nest.Opacity.Should().Be(0.5);
        nest.FontFamily.Should().Be("Microsoft JhengHei UI");
        nest.FontSize.Should().Be(14);
        nest.FontColor.Should().Be("#0D1321");
        nest.Pinned.Should().BeFalse();
        nest.Locked.Should().BeFalse();
        nest.IsCollapsed.Should().BeFalse();
        nest.City.Should().Be("深圳");
        nest.WorkStart.Should().Be("09:00");
        nest.WorkEnd.Should().Be("18:00");
        nest.WorkDays.Should().BeEquivalentTo(new List<int> { 1, 2, 3, 4, 5 });
        nest.MusicDisplayMode.Should().Be("Cover");
        nest.IsEasterEggTemp.Should().BeFalse();
    }

    // ---- TodoItem 默认值 ----

    [Fact]
    public void TodoItem_DefaultValues()
    {
        var todo = new TodoItem();
        todo.Id.Should().NotBe(Guid.Empty);
        todo.Text.Should().BeEmpty();
        todo.Done.Should().BeFalse();
        todo.Color.Should().Be("#FF8A00");
        todo.DueAt.Should().BeNull();
        todo.ReminderDismissed.Should().BeFalse();
        todo.ReminderOffsets.Should().BeEquivalentTo(new List<int> { 1440, 0 });
        todo.SentReminderOffsets.Should().BeEmpty();
        todo.Repeat.Should().Be("不重複");
        todo.Attachments.Should().BeEmpty();
    }

    // ---- CaptureItem 默认值 ----

    [Fact]
    public void CaptureItem_DefaultValues()
    {
        var item = new CaptureItem();
        item.Id.Should().NotBe(Guid.Empty);
        item.Text.Should().BeEmpty();
        item.ImagePath.Should().BeEmpty();
        item.Pinned.Should().BeFalse();
        item.Paper.Should().Be("White");
        item.Source.Should().Be("Manual");
        item.MarkdownPath.Should().BeEmpty();
    }

    // ---- TagItem 默认值 ----

    [Fact]
    public void TagItem_DefaultValues()
    {
        var tag = new TagItem();
        tag.Id.Should().NotBe(Guid.Empty);
        tag.Name.Should().BeEmpty();
        tag.Color.Should().Be("#FF8A00");
    }

    // ---- CountdownItem 默认值 ----

    [Fact]
    public void CountdownItem_DefaultValues()
    {
        var item = new CountdownItem();
        item.Id.Should().NotBe(Guid.Empty);
        item.Title.Should().Be("重要日子");
        item.Color.Should().Be("#FF8A00");
        item.Annual.Should().BeFalse();
    }

    // ---- AppState 默认值 ----

    [Fact]
    public void AppState_DefaultValues()
    {
        var state = new AppState();
        state.Nests.Should().BeEmpty();
        state.StartWithWindows.Should().BeFalse();
        state.WidgetOpacity.Should().Be(0.5);
        state.Theme.Should().Be("Acrylic");
        state.ThemePreset.Should().Be("Clear");
        state.GlobalFontFamily.Should().Be("Microsoft JhengHei UI");
        state.GlobalFontSize.Should().Be(14);
        state.GlobalFontColor.Should().Be("#0D1321");
        state.CornerRadius.Should().Be(18);
        state.IconSize.Should().Be(30);
        state.ItemSpacing.Should().Be(10);
        state.AlignWidgetsToGrid.Should().BeFalse();
        state.WidgetGridSize.Should().Be(20);
        state.ShowFileExtensions.Should().BeTrue();
        state.ShowReminderSummary.Should().BeTrue();
        state.ShowCollapsedLogo.Should().BeTrue();
        state.EasterEggUnlocked.Should().BeFalse();
        state.ShowFloatingBall.Should().BeTrue();
        state.FloatingBallSnapToEdge.Should().BeTrue();
        state.FloatingBallOpacity.Should().Be(0.5);
        state.CaptureDefaultFormat.Should().Be("png");
        state.CaptureCopyOnSave.Should().BeFalse();
        state.RecordingDefaultFps.Should().Be(30);
        state.RecordingCountdownSec.Should().Be(0);
        state.CaptureLimit.Should().Be(100);
        state.WeatherRefreshMinutes.Should().Be(30);
        state.Language.Should().Be("zh-TW");
        state.Hotkeys.Should().ContainKey("Screenshot");
        state.Hotkeys["Screenshot"].Should().Be("Ctrl + Alt + A");
        state.Hotkeys["ToggleAll"].Should().Be("Ctrl + Alt + B");
    }

    // ---- JSON 序列化/反序列化往返 ----

    [Fact]
    public void NestModel_JsonRoundTrip()
    {
        var original = new NestModel
        {
            Title = "测试便笺",
            Content = "Hello World",
            Kind = NestKind.Note,
            Left = 100,
            Top = 200,
            Width = 400,
            Height = 500,
            Skin = "Clear",
            Opacity = 0.8,
            Pinned = true,
            Locked = true,
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<NestModel>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(original.Id);
        deserialized.Title.Should().Be("测试便笺");
        deserialized.Content.Should().Be("Hello World");
        deserialized.Kind.Should().Be(NestKind.Note);
        deserialized.Left.Should().Be(100);
        deserialized.Top.Should().Be(200);
        deserialized.Width.Should().Be(400);
        deserialized.Height.Should().Be(500);
        deserialized.Skin.Should().Be("Clear");
        deserialized.Opacity.Should().Be(0.8);
        deserialized.Pinned.Should().BeTrue();
        deserialized.Locked.Should().BeTrue();
    }

    [Fact]
    public void AppState_JsonRoundTrip()
    {
        var original = new AppState
        {
            Theme = "Dark",
            WidgetOpacity = 0.7,
            CornerRadius = 12,
            Language = "en-US",
            CaptureDefaultFormat = "jpg",
            RecordingDefaultFps = 60,
            CaptureLimit = 200,
            WeatherRefreshMinutes = 15,
        };
        original.Nests.Add(new NestModel { Title = "Test" });

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppState>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Theme.Should().Be("Dark");
        deserialized.WidgetOpacity.Should().Be(0.7);
        deserialized.CornerRadius.Should().Be(12);
        deserialized.Language.Should().Be("en-US");
        deserialized.CaptureDefaultFormat.Should().Be("jpg");
        deserialized.RecordingDefaultFps.Should().Be(60);
        deserialized.CaptureLimit.Should().Be(200);
        deserialized.WeatherRefreshMinutes.Should().Be(15);
        deserialized.Nests.Should().HaveCount(1);
        deserialized.Nests[0].Title.Should().Be("Test");
    }

    [Fact]
    public void TodoItem_JsonRoundTrip()
    {
        var original = new TodoItem
        {
            Text = "买牛奶",
            Done = true,
            Color = "#FF0000",
            DueAt = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            Repeat = "每天",
        };

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<TodoItem>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Text.Should().Be("买牛奶");
        deserialized.Done.Should().BeTrue();
        deserialized.Color.Should().Be("#FF0000");
        deserialized.DueAt.Should().Be(original.DueAt);
        deserialized.Repeat.Should().Be("每天");
    }

    [Fact]
    public void AppState_Hotkeys_JsonRoundTrip()
    {
        var original = new AppState();
        original.Hotkeys["Screenshot"] = "Ctrl+Shift+S";

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<AppState>(json);

        deserialized!.Hotkeys["Screenshot"].Should().Be("Ctrl+Shift+S");
    }

    [Fact]
    public void NestKind_AllValues_RoundTrip()
    {
        foreach (NestKind kind in Enum.GetValues<NestKind>())
        {
            var nest = new NestModel { Kind = kind };
            var json = JsonSerializer.Serialize(nest);
            var deserialized = JsonSerializer.Deserialize<NestModel>(json);
            deserialized!.Kind.Should().Be(kind);
        }
    }
}
