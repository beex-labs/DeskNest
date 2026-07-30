using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Button = System.Windows.Controls.Button;
using Brushes = System.Windows.Media.Brushes;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using FontFamily = System.Windows.Media.FontFamily;

namespace BeeX.DeskNest;

public partial class WidgetWindow
{
    Grid? workTimerPanel;
    TextBlock? workTimerStatus, workTimerPrimary, workTimerCountdownLabel, workTimerCountdownValue;
    DispatcherTimer? workTimer;

    void BuildWorkTimerPanel()
    {
        workTimerPanel = new Grid { Visibility = Visibility.Collapsed };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(12) };
        workTimerStatus = new TextBlock { Text = L("今日工作"), HorizontalAlignment = HorizontalAlignment.Center, FontWeight = FontWeights.Normal };
        workTimerPrimary = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 4) };
        workTimerCountdownLabel = new TextBlock { Text = L("距離下班還有"), HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.Normal, Margin = new Thickness(0,8,0,0) };
        workTimerCountdownValue = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 8) };
        stack.Children.Add(workTimerStatus); stack.Children.Add(workTimerPrimary); stack.Children.Add(workTimerCountdownLabel); stack.Children.Add(workTimerCountdownValue); workTimerPanel.Children.Add(stack); ContentHost.Children.Add(workTimerPanel);
    }

    void StartWorkTimer()
    {
        ApplyWorkTimerTypography(); UpdateWorkTimer();
        workTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        workTimer.Tick += (_, _) => UpdateWorkTimer(); workTimer.Start();
    }

    void ApplyWorkTimerTypography()
    {
        if (workTimerPrimary == null) return;
        var family = new FontFamily(model.FontFamily);
        Brush selectedBrush;
        try { selectedBrush = (Brush)new BrushConverter().ConvertFromString(model.FontColor)!; }
        catch { selectedBrush = model.Skin == "Dark" ? Brushes.White : Brushes.Black; }
        var hasCustomColor = !string.IsNullOrWhiteSpace(model.FontColor)
                             && !model.FontColor.Equals("#0D1321", StringComparison.OrdinalIgnoreCase)
                             && !model.FontColor.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase);
        var gray = hasCustomColor ? selectedBrush : new SolidColorBrush(model.Skin == "Dark" ? Color.FromRgb(184, 192, 207) : Color.FromRgb(102, 112, 133));
        var red = hasCustomColor ? selectedBrush : new SolidColorBrush(Color.FromRgb(217, 45, 32));
        Brush? background = null;
        if (!string.IsNullOrWhiteSpace(model.TextBackgroundColor))
        {
            try { background = (Brush)new BrushConverter().ConvertFromString(model.TextBackgroundColor)!; } catch { }
        }
        foreach (var text in new[] { workTimerStatus, workTimerPrimary, workTimerCountdownLabel, workTimerCountdownValue }.Where(x => x != null)) text!.FontFamily = family;
        var compact=ActualHeight<260||ActualWidth<330;
        workTimerStatus!.Foreground=gray;workTimerStatus.FontWeight=FontWeights.SemiBold;workTimerStatus.FontSize=compact?Math.Max(12,model.FontSize):Math.Max(14,model.FontSize+2);workTimerStatus.Background=background??Brushes.Transparent;
        workTimerPrimary.Foreground=gray;workTimerPrimary.FontWeight=FontWeights.Bold;workTimerPrimary.FontSize=compact?Math.Max(18,model.FontSize+4):Math.Max(26,model.FontSize+10);workTimerPrimary.Background=background??Brushes.Transparent;
        workTimerCountdownLabel!.Foreground=red;workTimerCountdownLabel.FontWeight=FontWeights.Normal;workTimerCountdownLabel.FontSize=compact?Math.Max(13,model.FontSize):Math.Max(15,model.FontSize+1);workTimerCountdownLabel.Background=background??Brushes.Transparent;
        workTimerCountdownValue!.Foreground=red;workTimerCountdownValue.FontWeight=FontWeights.Bold;workTimerCountdownValue.FontSize=compact?Math.Max(26,model.FontSize+10):Math.Max(36,model.FontSize+20);workTimerCountdownValue.Background=background??Brushes.Transparent;
    }

    void UpdateWorkTimer()
    {
        if (workTimerPrimary == null || !TimeSpan.TryParse(model.WorkStart, out var startTime) || !TimeSpan.TryParse(model.WorkEnd, out var endTime)) return;
        var now=DateTime.Now;
        var crossesMidnight=endTime<=startTime;
        var start=now.Date+startTime;
        var end=now.Date+endTime;
        if(crossesMidnight&&now.TimeOfDay<endTime){start=now.Date.AddDays(-1)+startTime;end=now.Date+endTime;}
        else if(crossesMidnight)end=start.AddDays(1);
        var workday=model.WorkDays.Contains((int)start.DayOfWeek);
        if(!workday){workTimerStatus!.Text=L("今天不用上班");workTimerPrimary.Text=L("好好休息");workTimerCountdownLabel!.Text=L("今日班表");workTimerCountdownValue!.Text=$"{model.WorkStart} - {model.WorkEnd}";return;}
        if(now<start){workTimerStatus!.Text=L("尚未上班");workTimerPrimary.Text=$"{L("上班時間")} {model.WorkStart}";workTimerCountdownLabel!.Text=L("距離上班還有");workTimerCountdownValue!.Text=FormatDuration(start-now);}
        else if(now<end){workTimerStatus!.Text=L("已經上班");workTimerPrimary.Text=FormatDuration(now-start);workTimerCountdownLabel!.Text=L("距離下班還有");workTimerCountdownValue!.Text=FormatDuration(end-now);}
        else{workTimerStatus!.Text=L("今天已下班");workTimerPrimary.Text=$"{L("上班累計")} {FormatDuration(end-start)}";workTimerCountdownLabel!.Text=L("辛苦了");workTimerCountdownValue!.Text=L("下班快樂");}
        if (now >= end.AddMinutes(-1) && now < end && model.LastWorkEndAlertDate?.Date != end.Date)
        {
            model.LastWorkEndAlertDate = end.Date; service.Save(); System.Media.SystemSounds.Asterisk.Play(); BeeXDialog.Notify(this, L("快下班了"), L("距離下班只剩 1 分鐘，記得保存工作並準備打卡。"), service.State);
        }
        UpdateCollapsedWidgetHeader();
    }

    static string FormatDuration(TimeSpan span) => span.TotalHours >= 1 ? $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";
}
