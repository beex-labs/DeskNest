using System.Windows;

namespace BeeX.DeskNest;

partial class EasterEggGame
{
    /// <summary>關卡生成：在主路線遊走中放置下一塊平台，確保不與已有矩形重疊（最多 12 次嘗試）</summary>
    void PlaceNext(List<Rect> rects,double width,double prevW,double dy,double tight,ref double dir,ref double px,ref double py,double bandTop,double bandBottom)
    {
        // 由跳躍物理實時推導：滿跳上升 + 加重下落，在高差 dy（下落為正）下可跨越的最大邊緣間隙（留 90px 容錯，兼顧滑動平台擺幅）
        double MaxEdgeGap(double ddy)
        {
            var tUp=-JumpV/Gravity;var apex=JumpV*JumpV/(2*Gravity);
            var drop=ddy+apex;var tDown=drop>0?Math.Sqrt(2*drop/(Gravity*FallMul)):0;
            return Math.Max(60,MaxRun*.92*(tUp+tDown)-90);
        }
        for(var attempt=0;attempt<12;attempt++)
        {
            var d=dir;
            if(d>0&&px+prevW>vRight-width-320)d=-1;else if(d<0&&px<vLeft+width+320)d=1;
            var ady=dy+rng.Next(-18,19);
            if(py+ady<bandTop)ady=bandTop-py;if(py+ady>bandBottom)ady=bandBottom-py;
            var gap=Math.Max(30,MaxEdgeGap(ady)*Math.Min(.97,tight));
            var nx=d>0?px+prevW+gap:px-width-gap;
            nx=Math.Clamp(nx,vLeft+20,vRight-width-20);
            var ny=py+ady;
            var cand=new Rect(nx-26,ny-46,width+52,64+92);
            if(rects.All(r=>!r.IntersectsWith(cand))||attempt==11){dir=d;px=nx;py=ny;return;}
        }
    }
}
