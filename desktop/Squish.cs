using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace WinIsland
{
    /* 一个轴上的弹簧位移。

       为什么是解析解而不是逐帧积分：岛体那个 Spring 是靠 OnFrame 推的，
       而 OnFrame 的收尾条件是「全都 settled 就摘掉 Rendering」。按压回弹
       借不上那套循环 —— 按住不放时弹簧会收敛、循环停下来，之后松开就
       没人再喂时间了。自带的 CompositionTarget.Rendering 又不行：
       那是静态事件，多一个订阅方就多一处「关窗后把窗口钉住」的漏点
       （ANIMATION-PORT §10.7 记的就是这个事故）。

       所以这里走 Ui.cs 已经用了很多年的那条路：把位移算成关键帧，
       交给 WPF 的动画时钟去播（Rhythm / Wave / Breathe / Orb 全是这么做的）。
       好处一个不少：不新增帧循环、不订阅静态事件、播完自己停；
       而且轨迹是算出来的，验证时可以直接对模型断言。 */
    sealed class SquishAxis
    {
        public ScaleTransform Scale;
        public DependencyProperty Prop;

        /* 当前目标：1 = 静止；按下时是 1+amp 或它的反比。 */
        public double Target = 1.0;

        /* 这一段动画的起点状态与起始时刻（_clock 的秒数）。 */
        double _from = 1.0, _vel, _t0 = -1;

        public double Value
        {
            get
            {
                /* 动画在跑时 ScaleX/ScaleY 返回的是**当前生效值**，
                   不是基值 —— 这正是重定向时需要的「现在到底在哪」。 */
                return Prop == ScaleTransform.ScaleXProperty ? Scale.ScaleX : Scale.ScaleY;
            }
        }

        /// <summary>此刻的速度（形状单位/秒）。没在动就是 0。</summary>
        public double Velocity(double now)
        {
            if (_t0 < 0) return 0;
            double t = now - _t0;
            if (t <= 0) return _vel;
            return Squish.VelocityOf(_from, _vel, Target, t);
        }

        /// <summary>
        /// 从「现在的位置 / 现在的速度」重新指向 target，并播一段关键帧。
        /// 位置取自渲染时钟（精确），速度取自解析模型 —— 于是快速连点时
        /// 新的一段是接着上一段的形状和速度走的，不会在按下/松开之间跳一下。
        /// </summary>
        public void Aim(double target, double duration, EventHandler onDone)
        {
            double now = Squish.Now;
            double x0 = Value;
            double v0 = Velocity(now);

            Target = target;
            _from = x0;
            _vel = v0;
            _t0 = now;

            /* 基值先写成目标：动画播完（FillBehavior.Stop）会退回基值，
               基值等于目标就不会在最后一帧闪一下旧值。 */
            if (Prop == ScaleTransform.ScaleXProperty) Scale.ScaleX = target;
            else Scale.ScaleY = target;

            DoubleAnimationUsingKeyFrames tl = new DoubleAnimationUsingKeyFrames();
            tl.Duration = new Duration(TimeSpan.FromSeconds(duration));
            tl.FillBehavior = FillBehavior.Stop;

            /* 60Hz 取样：WPF 本来也是按帧取值，帧间线性插值，
               所以这串关键帧和逐帧积分得到的是同一条曲线。 */
            const int Steps = 24;
            for (int i = 0; i <= Steps; i++)
            {
                double t = duration * i / Steps;
                double v = i == Steps ? target : Squish.PositionOf(x0, v0, target, t);
                tl.KeyFrames.Add(new LinearDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
            }

            if (onDone != null) tl.Completed += onDone;
            Scale.BeginAnimation(Prop, tl);
        }

        /// <summary>立刻落到 target，不留动画。</summary>
        public void Snap(double target)
        {
            Target = target;
            Scale.BeginAnimation(Prop, null);
            if (Prop == ScaleTransform.ScaleXProperty) Scale.ScaleX = target;
            else Scale.ScaleY = target;
            _from = target; _vel = 0; _t0 = -1;
        }
    }

    /* 一次按压的形变状态：一块命中面 + 一个被缩放的视觉元素 + 两个弹簧轴。 */
    sealed class SquishTarget
    {
        public string Name;
        public FrameworkElement Surface;    /* 命中面：尺寸恒等于原元素，永不参与变形 */
        public FrameworkElement Visual;     /* 真正被缩放的元素：命中测试已关掉 */
        public SquishAxis X, Y;
        public bool Pressed;
        public double Amp;                  /* 这次按下用的横向放大比例 */
        public int Anims;                   /* 正在跑的关键帧段数，用来判断能不能摘变换 */

        /* 元素本来挂着的 RenderTransform。按下期间它被让到一边（组合进
           TransformGroup），松开时**原样还回去** —— 卡片里已经有别的层
           在自己用 RenderTransform（例如液面那一套），直接覆写就是把它吃掉了。 */
        public Transform Original;

        /* 我最后一次写上去的那个变换对象。摘的时候靠引用比对确认
           「现在这个还是我写的」，不是就说明别人接手了，不能再动。 */
        public Transform Mine;

        public bool AtRest
        {
            get
            {
                return Math.Abs(X.Target - 1.0) < 1e-9 && Math.Abs(Y.Target - 1.0) < 1e-9
                       && Anims == 0;
            }
        }
    }

    /* 命中面。单独一个类型只为一件事：Wrap 靠 `el.Parent is SquishPlate`
       判断「已经套过了」，重复调用不会叠两层。 */
    sealed class SquishPlate : Grid
    {
        public SquishTarget Target;
    }

    /// <summary>
    /// 轻量按压回弹（press-squash）：按下横向略微变宽、纵向按比例压缩，
    /// 松开由弹簧回到原状。只作用于明确可点的岛内控件。
    ///
    /// 算法移植自 FluidKit 的 useSquish（MIT）：
    ///   上游：https://github.com/runvendo/fluidkit → src/hooks/useSquish.ts
    ///   版权：Copyright (c) 2026 Yousef Hindy。许可全文见本文件末尾。
    ///
    /// 移植的是四件事：
    ///   1. 按压时横向放大 `1 + intensity`、纵向压缩成它的反比 `1 / (1 + intensity)`，
    ///      两轴乘积恒为 1 —— 看上去像一块被按扁的软物，不是整体变胖。
    ///   2. 同一组弹簧参数负责按下和松开两个方向，回弹的**超调**就是「抖那一下」
    ///      本身，不另写一段 bounce 动画（上游注释里明确说了这一点）。
    ///   3. 阻尼比 ζ = c/(2√k) = 0.64：只超调一次，不做多周期晃动。
    ///   4. 减少动态效果时按下不动、松开强制归位；归位那一半**不受**开关保护 ——
    ///      按下之后才把开关打开，也绝不能冻在压扁状态（上游同样如此）。
    ///
    /// 不移植的是：React / Motion 运行时（这边用项目自带的 ScaleTransform +
    /// WPF 动画时钟）、键盘 Space/Enter 的按下态（岛是 ShowActivated=false 的
    /// 无焦点窗口，没有键盘焦点这回事，硬接只会是一条永不触发的路径）。
    /// </summary>
    static class Squish
    {
        /* 幅度。上游默认 0.12（对一张 200px 的网页按钮是左右各 12px），
           岛里的按钮只有 20~36 DIP，照抄会抖得像坏了 —— 按「克制」取 5%。

           宽度再大也不按比例放大：一行 430 DIP 的列表行按 5% 算要左右各长 10.8px，
           那不是「略微变宽」。所以再压一条绝对上限 MaxGrowthPx（总增量），
           宽度超过 80 DIP 的控件实际吃到的百分比就低于 5%（430 DIP 的行约 0.9%）。 */
        public const double Intensity = 0.05;
        public const double MaxGrowthPx = 4.0;

        /* 上游 DEFAULT_SPRING = { stiffness: 550, damping: 30 }，
           也就是 a = x'' + c·x' + k·(x-T) = 0 里的 k 和 c（质量归 1）。
             ζ = 30 / (2√550) = 0.6396，ω₀ = √550 = 23.452 rad/s。 */
        public const double Stiffness = 550;
        public const double Damping = 30;

        /* 一段动画播多久。e^(-a·t) 在 t = 0.40s 时是 e^-6 = 0.0025，
           也就是残差不到幅度的 0.3%（32px 的按钮上约 0.004px）—— 收尾那一下
           落到目标值看不出来。第一个超调峰在 t = π/ω_d = 0.174s，
           「按下去弹回来」的手感都在前 0.25s 里，后面的尾巴是纯粹的等式收尾。 */
        const double Duration = 0.40;

        const double A = Damping / 2.0;                          /* 15.0     */
        static readonly double Wd = Math.Sqrt(Stiffness - A * A);  /* 18.0278 */

        static readonly Stopwatch _clock = Stopwatch.StartNew();

        /* 埋点出口直接用 Layout.Diag —— IslandWindow 早就把它接到窗口日志上了，
           于是「按压/松开到底有没有发生」在岛自己的日志里就查得到，
           不必再给这个类单开一条需要人记得去接的管道。 */

        static readonly List<SquishTarget> _all = new List<SquishTarget>();
        static bool? _override;             /* null = 跟随系统；探针/环境变量可改写 */
        static Window _hooked;

        internal static double Now { get { return _clock.Elapsed.TotalSeconds; } }

        /* ---------------------------------------------------- 弹簧解析解 */

        /// <summary>欠阻尼弹簧在 t 时刻的位置（t = 0 时从 x0、速度 v0 出发，指向 target）。</summary>
        public static double PositionOf(double x0, double v0, double target, double t)
        {
            double u0 = x0 - target;
            double c1 = (v0 + A * u0) / Wd;
            double e = Math.Exp(-A * t);
            return target + e * (u0 * Math.Cos(Wd * t) + c1 * Math.Sin(Wd * t));
        }

        /// <summary>上面那条曲线在 t 时刻的速度。</summary>
        public static double VelocityOf(double x0, double v0, double target, double t)
        {
            double u0 = x0 - target;
            double c1 = (v0 + A * u0) / Wd;
            double e = Math.Exp(-A * t);
            double cs = u0 * Math.Cos(Wd * t) + c1 * Math.Sin(Wd * t);
            double sn = -u0 * Math.Sin(Wd * t) + c1 * Math.Cos(Wd * t);
            return e * (-A * cs + Wd * sn);
        }

        /// <summary>超调比例：峰值越过目标的那部分和整段位移之比，e^(-πζ/√(1-ζ²))。</summary>
        public static double OvershootRatio
        {
            get
            {
                double zeta = Damping / (2.0 * Math.Sqrt(Stiffness));
                return Math.Exp(-Math.PI * zeta / Math.Sqrt(1.0 - zeta * zeta));
            }
        }

        /* ------------------------------------------------------------ 开关 */

        /// <summary>
        /// 「减少动态效果」是否生效。读 SPI_GETCLIENTAREAANIMATION，
        /// 也就是 设置 → 辅助功能 → 视觉效果 → 动画效果（Windows 10/11 里
        /// 这一项既管系统动画也管应用动画）。
        ///
        /// 每次现读、不缓存：这一项可以在程序运行中途被改，而它只在
        /// 「按下 / 松开」那一瞬间被问一次，一次 SystemParametersInfo 的代价
        /// （微秒级）换掉一个「改了设置要重启才生效」的坑，划算。
        /// 失败时退回 WPF 的同名属性，再失败就当「没有要求减弱」。
        /// </summary>
        public static bool Reduced
        {
            get
            {
                if (_override.HasValue) return _override.Value;

                string env = null;
                try { env = Environment.GetEnvironmentVariable("XIAODAO_SQUISH_MOTION"); }
                catch { }
                if (!string.IsNullOrEmpty(env))
                {
                    if (env == "off" || env == "0" || env == "reduced") return true;
                    if (env == "on" || env == "1") return false;
                }

                try
                {
                    int v;
                    if (SystemParametersInfo(SpiGetClientAreaAnimation, 0, out v, 0))
                        return v == 0;
                }
                catch { }
                try { return !SystemParameters.ClientAreaAnimation; }
                catch { return false; }
            }
        }

        /// <summary>探针用：null = 跟随系统。真实运行路径不改它。</summary>
        public static void OverrideReduced(bool? reduced)
        {
            _override = reduced;
        }

        /// <summary>探针 / 日志用：把系统那一项读成文字。</summary>
        public static string DescribeReduced()
        {
            string mode = _override.HasValue
                ? (_override.Value ? "forced-off" : "forced-on")
                : "auto";
            return "reduced=" + Reduced + " (" + mode + ")";
        }

        /* ------------------------------------------------------------ 挂载 */

        /// <summary>
        /// 给一个可点元素套上按压回弹，并把三个指针事件接好。
        /// 返回目标对象；处理程序要挂在它的 Surface 上（不一定等于传进来的 el）。
        ///
        /// 命中面的存在是为了满足「不改变命中区域」这一条：
        /// WPF 的命中测试走渲染变换链，直接把 ScaleTransform 挂在按钮上，
        /// 按下期间它的命中矩形会跟着横向放大、纵向缩小（32px 的按钮约 ±1.6px，
        /// 多指触摸的第二根手指就可能落进那个缝里，被当成「按住岛体拖动」）。
        /// 所以结构改成：
        ///
        ///     SquishPlate（Grid，透明背景，继承 Tag/Uid/Cursor）
        ///       └─ 原元素（IsHitTestVisible=false，RenderTransform=ScaleTransform）
        ///
        /// 命中面自动量到原元素**未缩放**的尺寸，所以：
        ///   · 布局尺寸、Canvas.Left/Top、变形中心全部不变；
        ///   · 元素被 Visibility.Collapsed 时命中面自动变 0×0（Grid 量 Collapsed
        ///     子元素得 0），「清理丢失」那种运行时折叠的按钮不会留一块隐形命中区。
        ///
        /// 元素还没进视觉树时套不了命中面（没有父容器可插），这时退化成
        /// 「直接变换元素本身」并留一行日志 —— 当前版面里没有这种调用点，
        /// 这一条只是防止以后有人把 OnTap 写到 At 之前时静默失效。
        /// </summary>
        public static SquishTarget Wrap(FrameworkElement el, string name)
        {
            if (el == null) return null;

            SquishTarget t = new SquishTarget();
            t.Name = string.IsNullOrEmpty(name) ? "?" : name;

            Panel parent = el.Parent as Panel;

            if (parent != null)
            {
                SquishPlate plate = new SquishPlate();
                /* 透明背景是「能被命中」的前提：WPF 里背景为 null 的面板
                   不参与命中测试，那样连点击都收不到了。 */
                plate.Background = Brushes.Transparent;
                /* Tag / Uid / 光标都必须跟过来：命中面一出现，原元素就退出了命中测试，
                   这三个属性原来挂在原元素上。
                     Tag="act" → IslandWindow.IsActionHit（按住按钮不拖窗口）
                     Uid="del" → Layout.IsDel（中转站的移除键，不能当成拖出起点）
                   Cursor 先继承，OnTap 随后统一覆写成 Hand。 */
                plate.Tag = el.Tag;
                plate.Uid = el.Uid;
                plate.Cursor = el.Cursor;

                int idx = parent.Children.IndexOf(el);
                Canvas.SetLeft(plate, Canvas.GetLeft(el));
                Canvas.SetTop(plate, Canvas.GetTop(el));
                parent.Children.RemoveAt(idx);
                parent.Children.Insert(idx, plate);
                plate.Children.Add(el);

                t.Surface = plate;
                plate.Target = t;
            }
            else
            {
                t.Surface = el;
                Log("squish wrap-fallback " + t.Name
                    + " (element has no parent yet; hit rect will follow the transform)");
            }

            /* 变形中心 = 元素自己的中心，但走 ScaleTransform 的 CenterX/CenterY，
               不去碰 RenderTransformOrigin：那个属性是元素级共享的，元素上本来
               挂着的别的变换（液面那一套）也在吃它，改它等于悄悄改了别人的图形。 */
            el.IsHitTestVisible = false;
            t.Visual = el;

            ScaleTransform st = new ScaleTransform(1, 1);
            t.X = new SquishAxis(); t.X.Scale = st; t.X.Prop = ScaleTransform.ScaleXProperty;
            t.Y = new SquishAxis(); t.Y.Scale = st; t.Y.Prop = ScaleTransform.ScaleYProperty;
            /* 先不挂：静止状态的可视树必须和加这个功能之前完全一样，
               否则「改动前后同一版面渲染逐位相等」这条证明就不成立了。
               第一次按下时才 Attach（见 Aim）。 */

            Wire(t);
            _all.Add(t);
            return t;
        }

        static void Wire(SquishTarget t)
        {
            FrameworkElement s = t.Surface;

            s.MouseLeftButtonDown += delegate
            {
                Press(t);
                /* 抓住鼠标：这样「在按钮外面松手」也会收到 MouseUp / LostMouseCapture。
                   不抓的话只能等岛那个 16ms 轮询来兜底，会慢半拍。
                   抓的是命中面而不是原元素 —— 原元素已经不参与命中了。 */
                try { s.CaptureMouse(); } catch { }
            };
            s.MouseLeftButtonUp += delegate
            {
                Release(t, "up");
                try { s.ReleaseMouseCapture(); } catch { }
            };
            /* 按住的状态下指针离开控件：和上游 onPointerLeave 一样收掉。 */
            s.MouseLeave += delegate
            {
                if (t.Pressed) Release(t, "leave");
            };
            /* 捕获被系统收走（窗口失活、弹出 UAC、切桌面、元素被移出树）。
               这是「松手那一下丢事件」最可靠的兜底，而且不靠任何轮询。 */
            s.LostMouseCapture += delegate
            {
                if (t.Pressed) Release(t, "capture lost");
            };
            /* 元素离开视觉树：内容重建、换层、关窗都会走这里。
               必须在离开前把形状归位 —— 但通常它马上就被 Forget 丢掉了，
               这里只是保证「万一它还被别处引用着」时不会留一个压扁的形状。 */
            s.Unloaded += delegate
            {
                if (t.Pressed) Release(t, "unloaded");
            };
        }

        /* 窗口级的两条兜底。只在有目标存在期间挂着，Forget 时摘掉 ——
           Window 上的事件会把这个静态类当引用目标，不摘就是一扇关不掉的窗。 */
        static void HookWindow(SquishTarget t)
        {
            Window w = null;
            try { w = Window.GetWindow(t.Surface); } catch { }
            if (w == null || ReferenceEquals(w, _hooked)) return;

            UnhookWindow();
            _hooked = w;
            w.PreviewMouseUp += OnWindowUp;
            w.Closed += OnWindowClosed;
        }

        static void UnhookWindow()
        {
            if (_hooked == null) return;
            try
            {
                _hooked.PreviewMouseUp -= OnWindowUp;
                _hooked.Closed -= OnWindowClosed;
            }
            catch { }
            _hooked = null;
        }

        static void OnWindowUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;
            ReleaseAll("window up");
        }

        static void OnWindowClosed(object sender, EventArgs e)
        {
            SnapAll("window closed");
            UnhookWindow();
        }

        /* ------------------------------------------------------------ 状态 */

        public static int Count { get { return _all.Count; } }

        public static int PressedCount
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _all.Count; i++) if (_all[i].Pressed) n++;
                return n;
            }
        }

        /// <summary>探针用：按名字取一个目标（第一个匹配）。</summary>
        public static SquishTarget Find(string name)
        {
            for (int i = 0; i < _all.Count; i++) if (_all[i].Name == name) return _all[i];
            return null;
        }

        /// <summary>探针用：当前所有目标的快照。</summary>
        public static SquishTarget[] Snapshot()
        {
            return _all.ToArray();
        }

        /* ------------------------------------------------------- 按下 / 松开 */

        public static void Press(SquishTarget t)
        {
            if (t == null) return;

            /* 减少动态效果：按下就是「什么都不发生」（上游同语义）。
               仍然记下 pressed，让松开那条路有一个确定的收尾。 */
            if (Reduced)
            {
                t.Pressed = true;
                t.Amp = 0;
                SnapTo(t);
                Log("squish press " + t.Name + " suppressed (reduced motion)");
                return;
            }

            if (t.Pressed) return;

            double amp = AmplitudeFor(t);
            t.Amp = amp;
            t.Pressed = true;

            HookWindow(t);
            Log("squish press " + t.Name + " amp=" + amp.ToString("0.0000")
                + " x=" + (1 + amp).ToString("0.0000")
                + " y=" + (1.0 / (1.0 + amp)).ToString("0.0000"));

            Aim(t, 1 + amp, 1.0 / (1.0 + amp));
        }

        /// <summary>松开：交给弹簧回到 1。幂等，重复调用不会重起一段动画。</summary>
        public static void Release(SquishTarget t, string why)
        {
            if (t == null) return;

            bool was = t.Pressed;
            t.Pressed = false;
            if (!was && t.AtRest) return;
            if (!was && t.X.Target == 1.0 && t.Y.Target == 1.0) return;   /* 已经在回 1 的路上 */

            /* 减少动态效果：不弹，直接归位。这一支不看开关的「当下」值之外的东西 ——
               按下之后才把开关打开，也必须回到 1（上游同样如此）。 */
            if (Reduced)
            {
                SnapTo(t);
                Log("squish release " + t.Name + " (" + why + ") snap=reduced");
                return;
            }

            Log("squish release " + t.Name + " (" + why + ")");
            Aim(t, 1.0, 1.0);
        }

        /// <summary>所有按下态一律松开。窗口级 MouseUp、取消都走这条。</summary>
        public static void ReleaseAll(string why)
        {
            for (int i = 0; i < _all.Count; i++) Release(_all[i], why);
        }

        /// <summary>立刻归位（不过弹簧）。关窗 / 退出用。</summary>
        public static void SnapAll(string why)
        {
            for (int i = 0; i < _all.Count; i++)
            {
                SquishTarget t = _all[i];
                if (t.Pressed) Log("squish release " + t.Name + " (" + why + ") snap");
                t.Pressed = false;
                SnapTo(t);
            }
        }

        /// <summary>
        /// 丢掉所有目标并归位。内容重建（Rebuild / 列表刷新）时调用：
        /// 旧的视觉树马上要被清掉，目标对象必须一起放掉，否则每换一次层就往
        /// _all 里堆一批死元素（岛的 Rebuild 是一次点击一次）。
        ///
        /// 归位是必须的：重建可能只换掉一部分控件，活下来的那个要是停在
        /// 中间形状，就成了「卡在压扁状态」。
        /// </summary>
        public static void Forget()
        {
            SnapAll("content rebuilt");
            _all.Clear();
            UnhookWindow();
        }

        /* ------------------------------------------------------------ 内部 */

        static void Aim(SquishTarget t, double tx, double ty)
        {
            t.Anims = 2;
            EventHandler done = delegate
            {
                t.Anims--;
                if (t.Anims < 0) t.Anims = 0;
                if (t.AtRest) Detach(t);
            };
            t.X.Aim(tx, Duration, done);
            t.Y.Aim(ty, Duration, done);
            Attach(t);
        }

        static void SnapTo(SquishTarget t)
        {
            t.Anims = 0;
            t.X.Snap(1.0);
            t.Y.Snap(1.0);
            Detach(t);
        }

        /* 变换挂上 / 摘掉。

           两条都必须成立：
             1. 不覆写元素本来挂着的 RenderTransform —— 卡片里已经有别的层在用它
                （液面/张力那一套），直接赋一个 ScaleTransform 就是把它吃掉。
                所以在按下那一刻把当时的值存进 Original，组合进 TransformGroup，
                松开时原样还回去。
             2. 静止时**摘掉**自己那一层。恒等的变换链哪怕看不出差别，留着它
                就没法用「改动前后同一版面渲染逐位相等」来证明「没按的时候
                画面一点没变」（probe-squish 的 A/B 项）。 */
        static void Attach(SquishTarget t)
        {
            if (t.Visual == null) return;

            /* 缩放中心用 CenterX/CenterY（元素自己的中心），不依赖
               RenderTransformOrigin；尺寸在按下这一刻取，布局本来就固定。 */
            double w = t.Visual.ActualWidth, h = t.Visual.ActualHeight;
            if (!double.IsNaN(w) && w > 0) t.X.Scale.CenterX = w / 2;
            if (!double.IsNaN(h) && h > 0) t.X.Scale.CenterY = h / 2;

            Transform current = t.Visual.RenderTransform;

            /* 已经是我挂的（或我刚才挂的组），不用重建 */
            if (ReferenceEquals(current, t.Mine)) return;

            t.Original = current;
            if (current == null)
            {
                t.Mine = t.X.Scale;
            }
            else
            {
                TransformGroup g = new TransformGroup();
                g.Children.Add(current);
                g.Children.Add(t.X.Scale);
                t.Mine = g;
            }
            t.Visual.RenderTransform = t.Mine;
        }

        static void Detach(SquishTarget t)
        {
            if (t.Visual == null) return;
            t.X.Snap(1.0);
            t.Y.Snap(1.0);

            /* 只在「现在挂着的还是我写的那一层」时才还原。
               别人中途换过（例如另一层动画重设了 RenderTransform），
               说明它已经接手，这时候再还一个旧值就是倒着覆盖别人的新值。 */
            if (t.Mine != null && ReferenceEquals(t.Visual.RenderTransform, t.Mine))
                t.Visual.RenderTransform = t.Original;
            t.Mine = null;
        }

        /* 横向放大比例。宽度按元素自己的实际宽度算，并受 MaxGrowthPx 绝对上限约束。 */
        public static double AmplitudeFor(SquishTarget t)
        {
            double w = WidthOf(t);
            double amp = Intensity;
            if (w > 0)
            {
                double cap = MaxGrowthPx / w;
                if (cap < amp) amp = cap;
            }
            return amp < 0 ? 0 : amp;
        }

        static double WidthOf(SquishTarget t)
        {
            if (t == null || t.Visual == null) return 0;
            double w = t.Visual.ActualWidth;
            if (double.IsNaN(w) || w <= 0) w = t.Visual.Width;
            if (double.IsNaN(w) || w <= 0) w = 0;
            return w;
        }

        static void Log(string s)
        {
            Action<string> d = Layout.Diag;
            if (d != null) d(s);
        }

        /* --------------------------------------------------- 减少动态效果 */
        const uint SpiGetClientAreaAnimation = 0x1042;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
                                                out int pvParam, uint fWinIni);
    }
}

/* ── 上游许可全文（MIT），移植部分受它约束 ────────────────────────────

   MIT License

   Copyright (c) 2026 Yousef Hindy

   Permission is hereby granted, free of charge, to any person obtaining a copy
   of this software and associated documentation files (the "Software"), to deal
   in the Software without restriction, including without limitation the rights
   to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
   copies of the Software, and to permit persons to whom the Software is
   furnished to do so, subject to the following conditions:

   The above copyright notice and this permission notice shall be included in all
   copies or substantial portions of the Software.

   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
   SOFTWARE.

   上游 FluidKit 的名称与品牌素材不在该许可内；本移植只取 useSquish 的算法本身，
   不含任何上游代码文件、React / Motion 依赖或品牌资源。 */