using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace WinIsland
{
    /* ==========================================================================
       形变面 —— 从 FluidKit 的 MorphSurface 移植过来的设计骨架
       ==========================================================================

       上游：https://github.com/runvendo/fluidkit （MIT）
             src/components/MorphSurface.tsx 、docs/primitives/morph-surface.md

       上游那句话是整块设计的根：**animate the surface, never the text**
       —— 形变只发生在「面」上，字形和图标一个像素都不许被拉伸。它靠三条具体
       机制做到，这三条就是本文件搬过来的东西：

         1. 锚定边缘的连续形变（anchor: "center" | "top"）
            主体尺寸是弹簧驱动的连续量，形变时**锚定的一对边不动**，另一对往外长。
            上游 anchorTop 时把顶边钉在 openTop（面板从胶囊里「倒」出来），
            center 时绕中心就地胀开。
            本项目原先的锚是隐式的（散在 SyncPlacement 里）：横向岛心、纵向岛顶。
            这里把它变成一个具名函数（IslandFrame.Island），于是「形变时哪条边不动」
            可以被断言，而不是只能靠肉眼。

         2. 主体与附属元素分别用合适的弹簧（bodySpring / satelliteSpring）
            上游主体 240/24，卫星 150/14 —— 卫星比主体软，所以它「慢半拍、落地时
            再晃一下」。本项目的主体弹簧（250/24，收起时 437.5/38.6）是既有的、
            逐位对过网页版的参数，**不动**；只给内容面配一套更软的新参数
            （见 MorphTuning.FaceInK）。两者各自由各自的 Spring 推进。

         3. 内容通过位置/透明度过渡，而不是被拉伸
            上游两张面始终在树上，只改 opacity 与 translateY：
              进场  opacity .28s ease .12s / transform .34s cubic-bezier(.22,1,.36,1) .12s
              退场  opacity .12s ease   / transform .12s ease（不等）
            本文件用同一套语义 + 现成的 Spring 落地（FaceBook / MorphFace），
            数值换成弹簧常数，于是「中途改向」自动从当前值继续 —— CSS 过渡在
            retarget 时也是这个行为，这也是上游敢直接翻转 open 的原因。

       没有搬的东西（也不该搬）：
         · 液态材质、张力桥、卫星水滴的几何 —— 本项目没有那套渲染管线；
         · motion/react 的动画引擎 —— 本项目有 Spring.cs，按要求复用；
         · clipContent 的 DOM 量测 —— WPF 这边裁剪由 _host.Clip 负责。

       许可：实质移植（结构、调参语义、退场比进场快的节拍、锚定边缘的定义）。
       上游为 MIT，完整许可与署名见仓库根目录 THIRD-PARTY-NOTICES.md。
       ========================================================================== */

    /// <summary>
    /// 形变调参。
    ///
    /// 主体那一套不在这里 —— 它是 IslandWindow 原本就有的 K/C（250/24，
    /// 收起时 ×1.75/×1.38），逐位保持不动，只在 MorphBody 里被引用。
    /// </summary>
    static class MorphTuning
    {
        /* 内容面（附属元素）的弹簧。

           为什么比主体软：主体是「形状」，要利落；内容面是跟着形状进来的字和图标，
           软一点才不像被甩出来。ζ = c / (2√k)：150/26 → 1.06，刚好不过冲 ——
           文字过冲会抖，看着像渲染坏了。 */
        public const double FaceInK = 150, FaceInC = 26;

        /* 退场比进场快（对应上游 0.12s vs 0.28s）：旧内容要在新内容站住之前让开，
           但不能和主体抢戏。340/37 → ζ = 1.003，同样不过冲。 */
        public const double FaceOutK = 340, FaceOutC = 37;

        /* 进场延迟：等主体先长出一截再把内容放上来。
           上游是 .12s；本项目的形变约 330ms 到位，90ms 落在「主体走过约 1/3」
           的位置 —— 再短内容会在还没长够的形状里露头，再长就能看见一个空壳。 */
        public const double FaceInDelay = 0.090;

        /* 进场时从 +8px 落到 0；退场时落到 +5px（往下沉一点再消失）。
           只改 Y，不缩放 —— 文字/图标永远不会被非等比拉伸。 */
        public const double FaceInRise = 8, FaceOutDrop = 5;

        /* 判定「这一帧还在不在动」。Spring.Settled 的阈值（0.35 / 12）是给像素量级
           设计的，拿来判 0..1 的透明度会在 65% 就判到位（这个坑 IslandWindow 在
           消息小球那里已经踩过），所以可见度和位移各有一套自己的阈值。 */
        public const double FadeEps = 0.0015, FadeVelEps = 0.02;
        public const double RiseEps = 0.05, RiseVelEps = 0.5;

        /* 场上最多留几张脸：当前那张 + 两张正在退场的。
           第三张是为了「A→B→C 快速连切」还能看见 C 从 B 手里接过去；
           再多没必要 —— 更早的那些已经淡到看不见了，留着只是白占渲染。 */
        public const int MaxFaces = 3;

        public static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }
    }

    /// <summary>
    /// 主体形变：两个尺寸弹簧 + 锚定边缘。
    ///
    /// 弹簧本身是 Squircle.cs 里那个 Spring —— 本类只负责「怎么推」和「锚在哪」，
    /// 不另写积分器。之所以要包一层：形变参数（扩张用 K/C、收起用更硬的
    /// K·1.75/C·1.38，纵向略快 6%）原本埋在 OnFrame 里，埋在帧回调里的东西
    /// 没法在窗口之外验证。搬到这里之后，测试程序可以用同一份代码跑一遍
    /// 「连续切层 / 中途反向」，断言的东西和产品跑的是同一套。
    /// </summary>
    class MorphBody
    {
        /* 与网页版同参（原 IslandWindow.K/C，逐位未改） */
        public const double K = 250, C = 24, CollapseK = 1.75, CollapseC = 1.38;

        readonly Spring _w, _h;

        public MorphBody(Spring w, Spring h)
        {
            if (w == null || h == null) throw new ArgumentNullException("spring");
            _w = w;
            _h = h;
        }

        public Spring W { get { return _w; } }
        public Spring H { get { return _h; } }

        public double Width { get { return _w.Value; } }
        public double Height { get { return _h.Value; } }

        /// <summary>改目标。Spring.Aim 保留当前值与速度 —— 这就是「中途改向不跳」。</summary>
        public void Aim(double w, double h)
        {
            _w.Aim(w);
            _h.Aim(h);
        }

        /// <summary>直接落位（启动、换场景、自检的 immediate 路径）。</summary>
        public void Snap()
        {
            _w.Snap();
            _h.Snap();
        }

        /// <summary>
        /// 推一帧。expansion 决定用哪一组刚度：收起比展开硬（快了会显得甩），
        /// 纵向略快于横向（原是 k*1.06 / c*1.02）。
        /// </summary>
        public void Step(double dt, bool expanding)
        {
            if (dt < 0) dt = 0;
            double k = expanding ? K : K * CollapseK;
            double c = expanding ? C : C * CollapseC;

            if (!_w.Settled) _w.Step(dt, k, c);
            if (!_h.Settled) _h.Step(dt, k * 1.06, c * 1.02);

            if (_w.Settled) _w.Snap();
            if (_h.Settled) _h.Snap();
        }

        public bool Settled { get { return _w.Settled && _h.Settled; } }

        /// <summary>
        /// 「还在带子里」—— 与区间 1% 或 0.5px 以内算到位。
        /// 用于 morph 耗时的诊断（lastAbove），和 Settled 是两件事：
        /// Settled 判「可以停帧了」，InBand 判「这条动画还算不算在跑」。
        /// </summary>
        public bool InBand
        {
            get { return InBandOf(_w) && InBandOf(_h); }
        }

        public static bool InBandOf(Spring s)
        {
            double d = Math.Abs(s.Target - s.From);
            if (d < 0.5) return true;
            return Math.Abs(s.Value - s.Target) <= Math.Max(0.5, d * 0.01);
        }
    }

    /// <summary>
    /// 窗口与岛体的落点计算。
    ///
    /// 存在的理由同 MorphBody：这些公式原先散在 SyncWindowSize / SyncPlacement 里，
    /// 改一行没人拦得住，也没法在窗口之外量。抽成纯函数之后，验证程序能直接断言
    /// 「换场景后窗口该多宽」「形变时岛心不动、岛顶不动」。
    /// 数值与搬运前逐位相同 —— 这里不引入任何新行为。
    /// </summary>
    static class IslandFrame
    {
        /// <summary>
        /// 岛体矩形。**锚定边缘就在这里**：横向绕岛心对称（左 = 岛心 − w/2），
        /// 纵向钉住岛顶。所以形变时「岛心」和「岛顶」两条线不动，另外两条往外长。
        /// </summary>
        public static Rect Island(double anchorX, double anchorTop, double w, double h)
        {
            double ww = Math.Max(2, w);
            double hh = Math.Max(2, h);
            return new Rect(anchorX - ww / 2.0, anchorTop, ww, hh);
        }

        /// <summary>
        /// 窗口宽度 = 展开宽 + 两侧透明留白 + 可选的两块预留。
        /// notifyReserve 在右侧（消息小球），petReserve 在左侧（桌宠出场位）。
        /// 预留只在对应功能真开着时才加进来，所以关着的时候窗口与改动前逐像素一致。
        /// </summary>
        public static double WindowW(double expW, double mx, double notifyReserve, double petReserve)
        {
            return expW + mx * 2 + notifyReserve + petReserve;
        }

        /// <summary>窗口高度 = 展开高 + 上留白 + 下留白（下留白给落影）。</summary>
        public static double WindowH(double expH, double mt, double mb)
        {
            return expH + mt + mb;
        }
    }

    /// <summary>
    /// 一张「内容面」。
    ///
    /// Payload 是调用方的可视子树（本项目里是 Built），这一层刻意不认识内容类型
    /// —— 于是验证程序可以塞任意占位对象进来驱动同一条时间线。
    ///
    /// 可见度与位移共用现成的 Spring：Aim() 保留当前值与速度，所以
    /// 「进到一半改为退场」「退到一半又翻回来」都从当前实际状态接着走。
    /// </summary>
    class MorphFace
    {
        public readonly string Key;
        public object Payload;

        public readonly Spring Fade;   /* 0..1 可见度 */
        public readonly Spring Rise;   /* Y 位移（px） */

        /// <summary>进场前的等待（秒）。只在「从无到有」时生效，退场与折返都不等。</summary>
        public double Delay;

        /// <summary>正在退场。决定用哪一组刚度。</summary>
        public bool Leaving;

        public MorphFace(string key, object payload)
        {
            Key = key;
            Payload = payload;
            Fade = new Spring(0);
            Rise = new Spring(MorphTuning.FaceInRise);
        }

        public double Opacity { get { return MorphTuning.Clamp01(Fade.Value); } }
        public double Offset { get { return Rise.Value; } }

        /// <summary>进场。withDelay=false 用于折返 —— 已经在场上的脸不该再等一次。</summary>
        public void Enter(bool immediate, bool withDelay)
        {
            Leaving = false;
            if (immediate)
            {
                Fade.Target = 1; Fade.Snap();
                Rise.Target = 0; Rise.Snap();
                Delay = 0;
                return;
            }
            Delay = withDelay ? MorphTuning.FaceInDelay : 0;
            Fade.Aim(1);
            Rise.Aim(0);
        }

        /// <summary>退场。不等延迟，落到 +FaceOutDrop。</summary>
        public void Leave()
        {
            Leaving = true;
            Delay = 0;
            Fade.Aim(0);
            Rise.Aim(MorphTuning.FaceOutDrop);
        }

        public void Step(double dt)
        {
            if (dt <= 0) return;

            if (Delay > 0)
            {
                Delay -= dt;
                if (Delay > 0) return;
            }

            double k = Leaving ? MorphTuning.FaceOutK : MorphTuning.FaceInK;
            double c = Leaving ? MorphTuning.FaceOutC : MorphTuning.FaceInC;
            Fade.Step(dt, k, c);
            Rise.Step(dt, k, c);

            Settle(Fade, MorphTuning.FadeEps, MorphTuning.FadeVelEps);
            Settle(Rise, MorphTuning.RiseEps, MorphTuning.RiseVelEps);
        }

        static void Settle(Spring s, double eps, double velEps)
        {
            if (Math.Abs(s.Value - s.Target) <= eps && Math.Abs(s.Velocity) <= velEps) s.Snap();
        }

        /// <summary>这条时间线已经走完，可以不再为它订阅帧回调。</summary>
        public bool Idle
        {
            get
            {
                if (Delay > 0) return false;
                return Math.Abs(Fade.Value - Fade.Target) <= MorphTuning.FadeEps
                    && Math.Abs(Fade.Velocity) <= MorphTuning.FadeVelEps
                    && Math.Abs(Rise.Value - Rise.Target) <= MorphTuning.RiseEps
                    && Math.Abs(Rise.Velocity) <= MorphTuning.RiseVelEps;
            }
        }
    }

    /// <summary>
    /// 几张内容面的交叉淡入淡出簿记。
    ///
    /// 换掉了原来的做法：`_content.Children.Clear()` + `Opacity=0` + 一条新的
    /// WPF DoubleAnimation。那套写法在**切层这一件事**上有三个硬伤：
    ///   · 每次切层都从空白淡入 —— 锚点定在「全透明」，中途再切一次就是从
    ///     当前画面跳回全透明重来，也就是会闪；
    ///   · 每次重建都换一个新 TranslateTransform，旧位移被当场丢弃 ——
    ///     6px 的起跳会重来一次，看着像抖一下；
    ///   · 新旧内容之间没有交接，只有「先没了再有」。
    ///
    /// 现在：所有在场上的脸各自有一条时间线，切层只是「改谁的目标」。
    ///   · 快速连切：新脸从当前画面接过去，不经过全透明；
    ///   · 折返（A→B→A）：直接复用还在退场的 A，不重建、不重新淡入；
    ///   · 退完的脸立刻从 Dead 交回调用方摘掉 —— 树上不留残影。
    /// </summary>
    class FaceBook
    {
        readonly List<MorphFace> _faces = new List<MorphFace>();
        readonly List<MorphFace> _dead = new List<MorphFace>();

        /// <summary>正在显示（或正在进场）的那张脸。</summary>
        public MorphFace Front;

        /// <summary>可以摘掉的脸。Show/Step 只往这里放，摘树是调用方的事。</summary>
        public List<MorphFace> Dead { get { return _dead; } }

        /// <summary>场上现有的脸（含正在退场的）。顺序即挂树顺序。</summary>
        public List<MorphFace> Faces { get { return _faces; } }

        public int Count { get { return _faces.Count; } }

        public MorphFace Find(string key)
        {
            for (int i = 0; i < _faces.Count; i++) if (_faces[i].Key == key) return _faces[i];
            return null;
        }

        /// <summary>
        /// 把 key 那张脸切到台前。
        ///
        /// make 只在**确实要新建**的时候调用 —— 折返与同 key 都不会重建内容，
        /// 所以列表滚动位置、封面缓存、Live 更新器都留在原地。
        /// </summary>
        public void Show(string key, Func<object> make, bool immediate)
        {
            if (Front != null && Front.Key == key) return;

            MorphFace next = Find(key);
            if (next == null)
            {
                next = new MorphFace(key, make());
                next.Enter(immediate, true);
            }
            else
            {
                /* 折返：它已经在场上（正在退场），翻回来接着走，不重建、不重新计时 */
                next.Enter(immediate, false);
            }

            for (int i = _faces.Count - 1; i >= 0; i--)
            {
                MorphFace f = _faces[i];
                if (ReferenceEquals(f, next)) continue;
                if (immediate)
                {
                    _faces.RemoveAt(i);
                    _dead.Add(f);
                }
                else
                {
                    f.Leave();
                }
            }

            if (!_faces.Contains(next)) _faces.Add(next);
            Front = next;

            Trim();
        }

        public void Step(double dt)
        {
            if (dt <= 0) return;

            for (int i = _faces.Count - 1; i >= 0; i--)
            {
                MorphFace f = _faces[i];
                f.Step(dt);
                if (ReferenceEquals(f, Front)) continue;
                if (f.Idle && f.Opacity <= 0.001)
                {
                    _dead.Add(f);
                    _faces.RemoveAt(i);
                }
            }
        }

        /// <summary>还有脸在动 —— 帧回调的收尾条件要看它，否则淡入淡出会被截断。</summary>
        public bool Busy
        {
            get
            {
                for (int i = 0; i < _faces.Count; i++) if (!_faces[i].Idle) return true;
                return false;
            }
        }

        /// <summary>超编时淘汰最淡的那张（不是当前那张）。</summary>
        void Trim()
        {
            while (_faces.Count > MorphTuning.MaxFaces)
            {
                int worst = -1;
                double worstOpacity = double.MaxValue;
                for (int i = 0; i < _faces.Count; i++)
                {
                    MorphFace f = _faces[i];
                    if (ReferenceEquals(f, Front)) continue;
                    if (f.Opacity < worstOpacity) { worstOpacity = f.Opacity; worst = i; }
                }
                if (worst < 0) break;
                _dead.Add(_faces[worst]);
                _faces.RemoveAt(worst);
            }
        }

        /// <summary>清空（退出时用，避免窗口关掉之后还有闭包引用内容树）。</summary>
        public void Reset()
        {
            _dead.AddRange(_faces);
            _faces.Clear();
            Front = null;
        }
    }

    /// <summary>桌宠在某一时刻的姿态。根坐标 DIP。</summary>
    struct PetPose
    {
        public bool InDock;      /* 还在岛内那一格（淡出中） */
        public double Side;      /* -1 停左边（默认），+1 停右边 */
        public double Dia;       /* 球径 */
        public double Cx, Cy;    /* 球心 */
        public double Opacity;
    }

    /// <summary>
    /// 桌宠时间线上的摆位。
    ///
    /// 原来这段算在 PlacePet 里，和 SetDiameter/Canvas.SetLeft/裁剪刷新混在一起，
    /// 于是「换层时球会不会跳」只能靠抓像素猜。把**纯算术**抽出来之后就能断言：
    /// 两段（淡出 → 滑出）交接处球径连续、球心连续、落点正好停在岛边一个间隙处。
    /// 公式与搬运前逐位相同，只是搬了位置。
    /// </summary>
    static class PetLink
    {
        /// <summary>smoothstep：起步慢、收尾更慢。两端一阶导为 0，接上淡出那段不打勾。</summary>
        public static double Ease(double u)
        {
            if (u <= 0) return 0;
            if (u >= 1) return 1;
            return u * u * (3 - 2 * u);
        }

        public static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        /// <summary>
        /// t：时间线上的位置（0 = 岛内原球处，fadeSec+runSec = 停在岛旁）。
        /// windowW 是**窗口**宽（不是岛宽）—— 判断左右哪边放得下用的是窗口里的余量。
        /// </summary>
        public static PetPose Sample(double t, double fadeSec, double runSec,
                                     double islandW, double islandH, double ix, double iy,
                                     double windowW, double d1, double d2, double gap)
        {
            PetPose p = new PetPose();

            double w = Math.Max(2, islandW);
            double h = Math.Max(2, islandH);
            double dockCx = ix + w / 2;
            double dockCy = iy + h / 2;

            /* 停靠点：岛旁，**优先左边**。
               球从岛体左侧出来往左移。左侧放不下时（岛贴近屏幕左缘，窗口左边距
               被压没了）才退到右边；两侧都不够时取宽的一侧。 */
            double r2 = d2 / 2;
            double need = gap + d2;
            double roomRight = windowW - (ix + w);
            double roomLeft = ix;
            double side;
            if (roomLeft >= need) side = -1;
            else if (roomRight >= need) side = 1;
            else side = roomRight >= roomLeft ? 1 : -1;
            p.Side = side;

            double restCx = side > 0 ? ix + w + gap + r2 : ix - gap - r2;
            double restCy = iy + h / 2;

            /* 时间线拆两段：
                 0 → fadeSec        还在岛内那一格，只淡出、**不缩**（缩会断直径）
                 fadeSec → 总长     在岛下那一层，从岛心移到岛旁、长到两倍 */
            double fade = Clamp01(t / fadeSec);
            double run = Clamp01((t - fadeSec) / runSec);
            bool inDock = t < fadeSec;

            p.InDock = inDock;
            if (inDock)
            {
                p.Cx = dockCx;
                p.Cy = dockCy;
                p.Dia = d1;
                p.Opacity = 1.0 - fade;
            }
            else
            {
                double e = Ease(run);
                p.Cx = dockCx + (restCx - dockCx) * e;
                p.Cy = dockCy + (restCy - dockCy) * e;
                p.Dia = d1 + (d2 - d1) * e;
                p.Opacity = 1.0;
            }
            return p;
        }
    }

    /// <summary>
    /// 内容面的可视部分（WPF 侧）。
    ///
    /// 一张脸只允许两种变换：透明度、Y 位移。这里刻意只提供 TranslateTransform
    /// —— 想给内容加 ScaleTransform 得先绕过这个类，而它就是「文字不许被拉伸」
    /// 这条规则在代码里的那道门。
    /// </summary>
    static class MorphFaceVisual
    {
        public static TranslateTransform Prepare(FrameworkElement el)
        {
            TranslateTransform tt = el.RenderTransform as TranslateTransform;
            if (tt == null || tt.IsFrozen)
            {
                tt = new TranslateTransform(0, MorphTuning.FaceInRise);
                el.RenderTransform = tt;
            }
            return tt;
        }

        public static void Apply(FrameworkElement el, MorphFace face, bool front)
        {
            double op = face == null ? 0 : face.Opacity;
            el.Opacity = op;

            TranslateTransform tt = Prepare(el);
            tt.Y = face == null ? 0 : face.Offset;

            /* 完全透明的脸既不画也不吃鼠标：退场中的旧卡片上有按钮，
               让它继续参与命中就会「点到一张看不见的卡」。 */
            el.Visibility = op <= 0.0005 ? Visibility.Hidden : Visibility.Visible;
            el.IsHitTestVisible = front;
            Panel.SetZIndex(el, front ? 2 : 1);
        }
    }
}