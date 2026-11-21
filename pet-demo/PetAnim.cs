using System;
using System.Collections.Generic;

namespace WinIsland
{
    enum PetExpression { Neutral, Happy, Surprised, Sleepy, Focused, Sad, Angry, Curious, Wink, Calm }
    struct PetFrame
    {
        public double GazeX,GazeY,Yaw,Pitch,Roll,Lid,ShapeIndex,MorphAmount;
        public double SquashX,SquashY,Zoom,OffsetX,OffsetY;
        public PetExpression Expression;
        public PetEyePose LeftEye,RightEye;
        public bool HasEyePose,HasMorph;
        public string State;
    }
    struct PetInput { public double Time,GazeX,GazeY; public bool HasGaze; public double PokeAge,SpinAge; }

    sealed class PetAnimationRuntime
    {
        public const double Step=1.0/120;
        const double BridgeDuration=.35;
        readonly PetRandom random;
        readonly PetSpring morph=new PetSpring(0);
        readonly List<PetImpulse> impulses=new List<PetImpulse>();
        PetClip[] clips=PetClips.Idle;
        int[] bag; int bagIndex,previous=-1;
        PetClip clip; double clipTime,bridgeTime=BridgeDuration,accumulator,faceTime=1;
        PetFrame bridgeFrom,current;
        PetEyePose leftFrom,rightFrom,left,right;
        PetExpression expression;
        string state="idle";
        public PetAnimationRuntime(uint seed)
        {
            random=new PetRandom(seed);clip=PetClips.Idle[0];
            left=leftFrom=PetFaces.Eye(PetExpression.Neutral,-1);right=rightFrom=PetFaces.Eye(PetExpression.Neutral,1);
            current=clip.Sample(0);current.HasEyePose=current.HasMorph=true;current.LeftEye=left;current.RightEye=right;
        }
        public PetFrame Frame{get{return current;}}
        public string State{get{return state;}}

        /* ---------------------------------------------------- 指针注视

           为什么要有这条通道：眼睛方向一直只由片段里的 Gaze 轨道决定，
           外部想让它「看某个点」没有任何入口（PetInput 的 HasGaze/GazeX/GazeY
           从来没有被读过，自检里那条 gazeIgnoresPointer 就是这件事的见证）。

           这一版有两层，缺一层都不像「在看」：

           1. **双轴弹簧**（PetGazeTrack / PetGaze）。外部给的是目标方向，不是当前方向。
              拖放事件按鼠标到达，频率是抖的、还会连续换向；把目标直接写进帧里，
              眼睛会一帧跳到新角度。弹簧让转向自己滑过去、停下时自己到位 —— 常数取自
              上游 Bencho 的 eye tracker（k = 0.06 / d = 0.34，换算见 PetGaze.cs）。
           2. **权重混合**，仍然是混合不是替换：片段自己的轨道是底稿，权重 0 时输出与
              没有这条通道时逐位相同。进和出都按 LookRamp 线性爬到目标再经
              PetEase.Smooth 映射，所以「文件拖走后眼睛滑回随机张望」不需要额外的
              定时器或状态机 —— LookAway() 之后权重自己降到 0，底稿重新显形。

           防穿模不在这里做：GazeX/GazeY 只是方向参数，下游 Pet3D.FitGaze 会把转幅
           收到「两只眼睛的贴片都还在正面圆盘里」为止（判据见 Pet3D.EyePairFits）。 */
        bool lookOn;
        double lookWeight,lookAmp;
        readonly PetGazeTrack follow=new PetGazeTrack();
        const double LookRamp=.30;
        public double LookWeight{get{return lookWeight;}}
        /// <summary>弹簧当前值（诊断与自检用；帧里的 GazeX/GazeY 才是混合结果）。</summary>
        public double LookFollowX{get{return follow.X;}}
        public double LookFollowY{get{return follow.Y;}}
        public void LookAt(double x,double y){LookAt(x,y,1.0);}

        /* amount = 这一眼的强度（0..1）。桌宠上是 1：远近已经在
           PetGaze.Follow 里用 tanh 饱和掉了，不在这里再衰减一次。 */
        public void LookAt(double x,double y,double amount)
        {
            x=PetGaze.Clamp(x,-1.0,1.0);
            y=PetGaze.Clamp(y,-1.0,1.0);
            if(amount<0)amount=0;if(amount>1)amount=1;
            /* 上一轮已经收干净（权重 0）时把弹簧直接落在新方向上 ——
               否则新一次拖放的头 0.3s 里，眼睛会先荡到**上一次**文件的方向
               再折回来，读起来像「记错了」。权重 0 时弹簧反正看不见，
               这个 snap 不产生任何跳变；平滑由下面的权重斜坡负责。 */
            if(!lookOn)follow.Snap(x,y);else follow.Aim(x,y);
            lookAmp=amount;lookOn=amount>0.001;
        }
        public void LookAway(){lookOn=false;follow.Rest();}
        public void SetState(string value){if(state==value)return;state=value;clips=PetClips.ForState(value);bag=null;previous=-1;NextClip();}
        public void PlayClip(string name)
        {
            PetClip selected=ReferenceClips.Find(name);
            if(selected==null)throw new ArgumentException("Unknown reference clip: "+name,"name");
            state=name;clips=new PetClip[]{selected};bag=null;previous=-1;NextClip();
        }
        public void Poke(){AddImpulse(PetImpulse.KindPoke);} public void Spin(){AddImpulse(PetImpulse.KindSpin);}
        public void Jump(){AddImpulse(PetImpulse.KindJump);}
        void AddImpulse(int kind){if(impulses.Count>=8)impulses.RemoveAt(0);PetImpulse p=new PetImpulse();p.Kind=kind;impulses.Add(p);}
        void NextClip()
        {
            if(bag==null||bagIndex>=bag.Length){bag=random.Shuffle(clips.Length,previous);bagIndex=0;}
            previous=bag[bagIndex++];bridgeFrom=current;clip=clips[previous];clipTime=0;bridgeTime=0;
        }
        /* 保持态：第三层打开期间要「站住看鼠标」，底稿必须停在一个明确的姿态上，
           而不是把片段停在半途 —— 片段里有 squash 和整圈翻滚，冻在中间读起来
           像卡死。这里给的是一个中立静止帧：不缩放、不转、不形变，只有眼睛会动。 */
        static readonly PetFrame HoldPose=BuildHoldPose();
        static PetFrame BuildHoldPose()
        {
            PetFrame f=new PetFrame();
            f.SquashX=1;f.SquashY=1;f.Zoom=1;   /* 0 会把球压成一条线，必须显式给 1 */
            f.Expression=PetExpression.Neutral;
            f.State="hold";
            f.LeftEye=PetFaces.Eye(PetExpression.Neutral,-1);
            f.RightEye=PetFaces.Eye(PetExpression.Neutral,1);
            f.HasEyePose=f.HasMorph=true;
            return f;
        }

        static PetFrame Blend(PetFrame a,PetFrame target,double t)
        {
            target.GazeX=AnimationMath.Lerp(a.GazeX,target.GazeX,t);target.GazeY=AnimationMath.Lerp(a.GazeY,target.GazeY,t);
            /* 三个转轴都要按最短路归一，不能只归一 yaw。
               参考片段里有整圈翻滚（Working_A / Working_F 的 pitch 收到 -360，
               Bodymoves_A 收到 +360）。桥接是**线性**插值：从 -360 拉回 0 会让球
               在 0.35s 里倒着猛转一整圈（实测峰值 3348 度/秒，见
               shots/pet-integration/anim/runtime-scan.csv 的 REVERSE 行）。
               归一之后 -360 与 0 等价，这一段就真的不动了，翻滚本身照旧由片段内的
               关键帧驱动。 */
            const double degrees=180/Math.PI;
            target.Yaw=AnimationMath.Lerp(a.Yaw,AnimationMath.NearestAngle(target.Yaw*degrees,a.Yaw*degrees)/degrees,t);
            target.Pitch=AnimationMath.Lerp(a.Pitch,AnimationMath.NearestAngle(target.Pitch*degrees,a.Pitch*degrees)/degrees,t);
            target.Roll=AnimationMath.Lerp(a.Roll,AnimationMath.NearestAngle(target.Roll*degrees,a.Roll*degrees)/degrees,t);
            target.OffsetX=AnimationMath.Lerp(a.OffsetX,target.OffsetX,t);target.OffsetY=AnimationMath.Lerp(a.OffsetY,target.OffsetY,t);
            target.SquashX=AnimationMath.Lerp(a.SquashX,target.SquashX,t);target.SquashY=AnimationMath.Lerp(a.SquashY,target.SquashY,t);
            target.Zoom=AnimationMath.Lerp(a.Zoom,target.Zoom,t);target.Lid=AnimationMath.Lerp(a.Lid,target.Lid,t);
            target.MorphAmount=AnimationMath.Lerp(a.MorphAmount,target.MorphAmount,t);return target;
        }

        /* ---------------------------------------------------- 保持态（第三层）

           第三层打开期间：眼睛跟鼠标，身体不许有随机动作。落地方式是把底稿换成
           上面那个静止中立帧，并停掉「抽下一个片段」的调度。

           进出都从当前画面接：bridgeFrom = 当前帧、bridgeTime = 0，走同一条 Blend。
           直接切会让身体在换姿态那一帧硬跳一下，而这一段恰好是用户正看着的时候。

           为什么不用「冻结当前片段进度」：片段可能正好停在 squash 或翻滚的中段，
           冻在那儿看起来是坏了，不是站着。中立帧是明确姿态，读起来是刻意的。 */
        bool hold;
        public bool Held{get{return hold;}}
        public void Hold(bool on)
        {
            if(hold==on)return;
            hold=on;
            bridgeFrom=current;bridgeTime=0;
            /* 退出保持时立刻抽一个随机片段接上；从同一帧桥过去，所以底稿是
               「从站姿慢慢活过来」，不是突然开始做动作。 */
            if(!on)NextClip();
        }
        PetFrame BaseFrame()
        {
            PetFrame target=hold?HoldPose:clip.Sample(clipTime);
            if(bridgeTime>=BridgeDuration)return target;
            return Blend(bridgeFrom,target,AnimationMath.Bezier(bridgeTime/BridgeDuration,PetEase.Bridge));
        }
        public void Advance(double seconds)
        {
            if(double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<0)throw new ArgumentOutOfRangeException("seconds");
            seconds=Math.Min(seconds,0.1);
            accumulator+=seconds;while(accumulator+1e-10>=Step){accumulator-=Step;Tick();}
        }
        void Tick()
        {
            if(bridgeTime<BridgeDuration)bridgeTime=Math.Min(BridgeDuration,bridgeTime+Step);
            else if(!hold){clipTime+=Step;if(clipTime+1e-9>=clip.Duration){clipTime=clip.Duration;NextClip();}}
            PetFrame f=BaseFrame();morph.Target=f.MorphAmount;morph.Advance(Step,14,1);
            f.MorphAmount=AnimationMath.Clamp(morph.X,0,1);f.HasMorph=true;
            PetExpression wanted=f.Expression;
            for(int i=impulses.Count-1;i>=0;i--)
            {
                PetImpulse p=impulses[i];p.Age+=Step;
                if(p.Age>=p.Duration){impulses.RemoveAt(i);continue;}
                p.Apply(ref f);
                wanted = p.Kind==PetImpulse.KindSpin ? PetExpression.Happy
                       : p.Kind==PetImpulse.KindJump ? PetExpression.Happy
                       : PetExpression.Surprised;
                f.State = p.Kind==PetImpulse.KindSpin ? "spin"
                        : p.Kind==PetImpulse.KindJump ? "jump" : "poked";
                impulses[i]=p;
            }
            /* 指针注视混合。放在冲量之后、表情混合之前：冲量改的是旋转/缩放，
               不碰视线，所以两者互不覆盖；表情混合只读 Expression。 */
            double lookTarget=lookOn?lookAmp:0.0;
            if(lookWeight<lookTarget)lookWeight=Math.Min(lookTarget,lookWeight+Step/LookRamp);
            else if(lookWeight>lookTarget)lookWeight=Math.Max(lookTarget,lookWeight-Step/LookRamp);
            /* 弹簧每帧都推（固定 1/120 s 一步，和 Advance 的分组无关）：
               权重归零之后它自己收回 0，下一次拖放就没有旧方向可残留。 */
            follow.Advance(Step);
            if(lookWeight>0)
            {
                double w=AnimationMath.Bezier(lookWeight,PetEase.Smooth);
                f.GazeX=AnimationMath.Lerp(f.GazeX,PetGaze.Clamp(follow.X,-1.0,1.0),w);
                f.GazeY=AnimationMath.Lerp(f.GazeY,PetGaze.Clamp(follow.Y,-1.0,1.0),w);
            }
            if(wanted!=expression){leftFrom=left;rightFrom=right;expression=wanted;faceTime=0;}
            faceTime=Math.Min(.28,faceTime+Step);double blend=AnimationMath.Bezier(faceTime/.28,PetEase.Smooth);
            left=PetEyePose.Blend(leftFrom,PetFaces.Eye(expression,-1),blend);
            right=PetEyePose.Blend(rightFrom,PetFaces.Eye(expression,1),blend);
            f.Expression=expression;f.LeftEye=left;f.RightEye=right;f.HasEyePose=true;current=f;
        }
    }
    static class PetAnim
    {
        public static PetFrame Sample(PetInput input)
        {
            PetAnimationRuntime runtime=new PetAnimationRuntime(17);double time=Math.Max(0,input.Time);
            double poke=input.PokeAge>=0?Math.Max(0,time-input.PokeAge):double.PositiveInfinity;
            double spin=input.SpinAge>=0?Math.Max(0,time-input.SpinAge):double.PositiveInfinity,elapsed=0;
            while(elapsed<time-1e-10)
            {
                if(poke<=elapsed+1e-10){runtime.Poke();poke=double.PositiveInfinity;}
                if(spin<=elapsed+1e-10){runtime.Spin();spin=double.PositiveInfinity;}
                double next=Math.Min(time,elapsed+PetAnimationRuntime.Step);next=Math.Min(next,Math.Min(poke,spin));
                runtime.Advance(next-elapsed);elapsed=next;
            }
            if(poke<=time+1e-10)runtime.Poke();if(spin<=time+1e-10)runtime.Spin();return runtime.Frame;
        }
    }
}
