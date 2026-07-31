using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;
using TextBox=System.Windows.Controls.TextBox;
using HorizontalAlignment=System.Windows.HorizontalAlignment;
using MessageBox=System.Windows.MessageBox;
using CheckBox=System.Windows.Controls.CheckBox;
using Button=System.Windows.Controls.Button;
using ComboBox=System.Windows.Controls.ComboBox;
using Image=System.Windows.Controls.Image;
using Orientation=System.Windows.Controls.Orientation;
using Control=System.Windows.Controls.Control;
using System.Windows.Interop;

namespace BeeX.DeskNest;
public partial class SettingsWindow:Window
{
 readonly DeskNestService service;
 bool loading;
 bool featureListLoading;
 Border? settingsRoot;
 Grid? settingsBody;
 TextBlock? settingsTitleText;
 readonly System.Windows.Threading.DispatcherTimer preferencePreviewTimer=new(){Interval=TimeSpan.FromMilliseconds(90)};
 bool preferencePreviewPending;
 public SettingsWindow(DeskNestService service){InitializeComponent();this.service=service;ConfigureTransparentChrome();MaximizeRestore.Attach(this);Nav.SelectedIndex=0;BeeXRootPathBox.Text=BeeXPaths.Root;SourceInitialized+=(_,_)=>ApplyPreferences();FfmpegInstallerService.ProgressChanged+=p=>{UpdateComponentProgress(FfmpegProgressBar,FfmpegStatusText,p,"正在下載 ffmpeg 元件…");if(FfmpegDownloadBtn!=null)FfmpegDownloadBtn.IsEnabled=false;};FfmpegInstallerService.InstallFinished+=_=>RefreshFfmpegStatus();OcrInstallerService.ProgressChanged+=p=>{UpdateComponentProgress(OcrProgressBar,OcrStatusText,p,"正在下載 OCR 元件…");if(OcrDownloadBtn!=null)OcrDownloadBtn.IsEnabled=false;};OcrInstallerService.InstallFinished+=_=>RefreshOcrStatus();Closing+=(_,e)=>{e.Cancel=true;Hide();};}
 string SettingsTitle()=>service.State.Language=="zh-CN"?"BeeX DeskNest 设置":service.State.Language=="en-US"?"BeeX DeskNest Settings":"BeeX DeskNest 設定";
 void ConfigureTransparentChrome(){settingsBody=(Grid)Content;Content=null;WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=System.Windows.Media.Brushes.Transparent;settingsBody.Background=System.Windows.Media.Brushes.Transparent;var shell=new Grid();shell.RowDefinitions.Add(new RowDefinition{Height=new GridLength(44)});shell.RowDefinitions.Add(new RowDefinition());var title=new Grid{Margin=new Thickness(14,0,8,0)};title.ColumnDefinitions.Add(new ColumnDefinition());title.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});var brand=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};brand.Children.Add(new Image{Source=new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")),Width=20,Height=20});settingsTitleText=new TextBlock{Text=SettingsTitle(),Margin=new Thickness(8,0,0,0),VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold};brand.Children.Add(settingsTitleText);title.Children.Add(brand);var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};var minimize=new Button{Content="−",Width=40,Height=40,Padding=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,ToolTip=Localization.T("最小化",service.State.Language)};minimize.Click+=(_,_)=>WindowState=WindowState.Minimized;var close=new Button{Content="×",Width=40,Height=40,Padding=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,ToolTip=Localization.T("關閉",service.State.Language)};close.Click+=(_,_)=>Hide();actions.Children.Add(minimize);actions.Children.Add(close);Grid.SetColumn(actions,1);title.Children.Add(actions);title.MouseLeftButtonDown+=(_,e)=>{if(InputHitTestHelper.IsInteractive(e.OriginalSource as DependencyObject))return;if(e.LeftButton==MouseButtonState.Pressed)DragMove();};shell.Children.Add(title);Grid.SetRow(settingsBody,1);shell.Children.Add(settingsBody);AddSettingsResizeGrip(shell);settingsRoot=new Border{CornerRadius=new CornerRadius(service.State.CornerRadius),BorderBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(90,255,138,0)),BorderThickness=new Thickness(1),ClipToBounds=true,Child=shell};Content=settingsRoot;System.Windows.Shell.WindowChrome.SetWindowChrome(this,new System.Windows.Shell.WindowChrome{ResizeBorderThickness=new Thickness(10),CaptionHeight=0,CornerRadius=new CornerRadius(service.State.CornerRadius),GlassFrameThickness=new Thickness(0),UseAeroCaptionButtons=false});}
 void AddSettingsResizeGrip(Grid shell){var grip=new System.Windows.Controls.Primitives.Thumb{Width=56,Height=56,HorizontalAlignment=HorizontalAlignment.Right,VerticalAlignment=VerticalAlignment.Bottom,Cursor=System.Windows.Input.Cursors.SizeNWSE,Background=System.Windows.Media.Brushes.Transparent,ToolTip=Localization.T("拖動調整大小",service.State.Language)};var template=new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb));var visual=new FrameworkElementFactory(typeof(TextBlock));visual.SetValue(TextBlock.TextProperty,"◢");visual.SetValue(TextBlock.ForegroundProperty,new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(185,255,138,0)));visual.SetValue(TextBlock.FontSizeProperty,18d);visual.SetValue(TextBlock.HorizontalAlignmentProperty,HorizontalAlignment.Right);visual.SetValue(TextBlock.VerticalAlignmentProperty,VerticalAlignment.Bottom);visual.SetValue(TextBlock.MarginProperty,new Thickness(0,0,7,5));template.VisualTree=visual;grip.Template=template;System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(grip,true);grip.PreviewMouseLeftButtonDown+=(_,e)=>{ReleaseCapture();SendMessage(new WindowInteropHelper(this).Handle,0x00A1,(IntPtr)17,IntPtr.Zero);e.Handled=true;};Grid.SetRowSpan(grip,2);System.Windows.Controls.Panel.SetZIndex(grip,999);shell.Children.Add(grip);}
 protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e){base.OnPreviewMouseLeftButtonDown(e);if(e.ClickCount!=1||e.GetPosition(this).Y>44||InputHitTestHelper.IsInteractive(e.OriginalSource as DependencyObject))return;e.Handled=true;DragMove();}
 protected override void OnContentRendered(EventArgs e){base.OnContentRendered(e);WindowRegionHelper.StyleCaptionButtons(this);WindowRegionHelper.ApplyDeferred(this,service.State.CornerRadius);}
 protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo){base.OnRenderSizeChanged(sizeInfo);WindowRegionHelper.ApplyDeferred(this,service.State.CornerRadius);}
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool ReleaseCapture();
 [System.Runtime.InteropServices.DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr hWnd,int msg,IntPtr wParam,IntPtr lParam);
 protected override void OnInitialized(EventArgs e)
 {
  base.OnInitialized(e);
  preferencePreviewTimer.Tick+=(_,_)=>{preferencePreviewTimer.Stop();if(!preferencePreviewPending)return;preferencePreviewPending=false;service.Save();};
 }
 
 public void LoadState(){loading=true;var s=service.State;Startup.IsChecked=s.StartWithWindows;ReminderSummary.IsChecked=s.ShowReminderSummary;FloatingBallEnabled.IsChecked=s.ShowFloatingBall;FloatingBallSnap.IsChecked=s.FloatingBallSnapToEdge;CollapsedLogo.IsChecked=s.ShowCollapsedLogo;FloatingBallOpacitySlider.Value=service.EffectiveFloatingBallOpacity();AlignToGrid.IsChecked=s.AlignWidgetsToGrid;SearchPaletteGuide.IsChecked=s.ShowSearchPaletteGuide;GridSizeSlider.Value=s.WidgetGridSize;GridSizeValue.Text=$"{s.WidgetGridSize:0} px";Theme.SelectedIndex=s.Theme=="Honey"?1:s.Theme=="Dark"?2:0;OpacitySlider.Value=1-s.WidgetOpacity;SetFontFamilySelection(InterfaceFontFamily,service.InterfaceFontFamily());InterfaceFontSlider.Value=service.InterfaceFontSize();SetFontFamilySelection(GlobalFontFamily,service.ContentFontFamily());FontSlider.Value=service.ContentFontSize();GlobalFontColor.Text=ColorUtils.NormalizeHexColor(s.GlobalFontColor,s.Theme=="Dark"?"#FFFFFF":"#0D1321");CornerSlider.Value=s.CornerRadius;IconSlider.Value=s.IconSize;SpacingSlider.Value=s.ItemSpacing;Extensions.IsChecked=s.ShowFileExtensions;ClipboardImagePath.Text=service.ClipboardImageDirectory;ScreenshotPath.Text=service.ScreenshotDirectory;DeepLApiKeyBox.Text=UserConfigHelper.ReadDeepLApiKey();LoadTranslateTarget();WeatherDefaultCity.Text=service.State.Nests.FirstOrDefault(n=>n.Kind==NestKind.Weather)?.City??"深圳";SelectByTag(CaptureFormatBox,s.CaptureDefaultFormat);CaptureCopyToClipboard.IsChecked=s.CaptureCopyOnSave;SelectByTag(RecordFpsBox,s.RecordingDefaultFps.ToString());SelectByTag(RecordDelayBox,s.RecordingCountdownSec.ToString());CaptureLimitSlider.Value=Math.Clamp(s.CaptureLimit,20,500);CaptureLimitValue.Text=((int)CaptureLimitSlider.Value).ToString();SelectByTag(WeatherRefreshBox,s.WeatherRefreshMinutes.ToString());SharedBackground.IsChecked=s.UseSharedWidgetBackground;TodoRemWeek.IsChecked=s.TodoDefaultReminderOffsets.Contains(10080);TodoRem3Day.IsChecked=s.TodoDefaultReminderOffsets.Contains(4320);TodoRemDay.IsChecked=s.TodoDefaultReminderOffsets.Contains(1440);TodoRemHour.IsChecked=s.TodoDefaultReminderOffsets.Contains(60);TodoRemOnTime.IsChecked=s.TodoDefaultReminderOffsets.Contains(0);Title=SettingsTitle();if(settingsTitleText!=null)settingsTitleText.Text=SettingsTitle();Localization.Apply(this,s.Language);SetLanguageSelection();BuildHotkeyEditors();BuildMusicGlobalSettings();RefreshFeatureNests();RefreshFfmpegStatus();RefreshOcrStatus();loading=false;RefreshNavIcons();ApplyPreferences();}
 sealed record LanguageOption(string Code,string Name){public override string ToString()=>Name;}
 static string LanguageDisplayName(string code,string uiLanguage)=>uiLanguage=="en-US"?code switch{"zh-TW"=>"Traditional Chinese","zh-CN"=>"Simplified Chinese",_=>"English"}:uiLanguage=="zh-CN"?code switch{"zh-TW"=>"繁体中文","zh-CN"=>"简体中文",_=>"English"}:code switch{"zh-TW"=>"繁體中文","zh-CN"=>"簡體中文",_=>"English"};
 void BuildLanguageOptions()
 {
  var lang=service.State.Language;
  var options=new List<LanguageOption>{new(lang,LanguageDisplayName(lang,lang))};
  foreach(var code in new[]{"zh-TW","zh-CN","en-US"})if(code!=lang)options.Add(new LanguageOption(code,LanguageDisplayName(code,lang)));
  loading=true;
  LanguageCombo.ItemsSource=options;
  LanguageCombo.SelectedValue=lang;
  loading=false;
 }
 void SetLanguageSelection()=>BuildLanguageOptions();
 public void RefreshLanguage(){Title=SettingsTitle();if(settingsTitleText!=null)settingsTitleText.Text=SettingsTitle();var names=new[]{"常規","外觀","文件格子","功能格子","待辦提醒","隨記圖庫","截圖與錄屏","音樂歌詞","天氣","快捷與交互","BeeX 清理","診斷與維護","關於"};Localization.Apply(this,service.State.Language);loading=true;SetLanguageSelection();loading=false;BuildHotkeyEditors();BuildMusicGlobalSettings();RefreshFeatureNests();RefreshNavIcons();PageTitle.Text=Localization.T(names[Math.Max(0,Nav.SelectedIndex)],service.State.Language);ApplyPreferences();}
  public void ApplyPreferences(){var st=service.State;var dark=st.Theme=="Dark";var honey=st.Theme=="Honey";var alpha=(byte)Math.Clamp(st.WidgetOpacity*255,0,255);var uniform=dark?System.Windows.Media.Color.FromArgb(alpha,13,19,33):honey?System.Windows.Media.Color.FromArgb(alpha,255,244,222):System.Windows.Media.Color.FromArgb(alpha,245,247,250);var uniformBrush=new System.Windows.Media.SolidColorBrush(uniform);var text=ContrastHelper.TextFor(uniformBrush,dark?System.Windows.Media.Brushes.White:new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13,19,33)));Background=System.Windows.Media.Brushes.Transparent;Opacity=1;if(settingsRoot!=null){settingsRoot.Background=uniformBrush;settingsRoot.CornerRadius=new CornerRadius(st.CornerRadius);}if(settingsBody!=null){settingsBody.Background=System.Windows.Media.Brushes.Transparent;if(settingsBody.Children[0] is Border side)side.Background=System.Windows.Media.Brushes.Transparent;ApplyThemeText(settingsBody,dark,text);}Foreground=text;Nav.Background=System.Windows.Media.Brushes.Transparent;Nav.Foreground=text;PageTitle.Foreground=text;FontSize=service.InterfaceFontSize();Localization.ApplyFont(this,service.InterfaceFontFamily(),service.InterfaceFontSize());if(System.Windows.PresentationSource.FromVisual(this)!=null)AcrylicHelper.Apply(this,st.Theme=="Acrylic",st.WidgetOpacity,dark);WindowRegionHelper.StyleCaptionButtons(this);RefreshNavIcons();}
 static void ApplyThemeText(DependencyObject root,bool dark,System.Windows.Media.Brush primary){var inputSurface=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(dark?(byte)86:(byte)210,255,255,255));var inputText=dark?System.Windows.Media.Brushes.White:new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13,19,33));var inputBorder=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(dark?(byte)130:(byte)150,255,255,255));for(var i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);i++){var child=System.Windows.Media.VisualTreeHelper.GetChild(root,i);if(child is TextBlock text){var secondary=text.Foreground is System.Windows.Media.SolidColorBrush brush&&(brush.Color==System.Windows.Media.Color.FromRgb(102,112,133)||brush.Color==System.Windows.Media.Color.FromRgb(184,192,207));text.Foreground=secondary?(dark?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(198,205,218)):new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102,112,133))):primary;}else if(child is TextBox box){box.Background=inputSurface;box.Foreground=inputText;box.CaretBrush=inputText;box.BorderBrush=inputBorder;box.MinHeight=34;box.MaxHeight=40;box.Padding=new Thickness(10,5,10,5);box.VerticalContentAlignment=VerticalAlignment.Center;}else if(child is ComboBox combo){combo.Background=inputSurface;combo.Foreground=inputText;combo.BorderBrush=inputBorder;combo.MinHeight=36;combo.MaxHeight=42;combo.Padding=new Thickness(12,6,12,6);combo.VerticalContentAlignment=VerticalAlignment.Center;}else if(child is ComboBoxItem item){item.Background=System.Windows.Media.Brushes.Transparent;item.Foreground=inputText;item.Padding=new Thickness(10,6,10,6);}else if(child is Button button){button.Foreground=ContrastHelper.TextFor(button.Background,primary);}else if(child is Control control)control.Foreground=primary;ApplyThemeText(child,dark,primary);}}
 void Nav_Changed(object s,SelectionChangedEventArgs e){if(!IsInitialized)return;var pages=new[]{GeneralPage,AppearancePage,FilesPage,FeaturesPage,TodoSettingsPage,CaptureSettingsPage,CaptureRecordPage,MusicSettingsPage,WeatherSettingsPage,HotkeysPage,CleanerPage,MaintenancePage,AboutPage};var names=new[]{"常規","外觀","文件格子","功能格子","待辦提醒","隨記圖庫","截圖與錄屏","音樂歌詞","天氣","快捷與交互","BeeX 清理","診斷與維護","關於"};for(int i=0;i<pages.Length;i++)pages[i].Visibility=i==Nav.SelectedIndex?Visibility.Visible:Visibility.Collapsed;PageTitle.Text=Localization.T(names[Math.Max(0,Math.Min(Nav.SelectedIndex,names.Length-1))],service.State.Language);if(Nav.SelectedIndex==3)RefreshFeatureNests();}
 void Changed(object? s,RoutedEventArgs e)
 {
  if(loading||!IsInitialized)return;
  var st=service.State;var prevTheme=st.Theme;
  st.StartWithWindows=Startup.IsChecked==true;st.ShowReminderSummary=ReminderSummary.IsChecked==true;st.ShowFloatingBall=FloatingBallEnabled.IsChecked==true;st.FloatingBallSnapToEdge=FloatingBallSnap.IsChecked==true;st.ShowCollapsedLogo=CollapsedLogo.IsChecked==true;st.FloatingBallOpacity=Math.Clamp(FloatingBallOpacitySlider.Value,.2,1);st.AlignWidgetsToGrid=AlignToGrid.IsChecked==true;st.ShowSearchPaletteGuide=SearchPaletteGuide.IsChecked==true;st.WidgetGridSize=Math.Clamp(GridSizeSlider.Value,10,80);GridSizeValue.Text=$"{st.WidgetGridSize:0} px";st.Theme=((ComboBoxItem?)Theme.SelectedItem)?.Tag?.ToString()??"Acrylic";st.WidgetOpacity=1-OpacitySlider.Value;st.InterfaceFontFamily=SelectedFontFamily(InterfaceFontFamily);st.InterfaceFontSize=InterfaceFontSlider.Value;st.ContentFontFamily=SelectedFontFamily(GlobalFontFamily);st.ContentFontSize=FontSlider.Value;st.GlobalFontFamily=st.ContentFontFamily;st.GlobalFontSize=st.ContentFontSize;st.GlobalFontColor=ColorUtils.NormalizeHexColor(GlobalFontColor.Text,st.Theme=="Dark"?"#FFFFFF":"#0D1321");GlobalFontColor.Text=st.GlobalFontColor;st.CornerRadius=CornerSlider.Value;st.IconSize=IconSlider.Value;st.ItemSpacing=SpacingSlider.Value;st.ShowFileExtensions=Extensions.IsChecked==true;st.UseSharedWidgetBackground=SharedBackground.IsChecked==true;st.CaptureCopyOnSave=CaptureCopyToClipboard.IsChecked==true;st.CaptureDefaultFormat=((ComboBoxItem?)CaptureFormatBox.SelectedItem)?.Tag?.ToString()??"png";st.RecordingDefaultFps=int.TryParse(((ComboBoxItem?)RecordFpsBox.SelectedItem)?.Tag?.ToString(),out var recFps)?recFps:30;st.RecordingCountdownSec=int.TryParse(((ComboBoxItem?)RecordDelayBox.SelectedItem)?.Tag?.ToString(),out var recDelay)?recDelay:0;st.CaptureLimit=(int)Math.Clamp(CaptureLimitSlider.Value,20,500);CaptureLimitValue.Text=st.CaptureLimit.ToString();st.WeatherRefreshMinutes=int.TryParse(((ComboBoxItem?)WeatherRefreshBox.SelectedItem)?.Tag?.ToString(),out var weatherMin)?weatherMin:30;ApplyGlobalTypographyToWidgets();
  // 切換主題時，若全局文字顏色仍是另一主題的默認值，自動翻轉為當前主題默認色（深色→白、其他→深藍），使輸入框與內容文字隨主題可讀。
  if(ReferenceEquals(s,Theme)&&prevTheme!=st.Theme){var fc=ColorUtils.NormalizeHexColor(GlobalFontColor.Text,"#0D1321");if(st.Theme=="Dark"&&fc=="#0D1321"){st.GlobalFontColor="#FFFFFF";GlobalFontColor.Text="#FFFFFF";ApplyGlobalTypographyToWidgets();}else if(st.Theme!="Dark"&&fc=="#FFFFFF"){st.GlobalFontColor="#0D1321";GlobalFontColor.Text="#0D1321";ApplyGlobalTypographyToWidgets();}}
  if(ReferenceEquals(s,Startup))service.SetStartup(st.StartWithWindows);
  ApplyPreferences();
  service.ApplyPreferences(false);
  preferencePreviewPending=true;
  preferencePreviewTimer.Stop();
  preferencePreviewTimer.Start();
 WindowRegionHelper.ApplyDeferred(this,st.CornerRadius);
 }
 void SetThemePresetSelection(string preset){}
 void SetFontFamilySelection(ComboBox combo,string family){foreach(ComboBoxItem item in combo.Items){if(string.Equals(item.Content?.ToString(),family,StringComparison.OrdinalIgnoreCase)){combo.SelectedItem=item;return;}}combo.SelectedIndex=0;}
 string SelectedFontFamily(ComboBox combo)=>((ComboBoxItem?)combo.SelectedItem)?.Content?.ToString()??"Microsoft JhengHei UI";
 void ApplyGlobalTypographyToWidgets(){var st=service.State;foreach(var nest in st.Nests){nest.FontFamily=service.ContentFontFamily();nest.FontSize=service.ContentFontSize();nest.FontColor=st.GlobalFontColor;}}
 void ThemePreset_Changed(object sender,SelectionChangedEventArgs e){}
 void GlobalFontColor_Click(object sender,RoutedEventArgs e){if(sender is not Button button||button.Tag is not string color)return;GlobalFontColor.Text=color;Changed(sender,e);}
 void GlobalFontColorPicker_Click(object sender,RoutedEventArgs e){var start=ColorUtils.NormalizeHexColor(GlobalFontColor.Text,service.State.Theme=="Dark"?"#FFFFFF":"#0D1321");if(ShowColorPicker(start,out var picked)){GlobalFontColor.Text=picked;Changed(sender,e);}}
 bool ShowColorPicker(string initialHex,out string resultHex)
 {
  resultHex=initialHex;
  var dark=service.State.Theme=="Dark";
  var fg=dark?System.Windows.Media.Brushes.White:(System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13,19,33));
  var surface=dark?new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18,24,39)):new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250,251,252));
  System.Windows.Media.Color init;try{init=(System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(initialHex);}catch{init=System.Windows.Media.Colors.White;}
  ColorUtils.RgbToHsv(init.R,init.G,init.B,out double h,out double s,out double v);
  double areaW=272,areaH=170,hueH=16;
  var d=new Window{Title=Localization.T("調色盤",service.State.Language),Width=300,SizeToContent=SizeToContent.Height,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner,WindowStyle=WindowStyle.None,AllowsTransparency=true,ResizeMode=ResizeMode.NoResize,Background=System.Windows.Media.Brushes.Transparent,Foreground=fg};
  var shell=new Border{CornerRadius=new CornerRadius(Math.Max(14,service.State.CornerRadius)),BorderBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(110,255,138,0)),BorderThickness=new Thickness(1),Background=surface,Padding=new Thickness(14)};
  var root=new StackPanel();
  var header=new Grid{Margin=new Thickness(0,0,0,10),Cursor=System.Windows.Input.Cursors.SizeAll};header.ColumnDefinitions.Add(new ColumnDefinition());header.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});header.Children.Add(new TextBlock{Text=Localization.T("調色盤",service.State.Language),FontWeight=FontWeights.SemiBold,FontSize=15,VerticalAlignment=VerticalAlignment.Center});var closeBtn=new Button{Content="×",Width=26,Height=26,Padding=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,Foreground=fg,FontSize=17};Grid.SetColumn(closeBtn,1);header.Children.Add(closeBtn);header.MouseLeftButtonDown+=(_,ev)=>{if(ev.ButtonState==System.Windows.Input.MouseButtonState.Pressed)try{d.DragMove();}catch{}};root.Children.Add(header);
  var sv=new Grid{Width=areaW,Height=areaH,Margin=new Thickness(0,0,0,12),Cursor=System.Windows.Input.Cursors.Cross};
  var baseRect=new System.Windows.Shapes.Rectangle{RadiusX=8,RadiusY=8,Fill=new System.Windows.Media.LinearGradientBrush{StartPoint=new System.Windows.Point(0,0),EndPoint=new System.Windows.Point(1,0),GradientStops={new System.Windows.Media.GradientStop(System.Windows.Media.Colors.White,0),new System.Windows.Media.GradientStop(ColorUtils.HsvToColor(h,1,1),1)}}};
  var overlay=new System.Windows.Shapes.Rectangle{RadiusX=8,RadiusY=8,Fill=new System.Windows.Media.LinearGradientBrush{StartPoint=new System.Windows.Point(0,0),EndPoint=new System.Windows.Point(0,1),GradientStops={new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromArgb(0,0,0,0),0),new System.Windows.Media.GradientStop(System.Windows.Media.Colors.Black,1)}}};
  var svThumb=new System.Windows.Shapes.Ellipse{Width=14,Height=14,Stroke=System.Windows.Media.Brushes.White,StrokeThickness=2,IsHitTestVisible=false,HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Top,Effect=new System.Windows.Media.Effects.DropShadowEffect{BlurRadius=3,ShadowDepth=0,Opacity=.6}};
  sv.Children.Add(baseRect);sv.Children.Add(overlay);sv.Children.Add(svThumb);root.Children.Add(sv);
  var hueArea=new Grid{Width=areaW,Height=hueH,Margin=new Thickness(0,0,0,12),Cursor=System.Windows.Input.Cursors.Hand};
  var hueTrack=new System.Windows.Shapes.Rectangle{RadiusX=7,RadiusY=7,Fill=new System.Windows.Media.LinearGradientBrush{StartPoint=new System.Windows.Point(0,0),EndPoint=new System.Windows.Point(1,0),GradientStops={new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(255,0,0),0),new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(255,255,0),.166),new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0,255,0),.333),new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0,255,255),.5),new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0,0,255),.666),new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(255,0,255),.833),new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(255,0,0),1)}}};
  var hueThumb=new Border{Width=4,Height=hueH,CornerRadius=new CornerRadius(2),Background=System.Windows.Media.Brushes.White,BorderBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(60,60,60)),BorderThickness=new Thickness(1),HorizontalAlignment=HorizontalAlignment.Left,VerticalAlignment=VerticalAlignment.Center,IsHitTestVisible=false};
  hueArea.Children.Add(hueTrack);hueArea.Children.Add(hueThumb);root.Children.Add(hueArea);
  var bottom=new Grid();bottom.ColumnDefinitions.Add(new ColumnDefinition{Width=GridLength.Auto});bottom.ColumnDefinitions.Add(new ColumnDefinition());var preview=new Border{Width=38,Height=38,CornerRadius=new CornerRadius(8),BorderBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(90,128,128,128)),BorderThickness=new Thickness(1)};var hexBox=new TextBox{VerticalContentAlignment=VerticalAlignment.Center,Margin=new Thickness(10,0,0,0),MinHeight=34,Foreground=fg,Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(dark?(byte)86:(byte)210,255,255,255)),Padding=new Thickness(10,5,10,5)};Grid.SetColumn(hexBox,1);bottom.Children.Add(preview);bottom.Children.Add(hexBox);root.Children.Add(bottom);
  var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};var cancel=new Button{Content=Localization.T("取消",service.State.Language),MinWidth=72,Height=32,Margin=new Thickness(0,0,8,0),Foreground=fg};var ok=new Button{Content=Localization.T("確定",service.State.Language),MinWidth=72,Height=32,Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,138,0)),Foreground=System.Windows.Media.Brushes.White};actions.Children.Add(cancel);actions.Children.Add(ok);root.Children.Add(actions);
  shell.Child=root;d.Content=shell;
  bool syncing=false;
  void Refresh(bool updateHex){((System.Windows.Media.LinearGradientBrush)baseRect.Fill).GradientStops[1].Color=ColorUtils.HsvToColor(h,1,1);svThumb.Margin=new Thickness(s*areaW-7,(1-v)*areaH-7,0,0);hueThumb.Margin=new Thickness(h/360*areaW-2,0,0,0);var col=ColorUtils.HsvToColor(h,s,v);preview.Background=new System.Windows.Media.SolidColorBrush(col);if(updateHex){syncing=true;hexBox.Text=$"#{col.R:X2}{col.G:X2}{col.B:X2}";syncing=false;}}
  void SetSv(System.Windows.Point p){s=Math.Clamp(p.X/areaW,0,1);v=1-Math.Clamp(p.Y/areaH,0,1);Refresh(true);}
  void SetHue(System.Windows.Point p){h=Math.Clamp(p.X/areaW,0,1)*360;Refresh(true);}
  sv.MouseLeftButtonDown+=(_,ev)=>{sv.CaptureMouse();SetSv(ev.GetPosition(sv));};sv.MouseMove+=(_,ev)=>{if(ev.LeftButton==System.Windows.Input.MouseButtonState.Pressed)SetSv(ev.GetPosition(sv));};sv.MouseLeftButtonUp+=(_,_)=>sv.ReleaseMouseCapture();
  hueArea.MouseLeftButtonDown+=(_,ev)=>{hueArea.CaptureMouse();SetHue(ev.GetPosition(hueArea));};hueArea.MouseMove+=(_,ev)=>{if(ev.LeftButton==System.Windows.Input.MouseButtonState.Pressed)SetHue(ev.GetPosition(hueArea));};hueArea.MouseLeftButtonUp+=(_,_)=>hueArea.ReleaseMouseCapture();
  hexBox.TextChanged+=(_,_)=>{if(syncing)return;var t=hexBox.Text.Trim();if(!t.StartsWith("#"))t="#"+t;if(System.Text.RegularExpressions.Regex.IsMatch(t,"^#[0-9a-fA-F]{6}$")){try{var c=(System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(t);ColorUtils.RgbToHsv(c.R,c.G,c.B,out h,out s,out v);Refresh(false);}catch{}}};
  string chosen=initialHex;bool okResult=false;
  closeBtn.Click+=(_,_)=>{d.Close();};cancel.Click+=(_,_)=>{d.Close();};ok.Click+=(_,_)=>{var col=ColorUtils.HsvToColor(h,s,v);chosen=$"#{col.R:X2}{col.G:X2}{col.B:X2}";okResult=true;d.Close();};
  Refresh(true);d.ShowDialog();if(okResult)resultHex=chosen;return okResult;
 }
 void GlobalFontColor_KeyDown(object sender,KeyEventArgs e){if(e.Key!=Key.Enter)return;GlobalFontColor_Changed(sender,new RoutedEventArgs());Keyboard.ClearFocus();e.Handled=true;}
 void GlobalFontColor_Changed(object sender,RoutedEventArgs e){if(loading)return;Changed(sender,e);}
 void OpenStorage_Click(object s,RoutedEventArgs e){var dir=BeeXPaths.FileBoxesDir;Directory.CreateDirectory(dir);Process.Start(new ProcessStartInfo("explorer.exe",dir){UseShellExecute=true});}
 /// <summary>BeeX 資料目錄：顯示/打開/更改並整體遷移。</summary>
 void OpenBeeXRoot_Click(object s,RoutedEventArgs e){Directory.CreateDirectory(BeeXPaths.Root);Process.Start(new ProcessStartInfo("explorer.exe",$"\"{BeeXPaths.Root}\""){UseShellExecute=true});}
 void ChangeBeeXRoot_Click(object s,RoutedEventArgs e)
 {
  var lang=service.State.Language;
  using var dialog=new System.Windows.Forms.FolderBrowserDialog{Description=Localization.T("選擇 BeeX 資料目錄",lang),UseDescriptionForTitle=true,SelectedPath=BeeXPaths.Root};
  if(dialog.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;
  // 空資料夾直接用；目標已有其他內容時自動追加 BeeX 子目錄，確認框展示最終實際路徑
  var target=BeeXPaths.NormalizeRoot(dialog.SelectedPath);
  if(string.Equals(Path.TrimEndingDirectorySeparator(target),Path.TrimEndingDirectorySeparator(BeeXPaths.Root),StringComparison.OrdinalIgnoreCase))return;
  if(!BeeXDialog.Confirm(this,Localization.T("BeeX 資料目錄",lang),Localization.T("所有截圖、錄屏、便籤、設定與元件將遷移到新位置，期間請勿關閉程式。",lang)+"\n\n"+target,service.State,Localization.T("開始遷移",lang)))return;
  RunRootMigration(target);
 }
 void RunRootMigration(string target)
 {
  string T(string v)=>Localization.T(v,service.State.Language);
  var text=new TextBlock{Text=T("正在遷移資料…"),FontSize=14,Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13,19,33)),TextTrimming=TextTrimming.CharacterEllipsis,Margin=new Thickness(0,10,0,0)};
  var heading=new TextBlock{Text=T("BeeX 資料目錄"),FontSize=18,FontWeight=FontWeights.SemiBold,Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13,19,33))};
  var bar=new System.Windows.Controls.ProgressBar{Height=6,IsIndeterminate=true,Margin=new Thickness(0,12,0,0),Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(255,138,0))};
  var stack=new System.Windows.Controls.StackPanel();stack.Children.Add(heading);stack.Children.Add(text);stack.Children.Add(bar);
  var borderBox=new Border{CornerRadius=new CornerRadius(14),Background=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(250,251,252)),BorderBrush=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(115,255,138,0)),BorderThickness=new Thickness(1),Padding=new Thickness(24),Child=stack};
  var win=new Window{Width=420,SizeToContent=SizeToContent.Height,Owner=this,WindowStartupLocation=WindowStartupLocation.CenterOwner,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=System.Windows.Media.Brushes.Transparent,ShowInTaskbar=false,Topmost=true,Content=borderBox};
  var done=false;Exception? error=null;
  win.Closing+=(_,e)=>{if(!done)e.Cancel=true;};
  win.ContentRendered+=async(_,_)=>
  {
   try{await Task.Run(()=>BeeXPaths.ChangeRoot(target,name=>win.Dispatcher.BeginInvoke(()=>text.Text=T("正在遷移資料…")+" "+name)));}
   catch(Exception ex){error=ex;}
   done=true;win.Close();
  };
  win.ShowDialog();
  if(error!=null){BeeXDialog.Alert(this,T("遷移失敗"),Localization.T(error.Message,service.State.Language),service.State);return;}
  BeeXRootPathBox.Text=BeeXPaths.Root;
  if(BeeXDialog.Confirm(this,T("BeeX 資料目錄"),T("遷移完成，需要重新啟動。"),service.State,T("立即重啟")))RestartApp();
 }
 void RestartApp()
 {
  try{service.Save();}catch{}
  var exe=Environment.ProcessPath;
  // 先退出再由外殼延遲拉起，避免新實例觸發「已在運行」詢問
  if(exe!=null)Process.Start(new ProcessStartInfo("cmd.exe","/c ping -n 2 127.0.0.1 >nul & start \"\" \""+exe+"\""){CreateNoWindow=true,UseShellExecute=false});
  service.Exit();
 }
 void DownloadFfmpeg_Click(object s,RoutedEventArgs e){FfmpegInstallerService.ShowInstallDialog(service.State.Language);RefreshFfmpegStatus();}
 void DownloadOcr_Click(object s,RoutedEventArgs e){OcrInstallerService.ShowInstallDialog(service.State.Language);RefreshOcrStatus();}
 void UpdateComponentProgress(System.Windows.Controls.ProgressBar? bar,TextBlock? status,(string Phase,int Percent) p,string downloadKey){if(bar==null||status==null)return;bar.Visibility=Visibility.Visible;bar.IsIndeterminate=p.Percent<0||p.Phase=="extract";if(p.Percent>=0&&p.Phase!="extract")bar.Value=p.Percent;status.Text=p.Phase=="extract"?Localization.T("正在解壓安裝…",service.State.Language):Localization.T(downloadKey,service.State.Language)+(p.Percent>=0?" "+p.Percent+"%":"");}
 void RefreshFfmpegStatus(){if(FfmpegStatusText==null||FfmpegProgressBar==null||FfmpegDownloadBtn==null)return;if(FfmpegInstallerService.Installing){UpdateComponentProgress(FfmpegProgressBar,FfmpegStatusText,FfmpegInstallerService.LastProgress,"正在下載 ffmpeg 元件…");FfmpegDownloadBtn.IsEnabled=false;return;}var installed=FfmpegService.IsAvailable;FfmpegProgressBar.Visibility=Visibility.Collapsed;FfmpegProgressBar.IsIndeterminate=false;FfmpegStatusText.Text=Localization.T(installed?"ffmpeg 已安裝":"ffmpeg 未安裝",service.State.Language);FfmpegDownloadBtn.IsEnabled=!installed;}
 void RefreshOcrStatus(){if(OcrStatusText==null||OcrProgressBar==null||OcrDownloadBtn==null)return;if(OcrInstallerService.Installing){UpdateComponentProgress(OcrProgressBar,OcrStatusText,OcrInstallerService.LastProgress,"正在下載 OCR 元件…");OcrDownloadBtn.IsEnabled=false;return;}var installed=OcrSidecarService.IsAvailable;OcrProgressBar.Visibility=Visibility.Collapsed;OcrProgressBar.IsIndeterminate=false;OcrStatusText.Text=Localization.T(installed?"OCR 元件已安裝":"OCR 元件未安裝",service.State.Language);OcrDownloadBtn.IsEnabled=!installed;}
 void ImagePath_KeyDown(object sender,KeyEventArgs e){if(e.Key!=Key.Enter)return;ImagePath_Changed(sender,new RoutedEventArgs());Keyboard.ClearFocus();e.Handled=true;}
 void ImagePath_Changed(object sender,RoutedEventArgs e){if(loading||sender is not TextBox box)return;var path=box.Text.Trim().Trim('"');try{if(string.IsNullOrWhiteSpace(path)||!Path.IsPathFullyQualified(path))throw new IOException();Directory.CreateDirectory(path);if(ReferenceEquals(box,ClipboardImagePath))service.State.ClipboardImageDirectory=path;else service.State.ScreenshotDirectory=path;service.Save();box.Text=path;}catch{BeeXDialog.Alert(this,"資料夾路徑無效","請輸入有效的完整資料夾路徑。",service.State);box.Text=ReferenceEquals(box,ClipboardImagePath)?service.ClipboardImageDirectory:service.ScreenshotDirectory;}}
 void ChooseImageFolder_Click(object sender,RoutedEventArgs e){if(sender is not Button button)return;using var dialog=new System.Windows.Forms.FolderBrowserDialog{Description=button.Tag?.ToString()=="Clipboard"?"選擇剪貼板圖片資料夾":"選擇螢幕截圖資料夾",UseDescriptionForTitle=true,SelectedPath=button.Tag?.ToString()=="Clipboard"?service.ClipboardImageDirectory:service.ScreenshotDirectory};if(dialog.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;var box=button.Tag?.ToString()=="Clipboard"?ClipboardImagePath:ScreenshotPath;box.Text=dialog.SelectedPath;ImagePath_Changed(box,new RoutedEventArgs());}
 void OpenImageFolder_Click(object sender,RoutedEventArgs e){if(sender is not Button button)return;var path=button.Tag?.ToString()=="Clipboard"?service.ClipboardImageDirectory:service.ScreenshotDirectory;Directory.CreateDirectory(path);Process.Start(new ProcessStartInfo("explorer.exe",$"\"{path}\""){UseShellExecute=true});}
 /// <summary>截圖與錄屏設定頁：打開錄屏輸出資料夾</summary>
 void OpenRecordingsFolder_Click(object sender,RoutedEventArgs e){Directory.CreateDirectory(BeeXPaths.RecordingsDir);Process.Start(new ProcessStartInfo("explorer.exe",$"\"{BeeXPaths.RecordingsDir}\""){UseShellExecute=true});}
 static void SelectByTag(ComboBox combo,string tag){foreach(ComboBoxItem item in combo.Items)if(string.Equals(item.Tag?.ToString(),tag,StringComparison.OrdinalIgnoreCase)){combo.SelectedItem=item;return;}combo.SelectedIndex=0;}
 /// <summary>新待辦默認提醒多選：只影響之後新建的待辦</summary>
 void TodoDefaultReminder_Changed(object sender,RoutedEventArgs e){if(loading||!IsInitialized)return;var offsets=new List<int>();if(TodoRemWeek.IsChecked==true)offsets.Add(10080);if(TodoRem3Day.IsChecked==true)offsets.Add(4320);if(TodoRemDay.IsChecked==true)offsets.Add(1440);if(TodoRemHour.IsChecked==true)offsets.Add(60);if(TodoRemOnTime.IsChecked==true)offsets.Add(0);service.State.TodoDefaultReminderOffsets=offsets;service.Save();}
 /// <summary>共享格子背景：選圖複製到 BeeX 資料目錄 backgrounds 後套用到全部格子</summary>
 void ChooseSharedBackground_Click(object sender,RoutedEventArgs e){using var picker=new System.Windows.Forms.OpenFileDialog{Title=Localization.T("選擇背景圖片",service.State.Language),Filter="Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*"};if(picker.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;try{var dir=Path.Combine(BeeXPaths.DataDir,"backgrounds");Directory.CreateDirectory(dir);var destination=Path.Combine(dir,Guid.NewGuid()+Path.GetExtension(picker.FileName).ToLowerInvariant());File.Copy(picker.FileName,destination);service.State.SharedWidgetBackgroundPath=destination;service.State.UseSharedWidgetBackground=true;loading=true;SharedBackground.IsChecked=true;loading=false;service.ApplyPreferences();}catch{}}
 void ClearSharedBackground_Click(object sender,RoutedEventArgs e){service.State.SharedWidgetBackgroundPath="";service.ApplyPreferences();}
 void WeatherDefaultCity_KeyDown(object sender,KeyEventArgs e){if(e.Key!=Key.Enter)return;WeatherDefaultCity_Changed(sender,new RoutedEventArgs());Keyboard.ClearFocus();e.Handled=true;}
 void WeatherDefaultCity_Changed(object sender,RoutedEventArgs e){if(loading)return;var city=WeatherDefaultCity.Text.Trim();if(string.IsNullOrWhiteSpace(city))return;foreach(var nest in service.State.Nests.Where(n=>n.Kind==NestKind.Weather))nest.City=city;service.Save();service.RefreshWidgets();}
 void ApplyWeatherCity_Click(object sender,RoutedEventArgs e)=>WeatherDefaultCity_Changed(WeatherDefaultCity,new RoutedEventArgs());
 static readonly (string Key,string Label)[] ControlToolButtons =
 [
  ("Note","便箋"),("Todo","待辦"),("MapFolder","映射資料夾"),("Managed","收納格子"),("CaptureFolder","截圖文件夾"),("QuickNote","隨記"),("Music","音樂"),("Clock","時鐘"),("Screenshot","立即截圖"),("ToggleAll","顯示／隱藏全部"),("Weather","天氣"),("Tags","標籤"),("SystemMonitor","系統監控"),("Countdown","日程倒數"),("Launcher","快速啟動"),("WorkTimer","上下班提醒"),("CollapseAll","折疊／展開")
 ];
 static readonly HashSet<string> MultiOpenableKeys=new(StringComparer.OrdinalIgnoreCase){"Note","Todo","MapFolder","Managed","QuickNote","Music","Clock","Weather","Tags","SystemMonitor","Countdown","WorkTimer"};
 string? featureDragKey;
 System.Windows.Point featureDragStart;
 System.Windows.Documents.AdornerLayer? featureAdornerLayer;
 DropLineAdorner? featureDropAdorner;
 ScrollViewer? featureScroller;
 System.Windows.Threading.DispatcherTimer? featureAutoScrollTimer;
 double featureAutoScrollDelta;
 string? featureDropBeforeKey;
 public void RefreshFeatureNests()
 {
  if(!IsInitialized||FeatureNestListPanel==null)return;
  featureListLoading=true;
  try
  {
   var state=service.State;
   var validKeys=ControlToolButtons.Select(t=>t.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
   state.ToolButtonVisibility=state.ToolButtonVisibility.Where(x=>validKeys.Contains(x.Key)).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.OrdinalIgnoreCase);
   foreach(var tool in ControlToolButtons)if(!state.ToolButtonVisibility.ContainsKey(tool.Key))state.ToolButtonVisibility[tool.Key]=true;
   state.ToolButtonMultiOpen=state.ToolButtonMultiOpen.Where(x=>MultiOpenableKeys.Contains(x.Key)).ToDictionary(x=>x.Key,x=>x.Value,StringComparer.OrdinalIgnoreCase);
   var order=state.ToolButtonOrder.Where(k=>ControlToolButtons.Any(t=>t.Key==k)).ToList();
   foreach(var tool in ControlToolButtons)if(!order.Contains(tool.Key))order.Add(tool.Key);
   state.ToolButtonOrder=order;
   service.Save();
   var iconBrush=state.Theme=="Dark"?System.Windows.Media.Brushes.White:(System.Windows.Media.Brush)new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13,19,33));
   var secondary=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102,112,133));
   FeatureNestListPanel.Children.Clear();
   FeatureNestListPanel.Children.Add(BuildFeatureHeader(state,secondary));
   foreach(var key in order)
   {
    var tool=ControlToolButtons.First(x=>x.Key==key);
    var row=new Grid{Margin=new Thickness(0,3,0,3),Tag=key};
    row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(40)});
    row.ColumnDefinitions.Add(new ColumnDefinition());
    row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(88)});
    row.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(40)});
    var visible=new CheckBox{IsChecked=state.ToolButtonVisibility.GetValueOrDefault(key,true),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Tag=key,ToolTip=Localization.T("在主控制台顯示此按鈕",state.Language)};
    visible.Checked+=FeatureToolVisible_Changed;visible.Unchecked+=FeatureToolVisible_Changed;Grid.SetColumn(visible,0);row.Children.Add(visible);
    var label=new TextBlock{Text=Localization.T(tool.Label,state.Language),VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold,Margin=new Thickness(4,0,0,0)};
    Grid.SetColumn(label,1);row.Children.Add(label);
    if(MultiOpenableKeys.Contains(key))
    {
     var multi=new CheckBox{IsChecked=state.ToolButtonMultiOpen.GetValueOrDefault(key,false),HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Tag=key,ToolTip=Localization.T("開啟後允許建立多個此功能格子；預設關閉（僅一個）",state.Language)};
     multi.Checked+=FeatureMultiOpen_Changed;multi.Unchecked+=FeatureMultiOpen_Changed;Grid.SetColumn(multi,2);row.Children.Add(multi);
    }
    var handle=new Button{Width=32,Height=28,Padding=new Thickness(0),Background=System.Windows.Media.Brushes.Transparent,BorderThickness=new Thickness(0),Cursor=System.Windows.Input.Cursors.SizeAll,Tag=key,ToolTip=Localization.T("按住拖動排序",state.Language),Content=new Image{Source=SvgIcon.Load("menu-2",16,iconBrush),Width=16,Height=16}};
    handle.PreviewMouseLeftButtonDown+=FeatureHandle_MouseDown;handle.PreviewMouseMove+=FeatureHandle_MouseMove;Grid.SetColumn(handle,3);row.Children.Add(handle);
    FeatureNestListPanel.Children.Add(row);
   }
  }
  finally{featureListLoading=false;}
  ApplyPreferences();
 }
 Grid BuildFeatureHeader(AppState state,System.Windows.Media.Brush secondary)
 {
  var header=new Grid{Margin=new Thickness(0,0,0,4)};
  header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(40)});
  header.ColumnDefinitions.Add(new ColumnDefinition());
  header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(88)});
  header.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(40)});
  void H(string t,int col,HorizontalAlignment ha){var tb=new TextBlock{Text=Localization.T(t,state.Language),FontWeight=FontWeights.SemiBold,FontSize=12,Foreground=secondary,HorizontalAlignment=ha,VerticalAlignment=VerticalAlignment.Center};Grid.SetColumn(tb,col);header.Children.Add(tb);}
  H("顯示",0,HorizontalAlignment.Center);
  H("功能",1,HorizontalAlignment.Left);
  H("允許多開",2,HorizontalAlignment.Center);
  H("排序",3,HorizontalAlignment.Center);
  return header;
 }
 void FeatureToolVisible_Changed(object sender,RoutedEventArgs e){if(featureListLoading||sender is not CheckBox box||box.Tag is not string key)return;service.State.ToolButtonVisibility[key]=box.IsChecked==true;service.Save();service.ApplyPreferences(false);}
 void FeatureMultiOpen_Changed(object sender,RoutedEventArgs e){if(featureListLoading||sender is not CheckBox box||box.Tag is not string key)return;service.State.ToolButtonMultiOpen[key]=box.IsChecked==true;service.Save();}
 void FeatureHandle_MouseDown(object sender,MouseButtonEventArgs e){if(sender is Button b&&b.Tag is string key){featureDragKey=key;featureDragStart=e.GetPosition(this);}}
 void FeatureHandle_MouseMove(object sender,System.Windows.Input.MouseEventArgs e){if(e.LeftButton!=MouseButtonState.Pressed||featureDragKey==null||sender is not Button b)return;var p=e.GetPosition(this);if(Math.Abs(p.X-featureDragStart.X)<6&&Math.Abs(p.Y-featureDragStart.Y)<6)return;var key=featureDragKey;featureDragKey=null;try{System.Windows.DragDrop.DoDragDrop(b,key,System.Windows.DragDropEffects.Move);}finally{EndFeatureDragVisuals();}}
 void FeatureRow_DragOver(object sender,System.Windows.DragEventArgs e){var ok=e.Data.GetDataPresent(System.Windows.DataFormats.StringFormat);e.Effects=ok?System.Windows.DragDropEffects.Move:System.Windows.DragDropEffects.None;e.Handled=true;if(!ok)return;var rows=FeatureNestListPanel.Children.OfType<Grid>().Where(g=>g.Tag is string k&&!string.IsNullOrEmpty(k)).ToList();var y=e.GetPosition(FeatureNestListPanel).Y;double lineY=0;string? beforeKey=null;var idx=rows.Count;for(var i=0;i<rows.Count;i++){var top=rows[i].TranslatePoint(new System.Windows.Point(0,0),FeatureNestListPanel).Y;if(y<top+rows[i].ActualHeight/2){idx=i;lineY=top-1;beforeKey=rows[i].Tag as string;break;}}if(idx==rows.Count&&rows.Count>0){var last=rows[^1];lineY=last.TranslatePoint(new System.Windows.Point(0,0),FeatureNestListPanel).Y+last.ActualHeight+1;}featureDropBeforeKey=beforeKey;ShowDropIndicator(lineY);var sv=FeatureScroller();if(sv!=null){var py=e.GetPosition(sv).Y;var vh=sv.ViewportHeight;const double edge=42;if(py<edge)featureAutoScrollDelta=-(6+(edge-py)*0.5);else if(py>vh-edge)featureAutoScrollDelta=6+(py-(vh-edge))*0.5;else featureAutoScrollDelta=0;EnsureAutoScrollTimer();}}
 void FeatureRow_Drop(object sender,System.Windows.DragEventArgs e){var present=e.Data.GetDataPresent(System.Windows.DataFormats.StringFormat);var dragKey=present?e.Data.GetData(System.Windows.DataFormats.StringFormat) as string:null;var beforeKey=featureDropBeforeKey;EndFeatureDragVisuals();if(string.IsNullOrWhiteSpace(dragKey))return;ReorderFeatureTool(dragKey!,beforeKey);}
 ScrollViewer? FeatureScroller()=>featureScroller??=VisualTreeUtils.FindParent<ScrollViewer>(FeatureNestListPanel);
 void ShowDropIndicator(double y){featureAdornerLayer??=System.Windows.Documents.AdornerLayer.GetAdornerLayer(FeatureNestListPanel);if(featureAdornerLayer==null)return;if(featureDropAdorner==null){featureDropAdorner=new DropLineAdorner(FeatureNestListPanel);featureAdornerLayer.Add(featureDropAdorner);}featureDropAdorner.Y=y;featureDropAdorner.InvalidateVisual();}
 void EnsureAutoScrollTimer(){if(featureAutoScrollTimer!=null)return;featureAutoScrollTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(30)};featureAutoScrollTimer.Tick+=(_,_)=>{var sv=FeatureScroller();if(sv==null||Math.Abs(featureAutoScrollDelta)<0.1)return;sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset+featureAutoScrollDelta,0,sv.ScrollableHeight));};featureAutoScrollTimer.Start();}
 void EndFeatureDragVisuals(){featureAutoScrollDelta=0;featureAutoScrollTimer?.Stop();featureAutoScrollTimer=null;if(featureDropAdorner!=null&&featureAdornerLayer!=null)featureAdornerLayer.Remove(featureDropAdorner);featureDropAdorner=null;featureDropBeforeKey=null;}
 sealed class DropLineAdorner:System.Windows.Documents.Adorner{public double Y;static readonly System.Windows.Media.Pen Line;static readonly System.Windows.Media.Brush Dot;static DropLineAdorner(){var c=System.Windows.Media.Color.FromRgb(255,138,0);Line=new System.Windows.Media.Pen(new System.Windows.Media.SolidColorBrush(c),3){StartLineCap=System.Windows.Media.PenLineCap.Round,EndLineCap=System.Windows.Media.PenLineCap.Round};Line.Freeze();Dot=new System.Windows.Media.SolidColorBrush(c);Dot.Freeze();}public DropLineAdorner(UIElement adorned):base(adorned){IsHitTestVisible=false;}protected override void OnRender(System.Windows.Media.DrawingContext dc){var w=((FrameworkElement)AdornedElement).ActualWidth;if(w<=14)return;dc.DrawLine(Line,new System.Windows.Point(7,Y),new System.Windows.Point(w-7,Y));dc.DrawEllipse(Dot,null,new System.Windows.Point(7,Y),3.5,3.5);dc.DrawEllipse(Dot,null,new System.Windows.Point(w-7,Y),3.5,3.5);}}
 static string? FeatureRowKey(DependencyObject? src){while(src!=null){if(src is Grid g&&g.Tag is string k&&!string.IsNullOrEmpty(k))return k;src=System.Windows.Media.VisualTreeHelper.GetParent(src);}return null;}
 void ReorderFeatureTool(string dragKey,string? targetKey){var order=service.State.ToolButtonOrder;if(!order.Contains(dragKey)||string.Equals(dragKey,targetKey,StringComparison.Ordinal))return;order.Remove(dragKey);var to=targetKey!=null?order.IndexOf(targetKey):-1;if(to<0)to=order.Count;order.Insert(to,dragKey);service.Save();service.ApplyPreferences(false);RefreshFeatureNests();}
 void FeatureVisible_Changed(object sender,RoutedEventArgs e){}
 void MoveFeatureNest(NestModel nest,int delta){}
 static string KindLabel(NestKind kind)=>Localization.DefaultTitle(kind);
 void BuildSelectedNestSettings(NestModel? nest)
 {
 }
 void BuildMusicNestSettings(NestModel nest)
 {
 }
 void BuildMusicGlobalSettings()
 {
  if(MusicGlobalSettingsPanel==null)return;
  MusicGlobalSettingsPanel.Children.Clear();
  var playerLogo=new CheckBox{Content=Localization.T("摺疊後顯示播放器圖標",service.State.Language),IsChecked=service.State.ShowCollapsedMusicPlayerLogo,Margin=new Thickness(0,0,0,4)};
  playerLogo.Checked+=(_,_)=>{if(loading)return;service.State.ShowCollapsedMusicPlayerLogo=true;service.ApplyPreferences();};
  playerLogo.Unchecked+=(_,_)=>{if(loading)return;service.State.ShowCollapsedMusicPlayerLogo=false;service.ApplyPreferences();};
  MusicGlobalSettingsPanel.Children.Add(playerLogo);
  MusicGlobalSettingsPanel.Children.Add(new TextBlock{Text=Localization.T("音樂格子摺疊時顯示正在播放的音樂軟體圖標；取不到圖標時回退為 BeeX Logo。此開關獨立於全局 Logo 開關。",service.State.Language),Foreground=new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(102,112,133)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)});
  MusicGlobalSettingsPanel.Children.Add(new TextBlock{Text=Localization.T("套用到所有音樂格子",service.State.Language),FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,8)});
  MusicGlobalSettingsPanel.Children.Add(new TextBlock{Text=Localization.T("歌名文字顏色",service.State.Language),Margin=new Thickness(0,5,0,4)});
  MusicGlobalSettingsPanel.Children.Add(GlobalMusicColorPalette("MusicTitleColor",false));
  MusicGlobalSettingsPanel.Children.Add(new TextBlock{Text=Localization.T("歌詞文字顏色",service.State.Language),Margin=new Thickness(0,8,0,4)});
  MusicGlobalSettingsPanel.Children.Add(GlobalMusicColorPalette("MusicLyricColor",false));
  MusicGlobalSettingsPanel.Children.Add(new TextBlock{Text=Localization.T("歌名／歌詞填充色",service.State.Language),Margin=new Thickness(0,8,0,4)});
  MusicGlobalSettingsPanel.Children.Add(GlobalMusicColorPalette("MusicOverlayColor",true));
 }
 StackPanel GlobalMusicColorPalette(string property,bool overlay)
 {
  var panel=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,5)};
  var colors=overlay?new[]{"","#66000000","#88000000","#66FFFFFF","#55FF8A00"}:new[]{"","#FFFFFF","#0D1321","#FF8A00","#D92D20","#175CD3","#067647","#7F56D9"};
  foreach(var hex in colors)
  {
   var brush=string.IsNullOrEmpty(hex)?System.Windows.Media.Brushes.Transparent:(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
   var b=new Button{Content=string.IsNullOrEmpty(hex)?Localization.T("清除",service.State.Language):"",Width=string.IsNullOrEmpty(hex)?54:30,Height=30,Margin=new Thickness(0,0,7,0),Background=brush,BorderThickness=new Thickness(1),Tag=hex};
   b.Click+=(_,_)=>{var value=(string)b.Tag;foreach(var nest in service.State.Nests.Where(n=>n.Kind==NestKind.Music)){if(property=="MusicTitleColor")nest.MusicTitleColor=value;else if(property=="MusicLyricColor")nest.MusicLyricColor=value;else nest.MusicOverlayColor=value;}service.Save();service.ApplyPreferences(false);};
   panel.Children.Add(b);
  }
  return panel;
 }
 StackPanel ColorPalette(NestModel nest,string property,bool overlay)
 {
  var panel=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(0,0,0,5)};
  var colors=overlay?new[]{"","#66000000","#88000000","#66FFFFFF","#55FF8A00"}:new[]{"","#FFFFFF","#0D1321","#FF8A00","#D92D20","#175CD3","#067647","#7F56D9"};
  foreach(var hex in colors)
  {
   var brush=string.IsNullOrEmpty(hex)?System.Windows.Media.Brushes.Transparent:(System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(hex)!;
   var b=new Button{Content=string.IsNullOrEmpty(hex)?Localization.T("清除",service.State.Language):"",Width=string.IsNullOrEmpty(hex)?54:30,Height=30,Margin=new Thickness(0,0,7,0),Background=brush,BorderThickness=new Thickness(1),Tag=hex};
   b.Click+=(_,_)=>{var value=(string)b.Tag;if(property=="MusicTitleColor")nest.MusicTitleColor=value;else if(property=="MusicLyricColor")nest.MusicLyricColor=value;else nest.MusicOverlayColor=value;service.Save();service.ApplyPreferences(false);BuildSelectedNestSettings(nest);};
   panel.Children.Add(b);
  }
  return panel;
 }
 void LaunchCleaner_Click(object s,RoutedEventArgs e)=>service.ShowCleaner();
 void DeepLApiKey_KeyDown(object sender,KeyEventArgs e){if(e.Key!=Key.Enter){return;}DeepLApiKey_Changed(sender,new RoutedEventArgs());Keyboard.ClearFocus();e.Handled=true;}
 static readonly string[] TranslateTargetCodes={"auto","zh","en","ja","ko"};
 void LoadTranslateTarget(){var lang=service.State.Language;TranslateTargetLabel.Text=Localization.T("截圖翻譯目標語言",lang);var labels=new[]{Localization.T("自動",lang),Localization.T("中文",lang),Localization.T("英文",lang),Localization.T("日文",lang),Localization.T("韓文",lang)};TranslateTargetBox.ItemsSource=labels;var code=UserConfigHelper.ReadTranslateTarget();var idx=Array.IndexOf(TranslateTargetCodes,code);TranslateTargetBox.SelectedIndex=idx<0?0:idx;}
 void TranslateTarget_Changed(object sender,SelectionChangedEventArgs e){if(loading)return;var i=TranslateTargetBox.SelectedIndex;UserConfigHelper.WriteTranslateTarget(i>=0&&i<TranslateTargetCodes.Length?TranslateTargetCodes[i]:"auto");}
 void DeepLApiKey_Changed(object sender,RoutedEventArgs e){if(loading)return;UserConfigHelper.WriteDeepLApiKey(DeepLApiKeyBox.Text.Trim());TranslateResultWindow.ClearDeepLKeyCache();}
 void Reset_Click(object s,RoutedEventArgs e){if(BeeXDialog.Confirm(this,Localization.T("重置偏好設定",service.State.Language),Localization.T("恢復預設偏好？不會刪除格子資料和使用者文件。",service.State.Language),service.State,Localization.T("重置",service.State.Language))){service.ResetPreferences();LoadState();}}
 void Language_Changed(object s,SelectionChangedEventArgs e){if(loading||!IsInitialized)return;var code=LanguageCombo.SelectedValue?.ToString();if(string.IsNullOrWhiteSpace(code)||code==service.State.Language)return;service.SetLanguage(code);}
 void BuildHotkeyEditors(){HotkeyEditorPanel.Children.Clear();var commands=new[]{("Launcher","快速啟動"),("Note","新增便箋"),("Todo","新增待辦"),("MapFolder","映射資料夾"),("Managed","新增收納格子"),("CaptureFolder","開啟截圖文件夾"),("QuickNote","新增隨記"),("Music","新增音樂"),("Clock","新增時鐘"),("Screenshot","立即截圖"),("TranslateScreenshot","截圖翻譯"),("PinText","釘選剪貼板文字"),("ToggleAll","顯示／隱藏全部"),("CollapseAll","摺疊／展開全部"),("Weather","新增天氣"),("MinimizeTransparent","最小化透明視窗")};foreach(var command in commands){var grid=new Grid{Margin=new Thickness(0,3,0,3)};grid.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(190)});grid.ColumnDefinitions.Add(new ColumnDefinition());var label=new TextBlock{Text=Localization.T(command.Item2,service.State.Language),VerticalAlignment=VerticalAlignment.Center};var editor=new TextBox{Text=service.State.Hotkeys.GetValueOrDefault(command.Item1,""),IsReadOnly=true,Tag=command.Item1,ToolTip=Localization.T("點擊後按組合鍵；Esc 清除",service.State.Language),HorizontalContentAlignment=HorizontalAlignment.Center};editor.PreviewKeyDown+=HotkeyEditor_KeyDown;Grid.SetColumn(editor,1);grid.Children.Add(label);grid.Children.Add(editor);HotkeyEditorPanel.Children.Add(grid);}}
 void HotkeyEditor_KeyDown(object sender,KeyEventArgs e){if(sender is not TextBox editor)return;e.Handled=true;var key=e.Key==Key.System?e.SystemKey:e.Key;if(key==Key.Escape){service.SetHotkey(editor.Tag!.ToString()!,"");editor.Text="";BuildHotkeyEditors();return;}if(key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)return;var parts=new List<string>();var modifiers=Keyboard.Modifiers;if(modifiers.HasFlag(ModifierKeys.Control))parts.Add("Ctrl");if(modifiers.HasFlag(ModifierKeys.Alt))parts.Add("Alt");if(modifiers.HasFlag(ModifierKeys.Shift))parts.Add("Shift");if(modifiers.HasFlag(ModifierKeys.Windows))parts.Add("Win");parts.Add(key.ToString());var shortcut=string.Join(" + ",parts);service.SetHotkey(editor.Tag!.ToString()!,shortcut);BuildHotkeyEditors();}
 /// <summary>關於頁作者區：GitHub logo + 名字的按鈕（Content 為面板，Localization.Apply 不會改寫），點擊用系統瀏覽器打開</summary>
 void BuildAuthorLinks(System.Windows.Media.Brush fg)
 {
  if(AuthorLinksPanel==null)return;
  AuthorLinksPanel.Children.Clear();
  foreach(var (name,url) in new[]{("Wind","https://github.com/windzxy"),("BeeX-Labs","https://github.com/beex-labs")})
  {
   var content=new StackPanel{Orientation=Orientation.Horizontal,IsHitTestVisible=false};
   content.Children.Add(new Image{Source=SvgIcon.Load("brand-github",18,fg),Width=18,Height=18,Margin=new Thickness(0,0,8,0),VerticalAlignment=VerticalAlignment.Center});
   content.Children.Add(new TextBlock{Text=name,VerticalAlignment=VerticalAlignment.Center,FontWeight=FontWeights.SemiBold,Foreground=fg});
   var button=new Button{Content=content,Tag=url,ToolTip=url,Margin=new Thickness(0,0,10,0),Padding=new Thickness(13,8,13,8),Cursor=System.Windows.Input.Cursors.Hand};
   button.Click+=(s,_)=>{if(s is Button b&&b.Tag is string link)try{Process.Start(new ProcessStartInfo(link){UseShellExecute=true});}catch{}};
   AuthorLinksPanel.Children.Add(button);
  }
 }
 void RefreshNavIcons()
 {
     var nameIcons=new(string name,string icon)[]{
         ("常規","settings"),("外觀","palette"),("文件格子","folder"),("功能格子","layout"),
         ("待辦提醒","bell"),("隨記圖庫","camera"),("截圖與錄屏","video"),("音樂歌詞","music"),("天氣","sun"),
         ("快捷與交互","keyboard"),("BeeX 清理","spray"),("診斷與維護","stethoscope"),("關於","info-circle")
     };
     var fg=service.State.Theme=="Dark"?System.Windows.Media.Brushes.White:(Foreground as System.Windows.Media.Brush??new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(13,19,33)));
     var items=new[]{NavGeneral,NavAppearance,NavFiles,NavFeatures,NavTodo,NavCapture,NavCaptureRecord,NavMusic,NavWeather,NavShortcuts,NavCleaner,NavMaintenance,NavAbout};
     for(int i=0;i<nameIcons.Length&&i<items.Length;i++)
     {
         var sp=new StackPanel{Orientation=Orientation.Horizontal};
         sp.Children.Add(new Image{Source=SvgIcon.Load(nameIcons[i].icon,18,fg,0.8),Width=18,Height=18,Margin=new Thickness(0,0,10,0)});
         sp.Children.Add(new TextBlock{Text=Localization.T(nameIcons[i].name,service.State.Language),VerticalAlignment=VerticalAlignment.Center});
         items[i]!.Content=sp;
     }
     BuildAuthorLinks(fg);
 }
}
