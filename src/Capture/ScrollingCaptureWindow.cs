using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using Drawing=System.Drawing;
using Imaging=System.Drawing.Imaging;
using Brushes=System.Windows.Media.Brushes;
using Color=System.Windows.Media.Color;
using Cursors=System.Windows.Input.Cursors;
using Button=System.Windows.Controls.Button;
using Image=System.Windows.Controls.Image;
using Orientation=System.Windows.Controls.Orientation;
using Clipboard=System.Windows.Clipboard;
using MouseButtonState=System.Windows.Input.MouseButtonState;
using IoPath=System.IO.Path;

namespace BeeX.DeskNest;

/// <summary>
/// 長截圖（滾動截圖）：框選一個固定物理區域後，用戶手動滾動底層窗口，本窗口按固定頻率
/// 抓取該區域並用「行特徵重疊匹配」把新增內容拼接成一張長圖，實時顯示縮略圖。完成後保存/複製。
/// 本窗口與區域邊框均設置 WDA_EXCLUDEFROMCAPTURE，不會被抓進長圖。
/// </summary>
public sealed class ScrollingCaptureWindow : Window
{
    const uint WDA_EXCLUDEFROMCAPTURE=0x11;
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hWnd,uint dwAffinity);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    readonly int px,py,pw,ph;
    readonly Rect diu;
    readonly string captureDirectory;
    readonly Action<string>? onSaved;
    readonly string language;

    Window? marker;
    System.Windows.Threading.DispatcherTimer? timer;
    Drawing.Bitmap? accum;
    byte[]? lastFrameBytes;    // 上一幀像素（方向守衛 MiddleShift + 運動遮罩用）
    int lastFrameStride;
    int? lastFrameTop;     // 上一幀成功配準的幀頂座標（長圖座標系）：連續性門檻用
    readonly List<Drawing.Bitmap> _pendingFrames=new();
    bool busy, finishing;
    DateTime graceUntil;

    readonly Image thumb=new(){Stretch=Stretch.Uniform,Width=240,MaxHeight=380,Margin=new Thickness(0,6,0,6)};
    readonly TextBlock hint=new(){Foreground=Brushes.White,Opacity=.85,FontSize=12,TextWrapping=TextWrapping.Wrap,Width=240};
    readonly TextBlock sizeText=new(){Foreground=Brushes.White,Opacity=.7,FontSize=11,Margin=new Thickness(0,2,0,0)};

    string L(string v)=>Localization.T(v,language);

    public ScrollingCaptureWindow(int px,int py,int pw,int ph,Rect diu,string captureDirectory,Action<string>? onSaved,string language)
    {
        this.px=px;this.py=py;this.pw=Math.Max(1,pw);this.ph=Math.Max(1,ph);this.diu=diu;
        this.captureDirectory=captureDirectory;this.onSaved=onSaved;this.language=language;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.NoResize;ShowInTaskbar=false;Topmost=true;
        AllowsTransparency=true;Background=Brushes.Transparent;SizeToContent=SizeToContent.WidthAndHeight;ShowActivated=false;
        BuildUi();
        Loaded+=(_,_)=>PlacePanel();
    }

    void BuildUi()
    {
        hint.Text=L("手動緩慢向下滾動頁面即可自動拼接長圖；完成後點「完成」。");
        var title=new TextBlock{Text=L("長截圖"),Foreground=Brushes.White,FontWeight=FontWeights.SemiBold,FontSize=14,Cursor=Cursors.SizeAll,Margin=new Thickness(0,0,0,4)};
        title.MouseLeftButtonDown+=(_,e)=>{if(e.LeftButton==MouseButtonState.Pressed){try{DragMove();}catch{}}};
        var done=new Button{Content=new TextBlock{Text="✓ "+L("完成"),Foreground=Brushes.White,FontWeight=FontWeights.SemiBold},Height=32,Margin=new Thickness(0,0,6,0),Padding=new Thickness(12,0,12,0),Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromRgb(255,138,0)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        done.Click+=(_,_)=>Finish();
        var cancel=new Button{Content=new TextBlock{Text="✕ "+L("取消"),Foreground=Brushes.White},Height=32,Padding=new Thickness(12,0,12,0),Foreground=Brushes.White,Background=new SolidColorBrush(Color.FromArgb(90,255,255,255)),BorderThickness=new Thickness(0),Cursor=Cursors.Hand};
        cancel.Click+=(_,_)=>Cancel();
        var buttons=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=System.Windows.HorizontalAlignment.Right};
        buttons.Children.Add(done);buttons.Children.Add(cancel);
        var col=new StackPanel();
        col.Children.Add(title);col.Children.Add(hint);col.Children.Add(thumb);col.Children.Add(sizeText);col.Children.Add(buttons);
        Content=new Border{CornerRadius=new CornerRadius(11),Padding=new Thickness(12),Background=new SolidColorBrush(Color.FromArgb(240,13,19,33)),BorderBrush=new SolidColorBrush(Color.FromArgb(160,255,138,0)),BorderThickness=new Thickness(1),Child=col};
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try{SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle,WDA_EXCLUDEFROMCAPTURE);}catch{}
        ShowMarker();
        timer=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(70)};
        timer.Tick+=(_,_)=>Grab();
        graceUntil=DateTime.Now.AddMilliseconds(300);
        timer.Start();
    }

    void ShowMarker()
    {
        const double bw=3;
        marker=new Window
        {
            WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,ShowInTaskbar=false,Topmost=true,
            AllowsTransparency=true,Background=Brushes.Transparent,ShowActivated=false,
            Left=diu.X-bw,Top=diu.Y-bw,Width=Math.Max(1,diu.Width+bw*2),Height=Math.Max(1,diu.Height+bw*2),IsHitTestVisible=false,
            Content=new Border{BorderBrush=new SolidColorBrush(Color.FromArgb(230,255,138,0)),BorderThickness=new Thickness(bw),Background=Brushes.Transparent}
        };
        marker.SourceInitialized+=(_,_)=>{try{SetWindowDisplayAffinity(new WindowInteropHelper(marker).Handle,WDA_EXCLUDEFROMCAPTURE);}catch{}};
        marker.Show();
    }

    void PlacePanel()
    {
        UpdateLayout();
        var vsR=SystemParameters.VirtualScreenLeft+SystemParameters.VirtualScreenWidth;
        var vsB=SystemParameters.VirtualScreenTop+SystemParameters.VirtualScreenHeight;
        var x=diu.Right+12;
        if(x+ActualWidth>vsR)x=diu.X-ActualWidth-12;
        if(x<SystemParameters.VirtualScreenLeft+4)x=vsR-ActualWidth-8;
        var y=diu.Y;
        if(y+ActualHeight>vsB)y=vsB-ActualHeight-8;
        Left=Math.Max(SystemParameters.VirtualScreenLeft+4,x);
        Top=Math.Max(SystemParameters.VirtualScreenTop+4,y);
    }

    void Grab()
    {
        if(busy||finishing)return;if(DateTime.Now<graceUntil)return;busy=true;
        try
        {
            using var cur=new Drawing.Bitmap(pw,ph,Imaging.PixelFormat.Format32bppArgb);
            using(var g=Drawing.Graphics.FromImage(cur))g.CopyFromScreen(px,py,0,0,new Drawing.Size(pw,ph),Drawing.CopyPixelOperation.SourceCopy);
            var bytes=BytesOf(cur,out var stride);
            if(accum==null)
            {
                accum=new Drawing.Bitmap(cur);
                lastFrameBytes=bytes;lastFrameStride=stride;
                UpdateThumb();
            }
            else
            {
                // 方向守衛：檢測向上滾動（內容下移），立即跳過本幀
                int? ms=MiddleShift(lastFrameBytes,bytes,lastFrameStride);
                if(ms==null){lastFrameBytes=bytes;lastFrameStride=stride;return;} // 匹配不可信，跳過
                if(ms.Value>=2){lastFrameBytes=bytes;lastFrameStride=stride;return;} // 內容下移 = 用戶向上滾，不拼接

                // 全局配準：匹配已拼長圖底部窗口
                int d=FindAppendRows(bytes,stride,out int? regTop);
                // 連續性門檻：需連續兩幀配準成功且幀頂跳變 ≤ 一屏
                if(d>=2&&lastFrameTop.HasValue&&regTop.HasValue&&Math.Abs(regTop.Value-lastFrameTop.Value)<=ph)
                {
                    AppendRows(bytes,stride,d,regTop.Value);
                    lastFrameTop=regTop;
                }
                else if(d>=2&&!lastFrameTop.HasValue)
                {
                    // 首幀匹配（accum 剛初始化），直接拼接
                    AppendRows(bytes,stride,d,regTop!.Value);
                    lastFrameTop=regTop;
                }
                // d<2 或連續性失敗：不拼接，不更新 lastFrameTop
                lastFrameBytes=bytes;lastFrameStride=stride;
            }
        }
        catch{}
        finally{busy=false;}
    }

    /// <summary>重疊區重寫式拼接：用當前幀的連續切片把長圖末尾整體重寫，接縫兩側永遠來自同一幀連續像素。
    /// 全程 LockBits + Marshal.Copy 字節拷貝，嚴禁 DrawImage 重採樣。frameTop = 當前幀頂行在長圖中的座標。</summary>
    void AppendRows(byte[] curBytes,int stride,int d,int frameTop)
    {
        if(accum==null||d<1)return;
        int accumH=accum.Height;
        int T=Math.Clamp(ph/8,24,80);
        int overlap=Math.Clamp(Math.Min(T,ph-d),0,accumH);
        int keep=accumH-overlap;
        int band=overlap+d;
        int srcStart=ph-band;
        if(srcStart<0){band+=srcStart;srcStart=0;}
        if(band<1)return;
        int newH=keep+band;
        var na=new Drawing.Bitmap(pw,newH,Imaging.PixelFormat.Format32bppArgb);
        var nd=na.LockBits(new Drawing.Rectangle(0,0,pw,newH),Imaging.ImageLockMode.WriteOnly,Imaging.PixelFormat.Format32bppArgb);
        int nStride=nd.Stride;
        try
        {
            // 1) 保留長圖頂部 [0,keep)
            if(keep>0)
            {
                var od=accum.LockBits(new Drawing.Rectangle(0,0,pw,keep),Imaging.ImageLockMode.ReadOnly,Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    int oStride=od.Stride;
                    var buf=new byte[oStride*keep];
                    Marshal.Copy(od.Scan0,buf,0,buf.Length);
                    if(oStride==nStride)Marshal.Copy(buf,0,nd.Scan0,buf.Length);
                    else{int c=Math.Min(oStride,nStride);for(int y=0;y<keep;y++)Marshal.Copy(buf,y*oStride,IntPtr.Add(nd.Scan0,y*nStride),c);}
                }
                finally{accum.UnlockBits(od);}
            }
            // 2) 用當前幀連續切片 [srcStart, srcStart+band) 重寫末尾 band 行
            int copy=Math.Min(stride,nStride);
            for(int y=0;y<band;y++)
            {
                int src=(srcStart+y)*stride;
                if(src<0||src+copy>curBytes.Length)continue;
                Marshal.Copy(curBytes,src,IntPtr.Add(nd.Scan0,(keep+y)*nStride),copy);
            }
        }
        finally{na.UnlockBits(nd);}
        accum.Dispose();accum=na;
        UpdateThumb();
    }

    /// <summary>
    /// 全局配準（主流長截圖做法）：把當前幀對位到「已拼長圖的底部窗口」，算出當前畫面在長圖中的位置：
    /// 仍在已拼範圍內（包括往上滾、回滾重看）則一行不加；幀底超出長圖末尾時只拼超出的行。
    /// 每幀獨立定位、無狀態機，天然免疫上下往返滾動與快滾失鎖（離開窗口匹配不到就不拼，回來自動恢復）。
    /// </summary>
    int FindAppendRows(byte[] cur,int stride,out int? regTop)
    {
        regTop=null;
        if(accum==null)return 0;
        int accumH=accum.Height;
        int T=Math.Clamp(ph/8,24,80);
        // 搜索窗口：長圖底部 winH = ph + T + 8
        int winH=Math.Min(accumH,ph+T+8);
        int winTop=accumH-winH;
        var win=AccumWindowBytes(winTop,winH,out int winStride);
        if(win==null)return 0;
        int colStep=Math.Max(1,pw/64);
        const int rowStep=2;
        // 候選模板帶：22%、34%、46%（避開頂部懸浮欄）
        foreach(var bandTop in new[]{(int)(ph*0.22),(int)(ph*0.34),(int)(ph*0.46)})
        {
            if(bandTop+T>=ph)continue;
            if(TemplateDetail(cur,bandTop,T,stride)<3)continue;
            int maxO=winH-T;
            if(maxO<0)continue;
            // 粗搜：列步長 pw/64、行步長 2、SAD 提前終止
            long bestSad=long.MaxValue;int bestO=-1;
            for(int o=0;o<=maxO;o++)
            {
                long sad=0;
                for(int ty=0;ty<T;ty+=rowStep)
                {
                    int crow=(bandTop+ty)*stride,wrow=(o+ty)*winStride;
                    for(int x=0;x<pw;x+=colStep)
                    {
                        int c=crow+x*4,w=wrow+x*4;
                        sad+=Math.Abs(cur[c]-win[w])+Math.Abs(cur[c+1]-win[w+1])+Math.Abs(cur[c+2]-win[w+2]);
                    }
                    if(sad>=bestSad)break;
                }
                if(sad<bestSad){bestSad=sad;bestO=o;}
            }
            if(bestO<0)continue;
            // 精修：±3 行內全分辨率逐行計算 SAD
            int fineO=bestO;long fineBest=long.MaxValue;
            for(int o=Math.Max(0,bestO-3);o<=Math.Min(maxO,bestO+3);o++)
            {
                long sad=0;
                for(int ty=0;ty<T;ty++)
                {
                    int crow=(bandTop+ty)*stride,wrow=(o+ty)*winStride;
                    for(int x=0;x<pw;x++)
                    {
                        int c=crow+x*4,w=wrow+x*4;
                        sad+=Math.Abs(cur[c]-win[w])+Math.Abs(cur[c+1]-win[w+1])+Math.Abs(cur[c+2]-win[w+2]);
                    }
                }
                if(sad<fineBest){fineBest=sad;fineO=o;}
            }
            // 校驗：均差 ≤10/通道 才接受匹配
            double avg=(double)fineBest/((long)T*pw*3);
            if(avg>10)continue;
            // 逐行重疊驗證：從重疊區域均勻抽 24 行，每行獨立判定均差 ≤12/通道
            int frameTop=winTop+fineO-bandTop;
            int ovStart=Math.Max(frameTop,winTop),ovEnd=Math.Min(frameTop+ph,accumH);
            if(ovEnd-ovStart<ph/3)continue;
            int badRows=0,rowsChecked=0;
            for(int i=0;i<24;i++)
            {
                int r=ovStart+(int)((long)(ovEnd-ovStart)*i/24);
                int cy=r-frameTop,wy=r-winTop;
                if(cy<0||cy>=ph||wy<0||wy>=winH)continue;
                int crow=cy*stride,wrow=wy*winStride;
                long rsad=0,rsamples=0;
                for(int x=0;x<pw;x+=colStep)
                {
                    int c=crow+x*4,w=wrow+x*4;
                    rsad+=Math.Abs(cur[c]-win[w])+Math.Abs(cur[c+1]-win[w+1])+Math.Abs(cur[c+2]-win[w+2]);
                    rsamples+=3;
                }
                if(rsamples==0)continue;
                rowsChecked++;
                if((double)rsad/rsamples>12)badRows++;
            }
            if(rowsChecked<8||badRows>1)continue; // 最多容忍 1 行失敗
            int d=frameTop+ph-accumH;
            regTop=frameTop;
            return d>=2?d:0;
        }
        return 0;
    }

    /// <summary>取已拼長圖底部窗口的像素字節（ReadOnly LockBits）。</summary>
    byte[]? AccumWindowBytes(int winTop,int winH,out int winStride)
    {
        winStride=0;
        if(accum==null||winH<=0)return null;
        try
        {
            var bd=accum.LockBits(new Drawing.Rectangle(0,winTop,pw,winH),Imaging.ImageLockMode.ReadOnly,Imaging.PixelFormat.Format32bppArgb);
            try
            {
                winStride=bd.Stride;
                var buffer=new byte[bd.Stride*winH];
                Marshal.Copy(bd.Scan0,buffer,0,buffer.Length);
                return buffer;
            }
            finally{accum.UnlockBits(bd);}
        }
        catch{return null;}
    }

    /// <summary>模板紋理量：相鄰採樣行的平均通道差。接近 0 = 空白/純色，匹配無辨識力。</summary>
    double TemplateDetail(byte[] buf,int top,int T,int stride)
    {
        int colStep=Math.Max(1,pw/64);
        const int rowStep=2;
        long sum=0,samples=0;
        for(int ty=rowStep;ty<T;ty+=rowStep)
        {
            int r0=(top+ty-rowStep)*stride,r1=(top+ty)*stride;
            for(int x=0;x<pw;x+=colStep)
            {
                int a=r0+x*4,b=r1+x*4;
                sum+=Math.Abs(buf[a]-buf[b])+Math.Abs(buf[a+1]-buf[b+1])+Math.Abs(buf[a+2]-buf[b+2]);
                samples+=3;
            }
        }
        return samples==0?0:(double)sum/samples;
    }

    /// <summary>方向守卫：检测内容是否下移（用户向上滚动）</summary>
    int? MiddleShift(byte[]? last, byte[] cur, int stride)
    {
        if(last==null)return null;
        int T = Math.Clamp(ph / 8, 24, 80);
        int m = (ph - T) / 2;
        int colStep = Math.Max(1, pw / 64);
        int rowStep = 2;

        float bestSAD = float.MaxValue;
        int bestO = -1;

        for (int o = 0; o <= ph - T; o++)
        {
            float sad = 0;
            int samples = 0;
            for (int ty = 0; ty < T; ty += rowStep)
            {
                int srcRow = (m + ty) * stride;
                int dstRow = (o + ty) * stride;
                for (int x = 0; x < pw; x += colStep)
                {
                    int si = srcRow + x * 4;
                    int di = dstRow + x * 4;
                    sad += Math.Abs(last[si] - cur[di]) + Math.Abs(last[si + 1] - cur[di + 1]) + Math.Abs(last[si + 2] - cur[di + 2]);
                    samples++;
                }
            }
            float avg = sad / samples;
            if (avg < bestSAD)
            {
                bestSAD = avg;
                bestO = o;
            }
        }

        if (bestSAD > 30 || bestO < 0) return null;
        return bestO - m;
    }

    /// <summary>運動遮罩：將當前幀底部 strip 中的靜止 UI 元素替換為背景色（採樣選區四邊像素眾數）。</summary>
    byte[] ApplyMotionMask(byte[] curBytes,int stride,byte[]? lastBytes,int lastStride)
    {
        if(lastBytes==null||lastBytes.Length==0)return curBytes;
        int stripH=Math.Max(1,ph/20); // 底部 strip 高度
        var result=new byte[curBytes.Length];
        System.Array.Copy(curBytes,result,curBytes.Length);
        // 採樣選區四邊像素眾數作為背景色
        var bgc=GetBorderColorMode(curBytes,stride);
        int top=ph-stripH;
        for(int y=top;y<ph;y++)
        {
            int curRow=y*stride,lastRow=y*lastStride;
            for(int x=0;x<pw;x++)
            {
                int ci=curRow+x*4,li=lastRow+x*4;
                if(li+2>=lastBytes.Length||ci+2>=curBytes.Length)continue;
                int dr=Math.Abs(curBytes[ci]-lastBytes[li]);
                int dg=Math.Abs(curBytes[ci+1]-lastBytes[li+1]);
                int db=Math.Abs(curBytes[ci+2]-lastBytes[li+2]);
                if(dr+dg+db<=30)
                {
                    // 靜止像素 → 填充背景色
                    result[ci]=bgc[0];result[ci+1]=bgc[1];result[ci+2]=bgc[2];result[ci+3]=255;
                }
            }
        }
        return result;
    }

    /// <summary>採樣選區四邊像素的眾數顏色。</summary>
    byte[] GetBorderColorMode(byte[] bytes,int stride)
    {
        var counts=new Dictionary<int,int>();
        void Sample(int x,int y)
        {
            int i=y*stride+x*4;
            if(i+2>=bytes.Length)return;
            int key=bytes[i]|(bytes[i+1]<<8)|(bytes[i+2]<<16);
            counts.TryGetValue(key,out int c);counts[key]=c+1;
        }
        for(int x=0;x<pw;x+=4){Sample(x,0);Sample(x,ph-1);}
        for(int y=0;y<ph;y+=4){Sample(0,y);Sample(pw-1,y);}
        int bestKey=0,bestCount=0;
        foreach(var kv in counts)if(kv.Value>bestCount){bestCount=kv.Value;bestKey=kv.Key;}
        return new[]{(byte)bestKey,(byte)(bestKey>>8),(byte)(bestKey>>16)};
    }

    void UpdateThumb()
    {
        if(accum==null)return;
        try
        {
            double scale=Math.Min(240.0/accum.Width,380.0/accum.Height);if(scale>1)scale=1;
            int tw=Math.Max(1,(int)(accum.Width*scale)),th=Math.Max(1,(int)(accum.Height*scale));
            using var small=new Drawing.Bitmap(tw,th,Imaging.PixelFormat.Format32bppArgb);
            using(var g=Drawing.Graphics.FromImage(small)){g.InterpolationMode=Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;g.DrawImage(accum,0,0,tw,th);}
            thumb.Source=ToBitmapSource(small);
            sizeText.Text=$"{accum.Width} × {accum.Height} px";
        }
        catch{}
    }

    static byte[] BytesOf(Drawing.Bitmap bmp,out int stride)
    {
        var data=bmp.LockBits(new Drawing.Rectangle(0,0,bmp.Width,bmp.Height),Imaging.ImageLockMode.ReadOnly,Imaging.PixelFormat.Format32bppArgb);
        stride=data.Stride;
        var bytes=new byte[stride*bmp.Height];
        Marshal.Copy(data.Scan0,bytes,0,bytes.Length);
        bmp.UnlockBits(data);
        return bytes;
    }

    static BitmapSource ToBitmapSource(Drawing.Bitmap bmp)
    {
        var h=bmp.GetHbitmap();
        try{var src=System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(h,IntPtr.Zero,Int32Rect.Empty,BitmapSizeOptions.FromEmptyOptions());src.Freeze();return src;}
        finally{DeleteObject(h);}
    }

    void Finish()
    {
        if(finishing)return;finishing=true;
        timer?.Stop();
        try{marker?.Close();}catch{}
        try
        {
            if(accum!=null)
            {
                var src=ToBitmapSource(accum);
                Directory.CreateDirectory(captureDirectory);
                var stamp=DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var path=IoPath.Combine(captureDirectory,$"BeeX_Long_{stamp}.png");
                var suffix=1;while(File.Exists(path))path=IoPath.Combine(captureDirectory,$"BeeX_Long_{stamp}_{suffix++}.png");
                using(var fs=File.Create(path)){var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(src));enc.Save(fs);}
                try{Clipboard.SetImage(src);}catch{}
                onSaved?.Invoke(path);
            }
        }
        catch(Exception ex){System.Windows.MessageBox.Show(ex.Message,"BeeX DeskNest",MessageBoxButton.OK,MessageBoxImage.Warning);}
        Cleanup();
        Close();
    }

    void Cancel()
    {
        if(finishing)return;finishing=true;
        timer?.Stop();
        try{marker?.Close();}catch{}
        Cleanup();
        Close();
    }

    void Cleanup(){try{accum?.Dispose();}catch{}accum=null;lastFrameBytes=null;lastFrameTop=null;}

    protected override void OnClosed(EventArgs e){try{marker?.Close();}catch{}base.OnClosed(e);}
}
