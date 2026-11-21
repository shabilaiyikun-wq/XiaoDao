using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinIsland;

/* ============================================================================
   形变 / 换层的离线验证。

   跑的东西和产品跑的是同一份代码：MorphBody（主体弹簧 + 锚定边缘）、
   FaceBook/MorphFace（内容交叉淡入淡出）、IslandFrame（窗口尺寸与岛体落点）、
   PetLink（桌宠时间线）。这里不建窗口、不依赖 DPI/桌面分辨率，所以
   在任何一台机器上都是同一串数字。

   覆盖：
     1. 层级切换（第一层 → 第二层 → 第三层 → 收回 → 回第一层）
     2. 快速反向切换（展开到一半改收回、收回途中又展开）
     3. 内容面交叉淡入淡出 + 折返复用
     4. 窗口尺寸（每个场景 / 带预留）
     5. 桌宠位置（时间线连续性、落点、贴边换侧）

   刻意没覆盖的（见 notes/ANIMATION-PORT.md 的「未覆盖场景」）：
     真实 WPF 渲染时序、DPI 缩放、玻璃裁剪的像素结果、拖放/点击穿透。
     那些要跑 desktop\verify 下的在线探针。
   ========================================================================== */

static class MorphTests
{
    static int _pass, _fail;

    /* 60Hz。窗口用的是 DispatcherTimer(16ms)，这里对齐这个节拍，
       于是「每帧位移」的阈值可以直接和肉眼能看见的跳变对照。 */
    const double DT = 1.0 / 60.0;

    static void Check(bool ok, string name, string detail)
    {
        if (ok)
        {
            _pass++;
            Console.WriteLine("PASS " + name + (detail == null || detail.Length == 0 ? "" : "   " + detail));
        }
        else
        {
            _fail++;
            Console.WriteLine("FAIL " + name + "   " + detail);
        }
    }

    static void Info(string s)
    {
        Console.WriteLine("     " + s);
    }

    static string N(double v)
    {
        return Math.Round(v, 2).ToString();
    }

    /* WPF 的 UI 对象（Canvas/TranslateTransform）要在 STA 线程上建 */
    [STAThread]
    static int Main()
    {
        Console.WriteLine("== morph-tests ==");
        BodyConstants();
        LayerSwitching();
        RapidReverse();
        FaceCrossFade();
        FaceReverseReuse();
        FaceVisualNoScale();
        WindowSizing();
        PetTimeline();
        Console.WriteLine("RESULT pass=" + _pass + " fail=" + _fail);
        return _fail == 0 ? 0 : 1;
    }

    /* ------------------------------------------------------------ 主体 */

    /// <summary>
    /// 阶跃响应里「速度」的上界。二阶系统从静止阶跃时，
    /// 速度峰值 = 0.438·Δ·ω（ζ≈0.76 那组参数下），于是单帧位移的上界
    /// 就是 (v0 + 0.5·Δ·ω)·dt —— 0.5 比 0.438 略保守，留一点点集成误差余量。
    ///
    /// 这个上界是**判断有没有跳变**用的：真正的跳变（比如中途回到起点）
    /// 会是 Δ 量级的单帧位移，远远超过它；而正常加速跑得再快也不会超。
    /// </summary>
    static double PeakStep(double delta, double v0, double kmul, double dt)
    {
        double w = Math.Sqrt(MorphBody.K * kmul);
        return (Math.Abs(v0) + 0.5 * Math.Abs(delta) * w) * dt;
    }

    /// <summary>
    /// 一套「产品同款」的驱动：改目标 → 按 60Hz 推帧。
    /// expanding 的判定与 IslandWindow.OnFrame 一致：只有 compact 用收起刚度。
    /// </summary>
    class Rig
    {
        public readonly Spring W, H;
        public readonly MorphBody Body;
        public double AnchorX = 845, AnchorTop = 220;
        public readonly List<double> Ws = new List<double>();
        public readonly List<double> Hs = new List<double>();

        /* 跳变计数与最大「实测/上限」比值：>1 就是跳变 */
        public int Jumps;
        public double WorstRatio;
        public string WorstAt = "";

        public Rig(double w, double h)
        {
            W = new Spring(w);
            H = new Spring(h);
            Body = new MorphBody(W, H);
        }

        public void Aim(double w, double h)
        {
            Body.Aim(w, h);
        }

        public void Step(int frames, bool expanding)
        {
            for (int i = 0; i < frames; i++)
            {
                /* 上限要用**推进前**的速度和剩余距离算 */
                double boundW = PeakStep(W.Target - W.Value, W.Velocity, 1.0, DT);
                double boundH = PeakStep(H.Target - H.Value, H.Velocity, 1.06, DT);
                double prevW = W.Value, prevH = H.Value;

                Body.Step(DT, expanding);

                double stepW = Math.Abs(W.Value - prevW);
                double stepH = Math.Abs(H.Value - prevH);
                Ws.Add(W.Value);
                Hs.Add(H.Value);

                Judge(stepW, boundW, "w");
                Judge(stepH, boundH, "h");
            }
        }

        void Judge(double step, double bound, string axis)
        {
            /* 落位吸附不算跳变：Spring.Settled 在 |Δ|≤0.35、|v|≤12 时直接 Snap 到
               目标，那一帧的位移可以大于「按剩余距离算出来的解析上限」（因为
               上限已经趋近 0，而吸附量还有个 0.35px 的绝对值）。给一个 0.6px 的
               地板，就只挡住真正有量级的跳变。 */
            if (bound < 0.6) bound = 0.6;

            double ratio = step / bound;
            if (ratio > WorstRatio)
            {
                WorstRatio = ratio;
                WorstAt = axis + " step=" + step.ToString("0.00") + " bound=" + bound.ToString("0.00");
            }
            if (ratio > 1.15) Jumps++;
        }

        public int RunToSettle(bool expanding, int cap)
        {
            int n = 0;
            while (!Body.Settled && n < cap)
            {
                Step(1, expanding);
                n++;
            }
            return n;
        }
    }

    /* 0. 主体弹簧常数必须还是原来那组 —— 这次改动不动手感 */
    static void BodyConstants()
    {
        Console.WriteLine("-- body constants (unchanged from the pre-port implementation) --");
        Check(MorphBody.K == 250 && MorphBody.C == 24
              && MorphBody.CollapseK == 1.75 && MorphBody.CollapseC == 1.38,
              "spring constants unchanged",
              "K=" + MorphBody.K + " C=" + MorphBody.C
              + " collapseK=" + MorphBody.CollapseK + " collapseC=" + MorphBody.CollapseC);

        /* 阻尼比：扩张是欠阻尼（ζ≈0.759，落位时有一点点回弹，这是既有手感），
           收起更硬也更稳（ζ≈0.792）。这里把两个数钉住，改参数时自检会响。 */
        double zOpen = MorphBody.C / (2 * Math.Sqrt(MorphBody.K));
        double zClose = (MorphBody.C * MorphBody.CollapseC) / (2 * Math.Sqrt(MorphBody.K * MorphBody.CollapseK));
        Check(Math.Abs(zOpen - 0.759) < 0.01 && Math.Abs(zClose - 0.792) < 0.01,
              "damping ratios unchanged",
              "open=" + Math.Round(zOpen, 3) + " collapse=" + Math.Round(zClose, 3));
    }

    /* 1. 层级切换 */
    static void LayerSwitching()
    {
        Console.WriteLine("-- layer switching (idle -> compact -> expanded -> compact -> idle) --");

        foreach (SceneDef d in Scenes.All)
        {
            Rig r = new Rig(Scenes.IdleW, Scenes.IdleH);
            double ax = r.AnchorX, at = r.AnchorTop;

            /* 第一层 → 第二层：目标 = 该场景的收起尺寸 */
            r.Aim(d.CompW, d.CompH);
            int n1 = r.RunToSettle(false, 300);
            Check(Math.Abs(r.W.Value - d.CompW) < 0.5 && Math.Abs(r.H.Value - d.CompH) < 0.5,
                  "settle compact " + d.Key,
                  N(r.W.Value) + "x" + N(r.H.Value) + " want " + d.CompW + "x" + d.CompH
                  + " frames=" + n1);

            /* 第二层 → 第三层 */
            r.Aim(d.ExpW, d.ExpH);
            int n2 = r.RunToSettle(true, 300);
            Check(Math.Abs(r.W.Value - d.ExpW) < 0.5 && Math.Abs(r.H.Value - d.ExpH) < 0.5,
                  "settle expanded " + d.Key,
                  N(r.W.Value) + "x" + N(r.H.Value) + " frames=" + n2);

            /* 第三层 → 第二层 → 第一层 */
            r.Aim(d.CompW, d.CompH);
            r.RunToSettle(false, 300);
            r.Aim(Scenes.IdleW, Scenes.IdleH);
            r.RunToSettle(false, 300);
            Check(Math.Abs(r.W.Value - Scenes.IdleW) < 0.5 && Math.Abs(r.H.Value - Scenes.IdleH) < 0.5,
                  "settle idle " + d.Key, N(r.W.Value) + "x" + N(r.H.Value));

            /* 整个过程中锚定边缘不许动：横向岛心、纵向岛顶。
               这两条线一动，用户看到的就是「岛整体挪了一下」，而不是「长出来」。 */
            bool anchored = true;
            double worstCx = 0, worstTop = 0, worstSym = 0;
            for (int i = 0; i < r.Ws.Count; i++)
            {
                Rect b = IslandFrame.Island(ax, at, r.Ws[i], r.Hs[i]);
                double cx = b.Left + b.Width / 2;
                double sym = (ax - b.Left) - (b.Right - ax);
                worstCx = Math.Max(worstCx, Math.Abs(cx - ax));
                worstTop = Math.Max(worstTop, Math.Abs(b.Top - at));
                worstSym = Math.Max(worstSym, Math.Abs(sym));
                if (Math.Abs(cx - ax) > 1e-9 || Math.Abs(b.Top - at) > 1e-9 || Math.Abs(sym) > 1e-9)
                    anchored = false;
            }
            Check(anchored, "anchor held " + d.Key,
                  "frames=" + r.Ws.Count + " maxCx=" + worstCx.ToString("E1")
                  + " maxTop=" + worstTop.ToString("E1") + " maxSym=" + worstSym.ToString("E1"));

            /* 连续：每一帧的位移都不超过解析上界（跳变会立刻超） */
            Check(r.Jumps == 0, "no jump " + d.Key,
                  "frames=" + r.Ws.Count + " worst=" + N(r.WorstRatio) + "x bound  " + r.WorstAt);

            /* 回弹幅度：ζ≈0.76 会有约 1% 的过冲（既有手感，未改）。
               这里设 3% 的上限 —— 挡住「为了更弹而偷偷调软」这种回归。 */
            double maxW = 0;
            for (int i = 0; i < r.Ws.Count; i++) maxW = Math.Max(maxW, r.Ws[i]);
            double overPct = (maxW - d.ExpW) / d.ExpW * 100.0;
            Check(overPct < 3.0, "overshoot bounded " + d.Key,
                  "maxW=" + N(maxW) + " (+" + N(overPct) + "%)");
        }
    }

    /* 2. 快速反向切换 */
    static void RapidReverse()
    {
        Console.WriteLine("-- rapid reverse --");
        SceneDef d = Scenes.Get("media");
        Rig r = new Rig(Scenes.IdleW, Scenes.IdleH);

        /* 展开到一半（6 帧 ≈ 100ms）就改收回 */
        r.Aim(d.ExpW, d.ExpH);
        r.Step(6, true);
        double turnW = r.W.Value;
        Info("turn#1 at w=" + N(turnW) + " v=" + N(r.W.Velocity)
             + " (was -> " + d.ExpW + ", now -> " + d.CompW + ")");

        /* Aim 只改 From/Target，绝不碰当前值 —— 这是「不跳回起点」的精确保证 */
        double beforeAim = r.W.Value;
        r.Aim(d.CompW, d.CompH);
        Check(r.W.Value == beforeAim, "aim keeps current value",
              "before=" + N(beforeAim) + " after=" + N(r.W.Value));

        /* 改向之后速度会把岛再往外带一小段（这是对的：不许有方向突变），
           但必须在几帧内掉头，且整段不许出现跳变。 */
        r.Step(1, false);
        double afterTurn = r.W.Value;
        Info("first frame after reverse: " + N(turnW) + " -> " + N(afterTurn)
             + " v=" + N(r.W.Velocity));

        /* 收回途中再展开（第二次反向）—— 这正是「快速连点」的样子 */
        r.Step(4, false);
        double turn2 = r.W.Value;
        r.Aim(d.ExpW, d.ExpH);
        r.Step(1, true);
        Info("turn#2 at w=" + N(turn2) + " v=" + N(r.W.Velocity));
        Check(r.Jumps == 0, "no jump through reversals",
              "frames=" + r.Ws.Count + " jumps=" + r.Jumps + " worst=" + N(r.WorstRatio) + "x  " + r.WorstAt);

        /* 三次来回之后必须收在最终目标上，不残留旧状态 */
        int n = r.RunToSettle(true, 400);
        Check(Math.Abs(r.W.Value - d.ExpW) < 0.5, "settles after reversals",
              N(r.W.Value) + " frames=" + n);
        Check(r.Jumps == 0, "still no jump while settling", "worst=" + N(r.WorstRatio) + "x  " + r.WorstAt);

        /* 反向序列结束后 From/Target 必须收敛 —— 残留的旧 Target 会让
           下一帧的 InBand/耗时判定错乱。 */
        Check(Math.Abs(r.W.Target - r.W.Value) < 0.5 && Math.Abs(r.H.Target - r.H.Value) < 0.5,
              "targets consistent", "w " + N(r.W.Value) + "/" + N(r.W.Target)
              + " h " + N(r.H.Value) + "/" + N(r.H.Target));

        /* 收回到第一层：再从展开态反向收回，确认反方向一样干净 */
        Rig r2 = new Rig(d.ExpW, d.ExpH);
        r2.Aim(d.CompW, d.CompH);
        r2.Step(4, false);
        r2.Aim(Scenes.IdleW, Scenes.IdleH);
        r2.Step(3, false);
        r2.Aim(d.ExpW, d.ExpH);
        r2.RunToSettle(true, 400);
        Check(r2.Jumps == 0 && Math.Abs(r2.W.Value - d.ExpW) < 0.5,
              "reverse while collapsing to idle",
              "w=" + N(r2.W.Value) + " jumps=" + r2.Jumps + " worst=" + N(r2.WorstRatio) + "x");
    }

    /* 3. 内容面的交叉淡入淡出 */
    static void FaceCrossFade()
    {
        Console.WriteLine("-- face cross-fade --");

        int made = 0;
        FaceBook fb = new FaceBook();

        fb.Show("A", delegate { made++; return "payloadA"; }, false);
        Check(made == 1 && fb.Front != null && fb.Front.Key == "A" && fb.Count == 1,
              "first face built", "made=" + made + " count=" + fb.Count);
        Check(fb.Front.Opacity == 0, "starts invisible", "op=" + N(fb.Front.Opacity));

        /* 延迟期内不亮：这段时间是留给主体先长出来的 */
        double t = 0;
        while (t < MorphTuning.FaceInDelay - DT)
        {
            fb.Step(DT); t += DT;
        }
        Check(fb.Front.Opacity == 0, "waits for delay",
              "t=" + N(t) + " op=" + N(fb.Front.Opacity) + " delay=" + MorphTuning.FaceInDelay);

        /* 延迟之后单调变亮，不过冲 */
        double prev = fb.Front.Opacity, maxOp = 0;
        bool monotone = true;
        for (int i = 0; i < 90; i++)
        {
            fb.Step(DT);
            double op = fb.Front.Opacity;
            if (op < prev - 1e-6) monotone = false;
            prev = op;
            maxOp = Math.Max(maxOp, op);
        }
        Check(monotone, "fade in monotone", "maxOp=" + N(maxOp));
        Check(maxOp <= 1.0001, "fade in no overshoot", N(maxOp));
        Check(Math.Abs(fb.Front.Opacity - 1) < 0.01, "fade in completes", N(fb.Front.Opacity));
        Check(!fb.Busy, "idle after fade in", "busy=" + fb.Busy);

        /* 换内容：新脸从 0 起，旧脸开始退场 —— 两张同时在树上 */
        fb.Show("B", delegate { made++; return "payloadB"; }, false);
        Check(made == 2 && fb.Count == 2, "swap keeps two faces", "made=" + made + " count=" + fb.Count);
        MorphFace a = fb.Find("A"), b = fb.Find("B");
        Check(a != null && a.Leaving, "old face leaving", a == null ? "missing" : "leaving=" + a.Leaving);
        Check(b != null && !b.Leaving && b.Opacity == 0, "new face starts at 0",
              b == null ? "missing" : "op=" + N(b.Opacity));

        /* 交接中段：两张都半透明（这就是「不是先没了再有」） */
        for (int i = 0; i < 6; i++) fb.Step(DT);
        double midOld = a.Opacity, midNew = b.Opacity;
        Check(midOld > 0 && midOld < 1 && midNew >= 0 && midNew < 1,
              "mid cross-fade both partial", "old=" + N(midOld) + " new=" + N(midNew));

        /* 退完：旧脸交回调用方，树上只剩一张 */
        for (int i = 0; i < 120; i++) fb.Step(DT);
        Check(fb.Count == 1 && fb.Front.Key == "B", "old face retired",
              "count=" + fb.Count + " front=" + (fb.Front == null ? "null" : fb.Front.Key));
        Check(fb.Dead.Count >= 1 && fb.Dead[0].Key == "A", "dead face reported",
              "dead=" + fb.Dead.Count);
        Check(Math.Abs(fb.Front.Opacity - 1) < 0.01 && Math.Abs(fb.Front.Offset) < 0.1,
              "new face fully in", "op=" + N(fb.Front.Opacity) + " off=" + N(fb.Front.Offset));
        fb.Dead.Clear();

        /* 同 key 再来一次：既不重建，也不重新淡入 */
        int before = made;
        double opBefore = fb.Front.Opacity;
        fb.Show("B", delegate { made++; return "payloadB2"; }, false);
        Check(made == before && Math.Abs(fb.Front.Opacity - opBefore) < 1e-9 && fb.Count == 1,
              "same key is a no-op", "made=" + made + " count=" + fb.Count);

        /* immediate：直接到位，不留过渡 */
        fb.Show("C", delegate { made++; return "payloadC"; }, true);
        Check(fb.Count == 1 && Math.Abs(fb.Front.Opacity - 1) < 1e-9 && Math.Abs(fb.Front.Offset) < 1e-9,
              "immediate snaps", "count=" + fb.Count + " op=" + N(fb.Front.Opacity));

        /* 连续快速切四张：场上数量受控（退到看不见的才摘掉） */
        bool capped = true;
        int worst = 0;
        string[] keys = new string[] { "A", "B", "C", "D", "E" };
        for (int i = 0; i < keys.Length; i++)
        {
            fb.Show(keys[i], delegate { made++; return "p"; }, false);
            for (int k = 0; k < 3; k++) fb.Step(DT);
            worst = Math.Max(worst, fb.Count);
            if (fb.Count > MorphTuning.MaxFaces) capped = false;
        }
        Check(capped, "face count capped", "worst=" + worst + " cap=" + MorphTuning.MaxFaces);
    }

    /* 4. 折返复用：A→B→A 不重建、不重新淡入 */
    static void FaceReverseReuse()
    {
        Console.WriteLine("-- face reverse (A -> B -> A) --");

        int made = 0;
        Func<object> mk = delegate { made++; return "p"; };
        FaceBook fb = new FaceBook();

        fb.Show("A", mk, false);
        for (int i = 0; i < 90; i++) fb.Step(DT);
        Check(Math.Abs(fb.Front.Opacity - 1) < 0.01, "A in", N(fb.Front.Opacity));

        /* 切到 B，等 A 退到一半 */
        fb.Show("B", mk, false);
        for (int i = 0; i < 4; i++) fb.Step(DT);
        MorphFace a = fb.Find("A");
        double aAtTurn = a.Opacity;
        double bAtTurn = fb.Front.Opacity;
        Info("turn at A.op=" + N(aAtTurn) + " B.op=" + N(bAtTurn) + " made=" + made);
        Check(aAtTurn > 0 && aAtTurn < 1, "A still partly visible", N(aAtTurn));

        int madeBefore = made;

        /* 折返：目标又是 A */
        fb.Show("A", mk, false);
        Check(made == madeBefore, "reverse reuses, no rebuild", "made=" + made + "/" + madeBefore);
        Check(fb.Front != null && fb.Front.Key == "A", "A is front again",
              fb.Front == null ? "null" : fb.Front.Key);
        Check(fb.Count == 2, "both faces stay", "count=" + fb.Count);
        Check(fb.Front.Opacity >= aAtTurn - 1e-9,
              "reverse keeps opacity", "at turn=" + N(aAtTurn) + " now=" + N(fb.Front.Opacity));
        Check(fb.Front.Delay <= 0, "reverse does not re-delay", N(fb.Front.Delay));

        /* 折返之后：速度是带着走的，所以可见度会先顺着原方向再沉一点才回头
           （这正是「没有方向突变」的代价）。要求：只掉头一次、沉得很浅、
           掉头很快，最后到 1 —— 掉头两次以上就说明有人在中间动了手脚。 */
        double minOp = fb.Front.Opacity;
        double last = fb.Front.Opacity;
        int dir = 0, turns = 0, fallFrames = 0;
        for (int i = 0; i < 120; i++)
        {
            fb.Step(DT);
            double op = fb.Front.Opacity;
            minOp = Math.Min(minOp, op);
            int d = op > last + 1e-9 ? 1 : (op < last - 1e-9 ? -1 : dir);
            if (d != 0 && dir != 0 && d != dir) turns++;
            if (d != 0) dir = d;
            if (d < 0) fallFrames++;
            last = op;
        }
        Check(minOp >= aAtTurn - 0.15, "reverse dip is shallow",
              "turn=" + N(aAtTurn) + " min=" + N(minOp) + " dip=" + N(aAtTurn - minOp));
        Check(turns == 1, "reverse turns around exactly once",
              "turns=" + turns + " fell for " + fallFrames + " frames ("
              + Math.Round(fallFrames * DT * 1000) + "ms)");
        Check(fb.Count == 1 && fb.Front.Key == "A", "B retired", "count=" + fb.Count);
        Check(Math.Abs(fb.Front.Opacity - 1) < 0.01, "A fully in", N(fb.Front.Opacity));
        Check(!fb.Busy, "idle at end", "busy=" + fb.Busy);
    }

    /* 5. 内容只做位移/透明度，绝不缩放 */
    static void FaceVisualNoScale()
    {
        Console.WriteLine("-- face visual: translate only --");

        MorphFace f = new MorphFace("k", null);
        Canvas el = new Canvas();

        MorphFaceVisual.Apply(el, f, true);
        Check(el.RenderTransform is TranslateTransform, "transform is translate",
              el.RenderTransform == null ? "null" : el.RenderTransform.GetType().Name);
        TranslateTransform tt = el.RenderTransform as TranslateTransform;
        Check(tt != null && Math.Abs(tt.X) < 1e-9, "translate X stays 0", tt == null ? "" : N(tt.X));
        Check(el.Opacity <= 1e-9, "opacity applied", N(el.Opacity));

        /* 位移的上下限：进场 8px、退场 5px，任何时刻都不该跑出去。
           跑出去说明有人给它塞了别的动画（缩放/位移混用）。 */
        f.Enter(false, true);
        double maxAbs = 0;
        for (int i = 0; i < 120; i++)
        {
            f.Step(DT);
            MorphFaceVisual.Apply(el, f, true);
            maxAbs = Math.Max(maxAbs, Math.Abs(tt.Y));
        }
        Check(maxAbs <= MorphTuning.FaceInRise + 0.01, "offset within rise budget",
              "maxAbs=" + N(maxAbs) + " budget=" + MorphTuning.FaceInRise);
        Check(Math.Abs(tt.Y) < 0.1, "offset returns to 0", N(tt.Y));

        /* 退场那张不吃点击（旧卡片上的按钮不能被点到） */
        f.Leave();
        MorphFaceVisual.Apply(el, f, false);
        Check(!el.IsHitTestVisible, "leaving face not hit-testable", "");

        /* 淡完就不画 */
        for (int i = 0; i < 120; i++) f.Step(DT);
        MorphFaceVisual.Apply(el, f, false);
        Check(el.Visibility == Visibility.Hidden, "invisible when faded out",
              el.Visibility + " op=" + N(el.Opacity));
    }

    /* 6. 窗口尺寸 */
    static void WindowSizing()
    {
        Console.WriteLine("-- window sizing --");

        const double MX = 24, MT = 16, MB = 34;

        foreach (SceneDef d in Scenes.All)
        {
            double w0 = IslandFrame.WindowW(d.ExpW, MX, 0, 0);
            double h0 = IslandFrame.WindowH(d.ExpH, MT, MB);
            Check(Math.Abs(w0 - (d.ExpW + 48)) < 1e-9 && Math.Abs(h0 - (d.ExpH + 50)) < 1e-9,
                  "window " + d.Key, N(w0) + "x" + N(h0));

            /* 岛体在任何一层都必须放得进窗口（放不进就会被窗口边缘切掉） */
            Check(d.ExpW <= w0 + 1e-9 && d.ExpH <= h0 + 1e-9 && d.CompW <= w0 && d.CompH <= h0
                  && Scenes.IdleW <= w0 && Scenes.IdleH <= h0,
                  "island fits " + d.Key, "");

            /* 预留只在功能开着时加：关着就必须和改动前逐像素一致 */
            Check(Math.Abs(IslandFrame.WindowW(d.ExpW, MX, 60, 0) - (w0 + 60)) < 1e-9,
                  "notify reserve " + d.Key, "");
            Check(Math.Abs(IslandFrame.WindowW(d.ExpW, MX, 0, 47.6) - (w0 + 47.6)) < 1e-9,
                  "pet reserve " + d.Key, "");
            Check(Math.Abs(IslandFrame.WindowW(d.ExpW, MX, 60, 47.6) - (w0 + 107.6)) < 1e-9,
                  "both reserves " + d.Key, "");
        }

        /* 媒体页的具体数字（原来是硬写在 SyncWindowSize 里的） */
        Check(Math.Abs(IslandFrame.WindowW(452, MX, 0, 0) - 500) < 1e-9
              && Math.Abs(IslandFrame.WindowH(206, MT, MB) - 256) < 1e-9,
              "media 500x256", "");
    }

    /* 7. 桌宠位置 */
    static void PetTimeline()
    {
        Console.WriteLine("-- pet timeline --");

        const double d1 = 16.8, d2 = 33.6, gap = 8, fade = 0.10, run = 0.34;
        const double iw = 52, ih = 30, ix = 71.6, iy = 16;
        double winW = 500;
        double total = fade + run;

        PetPose p0 = PetLink.Sample(0, fade, run, iw, ih, ix, iy, winW, d1, d2, gap);
        Check(p0.InDock && Math.Abs(p0.Dia - d1) < 1e-9 && Math.Abs(p0.Opacity - 1) < 1e-9,
              "t0 in dock", "dia=" + N(p0.Dia) + " op=" + N(p0.Opacity));
        Check(Math.Abs(p0.Cx - (ix + iw / 2)) < 1e-9 && Math.Abs(p0.Cy - (iy + ih / 2)) < 1e-9,
              "t0 at island centre", N(p0.Cx) + "," + N(p0.Cy));

        /* 交接点：直径与球心必须连续（换槽位那一帧不许跳） */
        PetPose before = PetLink.Sample(fade - 1e-6, fade, run, iw, ih, ix, iy, winW, d1, d2, gap);
        PetPose at = PetLink.Sample(fade, fade, run, iw, ih, ix, iy, winW, d1, d2, gap);
        Check(before.InDock && !at.InDock, "handoff switches slot", "");
        Check(Math.Abs(before.Dia - at.Dia) < 1e-3, "handoff dia continuous",
              N(before.Dia) + " -> " + N(at.Dia));
        Check(Math.Abs(before.Cx - at.Cx) < 1e-3 && Math.Abs(before.Cy - at.Cy) < 1e-3,
              "handoff centre continuous", N(before.Cx) + " vs " + N(at.Cx));

        /* 整条时间线：采样密集一点，逐点查连续性 */
        int steps = 400;
        double prevDia = 0, prevCx = 0;
        double maxDd = 0, maxDc = 0;
        bool monotoneCx = true;
        for (int i = 0; i <= steps; i++)
        {
            double t = total * i / steps;
            PetPose p = PetLink.Sample(t, fade, run, iw, ih, ix, iy, winW, d1, d2, gap);
            if (i > 0)
            {
                maxDd = Math.Max(maxDd, Math.Abs(p.Dia - prevDia));
                maxDc = Math.Max(maxDc, Math.Abs(p.Cx - prevCx));
                if (p.Cx > prevCx + 1e-9) monotoneCx = false;
            }
            prevDia = p.Dia; prevCx = p.Cx;
        }
        Check(maxDd < 1.0, "dia continuous", "maxStep=" + N(maxDd) + " over " + steps + " samples");
        Check(maxDc < 3.0, "centre continuous", "maxStep=" + N(maxDc));
        Check(monotoneCx, "moves outward only (left side)", "");

        /* 终点：停在岛旁，正好隔一个 gap，球长到两倍 */
        PetPose end = PetLink.Sample(total, fade, run, iw, ih, ix, iy, winW, d1, d2, gap);
        Check(Math.Abs(end.Dia - d2) < 1e-6, "ends at 2x", N(end.Dia));
        Check(Math.Abs(end.Cx - (ix - gap - d2 / 2)) < 1e-6, "ends at gap from island",
              N(end.Cx) + " want " + N(ix - gap - d2 / 2));
        Check(Math.Abs(end.Cy - (iy + ih / 2)) < 1e-6, "ends on island vertical centre", N(end.Cy));
        Check(end.Side < 0, "defaults to left", N(end.Side));

        /* 岛贴左缘：左边放不下 → 换到右边，且落点仍隔一个 gap */
        PetPose right = PetLink.Sample(total, fade, run, iw, ih, 4, iy, winW, d1, d2, gap);
        Check(right.Side > 0, "flips to right when cramped", N(right.Side));
        Check(Math.Abs(right.Cx - (4 + iw + gap + d2 / 2)) < 1e-6, "right rest position",
              N(right.Cx) + " want " + N(4 + iw + gap + d2 / 2));

        /* 两侧都放不下：挑宽的一侧，不许返回 0 或 NaN */
        PetPose tight = PetLink.Sample(total, fade, run, 400, 200, 2, iy, 420, d1, d2, gap);
        Check((tight.Side > 0 || tight.Side < 0) && !double.IsNaN(tight.Cx),
              "tight window still picks a side", "side=" + N(tight.Side) + " cx=" + N(tight.Cx));

        /* 收回：t 回到 0 必须是原球的位置与直径（不许留下上一次的放大态） */
        PetPose back = PetLink.Sample(0, fade, run, iw, ih, ix, iy, winW, d1, d2, gap);
        Check(Math.Abs(back.Dia - d1) < 1e-9 && Math.Abs(back.Cx - (ix + iw / 2)) < 1e-9,
              "collapse returns to orb", N(back.Dia) + " @ " + N(back.Cx));

        /* 岛尺寸在动（形变过程中）：落点必须跟着岛边一起走，不能停在旧位置 */
        PetPose mid = PetLink.Sample(total, fade, run, 200, 120, ix, iy, winW, d1, d2, gap);
        Check(Math.Abs(mid.Cx - (ix - gap - d2 / 2)) < 1e-6,
              "rest follows island width", N(mid.Cx));
    }
}