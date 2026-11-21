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

           这里加的是**权重混合**，不是替换：片段自己的轨道仍然是底稿，
           权重 0 时输出与改动前逐位相同；权重到 1 才完全看向 lookX/lookY。
           进和出都先按 LookRamp 线性爬到目标，再经 PetEase.Smooth 映射成曲线，
           所以「文件拖走后眼睛滑回随机张望」不需要额外的定时器或状态机 ——
           LookAway() 之后权重自己降到 0，底稿重新显形。

           防穿模不在这里做：GazeX/GazeY 只是方向参数，下游
           Pet3D.GazeShift 先钳到 ±1，PetShape.EyePair 再把眼球向量压到半径
           0.92 的球面内、z<0.12 剔除背面。喂到 ±1 也已经过同一道闸。 */
        bool lookOn;
        double lookX,lookY,lookWeight,lookAmp;
        const double LookRamp=.30;
        public double LookWeight{get{return lookWeight;}}
        public void LookAt(double x,double y){LookAt(x,y,1.0);}

        /* amount = 这一眼的强度（0..1）。球内是 1；离开球体之后按距离衰减，
           权重跟着降到 0，眼睛就平滑地交还给片段自己的 Gaze 轨道 ——
           而不是突然停在某个方向上。 */
        public void LookAt(double x,double y,double amount)
        {
            if(x<-1)x=-1;if(x>1)x=1;
            if(y<-1)y=-1;if(y>1)y=1;
            if(amount<0)amount=0;if(amount>1)amount=1;
            lookX=x;lookY=y;lookAmp=amount;lookOn=amount>0.001;
        }
        public void LookAway(){lookOn=false;}
        public void SetState(string value){if(state==value)return;state=value;clips=PetClips.ForState(value);bag=null;previous=-1;NextClip();}
        public void PlayClip(string name)
        {
            PetClip selected=ReferenceClips.Find(name);
            if(selected==null)throw new ArgumentException("Unknown reference clip: "+name,"name");
            state=name;clips=new PetClip[]{selected};bag=null;previous=-1;NextClip();
        }
        public void Poke(){AddImpulse(false);} public void Spin(){AddImpulse(true);}
        void AddImpulse(bool spin){if(impulses.Count>=8)impulses.RemoveAt(0);PetImpulse p=new PetImpulse();p.Spin=spin;impulses.Add(p);}
        void NextClip()
        {
            if(bag==null||bagIndex>=bag.Length){bag=random.Shuffle(clips.Length,previous);bagIndex=0;}
            previous=bag[bagIndex++];bridgeFrom=current;clip=clips[previous];clipTime=0;bridgeTime=0;
        }
        PetFrame BaseFrame()
        {
            PetFrame target=clip.Sample(clipTime);if(bridgeTime>=BridgeDuration)return target;
            double t=AnimationMath.Bezier(bridgeTime/BridgeDuration,PetEase.Bridge);PetFrame a=bridgeFrom;
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
        public void Advance(double seconds)
        {
            if(double.IsNaN(seconds)||double.IsInfinity(seconds)||seconds<0)throw new ArgumentOutOfRangeException("seconds");
            seconds=Math.Min(seconds,0.1);
            accumulator+=seconds;while(accumulator+1e-10>=Step){accumulator-=Step;Tick();}
        }
        void Tick()
        {
            if(bridgeTime<BridgeDuration)bridgeTime=Math.Min(BridgeDuration,bridgeTime+Step);
            else{clipTime+=Step;if(clipTime+1e-9>=clip.Duration){clipTime=clip.Duration;NextClip();}}
            PetFrame f=BaseFrame();morph.Target=f.MorphAmount;morph.Advance(Step,14,1);
            f.MorphAmount=AnimationMath.Clamp(morph.X,0,1);f.HasMorph=true;
            PetExpression wanted=f.Expression;
            for(int i=impulses.Count-1;i>=0;i--)
            {
                PetImpulse p=impulses[i];p.Age+=Step;
                if(p.Age>=p.Duration){impulses.RemoveAt(i);continue;}
                p.Apply(ref f);wanted=p.Spin?PetExpression.Happy:PetExpression.Surprised;
                f.State=p.Spin?"spin":"poked";impulses[i]=p;
            }
            /* 指针注视混合。放在冲量之后、表情混合之前：冲量改的是旋转/缩放，
               不碰视线，所以两者互不覆盖；表情混合只读 Expression。 */
            double lookTarget=lookOn?lookAmp:0.0;
            if(lookWeight<lookTarget)lookWeight=Math.Min(lookTarget,lookWeight+Step/LookRamp);
            else if(lookWeight>lookTarget)lookWeight=Math.Max(lookTarget,lookWeight-Step/LookRamp);
            if(lookWeight>0)
            {
                double w=AnimationMath.Bezier(lookWeight,PetEase.Smooth);
                f.GazeX=AnimationMath.Lerp(f.GazeX,lookX,w);
                f.GazeY=AnimationMath.Lerp(f.GazeY,lookY,w);
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
