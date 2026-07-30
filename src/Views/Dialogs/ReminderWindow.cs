using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using Button=System.Windows.Controls.Button;
using Orientation=System.Windows.Controls.Orientation;
using HorizontalAlignment=System.Windows.HorizontalAlignment;

namespace BeeX.DeskNest;

sealed class ReminderWindow:Window
{
    bool decided;
    public ReminderWindow(TodoItem todo,int offset,AppState state,Action complete,Action<int> snooze,Action dismiss)
    {
        Width=430;Height=285;WindowStartupLocation=WindowStartupLocation.CenterScreen;WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;ShowInTaskbar=true;Topmost=true;ResizeMode=ResizeMode.NoResize;
        System.Media.SystemSounds.Exclamation.Play();
        var dark=state.Theme=="Dark";var foreground=dark?Brushes.White:new SolidColorBrush(Color.FromRgb(13,19,33));var alpha=(byte)Math.Clamp(state.WidgetOpacity*255,0,255);var surface=dark?Color.FromArgb(alpha,22,29,45):state.Theme=="Honey"?Color.FromArgb(alpha,255,244,222):Color.FromArgb(alpha,247,248,250);
        var border=new Border{CornerRadius=new CornerRadius(20),Background=new SolidColorBrush(surface),BorderBrush=new SolidColorBrush(Color.FromRgb(255,138,0)),BorderThickness=new Thickness(1),Padding=new Thickness(24),ClipToBounds=true,SnapsToDevicePixels=true};
        var T=(Func<string,string>)(v=>Localization.T(v,state.Language));
        var lead=T(offset switch{10080=>"提前一週",4320=>"提前三天",1440=>"提前一天",60=>"提前一小時",1=>"提前一分鐘",0=>"準時",_=>"延後提醒"});var root=new StackPanel();root.Children.Add(new TextBlock{Text=$"{T("BeeX 待辦提醒")} · {lead}",Foreground=new SolidColorBrush(Color.FromRgb(255,138,0)),FontSize=14,FontWeight=FontWeights.SemiBold});root.Children.Add(new TextBlock{Text=todo.Text,Foreground=foreground,FontSize=20,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,MaxHeight=72,Margin=new Thickness(0,10,0,5)});root.Children.Add(new TextBlock{Text=$"{T("截止時間")}  {todo.DueAt:yyyy/MM/dd HH:mm}",Foreground=dark?Brushes.LightGray:Brushes.Gray,Margin=new Thickness(0,0,0,16)});
        var snoozeRow=new StackPanel{Orientation=Orientation.Horizontal};foreach(var option in new[]{("10 分鐘後",10),("30 分鐘後",30),("1 小時後",60)}){var button=new Button{Content=T(option.Item1),MinWidth=96};button.Click+=(_,_)=>Choose(()=>snooze(option.Item2));snoozeRow.Children.Add(button);}root.Children.Add(snoozeRow);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};var stop=new Button{Content=T("關閉"),MinWidth=92};stop.Click+=(_,_)=>Choose(dismiss);var done=new Button{Content=T("完成"),MinWidth=88,Background=new SolidColorBrush(Color.FromRgb(255,138,0)),Foreground=Brushes.White};done.Click+=(_,_)=>Choose(complete);actions.Children.Add(stop);actions.Children.Add(done);root.Children.Add(actions);border.Child=root;Content=border;
        Closing+=(_,_)=>{if(!decided){decided=true;dismiss();}};
    }
    void Choose(Action action){if(decided)return;decided=true;action();Close();}
}
