using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brush=System.Windows.Media.Brush;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using Colors=System.Windows.Media.Colors;
using Button=System.Windows.Controls.Button;
using Orientation=System.Windows.Controls.Orientation;
using HorizontalAlignment=System.Windows.HorizontalAlignment;

namespace BeeX.DeskNest;

static class BeeXDialog
{
    public static bool Confirm(Window? owner,string title,string message,AppState state,string confirmText="刪除",bool destructive=true,bool showCancel=true)
    {
        var lang=state.Language;title=Localization.T(title,lang);message=Localization.T(message,lang);
        var dark=state.Theme=="Dark";var honey=state.Theme=="Honey";var opacity=(byte)Math.Clamp(state.WidgetOpacity*255,0,255);
        var foreground=dark?Brushes.White:new SolidColorBrush(Color.FromRgb(13,19,33));
        var surface=new SolidColorBrush(dark?Color.FromArgb(opacity,22,29,45):honey?Color.FromArgb(opacity,255,244,222):Color.FromArgb(opacity,250,251,252));
        var dialog=new Window{Title=title,Width=410,Height=218,Owner=owner,WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,Topmost=owner==null};
        var border=new Border{CornerRadius=new CornerRadius(state.CornerRadius),Background=surface,BorderBrush=new SolidColorBrush(Color.FromArgb(115,255,138,0)),BorderThickness=new Thickness(1),Padding=new Thickness(24),ClipToBounds=true,SnapsToDevicePixels=true};
        var root=new Grid();root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var heading=new TextBlock{Text=title,Foreground=foreground,FontSize=20,FontWeight=FontWeights.SemiBold};
        var body=new TextBlock{Text=message,Foreground=dark?new SolidColorBrush(Color.FromRgb(210,215,225)):new SolidColorBrush(Color.FromRgb(77,87,104)),FontSize=14,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,18)};Grid.SetRow(body,1);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content=Localization.T("取消",lang),MinWidth=88,Background=dark?new SolidColorBrush(Color.FromArgb(45,255,255,255)):new SolidColorBrush(Color.FromRgb(255,243,229)),Foreground=foreground};cancel.Click+=(_,_)=>{dialog.DialogResult=false;dialog.Close();};
        var confirm=new Button{Content=Localization.T(confirmText,lang),MinWidth=88,Background=new SolidColorBrush(destructive?Color.FromRgb(217,45,32):Color.FromRgb(255,138,0)),Foreground=Brushes.White};confirm.Click+=(_,_)=>{dialog.DialogResult=true;dialog.Close();};if(showCancel)actions.Children.Add(cancel);actions.Children.Add(confirm);Grid.SetRow(actions,2);
        root.Children.Add(heading);root.Children.Add(body);root.Children.Add(actions);border.Child=root;dialog.Content=border;
        dialog.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape){dialog.DialogResult=false;dialog.Close();}};
        return dialog.ShowDialog()==true;
    }

    public static void Alert(Window? owner,string title,string message,AppState state)
    {
        Confirm(owner,title,message,state,"確定",false,false);
    }

    public static void Notify(Window? owner,string title,string message,AppState state)
    {
        var lang=state.Language;title=Localization.T(title,lang);message=Localization.T(message,lang);
        var dark=state.Theme=="Dark";var honey=state.Theme=="Honey";var opacity=(byte)Math.Clamp(state.WidgetOpacity*255,0,255);
        var foreground=dark?Brushes.White:new SolidColorBrush(Color.FromRgb(13,19,33));
        var surface=new SolidColorBrush(dark?Color.FromArgb(opacity,22,29,45):honey?Color.FromArgb(opacity,255,244,222):Color.FromArgb(opacity,250,251,252));
        var dialog=new Window{Title=title,Width=410,Height=218,Owner=owner,WindowStartupLocation=owner==null?WindowStartupLocation.CenterScreen:WindowStartupLocation.CenterOwner,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,Topmost=owner==null};
        var border=new Border{CornerRadius=new CornerRadius(state.CornerRadius),Background=surface,BorderBrush=new SolidColorBrush(Color.FromArgb(115,255,138,0)),BorderThickness=new Thickness(1),Padding=new Thickness(24),ClipToBounds=true,SnapsToDevicePixels=true};
        var root=new Grid();root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});root.RowDefinitions.Add(new RowDefinition());root.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
        var heading=new TextBlock{Text=title,Foreground=foreground,FontSize=20,FontWeight=FontWeights.SemiBold};
        var body=new TextBlock{Text=message,Foreground=dark?new SolidColorBrush(Color.FromRgb(210,215,225)):new SolidColorBrush(Color.FromRgb(77,87,104)),FontSize=14,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,14,0,18)};Grid.SetRow(body,1);
        var ok=new Button{Content=Localization.T("確定",state.Language),MinWidth=88,Background=new SolidColorBrush(Color.FromRgb(255,138,0)),Foreground=Brushes.White,HorizontalAlignment=HorizontalAlignment.Right};ok.Click+=(_,_)=>dialog.Close();Grid.SetRow(ok,2);
        root.Children.Add(heading);root.Children.Add(body);root.Children.Add(ok);border.Child=root;dialog.Content=border;
        border.MouseLeftButtonDown+=(_,e)=>{if(e.LeftButton==System.Windows.Input.MouseButtonState.Pressed)try{dialog.DragMove();}catch{}};
        dialog.KeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape)dialog.Close();};
        dialog.Show();
        dialog.Activate();
    }
}
