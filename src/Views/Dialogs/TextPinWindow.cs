using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using Cursors=System.Windows.Input.Cursors;
using Clipboard=System.Windows.Clipboard;
using MouseButtonState=System.Windows.Input.MouseButtonState;
using WpfContextMenu=System.Windows.Controls.ContextMenu;
using WpfMenuItem=System.Windows.Controls.MenuItem;

namespace BeeX.DeskNest;

/// <summary>
/// 「盯住文字」窗口：把剪貼板中的任意文字釘在螢幕上，始終置頂、可拖動、雙擊關閉，
/// 右鍵提供複製 / 關閉。純浮層，不參與截圖。
/// </summary>
public sealed class TextPinWindow : Window
{
    TextPinWindow(string text)
    {
        WindowStyle=WindowStyle.None;
        ResizeMode=ResizeMode.NoResize;
        ShowInTaskbar=false;
        Topmost=true;
        AllowsTransparency=true;
        Background=Brushes.Transparent;
        SizeToContent=SizeToContent.WidthAndHeight;

        var block=new TextBlock
        {
            Text=text,
            Foreground=Brushes.White,
            TextWrapping=TextWrapping.Wrap,
            MaxWidth=460,
            FontSize=15,
            LineHeight=22,
            LineStackingStrategy=LineStackingStrategy.BlockLineHeight
        };
        var scroll=new ScrollViewer
        {
            Content=block,
            MaxHeight=560,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled
        };
        var card=new Border
        {
            Background=new SolidColorBrush(Color.FromArgb(240,13,19,33)),
            BorderBrush=new SolidColorBrush(Color.FromArgb(220,255,138,0)),
            BorderThickness=new Thickness(1.5),
            CornerRadius=new CornerRadius(10),
            Padding=new Thickness(14,12,14,12),
            Cursor=Cursors.SizeAll,
            Child=scroll,
            Effect=new System.Windows.Media.Effects.DropShadowEffect{Color=Color.FromRgb(255,138,0),BlurRadius=18,ShadowDepth=0,Opacity=0.5}
        };
        Content=card;

        MouseLeftButtonDown+=(_,e)=>
        {
            if(e.ClickCount==2){Close();return;}
            if(e.LeftButton==MouseButtonState.Pressed){try{DragMove();}catch{}}
        };

        var menu=new WpfContextMenu{Background=new SolidColorBrush(Color.FromArgb(236,13,19,33)),Foreground=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromArgb(160,255,138,0)),BorderThickness=new Thickness(1)};
        var copy=new WpfMenuItem{Header=Localization.T("複製文字",Localization.CurrentLanguage),Foreground=Brushes.White};
        copy.Click+=(_,_)=>{try{Clipboard.SetText(text);}catch{}};
        var close=new WpfMenuItem{Header=Localization.T("關閉",Localization.CurrentLanguage),Foreground=Brushes.White};
        close.Click+=(_,_)=>Close();
        menu.Items.Add(copy);menu.Items.Add(close);
        ContextMenu=menu;

        KeyDown+=(_,e)=>{if(e.Key==Key.Escape)Close();};
    }

    /// <summary>讀取傳入文字並在螢幕上釘一個置頂浮層。</summary>
    public static void Pin(string text)
    {
        if(string.IsNullOrWhiteSpace(text))return;
        var window=new TextPinWindow(text);
        window.Loaded+=(_,_)=>
        {
            var area=SystemParameters.WorkArea;
            window.Left=area.Left+Math.Max(0,(area.Width-window.ActualWidth)/2);
            window.Top=area.Top+Math.Max(0,(area.Height-window.ActualHeight)/3);
        };
        window.Show();
        window.Activate();
    }
}
