/* 【旧实现 · 不参与编译】
   动画引擎已迁到 ..\pet-demo\PetAnim.cs（帧结构同时挪到 PetClips.cs / AnimationCore.cs）。
   这里还声明 PetExpression / PetFrame / PetInput / PetAnim，加回编译列表会和
   pet-demo 的版本重复定义。 */
using System;

namespace WinIsland
{
    /* 桌宠的动画引擎。

       核心约定：Sample(input) 是纯函数 —— 给定时间与输入，返回一帧的全部参数。
       没有内部状态、没有累加器。这样一来：
         * 任何一帧都能重放，自检里可以直接断言「同一个 t 两次结果相同」；
         * 帧率高低不影响动画内容，掉帧只是少画几帧，不会走形；
         * 想改动画只改这里，窗口那边永远只负责「把帧贴到几何上」。

       状态不是散落的 if，而是一张时间表：
         idle  —— 视线漂移 + 呼吸 + 不规律眨眼
         poke  —— 被戳之后的一小段衰减振荡（压扁、回弹）
         spin  —— 被命令转一圈（yaw 走满 2π，眼睛会在背面消失）
         换形状 —— 每 9 秒一次，且强制藏在一次眨眼后面 */

    enum PetExpression
    {
        Neutral, Happy, Surprised, Sleepy, Focused, Sad, Angry, Curious, Wink
    }

    struct PetFrame
    {
        public double GazeX, GazeY;   /* -1..1，归一化视线 */
        public double Yaw;            /* 球面自转（弧度），0..2π */
        public double Pitch;          /* 球面俯仰（弧度） */
        public PetExpression Expression;
        public double Lid;            /* 0 = 睁眼，1 = 闭眼 */
        public double ShapeIndex;     /* 浮点形状索引，见 PetShape.Names */
        public double SquashX, SquashY;
        public double OffsetY;
        public string State;
    }

    struct PetInput
    {
        public double Time;           /* 秒，单调递增 */
        public double GazeX, GazeY;   /* -1..1，来自鼠标指示 */
        public bool HasGaze;
        public double PokeAge;        /* 被戳之后经过的秒数，< 0 表示没戳过 */
        public double SpinAge;        /* 转圈开始后经过的秒数，< 0 表示没转过 */
    }

    static class PetAnim
    {
        const double TAU = Math.PI * 2.0;

        /* 换形状的节奏。过渡窗口刻意排在片段开头之后一点点，
           这样刚启动时看到的是一个稳定的球，不会一上来就变形。 */
        const double SEG = 9.0;
        const double TR0 = 1.10;
        const double TR1 = 1.32;
        const double TR2 = 1.48;
        const double TR3 = 1.72;

        const double POKE_DUR = 1.15;
        const double SPIN_DUR = 0.95;

        public static PetFrame Sample(PetInput p)
        {
            PetFrame f = new PetFrame();
            double t = p.Time;
            int idleCycle = (int)Math.Floor(t / 5.0);
            int shuffleGroup = (int)Math.Floor(idleCycle / 5.0);
            int clipOffset = (int)(PetShape.Hash((uint)(shuffleGroup + 17) * 2654435761u) % 5u);
            int clip = (idleCycle % 5 + clipOffset) % 5;
            double clipTime = t - idleCycle * 5.0;
            double clipPhase = clipTime / 5.0;
            string[] clipNames = new string[] { "idle-drift", "idle-glance", "idle-nod", "idle-bounce", "idle-rest" };

            /* ---------------- 视线 ---------------- */

            /* 自动游荡：三个不同周期叠加，看起来不像循环 */
            double wx = 0.26 * Math.Sin(t * 0.37) + 0.12 * Math.Sin(t * 0.91 + 1.1) + 0.05 * Math.Sin(t * 2.13 + 0.4);
            double wy = 0.20 * Math.Sin(t * 0.47 + 2.0) + 0.09 * Math.Sin(t * 1.13 + 0.6);

            double clipLook = Math.Sin(clipPhase * TAU);
            double clipNod = Math.Sin(Math.PI * clipPhase);
            double clipGazeX = clip == 1 ? (clipPhase < 0.5 ? -0.42 : 0.42) : 0.0;
            double clipGazeY = clip == 2 ? -0.38 * clipNod : 0.0;
            if (clip == 0) clipGazeX += 0.18 * clipLook;

            if (p.HasGaze)
            {
                /* 桌面光标决定注意力方向，动作片段只保留轻微的身体语言。 */
                f.GazeX = wx * 0.16 + p.GazeX * 0.72 + clipGazeX * 0.12;
                f.GazeY = wy * 0.16 + p.GazeY * 0.72 + clipGazeY * 0.12;
            }
            else
            {
                f.GazeX = wx * 0.72 + clipGazeX;
                f.GazeY = wy * 0.72 + clipGazeY;
            }

            f.Pitch = clip == 2 ? 0.18 * clipNod : 0.025 * Math.Sin(t * 0.31);
            f.Expression = clip == 1 ? PetExpression.Curious
                         : clip == 2 ? PetExpression.Happy
                         : clip == 4 ? PetExpression.Sleepy
                         : PetExpression.Neutral;

            double gd = Math.Sqrt(f.GazeX * f.GazeX + f.GazeY * f.GazeY);
            if (gd > 0.95)
            {
                f.GazeX = f.GazeX / gd * 0.95;
                f.GazeY = f.GazeY / gd * 0.95;
            }

            /* ---------------- 呼吸 ---------------- */

            /* 0.5% 的膨胀，小到几乎看不出幅度，但少了就会显得是张静止图 */
            double breath = 0.0055 * Math.Sin(t * (TAU / 4.3));
            double sqx = 1.0 + breath;
            double sqy = 1.0 - breath;

            /* ---------------- 眨眼 ---------------- */

            /* 两个不同周期叠加 → 间隔不规整。固定周期会很快被看出是循环。 */
            double lid = BlinkAt(t + 0.35, 3.7, 0.22);
            double second = BlinkAt(t + 2.05, 5.3, 0.26);
            if (second > lid) lid = second;

            /* ---------------- 形状与 morph ---------------- */

            double segF = Math.Floor(t / SEG);
            double local = t - segF * SEG;
            int cur = PetShape.PickShape(segF);
            int prev = PetShape.PickShape(segF - 1.0);

            double shape;
            if (local < TR0)
            {
                shape = 0.0;
            }
            else if (local < TR1)
            {
                double u = (local - TR0) / (TR1 - TR0);
                /* Ease into the selected silhouette, then return to the reference ball. */
                double e = 1.0 - Math.Pow(1.0 - u, 2.2);
                shape = cur * e;
            }
            else if (local < TR2)
            {
                shape = cur;
            }
            else if (local < TR3)
            {
                double u = (local - TR2) / (TR3 - TR2);
                double smooth = u * u * (3.0 - 2.0 * u);
                shape = cur * (1.0 - smooth);
            }
            else
            {
                shape = 0.0;
            }

            /* Morph behind a blink so the silhouette change does not pop visibly. */
            bool morphing = local >= TR0 - 0.05 && local < TR3 + 0.06;
            if (morphing && lid < 0.94) lid = 0.94;

            /* ---------------- 戳一下 ---------------- */

            string state = clipNames[clip];
            if (clip == 3)
            {
                double bounce = Math.Sin(Math.PI * clipPhase);
                f.OffsetY -= 5.0 * bounce * bounce;
                sqy += 0.07 * bounce;
                sqx -= 0.035 * bounce;
                if (clipPhase > 0.32 && clipPhase < 0.42) lid = Math.Max(lid, 0.65);
            }

            if (p.PokeAge >= 0 && p.PokeAge < POKE_DUR)
            {
                state = "poked";
                f.Expression = PetExpression.Surprised;
                double age = p.PokeAge;

                /* 衰减振荡：一压一回弹再收敛，比单次 ease 更有"肉感" */
                double env = Math.Exp(-age * 4.6);
                double osc = Math.Cos(age * 27.0) * env;

                sqx += 0.15 * osc;
                sqy -= 0.15 * osc;

                /* 落地时往下沉一点，配合压扁才像有重量 */
                f.OffsetY = 2.4 * env;

                /* 被戳会眯一下眼，但很快恢复，否则像生气 */
                if (age < 0.22) lid = Math.Max(lid, 0.55 * (1.0 - age / 0.22));
            }

            /* ---------------- 转圈 ---------------- */

            if (p.SpinAge >= 0 && p.SpinAge < SPIN_DUR)
            {
                state = "spin";
                double u = p.SpinAge / SPIN_DUR;
                double e = 1.0 - Math.Pow(1.0 - u, 3.0);   /* ease-out */
                f.Yaw += TAU * e;
                f.Expression = PetExpression.Happy;
                /* 转起来的时候眼睛本来就贴不住球面了，稍微眯一点更可信 */
                lid = Math.Max(lid, 0.12);
            }

            f.Lid = lid;
            f.ShapeIndex = shape;
            f.SquashX = sqx;
            f.SquashY = sqy;
            f.State = state;
            return f;
        }

        /// <summary>周期性的眨眼包络：在 [0, dur) 窗口内给出 0→1→0。</summary>
        static double BlinkAt(double t, double period, double dur)
        {
            if (t < 0) return 0;
            double m = t - Math.Floor(t / period) * period;
            if (m >= dur) return 0;
            double u = m / dur;
            return Math.Sin(Math.PI * u);
        }
    }
}