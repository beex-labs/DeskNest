using System.Windows;

namespace BeeX.DeskNest;

partial class EasterEggGame
{
    /// <summary>Level generation: places the next platform along the main-route walk, ensuring no overlap with existing rects (up to 12 attempts).</summary>
    void PlaceNext(List<Rect> rects,double width,double prevW,double dy,double tight,ref double dir,ref double px,ref double py,double bandTop,double bandBottom)
    {
        // Derived in real time from jump physics: full-jump rise + heavier fall, the max edge gap crossable at height difference dy (falling positive), leaving 90px tolerance and accounting for sliding-platform amplitude
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
