using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Brush = System.Windows.Media.Brush;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using Cursors = System.Windows.Input.Cursors;
using UniformGrid = System.Windows.Controls.Primitives.UniformGrid;

namespace BeeX.DeskNest;

static class BeeXWorkScheduleDialog
{
    public static bool Edit(Window owner, AppState state, NestModel model)
    {
        string L(string value) => Localization.T(value, state.Language);
        var dark = state.Theme == "Dark"; var foreground = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(13, 19, 33));
        var surface = dark ? new SolidColorBrush(Color.FromRgb(18, 24, 39)) : state.Theme == "Honey" ? new SolidColorBrush(Color.FromRgb(255, 247, 232)) : new SolidColorBrush(Color.FromRgb(248, 249, 251));
        var soft = dark ? new SolidColorBrush(Color.FromRgb(31, 39, 55)) : Brushes.White; var orange = new SolidColorBrush(Color.FromRgb(255, 138, 0));
        var window = new Window { Width = 500, Height = 430, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Foreground = foreground, FontSize = state.GlobalFontSize };
        var shell = new Border { Background = surface, BorderBrush = new SolidColorBrush(Color.FromArgb(140, 255, 138, 0)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(Math.Max(16, state.CornerRadius)), ClipToBounds = true };
        // 高 DPI 下 65 物理像素換算後可能小於關閉按鈕高度，設下限避免按鈕被標題行裁切
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Math.Max(TitleBarMetrics.Dip(owner), 48)) }); layout.RowDefinitions.Add(new RowDefinition());
        var header = new Grid { Margin = new Thickness(22, 0, 14, 0), Cursor = Cursors.SizeAll }; header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center }; brand.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")), Width = 24, Height = 24 }); brand.Children.Add(new TextBlock { Text = L("上下班表設定"), FontWeight = FontWeights.SemiBold, FontSize = 18, Foreground = foreground, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }); header.Children.Add(brand);
        var close = new Button { Content = "×", FontSize = 24, Width = 36, Height = 36, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, Foreground = foreground, BorderThickness = new Thickness(0) }; close.Click += (_, _) => window.DialogResult = false; Grid.SetColumn(close, 1); header.Children.Add(close); header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) window.DragMove(); }; layout.Children.Add(header);
        var body = new StackPanel { Margin = new Thickness(30, 12, 30, 28) }; body.Children.Add(Label(L("上下班時間"), foreground));
        var timeGrid = new Grid { Margin = new Thickness(0, 9, 0, 22) }; timeGrid.ColumnDefinitions.Add(new ColumnDefinition()); timeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) }); timeGrid.ColumnDefinitions.Add(new ColumnDefinition());
        TextBox TimeBox(string value) => new() { Text = NormalizeTime(value), FontSize = 22, FontWeight = FontWeights.SemiBold, HorizontalContentAlignment = HorizontalAlignment.Center, Padding = new Thickness(12, 10, 12, 10), Background = Brushes.Transparent, Foreground = foreground, BorderThickness = new Thickness(0), MaxLength = 5 };
        var start = TimeBox(model.WorkStart); var end = TimeBox(model.WorkEnd); AttachTimeMask(start,()=>end.Focus()); AttachTimeMask(end,null); var startCard = new Border { Child = start, Background = soft, CornerRadius = new CornerRadius(11) }; var endCard = new Border { Child = end, Background = soft, CornerRadius = new CornerRadius(11) }; var arrow = new TextBlock { Text = "→", Foreground = orange, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(arrow, 1); Grid.SetColumn(endCard, 2); timeGrid.Children.Add(startCard); timeGrid.Children.Add(arrow); timeGrid.Children.Add(endCard); body.Children.Add(timeGrid);
        body.Children.Add(Label(L("工作日（可多選）"), foreground)); var selected = model.WorkDays.ToHashSet(); var days = new UniformGrid { Columns = 7, Margin = new Thickness(0, 9, 0, 22) }; var names = state.Language == "en-US" ? new[] { "Su", "Mo", "Tu", "We", "Th", "Fr", "Sa" } : new[] { "日", "一", "二", "三", "四", "五", "六" };
        for (var day = 0; day < 7; day++) { var value = day; var on = selected.Contains(value); var b = new Button { Content = names[day], Height = 48, Margin = new Thickness(2), Tag = value, Background = on ? orange : soft, Foreground = on ? Brushes.White : foreground }; b.Click += (_, _) => { if (!selected.Add(value)) selected.Remove(value); var active = selected.Contains(value); b.Background = active ? orange : soft; b.Foreground = active ? Brushes.White : foreground; }; days.Children.Add(b); } body.Children.Add(days);
        body.Children.Add(new TextBlock { Text = L("下班前 1 分鐘會彈出 BeeX 提醒"), Foreground = dark ? Brushes.LightGray : Brushes.Gray, Margin = new Thickness(2, 0, 0, 22) });
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition()); var cancel = new Button { Content = L("取消"), Height = 48, Background = soft, Foreground = foreground, Margin = new Thickness(0, 0, 6, 0) }; var save = new Button { Content = L("保存班表"), Height = 48, Background = orange, Foreground = Brushes.White, Margin = new Thickness(6, 0, 0, 0), FontWeight = FontWeights.SemiBold }; cancel.Click += (_, _) => window.DialogResult = false; save.Click += (_, _) => { if (!TimeSpan.TryParse(start.Text, out _) || !TimeSpan.TryParse(end.Text, out _)) { BeeXDialog.Alert(window, L("時間格式不正確"), L("請使用 HH:mm 格式，例如 09:00。"), state); return; } window.DialogResult = true; }; Grid.SetColumn(save, 1); actions.Children.Add(cancel); actions.Children.Add(save); body.Children.Add(actions); Grid.SetRow(body, 1); layout.Children.Add(body); shell.Child = layout; window.Content = shell;
        if (window.ShowDialog() != true) return false; model.WorkStart = start.Text.Trim(); model.WorkEnd = end.Text.Trim(); model.WorkDays = selected.OrderBy(x => x).ToList(); model.LastWorkEndAlertDate = null; return true;
    }
    static TextBlock Label(string value, Brush brush) => new() { Text = value, Foreground = brush, FontWeight = FontWeights.SemiBold, FontSize = 13 };
    static string NormalizeTime(string value){if(TimeSpan.TryParse(value,out var t))return $"{(int)t.TotalHours%24:00}:{t.Minutes:00}";var digits=new string((value??"").Where(char.IsDigit).Take(4).ToArray()).PadRight(4,'0');return $"{digits[..2]}:{digits[2..4]}";}
    static void AttachTimeMask(TextBox box,Action? completed)
    {
        bool rendering=false, fresh=true; var digits=new string(box.Text.Where(char.IsDigit).Take(4).ToArray()).PadRight(4,'0')[..4];
        void Render(bool placeholders=true)
        {
            rendering=true;
            var display=placeholders ? digits.PadRight(4,'_') : digits.PadRight(4,'0');
            box.Text=$"{display[..2]}:{display[2..4]}";
            box.CaretIndex=digits.Length<=2?Math.Min(digits.Length,2):Math.Min(5,3+digits.Length-2);
            rendering=false;
        }
        void AcceptDigit(int number)
        {
            if(fresh){digits="";fresh=false;}
            if(digits.Length>=4)digits="";
            digits+=number.ToString();
            Render();
            if(digits.Length==2)box.CaretIndex=3;
            if(digits.Length>=4)completed?.Invoke();
        }
        box.PreviewTextInput+=(_,e)=>{e.Handled=true;};
        System.Windows.DataObject.AddPastingHandler(box,(_,e)=>{
            if(!e.DataObject.GetDataPresent(System.Windows.DataFormats.Text)){e.CancelCommand();return;}
            var text=e.DataObject.GetData(System.Windows.DataFormats.Text)?.ToString()??"";
            digits=new string(text.Where(char.IsDigit).Take(4).ToArray());
            fresh=false;
            Render();
            e.CancelCommand();
            if(digits.Length>=4)completed?.Invoke();
        });
        box.GotKeyboardFocus+=(_,_)=>{digits=new string(box.Text.Where(char.IsDigit).Take(4).ToArray());fresh=true;box.SelectAll();};
        box.PreviewMouseLeftButtonDown+=(_,_)=>{digits=new string(box.Text.Where(char.IsDigit).Take(4).ToArray());fresh=true;box.Dispatcher.BeginInvoke(new Action(()=>box.SelectAll()),System.Windows.Threading.DispatcherPriority.Input);};
        box.PreviewKeyDown+=(_,e)=>{
            int? number=e.Key switch{>=Key.D0 and <=Key.D9=>(int)(e.Key-Key.D0),>=Key.NumPad0 and <=Key.NumPad9=>(int)(e.Key-Key.NumPad0),_=>null};
            if(number.HasValue){AcceptDigit(number.Value);e.Handled=true;return;}
            if(e.Key==Key.Back||e.Key==Key.Delete){if(fresh){digits="";fresh=false;}else if(digits.Length>0)digits=digits[..^1];Render();e.Handled=true;return;}
            if(e.Key==Key.OemSemicolon||e.Key==Key.Decimal||e.Key==Key.Space){fresh=false;if(digits.Length==1)digits="0"+digits;else if(digits.Length==0)digits="00";Render();box.CaretIndex=3;e.Handled=true;return;}
            if(e.Key is Key.Left or Key.Right or Key.Home or Key.End){e.Handled=true;return;}
        };
        box.TextChanged+=(_,_)=>{if(rendering)return;};
        box.LostFocus+=(_,_)=>{fresh=true;digits=new string(box.Text.Where(char.IsDigit).Take(4).ToArray()).PadRight(4,'0')[..4];var hour=Math.Clamp(int.Parse(digits[..2]),0,23);var minute=Math.Clamp(int.Parse(digits[2..4]),0,59);digits=$"{hour:00}{minute:00}";Render(false);};
        Render(false);
    }
}
