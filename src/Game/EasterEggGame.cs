using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using WpfColor=System.Windows.Media.Color;
using WpfPoint=System.Windows.Point;
using Border=System.Windows.Controls.Border;
using Canvas=System.Windows.Controls.Canvas;
using Grid=System.Windows.Controls.Grid;
using TextBlock=System.Windows.Controls.TextBlock;
using Rectangle=System.Windows.Shapes.Rectangle;

namespace BeeX.DeskNest;

/// <summary>
/// 蜂巢彩蛋（超級馬里奧版）：所有可見組件摺疊成 64px 平台、懸浮球化身角色，←→/AD 移動、↑/W/空格 跳躍。
/// 馬里奧式手感：加速度/慣性、長按跳更高、土狼時間 + 跳躍緩衝、踩頭反彈；真實幀時長積分（不再固定 16ms，徹底跟手）。
/// 關卡加料：部分摺疊欄自動左右/上下滑動（站上去會被載著走）、主路線撒金幣導航、蜂賊巡邏（可踩扁、側碰即死）。
/// 抵達黑洞獲勝——彩帶漫天 + 橫幅「恭喜你！手指康復運動 xx 秒」；墜落桌面底部或被蜂賊撞到死亡，Esc 隨時退出。
/// 遊戲前快照組件狀態（位置/摺疊/置頂），結束後完整還原並刪除臨時補足的平台；平台佈局基於 VirtualScreen。
/// </summary>
sealed partial class EasterEggGame
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd,int index,IntPtr value);
    public static bool Running{get;private set;}
    sealed record NestSnapshot(Guid Id,double Left,double Top,bool Collapsed,bool Pinned);
    /// <summary>自動滑動平台：圍繞基準點正弦擺動，站在上面的球會被載著走</summary>
    sealed class Mover{public WidgetWindow Win=null!;public double BaseX,BaseY,AmpX,AmpY,Omega,Phase;}
    sealed class Coin{public double X,Y;public Grid Visual=null!;public bool Taken;}
    /// <summary>蜂賊：在平台頂面來回巡邏，踩頭反彈消滅、側碰即死</summary>
    sealed class Foe{public WidgetWindow Plat=null!;public double X,V;public Grid Visual=null!;public bool Alive=true;}
    sealed class Ribbon{public double X,Y,Vx,Vy,Rot,Vr;public Rectangle Visual=null!;}
    enum EggResult{Won,Dead,Stung,Quit}
    enum Phase{Play,WinAnim,Celebrate}
    const int BallSize=58,MinPlatforms=14,FoeSize=34,CoinSize=22;
    readonly DeskNestService service;
    readonly Random rng=new();
    readonly List<NestSnapshot> snapshots=[];
    readonly List<NestModel> tempNests=[];
    readonly List<WidgetWindow> platforms=[];
    readonly List<Mover> movers=[];
    readonly List<Coin> coins=[];
    readonly List<Foe> foes=[];
    readonly List<Ribbon> ribbons=[];
    readonly Stopwatch clock=new();
    Window? overlay;Canvas? canvas;Border? ballShell;TextBlock? hud;
    RotateTransform ballSpin=new();ScaleTransform ballScale=new(1,1);
    double ballX,ballY,velX,velY,vLeft,vRight,vTop,vBottom;
    double coyote,jumpBuf,lastT,startT,winSeconds,winAnimT,celebrateT,winStartX,winStartY;
    double holeCX,holeCY;
    int coinCount;
    bool grounded,prevUp,ended,hooked;
    WidgetWindow? standOn;
    Phase phase=Phase.Play;
    public EasterEggGame(DeskNestService service){this.service=service;}
    string L(string value)=>Localization.T(value,service.State.Language);
    string F(string key,params object[] args)=>Localization.Format(key,service.State.Language,args);

    public void Start()
    {
        if(Running)return;Running=true;ended=false;
        var participants=service.State.Nests.Where(n=>n.IsVisible&&service.WindowOf(n) is {IsLoaded:true}).ToList();
        // 組件不夠時臨時補足平台（Note 格子），遊戲結束後刪除
        for(var i=participants.Count;i<MinPlatforms;i++){var t=service.AddEasterEggPlatform(tempNests.Count);tempNests.Add(t);participants.Add(t);}
        foreach(var n in participants.Where(n=>tempNests.All(t=>t.Id!=n.Id)))snapshots.Add(new NestSnapshot(n.Id,n.Left,n.Top,n.IsCollapsed,n.Pinned));
        service.SuspendGlobalHotkeys();
        service.HideFloatingBallForGame();
        // 虛擬桌面邊界：VirtualScreen 自動覆蓋所有顯示器（單屏=主屏，雙屏=橫跨兩屏）
        vLeft=SystemParameters.VirtualScreenLeft;vRight=vLeft+SystemParameters.VirtualScreenWidth;
        vTop=SystemParameters.VirtualScreenTop;vBottom=vTop+SystemParameters.VirtualScreenHeight;
        var bandTop=vTop+120;var bandBottom=Math.Max(bandTop+40,vBottom-170);
        var pairs=participants.Select(n=>(n,w:service.WindowOf(n))).Where(p=>p.w is {IsLoaded:true}).Select(p=>(p.n,w:p.w!)).ToList();
        if(pairs.Count==0){End(EggResult.Quit);return;}
        var mainPlats=new List<WidgetWindow>();
        // 主路線占約 55%：唯一保證可通關的隨機遊走鏈；其餘全部作為誘餌斷頭路
        var mainCount=Math.Clamp((int)Math.Ceiling(pairs.Count*.55),Math.Min(6,pairs.Count),pairs.Count);
        var widths=pairs.Select(p=>Math.Max(200,p.w.Width)).ToList();
        // 帶難點的主路線：折返爬塔、極限跳、懸崖速降、普通跳隨機串接；平直路線判廢重生成
        var bestMain=new List<Rect>();double bestScore=double.MinValue;
        for(var round=0;round<28;round++)
        {
            var rects=new List<Rect>();
            var dir=rng.Next(2)==0?1d:-1d;
            var px=vLeft+40+rng.NextDouble()*Math.Max(0,vRight-vLeft-widths[0]-80);
            var py=bandBottom-rng.NextDouble()*(bandBottom-bandTop)*.3; // 起點偏低，把垂直空間留給難點
            rects.Add(new Rect(px,py,widths[0],64));
            var reversals=0;var i=1;
            var finale=Math.Min(2,mainCount-1); // 終局爬塔預留平台數
            while(i<mainCount-finale)
            {
                var seg=rng.Next(100);
                if(seg<38&&i+1<mainCount-finale) // 折返爬塔段：2~3 塊交替方向陡升，必須精準折返跳
                {
                    var steps=Math.Min(2+rng.Next(2),mainCount-finale-i);
                    for(var s=0;s<steps;s++)
                    {
                        dir=-dir;reversals++;
                        PlaceNext(rects,widths[i],widths[i-1],-(96+rng.NextDouble()*22),.45+rng.NextDouble()*.4,ref dir,ref px,ref py,bandTop,bandBottom);
                        rects.Add(new Rect(px,py,widths[i],64));i++;
                    }
                }
                else if(seg<64) // 極限跳段：間隙逼近物理極限（88%~97% MaxEdgeGap）
                {
                    PlaceNext(rects,widths[i],widths[i-1],rng.Next(-30,110),.88+rng.NextDouble()*.09,ref dir,ref px,ref py,bandTop,bandBottom);
                    rects.Add(new Rect(px,py,widths[i],64));i++;
                }
                else if(seg<84) // 懸崖速降段：大落差俯衝，接台容錯窄
                {
                    PlaceNext(rects,widths[i],widths[i-1],170+rng.NextDouble()*230,.55+rng.NextDouble()*.35,ref dir,ref px,ref py,bandTop,bandBottom);
                    rects.Add(new Rect(px,py,widths[i],64));i++;
                }
                else // 普通跳，稽釋節奏
                {
                    PlaceNext(rects,widths[i],widths[i-1],rng.Next(-80,140),.5+rng.NextDouble()*.3,ref dir,ref px,ref py,bandTop,bandBottom);
                    rects.Add(new Rect(px,py,widths[i],64));i++;
                }
            }
            // 終局爬塔：收尾強制連續陡升折返，進洞前必須精準收步
            while(i<mainCount)
            {
                dir=-dir;reversals++;
                PlaceNext(rects,widths[i],widths[i-1],-(100+rng.NextDouble()*18),.5+rng.NextDouble()*.35,ref dir,ref px,ref py,bandTop,bandBottom);
                rects.Add(new Rect(px,py,widths[i],64));i++;
            }
            var span=rects.Max(r=>r.Right)-rects.Min(r=>r.Left);
            var vSpread=rects.Max(r=>r.Y)-rects.Min(r=>r.Y);
            var score=span+vSpread*1.5+reversals*120; // 平直路線（低落差、少折返）得分低，不會被選中
            if(score>bestScore){bestScore=score;bestMain=rects;}
            if(span>=(vRight-vLeft)*.6&&vSpread>=(bandBottom-bandTop)*.45&&reversals>=3)break;
        }
        var placed=new List<Rect>(bestMain);
        for(var i=0;i<mainCount;i++)
        {
            var (n,w)=pairs[i];
            n.IsCollapsed=true;w.ApplyCollapseState(false);
            w.Left=bestMain[i].X;w.Top=bestMain[i].Y;w.Topmost=true;if(!w.IsVisible)w.Show();
            mainPlats.Add(w);platforms.Add(w);
        }
        // 出生點取主路線幾何最左平台、黑洞取主路線幾何最右平台
        var first=mainPlats.OrderBy(p=>p.Left).First();
        var last=mainPlats.OrderByDescending(p=>p.Left+p.Width).First();
        if(ReferenceEquals(first,last)&&mainPlats.Count>1)last=mainPlats.OrderByDescending(p=>p.Left+p.Width).ElementAt(1);
        var holeX=last.Left+Math.Max(200,last.Width)/2;var holeY=last.Top-50;
        // 誘餌斷頭路：其餘平台全屏隨機撒佈；黑洞 460px 保護區內禁止誘餌
        for(var i=mainCount;i<pairs.Count;i++)
        {
            var (n,w)=pairs[i];
            n.IsCollapsed=true;w.ApplyCollapseState(false);
            var width=Math.Max(200,w.Width);
            double dx=vLeft+20,dyPos=bandBottom;var ok=false;
            for(var attempt=0;attempt<40&&!ok;attempt++)
            {
                dx=vLeft+20+rng.NextDouble()*Math.Max(1,vRight-vLeft-width-40);
                dyPos=bandTop+rng.NextDouble()*Math.Max(1,bandBottom-bandTop);
                var cx=dx+width/2;var cy=dyPos+32;
                if(Math.Sqrt((cx-holeX)*(cx-holeX)+(cy-holeY)*(cy-holeY))<460)continue;
                var cand=new Rect(dx-26,dyPos-46,width+52,64+92);
                ok=placed.All(r=>!r.IntersectsWith(cand));
            }
            w.Left=dx;w.Top=dyPos;w.Topmost=true;if(!w.IsVisible)w.Show();
            placed.Add(new Rect(dx,dyPos,width,64));
            platforms.Add(w);
        }
        // 自動滑動平台：主路線中段每隔一塊挑一個小擺幅橫移（間隙容錯內），誘餌平台則大膽橫/縱漂移
        for(var i=1;i<mainPlats.Count-1;i++)
            if(i%3==1)movers.Add(new Mover{Win=mainPlats[i],BaseX=mainPlats[i].Left,BaseY=mainPlats[i].Top,AmpX=42+rng.NextDouble()*28,Omega=.9+rng.NextDouble()*.7,Phase=rng.NextDouble()*Math.Tau});
        foreach(var w in platforms.Except(mainPlats))
            if(rng.Next(100)<45)movers.Add(rng.Next(2)==0
                ?new Mover{Win=w,BaseX=w.Left,BaseY=w.Top,AmpX=70+rng.NextDouble()*80,Omega=.7+rng.NextDouble()*.8,Phase=rng.NextDouble()*Math.Tau}
                :new Mover{Win=w,BaseX=w.Left,BaseY=w.Top,AmpY=44+rng.NextDouble()*46,Omega=.7+rng.NextDouble()*.8,Phase=rng.NextDouble()*Math.Tau});
        holeCX=holeX;holeCY=last.Top-50;
        // 全屏透明點擊穿透畫布：球、金幣、蜂賊、黑洞、HUD、彩帶全部畫在同一層，天然壓在平台之上
        BuildOverlay();
        BuildHoleVisual(holeX-42,last.Top-92);
        // 金幣導航：沿主路線每塊平台上方撒 1 枚 + 相鄰平台間隙上空補 1 枚，跟著金幣走就是通關路
        for(var i=1;i<mainPlats.Count;i++)
        {
            var p=mainPlats[i];
            AddCoin(p.Left+Math.Max(200,p.Width)/2,p.Top-64);
            var q=mainPlats[i-1];
            AddCoin((p.Left+Math.Max(200,p.Width)/2+q.Left+Math.Max(200,q.Width)/2)/2,Math.Min(p.Top,q.Top)-120);
        }
        // 蜂賊巡邏：主路線的寬平台，最多 5 隻；出生台與黑洞台一律排除——出生台按幾何最左選取未必是鏈首，單靠 Skip(1) 有幾率漏排導致開局貼臉即死
        foreach(var p in mainPlats.Where(p=>!ReferenceEquals(p,first)&&!ReferenceEquals(p,last)&&p.Width>=240))
        {
            if(foes.Count>=5||rng.Next(100)>=50)continue;
            var f=new Foe{Plat=p,X=p.Left+p.Width/2,V=(rng.Next(2)==0?-1:1)*(68+rng.NextDouble()*52)};
            f.Visual=BuildFoeVisual();canvas!.Children.Add(f.Visual);foes.Add(f);
        }
        BuildBallVisual();
        ballX=first.Left+Math.Max(200,first.Width)/2-BallSize/2d;ballY=first.Top-BallSize;
        velX=velY=0;grounded=true;prevUp=true;coyote=CoyoteTime;jumpBuf=0;coinCount=0;standOn=first;phase=Phase.Play;
        BuildHud();
        overlay!.Show();
        SyncVisuals();
        BeeXDialog.Notify(null,L("蜂巢彩蛋"),L("←→／AD 移動，↑／空格 跳躍（長按跳更高）；吃金幣、踩扁蜂賊，抵達黑洞獲勝，Esc 隨時退出。"),service.State);
        clock.Restart();lastT=0;startT=0;
        CompositionTarget.Rendering+=OnFrame;hooked=true;
    }

    void End(EggResult result)
    {
        if(ended)return;ended=true;
        if(hooked){CompositionTarget.Rendering-=OnFrame;hooked=false;}
        try{overlay?.Close();}catch{}
        // 刪除臨時補足的平台
        foreach(var t in tempNests.ToList())service.Remove(t);
        // 還原用戶組件：位置、摺疊狀態、置頂
        foreach(var s in snapshots)
        {
            var nest=service.State.Nests.FirstOrDefault(n=>n.Id==s.Id);if(nest==null)continue;
            nest.Left=s.Left;nest.Top=s.Top;nest.IsCollapsed=s.Collapsed;nest.Pinned=s.Pinned;
            var w=service.WindowOf(nest);if(w==null)continue;
            w.Left=s.Left;w.Top=s.Top;w.ApplyCollapseState(false);w.Topmost=s.Pinned;
        }
        service.State.EasterEggUnlocked=true;service.Save();
        service.ResumeGlobalHotkeys();
        service.ApplyFloatingBallVisibility();
        service.EnsureEasterEggEntry();
        Running=false;
        if(result==EggResult.Won)BeeXDialog.Alert(null,L("蜂巢彩蛋"),F("恭喜你！手指康復運動 {0} 秒，收集金幣 {1} 枚。黑洞收下了你的懸浮球，遊戲入口已加入主控制台。",winSeconds,coinCount),service.State);
        else if(result==EggResult.Dead)BeeXDialog.Alert(null,L("蜂巢彩蛋"),L("懸浮球墜落了，遊戲結束。遊戲入口已加入主控制台。"),service.State);
        else if(result==EggResult.Stung)BeeXDialog.Alert(null,L("蜂巢彩蛋"),L("被蜂賊撞到了，遊戲結束。下次從頭頂踩扁它！遊戲入口已加入主控制台。"),service.State);
    }
}
