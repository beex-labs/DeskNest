using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Drawing=System.Drawing;
using Imaging=System.Drawing.Imaging;
using D2=System.Drawing.Drawing2D;
using WpfBrushes=System.Windows.Media.Brushes;
using WpfColor=System.Windows.Media.Color;
using WpfBrush=System.Windows.Media.Brush;
using Point=System.Windows.Point;
using Cursors=System.Windows.Input.Cursors;
using MouseButtonState=System.Windows.Input.MouseButtonState;
using WpfRectangle=System.Windows.Shapes.Rectangle;
using Image=System.Windows.Controls.Image;

namespace BeeX.DeskNest;

public enum RecordTool { None, Pen, Highlighter, Line, Arrow, Rect, Ellipse, Number, Eraser, Move, Mosaic, Picker }

/// <summary>
/// Annotations on the floating layer during recording (raster version): Annotations are drawn on a transparent bitmap surface the same size as the region, and the frame-capture thread composites them into the video frame by frame.
/// Mosaic is applied using a brush (cell mask), pixelating the live video frame by frame during recording; a separate mosaicSurf screenshot preview is displayed on the screen at regular intervals.
/// Color Picker: Move the magnifying glass, press C to copy the color value and close the window. This window (WDA_EXCLUDEFROMCAPTURE) is intended only for input and on-screen preview.
/// </summary>
public sealed class RecordAnnotationLayer : Window
{
    const int GWL_EXSTYLE=-20, WS_EX_TRANSPARENT=0x20, WS_EX_LAYERED=0x80000, WS_EX_TOOLWINDOW=0x80;
    const uint WDA_EXCLUDEFROMCAPTURE=0x11;
    const int WH_KEYBOARD_LL=13, WM_KEYDOWN=0x0100, WM_SYSKEYDOWN=0x0104;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h,int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h,int i,int v);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h,uint a);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SetWindowsHookEx(int idHook,HookProc lpfn,IntPtr hMod,uint dwThreadId);
    [DllImport("user32.dll",SetLastError=true)] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk,int nCode,IntPtr wParam,IntPtr lParam);
    [DllImport("kernel32.dll",CharSet=CharSet.Auto)] static extern IntPtr GetModuleHandle(string? m);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    delegate IntPtr HookProc(int nCode,IntPtr wParam,IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] struct POINT{public int X;public int Y;}

    readonly int pw,ph;
    readonly double scaleX,scaleY;
    readonly object surfLock=new();
    readonly Drawing.Bitmap surface;
    readonly Drawing.Graphics gSurf;
    readonly WriteableBitmap display;
    readonly Image displayImg=new(){Stretch=Stretch.Fill,IsHitTestVisible=false};
    readonly Canvas preview=new(){IsHitTestVisible=false};
    readonly Grid root=new();
    readonly WpfBrush hitBrush=new SolidColorBrush(WpfColor.FromArgb(1,0,0,0));
    readonly List<(Drawing.Bitmap snap,long[] mask)> undo=new();

    // Mosaic (Brush Stroke): Cell Mask + On-Screen Preview Layer
    readonly HashSet<long> mosaicCells=new();
    int mosaicBlock=16, mosaicBrushW=44;
    volatile int regionScrX, regionScrY;
    bool mosaicPainting;
    readonly Drawing.Bitmap mosaicSurf;
    readonly WriteableBitmap mosaicDisp;
    readonly Image mosaicImg=new(){Stretch=Stretch.Fill,IsHitTestVisible=false};
    System.Windows.Threading.DispatcherTimer? mosaicTimer;
    volatile bool mosaicBusy; volatile bool mosaicDirty=true; byte[]? mosaicClearBuf;
    System.Windows.Threading.DispatcherTimer? moveTimer;

    // Color Picker Magnifier
    Border? magnifier; Image? magImg; TextBlock? magText; WriteableBitmap? magWb; Drawing.Color lastPick;

    IntPtr hwnd, keyHook; HookProc? keyProc;
    RecordTool tool=RecordTool.None;
    WpfColor color=WpfColor.FromRgb(255,59,48);
    double width=4;
    int dash;
    double eraserWidth=24;
    int number=1;

    bool drawing, erasing, moving;
    Point startDiu, lastDiu;
    POINT moveScrStart;
    readonly List<Point> strokePts=new();
    Drawing.Bitmap? strokeBase;
    System.Windows.Shapes.Shape? previewShape;
    System.Windows.Shapes.Polygon? previewHead;
    System.Windows.Shapes.Ellipse? ring;

    public event Action<double,double>? RegionMoved;
    public event Action? RegionMoveBegin;
    public event Action<WpfColor>? ColorPicked;
    public event Action<double>? WidthChanged;

    public RecordAnnotationLayer(Rect diu,int physicalW,int physicalH)
    {
        pw=Math.Max(2,physicalW);ph=Math.Max(2,physicalH);
        scaleX=pw/Math.Max(1,diu.Width);scaleY=ph/Math.Max(1,diu.Height);
        surface=new Drawing.Bitmap(pw,ph,Imaging.PixelFormat.Format32bppArgb);
        gSurf=Drawing.Graphics.FromImage(surface);
        gSurf.SmoothingMode=D2.SmoothingMode.AntiAlias;
        gSurf.InterpolationMode=D2.InterpolationMode.HighQualityBicubic;
        display=new WriteableBitmap(pw,ph,96,96,PixelFormats.Bgra32,null);
        displayImg.Source=display;
        mosaicSurf=new Drawing.Bitmap(pw,ph,Imaging.PixelFormat.Format32bppArgb);
        mosaicDisp=new WriteableBitmap(pw,ph,96,96,PixelFormats.Bgra32,null);
        mosaicImg.Source=mosaicDisp;

        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=true;
        AllowsTransparency=true;Background=WpfBrushes.Transparent;ShowActivated=false;
        Left=diu.X;Top=diu.Y;Width=Math.Max(1,diu.Width);Height=Math.Max(1,diu.Height);
        root.Children.Add(mosaicImg);root.Children.Add(displayImg);root.Children.Add(preview);
        Content=root;
        MouseLeftButtonDown+=Down;MouseMove+=Move;MouseLeftButtonUp+=Up;MouseWheel+=OnWheel;
    }

    /// <summary>Frame-by-frame invocation of the frame-capture thread: First, the mosaic is pixelated (by reading the current live image), and then the annotation bitmap is overlaid. </summary>
    public void ProcessFrame(Drawing.Bitmap frame)
    {
        long[]? cells=null;
        lock(surfLock){if(mosaicCells.Count>0)cells=mosaicCells.ToArray();}
        if(cells!=null)PixelateCells(frame,cells,mosaicBlock);
        lock(surfLock){try{using var g=Drawing.Graphics.FromImage(frame);g.DrawImage(surface,new Drawing.Rectangle(0,0,pw,ph));}catch{}}
    }

    public void SetRegionOrigin(int x,int y){regionScrX=x;regionScrY=y;}

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd=new WindowInteropHelper(this).Handle;
        try{SetWindowDisplayAffinity(hwnd,WDA_EXCLUDEFROMCAPTURE);}catch{}
        try{keyProc=HookCallback;keyHook=SetWindowsHookEx(WH_KEYBOARD_LL,keyProc,GetModuleHandle(null),0);}catch{}
        ApplyClickThrough();
    }

    IntPtr HookCallback(int nCode,IntPtr wParam,IntPtr lParam)
    {
        try
        {
            if(nCode>=0&&(wParam==(IntPtr)WM_KEYDOWN||wParam==(IntPtr)WM_SYSKEYDOWN))
            {
                int vk=Marshal.ReadInt32(lParam);
                bool ctrl=(GetAsyncKeyState(0x11)&0x8000)!=0;
                if(vk==0x43&&tool==RecordTool.Picker)Dispatcher.BeginInvoke(new Action(CommitPick));       // C
                else if(vk==0x5A&&ctrl)Dispatcher.BeginInvoke(new Action(Undo));                            // Ctrl+Z
            }
        }
        catch{}
        return CallNextHookEx(keyHook,nCode,wParam,lParam);
    }

    public void SetTool(RecordTool t){tool=t;if(t==RecordTool.Number)number=1;Cursor=t switch{RecordTool.None=>Cursors.Arrow,RecordTool.Move=>Cursors.SizeAll,RecordTool.Pen or RecordTool.Highlighter or RecordTool.Eraser or RecordTool.Mosaic=>Cursors.None,_=>Cursors.Cross};if(!IsBrushTool(t))HideRing();if(t!=RecordTool.Picker)HideMagnifier();root.Background=t==RecordTool.None?WpfBrushes.Transparent:hitBrush;ApplyClickThrough();}
    public void SetColor(WpfColor c)=>color=c;
    public void SetWidth(double w)=>width=w;
    public void SetDash(int d)=>dash=d;
    public void MoveTo(Rect diu){Left=diu.X;Top=diu.Y;}
    static bool IsBrushTool(RecordTool t)=>t is RecordTool.Pen or RecordTool.Highlighter or RecordTool.Eraser or RecordTool.Mosaic;

    void ApplyClickThrough()
    {
        if(hwnd==IntPtr.Zero)return;
        var ex=GetWindowLong(hwnd,GWL_EXSTYLE)|WS_EX_LAYERED|WS_EX_TOOLWINDOW;
        if(tool==RecordTool.None)ex|=WS_EX_TRANSPARENT;else ex&=~WS_EX_TRANSPARENT;
        SetWindowLong(hwnd,GWL_EXSTYLE,ex);
    }

    void Down(object s,MouseButtonEventArgs e)
    {
        if(tool==RecordTool.None)return;
        var p=e.GetPosition(root);
        if(tool==RecordTool.Move){moving=true;GetCursorPos(out moveScrStart);RegionMoveBegin?.Invoke();EnsureMoveTimer();moveTimer!.Start();e.Handled=true;return;}
        if(tool==RecordTool.Picker){e.Handled=true;return;}
        if(tool==RecordTool.Mosaic){PushUndo();mosaicPainting=true;CaptureMouse();var rp=MosaicPoint();PaintMosaic(rp);lastDiu=rp;UpdateRing(rp);EnsureMosaicTimer();e.Handled=true;return;}
        if(tool==RecordTool.Eraser){PushUndo();erasing=true;CaptureMouse();EraseCircle(p);lastDiu=p;e.Handled=true;return;}
        if(tool==RecordTool.Number){PushUndo();StampNumber(p);e.Handled=true;return;}
        PushUndo();
        startDiu=lastDiu=p;drawing=true;CaptureMouse();
        if(tool is RecordTool.Pen or RecordTool.Highlighter){strokeBase=CloneSurface();strokePts.Clear();strokePts.Add(p);RedrawStroke();}
        else if(tool is RecordTool.Rect or RecordTool.Ellipse or RecordTool.Line or RecordTool.Arrow)StartPreview(p);
        e.Handled=true;
    }

    void Move(object s,System.Windows.Input.MouseEventArgs e)
    {
        var p=e.GetPosition(root);
        if(IsBrushTool(tool))UpdateRing(tool==RecordTool.Mosaic?MosaicPoint():p);else HideRing();
        if(tool==RecordTool.Picker){UpdateMagnifier(p);return;}
        if(moving)return;
        if(e.LeftButton!=MouseButtonState.Pressed)return;
        if(mosaicPainting){var rp=MosaicPoint();PaintMosaicLine(lastDiu,rp);lastDiu=rp;return;}
        if(erasing){EraseCircle(p);lastDiu=p;return;}
        if(!drawing)return;
        lastDiu=p;
        if(tool is RecordTool.Pen or RecordTool.Highlighter){strokePts.Add(p);RedrawStroke();}
        else UpdatePreview(startDiu,p);
    }

    void Up(object s,MouseButtonEventArgs e)
    {
        if(moving){moving=false;moveTimer?.Stop();e.Handled=true;return;}
        if(mosaicPainting){mosaicPainting=false;ReleaseMouseCapture();e.Handled=true;return;}
        if(erasing){erasing=false;ReleaseMouseCapture();e.Handled=true;return;}
        if(!drawing)return;
        drawing=false;ReleaseMouseCapture();
        if(tool is RecordTool.Pen or RecordTool.Highlighter){strokeBase?.Dispose();strokeBase=null;strokePts.Clear();}
        else if(tool is RecordTool.Rect or RecordTool.Ellipse or RecordTool.Line or RecordTool.Arrow){BakeShape(tool,startDiu,lastDiu);ClearPreview();}
        e.Handled=true;
    }

    void OnWheel(object s,MouseWheelEventArgs e)
    {
        if(tool is RecordTool.None or RecordTool.Move or RecordTool.Picker)return;
        int d=e.Delta>0?1:-1;
        if(tool==RecordTool.Eraser)eraserWidth=Math.Clamp(eraserWidth+d*4,8,240);
        else if(tool==RecordTool.Mosaic)mosaicBrushW=Math.Clamp(mosaicBrushW+d*4,12,240);
        else{width=Math.Clamp(width+d,1,60);WidthChanged?.Invoke(width);}
        UpdateRing(lastDiu);e.Handled=true;
    }

    // Use a timer to poll the absolute cursor position to select the area (does not rely on WPF capture/window position, preventing the selection from failing to follow due to loss of capture when the window is moved)
    void EnsureMoveTimer()
    {
        if(moveTimer!=null)return;
        moveTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
        moveTimer.Tick+=(_,_)=>
        {
            if((GetAsyncKeyState(0x01)&0x8000)==0){moving=false;moveTimer!.Stop();return;}
            GetCursorPos(out var mp);
            RegionMoved?.Invoke((mp.X-moveScrStart.X)/scaleX,(mp.Y-moveScrStart.Y)/scaleY);
        };
    }

    // ---- Surface Rendering (Physical Pixels) ----
    Drawing.Color GC(WpfColor c)=>Drawing.Color.FromArgb(c.A,c.R,c.G,c.B);
    float SX(double v)=>(float)(v*scaleX);
    float SY(double v)=>(float)(v*scaleY);

    Drawing.Pen NewPen(bool applyDash)
    {
        var pen=new Drawing.Pen(GC(color),Math.Max(1f,SX(width))){StartCap=D2.LineCap.Round,EndCap=D2.LineCap.Round,LineJoin=D2.LineJoin.Round};
        if(applyDash&&dash!=0){pen.DashStyle=D2.DashStyle.Custom;pen.DashCap=D2.DashCap.Round;pen.DashPattern=dash switch{1=>new[]{4f,3f},2=>new[]{8f,4f},_=>new[]{1.5f,3f}};}
        return pen;
    }

    Drawing.Bitmap CloneSurface(){lock(surfLock){return (Drawing.Bitmap)surface.Clone();}}

    void RedrawStroke()
    {
        if(strokeBase==null||strokePts.Count==0)return;
        bool hl=tool==RecordTool.Highlighter;
        lock(surfLock)
        {
            var cm=gSurf.CompositingMode;
            gSurf.CompositingMode=D2.CompositingMode.SourceCopy;gSurf.Clear(Drawing.Color.Transparent);gSurf.DrawImageUnscaled(strokeBase,0,0);
            gSurf.CompositingMode=cm;
            var col=hl?Drawing.Color.FromArgb(110,color.R,color.G,color.B):GC(color);
            using var pen=new Drawing.Pen(col,Math.Max(1f,SX(hl?Math.Max(12,width*4):width))){StartCap=D2.LineCap.Round,EndCap=D2.LineCap.Round,LineJoin=D2.LineJoin.Round};
            if(!hl&&dash!=0){pen.DashStyle=D2.DashStyle.Custom;pen.DashCap=D2.DashCap.Round;pen.DashPattern=dash switch{1=>new[]{4f,3f},2=>new[]{8f,4f},_=>new[]{1.5f,3f}};}
            if(strokePts.Count==1)
            {
                float r=pen.Width/2;using var b=new Drawing.SolidBrush(col);
                gSurf.FillEllipse(b,SX(strokePts[0].X)-r,SY(strokePts[0].Y)-r,2*r,2*r);
            }
            else
            {
                var pts=new Drawing.PointF[strokePts.Count];
                for(int i=0;i<strokePts.Count;i++)pts[i]=new Drawing.PointF(SX(strokePts[i].X),SY(strokePts[i].Y));
                gSurf.DrawLines(pen,pts);
            }
        }
        RefreshDisplay();
    }

    void EraseCircle(Point c)
    {
        float r=(float)(eraserWidth/2*scaleX),cx=SX(c.X),cy=SY(c.Y);
        lock(surfLock)
        {
            var old=gSurf.CompositingMode;gSurf.CompositingMode=D2.CompositingMode.SourceCopy;
            using var b=new Drawing.SolidBrush(Drawing.Color.Transparent);
            gSurf.FillEllipse(b,cx-r,cy-r,2*r,2*r);
            gSurf.CompositingMode=old;
        }
        RefreshDisplay();
    }

    void BakeShape(RecordTool t,Point a,Point b)
    {
        lock(surfLock)
        {
            using var pen=NewPen(true);
            float ax=SX(a.X),ay=SY(a.Y),bx=SX(b.X),by=SY(b.Y);
            switch(t)
            {
                case RecordTool.Line:gSurf.DrawLine(pen,ax,ay,bx,by);break;
                case RecordTool.Rect:{var r=RectF(ax,ay,bx,by);float h=pen.Width/2f;gSurf.DrawRectangle(pen,r.X+h,r.Y+h,Math.Max(0.1f,r.Width-pen.Width),Math.Max(0.1f,r.Height-pen.Width));break;}
                case RecordTool.Ellipse:{var r=RectF(ax,ay,bx,by);float h=pen.Width/2f;gSurf.DrawEllipse(pen,r.X+h,r.Y+h,Math.Max(0.1f,r.Width-pen.Width),Math.Max(0.1f,r.Height-pen.Width));break;}
                case RecordTool.Arrow:
                    double ang=Math.Atan2(by-ay,bx-ax),len=Math.Max(10,width*3)*scaleX,sp=Math.PI/7;
                    double dist=Math.Sqrt((bx-ax)*(bx-ax)+(by-ay)*(by-ay)),ret=Math.Min(len*0.85,dist);
                    float bxl=(float)(bx-Math.Cos(ang)*ret),byl=(float)(by-Math.Sin(ang)*ret);
                    gSurf.DrawLine(pen,ax,ay,bxl,byl);
                    var pts=new[]{new Drawing.PointF(bx,by),new Drawing.PointF((float)(bx-len*Math.Cos(ang-sp)),(float)(by-len*Math.Sin(ang-sp))),new Drawing.PointF((float)(bx-len*Math.Cos(ang+sp)),(float)(by-len*Math.Sin(ang+sp)))};
                    using(var fb=new Drawing.SolidBrush(GC(color)))gSurf.FillPolygon(fb,pts);
                    break;
            }
        }
        RefreshDisplay();
    }

    void StampNumber(Point p)
    {
        lock(surfLock)
        {
            double d=Math.Max(22,width*5)*scaleX;float cx=SX(p.X),cy=SY(p.Y);
            using var b=new Drawing.SolidBrush(GC(color));
            gSurf.FillEllipse(b,(float)(cx-d/2),(float)(cy-d/2),(float)d,(float)d);
            var lum=0.299*color.R+0.587*color.G+0.114*color.B;
            using var tb=new Drawing.SolidBrush(lum>150?Drawing.Color.Black:Drawing.Color.White);
            using var f=new Drawing.Font("Arial",(float)(d*0.42),Drawing.FontStyle.Bold,Drawing.GraphicsUnit.Pixel);
            using var sf=new Drawing.StringFormat{Alignment=Drawing.StringAlignment.Center,LineAlignment=Drawing.StringAlignment.Center};
            gSurf.DrawString(number.ToString(),f,tb,new Drawing.RectangleF((float)(cx-d/2),(float)(cy-d/2),(float)d,(float)d),sf);
        }
        number++;RefreshDisplay();
    }

    static Drawing.RectangleF RectF(float ax,float ay,float bx,float by)=>new(Math.Min(ax,bx),Math.Min(ay,by),Math.Abs(ax-bx),Math.Abs(ay-by));

    void RefreshDisplay()
    {
        if(!Dispatcher.CheckAccess()){Dispatcher.BeginInvoke(new Action(RefreshDisplay));return;}
        try
        {
            lock(surfLock)
            {
                var data=surface.LockBits(new Drawing.Rectangle(0,0,pw,ph),Imaging.ImageLockMode.ReadOnly,Imaging.PixelFormat.Format32bppArgb);
                try{display.WritePixels(new Int32Rect(0,0,pw,ph),data.Scan0,data.Stride*ph,data.Stride);}
                finally{surface.UnlockBits(data);}
            }
        }
        catch{}
    }

    // ---- Mosaic (Brush-Applied Mask + Frame-by-Frame Pixelation) ----
    // Use "Absolute Cursor → Record Region with Local Physical Coordinates" to ensure the same reference point as frame capture/recording, guaranteeing that the mosaic is precisely positioned at the cursor location (unaffected by window or DPI misalignment).
    Point MosaicPoint(){GetCursorPos(out var cp);return new Point((cp.X-regionScrX)/Math.Max(0.0001,scaleX),(cp.Y-regionScrY)/Math.Max(0.0001,scaleY));}
    List<long> MarkMosaic(Point p)
    {
        double cxp=p.X*scaleX,cyp=p.Y*scaleY,r=mosaicBrushW/2.0*scaleX;
        int c0x=(int)Math.Floor((cxp-r)/mosaicBlock),c1x=(int)Math.Floor((cxp+r)/mosaicBlock);
        int c0y=(int)Math.Floor((cyp-r)/mosaicBlock),c1y=(int)Math.Floor((cyp+r)/mosaicBlock);
        int maxCx=(pw-1)/mosaicBlock,maxCy=(ph-1)/mosaicBlock;
        var touched=new List<long>();
        lock(surfLock)
        {
            for(int cy=c0y;cy<=c1y;cy++)for(int cx=c0x;cx<=c1x;cx++)
            {
                if(cx<0||cy<0||cx>maxCx||cy>maxCy)continue;
                double bcx=(cx+0.5)*mosaicBlock,bcy=(cy+0.5)*mosaicBlock;
                if((bcx-cxp)*(bcx-cxp)+(bcy-cyp)*(bcy-cyp)<=r*r){var key=((long)cy<<32)|(uint)cx;mosaicCells.Add(key);touched.Add(key);}
            }
        }
        return touched;
    }
    void PaintMosaic(Point p){var t=MarkMosaic(p);if(t.Count>0)RenderMosaicCellsNow(t);}
    // Synchronously render specified grid cells (only the small area covered by the brush) to ensure real-time responsiveness; leave the overall live refresh to the background timer.
    void RenderMosaicCellsNow(List<long> cells)
    {
        int block=mosaicBlock,minX=pw,minY=ph,maxX=0,maxY=0;
        foreach(var key in cells){int cx=(int)(key&0xFFFFFFFF),cy=(int)(key>>32);int x=cx*block,y=cy*block;if(x<minX)minX=x;if(y<minY)minY=y;if(x+block>maxX)maxX=x+block;if(y+block>maxY)maxY=y+block;}
        minX=Math.Max(0,minX);minY=Math.Max(0,minY);maxX=Math.Min(pw,maxX);maxY=Math.Min(ph,maxY);
        int bw=maxX-minX,bh=maxY-minY;if(bw<1||bh<1)return;
        try
        {
            using var grab=new Drawing.Bitmap(bw,bh,Imaging.PixelFormat.Format32bppArgb);
            using(var g=Drawing.Graphics.FromImage(grab))g.CopyFromScreen(regionScrX+minX,regionScrY+minY,0,0,new Drawing.Size(bw,bh),Drawing.CopyPixelOperation.SourceCopy);
            var gd=grab.LockBits(new Drawing.Rectangle(0,0,bw,bh),Imaging.ImageLockMode.ReadOnly,Imaging.PixelFormat.Format32bppArgb);
            int stride=gd.Stride;var src=new byte[stride*bh];Marshal.Copy(gd.Scan0,src,0,src.Length);grab.UnlockBits(gd);
            var outBuf=new byte[stride*bh];
            foreach(var key in cells){int cx=(int)(key&0xFFFFFFFF),cy=(int)(key>>32);AvgFillBlockToBuf(src,outBuf,stride,bw,bh,cx*block-minX,cy*block-minY,block);}
            mosaicDisp.WritePixels(new Int32Rect(minX,minY,bw,bh),outBuf,stride,0);
        }
        catch{}
    }
    void PaintMosaicLine(Point a,Point b)
    {
        double dist=Math.Max(Math.Abs(b.X-a.X),Math.Abs(b.Y-a.Y));
        int steps=Math.Max(1,(int)(dist/4));
        var all=new HashSet<long>();
        for(int i=0;i<=steps;i++){double t=(double)i/steps;foreach(var k in MarkMosaic(new Point(a.X+(b.X-a.X)*t,a.Y+(b.Y-a.Y)*t)))all.Add(k);}
        if(all.Count>0)RenderMosaicCellsNow(new List<long>(all));
    }
    void EnsureMosaicTimer()
    {
        if(mosaicTimer!=null)return;
        mosaicTimer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(120)};
        mosaicTimer.Tick+=(_,_)=>MosaicTick();
        mosaicTimer.Start();
    }
    // On-screen mosaic preview: Screen capture and pixelation are handled in a background thread, and only the mask bounding box is processed; the UI thread only performs small WritePixels operations to prevent stuttering.
    void MosaicTick()
    {
        if(mosaicBusy)return;
        long[] cells;
        lock(surfLock){cells=mosaicCells.Count==0?Array.Empty<long>():mosaicCells.ToArray();}
        if(cells.Length==0)
        {
            if(mosaicDirty){mosaicDirty=false;try{mosaicClearBuf??=new byte[pw*4*ph];mosaicDisp.WritePixels(new Int32Rect(0,0,pw,ph),mosaicClearBuf,pw*4,0);}catch{}}
            return;
        }
        int block=mosaicBlock,minX=pw,minY=ph,maxX=0,maxY=0;
        foreach(var key in cells){int cx=(int)(key&0xFFFFFFFF),cy=(int)(key>>32);int x=cx*block,y=cy*block;if(x<minX)minX=x;if(y<minY)minY=y;if(x+block>maxX)maxX=x+block;if(y+block>maxY)maxY=y+block;}
        minX=Math.Max(0,minX);minY=Math.Max(0,minY);maxX=Math.Min(pw,maxX);maxY=Math.Min(ph,maxY);
        int bw=maxX-minX,bh=maxY-minY;if(bw<1||bh<1)return;
        int ox=regionScrX+minX,oy=regionScrY+minY;
        bool clearFirst=mosaicDirty;mosaicDirty=false;
        mosaicBusy=true;
        System.Threading.Tasks.Task.Run(()=>
        {
            byte[]? outBuf=null;int stride=bw*4;
            try
            {
                using var grab=new Drawing.Bitmap(bw,bh,Imaging.PixelFormat.Format32bppArgb);
                using(var g=Drawing.Graphics.FromImage(grab))g.CopyFromScreen(ox,oy,0,0,new Drawing.Size(bw,bh),Drawing.CopyPixelOperation.SourceCopy);
                var gd=grab.LockBits(new Drawing.Rectangle(0,0,bw,bh),Imaging.ImageLockMode.ReadOnly,Imaging.PixelFormat.Format32bppArgb);
                stride=gd.Stride;var src=new byte[stride*bh];Marshal.Copy(gd.Scan0,src,0,src.Length);grab.UnlockBits(gd);
                outBuf=new byte[stride*bh];  // Transparent background; only the grid cells are filled with opaque pixels.
                foreach(var key in cells)
                {
                    int cx=(int)(key&0xFFFFFFFF),cy=(int)(key>>32);
                    AvgFillBlockToBuf(src,outBuf,stride,bw,bh,cx*block-minX,cy*block-minY,block);
                }
            }
            catch{outBuf=null;}
            var fbuf=outBuf;int fstride=stride,fbw=bw,fbh=bh,fx=minX,fy=minY;bool fclear=clearFirst;
            Dispatcher.BeginInvoke(new Action(()=>
            {
                try
                {
                    if(fclear){mosaicClearBuf??=new byte[pw*4*ph];mosaicDisp.WritePixels(new Int32Rect(0,0,pw,ph),mosaicClearBuf,pw*4,0);}
                    if(fbuf!=null)mosaicDisp.WritePixels(new Int32Rect(fx,fy,fbw,fbh),fbuf,fstride,0);
                }
                catch{}
                mosaicBusy=false;
            }));
        });
    }
    static void AvgFillBlockToBuf(byte[] src,byte[] dst,int stride,int w,int h,int x0,int y0,int block)
    {
        int xs=Math.Max(0,x0),ys=Math.Max(0,y0),xe=Math.Min(w,x0+block),ye=Math.Min(h,y0+block);
        if(xe<=xs||ye<=ys)return;
        long sb=0,sg=0,sr=0;int cnt=0;
        for(int y=ys;y<ye;y++){int off=y*stride+xs*4;for(int x=xs;x<xe;x++){sb+=src[off];sg+=src[off+1];sr+=src[off+2];off+=4;cnt++;}}
        if(cnt==0)return;byte ab=(byte)(sb/cnt),ag=(byte)(sg/cnt),ar=(byte)(sr/cnt);
        for(int y=ys;y<ye;y++){int off=y*stride+xs*4;for(int x=xs;x<xe;x++){dst[off]=ab;dst[off+1]=ag;dst[off+2]=ar;dst[off+3]=255;off+=4;}}
    }
    static void PixelateCells(Drawing.Bitmap bmp,long[] cells,int block)
    {
        if(cells.Length==0)return;
        int W=bmp.Width,H=bmp.Height,minX=W,minY=H,maxX=0,maxY=0;
        foreach(var key in cells){int cx=(int)(key&0xFFFFFFFF),cy=(int)(key>>32);int x=cx*block,y=cy*block;if(x<minX)minX=x;if(y<minY)minY=y;if(x+block>maxX)maxX=x+block;if(y+block>maxY)maxY=y+block;}
        var bbox=Drawing.Rectangle.Intersect(new Drawing.Rectangle(minX,minY,maxX-minX,maxY-minY),new Drawing.Rectangle(0,0,W,H));
        if(bbox.Width<1||bbox.Height<1)return;
        Imaging.BitmapData? data=null;
        try
        {
            data=bmp.LockBits(bbox,Imaging.ImageLockMode.ReadWrite,Imaging.PixelFormat.Format32bppArgb);
            int stride=data.Stride;var buf=new byte[stride*bbox.Height];Marshal.Copy(data.Scan0,buf,0,buf.Length);
            foreach(var key in cells)
            {
                int cx=(int)(key&0xFFFFFFFF),cy=(int)(key>>32);
                AvgFillBlock(buf,stride,bbox.Width,bbox.Height,cx*block-bbox.X,cy*block-bbox.Y,block);
            }
            Marshal.Copy(buf,0,data.Scan0,buf.Length);
        }
        catch{}
        finally{if(data!=null)try{bmp.UnlockBits(data);}catch{}}
    }
    static void AvgFillBlock(byte[] buf,int stride,int w,int h,int x0,int y0,int block)
    {
        int xs=Math.Max(0,x0),ys=Math.Max(0,y0),xe=Math.Min(w,x0+block),ye=Math.Min(h,y0+block);
        if(xe<=xs||ye<=ys)return;
        long sb=0,sg=0,sr=0;int cnt=0;
        for(int y=ys;y<ye;y++){int off=y*stride+xs*4;for(int x=xs;x<xe;x++){sb+=buf[off];sg+=buf[off+1];sr+=buf[off+2];off+=4;cnt++;}}
        if(cnt==0)return;byte ab=(byte)(sb/cnt),ag=(byte)(sg/cnt),ar=(byte)(sr/cnt);
        for(int y=ys;y<ye;y++){int off=y*stride+xs*4;for(int x=xs;x<xe;x++){buf[off]=ab;buf[off+1]=ag;buf[off+2]=ar;buf[off+3]=255;off+=4;}}
    }

    // ---- Color Picker ----
    void UpdateMagnifier(Point p)
    {
        try
        {
            GetCursorPos(out var cp);
            const int gs=17;
            using var grab=new Drawing.Bitmap(gs,gs,Imaging.PixelFormat.Format32bppArgb);
            using(var g=Drawing.Graphics.FromImage(grab))g.CopyFromScreen(cp.X-gs/2,cp.Y-gs/2,0,0,new Drawing.Size(gs,gs),Drawing.CopyPixelOperation.SourceCopy);
            lastPick=grab.GetPixel(gs/2,gs/2);
            EnsureMagnifier(gs);
            var data=grab.LockBits(new Drawing.Rectangle(0,0,gs,gs),Imaging.ImageLockMode.ReadOnly,Imaging.PixelFormat.Format32bppArgb);
            try{magWb!.WritePixels(new Int32Rect(0,0,gs,gs),data.Scan0,data.Stride*gs,data.Stride);}finally{grab.UnlockBits(data);}
            magText!.Text=$"#{lastPick.R:X2}{lastPick.G:X2}{lastPick.B:X2}  ·C";
            magText.Background=new SolidColorBrush(WpfColor.FromRgb(lastPick.R,lastPick.G,lastPick.B));
            magText.Foreground=(0.299*lastPick.R+0.587*lastPick.G+0.114*lastPick.B)>150?WpfBrushes.Black:WpfBrushes.White;
            double mx=Math.Min(p.X+18,Math.Max(0,Width-140)),my=Math.Min(p.Y+18,Math.Max(0,Height-150));
            Canvas.SetLeft(magnifier!,Math.Max(0,mx));Canvas.SetTop(magnifier!,Math.Max(0,my));
            magnifier!.Visibility=Visibility.Visible;
        }
        catch{}
    }
    void EnsureMagnifier(int gs)
    {
        if(magnifier!=null){if(magWb==null||magWb.PixelWidth!=gs){magWb=new WriteableBitmap(gs,gs,96,96,PixelFormats.Bgra32,null);magImg!.Source=magWb;}return;}
        magWb=new WriteableBitmap(gs,gs,96,96,PixelFormats.Bgra32,null);
        magImg=new Image{Width=120,Height=120,Source=magWb};
        RenderOptions.SetBitmapScalingMode(magImg,BitmapScalingMode.NearestNeighbor);
        var cross=new WpfRectangle{Width=Math.Max(3,120.0/gs),Height=Math.Max(3,120.0/gs),Stroke=new SolidColorBrush(WpfColor.FromArgb(235,255,138,0)),StrokeThickness=1.4,Fill=WpfBrushes.Transparent,HorizontalAlignment=System.Windows.HorizontalAlignment.Center,VerticalAlignment=System.Windows.VerticalAlignment.Center};
        magText=new TextBlock{FontSize=12,FontWeight=FontWeights.Bold,Padding=new Thickness(4,2,4,2),TextAlignment=TextAlignment.Center};
        var grid=new Grid();grid.Children.Add(magImg);grid.Children.Add(cross);
        var panel=new StackPanel();panel.Children.Add(grid);panel.Children.Add(magText);
        magnifier=new Border{Background=new SolidColorBrush(WpfColor.FromArgb(240,20,20,20)),BorderBrush=new SolidColorBrush(WpfColor.FromArgb(200,255,138,0)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),Padding=new Thickness(3),Child=panel,IsHitTestVisible=false};
        preview.Children.Add(magnifier);
    }
    void HideMagnifier(){if(magnifier!=null)magnifier.Visibility=Visibility.Collapsed;}
    void CommitPick()
    {
        try
        {
            var hex=$"#{lastPick.R:X2}{lastPick.G:X2}{lastPick.B:X2}";
            try{System.Windows.Clipboard.SetText(hex);}catch{}
            color=WpfColor.FromRgb(lastPick.R,lastPick.G,lastPick.B);
            ColorPicked?.Invoke(color);
        }
        catch{}
    }

    // ---- Undo (surface snapshot + mosaic mask snapshot) ----
    void PushUndo(){ lock(surfLock){ try{ undo.Add(((Drawing.Bitmap)surface.Clone(),mosaicCells.ToArray())); if(undo.Count>12){undo[0].snap.Dispose();undo.RemoveAt(0);} }catch{} } }
    public void Undo()
    {
        lock(surfLock)
        {
            if(undo.Count==0)return;
            var e=undo[^1];undo.RemoveAt(undo.Count-1);
            try{var old=gSurf.CompositingMode;gSurf.CompositingMode=D2.CompositingMode.SourceCopy;gSurf.Clear(Drawing.Color.Transparent);gSurf.DrawImageUnscaled(e.snap,0,0);gSurf.CompositingMode=old;}catch{}
            e.snap.Dispose();
            mosaicCells.Clear();foreach(var k in e.mask)mosaicCells.Add(k);
        }
        mosaicDirty=true;
        RefreshDisplay();
    }

    // ---- On-Screen Preview (WPF, displays only still images, no video) ----
    void StartPreview(Point p)
    {
        ClearPreview();
        var wc=new SolidColorBrush(color);var dashArr=DashArr();
        switch(tool)
        {
            case RecordTool.Rect:previewShape=new WpfRectangle{Stroke=wc,StrokeThickness=width,StrokeDashArray=dashArr};break;
            case RecordTool.Ellipse:previewShape=new System.Windows.Shapes.Ellipse{Stroke=wc,StrokeThickness=width,StrokeDashArray=dashArr};break;
            case RecordTool.Line:previewShape=new System.Windows.Shapes.Line{Stroke=wc,StrokeThickness=width,StrokeDashArray=dashArr,X1=p.X,Y1=p.Y,X2=p.X,Y2=p.Y};break;
            case RecordTool.Arrow:
                previewShape=new System.Windows.Shapes.Line{Stroke=wc,StrokeThickness=width,StrokeDashArray=dashArr,X1=p.X,Y1=p.Y,X2=p.X,Y2=p.Y};
                previewHead=new System.Windows.Shapes.Polygon{Fill=wc};preview.Children.Add(previewHead);break;
        }
        if(previewShape!=null)preview.Children.Add(previewShape);
    }
    void UpdatePreview(Point a,Point b)
    {
        switch(previewShape)
        {
            case System.Windows.Shapes.Line ln:
                if(previewHead!=null)
                {
                    double ang=Math.Atan2(b.Y-a.Y,b.X-a.X),len=Math.Max(10,width*3),sp=Math.PI/7;
                    double dist=Math.Sqrt((b.X-a.X)*(b.X-a.X)+(b.Y-a.Y)*(b.Y-a.Y)),ret=Math.Min(len*0.85,dist);
                    ln.X2=b.X-Math.Cos(ang)*ret;ln.Y2=b.Y-Math.Sin(ang)*ret;
                    previewHead.Points=new PointCollection{new Point(b.X,b.Y),new Point(b.X-len*Math.Cos(ang-sp),b.Y-len*Math.Sin(ang-sp)),new Point(b.X-len*Math.Cos(ang+sp),b.Y-len*Math.Sin(ang+sp))};
                }
                else{ln.X2=b.X;ln.Y2=b.Y;}
                break;
            case WpfRectangle r:{var rc=Norm(a,b);Canvas.SetLeft(r,rc.X);Canvas.SetTop(r,rc.Y);r.Width=rc.Width;r.Height=rc.Height;break;}
            case System.Windows.Shapes.Ellipse el:{var rc=Norm(a,b);Canvas.SetLeft(el,rc.X);Canvas.SetTop(el,rc.Y);el.Width=rc.Width;el.Height=rc.Height;break;}
        }
    }
    void ClearPreview(){if(previewShape!=null)preview.Children.Remove(previewShape);if(previewHead!=null)preview.Children.Remove(previewHead);previewShape=null;previewHead=null;}
    DoubleCollection? DashArr()=>dash switch{1=>new DoubleCollection{4,3},2=>new DoubleCollection{8,4},3=>new DoubleCollection{1.5,3},_=>null};

    void UpdateRing(Point p)
    {
        if(!IsBrushTool(tool)){HideRing();return;}
        if(ring==null){ring=new System.Windows.Shapes.Ellipse{Stroke=new SolidColorBrush(WpfColor.FromArgb(235,255,255,255)),StrokeThickness=1.5,Fill=new SolidColorBrush(WpfColor.FromArgb(30,255,138,0)),IsHitTestVisible=false};preview.Children.Add(ring);}
        double dia=tool==RecordTool.Eraser?eraserWidth:tool==RecordTool.Mosaic?mosaicBrushW:(tool==RecordTool.Highlighter?Math.Max(12,width*4):Math.Max(6,width));
        dia=Math.Max(6,dia);ring.Width=dia;ring.Height=dia;Canvas.SetLeft(ring,p.X-dia/2);Canvas.SetTop(ring,p.Y-dia/2);ring.Visibility=Visibility.Visible;
    }
    void HideRing(){if(ring!=null)ring.Visibility=Visibility.Collapsed;}

    static Rect Norm(Point a,Point b)=>new(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Abs(a.X-b.X),Math.Abs(a.Y-b.Y));

    protected override void OnClosed(EventArgs e)
    {
        try{if(keyHook!=IntPtr.Zero){UnhookWindowsHookEx(keyHook);keyHook=IntPtr.Zero;}}catch{}
        try{mosaicTimer?.Stop();moveTimer?.Stop();}catch{}
        try{lock(surfLock){gSurf.Dispose();surface.Dispose();mosaicSurf.Dispose();strokeBase?.Dispose();foreach(var u in undo)u.snap.Dispose();undo.Clear();}}catch{}
        base.OnClosed(e);
    }
}
