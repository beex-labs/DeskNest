using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Button=System.Windows.Controls.Button;
using TextBox=System.Windows.Controls.TextBox;
using ComboBox=System.Windows.Controls.ComboBox;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using Orientation=System.Windows.Controls.Orientation;
using HorizontalAlignment=System.Windows.HorizontalAlignment;
using Image=System.Windows.Controls.Image;
using FontFamily=System.Windows.Media.FontFamily;
using Brush=System.Windows.Media.Brush;
using Shape=System.Windows.Shapes.Shape;
using UniformGrid=System.Windows.Controls.Primitives.UniformGrid;

namespace BeeX.DeskNest;

static class BeeXTodoDialog
{
 static readonly string[] Palette=["#D92D20","#F79009","#FEC84B","#12B76A","#2E90FA","#7F56D9","#EE46BC","#667085"];
 public static bool Edit(Window owner,AppState state,TodoItem todo)
 {
  string L(string s)=>Localization.T(s,state.Language);
  var dark=state.Theme=="Dark";var alpha=(byte)Math.Clamp(state.WidgetOpacity*255,0,255);var foreground=dark?Brushes.White:new SolidColorBrush(Color.FromRgb(13,19,33));var surface=new SolidColorBrush(dark?Color.FromArgb(alpha,13,19,33):state.Theme=="Honey"?Color.FromArgb(alpha,255,244,222):Color.FromArgb(alpha,245,247,250));
  var window=new Window{Title=L("待辦詳情"),Width=560,Height=650,Owner=owner,WindowStartupLocation=WindowStartupLocation.CenterOwner,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,ShowInTaskbar=false,Foreground=foreground,FontSize=state.GlobalFontSize};var border=new Border{CornerRadius=new CornerRadius(state.CornerRadius),Background=surface,BorderBrush=new SolidColorBrush(Color.FromArgb(125,255,138,0)),BorderThickness=new Thickness(1),ClipToBounds=true};var layout=new Grid();layout.RowDefinitions.Add(new RowDefinition{Height=new GridLength(TitleBarMetrics.Dip(owner))});layout.RowDefinitions.Add(new RowDefinition());layout.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});
  var header=new Grid{Margin=new Thickness(18,0,10,0),Background=Brushes.Transparent};header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});var brand=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};brand.Children.Add(new Image{Source=new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")),Width=23,Height=23});brand.Children.Add(new TextBlock{Text=L("待辦詳情"),Foreground=foreground,FontWeight=FontWeights.SemiBold,FontSize=17,Margin=new Thickness(9,0,0,0),VerticalAlignment=VerticalAlignment.Center});header.Children.Add(brand);var close=new Button{Content="\uE8BB",FontFamily=new FontFamily("Segoe MDL2 Assets"),Width=38,Height=38,Padding=new Thickness(0),Background=Brushes.Transparent,Foreground=foreground,BorderThickness=new Thickness(0)};close.Click+=(_,_)=>window.DialogResult=false;Grid.SetColumn(close,1);header.Children.Add(close);header.MouseLeftButtonDown+=(_,e)=>{if(e.LeftButton==MouseButtonState.Pressed)window.DragMove();};layout.Children.Add(header);
  var panel=new StackPanel{Margin=new Thickness(28,10,28,24)};
  panel.Children.Add(Label(L("任務內容（最多 10 行）"),foreground));var text=new TextBox{Text=todo.Text,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=145,Margin=new Thickness(0,7,0,14),Padding=new Thickness(10),Background=dark?new SolidColorBrush(Color.FromArgb(35,255,255,255)):new SolidColorBrush(Color.FromArgb(170,255,255,255)),Foreground=foreground};panel.Children.Add(text);
  panel.Children.Add(Label("Deadline",foreground));var dateRow=new UniformGrid{Columns=5,Margin=new Thickness(0,7,0,12)};ComboBox year=Box(90),month=Box(60),day=Box(60),hour=Box(60),minute=Box(60);var baseDate=todo.DueAt??DateTime.Today.AddDays(1).AddHours(18);for(var y=DateTime.Today.Year-2;y<=DateTime.Today.Year+20;y++)year.Items.Add(y);for(var n=1;n<=12;n++)month.Items.Add(n);for(var n=1;n<=31;n++)day.Items.Add(n);for(var n=0;n<24;n++)hour.Items.Add(n.ToString("00"));for(var n=0;n<60;n+=5)minute.Items.Add(n.ToString("00"));year.SelectedItem=baseDate.Year;month.SelectedItem=baseDate.Month;day.SelectedItem=baseDate.Day;hour.SelectedItem=baseDate.Hour.ToString("00");minute.SelectedItem=(baseDate.Minute/5*5).ToString("00");foreach(var x in new[]{year,month,day,hour,minute})dateRow.Children.Add(x);panel.Children.Add(dateRow);
  panel.Children.Add(Label(L("重複"),foreground));var repeats=new UniformGrid{Columns=5,Margin=new Thickness(0,7,0,14)};string selectedRepeat=todo.Repeat;foreach(var value in new[]{"不重複","每天","每週","每兩週","每月"}){var b=new Button{Content=L(value),Tag=value,Margin=new Thickness(2),Background=value==selectedRepeat?Orange():Soft(dark),Foreground=value==selectedRepeat?Brushes.White:foreground};b.Click+=(_,_)=>{selectedRepeat=value;foreach(Button x in repeats.Children){var on=x.Tag?.ToString()==value;x.Background=on?Orange():Soft(dark);x.Foreground=on?Brushes.White:foreground;}};repeats.Children.Add(b);}panel.Children.Add(repeats);
  panel.Children.Add(Label(L("提醒時間（可多選）"),foreground));var reminderChoices=new (string Label,int Minutes)[]{("一週前",10080),("三天前",4320),("一天前",1440),("一小時前",60),("一分鐘前",1),("準時",0)};var selectedReminders=todo.ReminderOffsets.ToHashSet();var reminders=new UniformGrid{Columns=3,Margin=new Thickness(0,7,0,14)};foreach(var choice in reminderChoices){var b=new Button{Content=L(choice.Label),Tag=choice.Minutes,Background=selectedReminders.Contains(choice.Minutes)?Orange():Soft(dark),Foreground=selectedReminders.Contains(choice.Minutes)?Brushes.White:foreground,Margin=new Thickness(2)};b.Click+=(_,_)=>{if(!selectedReminders.Add(choice.Minutes))selectedReminders.Remove(choice.Minutes);var on=selectedReminders.Contains(choice.Minutes);b.Background=on?Orange():Soft(dark);b.Foreground=on?Brushes.White:foreground;};reminders.Children.Add(b);}panel.Children.Add(reminders);
  // 標記顏色：圓形色塊，選中顯示白色✓與白邊
  panel.Children.Add(Label(L("標記顏色"),foreground));var swatches=new UniformGrid{Columns=8,Margin=new Thickness(0,7,0,16),Height=40};string selectedColor=todo.Color;var circle=CircleSwatchTemplate();foreach(var value in Palette){var on0=value==selectedColor;var b=new Button{Tag=value,Template=circle,Width=30,Height=30,HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Content=on0?"✓":"",Foreground=Brushes.White,FontWeight=FontWeights.Bold,FontSize=15,Background=(Brush)new BrushConverter().ConvertFromString(value)!,BorderBrush=Brushes.White,BorderThickness=new Thickness(on0?3:0)};b.Click+=(_,_)=>{selectedColor=value;foreach(Button x in swatches.Children){var on=x.Tag?.ToString()==value;x.Content=on?"✓":"";x.BorderThickness=new Thickness(on?3:0);}};swatches.Children.Add(b);}panel.Children.Add(swatches);
  // 附件：明確的「添加附件」按鈕 + 已添加列表（可逐項移除）
  var attachments=todo.Attachments.ToList();
  var attachHeader=new Grid{Margin=new Thickness(0,0,0,6)};attachHeader.ColumnDefinitions.Add(new ColumnDefinition());attachHeader.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});attachHeader.Children.Add(Label(L("附件"),foreground));var addBtn=new Button{Content="＋ "+L("添加附件"),Background=Soft(dark),Foreground=foreground};Grid.SetColumn(addBtn,1);attachHeader.Children.Add(addBtn);panel.Children.Add(attachHeader);
  var attachList=new StackPanel{Margin=new Thickness(0,0,0,10)};panel.Children.Add(attachList);
  void RefreshAttach()
  {
   attachList.Children.Clear();
   if(attachments.Count==0){attachList.Children.Add(new TextBlock{Text=L("尚無附件"),Foreground=new SolidColorBrush(Color.FromArgb(160,128,128,128)),FontSize=12,Margin=new Thickness(2,2,0,0)});return;}
   foreach(var file in attachments.ToList())
   {
    var row=new Grid{Margin=new Thickness(0,2,0,2)};row.ColumnDefinitions.Add(new ColumnDefinition());row.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});
    var name=new TextBlock{Text=System.IO.Path.GetFileName(file),Foreground=foreground,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis,ToolTip=file};row.Children.Add(name);
    var remove=new Button{Content="✕",Width=26,Height=26,Padding=new Thickness(0),FontSize=12,Background=Soft(dark),Foreground=foreground};Grid.SetColumn(remove,1);var captured=file;remove.Click+=(_,_)=>{attachments.Remove(captured);RefreshAttach();};row.Children.Add(remove);
    attachList.Children.Add(row);
   }
  }
  addBtn.Click+=(_,_)=>{using var dialog=new System.Windows.Forms.OpenFileDialog{Multiselect=true};if(dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK){attachments.AddRange(dialog.FileNames);RefreshAttach();}};
  RefreshAttach();
  var scroll=new ScrollViewer{Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(scroll,1);layout.Children.Add(scroll);
  var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(28,10,28,18)};Grid.SetRow(actions,2);var cancel=new Button{Content=L("取消"),MinWidth=90,Background=Soft(dark),Foreground=foreground};cancel.Click+=(_,_)=>window.DialogResult=false;var save=new Button{Content=L("保存"),MinWidth=90,Margin=new Thickness(10,0,0,0),Background=Orange(),Foreground=Brushes.White};save.Click+=(_,_)=>window.DialogResult=true;actions.Children.Add(cancel);actions.Children.Add(save);layout.Children.Add(actions);border.Child=layout;window.Content=border;window.Loaded+=(_,_)=>WindowRegionHelper.ApplyDeferred(window,state.CornerRadius);
  if(window.ShowDialog()!=true)return false;var yv=(int)year.SelectedItem;var mv=(int)month.SelectedItem;var dv=Math.Min((int)day.SelectedItem,DateTime.DaysInMonth(yv,mv));var due=new DateTime(yv,mv,dv,int.Parse((string)hour.SelectedItem),int.Parse((string)minute.SelectedItem),0);var changed=todo.DueAt!=due||!todo.ReminderOffsets.OrderBy(x=>x).SequenceEqual(selectedReminders.OrderBy(x=>x));todo.Text=string.Join(Environment.NewLine,text.Text.Split('\n').Take(10));todo.DueAt=due;todo.Repeat=selectedRepeat;todo.Color=selectedColor;todo.Attachments=attachments;todo.ReminderOffsets=selectedReminders.OrderByDescending(x=>x).ToList();if(changed){todo.ReminderDismissed=false;todo.SnoozeUntil=null;todo.SentReminderOffsets.Clear();todo.DeadlineNotice1DaySent=false;todo.DeadlineNotice2DaysSent=false;}return true;
 }
 // 圓形色塊模板：Ellipse 填充=Background，白色描邊=BorderBrush(選中時可見)，中心顯示✓
 static ControlTemplate CircleSwatchTemplate()
 {
  var t=new ControlTemplate(typeof(Button));
  var grid=new FrameworkElementFactory(typeof(Grid));
  var ell=new FrameworkElementFactory(typeof(Ellipse));
  ell.SetValue(Shape.FillProperty,new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BackgroundProperty));
  ell.SetValue(Shape.StrokeProperty,new System.Windows.TemplateBindingExtension(System.Windows.Controls.Control.BorderBrushProperty));
  ell.SetValue(Shape.StrokeThicknessProperty,3.0);
  grid.AppendChild(ell);
  var cp=new FrameworkElementFactory(typeof(ContentPresenter));
  cp.SetValue(FrameworkElement.HorizontalAlignmentProperty,HorizontalAlignment.Center);
  cp.SetValue(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center);
  grid.AppendChild(cp);
  t.VisualTree=grid;
  return t;
 }
 static ComboBox Box(double width)=>new(){MinWidth=width,Margin=new Thickness(3)};
 static TextBlock Label(string value,Brush brush)=>new(){Text=value,Foreground=brush,FontWeight=FontWeights.SemiBold};
 static Brush Orange()=>new SolidColorBrush(Color.FromRgb(255,138,0));
 static Brush Soft(bool dark)=>dark?new SolidColorBrush(Color.FromArgb(35,255,255,255)):new SolidColorBrush(Color.FromRgb(255,243,229));
}
