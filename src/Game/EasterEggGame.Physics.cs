using System.Windows;
using System.Windows.Media;
using Canvas=System.Windows.Controls.Canvas;

namespace BeeX.DeskNest;

partial class EasterEggGame
{
    // Mario-feel physics: horizontal with acceleration/friction, jump rise cuts force early on key release, fall is heavier and crisper;
    // overall slowdown: velocity terms x0.75, acceleration/gravity terms x0.75^2, keeping trajectory shape and jump height/jumpable gap unchanged
    const double MaxRun=428,GroundAccel=3600,GroundDecel=3150,AirAccel=2475,AirDecel=620;
    const double JumpV=-990,Gravity=2360,LowJumpMul=2.4,FallMul=1.55,MaxFall=2100,StompBounce=-660;
    const double CoyoteTime=.1,JumpBufferTime=.12;

    /// <summary>Render-frame driven + real frame-time integration: UI stutter no longer causes slow motion, and input responds instantly.</summary>
    void OnFrame(object? sender,EventArgs e)
    {
        if(ended)return;
        var now=clock.Elapsed.TotalSeconds;
        var dt=Math.Clamp(now-lastT,.0001,.05);lastT=now;
        if((GetAsyncKeyState(0x1B)&0x8000)!=0){End(EggResult.Quit);return;}
        if(phase==Phase.WinAnim){StepWinAnim(dt);return;}
        if(phase==Phase.Celebrate){StepCelebrate(dt);return;}
        // Sliding platforms first: a ball standing on one is carried along (Mario-style platform carrying)
        foreach(var m in movers)
        {
            if(!m.Win.IsLoaded)continue;
            var nxp=m.BaseX+(m.AmpX>0?m.AmpX*Math.Sin(now*m.Omega+m.Phase):0);
            var nyp=m.BaseY+(m.AmpY>0?m.AmpY*Math.Sin(now*m.Omega+m.Phase):0);
            var dxm=nxp-m.Win.Left;var dym=nyp-m.Win.Top;
            m.Win.Left=nxp;m.Win.Top=nyp;
            if(grounded&&ReferenceEquals(standOn,m.Win)){ballX+=dxm;ballY+=dym;}
        }
        // Input: arrow keys + WASD, jump with up/W/space
        static bool Key(int vk)=>(GetAsyncKeyState(vk)&0x8000)!=0;
        var leftKey=Key(0x25)||Key(0x41);
        var rightKey=Key(0x27)||Key(0x44);
        var upKey=Key(0x26)||Key(0x57)||Key(0x20);
        // Horizontal: target velocity + acceleration approach, fast braking on the ground, inertia preserved in the air (Mario-style dash feel)
        var target=leftKey==rightKey?0:leftKey?-MaxRun:MaxRun;
        var accel=grounded?(target==0?GroundDecel:GroundAccel):(target==0?AirDecel:AirAccel);
        velX=velX<target?Math.Min(target,velX+accel*dt):Math.Max(target,velX-accel*dt);
        // Jump: jump buffering (pressing early still counts) + coyote time (can still jump just after leaving a platform edge), so fast taps no longer miss a beat
        jumpBuf=upKey&&!prevUp?JumpBufferTime:Math.Max(0,jumpBuf-dt);
        prevUp=upKey;
        coyote=grounded?CoyoteTime:Math.Max(0,coyote-dt);
        if(jumpBuf>0&&coyote>0){velY=JumpV;grounded=false;coyote=0;jumpBuf=0;}
        // Variable jump height: releasing the jump key while rising doubles gravity to cut force early; a heavier fall makes landing crisper
        var g=velY<0?(upKey?Gravity:Gravity*LowJumpMul):Gravity*FallMul;
        velY=Math.Min(velY+g*dt,MaxFall);
        // Full AABB per-axis collision: platforms are solid blocks -- landing on top, left/right side walls block, bottom head-bump
        const double platH=64;
        var nx=Math.Clamp(ballX+velX*dt,vLeft,vRight-BallSize);
        foreach(var w in platforms)
        {
            if(!w.IsLoaded)continue;
            var l=w.Left;var r=w.Left+w.Width;var top=w.Top;var bottom=w.Top+platH;
            if(ballY+BallSize<=top+1||ballY>=bottom-1)continue; // no vertical overlap
            if(velX>0&&ballX+BallSize<=l+1&&nx+BallSize>l){nx=l-BallSize;velX=0;}
            else if(velX<0&&ballX>=r-1&&nx<r){nx=r;velX=0;}
        }
        ballX=nx;
        var prevBottom=ballY+BallSize; // ball bottom before the Y move: stomp detection looks at the approach direction rather than penetration depth, so single-frame tunneling at high fall speed is no longer mistaken for a side hit
        var ny=ballY+velY*dt;
        grounded=false;standOn=null;
        foreach(var w in platforms)
        {
            if(!w.IsLoaded)continue;
            var l=w.Left;var r=w.Left+w.Width;var top=w.Top;var bottom=w.Top+platH;
            if(ballX+BallSize-6<=l||ballX+6>=r)continue; // no horizontal overlap (6px edge tolerance)
            if(velY>=0&&ballY+BallSize<=top+3&&ny+BallSize>=top){ny=top-BallSize;velY=0;grounded=true;standOn=w;}
            else if(velY<0&&ballY>=bottom-3&&ny<bottom){ny=bottom;velY=0;}
        }
        ballY=ny;
        // Bee thief patrol + stomp detection: pressing down from above = stomp and bounce, side/below contact = death
        foreach(var f in foes)
        {
            if(!f.Alive||!f.Plat.IsLoaded)continue;
            var lo=f.Plat.Left+4;var hi=f.Plat.Left+f.Plat.Width-FoeSize-4;
            f.X+=f.V*dt;
            if(f.X<lo){f.X=lo;f.V=Math.Abs(f.V);}else if(f.X>hi){f.X=hi;f.V=-Math.Abs(f.V);}
            var fy=f.Plat.Top-FoeSize;
            if(ballX+BallSize>f.X+4&&ballX<f.X+FoeSize-4&&ballY+BallSize>fy+6&&ballY<fy+FoeSize)
            {
                // If the ball bottom in the previous frame was at or above the thief's head and not rising = pressing down from above (including the case where velY was zeroed on same-frame landing): stomp and bounce
                if(velY>=0&&prevBottom<=fy+14){f.Alive=false;canvas!.Children.Remove(f.Visual);velY=StompBounce;grounded=false;}
                else{End(EggResult.Stung);return;}
            }
            Canvas.SetLeft(f.Visual,f.X-vLeft);Canvas.SetTop(f.Visual,fy-vTop);
            f.Visual.RenderTransform=f.V<0?null:new ScaleTransform(-1,1,FoeSize/2d,0);
        }
        // Coin collection
        foreach(var c in coins)
        {
            if(c.Taken)continue;
            var dxc=ballX+BallSize/2d-c.X;var dyc=ballY+BallSize/2d-c.Y;
            if(dxc*dxc+dyc*dyc<38*38){c.Taken=true;coinCount++;canvas!.Children.Remove(c.Visual);}
        }
        SyncVisuals();
        if(hud!=null)hud.Text=F("手指康復運動 {0} 秒",(now-startT).ToString("0.0"))+" ｜ "+F("金幣 {0} 枚",coinCount);
        var dxh=ballX+BallSize/2d-holeCX;var dyh=ballY+BallSize/2d-holeCY;
        if(Math.Sqrt(dxh*dxh+dyh*dyh)<40)
        {
            winSeconds=Math.Round(now-startT,1);
            winStartX=ballX;winStartY=ballY;winAnimT=0;phase=Phase.WinAnim;
            return;
        }
        if(ballY>vBottom+60)End(EggResult.Dead);
    }
}
