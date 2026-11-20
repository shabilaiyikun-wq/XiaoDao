using System;

namespace WinIsland
{
    struct PetEyePose
    {
        public double Width, Height, Tilt, Bend, OffsetY;
        public PetEyePose(double w, double h, double tilt, double bend, double y)
        { Width = w; Height = h; Tilt = tilt; Bend = bend; OffsetY = y; }
        public static PetEyePose Blend(PetEyePose a, PetEyePose b, double t)
        { return new PetEyePose(AnimationMath.Lerp(a.Width,b.Width,t), AnimationMath.Lerp(a.Height,b.Height,t), AnimationMath.Lerp(a.Tilt,b.Tilt,t), AnimationMath.Lerp(a.Bend,b.Bend,t), AnimationMath.Lerp(a.OffsetY,b.OffsetY,t)); }
    }
    static class PetFaces
    {
        // Original dimensions for this demo, not upstream expression data.
        public static PetEyePose Eye(PetExpression e, int side)
        {
            switch (e)
            {
                case PetExpression.Happy: return new PetEyePose(.29,.066,side*-.06,.09,.02);
                case PetExpression.Surprised: return new PetEyePose(.23,.64,0,0,.025);
                case PetExpression.Sleepy: return new PetEyePose(.25,.13,side*.10,-.02,-.03);
                case PetExpression.Focused: return new PetEyePose(.18,.35,side*-.14,0,0);
                case PetExpression.Sad: return new PetEyePose(.20,.34,side*.30,-.025,-.035);
                case PetExpression.Angry: return new PetEyePose(.24,.22,side*-.42,0,0);
                case PetExpression.Curious: return new PetEyePose(.195,side<0?.56:.40,side*.08,0,side<0?.025:-.02);
                case PetExpression.Wink: return new PetEyePose(.22,side<0?.51:.028,0,side<0?0:.025,0);
                /* calm：上游是一个「宽而扁、内眼角略外推」的横向透镜
                   （pts 沿 x 铺开、oval [0.296,0.247]、ovalMix 0），
                   和 neutral（竖长）明显不同。这里按本项目自己的量纲近似成
                   宽而扁的胶囊：比 neutral 宽、比 neutral 矮，且不会越到另一只眼上。
                   注意：形状仍是近似，不是上游逐点复现。 */
                case PetExpression.Calm: return new PetEyePose(.30,.17,0,side*.04,-.015);
                default: return new PetEyePose(.195,.51,0,0,0);
            }
        }
    }
    sealed class PetFaceKey
    {
        public double Time; public PetExpression Expression;
        public PetFaceKey(double time, PetExpression expression) { Time=time; Expression=expression; }
    }
    sealed class PetClip
    {
        public string Name; public double Duration=5; public int Shape;
        public PetTrack Position, Rotation, Squash, Lids, Gaze, Morph, Scale;
        public PetFaceKey[] Expressions=new PetFaceKey[0];
        public double[] BlinkTimes=new double[0];
        public PetFrame Sample(double time)
        {
            PetFrame f=new PetFrame(); f.SquashX=f.SquashY=f.Zoom=1; f.State=Name; double[] v;
            if(Position!=null&&(v=Position.Sample(time))!=null){f.OffsetX=v[0]*74;f.OffsetY=-v[1]*74;}
            if(Rotation!=null&&(v=Rotation.Sample(time))!=null){f.Pitch=v[0]*Math.PI/180;f.Yaw=v[1]*Math.PI/180;f.Roll=v[2]*Math.PI/180;}
            if(Squash!=null&&(v=Squash.Sample(time))!=null){f.SquashX=v[0];f.SquashY=v[1];}
            else{double breath=.0055*Math.Sin(time*Math.PI*2/4.3);f.SquashX+=breath;f.SquashY-=breath;}
            if(Lids!=null&&(v=Lids.Sample(time))!=null)f.Lid=AnimationMath.Clamp(v[0],0,1);
            for(int i=0;i<BlinkTimes.Length;i++)
            {
                double age=time-BlinkTimes[i], lid=0;
                if(age>=0&&age<0.26)
                {
                    if(age<0.071)lid=age/0.071;
                    else if(age<0.104)lid=1;
                    else lid=1-(age-0.104)/0.156;
                    f.Lid=Math.Max(f.Lid,AnimationMath.Clamp(lid,0,1));
                }
            }
            if(Gaze!=null&&(v=Gaze.Sample(time))!=null){f.GazeX=v[0];f.GazeY=v[1];}
            if(Morph!=null&&(v=Morph.Sample(time))!=null)f.MorphAmount=v[0];
            if(Scale!=null&&(v=Scale.Sample(time))!=null)f.Zoom=v[0]/ReferenceClips.DefaultZoom;
            f.ShapeIndex=Shape;
            for(int i=0;i<Expressions.Length&&Expressions[i].Time<=time+1e-9;i++)f.Expression=Expressions[i].Expression;
            f.LeftEye=PetFaces.Eye(f.Expression,-1);f.RightEye=PetFaces.Eye(f.Expression,1);f.HasEyePose=true;
            return f;
        }
    }
    static class PetClips
    {
        static PetKey K(double t,params double[] v){return new PetKey(t,v);}
        static PetTrack Blink(double t){return new PetTrack(K(0,0),K(t,0),K(t+.117,1),K(t+.17,1),K(t+.26,0));}
        // Demo-authored choreography, evaluated by the reference-style track engine.
        static readonly PetClip[] AuthoredIdle={
          new PetClip{Name="idle-rest",Duration=4.8,Squash=new PetTrack(K(0,1,1),K(2.4,1.006,.994),K(4.8,1,1)),Lids=Blink(3.15)},
          new PetClip{Name="idle-glance",Duration=5.4,Gaze=new PetTrack(K(0,0,0),K(.55,-.76,.18),K(1.6,-.76,.18),K(2.15,.72,-.18),K(3.6,.72,-.18),K(4.3,0,0)),Rotation=new PetTrack(K(0,0,0,0),K(.7,0,-8,3),K(1.6,0,-8,3),K(2.4,0,8,-3),K(3.6,0,8,-3),K(4.4,0,0,0)),Expressions=new[]{new PetFaceKey(.15,PetExpression.Curious),new PetFaceKey(4.4,PetExpression.Neutral)},Lids=Blink(3.8)},
          new PetClip{Name="idle-happy",Duration=4.5,Position=new PetTrack(K(0,0,0),K(1,0,.055),K(2.4,0,.055),K(3.5,0,0)),Rotation=new PetTrack(K(0,0,0,0),K(1.2,-4,0,-6),K(2.6,-4,0,5),K(3.8,0,0,0)),Expressions=new[]{new PetFaceKey(.35,PetExpression.Happy),new PetFaceKey(3.5,PetExpression.Neutral)},Lids=Blink(3.9)},
          new PetClip{Name="idle-look-up",Duration=5.1,Gaze=new PetTrack(K(0,0,0),K(.8,.38,.62),K(2.7,.38,.62),K(3.4,-.55,.42),K(4.4,0,0)),Rotation=new PetTrack(K(0,0,0,0),K(1,-7,5,-4),K(2.7,-7,5,-4),K(4.5,0,0,0)),Expressions=new[]{new PetFaceKey(.25,PetExpression.Focused),new PetFaceKey(4.5,PetExpression.Neutral)},Lids=Blink(3.5)},
          new PetClip{Name="idle-sleepy",Duration=5.8,Rotation=new PetTrack(K(0,0,0,0),K(2.2,7,0,5),K(3.5,7,0,5),K(4.8,0,0,0)),Expressions=new[]{new PetFaceKey(.45,PetExpression.Sleepy),new PetFaceKey(4.7,PetExpression.Neutral)},Lids=Blink(4.65)},
          new PetClip{Name="idle-wink",Duration=4.2,Rotation=new PetTrack(K(0,0,0,0),K(1.3,0,-4,-7),K(2.3,0,-4,-7),K(3.4,0,0,0)),Expressions=new[]{new PetFaceKey(.65,PetExpression.Wink),new PetFaceKey(2.2,PetExpression.Happy),new PetFaceKey(3.4,PetExpression.Neutral)}},
          new PetClip{Name="idle-morph",Duration=5.2,Shape=1,Morph=new PetTrack(K(0,0),K(.8,0),K(.917,0),K(.94,1),K(.95,1),K(.97,0),K(5.2,0)),Squash=new PetTrack(K(0,1,1),K(1.3,.97,1.03),K(3.8,1,1)),Expressions=new[]{new PetFaceKey(.7,PetExpression.Curious),new PetFaceKey(1.06,PetExpression.Neutral)},Lids=Blink(.8)}
        };
        public static readonly PetClip[] Idle = AuthoredIdle;
        public static PetClip MorphDemo { get { return AuthoredIdle[6]; } }
        public static PetClip[] ForState(string s)
        { switch(s){case "happy":return new[]{ReferenceClips.Find("Done_A"),ReferenceClips.Find("Done_B"),ReferenceClips.Find("Done_C")};case "thinking":return new[]{ReferenceClips.Find("Working_A"),ReferenceClips.Find("Working_B"),ReferenceClips.Find("Working_C"),ReferenceClips.Find("Working_D"),ReferenceClips.Find("Working_E"),ReferenceClips.Find("Working_F")};case "sleepy":return new[]{ReferenceClips.Find("Blocked_C")};case "curious":return new[]{ReferenceClips.Find("Working_B")};case "morph":return new[]{AuthoredIdle[6]};default:return Idle;} }
    }
}
