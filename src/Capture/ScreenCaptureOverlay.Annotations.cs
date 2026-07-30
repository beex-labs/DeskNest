using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Documents;
using System.Windows.Shapes;
using Drawing=System.Drawing;
using Forms=System.Windows.Forms;
using Image=System.Windows.Controls.Image;
using Point=System.Windows.Point;
using Color=System.Windows.Media.Color;
using Brushes=System.Windows.Media.Brushes;
using Cursors=System.Windows.Input.Cursors;
using Button=System.Windows.Controls.Button;
using Cursor=System.Windows.Input.Cursor;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;
using Orientation=System.Windows.Controls.Orientation;
using Size=System.Windows.Size;
using Clipboard=System.Windows.Clipboard;
using IoPath=System.IO.Path;
using WpfRectangle=System.Windows.Shapes.Rectangle;
using WpfTextBox=System.Windows.Controls.TextBox;
using WpfContextMenu=System.Windows.Controls.ContextMenu;
using WpfMenuItem=System.Windows.Controls.MenuItem;
using WpfSeparator=System.Windows.Controls.Separator;

namespace BeeX.DeskNest;

sealed partial class ScreenCaptureOverlay
{
    void BuildToolbar()
    {
        // ToolTip「文案    快捷鍵」四空格分隔：Localization.T 只翻譯前半段，快捷鍵原樣保留
        string Tip(string text,string keyHint)=>L(text)+"    "+keyHint;
        var row=new StackPanel{Orientation=Orientation.Horizontal};
        row.Children.Add(IconButton("check",Tip("複製","Ctrl+C"),CopyOnly,primary:true));
        row.Children.Add(IconButton("device-floppy",Tip("保存並複製","Enter"),CompleteCapture));
        row.Children.Add(IconButton("download",Tip("僅保存","Ctrl+S"),SaveOnly));
        row.Children.Add(ToolIconButton("square",AnnotationTool.Rect,Tip("矩形","R")));
        row.Children.Add(ToolIconButton("circle",AnnotationTool.Ellipse,Tip("圓形 / 橢圓","O")));
        row.Children.Add(ToolIconButton("line",AnnotationTool.Line,Tip("線條","I")));
        row.Children.Add(ToolIconButton("arrow-up-right",AnnotationTool.Arrow,Tip("箭頭","A")));
        row.Children.Add(ToolIconButton("pencil",AnnotationTool.Pen,Tip("畫筆","P")));
        row.Children.Add(ToolIconButton("highlight",AnnotationTool.Highlighter,Tip("螢光筆","H")));
        row.Children.Add(ToolIconButton("grid-dots",AnnotationTool.Mosaic,Tip("馬賽克","M")));
        row.Children.Add(ToolIconButton("list-numbers",AnnotationTool.Number,Tip("序號標註","N")));
        row.Children.Add(ToolIconButton("eraser",AnnotationTool.Eraser,Tip("橡皮擦","E")));
        row.Children.Add(ToolIconButton("color-picker",AnnotationTool.ColorPicker,Tip("取色器","K")));
        row.Children.Add(ToolIconButton("typography",AnnotationTool.Text,Tip("文字樣式","T")));
        row.Children.Add(IconButton("pin",Tip("盯住","D"),Pin));
        row.Children.Add(IconButton("scan",Tip("截圖辨識","S / Shift+C"),StartOcr));
        row.Children.Add(IconButton("a-b-2",Tip("翻譯","Q"),StartTranslate));
        row.Children.Add(IconButton("scroll",Tip("長截圖","L"),StartScrollingCapture));
        row.Children.Add(IconButton("video",Tip("錄屏","V"),StartRecording));
        var refreshBtn=IconButton("refresh",Tip("重新截取（長按連續刷新）","F5"),()=>{});
        refreshBtn.PreviewMouseLeftButtonDown+=(_,e)=>{e.Handled=true;RefreshShot();holdTimer.Start();};
        row.Children.Add(refreshBtn);
        row.Children.Add(IconButton("dots",L("更多"),ToggleMoreTools));
        formatBtn=new Button{Content=new TextBlock{Text="PNG",Foreground=Brushes.White,FontWeight=FontWeights.SemiBold},MinWidth=46,Height=34,Margin=new Thickness(3),Padding=new Thickness(6,0,6,0),Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(82,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand,ToolTip=Tip("圖片格式（點擊切換）","G")};
        formatBtn.Click+=(_,_)=>ShowFormatMenu();
        row.Children.Add(formatBtn);
        row.Children.Add(IconButton("x",Tip("取消","Esc"),Close));
        toolbar.Child=row;
        secondaryBar.Child=secondaryRow;
        UpdateToolHighlights();
    }

    void CycleFormat(){var i=Array.IndexOf(captureFormats,outputFormat);i=(i+1)%captureFormats.Length;outputFormat=captureFormats[i];if(formatBtn!=null)formatBtn.Content=new TextBlock{Text=outputFormat.ToUpperInvariant(),Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};}
    void ShowFormatMenu()
    {
        var menu=new WpfContextMenu{Background=new SolidColorBrush(Color.FromArgb(236,13,19,33)),Foreground=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromArgb(160,255,138,0)),BorderThickness=new Thickness(1)};
        foreach(var f in captureFormats)
        {
            var ff=f;
            var it=new WpfMenuItem{Header=ff.ToUpperInvariant(),IsCheckable=true,IsChecked=ff==outputFormat,Foreground=Brushes.White};
            it.Click+=(_,_)=>{outputFormat=ff;if(formatBtn!=null)formatBtn.Content=new TextBlock{Text=ff.ToUpperInvariant(),Foreground=Brushes.White,FontWeight=FontWeights.SemiBold};};
            menu.Items.Add(it);
        }
        menu.PlacementTarget=formatBtn;menu.IsOpen=true;
    }
    static BitmapEncoder EncoderFor(string fmt)=>fmt switch
    {
        "jpg"=>new JpegBitmapEncoder{QualityLevel=95},
        "bmp"=>new BmpBitmapEncoder(),
        "gif"=>new GifBitmapEncoder(),
        "tiff"=>new TiffBitmapEncoder(),
        _=>new PngBitmapEncoder()
    };

    static Image Glyph(string name,double size=18)=>new Image{Source=SvgIcon.Load(name,size,Brushes.White),Width=size,Height=size,Stretch=Stretch.Uniform,IsHitTestVisible=false};

    static StackPanel IconWithChevron(string name)
    {
        var sp=new StackPanel{Orientation=Orientation.Horizontal,IsHitTestVisible=false};
        sp.Children.Add(Glyph(name,18));
        var chev=Glyph("chevron-down",12);
        chev.Margin=new Thickness(2,0,0,0);
        sp.Children.Add(chev);
        return sp;
    }

    Button IconButton(string icon,string tip,Action action,bool primary=false)
    {
        var button=new Button{Content=Glyph(icon),Padding=new Thickness(9,7,9,7),Margin=new Thickness(3),MinWidth=40,Foreground=Brushes.White,Background=primary?new SolidColorBrush(Color.FromRgb(255,138,0)):new SolidColorBrush(Color.FromArgb(82,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand,ToolTip=tip};
        button.Click+=(_,_)=>action();
        return button;
    }

    Button ToolIconButton(string icon,AnnotationTool tool,string tip)
    {
        var button=IconButton(icon,tip,()=>SelectTool(tool));
        toolButtons[tool]=button;
        return button;
    }

    void SelectTool(AnnotationTool tool)
    {
        if(annotationTool==tool&&tool!=AnnotationTool.Select)tool=AnnotationTool.Select;
        annotationTool=tool;
        ClearAnnResizer();
        if(tool==AnnotationTool.Number)numberCounter=1;
        if(tool!=AnnotationTool.ColorPicker)HideLoupe();
        if(!IsBrushTool(tool))HideBrushRing();
        Cursor=CursorForAnnotationTool(tool);
        UpdateToolHighlights();
        RefreshSecondaryBar();
    }

    void RefreshSecondaryBar()
    {
        secondaryRow.Children.Clear();
        var show=true;
        switch(annotationTool)
        {
            case AnnotationTool.Rect:
            case AnnotationTool.Ellipse:BuildShapeSettings();break;
            case AnnotationTool.Line:
            case AnnotationTool.Arrow:
            case AnnotationTool.Pen:
            case AnnotationTool.Highlighter:BuildLineSettings();break;
            case AnnotationTool.Text:BuildTextSettings();break;
            case AnnotationTool.Number:BuildNumberSettings();break;
            case AnnotationTool.Mosaic:BuildMosaicSettings();break;
            default:show=false;break;
        }
        secondaryBar.Visibility=show?Visibility.Visible:Visibility.Collapsed;
        hasSecondary=show;
        if(toolbar.Visibility==Visibility.Visible)ShowToolbar();
    }

    void BuildShapeSettings()
    {
        secondaryRow.Children.Add(ColorPickerBlock(L("線條顏色"),annotationTextColors,annotationTextColorIndex,i=>annotationTextColorIndex=i));
        secondaryRow.Children.Add(ColorPickerBlock(L("填充顏色（× 為無填充）"),annotationFillColors,annotationFillColorIndex,i=>annotationFillColorIndex=i));
        secondaryRow.Children.Add(StrokeWidthBlock());
    }

    void BuildLineSettings()
    {
        secondaryRow.Children.Add(ColorPickerBlock(L("顏色"),annotationTextColors,annotationTextColorIndex,i=>annotationTextColorIndex=i));
        secondaryRow.Children.Add(StrokeWidthBlock());
        secondaryRow.Children.Add(LineStyleBlock());
    }

    FrameworkElement LineStyleBlock()
    {
        var root=new StackPanel{Margin=new Thickness(6,5,6,8)};
        root.Children.Add(new TextBlock{Text=L("線型"),Foreground=Brushes.White,Opacity=.84,Margin=new Thickness(2,0,2,5)});
        var row=new StackPanel{Orientation=Orientation.Horizontal};
        row.Children.Add(DashChoice(0,"—"));
        row.Children.Add(DashChoice(1,"- -"));
        row.Children.Add(DashChoice(2,"— —"));
        row.Children.Add(DashChoice(3,"···"));
        root.Children.Add(row);
        return root;
    }

    Button DashChoice(int style,string label)
    {
        var active=annotationDash==style;
        var b=new Button{Content=new TextBlock{Text=label,Foreground=Brushes.White},MinWidth=38,Height=28,Margin=new Thickness(2),Padding=new Thickness(4,0,4,0),Foreground=Brushes.White,Background=active?new SolidColorBrush(Color.FromRgb(255,138,0)):new SolidColorBrush(Color.FromArgb(82,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        b.Click+=(_,_)=>{annotationDash=style;ApplySelectedAnnotationStyle();RefreshSecondaryBar();};
        return b;
    }

    static DoubleCollection? DashFor(int s)=>s switch{1=>new DoubleCollection{4,3},2=>new DoubleCollection{8,4},3=>new DoubleCollection{1,3},_=>null};

    void BuildMosaicSettings()
    {
        var modeRoot=new StackPanel{Margin=new Thickness(6,5,12,8)};
        modeRoot.Children.Add(new TextBlock{Text=L("模式"),Foreground=Brushes.White,Opacity=.84,Margin=new Thickness(2,0,2,5)});
        var mrow=new StackPanel{Orientation=Orientation.Horizontal};
        mrow.Children.Add(LabelToggle(L("框選"),!mosaicBrush,()=>{mosaicBrush=false;RefreshSecondaryBar();}));
        mrow.Children.Add(LabelToggle(L("畫筆"),mosaicBrush,()=>{mosaicBrush=true;RefreshSecondaryBar();}));
        modeRoot.Children.Add(mrow);
        secondaryRow.Children.Add(modeRoot);
        secondaryRow.Children.Add(NumberBox(L("馬賽克塊"),()=>mosaicBlock,v=>mosaicBlock=(int)Math.Round(v),2,120));
        if(mosaicBrush)secondaryRow.Children.Add(NumberBox(L("畫筆粗細"),()=>mosaicBrushWidth,v=>mosaicBrushWidth=v,6,200));
    }

    Button LabelToggle(string text,bool active,Action onClick)
    {
        var b=new Button{Content=new TextBlock{Text=text,Foreground=Brushes.White},MinWidth=44,Height=28,Margin=new Thickness(2),Padding=new Thickness(8,0,8,0),Foreground=Brushes.White,Background=active?new SolidColorBrush(Color.FromRgb(255,138,0)):new SolidColorBrush(Color.FromArgb(82,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        b.Click+=(_,_)=>onClick();
        return b;
    }

    Button MosaicChoice(int block,string label)
    {
        var active=mosaicBlock==block;
        var button=new Button{Content=new TextBlock{Text=label,Foreground=Brushes.White},Width=40,Height=28,Margin=new Thickness(2),Padding=new Thickness(0),Foreground=Brushes.White,Background=active?new SolidColorBrush(Color.FromRgb(255,138,0)):new SolidColorBrush(Color.FromArgb(82,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        button.Click+=(_,_)=>{mosaicBlock=block;RefreshSecondaryBar();};
        return button;
    }

    FrameworkElement StrokeWidthBlock()=>NumberBox(L("粗細"),()=>annotationStrokeWidth,v=>annotationStrokeWidth=v,1,60);

    FrameworkElement NumberBox(string title,Func<double> get,Action<double> set,double min,double max)
    {
        var root=new StackPanel{Margin=new Thickness(6,5,6,8)};
        root.Children.Add(new TextBlock{Text=title,Foreground=Brushes.White,Opacity=.84,Margin=new Thickness(2,0,2,5)});
        var row=new StackPanel{Orientation=Orientation.Horizontal};
        var tb=new WpfTextBox{Text=$"{get():0}",Width=54,Height=26,VerticalContentAlignment=System.Windows.VerticalAlignment.Center,Padding=new Thickness(6,0,6,0),Background=new SolidColorBrush(Color.FromArgb(40,255,255,255)),Foreground=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromArgb(120,255,255,255)),BorderThickness=new Thickness(1),CaretBrush=Brushes.White};
        tb.PreviewTextInput+=(_,e)=>{e.Handled=!e.Text.All(char.IsDigit);};
        tb.TextChanged+=(_,_)=>{if(double.TryParse(tb.Text,out var v)){set(Math.Clamp(v,min,max));ApplySelectedAnnotationStyle();}};
        row.Children.Add(tb);
        row.Children.Add(new TextBlock{Text="px",Foreground=Brushes.White,Opacity=.7,VerticalAlignment=System.Windows.VerticalAlignment.Center,Margin=new Thickness(5,0,0,0)});
        root.Children.Add(row);
        return root;
    }

    void BuildTextSettings()
    {
        secondaryRow.Children.Add(ColorPickerBlock(L("文字顏色"),annotationTextColors,annotationTextColorIndex,i=>annotationTextColorIndex=i));
        secondaryRow.Children.Add(ColorPickerBlock(L("背景顏色（× 為透明）"),annotationFillColors,annotationFillColorIndex,i=>annotationFillColorIndex=i));
        secondaryRow.Children.Add(FontSizeBlock());
    }

    FrameworkElement ShapeTypeBlock()
    {
        var root=new StackPanel{Margin=new Thickness(6,5,12,8)};
        root.Children.Add(new TextBlock{Text=L("形狀"),Foreground=Brushes.White,Opacity=.84,Margin=new Thickness(2,0,2,5)});
        var row=new StackPanel{Orientation=Orientation.Horizontal};
        row.Children.Add(ShapeChoice("square",AnnotationTool.Rect,L("矩形")));
        row.Children.Add(ShapeChoice("circle",AnnotationTool.Ellipse,L("圓形 / 橢圓")));
        root.Children.Add(row);
        return root;
    }

    Button ShapeChoice(string icon,AnnotationTool tool,string tip)
    {
        var active=annotationTool==tool;
        var button=new Button{Content=Glyph(icon,18),Width=36,Height=28,Margin=new Thickness(2),Padding=new Thickness(0),ToolTip=tip,Foreground=Brushes.White,Background=active?new SolidColorBrush(Color.FromRgb(255,138,0)):new SolidColorBrush(Color.FromArgb(82,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        button.Click+=(_,_)=>SelectTool(tool);
        return button;
    }

    FrameworkElement FontSizeBlock()=>NumberBox(L("字體大小"),()=>annotationFontSize,v=>annotationFontSize=v,8,120);

    Button FontStep(string icon,Action step)
    {
        var button=new Button{Content=Glyph(icon,16),Width=34,Height=28,Margin=new Thickness(2),Padding=new Thickness(0),Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(82,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        button.Click+=(_,_)=>{step();ApplySelectedAnnotationStyle();};
        return button;
    }

    FrameworkElement ColorPickerBlock(string header,Color[] colors,int selected,Action<int> apply)
    {
        var root=new StackPanel{Margin=new Thickness(6,5,6,8)};
        root.Children.Add(new TextBlock{Text=header,Foreground=Brushes.White,Opacity=.84,Margin=new Thickness(2,0,2,5)});
        var row=new WrapPanel();
        for(var i=0;i<colors.Length;i++)
        {
            var color=colors[i];var idx=i;
            var sw=new Border{Width=26,Height=26,CornerRadius=new CornerRadius(13),Margin=new Thickness(2),Background=new SolidColorBrush(color),BorderBrush=new SolidColorBrush(i==selected?Color.FromRgb(255,138,0):Color.FromArgb(150,255,255,255)),BorderThickness=new Thickness(i==selected?2:1),Cursor=Cursors.Hand,ToolTip=$"{header} {i+1}"};
            if(color.A==0)sw.Child=new TextBlock{Text="×",Foreground=Brushes.White,HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=System.Windows.VerticalAlignment.Center};
            sw.MouseLeftButtonDown+=(_,e)=>{apply(idx);ApplySelectedAnnotationStyle();UpdateToolHighlights();e.Handled=true;};
            row.Children.Add(sw);
        }
        root.Children.Add(row);
        return root;
    }
    static WpfContextMenu? FindContextMenu(DependencyObject? child){while(child!=null){if(child is WpfContextMenu menu)return menu;child=VisualTreeHelper.GetParent(child);}return null;}

    void UpdateToolHighlights()
    {
        foreach(var (tool,button) in toolButtons)
        {
            var active=tool==annotationTool;
            button.Background=active?new SolidColorBrush(Color.FromRgb(255,138,0)):new SolidColorBrush(Color.FromArgb(82,255,255,255));
            button.Foreground=Brushes.White;
        }
    }

    static Cursor CursorForAnnotationTool(AnnotationTool tool)=>tool switch
    {
        AnnotationTool.Text=>Cursors.IBeam,
        AnnotationTool.Pen or AnnotationTool.Highlighter or AnnotationTool.Eraser or AnnotationTool.Mosaic=>Cursors.None,
        AnnotationTool.Line or AnnotationTool.Arrow=>Cursors.Pen,
        AnnotationTool.Rect or AnnotationTool.Ellipse=>Cursors.Arrow,
        _=>Cursors.Cross
    };
    static bool IsBrushTool(AnnotationTool t)=>t is AnnotationTool.Pen or AnnotationTool.Highlighter or AnnotationTool.Eraser or AnnotationTool.Mosaic;

    System.Windows.Media.Brush AnnotationStroke()=>new SolidColorBrush(annotationTextColors[annotationTextColorIndex]);
    System.Windows.Media.Brush AnnotationFill()=>new SolidColorBrush(annotationFillColors[annotationFillColorIndex]);
    void CycleAnnotationTextColor(){annotationTextColorIndex=(annotationTextColorIndex+1)%annotationTextColors.Length;UpdateToolHighlights();}
    void CycleAnnotationFillColor(){annotationFillColorIndex=(annotationFillColorIndex+1)%annotationFillColors.Length;UpdateToolHighlights();}

    void BuildHandles()
    {
        foreach(var name in new[]{"NW","N","NE","E","SE","S","SW","W"})
        {
            var handle=new Border{Tag=name,Width=10,Height=10,CornerRadius=new CornerRadius(5),Background=Brushes.White,BorderBrush=new SolidColorBrush(Color.FromRgb(255,138,0)),BorderThickness=new Thickness(2),Visibility=Visibility.Collapsed,Cursor=CursorForHandle(name)};
            handle.MouseLeftButtonDown+=HandleDown;
            System.Windows.Controls.Panel.SetZIndex(handle,80);
            handles.Add(handle);
            canvas.Children.Add(handle);
        }
    }

    static Cursor CursorForHandle(string handle)=>handle switch
    {
        "NW" or "SE"=>Cursors.SizeNWSE,
        "NE" or "SW"=>Cursors.SizeNESW,
        "N" or "S"=>Cursors.SizeNS,
        "E" or "W"=>Cursors.SizeWE,
        _=>Cursors.Arrow
    };

    UIElement? CreateAnnotation(AnnotationTool tool,Point p)
    {
        var stroke=AnnotationStroke();
        var fill=AnnotationFill();
        var sw=annotationStrokeWidth;
        switch(tool)
        {
            case AnnotationTool.Rect:
                return new WpfRectangle{Stroke=stroke,StrokeThickness=sw,Fill=fill,RadiusX=4,RadiusY=4};
            case AnnotationTool.Ellipse:
                return new Ellipse{Stroke=stroke,StrokeThickness=sw,Fill=fill};
            case AnnotationTool.Line:
                return new Line{Stroke=stroke,StrokeThickness=sw,StrokeDashArray=DashFor(annotationDash),StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,X1=p.X,Y1=p.Y,X2=p.X,Y2=p.Y};
            case AnnotationTool.Arrow:
                var group=new Canvas();
                group.Children.Add(new Line{Stroke=stroke,StrokeThickness=sw,StrokeDashArray=DashFor(annotationDash),StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,X1=p.X,Y1=p.Y,X2=p.X,Y2=p.Y});
                group.Children.Add(new Polygon{Fill=stroke,Points=new PointCollection{p,p,p}});
                return group;
            case AnnotationTool.Pen:
                return new Polyline{Stroke=stroke,StrokeThickness=sw,StrokeDashArray=DashFor(annotationDash),StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,Points=new PointCollection{p}};
            case AnnotationTool.Highlighter:
                return new Polyline{Tag="hl",Stroke=HighlighterStroke(),StrokeThickness=Math.Max(12,sw*4),StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,Points=new PointCollection{p}};
            case AnnotationTool.Mosaic when mosaicBrush:
                var mbBrush=new ImageBrush(MakeMosaic(selectionRect)){ViewportUnits=BrushMappingMode.Absolute,Viewport=selectionRect,Stretch=Stretch.Fill,TileMode=TileMode.None};
                var mpl=new Polyline{Tag="mosaicbrush",Stroke=mbBrush,StrokeThickness=mosaicBrushWidth,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,Points=new PointCollection{p}};
                RenderOptions.SetBitmapScalingMode(mpl,BitmapScalingMode.NearestNeighbor);
                return mpl;
            case AnnotationTool.Mosaic:
                var mimg=new Image{Tag="mosaic",Stretch=Stretch.Fill};
                RenderOptions.SetBitmapScalingMode(mimg,BitmapScalingMode.NearestNeighbor);
                Canvas.SetLeft(mimg,p.X);Canvas.SetTop(mimg,p.Y);
                return mimg;
            case AnnotationTool.Eraser:
                var erBrush=new ImageBrush(source){ViewportUnits=BrushMappingMode.Absolute,Viewport=new Rect(0,0,CW,CH),Stretch=Stretch.Fill,TileMode=TileMode.None};
                var erpl=new Polyline{Tag="eraser",Stroke=erBrush,StrokeThickness=eraserWidth,StrokeLineJoin=PenLineJoin.Round,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,Points=new PointCollection{p}};
                return erpl;
            case AnnotationTool.Text:
                var box=new WpfTextBox{Text=L("文字"),Foreground=stroke,Background=fill,BorderBrush=stroke,BorderThickness=new Thickness(1),Padding=new Thickness(8,4,8,4),FontSize=annotationFontSize,FontFamily=new System.Windows.Media.FontFamily(annotationFontFamily),FontWeight=FontWeights.SemiBold,MinWidth=80};
                Canvas.SetLeft(box,p.X);
                Canvas.SetTop(box,p.Y);
                box.LostFocus+=(_,_)=>{if(string.IsNullOrWhiteSpace(box.Text))canvas.Children.Remove(box);ShowToolbar();};
                return box;
            default:
                return null;
        }
    }

    System.Windows.Media.Brush HighlighterStroke(){var c=annotationTextColors[annotationTextColorIndex];return new SolidColorBrush(Color.FromArgb(110,c.R,c.G,c.B));}

    void AttachAnnotationEvents(UIElement element)
    {
        element.MouseLeftButtonDown+=(_,e)=>
        {
            if(e.ClickCount==2&&element is Canvas ac&&ac.Children.OfType<Polygon>().Any()){AddArrowText(ac);e.Handled=true;return;}
            if(annotationTool!=AnnotationTool.Select)return;
            selectedAnnotation=element;
            if(element is FrameworkElement rf)SelectAnnotationForResize(rf);
            annotationTool=ToolFromAnnotation(element);
            if(element is WpfTextBox box){box.Focus();box.CaretIndex=box.Text.Length;}
            Cursor=CursorForAnnotationTool(annotationTool);
            UpdateToolHighlights();
            e.Handled=true;
        };
    }

    void AddArrowText(Canvas group)
    {
        if(group.Children.OfType<WpfTextBox>().FirstOrDefault() is WpfTextBox exist){exist.Focus();exist.SelectAll();return;}
        var line=group.Children.OfType<Line>().FirstOrDefault();
        if(line==null)return;
        var mx=(line.X1+line.X2)/2;var my=(line.Y1+line.Y2)/2;
        var tb=new WpfTextBox{Text=L("文字"),Foreground=AnnotationStroke(),Background=new SolidColorBrush(Color.FromArgb(180,13,19,33)),BorderThickness=new Thickness(0),Padding=new Thickness(4,1,4,1),FontSize=annotationFontSize,FontWeight=FontWeights.SemiBold,MinWidth=30};
        Canvas.SetLeft(tb,mx-16);Canvas.SetTop(tb,my-14);
        tb.LostFocus+=(_,_)=>{if(string.IsNullOrWhiteSpace(tb.Text))group.Children.Remove(tb);};
        group.Children.Add(tb);
        tb.Focus();tb.SelectAll();
    }
    AnnotationTool ToolFromAnnotation(UIElement element)=>element switch
    {
        WpfRectangle=>AnnotationTool.Rect,
        Ellipse=>AnnotationTool.Ellipse,
        Line=>AnnotationTool.Line,
        Polyline pl when (pl.Tag as string)=="hl"=>AnnotationTool.Highlighter,
        Polyline pl2 when (pl2.Tag as string)=="mosaicbrush"=>AnnotationTool.Mosaic,
        Polyline=>AnnotationTool.Pen,
        Image img when (img.Tag as string)=="mosaic"=>AnnotationTool.Mosaic,
        Canvas group when group.Children.OfType<Polygon>().Any()=>AnnotationTool.Arrow,
        WpfTextBox=>AnnotationTool.Text,
        _=>annotationTool
    };

    void ApplySelectedAnnotationStyle()
    {
        if(selectedAnnotation==null)return;
        var stroke=AnnotationStroke();
        var fill=AnnotationFill();
        switch(selectedAnnotation)
        {
            case Shape shape:
                if((shape.Tag as string)=="mosaicbrush"||(shape.Tag as string)=="eraser")break;
                if((shape.Tag as string)=="hl"){shape.Stroke=HighlighterStroke();break;}
                shape.Stroke=stroke;
                shape.StrokeThickness=annotationStrokeWidth;
                shape.StrokeDashArray=DashFor(annotationDash);
                if(shape is WpfRectangle||shape is Ellipse)shape.Fill=fill;
                break;
            case Canvas group:
                foreach(var line in group.Children.OfType<Line>()){line.Stroke=stroke;line.StrokeThickness=annotationStrokeWidth;line.StrokeDashArray=DashFor(annotationDash);}
                foreach(var head in group.Children.OfType<Polygon>())head.Fill=stroke;
                break;
            case WpfTextBox box:
                box.Foreground=stroke;
                box.Background=fill;
                box.BorderBrush=stroke;
                box.FontSize=annotationFontSize;
                box.FontFamily=new System.Windows.Media.FontFamily(annotationFontFamily);
                break;
        }
    }

    void UpdateAnnotation(UIElement element,Point a,Point b)
    {
        var rect=NormalizeRect(a,b);
        rect.Intersect(selectionRect);
        switch(element)
        {
            case WpfRectangle r:
                Canvas.SetLeft(r,rect.X);Canvas.SetTop(r,rect.Y);r.Width=rect.Width;r.Height=rect.Height;break;
            case Ellipse e:
                Canvas.SetLeft(e,rect.X);Canvas.SetTop(e,rect.Y);e.Width=rect.Width;e.Height=rect.Height;break;
            case Line line:
                line.X1=Math.Clamp(a.X,selectionRect.Left,selectionRect.Right);line.Y1=Math.Clamp(a.Y,selectionRect.Top,selectionRect.Bottom);line.X2=Math.Clamp(b.X,selectionRect.Left,selectionRect.Right);line.Y2=Math.Clamp(b.Y,selectionRect.Top,selectionRect.Bottom);break;
            case Canvas group when group.Children.Count>=2&&group.Children[0] is Line line&&group.Children[1] is Polygon head:
                var x1=Math.Clamp(a.X,selectionRect.Left,selectionRect.Right);var y1=Math.Clamp(a.Y,selectionRect.Top,selectionRect.Bottom);var x2=Math.Clamp(b.X,selectionRect.Left,selectionRect.Right);var y2=Math.Clamp(b.Y,selectionRect.Top,selectionRect.Bottom);
                var angle=Math.Atan2(y2-y1,x2-x1);var len=Math.Max(13d,line.StrokeThickness*3);var spread=Math.PI/7;
                var dist=Math.Sqrt((x2-x1)*(x2-x1)+(y2-y1)*(y2-y1));var ret=Math.Min(len*0.85,dist);
                line.X1=x1;line.Y1=y1;line.X2=x2-Math.Cos(angle)*ret;line.Y2=y2-Math.Sin(angle)*ret;
                head.Points=new PointCollection{new Point(x2,y2),new Point(x2-len*Math.Cos(angle-spread),y2-len*Math.Sin(angle-spread)),new Point(x2-len*Math.Cos(angle+spread),y2-len*Math.Sin(angle+spread))};
                break;
            case Polyline pl:
                pl.Points.Add(new Point(Math.Clamp(b.X,selectionRect.Left,selectionRect.Right),Math.Clamp(b.Y,selectionRect.Top,selectionRect.Bottom)));
                break;
            case Image img when (img.Tag as string)=="mosaic":
                var mr=NormalizeRect(a,b);mr.Intersect(selectionRect);
                Canvas.SetLeft(img,mr.X);Canvas.SetTop(img,mr.Y);img.Width=mr.Width;img.Height=mr.Height;
                if(mr.Width>=2&&mr.Height>=2){var mb=MakeMosaic(mr);if(mb!=null)img.Source=mb;}
                break;
        }
    }

    void RemoveLastAnnotation()
    {
        for(var i=canvas.Children.Count-1;i>=0;i--)
        {
            var child=canvas.Children[i];
            if(child is UIElement ue&&IsChrome(ue))continue;
            if(child is Border bd&&(bd.Tag as string)=="num"&&numberCounter>1)numberCounter--;
            canvas.Children.RemoveAt(i);
            break;
        }
    }

    bool IsChrome(UIElement el)=>el==screen||el==dimMask||el==selection||el==toolbar||el==secondaryBar||el==sideBar||el==sizeTip||el==brushRing||(el is Border b&&handles.Contains(b));

    void EraseAt(Point p)
    {
        var hit=VisualTreeHelper.HitTest(canvas,p)?.VisualHit as DependencyObject;
        while(hit!=null)
        {
            if(hit is UIElement ue&&canvas.Children.Contains(ue)){if(!IsChrome(ue))canvas.Children.Remove(ue);return;}
            hit=VisualTreeHelper.GetParent(hit);
        }
    }

    void StampNumber(Point p)
    {
        var d=Math.Max(20,annotationFontSize+6);
        var col=annotationTextColors[annotationTextColorIndex];
        var lum=0.299*col.R+0.587*col.G+0.114*col.B;
        var badge=new Border{Tag="num",Width=d,Height=d,CornerRadius=new CornerRadius(d/2),Background=new SolidColorBrush(col),Child=new TextBlock{Text=numberCounter.ToString(),Foreground=lum>150?Brushes.Black:Brushes.White,FontWeight=FontWeights.Bold,FontSize=d*0.5,HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=System.Windows.VerticalAlignment.Center}};
        Canvas.SetLeft(badge,p.X-d/2);Canvas.SetTop(badge,p.Y-d/2);
        System.Windows.Controls.Panel.SetZIndex(badge,50);
        canvas.Children.Add(badge);
        numberCounter++;
    }

    void BuildNumberSettings()
    {
        secondaryRow.Children.Add(ColorPickerBlock(L("顏色"),annotationTextColors,annotationTextColorIndex,i=>annotationTextColorIndex=i));
        secondaryRow.Children.Add(NumberBox(L("大小"),()=>annotationFontSize,v=>annotationFontSize=v,12,80));
    }

    string ColorString()=>colorHex?$"#{pickedColor.R:X2}{pickedColor.G:X2}{pickedColor.B:X2}":$"rgb({pickedColor.R},{pickedColor.G},{pickedColor.B})";
    void HideLoupe(){if(loupe!=null)loupe.Visibility=Visibility.Collapsed;}

    double BrushDiameter()=>annotationTool switch
    {
        AnnotationTool.Pen=>annotationStrokeWidth,
        AnnotationTool.Highlighter=>Math.Max(12,annotationStrokeWidth*4),
        AnnotationTool.Eraser=>eraserWidth,
        AnnotationTool.Mosaic=>mosaicBrush?mosaicBrushWidth:mosaicBlock,
        _=>annotationStrokeWidth
    };
    void UpdateBrushRing(Point cp)
    {
        if(brushRing==null){brushRing=new System.Windows.Shapes.Ellipse{Stroke=new SolidColorBrush(Color.FromArgb(235,255,255,255)),StrokeThickness=1.5,Fill=new SolidColorBrush(Color.FromArgb(26,255,138,0)),IsHitTestVisible=false};canvas.Children.Add(brushRing);System.Windows.Controls.Panel.SetZIndex(brushRing,105);}
        var d=Math.Max(6,BrushDiameter());
        brushRing.Width=d;brushRing.Height=d;
        Canvas.SetLeft(brushRing,cp.X-d/2);Canvas.SetTop(brushRing,cp.Y-d/2);
        brushRing.Visibility=Visibility.Visible;
    }
    void HideBrushRing(){if(brushRing!=null)brushRing.Visibility=Visibility.Collapsed;}

    void BuildLoupe()
    {
        loupeImg=new Image{Width=130,Height=130,Stretch=Stretch.Fill,IsHitTestVisible=false};
        RenderOptions.SetBitmapScalingMode(loupeImg,BitmapScalingMode.NearestNeighbor);
        var cross=new Border{Width=12,Height=12,BorderBrush=new SolidColorBrush(Color.FromArgb(235,255,255,255)),BorderThickness=new Thickness(1.5),HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=System.Windows.VerticalAlignment.Center,IsHitTestVisible=false};
        var zoomGrid=new Grid{Width=130,Height=130};
        zoomGrid.Children.Add(loupeImg);zoomGrid.Children.Add(cross);
        loupeSwatch=new Border{Width=14,Height=14,BorderBrush=Brushes.White,BorderThickness=new Thickness(1),Margin=new Thickness(0,0,6,0),VerticalAlignment=System.Windows.VerticalAlignment.Center};
        loupeText=new TextBlock{Foreground=Brushes.White,FontSize=12,VerticalAlignment=System.Windows.VerticalAlignment.Center};
        var valRow=new StackPanel{Orientation=Orientation.Horizontal};
        valRow.Children.Add(loupeSwatch);valRow.Children.Add(loupeText);
        var hint=new TextBlock{Text=L("Shift 切換格式 · C 複製"),Foreground=Brushes.White,Opacity=.6,FontSize=10,Margin=new Thickness(0,3,0,0)};
        var info=new StackPanel{Margin=new Thickness(8,6,8,8)};
        info.Children.Add(valRow);info.Children.Add(hint);
        var body=new StackPanel();body.Children.Add(zoomGrid);body.Children.Add(info);
        loupe=new Border{Visibility=Visibility.Collapsed,CornerRadius=new CornerRadius(8),Background=new SolidColorBrush(Color.FromArgb(235,13,19,33)),BorderBrush=new SolidColorBrush(Color.FromArgb(140,255,138,0)),BorderThickness=new Thickness(1),Child=body,IsHitTestVisible=false};
        canvas.Children.Add(loupe);
        System.Windows.Controls.Panel.SetZIndex(loupe,110);
    }

    void UpdateLoupe(Point cp)
    {
        if(srcPixels==null)return;
        if(loupe==null)BuildLoupe();
        var scaleX=source.PixelWidth/CW;var scaleY=source.PixelHeight/CH;
        pickPx=(int)Math.Clamp(cp.X*scaleX,0,source.PixelWidth-1);
        pickPy=(int)Math.Clamp(cp.Y*scaleY,0,source.PixelHeight-1);
        const int R=7;
        int x0=Math.Clamp(pickPx-R,0,source.PixelWidth-1),y0=Math.Clamp(pickPy-R,0,source.PixelHeight-1);
        int w=Math.Min(2*R+1,source.PixelWidth-x0),h=Math.Min(2*R+1,source.PixelHeight-y0);
        try{loupeImg!.Source=new CroppedBitmap(source,new Int32Rect(x0,y0,Math.Max(1,w),Math.Max(1,h)));}catch{}
        int o=pickPy*srcStride+pickPx*4;pickedColor=Color.FromRgb(srcPixels[o+2],srcPixels[o+1],srcPixels[o]);
        loupeSwatch!.Background=new SolidColorBrush(pickedColor);
        loupeText!.Text=$"({pickPx},{pickPy})  "+ColorString();
        loupe!.Visibility=Visibility.Visible;loupe.UpdateLayout();
        var lx=cp.X+18;var ly=cp.Y+18;
        if(lx+loupe.ActualWidth>CW-6)lx=cp.X-loupe.ActualWidth-18;
        if(ly+loupe.ActualHeight>CH-6)ly=cp.Y-loupe.ActualHeight-18;
        Canvas.SetLeft(loupe,Math.Max(4,lx));Canvas.SetTop(loupe,Math.Max(4,ly));
    }

    void ClearAnnResizer(){if(annResizer!=null){AdornerLayer.GetAdornerLayer(canvas)?.Remove(annResizer);annResizer=null;}}
    void SelectAnnotationForResize(FrameworkElement fe)
    {
        ClearAnnResizer();
        if(!(fe is WpfRectangle||fe is Ellipse||fe is WpfTextBox))return;
        var layer=AdornerLayer.GetAdornerLayer(canvas);
        if(layer==null)return;
        annResizer=new AnnResizeAdorner(fe);
        layer.Add(annResizer);
    }

    sealed class AnnResizeAdorner : Adorner
    {
        readonly VisualCollection visuals;
        readonly System.Windows.Controls.Primitives.Thumb[] thumbs=new System.Windows.Controls.Primitives.Thumb[4];
        readonly FrameworkElement fe;
        public AnnResizeAdorner(FrameworkElement adorned):base(adorned)
        {
            fe=adorned;
            visuals=new VisualCollection(this);
            var tpl=ThumbTemplate();
            for(int i=0;i<4;i++){var t=new System.Windows.Controls.Primitives.Thumb{Width=12,Height=12,Template=tpl,Cursor=Cursors.SizeAll};thumbs[i]=t;visuals.Add(t);}
            thumbs[0].DragDelta+=(_,e)=>Resize(-e.HorizontalChange,-e.VerticalChange,true,true);
            thumbs[1].DragDelta+=(_,e)=>Resize(e.HorizontalChange,-e.VerticalChange,false,true);
            thumbs[2].DragDelta+=(_,e)=>Resize(e.HorizontalChange,e.VerticalChange,false,false);
            thumbs[3].DragDelta+=(_,e)=>Resize(-e.HorizontalChange,e.VerticalChange,true,false);
        }
        void Resize(double dw,double dh,bool moveX,bool moveY)
        {
            double curW=double.IsNaN(fe.Width)?fe.ActualWidth:fe.Width;
            double curH=double.IsNaN(fe.Height)?fe.ActualHeight:fe.Height;
            double newW=Math.Max(10,curW+dw),newH=Math.Max(10,curH+dh);
            if(fe is WpfTextBox tb){var ratio=newH/Math.Max(1,curH);tb.FontSize=Math.Max(8,tb.FontSize*ratio);tb.Width=newW;}
            else{fe.Width=newW;fe.Height=newH;}
            var left=Canvas.GetLeft(fe);if(double.IsNaN(left))left=0;
            var top=Canvas.GetTop(fe);if(double.IsNaN(top))top=0;
            if(moveX)Canvas.SetLeft(fe,left-(newW-curW));
            if(moveY)Canvas.SetTop(fe,top-(newH-curH));
            InvalidateArrange();
        }
        static ControlTemplate ThumbTemplate()
        {
            var tpl=new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb));
            var f=new FrameworkElementFactory(typeof(Border));
            f.SetValue(Border.BackgroundProperty,Brushes.White);
            f.SetValue(Border.BorderBrushProperty,new SolidColorBrush(Color.FromRgb(255,138,0)));
            f.SetValue(Border.BorderThicknessProperty,new Thickness(2));
            f.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));
            tpl.VisualTree=f;
            return tpl;
        }
        protected override int VisualChildrenCount=>visuals.Count;
        protected override Visual GetVisualChild(int i)=>visuals[i];
        protected override Size MeasureOverride(Size c){foreach(var t in thumbs)t.Measure(new Size(12,12));return base.MeasureOverride(c);}
        protected override Size ArrangeOverride(Size finalSize)
        {
            var w=AdornedElement.RenderSize.Width;var h=AdornedElement.RenderSize.Height;
            thumbs[0].Arrange(new Rect(-6,-6,12,12));
            thumbs[1].Arrange(new Rect(w-6,-6,12,12));
            thumbs[2].Arrange(new Rect(w-6,h-6,12,12));
            thumbs[3].Arrange(new Rect(-6,h-6,12,12));
            return finalSize;
        }
    }

}
