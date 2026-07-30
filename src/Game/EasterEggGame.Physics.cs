using System.Windows;
using System.Windows.Media;
using Canvas=System.Windows.Controls.Canvas;

namespace BeeX.DeskNest;

partial class EasterEggGame
{
    // 馬里奧手感物理：水平帶加速度/摩擦，跳躍上升鬆鍵提前收力、下落加重更乾脆；
    // 整體降速：速度類 ×0.75、加速度/重力類 ×0.75²，軌跡形狀與跳躍高度/可跳間隙保持不變
    const double MaxRun=428,GroundAccel=3600,GroundDecel=3150,AirAccel=2475,AirDecel=620;
    const double JumpV=-990,Gravity=2360,LowJumpMul=2.4,FallMul=1.55,MaxFall=2100,StompBounce=-660;
    const double CoyoteTime=.1,JumpBufferTime=.12;

    /// <summary>渲染幀驅動 + 真實幀時長積分：UI 卡頓不再造成慢動作，操作即時響應</summary>
    void OnFrame(object? sender,EventArgs e)
    {
        if(ended)return;
        var now=clock.Elapsed.TotalSeconds;
        var dt=Math.Clamp(now-lastT,.0001,.05);lastT=now;
        if((GetAsyncKeyState(0x1B)&0x8000)!=0){End(EggResult.Quit);return;}
        if(phase==Phase.WinAnim){StepWinAnim(dt);return;}
        if(phase==Phase.Celebrate){StepCelebrate(dt);return;}
        // 滑動平台先行：站在上面的球被載著走（馬里奧式平台載人）
        foreach(var m in movers)
        {
            if(!m.Win.IsLoaded)continue;
            var nxp=m.BaseX+(m.AmpX>0?m.AmpX*Math.Sin(now*m.Omega+m.Phase):0);
            var nyp=m.BaseY+(m.AmpY>0?m.AmpY*Math.Sin(now*m.Omega+m.Phase):0);
            var dxm=nxp-m.Win.Left;var dym=nyp-m.Win.Top;
            m.Win.Left=nxp;m.Win.Top=nyp;
            if(grounded&&ReferenceEquals(standOn,m.Win)){ballX+=dxm;ballY+=dym;}
        }
        // 輸入：方向鍵 + WASD 雙鍵位，↑/W/空格跳躍
        static bool Key(int vk)=>(GetAsyncKeyState(vk)&0x8000)!=0;
        var leftKey=Key(0x25)||Key(0x41);
        var rightKey=Key(0x27)||Key(0x44);
        var upKey=Key(0x26)||Key(0x57)||Key(0x20);
        // 水平：目標速度 + 加速度逼近，地面剎車快、空中慣性保留（馬里奧式衝刺手感）
        var target=leftKey==rightKey?0:leftKey?-MaxRun:MaxRun;
        var accel=grounded?(target==0?GroundDecel:GroundAccel):(target==0?AirDecel:AirAccel);
        velX=velX<target?Math.Min(target,velX+accel*dt):Math.Max(target,velX-accel*dt);
        // 跳躍：跳躍緩衝（提前按也算）+ 土狼時間（剛走出平台邊仍可跳），快速連點不再漏拍
        jumpBuf=upKey&&!prevUp?JumpBufferTime:Math.Max(0,jumpBuf-dt);
        prevUp=upKey;
        coyote=grounded?CoyoteTime:Math.Max(0,coyote-dt);
        if(jumpBuf>0&&coyote>0){velY=JumpV;grounded=false;coyote=0;jumpBuf=0;}
        // 可變跳高：上升時鬆開跳躍鍵重力加倍提前收力；下落加重讓落地更乾脆
        var g=velY<0?(upKey?Gravity:Gravity*LowJumpMul):Gravity*FallMul;
        velY=Math.Min(velY+g*dt,MaxFall);
        // 完整 AABB 分軸碰撞：平台為實心塊——頂部著陸、左右側牆阻擋、底部頂頭
        const double platH=64;
        var nx=Math.Clamp(ballX+velX*dt,vLeft,vRight-BallSize);
        foreach(var w in platforms)
        {
            if(!w.IsLoaded)continue;
            var l=w.Left;var r=w.Left+w.Width;var top=w.Top;var bottom=w.Top+platH;
            if(ballY+BallSize<=top+1||ballY>=bottom-1)continue; // 垂直方向無重疊
            if(velX>0&&ballX+BallSize<=l+1&&nx+BallSize>l){nx=l-BallSize;velX=0;}
            else if(velX<0&&ballX>=r-1&&nx<r){nx=r;velX=0;}
        }
        ballX=nx;
        var prevBottom=ballY+BallSize; // Y 移動前的球底：踩頭判定看「來向」而非壓入深度，高速下落單幀穿透不再誤判為側撞
        var ny=ballY+velY*dt;
        grounded=false;standOn=null;
        foreach(var w in platforms)
        {
            if(!w.IsLoaded)continue;
            var l=w.Left;var r=w.Left+w.Width;var top=w.Top;var bottom=w.Top+platH;
            if(ballX+BallSize-6<=l||ballX+6>=r)continue; // 水平方向無重疊（留 6px 邊緣容差）
            if(velY>=0&&ballY+BallSize<=top+3&&ny+BallSize>=top){ny=top-BallSize;velY=0;grounded=true;standOn=w;}
            else if(velY<0&&ballY>=bottom-3&&ny<bottom){ny=bottom;velY=0;}
        }
        ballY=ny;
        // 蜂賊巡邏 + 踩頭判定：從上方壓下＝踩扁反彈，側面/下方碰到＝死亡
        foreach(var f in foes)
        {
            if(!f.Alive||!f.Plat.IsLoaded)continue;
            var lo=f.Plat.Left+4;var hi=f.Plat.Left+f.Plat.Width-FoeSize-4;
            f.X+=f.V*dt;
            if(f.X<lo){f.X=lo;f.V=Math.Abs(f.V);}else if(f.X>hi){f.X=hi;f.V=-Math.Abs(f.V);}
            var fy=f.Plat.Top-FoeSize;
            if(ballX+BallSize>f.X+4&&ballX<f.X+FoeSize-4&&ballY+BallSize>fy+6&&ballY<fy+FoeSize)
            {
                // 上一幀球底還在蜂賊頭頂附近或更高且非上升＝從上方壓下（含同幀著地 velY 已歸零的情況），踩扁反彈
                if(velY>=0&&prevBottom<=fy+14){f.Alive=false;canvas!.Children.Remove(f.Visual);velY=StompBounce;grounded=false;}
                else{End(EggResult.Stung);return;}
            }
            Canvas.SetLeft(f.Visual,f.X-vLeft);Canvas.SetTop(f.Visual,fy-vTop);
            f.Visual.RenderTransform=f.V<0?null:new ScaleTransform(-1,1,FoeSize/2d,0);
        }
        // 金幣收集
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
