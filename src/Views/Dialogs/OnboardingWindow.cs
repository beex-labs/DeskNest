using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush=System.Windows.Media.Brush;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using Button=System.Windows.Controls.Button;
using Image=System.Windows.Controls.Image;
using Orientation=System.Windows.Controls.Orientation;
using HorizontalAlignment=System.Windows.HorizontalAlignment;
using Cursors=System.Windows.Input.Cursors;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;

namespace BeeX.DeskNest;

/// <summary>
/// 引導模式：每台新電腦首次啟動觸發一次的四步嚮導（語言 → 主題 → 起始格子 → 快捷鍵導覽）。
/// 完成/跳過/直接關窗都會記錄本機機器指紋，之後不再觸發；不再自動塞默認格子，改由用戶自選。
/// </summary>
sealed class OnboardingWindow : Window
{
    const int PageCount=4;
    static readonly Color Accent=Color.FromRgb(255,138,0);
    readonly DeskNestService service;
    int page;
    string language;
    string theme;
    readonly HashSet<NestKind> picks;
    bool finished;

    public OnboardingWindow(DeskNestService service)
    {
        this.service=service;
        language=service.State.Language;
        theme=service.State.Theme;
        // 預勾選：桌面已有格子（資料目錄遷移到新電腦）時反映現狀，避免完成後出現「只勾待辦卻冒出一堆舊格子」；全新用戶才用推薦四件套
        var existing=service.State.Nests.Select(n=>n.Kind).Where(DeskNestService.OnboardingKinds.Contains).ToHashSet();
        picks=existing.Count>0?existing:[NestKind.Todo,NestKind.Music,NestKind.Weather,NestKind.Clock];
        Title="BeeX DeskNest";
        Width=680;Height=560;
        WindowStartupLocation=WindowStartupLocation.CenterScreen;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;
        AllowsTransparency=true;Background=Brushes.Transparent;
        Topmost=true;ShowInTaskbar=true;
        Icon=new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.ico"));
        FontFamily=new System.Windows.Media.FontFamily(service.InterfaceFontFamily());
        KeyDown+=OnKeyDown;
        Closing+=(_,_)=>{if(!finished){finished=true;service.SkipOnboarding();}};
        BuildUi();
    }

    string L(string key)=>Localization.T(key,language);
    bool Dark=>theme=="Dark";
    bool Honey=>theme=="Honey";
    Brush Fg=>Dark?Brushes.White:new SolidColorBrush(Color.FromRgb(13,19,33));
    Brush FgMuted=>Dark?new SolidColorBrush(Color.FromRgb(178,186,201)):new SolidColorBrush(Color.FromRgb(102,112,133));
    Color SurfaceColor=>Dark?Color.FromRgb(22,29,45):Honey?Color.FromRgb(255,244,222):Color.FromRgb(250,251,252);
    Brush CardBg=>new SolidColorBrush(Dark?Color.FromArgb(28,255,255,255):Color.FromArgb(16,255,138,0));

    void OnKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Escape)Close();
        else if(e.Key==Key.Enter){if(page<PageCount-1)GoTo(page+1);else Finish();}
    }

    void GoTo(int target){page=Math.Clamp(target,0,PageCount-1);BuildUi();}

    void Finish()
    {
        if(finished)return;
        finished=true;
        service.CompleteOnboarding(language,theme,picks.ToList());
        Close();
    }

    void BuildUi()
    {
        var shell=new Border{CornerRadius=new CornerRadius(service.State.CornerRadius),Background=new SolidColorBrush(SurfaceColor),BorderBrush=new SolidColorBrush(Color.FromArgb(130,Accent.R,Accent.G,Accent.B)),BorderThickness=new Thickness(1),Padding=new Thickness(30,22,30,22),ClipToBounds=true,SnapsToDevicePixels=true};
        shell.MouseLeftButtonDown+=(_,e)=>{if(e.LeftButton==MouseButtonState.Pressed)try{DragMove();}catch{}};
        var root=new Grid();
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        root.Children.Add(BuildHeader());
        var content=page switch{0=>BuildWelcomePage(),1=>BuildThemePage(),2=>BuildWidgetPage(),_=>BuildTourPage()};
        Grid.SetRow(content,1);root.Children.Add(content);
        var footer=BuildFooter();Grid.SetRow(footer,2);root.Children.Add(footer);
        shell.Child=root;Content=shell;
    }

    UIElement BuildHeader()
    {
        var header=new Grid{Margin=new Thickness(0,0,0,4)};
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var brand=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};
        brand.Children.Add(new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")),Width=24,Height=24});
        brand.Children.Add(new TextBlock{Text="BeeX DeskNest",FontWeight=FontWeights.SemiBold,FontSize=16,Foreground=Fg,Margin=new Thickness(10,0,0,0),VerticalAlignment=VerticalAlignment.Center});
        header.Children.Add(brand);
        var close=new Button{Content="×",FontSize=22,Width=34,Height=34,Padding=new Thickness(0),Background=Brushes.Transparent,BorderThickness=new Thickness(0),Foreground=FgMuted,Cursor=Cursors.Hand,ToolTip=L("跳過")};
        close.Click+=(_,_)=>Close();
        Grid.SetColumn(close,1);header.Children.Add(close);
        return header;
    }

    UIElement BuildFooter()
    {
        var footer=new Grid{Margin=new Thickness(0,18,0,0)};
        footer.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        footer.ColumnDefinitions.Add(new ColumnDefinition());
        footer.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
        var skip=new Button{Content=L("跳過"),Background=Brushes.Transparent,BorderThickness=new Thickness(0),Foreground=FgMuted,Padding=new Thickness(10,7,10,7),Cursor=Cursors.Hand,Visibility=page<PageCount-1?Visibility.Visible:Visibility.Hidden};
        skip.Click+=(_,_)=>Close();
        footer.Children.Add(skip);
        var dots=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center};
        for(var i=0;i<PageCount;i++)
        {
            var current=i==page;
            dots.Children.Add(new Border{Width=current?22:8,Height=8,CornerRadius=new CornerRadius(4),Margin=new Thickness(4,0,4,0),Background=new SolidColorBrush(current?Accent:Color.FromArgb(70,Accent.R,Accent.G,Accent.B))});
        }
        Grid.SetColumn(dots,1);footer.Children.Add(dots);
        var actions=new StackPanel{Orientation=Orientation.Horizontal};
        if(page>0)
        {
            var back=new Button{Content=L("上一步"),MinWidth=92,Padding=new Thickness(14,8,14,8),Margin=new Thickness(0,0,10,0),Background=Dark?new SolidColorBrush(Color.FromArgb(45,255,255,255)):new SolidColorBrush(Color.FromRgb(255,243,229)),Foreground=Fg,BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
            back.Click+=(_,_)=>GoTo(page-1);
            actions.Children.Add(back);
        }
        var next=new Button{Content=page<PageCount-1?L("下一步"):L("開始使用"),MinWidth=112,Padding=new Thickness(16,8,16,8),Background=new SolidColorBrush(Accent),Foreground=Brushes.White,FontWeight=FontWeights.SemiBold,BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        next.Click+=(_,_)=>{if(page<PageCount-1)GoTo(page+1);else Finish();};
        actions.Children.Add(next);
        Grid.SetColumn(actions,2);footer.Children.Add(actions);
        return footer;
    }

    TextBlock PageTitle(string key)=>new(){Text=L(key),FontSize=22,FontWeight=FontWeights.SemiBold,Foreground=Fg,HorizontalAlignment=HorizontalAlignment.Center};
    TextBlock PageHint(string key)=>new(){Text=L(key),FontSize=13,Foreground=FgMuted,HorizontalAlignment=HorizontalAlignment.Center,TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,Margin=new Thickness(0,8,0,0)};

    // 第 1 步：歡迎 + 介面語言（語言名稱用各自語言顯示，不做翻譯）
    UIElement BuildWelcomePage()
    {
        var panel=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        panel.Children.Add(new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")),Width=76,Height=76,HorizontalAlignment=HorizontalAlignment.Center});
        panel.Children.Add(new TextBlock{Text=L("歡迎使用 BeeX DeskNest"),FontSize=26,FontWeight=FontWeights.Bold,Foreground=Fg,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,16,0,0)});
        panel.Children.Add(new TextBlock{Text=L("把工作常用內容安放在桌面"),FontSize=14,Foreground=FgMuted,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,8,0,0)});
        panel.Children.Add(new TextBlock{Text=L("選擇介面語言"),FontSize=14,FontWeight=FontWeights.SemiBold,Foreground=Fg,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,30,0,12)});
        var row=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Center};
        foreach(var (code,label) in new[]{("zh-TW","繁體中文"),("zh-CN","简体中文"),("en-US","English")})
        {
            var selected=code==language;
            var pill=new Border{CornerRadius=new CornerRadius(20),Padding=new Thickness(26,10,26,10),Margin=new Thickness(8,0,8,0),Cursor=Cursors.Hand,Background=selected?new SolidColorBrush(Color.FromArgb(36,Accent.R,Accent.G,Accent.B)):CardBg,BorderBrush=new SolidColorBrush(selected?Accent:Color.FromArgb(50,Accent.R,Accent.G,Accent.B)),BorderThickness=new Thickness(selected?2:1),Child=new TextBlock{Text=label,FontSize=14,FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal,Foreground=Fg}};
            // 在 ButtonDown 階段處理並標記 Handled：否則外層 shell 的 DragMove 會吞掉 ButtonUp，點擊選不中
            pill.MouseLeftButtonDown+=(_,e)=>{language=code;BuildUi();e.Handled=true;};
            row.Children.Add(pill);
        }
        panel.Children.Add(row);
        return panel;
    }

    // 第 2 步：外觀主題（選中即在嚮導內即時預覽，最終確定才落到全局）
    UIElement BuildThemePage()
    {
        var panel=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        panel.Children.Add(PageTitle("挑選外觀主題"));
        panel.Children.Add(PageHint("之後可在設定→外觀中隨時更換。"));
        var row=new UniformGrid{Columns=3,Margin=new Thickness(0,26,0,0)};
        foreach(var (value,label,surface,text) in new[]
        {
            ("Acrylic","清透玻璃",Color.FromRgb(245,247,250),Color.FromRgb(13,19,33)),
            ("Honey","蜂蜜暖色",Color.FromRgb(255,244,222),Color.FromRgb(13,19,33)),
            ("Dark","深色",Color.FromRgb(22,29,45),Colors.White)
        })
        {
            var selected=value==theme;
            var preview=new Border{Height=96,CornerRadius=new CornerRadius(12),Background=new SolidColorBrush(surface),Margin=new Thickness(0,0,0,12)};
            var sample=new StackPanel{Margin=new Thickness(14,12,14,0)};
            sample.Children.Add(new Border{Width=52,Height=8,CornerRadius=new CornerRadius(4),Background=new SolidColorBrush(Accent),HorizontalAlignment=HorizontalAlignment.Left});
            sample.Children.Add(new Border{Width=92,Height=6,CornerRadius=new CornerRadius(3),Margin=new Thickness(0,10,0,0),Background=new SolidColorBrush(Color.FromArgb(90,text.R,text.G,text.B)),HorizontalAlignment=HorizontalAlignment.Left});
            sample.Children.Add(new Border{Width=70,Height=6,CornerRadius=new CornerRadius(3),Margin=new Thickness(0,7,0,0),Background=new SolidColorBrush(Color.FromArgb(60,text.R,text.G,text.B)),HorizontalAlignment=HorizontalAlignment.Left});
            preview.Child=sample;
            var body=new StackPanel();
            body.Children.Add(preview);
            body.Children.Add(new TextBlock{Text=L(label),FontSize=14,FontWeight=selected?FontWeights.SemiBold:FontWeights.Normal,Foreground=Fg,HorizontalAlignment=HorizontalAlignment.Center});
            var card=new Border{CornerRadius=new CornerRadius(14),Padding=new Thickness(12,12,12,14),Margin=new Thickness(9,0,9,0),Cursor=Cursors.Hand,Background=selected?new SolidColorBrush(Color.FromArgb(30,Accent.R,Accent.G,Accent.B)):CardBg,BorderBrush=new SolidColorBrush(selected?Accent:Color.FromArgb(50,Accent.R,Accent.G,Accent.B)),BorderThickness=new Thickness(selected?2:1),Child=body};
            card.MouseLeftButtonDown+=(_,e)=>{theme=value;BuildUi();e.Handled=true;};
            row.Children.Add(card);
        }
        panel.Children.Add(row);
        return panel;
    }

    // 第 3 步：自選起始格子（替代舊的「默認塞 4 個格子」設計）
    UIElement BuildWidgetPage()
    {
        var panel=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        panel.Children.Add(PageTitle("挑選要放上桌面的格子"));
        panel.Children.Add(PageHint("推薦組合已勾選；之後可在主控制台隨時新增或刪除。"));
        var grid=new UniformGrid{Columns=3,Margin=new Thickness(0,22,0,0)};
        foreach(var (kind,icon,desc) in new[]
        {
            (NestKind.Todo,"checklist","任務、提醒與 Deadline"),
            (NestKind.Music,"music","正在播放與同步歌詞"),
            (NestKind.Weather,"cloud","今天與未來天氣預報"),
            (NestKind.Clock,"clock","時間與 BeeX 月曆"),
            (NestKind.Note,"note","隨手記下靈感"),
            (NestKind.Capture,"camera","剪貼板與截圖自動收集")
        })
        {
            var selected=picks.Contains(kind);
            var body=new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
            body.ColumnDefinitions.Add(new ColumnDefinition());
            var iconBox=new Border{Width=38,Height=38,CornerRadius=new CornerRadius(10),Background=new SolidColorBrush(Color.FromArgb(30,Accent.R,Accent.G,Accent.B)),VerticalAlignment=VerticalAlignment.Top,Child=new Image{Source=SvgIcon.Load(icon,20,new SolidColorBrush(Accent)),Width=20,Height=20,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center}};
            body.Children.Add(iconBox);
            var text=new StackPanel{Margin=new Thickness(10,0,0,0)};
            var titleRow=new StackPanel{Orientation=Orientation.Horizontal};
            titleRow.Children.Add(new TextBlock{Text=Localization.T(Localization.DefaultTitle(kind),language),FontSize=14,FontWeight=FontWeights.SemiBold,Foreground=Fg});
            if(selected)titleRow.Children.Add(new TextBlock{Text="✓",FontSize=13,FontWeight=FontWeights.Bold,Foreground=new SolidColorBrush(Accent),Margin=new Thickness(6,0,0,0)});
            text.Children.Add(titleRow);
            text.Children.Add(new TextBlock{Text=L(desc),FontSize=11.5,Foreground=FgMuted,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0)});
            Grid.SetColumn(text,1);body.Children.Add(text);
            var card=new Border{CornerRadius=new CornerRadius(14),Padding=new Thickness(14,13,12,13),Margin=new Thickness(7,7,7,7),Cursor=Cursors.Hand,Background=selected?new SolidColorBrush(Color.FromArgb(30,Accent.R,Accent.G,Accent.B)):CardBg,BorderBrush=new SolidColorBrush(selected?Accent:Color.FromArgb(50,Accent.R,Accent.G,Accent.B)),BorderThickness=new Thickness(selected?2:1),Child=body};
            card.MouseLeftButtonDown+=(_,e)=>{if(!picks.Remove(kind))picks.Add(kind);BuildUi();e.Handled=true;};
            grid.Children.Add(card);
        }
        panel.Children.Add(grid);
        return panel;
    }

    // 第 4 步：快捷鍵與入口導覽
    UIElement BuildTourPage()
    {
        var panel=new StackPanel{VerticalAlignment=VerticalAlignment.Center};
        panel.Children.Add(PageTitle("常用快捷鍵與入口"));
        panel.Children.Add(PageHint("這些入口隨時等著你；現在就開始吧。"));
        var list=new StackPanel{Margin=new Thickness(56,24,56,0)};
        foreach(var (shortcut,desc) in new[]
        {
            ("Ctrl + Alt + A","區域截圖"),
            ("Ctrl + Alt + B","顯示／隱藏全部"),
            ("Ctrl + Alt + Q","截圖翻譯"),
            ("Ctrl + Alt + T","釘選剪貼板文字")
        })
        {
            var row=new Grid{Margin=new Thickness(0,0,0,10)};
            row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(170)});
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Border{CornerRadius=new CornerRadius(8),Background=CardBg,BorderBrush=new SolidColorBrush(Color.FromArgb(60,Accent.R,Accent.G,Accent.B)),BorderThickness=new Thickness(1),Padding=new Thickness(10,6,10,6),HorizontalAlignment=HorizontalAlignment.Left,Child=new TextBlock{Text=shortcut,FontSize=13,FontWeight=FontWeights.SemiBold,Foreground=new SolidColorBrush(Accent)}});
            var label=new TextBlock{Text=L(desc),FontSize=14,Foreground=Fg,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(14,0,0,0)};
            Grid.SetColumn(label,1);row.Children.Add(label);
            list.Children.Add(row);
        }
        panel.Children.Add(list);
        foreach(var tip in new[]{"雙擊系統匣圖標或點擊懸浮球可打開主控制台","所有功能都能在主控制台與設定頁找到"})
            panel.Children.Add(new TextBlock{Text="•  "+L(tip),FontSize=12.5,Foreground=FgMuted,HorizontalAlignment=HorizontalAlignment.Center,Margin=new Thickness(0,6,0,0)});
        return panel;
    }
}
