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

public sealed partial class ScreenCaptureOverlay : Window
{
    enum CaptureMode { None, Drawing, Moving, Resizing }
    enum AnnotationTool { Select, Rect, Ellipse, Line, Arrow, Text, Pen, Highlighter, Mosaic, Eraser, Number, ColorPicker }
    enum PinEdge { Glow, Border, None }

    readonly Canvas canvas=new();
    readonly Canvas outerCanvas=new();
    // Select the in-place translation layer: It is rendered as a child element of the canvas along with the Crop() function (simply copy or save to obtain the composite image with the translation).
    readonly Canvas translationLayer=new(){IsHitTestVisible=false};
    bool translateMode;
    System.Threading.CancellationTokenSource? translateCts;
    Rect translatedRect;
    string? translateTempPath;
    bool pinned;
    Window? pinBar;
    WpfRectangle? pinGlow;
    double pinScale=1.0;
    bool pinDragArmed;
    Point pinDragStart;
    PinEdge pinEdge=PinEdge.Glow;
    double CW,CH;
    readonly Image screen=new(){Stretch=Stretch.Fill,IsHitTestVisible=false};
    readonly Border selection=new(){BorderBrush=new SolidColorBrush(Color.FromRgb(255,138,0)),BorderThickness=new Thickness(2),Visibility=Visibility.Collapsed,IsHitTestVisible=false};
    readonly Border toolbar=new(){Visibility=Visibility.Collapsed,CornerRadius=new CornerRadius(13),Padding=new Thickness(6),Background=new SolidColorBrush(Color.FromArgb(232,13,19,33)),BorderBrush=new SolidColorBrush(Color.FromArgb(125,255,138,0)),BorderThickness=new Thickness(1)};
    readonly Border secondaryBar=new(){Visibility=Visibility.Collapsed,CornerRadius=new CornerRadius(13),Padding=new Thickness(6),Background=new SolidColorBrush(Color.FromArgb(232,13,19,33)),BorderBrush=new SolidColorBrush(Color.FromArgb(125,255,138,0)),BorderThickness=new Thickness(1)};
    readonly StackPanel secondaryRow=new(){Orientation=Orientation.Horizontal};
    readonly TextBlock sizeTip=new(){Visibility=Visibility.Collapsed,Foreground=Brushes.White,FontSize=12,Padding=new Thickness(8,4,8,4),IsHitTestVisible=false,Background=new SolidColorBrush(Color.FromArgb(210,13,19,33))};
    readonly System.Windows.Shapes.Path dimMask=new(){Fill=new SolidColorBrush(Color.FromArgb(120,0,0,0)),IsHitTestVisible=false};
    readonly GeometryGroup maskGeo=new(){FillRule=FillRule.EvenOdd};
    readonly RectangleGeometry maskOuter=new(),maskHole=new();
    readonly List<DetectWindow> detectWindows=[];
    bool autoActive=true,selectionCommitted,selAnimating,hasSecondary;
    Rect autoCandidate;
    int lastDetectX=int.MinValue,lastDetectY=int.MinValue;
    Rect lastRawRect;
    byte[]? srcPixels;
    int srcStride;
    readonly List<Border> handles=[];
    readonly Action<string> completed;
    readonly Action<string,System.Drawing.Rectangle>? completedWithRect;
    readonly bool autoTranslateOnSelect;
    readonly Action? closed;
    readonly string captureDirectory;
    readonly string language;
    BitmapSource source;
    readonly Rect virtualScreen;
    Point start,dragStart;
    Rect selectionRect,dragStartRect;
    CaptureMode mode;
    string resizeHandle="";
    AnnotationTool annotationTool;
    UIElement? activeAnnotation;
    UIElement? selectedAnnotation;
    bool mosaicBrush;
    double mosaicBrushWidth=24;
    double borderWidth=3;
    // Default values that can be changed on the Settings page (synchronized by DeskNestService): Default save format and "Copy when saving"
    public static string DefaultFormat="png";
    public static bool CopyOnSave;
    string outputFormat=captureFormats.Contains(DefaultFormat)?DefaultFormat:"png";
    static readonly string[] captureFormats={"png","jpg","bmp","tiff"};
    Button? formatBtn;
    bool colorHex=true;
    int pickPx,pickPy;
    Color pickedColor;
    Border? loupe;Image? loupeImg;TextBlock? loupeText;Border? loupeSwatch;
    Adorner? annResizer;
    System.Windows.Shapes.Ellipse? brushRing;
    Point lastCanvasPt;
    Point annotationStart;
    readonly Dictionary<AnnotationTool,Button> toolButtons=[];
    readonly Color[] annotationTextColors={Color.FromRgb(255,138,0),Color.FromRgb(255,255,255),Color.FromRgb(13,19,33),Color.FromRgb(239,68,68),Color.FromRgb(34,197,94),Color.FromRgb(46,144,250),Color.FromRgb(126,86,217)};
    readonly Color[] annotationFillColors={Color.FromArgb(0,0,0,0),Color.FromArgb(170,13,19,33),Color.FromArgb(135,255,243,229),Color.FromArgb(120,255,138,0),Color.FromArgb(110,239,68,68),Color.FromArgb(105,46,144,250)};
    int annotationTextColorIndex,annotationFillColorIndex=0;
    double annotationFontSize=20;
    double annotationStrokeWidth=3;
    int mosaicBlock=14,annotationDash;
    double eraserWidth=20;
    int numberCounter=1;
    bool outputBorder,keepAspect,refreshing;
    double outputCorner;
    readonly Border sideBar=new(){Visibility=Visibility.Collapsed,CornerRadius=new CornerRadius(11),Padding=new Thickness(5),Background=new SolidColorBrush(Color.FromArgb(232,13,19,33)),BorderBrush=new SolidColorBrush(Color.FromArgb(125,255,138,0)),BorderThickness=new Thickness(1)};
    Button? borderBtn,cornerBtn,aspectBtn;
    readonly System.Windows.Threading.DispatcherTimer holdTimer=new(){Interval=TimeSpan.FromMilliseconds(650)};
    string annotationFontFamily="Microsoft JhengHei UI";

    ScreenCaptureOverlay(string captureDirectory,Action<string> completed,Action? closed,string language,bool autoTranslateOnSelect=false)
    {
        this.captureDirectory=captureDirectory;
        this.completed=completed;
        this.completedWithRect=null;
        this.closed=closed;
        this.language=language;
        this.autoTranslateOnSelect=autoTranslateOnSelect;
        var physical=Forms.SystemInformation.VirtualScreen;
        virtualScreen=new Rect(physical.Left,physical.Top,physical.Width,physical.Height);
        Left=SystemParameters.VirtualScreenLeft;
        Top=SystemParameters.VirtualScreenTop;
        Width=SystemParameters.VirtualScreenWidth;
        Height=SystemParameters.VirtualScreenHeight;
        WindowStyle=WindowStyle.None;
        ResizeMode=ResizeMode.NoResize;
        ShowInTaskbar=false;
        Topmost=true;
        Cursor=Cursors.Cross;
        Background=Brushes.Black;
        AllowsTransparency=false;
        Focusable=true;
        // Disable the input method in the selected area: Otherwise, the Chinese input method will override shortcut keys such as Shift+C (the comment text box has its own input context when it has focus and is not affected).
        InputMethod.SetIsInputMethodEnabled(this,false);

        source=Grab(physical);
        screen.Source=source;
        SnapshotWindows();
        try{srcStride=source.PixelWidth*4;srcPixels=new byte[srcStride*source.PixelHeight];source.CopyPixels(srcPixels,srcStride,0);}catch{srcPixels=null;}
        canvas.Background=new SolidColorBrush(Color.FromArgb(78,0,0,0));
        canvas.Children.Add(screen);
        canvas.Children.Add(dimMask);
        canvas.Children.Add(translationLayer);
        canvas.Children.Add(selection);
        canvas.Children.Add(sizeTip);
        outerCanvas.Children.Add(canvas);
        outerCanvas.Children.Add(toolbar);
        outerCanvas.Children.Add(secondaryBar);
        outerCanvas.Children.Add(sideBar);
        Canvas.SetLeft(canvas,0);Canvas.SetTop(canvas,0);
        System.Windows.Controls.Panel.SetZIndex(screen,0);
        System.Windows.Controls.Panel.SetZIndex(dimMask,10);
        System.Windows.Controls.Panel.SetZIndex(translationLayer,15);
        System.Windows.Controls.Panel.SetZIndex(selection,20);
        System.Windows.Controls.Panel.SetZIndex(sizeTip,90);
        System.Windows.Controls.Panel.SetZIndex(toolbar,100);
        System.Windows.Controls.Panel.SetZIndex(secondaryBar,100);
        System.Windows.Controls.Panel.SetZIndex(sideBar,100);
        maskGeo.Children.Add(maskOuter);maskGeo.Children.Add(maskHole);dimMask.Data=maskGeo;
        BuildHandles();
        BuildToolbar();
        BuildSideBar();
        holdTimer.Tick+=(_,_)=>{if((GetAsyncKeyState(1)&0x8000)==0){holdTimer.Stop();return;}RefreshShot();};
        Content=outerCanvas;
        // Preload the OCR feature in the sidebar; the model loads in the background while the user selects the area, and results appear almost instantly when the user clicks “Screenshot Recognition.”
        OcrSidecarService.WarmUp();

        Loaded+=(_,_)=>{CW=ActualWidth;CH=ActualHeight;canvas.Width=CW;canvas.Height=CH;screen.Width=CW;screen.Height=CH;maskOuter.Rect=new Rect(0,0,CW,CH);UpdateDimMask(null);PreloadUia();Focus();};
        MouseLeftButtonDown+=Down;
        MouseMove+=Move;
        MouseLeftButtonUp+=Up;
        MouseWheel+=OnWheel;
        MouseRightButtonUp+=(_,_)=>{if(pinned)ShowPinMenu();};
        LocationChanged+=(_,_)=>{if(pinned)PositionPinBar();};
        KeyDown+=Overlay_KeyDown;
        Closed+=(_,_)=>{try{translateCts?.Cancel();translateCts?.Dispose();}catch{}try{if(translateTempPath!=null&&File.Exists(translateTempPath))File.Delete(translateTempPath);}catch{}try{pinBar?.Close();}catch{}this.closed?.Invoke();};
    }

    ScreenCaptureOverlay(string captureDirectory,Action<string,System.Drawing.Rectangle> completedWithRect,Action? closed,string language)
        : this(captureDirectory,_=>{},closed,language)
    {
        this.completedWithRect=completedWithRect;
    }

    public static void Begin(string captureDirectory,Action<string> completed,Action? closed=null,string language="zh-TW",bool autoTranslateOnSelect=false)=>new ScreenCaptureOverlay(captureDirectory,completed,closed,language,autoTranslateOnSelect).Show();
    public static void Begin(string captureDirectory,Action<string,System.Drawing.Rectangle> completedWithRect,Action? closed=null,string language="zh-TW")=>new ScreenCaptureOverlay(captureDirectory,completedWithRect,closed,language).Show();
    string L(string value)=>Localization.T(value,language);

    static BitmapSource Grab(Drawing.Rectangle b)
    {
        using var bmp=new Drawing.Bitmap(b.Width,b.Height,Drawing.Imaging.PixelFormat.Format32bppArgb);
        using(var g=Drawing.Graphics.FromImage(bmp))g.CopyFromScreen(b.Left,b.Top,0,0,b.Size);
        var h=bmp.GetHbitmap();
        try{return System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(h,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());}
        finally{DeleteObject(h);}
    }

    void Down(object s,MouseButtonEventArgs e)
    {
        if(IsDescendantOf(toolbar,e.OriginalSource as DependencyObject)||IsDescendantOf(sideBar,e.OriginalSource as DependencyObject)||IsDescendantOf(secondaryBar,e.OriginalSource as DependencyObject))return;
        var p=e.GetPosition(canvas);
        if(pinned&&e.ClickCount==2){Close();return;}
        if(pinned&&(annotationTool==AnnotationTool.Select||!selectionRect.Contains(p))){pinDragArmed=true;pinDragStart=e.GetPosition(this);return;}
        ClearAnnResizer();
        if(!selectionCommitted)
        {
            mode=CaptureMode.Drawing;
            start=p;
            autoActive=false;
            CaptureMouse();
            return;
        }
        if(annotationTool==AnnotationTool.Number&&selectionRect.Contains(p))
        {
            mode=CaptureMode.None;StampNumber(p);e.Handled=true;return;
        }
        if(annotationTool!=AnnotationTool.Select&&selectionRect.Contains(p))
        {
            mode=CaptureMode.None;
            annotationStart=p;
            activeAnnotation=CreateAnnotation(annotationTool,p);
            if(activeAnnotation!=null){AttachAnnotationEvents(activeAnnotation);selectedAnnotation=activeAnnotation;System.Windows.Controls.Panel.SetZIndex(activeAnnotation,50);canvas.Children.Add(activeAnnotation);System.Windows.Controls.Panel.SetZIndex(toolbar,100);System.Windows.Controls.Panel.SetZIndex(sizeTip,90);}
            CaptureMouse();
            e.Handled=true;
            return;
        }
        if(!pinned&&selection.Visibility==Visibility.Visible&&selectionRect.Contains(p))
        {
            mode=CaptureMode.Moving;
            dragStart=p;
            dragStartRect=selectionRect;
            Cursor=Cursors.SizeAll;
            HideToolbar();
            CaptureMouse();
            return;
        }
        if(pinned)return;
        mode=CaptureMode.Drawing;
        start=p;
        selectionRect=new Rect(start,new Size(0,0));
        selection.Visibility=Visibility.Visible;
        HideToolbar();
        UpdateSelection(selectionRect);
        CaptureMouse();
    }

    static bool IsDescendantOf(DependencyObject parent,DependencyObject? child)
    {
        while(child!=null)
        {
            if(ReferenceEquals(child,parent))return true;
            child=VisualTreeHelper.GetParent(child);
        }
        return false;
    }

    void HandleDown(object s,MouseButtonEventArgs e)
    {
        if(s is not Border handle||handle.Tag is not string tag)return;
        mode=CaptureMode.Resizing;
        resizeHandle=tag;
        dragStart=e.GetPosition(canvas);
        dragStartRect=selectionRect;
        Cursor=CursorForHandle(tag);
        HideToolbar();
        CaptureMouse();
        e.Handled=true;
    }

    void Move(object s,System.Windows.Input.MouseEventArgs e)
    {
        var p=e.GetPosition(canvas);
        if(pinned&&pinDragArmed&&e.LeftButton==MouseButtonState.Pressed){var cur=e.GetPosition(this);if(Math.Abs(cur.X-pinDragStart.X)>=4||Math.Abs(cur.Y-pinDragStart.Y)>=4){pinDragArmed=false;try{DragMove();}catch{}}return;}
        if(!selectionCommitted)
        {
            if(mode==CaptureMode.Drawing&&e.LeftButton==MouseButtonState.Pressed){if(Math.Abs(p.X-start.X)>=4||Math.Abs(p.Y-start.Y)>=4)UpdateSelection(NormalizeRect(start,p));}
            else if(autoActive){HoverDetect();UpdateLoupe(p);}
            return;
        }
        if(annotationTool==AnnotationTool.ColorPicker){UpdateLoupe(p);return;}
        lastCanvasPt=p;
        if(IsBrushTool(annotationTool))UpdateBrushRing(p);else HideBrushRing();
        if(activeAnnotation!=null&&e.LeftButton==MouseButtonState.Pressed)
        {
            UpdateAnnotation(activeAnnotation,annotationStart,p);
            e.Handled=true;
            return;
        }
        if(mode==CaptureMode.None)
        {
            if(pinned)Cursor=selectionRect.Contains(p)?(annotationTool!=AnnotationTool.Select?CursorForAnnotationTool(annotationTool):Cursors.Arrow):Cursors.SizeAll;
            else Cursor=selection.Visibility==Visibility.Visible&&selectionRect.Contains(p)?(annotationTool!=AnnotationTool.Select?CursorForAnnotationTool(annotationTool):Cursors.SizeAll):Cursors.Cross;
            return;
        }
        if(e.LeftButton!=MouseButtonState.Pressed)return;
        // Start dragging the selection in translation mode: First, clear the old translation to avoid misalignment or residual text; while holding the Up arrow key, press the new selection to retranslate.
        if(translateMode&&(mode==CaptureMode.Moving||mode==CaptureMode.Resizing)&&translationLayer.Children.Count>0)ClearTranslation();
        if(mode==CaptureMode.Drawing)UpdateSelection(NormalizeRect(start,p));
        else if(mode==CaptureMode.Moving)
        {
            var delta=p-dragStart;
            var next=new Rect(dragStartRect.X+delta.X,dragStartRect.Y+delta.Y,dragStartRect.Width,dragStartRect.Height);
            next.X=Math.Clamp(next.X,0,Math.Max(0,ActualWidth-next.Width));
            next.Y=Math.Clamp(next.Y,0,Math.Max(0,ActualHeight-next.Height));
            UpdateSelection(next);
        }
        else if(mode==CaptureMode.Resizing)UpdateSelection(ResizeRect(dragStartRect,p-dragStart,resizeHandle));
    }

    void Up(object s,MouseButtonEventArgs e)
    {
        pinDragArmed=false;
        if(!selectionCommitted)
        {
            ReleaseMouseCapture();
            mode=CaptureMode.None;
            var p=e.GetPosition(canvas);
            var moved=Math.Abs(p.X-start.X)>=4||Math.Abs(p.Y-start.Y)>=4;
            var r=moved?NormalizeRect(start,p):autoCandidate;
            if(r.Width<4||r.Height<4){autoActive=true;return;}
            CommitSelection(r);
            return;
        }
        if(activeAnnotation!=null)
        {
            ReleaseMouseCapture();
            if(activeAnnotation is WpfTextBox text)
            {
                text.Focus();
                text.SelectAll();
            }
            else
            {
                // Editing handles (rectangular/circular corners, line/arrow endpoints) appear immediately after drawing, and allow for further adjustments.
                SelectAnnotationForResize(activeAnnotation);
                ShowToolbar();
            }
            activeAnnotation=null;
            e.Handled=true;
            return;
        }
        if(mode==CaptureMode.None)return;
        ReleaseMouseCapture();
        mode=CaptureMode.None;
        Cursor=selection.Visibility==Visibility.Visible&&annotationTool!=AnnotationTool.Select?CursorForAnnotationTool(annotationTool):Cursors.Cross;
        if(selectionRect.Width<4||selectionRect.Height<4){HideSelection();return;}
        ShowToolbar();
        // Selection is moved or scaled in translation mode: Retranslate based on the new selection
        if(translateMode&&selectionRect!=translatedRect)
        {
            translateCts?.Cancel();
            translateCts=new System.Threading.CancellationTokenSource();
            translatedRect=selectionRect;
            _ = RunTranslateAsync(translateCts.Token);
        }
    }

    void Overlay_KeyDown(object sender,KeyEventArgs e)
    {
        // Do not capture keyboard shortcuts when the annotation text input field has focus (to prevent accidentally triggering OCR when typing a capital "C")
        if(e.OriginalSource is WpfTextBox)return;
        // When the input method is enabled, keystrokes are interpreted as `ImeProcessed`; you must restore the actual key values, otherwise shortcuts such as Shift+C will not work.
        var key=e.Key==Key.ImeProcessed?e.ImeProcessedKey:e.Key;
        if(key==Key.Escape){translateCts?.Cancel();Close();e.Handled=true;}
        else if(annotationTool!=AnnotationTool.ColorPicker&&(Keyboard.Modifiers&ModifierKeys.Shift)!=0&&key==Key.C){StartOcr();e.Handled=true;}
        else if(annotationTool==AnnotationTool.ColorPicker&&(key==Key.LeftShift||key==Key.RightShift)){colorHex=!colorHex;if(loupeText!=null)loupeText.Text=$"({pickPx},{pickPy})  "+ColorString();e.Handled=true;}
        else if(annotationTool==AnnotationTool.ColorPicker&&key==Key.C){try{Clipboard.SetText(ColorString());}catch{}e.Handled=true;}
        else if(key==Key.Enter){CompleteCapture();e.Handled=true;}
        else if((Keyboard.Modifiers&ModifierKeys.Control)!=0&&key==Key.C){CopyOnly();e.Handled=true;}
        else if((Keyboard.Modifiers&ModifierKeys.Control)!=0&&key==Key.S){SaveOnly();e.Handled=true;}
        else if((Keyboard.Modifiers&ModifierKeys.Control)!=0&&key==Key.Z){RemoveLastAnnotation();e.Handled=true;}
        else if(key==Key.Delete&&selectedAnnotation!=null&&canvas.Children.Contains(selectedAnnotation)){canvas.Children.Remove(selectedAnnotation);selectedAnnotation=null;ClearAnnResizer();e.Handled=true;}
        // One-key shortcuts after the selection is complete (no modifier keys; C/Shift in the color picker mode has already been handled in the branch above, and focus is returned early when the comment text box is selected)
        else if(selectionCommitted&&Keyboard.Modifiers==ModifierKeys.None)
        {
            switch(key)
            {
                case Key.L:StartScrollingCapture();e.Handled=true;break;
                case Key.V:StartRecording();e.Handled=true;break;
                case Key.Q:StartTranslate();e.Handled=true;break;
                case Key.S:StartOcr();e.Handled=true;break;
                case Key.D:Pin();e.Handled=true;break;
                case Key.G:CycleFormat();e.Handled=true;break;
                case Key.F5:RefreshShot();e.Handled=true;break;
                case Key.R:SelectTool(AnnotationTool.Rect);e.Handled=true;break;
                case Key.O:SelectTool(AnnotationTool.Ellipse);e.Handled=true;break;
                case Key.I:SelectTool(AnnotationTool.Line);e.Handled=true;break;
                case Key.A:SelectTool(AnnotationTool.Arrow);e.Handled=true;break;
                case Key.P:SelectTool(AnnotationTool.Pen);e.Handled=true;break;
                case Key.H:SelectTool(AnnotationTool.Highlighter);e.Handled=true;break;
                case Key.M:SelectTool(AnnotationTool.Mosaic);e.Handled=true;break;
                case Key.N:SelectTool(AnnotationTool.Number);e.Handled=true;break;
                case Key.E:SelectTool(AnnotationTool.Eraser);e.Handled=true;break;
                case Key.K:SelectTool(AnnotationTool.ColorPicker);e.Handled=true;break;
                case Key.T:SelectTool(AnnotationTool.Text);e.Handled=true;break;
            }
        }
    }

    void OnWheel(object sender,System.Windows.Input.MouseWheelEventArgs e)
    {
        if(pinned&&annotationTool==AnnotationTool.Select){ZoomPin(e.Delta);e.Handled=true;return;}
        if(!selectionCommitted)return;
        int d=e.Delta>0?1:-1;
        switch(annotationTool)
        {
            case AnnotationTool.Pen:case AnnotationTool.Highlighter:case AnnotationTool.Line:case AnnotationTool.Arrow:annotationStrokeWidth=Math.Clamp(annotationStrokeWidth+d,1,60);break;
            case AnnotationTool.Eraser:eraserWidth=Math.Clamp(eraserWidth+d*2,6,200);break;
            case AnnotationTool.Mosaic:if(mosaicBrush)mosaicBrushWidth=Math.Clamp(mosaicBrushWidth+d*2,6,200);else mosaicBlock=(int)Math.Clamp(mosaicBlock+d,2,120);break;
            default:return;
        }
        RefreshSecondaryBar();
        if(IsBrushTool(annotationTool))UpdateBrushRing(lastCanvasPt);
        e.Handled=true;
    }

    static Rect NormalizeRect(Point a,Point b)=>new(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Abs(a.X-b.X),Math.Abs(a.Y-b.Y));

    Rect ResizeRect(Rect startRect,Vector delta,string handle)
    {
        var left=startRect.Left;var top=startRect.Top;var right=startRect.Right;var bottom=startRect.Bottom;
        if(handle.Contains('W'))left+=delta.X;
        if(handle.Contains('E'))right+=delta.X;
        if(handle.Contains('N'))top+=delta.Y;
        if(handle.Contains('S'))bottom+=delta.Y;
        left=Math.Clamp(left,0,ActualWidth);
        right=Math.Clamp(right,0,ActualWidth);
        top=Math.Clamp(top,0,ActualHeight);
        bottom=Math.Clamp(bottom,0,ActualHeight);
        if(right<left)(left,right)=(right,left);
        if(bottom<top)(top,bottom)=(bottom,top);
        var rect=new Rect(left,top,Math.Max(1,right-left),Math.Max(1,bottom-top));
        if(keepAspect&&startRect.Width>1&&startRect.Height>1)
        {
            var ratio=startRect.Width/startRect.Height;var w=rect.Width;var h=rect.Height;
            if(handle=="N"||handle=="S")w=h*ratio;else h=w/ratio;
            var nx=rect.X;var ny=rect.Y;
            if(handle.Contains('W'))nx=rect.Right-w;
            if(handle.Contains('N'))ny=rect.Bottom-h;
            rect=new Rect(nx,ny,Math.Max(1,w),Math.Max(1,h));
        }
        return rect;
    }

    void UpdateSelection(Rect rect)
    {
        ClearSelectionAnims();
        selectionRect=rect;
        Canvas.SetLeft(selection,rect.X);
        Canvas.SetTop(selection,rect.Y);
        selection.Width=rect.Width;
        selection.Height=rect.Height;
        UpdateHandles();
        UpdateSizeTip();
        UpdateDimMask(rect);
    }

    void UpdateHandles()
    {
        if(selection.Visibility!=Visibility.Visible){foreach(var h in handles)h.Visibility=Visibility.Collapsed;return;}
        var points=new Dictionary<string,Point>
        {
            ["NW"]=new(selectionRect.Left,selectionRect.Top),
            ["N"]=new(selectionRect.Left+selectionRect.Width/2,selectionRect.Top),
            ["NE"]=new(selectionRect.Right,selectionRect.Top),
            ["E"]=new(selectionRect.Right,selectionRect.Top+selectionRect.Height/2),
            ["SE"]=new(selectionRect.Right,selectionRect.Bottom),
            ["S"]=new(selectionRect.Left+selectionRect.Width/2,selectionRect.Bottom),
            ["SW"]=new(selectionRect.Left,selectionRect.Bottom),
            ["W"]=new(selectionRect.Left,selectionRect.Top+selectionRect.Height/2)
        };
        foreach(var h in handles)
        {
            var name=(string)h.Tag;
            var p=points[name];
            h.Visibility=Visibility.Visible;
            Canvas.SetLeft(h,p.X-h.Width/2);
            Canvas.SetTop(h,p.Y-h.Height/2);
        }
    }

    void UpdateSizeTip()
    {
        var scaleX=source.PixelWidth/CW;
        var scaleY=source.PixelHeight/CH;
        sizeTip.Text=$"{(int)(selectionRect.Width*scaleX)} × {(int)(selectionRect.Height*scaleY)}";
        sizeTip.Visibility=selection.Visibility;
        Canvas.SetLeft(sizeTip,Math.Clamp(selectionRect.X,6,Math.Max(6,ActualWidth-130)));
        Canvas.SetTop(sizeTip,Math.Max(6,selectionRect.Y-32));
    }

    void ShowToolbar()
    {
        if(pinned){toolbar.Visibility=Visibility.Visible;PositionPinBar();return;}
        toolbar.Visibility=Visibility.Visible;
        toolbar.UpdateLayout();
        var x=selectionRect.Right-toolbar.ActualWidth;
        var y=selectionRect.Bottom+10;
        if(y+toolbar.ActualHeight>ActualHeight-8)y=selectionRect.Top-toolbar.ActualHeight-10;
        Canvas.SetLeft(toolbar,Math.Clamp(x,8,Math.Max(8,ActualWidth-toolbar.ActualWidth-8)));
        Canvas.SetTop(toolbar,Math.Clamp(y,8,Math.Max(8,ActualHeight-toolbar.ActualHeight-8)));
        // The sidebar (outer border/rounded corners/maintain aspect ratio) is now toggled via the "More..." button on the main toolbar and no longer pops up automatically with the toolbar.
        sideBar.Visibility=Visibility.Collapsed;
        if(hasSecondary)
        {
            secondaryBar.Visibility=Visibility.Visible;secondaryBar.UpdateLayout();
            var ty=Canvas.GetTop(toolbar);var tx=Canvas.GetLeft(toolbar);
            var sy2=ty+toolbar.ActualHeight+8;
            if(sy2+secondaryBar.ActualHeight>ActualHeight-8)sy2=ty-secondaryBar.ActualHeight-8;
            Canvas.SetLeft(secondaryBar,Math.Clamp(tx,8,Math.Max(8,ActualWidth-secondaryBar.ActualWidth-8)));
            Canvas.SetTop(secondaryBar,Math.Clamp(sy2,8,Math.Max(8,ActualHeight-secondaryBar.ActualHeight-8)));
        }
        else secondaryBar.Visibility=Visibility.Collapsed;
    }

    void UpdateDimMask(Rect? bright)
    {
        maskHole.Rect=bright is Rect r&&r.Width>0&&r.Height>0?r:Rect.Empty;
    }

    void HoverDetect()
    {
        if(!GetCursorPos(out var pt))return;
        if(pt.X==lastDetectX&&pt.Y==lastDetectY)return;
        lastDetectX=pt.X;lastDetectY=pt.Y;
        DetectWindow? win=null;
        foreach(var d in detectWindows){if(d.Rect.Contains(pt.X,pt.Y)){win=d;break;}}
        if(win==null)return;
        var best=win.Rect;var bestArea=best.Width*best.Height;
        foreach(var part in win.Parts){if(part.Contains(pt.X,pt.Y)){var a=part.Width*part.Height;if(a<bestArea){best=part;bestArea=a;}}}
        ApplyCandidate(best);
        if(!win.UiaLoaded&&!win.UiaLoading){win.UiaLoading=true;LoadUiaAsync(win);}
    }

    void RedetectAtCursor()
    {
        if(!autoActive||selectionCommitted||mode!=CaptureMode.None)return;
        lastDetectX=int.MinValue;lastDetectY=int.MinValue;
        HoverDetect();
    }

    void PreloadUia()
    {
        var n=0;
        foreach(var w in detectWindows){if(n++>=6)break;if(!w.UiaLoaded&&!w.UiaLoading){w.UiaLoading=true;LoadUiaAsync(w);}}
        HoverDetect();
    }

    void LoadUiaAsync(DetectWindow win)
    {
        var hwnd=win.Hwnd;var winArea=win.Rect.Width*win.Rect.Height;
        System.Threading.Tasks.Task.Run(()=>
        {
            var rects=new List<Rect>();
            try
            {
                var root=System.Windows.Automation.AutomationElement.FromHandle(hwnd);
                var all=root?.FindAll(System.Windows.Automation.TreeScope.Descendants,new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.IsControlElementProperty,true));
                if(all!=null)foreach(System.Windows.Automation.AutomationElement el in all)
                {
                    try{var b=el.Current.BoundingRectangle;if(b.Width>=6&&b.Height>=6&&!double.IsInfinity(b.Width)&&!double.IsInfinity(b.Height)&&b.Width*b.Height<winArea*0.92)rects.Add(b);}catch{}
                    if(rects.Count>3000)break;
                }
            }
            catch{}
            Dispatcher.BeginInvoke(()=>{win.Parts.AddRange(rects);win.UiaLoaded=true;win.UiaLoading=false;RedetectAtCursor();});
        });
    }

    void ApplyCandidate(Rect physical)
    {
        var scaleX=source.PixelWidth/CW;
        var scaleY=source.PixelHeight/CH;
        if(scaleX<=0||scaleY<=0)return;
        var raw=new Rect((physical.X-virtualScreen.X)/scaleX,(physical.Y-virtualScreen.Y)/scaleY,physical.Width/scaleX,physical.Height/scaleY);
        raw.Intersect(new Rect(0,0,ActualWidth,ActualHeight));
        if(raw.Width<2||raw.Height<2)return;
        var visible=selection.Visibility==Visibility.Visible;
        if(visible&&RectsClose(lastRawRect,raw))return;
        lastRawRect=raw;
        var r=TrimToContent(raw);
        var cx=(lastDetectX-virtualScreen.X)/scaleX;var cy=(lastDetectY-virtualScreen.Y)/scaleY;
        if(!r.Contains(cx,cy))r=raw;
        r=EnforceMinSize(r);
        autoCandidate=r;
        selectionRect=r;
        selection.Visibility=Visibility.Visible;
        foreach(var h in handles)h.Visibility=Visibility.Collapsed;
        if(visible)AnimateSelectionTo(r);else SetSelectionInstant(r);
        UpdateSizeTip();
    }

    Rect TrimToContent(Rect c)
    {
        if(srcPixels==null||c.Width<10||c.Height<8)return c;
        var scaleX=source.PixelWidth/CW;
        var scaleY=source.PixelHeight/CH;
        int x0=Math.Clamp((int)(c.X*scaleX),0,source.PixelWidth-1);
        int y0=Math.Clamp((int)(c.Y*scaleY),0,source.PixelHeight-1);
        int x1=Math.Clamp((int)Math.Ceiling((c.X+c.Width)*scaleX),x0+1,source.PixelWidth);
        int y1=Math.Clamp((int)Math.Ceiling((c.Y+c.Height)*scaleY),y0+1,source.PixelHeight);
        int w=x1-x0,h=y1-y0;
        if(w<10||h<8||(long)w*h>1600000)return c;
        long sr=0,sg=0,sb=0;
        foreach(var(cx,cy) in new[]{(x0,y0),(x1-1,y0),(x0,y1-1),(x1-1,y1-1)}){int o=cy*srcStride+cx*4;sb+=srcPixels[o];sg+=srcPixels[o+1];sr+=srcPixels[o+2];}
        int bb=(int)(sb/4),bgc=(int)(sg/4),br=(int)(sr/4);
        int minX=x1,minY=y1,maxX=x0-1,maxY=y0-1;
        for(int y=y0;y<y1;y++)
        {
            int rowoff=y*srcStride;
            for(int x=x0;x<x1;x++)
            {
                int o=rowoff+x*4;
                if(Math.Abs(srcPixels[o]-bb)+Math.Abs(srcPixels[o+1]-bgc)+Math.Abs(srcPixels[o+2]-br)>64)
                {
                    if(x<minX)minX=x;if(x>maxX)maxX=x;if(y<minY)minY=y;if(y>maxY)maxY=y;
                }
            }
        }
        if(maxX<minX||maxY<minY)return c;
        minX=Math.Max(x0,minX-2);minY=Math.Max(y0,minY-2);maxX=Math.Min(x1-1,maxX+2);maxY=Math.Min(y1-1,maxY+2);
        double tw=(maxX-minX+1)/scaleX,th=(maxY-minY+1)/scaleY;
        if(tw<6||th<6)return c;
        var tr=new Rect(minX/scaleX,minY/scaleY,tw,th);
        return tr.Width*tr.Height<c.Width*c.Height*0.97?tr:c;
    }

    BitmapSource? MakeMosaic(Rect c)
    {
        if(srcPixels==null)return null;
        var scaleX=source.PixelWidth/CW;var scaleY=source.PixelHeight/CH;
        int x0=Math.Clamp((int)(c.X*scaleX),0,source.PixelWidth-1);
        int y0=Math.Clamp((int)(c.Y*scaleY),0,source.PixelHeight-1);
        int x1=Math.Clamp((int)Math.Ceiling((c.X+c.Width)*scaleX),x0+1,source.PixelWidth);
        int y1=Math.Clamp((int)Math.Ceiling((c.Y+c.Height)*scaleY),y0+1,source.PixelHeight);
        int w=x1-x0,h=y1-y0;if(w<1||h<1)return null;
        int block=Math.Max(2,(int)Math.Round(mosaicBlock*scaleX));
        int cols=Math.Max(1,(w+block-1)/block),rows=Math.Max(1,(h+block-1)/block);
        var outp=new byte[cols*rows*4];
        for(int by=0;by<rows;by++)for(int bx=0;bx<cols;bx++)
        {
            int sx=x0+bx*block,sy=y0+by*block,ex=Math.Min(sx+block,x1),ey=Math.Min(sy+block,y1);
            long ar=0,ag=0,ab=0,cnt=0;
            for(int y=sy;y<ey;y+=2)for(int x=sx;x<ex;x+=2){int o=y*srcStride+x*4;ab+=srcPixels[o];ag+=srcPixels[o+1];ar+=srcPixels[o+2];cnt++;}
            if(cnt==0)cnt=1;
            int oo=(by*cols+bx)*4;outp[oo]=(byte)(ab/cnt);outp[oo+1]=(byte)(ag/cnt);outp[oo+2]=(byte)(ar/cnt);outp[oo+3]=255;
        }
        var bmp=BitmapSource.Create(cols,rows,96,96,PixelFormats.Bgra32,null,outp,cols*4);
        bmp.Freeze();return bmp;
    }

    Rect EnforceMinSize(Rect r)
    {
        const double m=25;
        var w=Math.Max(r.Width,m);var h=Math.Max(r.Height,m);
        var cx=r.X+r.Width/2;var cy=r.Y+r.Height/2;
        var x=Math.Clamp(cx-w/2,0,Math.Max(0,ActualWidth-w));
        var y=Math.Clamp(cy-h/2,0,Math.Max(0,ActualHeight-h));
        return new Rect(x,y,w,h);
    }

    static bool RectsClose(Rect a,Rect b)=>Math.Abs(a.X-b.X)<2&&Math.Abs(a.Y-b.Y)<2&&Math.Abs(a.Width-b.Width)<2&&Math.Abs(a.Height-b.Height)<2;
    void ClearSelectionAnims()
    {
        if(!selAnimating)return;
        selAnimating=false;
        selection.BeginAnimation(Canvas.LeftProperty,null);
        selection.BeginAnimation(Canvas.TopProperty,null);
        selection.BeginAnimation(FrameworkElement.WidthProperty,null);
        selection.BeginAnimation(FrameworkElement.HeightProperty,null);
        maskHole.BeginAnimation(RectangleGeometry.RectProperty,null);
    }

    void SetSelectionInstant(Rect r)
    {
        ClearSelectionAnims();
        Canvas.SetLeft(selection,r.X);Canvas.SetTop(selection,r.Y);
        selection.Width=r.Width;selection.Height=r.Height;
        maskHole.Rect=r;
    }

    void AnimateSelectionTo(Rect r)
    {
        selAnimating=true;
        var dur=new Duration(TimeSpan.FromMilliseconds(260));
        var ease=new CubicEase{EasingMode=EasingMode.EaseOut};
        selection.BeginAnimation(Canvas.LeftProperty,new DoubleAnimation(r.X,dur){EasingFunction=ease});
        selection.BeginAnimation(Canvas.TopProperty,new DoubleAnimation(r.Y,dur){EasingFunction=ease});
        selection.BeginAnimation(FrameworkElement.WidthProperty,new DoubleAnimation(r.Width,dur){EasingFunction=ease});
        selection.BeginAnimation(FrameworkElement.HeightProperty,new DoubleAnimation(r.Height,dur){EasingFunction=ease});
        maskHole.BeginAnimation(RectangleGeometry.RectProperty,new RectAnimation(r,dur){EasingFunction=ease});
    }

    void CommitSelection(Rect r)
    {
        r=EnforceMinSize(r);
        selectionCommitted=true;
        autoActive=false;
        selection.Visibility=Visibility.Visible;
        UpdateSelection(r);
        ShowToolbar();
        HideLoupe();
        // Automatic Translation Mode: Selecting text triggers in-place translation immediately (the selection box and toolbar remain visible; dragging or zooming the selection area will automatically re-translate the text), rather than closing the window and opening a separate translation window.
        if(autoTranslateOnSelect)Dispatcher.BeginInvoke(new Action(StartTranslate),System.Windows.Threading.DispatcherPriority.Background);
    }

    void SnapshotWindows()
    {
        try
        {
            EnumWindows((h,_)=>
            {
                if(!IsWindowVisible(h)||!GetWindowRect(h,out var r))return true;
                var w=r.Right-r.Left;var ht=r.Bottom-r.Top;
                if(w<8||ht<8||r.Left<=-32000||r.Top<=-32000)return true;
                var dw=new DetectWindow{Hwnd=h,Rect=new Rect(r.Left,r.Top,w,ht)};
                EnumChildWindows(h,(c,_)=>
                {
                    if(IsWindowVisible(c)&&GetWindowRect(c,out var cr)){var cw=cr.Right-cr.Left;var ch=cr.Bottom-cr.Top;if(cw>=6&&ch>=6&&cr.Left>-32000)dw.Parts.Add(new Rect(cr.Left,cr.Top,cw,ch));}
                    return true;
                },IntPtr.Zero);
                detectWindows.Add(dw);
                return true;
            },IntPtr.Zero);
        }
        catch{}
    }

    sealed class DetectWindow{public IntPtr Hwnd;public Rect Rect;public readonly List<Rect> Parts=[];public bool UiaLoaded,UiaLoading;}
    struct RECT{public int Left,Top,Right,Bottom;}
    struct POINT{public int X,Y;}
    delegate bool EnumWindowsProc(IntPtr h,IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool EnumWindows(EnumWindowsProc cb,IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool EnumChildWindows(IntPtr parent,EnumWindowsProc cb,IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool GetWindowRect(IntPtr h,out RECT r);
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr h);
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern bool GetCursorPos(out POINT p);
    [System.Runtime.InteropServices.DllImport("user32.dll")]static extern short GetAsyncKeyState(int vKey);

    void BuildSideBar()
    {
        var col=new StackPanel{Orientation=Orientation.Horizontal};
        borderBtn=SideButton("square",L("外邊框"),ToggleBorder);
        cornerBtn=SideButton("border-radius",L("圓角"),ToggleCorner);
        aspectBtn=SideButton("aspect-ratio",L("保持縦横比"),ToggleAspect);
        col.Children.Add(borderBtn);col.Children.Add(cornerBtn);col.Children.Add(aspectBtn);
        sideBar.Child=col;
    }

    // "More" button: Toggles the sidebar (three tools: Outer Border, Rounded Corners, and Maintain Proportions) to open as a pop-up panel
    void ToggleMoreTools()
    {
        if(sideBar.Visibility==Visibility.Visible){sideBar.Visibility=Visibility.Collapsed;return;}
        sideBar.Visibility=Visibility.Visible;sideBar.UpdateLayout();
        var tlx=Canvas.GetLeft(toolbar);var tly=Canvas.GetTop(toolbar);
        if(double.IsNaN(tlx))tlx=8;if(double.IsNaN(tly))tly=8;
        var sw=sideBar.ActualWidth;var sh=sideBar.ActualHeight;
        var sx=Math.Clamp(tlx+toolbar.ActualWidth-sw,8,Math.Max(8,ActualWidth-sw-8));
        var sy=tly-sh-6;if(sy<8)sy=tly+toolbar.ActualHeight+6;
        Canvas.SetLeft(sideBar,sx);Canvas.SetTop(sideBar,Math.Clamp(sy,8,Math.Max(8,ActualHeight-sh-8)));
    }

    Button SideButton(string icon,string tip,Action action)
    {
        var b=new Button{Content=Glyph(icon),Width=40,Height=38,Margin=new Thickness(3,0,3,0),Padding=new Thickness(0),Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(82,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand,ToolTip=tip};
        b.Click+=(_,_)=>action();
        return b;
    }

    void ToggleBorder(){outputBorder=!outputBorder;SetSideActive(borderBtn,outputBorder);if(outputBorder)ShowOutputStepper(L("邊框粗細"),()=>borderWidth,v=>borderWidth=Math.Clamp(v,1,16),1);else HideSecondary();}
    void ToggleAspect(){keepAspect=!keepAspect;SetSideActive(aspectBtn,keepAspect);}
    void ToggleCorner(){outputCorner=outputCorner>0?0:16;SetSideActive(cornerBtn,outputCorner>0);if(outputCorner>0)ShowOutputStepper(L("圓角半徑"),()=>outputCorner,v=>outputCorner=Math.Clamp(v,0,120),4);else HideSecondary();}
    void ShowOutputStepper(string title,Func<double> get,Action<double> set,double step)
    {
        secondaryRow.Children.Clear();
        secondaryRow.Children.Add(NumberBox(title,get,set,0,300));
        hasSecondary=true;
        if(toolbar.Visibility==Visibility.Visible)ShowToolbar();else secondaryBar.Visibility=Visibility.Visible;
    }
    void HideSecondary(){secondaryRow.Children.Clear();hasSecondary=false;secondaryBar.Visibility=Visibility.Collapsed;if(toolbar.Visibility==Visibility.Visible)ShowToolbar();}
    Button OutStep(string icon,Action step){var b=new Button{Content=Glyph(icon,16),Width=34,Height=28,Margin=new Thickness(2),Padding=new Thickness(0),Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(82,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};b.Click+=(_,_)=>step();return b;}
    static void SetSideActive(Button? b,bool on){if(b!=null)b.Background=on?new SolidColorBrush(Color.FromRgb(255,138,0)):new SolidColorBrush(Color.FromArgb(82,255,255,255));}

    void RefreshShot()
    {
        if(refreshing)return;refreshing=true;
        Visibility=Visibility.Hidden;
        var t=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(90)};
        t.Tick+=(_,_)=>
        {
            t.Stop();
            try{var ns=Grab(Forms.SystemInformation.VirtualScreen);source=ns;screen.Source=ns;screen.Width=CW;screen.Height=CH;srcStride=ns.PixelWidth*4;srcPixels=new byte[srcStride*ns.PixelHeight];ns.CopyPixels(srcPixels,srcStride,0);}catch{}
            Visibility=Visibility.Visible;Activate();refreshing=false;
        };
        t.Start();
    }

    BitmapSource ApplyOutputStyle(BitmapSource crop)
    {
        if(!outputBorder&&outputCorner<=0)return crop;
        var scaleX=source.PixelWidth/CW;
        double rad=outputCorner*scaleX;
        double bw=outputBorder?Math.Max(1,borderWidth*scaleX):0;
        int w=crop.PixelWidth,h=crop.PixelHeight;
        var dv=new DrawingVisual();
        using(var dc=dv.RenderOpen())
        {
            var full=new Rect(0,0,w,h);
            dc.PushClip(new RectangleGeometry(full,rad,rad));
            dc.DrawImage(crop,full);
            dc.Pop();
            if(bw>0)dc.DrawRoundedRectangle(null,new System.Windows.Media.Pen(new SolidColorBrush(Color.FromRgb(255,138,0)),bw),new Rect(bw/2,bw/2,Math.Max(0,w-bw),Math.Max(0,h-bw)),Math.Max(0,rad-bw/2),Math.Max(0,rad-bw/2));
        }
        var rtb=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);
        rtb.Render(dv);rtb.Freeze();return rtb;
    }

    void HideToolbar(){toolbar.Visibility=Visibility.Collapsed;secondaryBar.Visibility=Visibility.Collapsed;sideBar.Visibility=Visibility.Collapsed;}
    void HideSelection(){selection.Visibility=Visibility.Collapsed;sizeTip.Visibility=Visibility.Collapsed;HideToolbar();UpdateHandles();if(!selectionCommitted){autoActive=true;UpdateDimMask(null);}}

    BitmapSource? Crop()
    {
        if(selectionRect.Width<4||selectionRect.Height<4)return null;
        var previousBackground=canvas.Background;
        var previousSelection=selection.Visibility;
        var previousToolbar=toolbar.Visibility;
        var previousTip=sizeTip.Visibility;
        var handleStates=handles.Select(h=>h.Visibility).ToArray();
        var previousMask=dimMask.Visibility;
        var previousLoupe=loupe?.Visibility??Visibility.Collapsed;
        var previousRing=brushRing?.Visibility??Visibility.Collapsed;
        canvas.Background=Brushes.Transparent;
        dimMask.Visibility=Visibility.Collapsed;
        if(loupe!=null)loupe.Visibility=Visibility.Collapsed;
        if(brushRing!=null)brushRing.Visibility=Visibility.Collapsed;
        selection.Visibility=Visibility.Collapsed;
        toolbar.Visibility=Visibility.Collapsed;
        sizeTip.Visibility=Visibility.Collapsed;
        foreach(var h in handles)h.Visibility=Visibility.Collapsed;
        try
        {
            UpdateLayout();
            var scaleX=source.PixelWidth/CW;var scaleY=source.PixelHeight/CH;
            var width=Math.Max(1,(int)Math.Ceiling(CW*scaleX));
            var height=Math.Max(1,(int)Math.Ceiling(CH*scaleY));
            var render=new RenderTargetBitmap(width,height,96*scaleX,96*scaleY,PixelFormats.Pbgra32);
            render.Render(canvas);
            var offX=Canvas.GetLeft(canvas);if(double.IsNaN(offX))offX=0;
            var offY=Canvas.GetTop(canvas);if(double.IsNaN(offY))offY=0;
            var x=Math.Clamp((int)Math.Floor((selectionRect.X+offX)*scaleX),0,width-1);
            var y=Math.Clamp((int)Math.Floor((selectionRect.Y+offY)*scaleY),0,height-1);
            var w=Math.Min(Math.Max(1,(int)Math.Ceiling(selectionRect.Width*scaleX)),width-x);
            var h=Math.Min(Math.Max(1,(int)Math.Ceiling(selectionRect.Height*scaleY)),height-y);
            return ApplyOutputStyle(new CroppedBitmap(render,new Int32Rect(x,y,w,h)));
        }
        finally
        {
            canvas.Background=previousBackground;
            dimMask.Visibility=previousMask;
            if(loupe!=null)loupe.Visibility=previousLoupe;
            if(brushRing!=null)brushRing.Visibility=previousRing;
            selection.Visibility=previousSelection;
            toolbar.Visibility=previousToolbar;
            sizeTip.Visibility=previousTip;
            for(var i=0;i<handles.Count&&i<handleStates.Length;i++)handles[i].Visibility=handleStates[i];
            if(pinned)canvas.InvalidateVisual();
        }
    }

    void CompleteCapture()
    {
        var path=SaveCapture(copy:true);
        if(pinned)return;
        if(!string.IsNullOrWhiteSpace(path)){if(completedWithRect!=null){var(px,py,pw,ph)=PhysicalSelection();completedWithRect(path,new System.Drawing.Rectangle(px,py,pw,ph));}else completed(path);Close();}
    }

    (int px,int py,int pw,int ph) PhysicalSelection()
    {
        var scaleX=source.PixelWidth/CW;var scaleY=source.PixelHeight/CH;
        int x=(int)Math.Round(virtualScreen.X+selectionRect.X*scaleX);
        int y=(int)Math.Round(virtualScreen.Y+selectionRect.Y*scaleY);
        int w=(int)Math.Round(selectionRect.Width*scaleX);
        int h=(int)Math.Round(selectionRect.Height*scaleY);
        return (x,y,w,h);
    }

    Rect DiuSelection()=>new Rect(SystemParameters.VirtualScreenLeft+selectionRect.X,SystemParameters.VirtualScreenTop+selectionRect.Y,selectionRect.Width,selectionRect.Height);

    void StartOcr()
    {
        if(pinned)return;
        if(selectionRect.Width<4||selectionRect.Height<4)return;
        var crop=Crop();
        if(crop==null)return;
        string path;
        try
        {
            path=IoPath.Combine(IoPath.GetTempPath(),$"BeeX_OCR_capture_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
            using var fs=File.Create(path);
            var enc=new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(crop));
            enc.Save(fs);
        }
        catch(Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message,"BeeX DeskNest",MessageBoxButton.OK,MessageBoxImage.Warning);
            return;
        }
        var lang=language;
        Close();
        new OcrResultWindow(path,lang).Show();
    }

    void StartScrollingCapture()
    {
        if(pinned)return;
        if(selectionRect.Width<8||selectionRect.Height<8)return;
        var(x,y,w,h)=PhysicalSelection();
        var d=DiuSelection();
        var dir=captureDirectory;var lang=language;var comp=completed;
        var win=new ScrollingCaptureWindow(x,y,w,h,d,dir,p=>comp(p),lang);
        Close();
        win.Show();
    }

    void StartRecording()
    {
        if(pinned)return;
        if(selectionRect.Width<8||selectionRect.Height<8)return;
        var(x,y,w,h)=PhysicalSelection();
        var d=DiuSelection();
        var dir=captureDirectory;var lang=language;
        var rec=new RecordingController(x,y,w,h,d,dir,lang);
        Close();
        rec.Show();
    }

    void CopyOnly()
    {
        var crop=Crop();
        if(crop==null)return;
        Clipboard.SetImage(crop);
        if(!pinned)Close();
    }

    void SaveOnly()
    {
        var path=SaveCapture(copy:CopyOnSave);
        if(pinned)return;
        if(!string.IsNullOrWhiteSpace(path)){if(completedWithRect!=null){var(px,py,pw,ph)=PhysicalSelection();completedWithRect(path,new System.Drawing.Rectangle(px,py,pw,ph));}else completed(path);Close();}
    }

    string SaveCapture(bool copy)
    {
        try
        {
            var crop=Crop();
            if(crop==null)return "";
            if(copy)Clipboard.SetImage(crop);
            Directory.CreateDirectory(captureDirectory);
            var stamp=DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            var path=IoPath.Combine(captureDirectory,$"BeeX_Capture_{stamp}.{outputFormat}");
            var suffix=1;
            while(File.Exists(path))path=IoPath.Combine(captureDirectory,$"BeeX_Capture_{stamp}_{suffix++}.{outputFormat}");
            using(var fs=File.Create(path))
            {
                var enc=EncoderFor(outputFormat);
                enc.Frames.Add(BitmapFrame.Create(crop));
                enc.Save(fs);
            }
            return path;
        }
        catch(Exception ex)
        {
            System.Windows.MessageBox.Show(ex.Message,"BeeX DeskNest",MessageBoxButton.OK,MessageBoxImage.Warning);
            ShowToolbar();
            return "";
        }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]static extern bool DeleteObject(IntPtr h);
}
