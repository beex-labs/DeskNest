using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using CheckBox = System.Windows.Controls.CheckBox;
using Orientation = System.Windows.Controls.Orientation;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using ComboBox = System.Windows.Controls.ComboBox;
using Slider = System.Windows.Controls.Slider;
using UniformGrid = System.Windows.Controls.Primitives.UniformGrid;
using FontFamily = System.Windows.Media.FontFamily;

namespace BeeX.DeskNest;

static class BeeXCountdownDialog
{
    public static bool TryEdit(Window owner, AppState state, CountdownItem source, out string title, out DateTime date, out bool annual, out string fontFamily, out double fontSize, out string fontColor)
    {
        string L(string v) => Localization.T(v, state.Language);
        var dark = state.Theme == "Dark";
        var honey = state.Theme == "Honey";
        var foreground = dark ? Brushes.White : new SolidColorBrush(Color.FromRgb(13, 19, 33));
        var secondary = dark ? new SolidColorBrush(Color.FromRgb(184, 192, 207)) : new SolidColorBrush(Color.FromRgb(102, 112, 133));
        var surface = dark ? Color.FromRgb(18, 24, 39) : honey ? Color.FromRgb(255, 247, 232) : Color.FromRgb(248, 249, 251);
        var card = dark ? new SolidColorBrush(Color.FromRgb(31, 39, 55)) : Brushes.White;
        var orange = new SolidColorBrush(Color.FromRgb(255, 138, 0));
        var softOrange = dark ? new SolidColorBrush(Color.FromRgb(70, 48, 24)) : new SolidColorBrush(Color.FromRgb(255, 243, 229));
        var border = dark ? new SolidColorBrush(Color.FromRgb(55, 65, 81)) : new SolidColorBrush(Color.FromRgb(229, 231, 235));

        var window = new Window { Width = 520, Height = 680, Owner = owner, WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, FontSize = state.GlobalFontSize, Foreground = foreground };
        var shell = new Border { CornerRadius = new CornerRadius(Math.Max(16, state.CornerRadius)), Background = new SolidColorBrush(surface), BorderBrush = new SolidColorBrush(Color.FromArgb(150, 255, 138, 0)), BorderThickness = new Thickness(1), ClipToBounds = true };
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarMetrics.Dip(owner)) });
        root.RowDefinitions.Add(new RowDefinition());

        var header = new Grid { Margin = new Thickness(22, 0, 14, 0), Cursor = Cursors.SizeAll };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")), Width = 25, Height = 25 });
        brand.Children.Add(new TextBlock { Text = source.Id == Guid.Empty ? L("新增倒數日") : L("編輯倒數日"), FontWeight = FontWeights.SemiBold, FontSize = 18, Foreground = foreground, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) });
        header.Children.Add(brand);
        var close = new Button { Content = "×", FontSize = 27, Width = 42, Height = 42, Padding = new Thickness(0), Background = Brushes.Transparent, Foreground = foreground, BorderThickness = new Thickness(0) };
        close.Click += (_, _) => window.DialogResult = false;
        Grid.SetColumn(close, 1); header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed) window.DragMove(); };
        root.Children.Add(header);

        var body = new StackPanel { Margin = new Thickness(30, 10, 30, 28) };
        body.Children.Add(Label(L("事件名稱"), secondary));
        var name = new TextBox { Text = source.Title, Padding = new Thickness(14, 11, 14, 11), Background = Brushes.Transparent, Foreground = foreground, BorderThickness = new Thickness(0), FontSize = 16 };
        body.Children.Add(new Border { Child = name, Background = card, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 7, 0, 20) });

        body.Children.Add(Label(L("目標日期"), secondary));
        var selected = source.Date.Date;
        TextBlock? yearValue = null, monthValue = null, dayValue = null, preview = null;
        void Refresh()
        {
            yearValue!.Text = selected.Year.ToString(); monthValue!.Text = selected.Month.ToString("00"); dayValue!.Text = selected.Day.ToString("00");
            var days = (selected - DateTime.Today).Days;
            preview!.Text = days switch { 0 => L("就是今天"), > 0 => Localization.Format("還有 {0} 天", state.Language, days), _ => Localization.Format("已過 {0} 天", state.Language, -days) };
        }
        Border Stepper(string caption, Func<DateTime, int, DateTime> change, out TextBlock value)
        {
            var panel = new Grid(); panel.RowDefinitions.Add(new RowDefinition()); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) }); panel.ColumnDefinitions.Add(new ColumnDefinition()); panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            Button Arrow(string glyph,string tip)=>new(){Content=new TextBlock{Text=glyph,FontSize=14,FontWeight=FontWeights.SemiBold,Foreground=orange,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center},ToolTip=tip,Width=28,Height=36,Padding=new Thickness(0),Margin=new Thickness(2,7,2,0),Background=Brushes.Transparent,BorderThickness=new Thickness(0),HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center};
            var minus = Arrow("◀",L("上一個") );
            value = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Foreground = foreground, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var plus = Arrow("▶",L("下一個"));
            minus.Click += (_, _) => { selected = change(selected, -1); Refresh(); };
            plus.Click += (_, _) => { selected = change(selected, 1); Refresh(); };
            panel.MouseWheel+=(_,e)=>{selected=change(selected,e.Delta>0?-1:1);Refresh();e.Handled=true;};
            Grid.SetColumn(value, 1); Grid.SetColumn(plus, 2); panel.Children.Add(minus); panel.Children.Add(value); panel.Children.Add(plus);
            var label = new TextBlock { Text = caption, Foreground = secondary, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 8) };
            Grid.SetRow(label, 1); Grid.SetColumnSpan(label, 3); panel.Children.Add(label);
            return new Border { Child = panel, Background = card, BorderBrush = border, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Height = 82 };
        }
        var dates = new Grid { Margin = new Thickness(0, 8, 0, 13) };
        dates.ColumnDefinitions.Add(new ColumnDefinition()); dates.ColumnDefinitions.Add(new ColumnDefinition()); dates.ColumnDefinitions.Add(new ColumnDefinition());
        var yearCard = Stepper("年", (d, n) => { var target = d.Year + n; return new DateTime(target, d.Month, Math.Min(d.Day, DateTime.DaysInMonth(target, d.Month))); }, out yearValue);
        var monthCard = Stepper("月", (d, n) => d.AddMonths(n), out monthValue); monthCard.Margin = new Thickness(9, 0, 9, 0);
        var dayCard = Stepper("日", (d, n) => d.AddDays(n), out dayValue);
        Grid.SetColumn(monthCard, 1); Grid.SetColumn(dayCard, 2); dates.Children.Add(yearCard); dates.Children.Add(monthCard); dates.Children.Add(dayCard); body.Children.Add(dates);

        var quickDate = new Grid { Margin = new Thickness(0, 0, 0, 13) };
        quickDate.ColumnDefinitions.Add(new ColumnDefinition());
        foreach (var _ in Enumerable.Range(0, 4)) quickDate.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var directDate = new TextBox { Padding = new Thickness(12, 8, 12, 8), Background = card, Foreground = foreground, BorderBrush = border, BorderThickness = new Thickness(1), FontSize = 14, ToolTip = L("輸入 yyyy/MM/dd 後按 Enter") };
        var calendarPopup = new System.Windows.Controls.Primitives.Popup { PlacementTarget = directDate, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var shownMonth = new DateTime(selected.Year, selected.Month, 1);
        void RefreshDirectDate() => directDate.Text = selected.ToString("yyyy/MM/dd");
        void ApplyDirectDate()
        {
            if (DateTime.TryParse(directDate.Text, out var parsed)) { selected = parsed.Date; Refresh(); RefreshDirectDate(); }
            else { directDate.BorderBrush = new SolidColorBrush(Color.FromRgb(217, 45, 32)); directDate.SelectAll(); }
        }
        directDate.KeyDown += (_, e) => { if (e.Key == Key.Enter) { ApplyDirectDate(); e.Handled = true; } };
        directDate.LostFocus += (_, _) => directDate.BorderBrush = border;
        void BuildCalendar()
        {
            var calendar = new StackPanel { Margin = new Thickness(12) };
            var monthHeader = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            monthHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) }); monthHeader.ColumnDefinitions.Add(new ColumnDefinition()); monthHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
            Button MonthArrow(bool previousDirection, string tip)
            {
                var shape = new System.Windows.Shapes.Path { Data = Geometry.Parse("M 4,1 L 12,8 L 4,15 Z"), Fill = orange, Width = 16, Height = 16, Stretch = Stretch.Uniform, RenderTransformOrigin = new System.Windows.Point(.5, .5) };
                if (previousDirection) shape.RenderTransform = new ScaleTransform(-1, 1);
                return new Button { Content = shape, Width = 36, Height = 32, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Background = Brushes.Transparent, BorderThickness = new Thickness(0), ToolTip = tip };
            }
            var previous = MonthArrow(true, L("上一個月"));
            var monthTitle = new TextBlock { Text = shownMonth.ToString("yyyy 年 M 月"), Foreground = foreground, FontSize = 15, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var next = MonthArrow(false, L("下一個月"));
            previous.Click += (_, _) => { shownMonth = shownMonth.AddMonths(-1); BuildCalendar(); };
            next.Click += (_, _) => { shownMonth = shownMonth.AddMonths(1); BuildCalendar(); };
            Grid.SetColumn(monthTitle, 1); Grid.SetColumn(next, 2); monthHeader.Children.Add(previous); monthHeader.Children.Add(monthTitle); monthHeader.Children.Add(next); calendar.Children.Add(monthHeader);
            var week = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 4) };
            foreach (var text in new[] { "一", "二", "三", "四", "五", "六", "日" }) week.Children.Add(new TextBlock { Text = text, Foreground = secondary, FontSize = 11, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 5, 0, 5) });
            calendar.Children.Add(week);
            var days = new UniformGrid { Columns = 7, Rows = 6 };
            var offset = ((int)shownMonth.DayOfWeek + 6) % 7;
            var firstCell = shownMonth.AddDays(-offset);
            for (var index = 0; index < 42; index++)
            {
                var dateValue = firstCell.AddDays(index);
                var isCurrentMonth = dateValue.Month == shownMonth.Month;
                var isSelected = dateValue.Date == selected.Date;
                var day = new Button { Content = dateValue.Day.ToString(), Tag = dateValue, Width = 38, Height = 34, Margin = new Thickness(2), Padding = new Thickness(0), Background = isSelected ? orange : Brushes.Transparent, Foreground = isSelected ? Brushes.White : isCurrentMonth ? foreground : secondary, BorderThickness = new Thickness(0), FontWeight = isSelected ? FontWeights.SemiBold : FontWeights.Normal };
                day.Click += (_, _) => { selected = ((DateTime)day.Tag).Date; shownMonth = new DateTime(selected.Year, selected.Month, 1); Refresh(); RefreshDirectDate(); calendarPopup.IsOpen = false; };
                days.Children.Add(day);
            }
            calendar.Children.Add(days);
            var today = new Button { Content = L("回到今天"), Foreground = orange, Background = softOrange, BorderThickness = new Thickness(0), Height = 34, Margin = new Thickness(0, 8, 0, 0) };
            today.Click += (_, _) => { selected = DateTime.Today; shownMonth = new DateTime(selected.Year, selected.Month, 1); Refresh(); RefreshDirectDate(); calendarPopup.IsOpen = false; };
            calendar.Children.Add(today);
            calendarPopup.Child = new Border { Child = calendar, Width = 320, Background = new SolidColorBrush(surface), BorderBrush = new SolidColorBrush(Color.FromArgb(150, 255, 138, 0)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 5, Opacity = .22 } };
        }
        directDate.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (calendarPopup.IsOpen) return;
            shownMonth = new DateTime(selected.Year, selected.Month, 1);
            BuildCalendar();
            directDate.Dispatcher.BeginInvoke(new Action(() => calendarPopup.IsOpen = true), System.Windows.Threading.DispatcherPriority.Input);
        };
        quickDate.Children.Add(directDate);
        Button Quick(string text, Action action)
        {
            var button = new Button { Content = text, Height = 38, MinWidth = 54, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(6, 0, 0, 0), Background = softOrange, Foreground = foreground, BorderThickness = new Thickness(0), FontSize = 12 };
            button.Click += (_, _) => { action(); Refresh(); RefreshDirectDate(); };
            return button;
        }
        var todayButton = Quick(L("今天"), () => selected = DateTime.Today);
        var weekButton = Quick(L("+7 天"), () => selected = selected.AddDays(7));
        var monthButton = Quick(L("+1 月"), () => selected = selected.AddMonths(1));
        var yearButton = Quick(L("+1 年"), () => selected = selected.AddYears(1));
        Grid.SetColumn(todayButton, 1); Grid.SetColumn(weekButton, 2); Grid.SetColumn(monthButton, 3); Grid.SetColumn(yearButton, 4);
        quickDate.Children.Add(todayButton); quickDate.Children.Add(weekButton); quickDate.Children.Add(monthButton); quickDate.Children.Add(yearButton);
        body.Children.Add(quickDate);

        preview = new TextBlock { Foreground = orange, FontWeight = FontWeights.SemiBold, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center };
        body.Children.Add(new Border { Child = preview, Background = softOrange, CornerRadius = new CornerRadius(10), Padding = new Thickness(12, 9, 12, 9), Margin = new Thickness(0, 0, 0, 15) });
        var repeat = new CheckBox { Content = L("每年重複提醒"), IsChecked = source.Annual, Foreground = foreground, Margin = new Thickness(4, 0, 0, 20) };
        body.Children.Add(repeat);

        body.Children.Add(Label(L("此倒數日的字體"), secondary));
        var selectedFamily=string.IsNullOrWhiteSpace(source.FontFamily)?"Microsoft JhengHei UI":source.FontFamily;var selectedSize=source.FontSize<=0?state.GlobalFontSize:source.FontSize;var selectedColor=string.IsNullOrWhiteSpace(source.FontColor)?"#0D1321":source.FontColor;
        var fontRow=new Grid{Margin=new Thickness(0,7,0,10)};fontRow.ColumnDefinitions.Add(new ColumnDefinition());fontRow.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(145)});
        var fonts=new ComboBox{ItemsSource=new[]{"Microsoft JhengHei UI","Microsoft YaHei UI","Segoe UI","Arial","Consolas"},SelectedItem=selectedFamily,Padding=new Thickness(8,6,8,6)};
        var sizeText=new TextBlock{Text=$"{selectedSize:0} px",Foreground=foreground,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(sizeText,1);fontRow.Children.Add(fonts);fontRow.Children.Add(sizeText);body.Children.Add(fontRow);
        var sizeSlider=new Slider{Minimum=12,Maximum=48,Value=selectedSize,TickFrequency=1,IsSnapToTickEnabled=true,Margin=new Thickness(0,0,0,10)};body.Children.Add(sizeSlider);
        TextBlock fontPreview=null!;var palette=new UniformGrid{Columns=8,Height=38,Margin=new Thickness(0,0,0,10)};foreach(var hex in new[]{"#0D1321","#667085","#FFFFFF","#FF8A00","#D92D20","#175CD3","#067647","#7F56D9"}){var swatch=new Button{Tag=hex,Background=(Brush)new BrushConverter().ConvertFromString(hex)!,Margin=new Thickness(3),Padding=new Thickness(0),BorderBrush=orange,BorderThickness=new Thickness(hex==selectedColor?3:0)};swatch.Click+=(_,_)=>{selectedColor=hex;foreach(Button b in palette.Children)b.BorderThickness=new Thickness(b.Tag?.ToString()==hex?3:0);fontPreview.Foreground=(Brush)new BrushConverter().ConvertFromString(hex)!;};palette.Children.Add(swatch);}body.Children.Add(palette);
        fontPreview=new TextBlock{Text=source.Title,FontFamily=new FontFamily(selectedFamily),FontSize=selectedSize,Foreground=(Brush)new BrushConverter().ConvertFromString(selectedColor)!,TextAlignment=TextAlignment.Center,TextWrapping=TextWrapping.Wrap,Padding=new Thickness(10)};body.Children.Add(new Border{Child=fontPreview,Background=card,CornerRadius=new CornerRadius(10),Margin=new Thickness(0,0,0,18)});
        fonts.SelectionChanged+=(_,_)=>{selectedFamily=fonts.SelectedItem?.ToString()??selectedFamily;fontPreview.FontFamily=new FontFamily(selectedFamily);};sizeSlider.ValueChanged+=(_,_)=>{selectedSize=sizeSlider.Value;sizeText.Text=$"{selectedSize:0} px";fontPreview.FontSize=selectedSize;};name.TextChanged+=(_,_)=>fontPreview.Text=string.IsNullOrWhiteSpace(name.Text)?L("倒數日預覽"):name.Text;

        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        var cancel = new Button { Content = L("取消"), Height = 48, Background = softOrange, Foreground = foreground, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 6, 0) };
        var save = new Button { Content = L("保存倒數日"), Height = 48, Background = orange, Foreground = Brushes.White, BorderThickness = new Thickness(0), Margin = new Thickness(6, 0, 0, 0), FontWeight = FontWeights.SemiBold };
        cancel.Click += (_, _) => window.DialogResult = false; save.Click += (_, _) => window.DialogResult = true;
        Grid.SetColumn(save, 1); actions.Children.Add(cancel); actions.Children.Add(save); body.Children.Add(actions);
        var scroll=new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(scroll, 1); root.Children.Add(scroll); shell.Child = root; window.Content = shell;

        Refresh(); RefreshDirectDate(); name.Focus(); name.SelectAll();
        window.Loaded += (_, _) => WindowRegionHelper.ApplyDeferred(window, Math.Max(16, state.CornerRadius));
        var accepted = window.ShowDialog() == true;
        title = string.IsNullOrWhiteSpace(name.Text) ? "重要日子" : name.Text.Trim(); date = selected; annual = repeat.IsChecked == true;fontFamily=selectedFamily;fontSize=selectedSize;fontColor=selectedColor;
        return accepted;
    }

    static TextBlock Label(string text, Brush foreground) => new() { Text = text, Foreground = foreground, FontWeight = FontWeights.SemiBold, FontSize = 13 };
}
