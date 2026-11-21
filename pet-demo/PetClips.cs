using System;
using System.Collections.Generic;

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
                /* Bend 的语义（由 Pet3D.EyeBoundary 消费）：拱起幅度 = Bend × **眼宽**。
                   |Bend| ≥ Pet3D.ArchBend(0.25) 才画成拱桥形，以下仍是胶囊。
                   Happy 的 0.38 意思是「拱起 0.38 个眼宽」——
                   眼睛宽 0.29、拱高因此约 0.11，比例约 1:2.6，才看得出来是个拱。
                   （早先按眼高算过一次：0.066×0.9=0.059，屏幕上只有 1 DIP 高、
                   而眼宽 4.9 DIP，比例 1:5，看起来仍然是一根平线 —— 所以改按眼宽。） */
                case PetExpression.Happy: return new PetEyePose(.29,.066,side*-.06,.38,.02);
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
        /* 鼠标停到球上时的样子：低头 + 眯眼 + 左右摇头，循环播放直到鼠标离开。

           三个通道各管一件事，别混：
             Rotation 的 [0]/[1]/[2] = pitch / yaw / roll（度，见 PetClip.Sample）；
                       正 pitch 是低头（idle-look-up 用的是 -7，那是抬头）。
             Lids     0~1，1 = 完全闭眼；Pet3D 里 Height *= (1-Lid)，
                       所以 0.55 就是「眼睛只剩四成半高」= 眯眼。
             Gaze     的 +Y 向上；低头配一个向下的注视，姿态才自洽。

           为什么不写 Expressions：默认就是 Neutral（竖长那种眼型），
           叠上 Lid 0.55 正好是「半睁半闭的眯眼」。换成 Calm（宽而扁）再压
           0.55 会扁到几乎看不见眼睛，读起来像闭上了 —— 那不是要的效果。

           时长 2.6s：里面走完 2 个来回的摇头。它是循环片段，
           鼠标一离开就换回 idle，所以首尾都回到 0 姿态，接回 idle 不会跳。

           声明顺序：必须排在 Idle 之前。Idle 的初始化会去查「被绑定的状态用到哪些
           片段」，万一以后把 hover 也绑上事件，那次查询就会取到这个字段；
           静态字段按文本顺序初始化，排在后面就是 null。 */
        public static readonly PetClip Hover = new PetClip {
            Name = "hover-shy", Duration = 2.6,
            Rotation = new PetTrack(
                K(0, 10, 0, 0), K(.35, 12, -13, 2), K(.8, 12, 13, -2),
                K(1.25, 12, -13, 2), K(1.7, 12, 13, -2), K(2.15, 11, 0, 1), K(2.6, 10, 0, 0)),
            Lids = new PetTrack(K(0, .18), K(.3, .55), K(2.3, .55), K(2.6, .18)),
            Gaze = new PetTrack(K(0, 0, 0), K(.4, 0, -.42), K(2.2, 0, -.42), K(2.6, 0, -.12)),
            Squash = new PetTrack(K(0, 1, 1), K(.4, 1.02, .975), K(2.2, 1.015, .985), K(2.6, 1, 1))
        };

        public static readonly PetClip[] Idle = BuildIdlePool();

        /* 随机待机池 = 7 段自制待机 + 参考动画里**没被绑定占用**的那些。

           池子的组成不是手工名单，而是从绑定表推导的（见 BuildIdlePool）。
           规则一句话：**被绑到事件上的动作，一律不进随机池**。

           为什么要有这条：一个片段一旦被用来表达「某事发生了」，它就不能同时
           在待机里随机出现 —— 否则「Done」既是「收工庆祝」又是路过的一个随机动画，
           信号就废了（你会分不清它是在庆祝还是在发呆）。这也是添加 hover-shy 时
           一开始就没让它进池子的同一个理由。

           为什么单独再排掉 Blocked_A：它是「被拦住」的负向动作，表情走
           confused→neutral，在没有上下文的自播里只会读成「它卡住了」。
           这跟绑定无关，是内容上的取舍。手点「全部动画」仍然可以播它。

           为什么 7 段自制排在最前：PetAnim 构造时拿 Idle[0] 当初始片段，
           起手静止姿态因此与改动前逐帧一致（外观没变）。 */
        static PetClip[] BuildIdlePool()
        {
            PetClip[] authored = AuthoredIdle;
            List<PetClip> pool = new List<PetClip>(authored);
            try
            {
                string[] reserved = ReservedClipNames();
                foreach (PetClipGroup group in ReferenceClips.Groups)
                {
                    for (int i = 0; i < group.Clips.Length; i++)
                    {
                        string name = group.Clips[i];
                        if (Array.IndexOf(reserved, name) >= 0) continue;
                        PetClip clip = ReferenceClips.Find(name);
                        if (clip != null) pool.Add(clip);
                    }
                }
            }
            catch (Exception)
            {
                /* 参考动画资源缺失或损坏时（ReferenceClips 静态构造会抛），退回 7 段自制。
                   这条路径在改动前根本不碰 ReferenceClips，不能让新加的池子把
                   「资源坏了岛就起不来」变成新故障。 */
                return authored;
            }
            return pool.ToArray();
        }

        /// <summary>
        /// 不许进随机池的片段名 = 「被绑定状态用到的所有片段」+ 内容上排掉的负向动作。
        ///
        /// 前半段是推导出来的：绑了新事件不用回来改这里。
        /// 万一某个绑定写了 ForState 不认识的状态名，ForState 会回落到 Idle
        /// （初始化期间还是 null），下面两处 null 判断会跳过它 —— 不炸，但也不会排除；
        /// 「状态名拼错」这件事由 AnimationTests 里的断言负责抓。
        /// </summary>
        public static string[] ReservedClipNames()
        {
            List<string> names = new List<string>();
            names.Add(ExcludedIdleReference);

            string[] bound = PetReactions.BoundStates();
            for (int s = 0; s < bound.Length; s++)
            {
                PetClip[] clips = ForState(bound[s]);
                if (clips == null) continue;
                for (int i = 0; i < clips.Length; i++)
                {
                    if (clips[i] == null || clips[i].Name == null) continue;
                    if (!names.Contains(clips[i].Name)) names.Add(clips[i].Name);
                }
            }
            return names.ToArray();
        }

        /* 内容上排掉的参考动画（与绑定无关，理由见上）。 */
        const string ExcludedIdleReference = "Blocked_A";

        public static PetClip MorphDemo { get { return AuthoredIdle[6]; } }

        public static PetClip[] ForState(string s)
        {
            switch (s)
            {
                case "happy": return new[]{ ReferenceClips.Find("Done_A"), ReferenceClips.Find("Done_B"), ReferenceClips.Find("Done_C") };
                case "thinking": return new[]{ ReferenceClips.Find("Working_A"), ReferenceClips.Find("Working_B"), ReferenceClips.Find("Working_C"), ReferenceClips.Find("Working_D"), ReferenceClips.Find("Working_E"), ReferenceClips.Find("Working_F") };
                case "sleepy": return new[]{ ReferenceClips.Find("Blocked_C") };
                case "curious": return new[]{ ReferenceClips.Find("Working_B") };
                case "morph": return new[]{ AuthoredIdle[6] };
                /* 被拎着走：晃两下。单独给个状态名，别复用 thinking —— 语义不同，
                   而且池子排除是按状态反查片段的，混用会把两件事绑死在一起。 */
                case "dizzy": return new[]{ ReferenceClips.Find("Rotation_C") };
                /* 鼠标停球上：一段循环。 */
                case "hover": return new[]{ Hover };
                default: return Idle;
            }
        }
    }
}
