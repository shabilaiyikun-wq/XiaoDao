using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinIsland
{
    /* 按压回弹（Squish）验收探针。

       验的是「用户按下去会发生什么」，不是「那个方法自己能跑」：
       真实版面由 Layout.Build 生成（不是手搓的假按钮），真实路由事件由
       RaiseEvent 投递（不是直接调私有方法），形状从 ScaleTransform 上读
       （WPF 动画时钟真正渲染出来的那个值，不是模型里算的那个）。

       用法：
         probe-squish.exe                 跑全部用例，打印 PASS/FAIL，退出码 = 失败数
         probe-squish.exe render <png>    只渲染一张静止版面（给改动前后逐位对比用）
    */
    class ProbeApp : Application
    {
    }

    static class ProbeSquish
    {
        static int _fail;
        static readonly List<string> _lines = new List<string>();

        static void Ok(bool cond, string msg, string detail)
        {
            string line = (cond ? "   ok   " : "   FAIL ") + msg;
            if (!string.IsNullOrEmpty(detail)) line += "  [" + detail + "]";
            _lines.Add(line);
            Console.WriteLine(line);
            if (!cond) _fail++;
        }

        static void Head(string s)
        {
            _lines.Add("");
            _lines.Add("== " + s);
            Console.WriteLine();
            Console.WriteLine("== " + s);
        }

        static string F(double v) { return v.ToString("0.00000", CultureInfo.InvariantCulture); }

        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length >= 1 && args[0] == "render")
            {
                return RenderOnly(args.Length >= 2 ? args[1] : "squish-render.png");
            }

            if (args.Length >= 1 && args[0] == "centers")
            {
                string sc = args.Length >= 2 ? args[1] : "timer";
                double cw = args.Length >= 3 ? double.Parse(args[2], CultureInfo.InvariantCulture) : 436;
                double ch = args.Length >= 4 ? double.Parse(args[3], CultureInfo.InvariantCulture) : 216;
                return PrintCenters(sc, cw, ch);
            }

            ProbeApp app = new ProbeApp();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int rc = 1;

            app.Startup += delegate
            {
                try
                {
                    rc = Run();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("!! PROBE THREW " + ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine(ex.StackTrace);
                    rc = 99;
                }
                finally
                {
                    Console.WriteLine();
                    Console.WriteLine(rc == 0 ? "ALL PASS" : (rc + " FAILED"));
                    try { Finish(rc); } catch { }
                    app.Shutdown(rc);
                }
            };

            app.Run();
            return rc;
        }

        static void Finish(int rc)
        {
            _lines.Add("");
            _lines.Add(rc == 0 ? "ALL PASS" : (rc + " FAILED"));
            try
            {
                File.WriteAllLines("probe-squish.log", _lines.ToArray(), new UTF8Encoding(false));
            }
            catch { }
        }

        /* ------------------------------------------------------------ 卡片 */

        class Card
        {
            public Window Win;
            public Canvas Root;
            public Built Built;
            public Dictionary<string, int> Taps = new Dictionary<string, int>();
            public List<string> Diag = new List<string>();
            public string Scene;
        }

        static Card MakeCard(string scene, double w, double h)
        {
            Card c = new Card();
            c.Scene = scene;

            Layout.Diag = delegate(string s)
            {
                if (c.Diag.Count < 400) c.Diag.Add(s);
            };

            Look look = Look.ByKey("glass");
            Art art = Art.ByKey("aurora");
            Snap snap = new Snap();

            CardActions act = new CardActions();
            act.TimerToggle = delegate { Bump(c, "timer-toggle"); };
            act.TimerReset = delegate { Bump(c, "timer-reset"); };
            act.TimerPlus = delegate { Bump(c, "timer-plus"); };
            act.Scene = delegate(string k) { Bump(c, "scene-" + k); };
            act.ClearClips = delegate { Bump(c, "clip-clear"); };
            act.AddTodo = delegate { Bump(c, "todo-add"); };
            act.ClearDoneTodos = delegate { Bump(c, "todo-clear"); };
            act.ClearTransfer = delegate { Bump(c, "transfer-clear"); };

            c.Built = Layout.Build(scene, "expanded", w, h, look, art, snap, act, false, false);

            c.Root = new Canvas();
            c.Root.Width = w;
            c.Root.Height = h;
            c.Root.Background = Brushes.Transparent;
            Ui.At(c.Root, c.Built.Root, 0, 0);

            Window win = new Window();
            win.WindowStyle = WindowStyle.None;
            win.AllowsTransparency = true;
            win.Background = Brushes.Transparent;
            win.ResizeMode = ResizeMode.NoResize;
            win.ShowInTaskbar = false;
            win.ShowActivated = false;
            win.Width = w;
            win.Height = h;
            win.Left = -4000;
            win.Top = -4000;
            win.Content = c.Root;
            win.Show();
            c.Win = win;

            Pump(160);
            return c;
        }

        static void DropCard(Card c)
        {
            if (c == null) return;
            Squish.Forget();
            try { c.Win.Close(); } catch { }
            Pump(60);
        }

        /* 打印每个可点控件在卡片坐标系里的中心点。
           端到端脚本拿这些坐标去做真实的 SendInput 点击，
           这样「点在哪」不是猜的，而是和探针量到的是同一份几何。 */
        static int PrintCenters(string scene, double w, double h)
        {
            ProbeApp app = new ProbeApp();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int rc = 1;

            app.Startup += delegate
            {
                try
                {
                    Card c = MakeCard(scene, w, h);
                    SquishTarget[] all = Squish.Snapshot();
                    for (int i = 0; i < all.Length; i++)
                    {
                        SquishTarget t = all[i];
                        Point p = CenterOf(t.Surface, c.Root);
                        Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                            "center {0} {1:0.0} {2:0.0} w={3:0.0} h={4:0.0}",
                            t.Name, p.X, p.Y, t.Surface.RenderSize.Width,
                            t.Surface.RenderSize.Height));
                    }
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "card {0:0}x{1:0}", w, h));
                    rc = 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("!! " + ex.Message);
                    rc = 98;
                }
                finally { app.Shutdown(rc); }
            };

            app.Run();
            return rc;
        }

        /* ------------------------------------------------------------ 用例 */

        static int Run()
        {
            Console.WriteLine("probe-squish: " + Squish.DescribeReduced());

            double w = 436, h = 216;

            /* ---------------------------------------- 0. 挂载范围 */
            Head("0. 挂载范围：只作用于明确可点的控件");
            Card c0 = MakeCard("timer", w, h);

            List<SquishPlate> plates = FindPlates(c0.Root);
            SquishTarget[] targets = Squish.Snapshot();

            Ok(targets.Length > 0, "OnTap 的可点控件都套上了不变形的命中面",
               "targets=" + targets.Length + " plates=" + plates.Count);
            Ok(plates.Count == targets.Length, "命中面数量 == 目标数量",
               plates.Count + " / " + targets.Length);

            bool fallback = false, allAct = true;
            for (int i = 0; i < targets.Length; i++)
            {
                if (ReferenceEquals(targets[i].Surface, targets[i].Visual)) fallback = true;
                if ((targets[i].Surface.Tag as string) != "act") allAct = false;
            }
            Ok(!fallback, "没有走到「元素还没有父容器」的退路（命中面全部就位）",
               fallback ? "存在 fallback" : "0 fallback");
            Ok(allAct, "名单里每个都是 Tag=act 的可点控件（岛体 / 拖动 / 桌宠不在内）", "");

            SquishTarget toggle = Squish.Find("timer-toggle");
            SquishTarget reset = Squish.Find("timer-reset");
            Ok(toggle != null && reset != null, "按名字取得到 timer-toggle / timer-reset", "");
            if (toggle == null || reset == null) { DropCard(c0); return _fail + 1; }

            /* 静止态：我这一层不能挂着。元素上本来就有别的变换（液面那一套），
               所以判据是「挂着的东西不是我的」而不是「什么都没挂」。 */
            Ok(!ReferenceEquals(toggle.Visual.RenderTransform, toggle.X.Scale)
               && !ReferenceEquals(toggle.Visual.RenderTransform, toggle.Mine),
               "静止时不挂按压变换（元素原有变换保持原样）",
               "rt=" + (toggle.Visual.RenderTransform == null ? "null"
                       : toggle.Visual.RenderTransform.GetType().Name));

            Transform originalAtRest = toggle.Visual.RenderTransform;

            /* ---------------------------------------- 1. 按下 */
            Head("1. 按下 → 横向变宽 / 纵向压缩 / 中心与命中区域稳定");

            Size before = new Size(toggle.Visual.ActualWidth, toggle.Visual.ActualHeight);
            Size plateBefore = toggle.Surface.RenderSize;
            Point centerBefore = CenterOf(toggle.Surface, c0.Root);

            Down(toggle.Surface);
            List<double[]> trace = new List<double[]>();
            for (int i = 0; i < 16; i++)
            {
                Pump(16);
                trace.Add(new double[] { toggle.X.Value, toggle.Y.Value });
            }

            double peakX = 1, minY = 1;
            for (int i = 0; i < trace.Count; i++)
            {
                if (trace[i][0] > peakX) peakX = trace[i][0];
                if (trace[i][1] < minY) minY = trace[i][1];
            }
            double amp = toggle.Amp;
            double bound = 1 + amp * (1 + Squish.OvershootRatio) + 0.005;

            Ok(amp > 0 && amp <= Squish.Intensity + 1e-9, "幅度不超过 Intensity（克制）",
               "amp=" + F(amp));
            Ok(peakX > 1.0 && peakX <= bound, "横向确实变宽，且不超出弹簧超调",
               "peakX=" + F(peakX) + " 上限=" + F(bound));
            Ok(minY < 1.0 && Math.Abs(minY * peakX - 1.0) < 0.004,
               "纵向按反比压缩（X·Y≈1，体积守恒）",
               "minY=" + F(minY) + " X·Y=" + F(minY * peakX));

            Ok(Math.Abs(toggle.Visual.ActualWidth - before.Width) < 1e-9
               && Math.Abs(toggle.Visual.ActualHeight - before.Height) < 1e-9,
               "布局尺寸不变（ActualWidth/Height 一个像素都没动）",
               F(before.Width) + "x" + F(before.Height));

            Point centerDuring = CenterOf(toggle.Surface, c0.Root);
            Ok(Math.Abs(centerDuring.X - centerBefore.X) < 0.01
               && Math.Abs(centerDuring.Y - centerBefore.Y) < 0.01,
               "变形中心稳定（命中面位置没漂）",
               F(centerBefore.X) + "," + F(centerBefore.Y) + " -> "
               + F(centerDuring.X) + "," + F(centerDuring.Y));

            Ok(Math.Abs(toggle.Surface.RenderSize.Width - plateBefore.Width) < 1e-9
               && Math.Abs(toggle.Surface.RenderSize.Height - plateBefore.Height) < 1e-9,
               "命中面尺寸不变（命中区域不随变形走）", F(plateBefore.Width) + "x" + F(plateBefore.Height));

            /* 缩放确实绕中心：元素左右/上下两端相对命中面的中心对称 */
            double cx = toggle.X.Scale.CenterX, cy = toggle.X.Scale.CenterY;
            Ok(Math.Abs(cx - before.Width / 2) < 0.01 && Math.Abs(cy - before.Height / 2) < 0.01,
               "缩放中心 = 元素中心（用 CenterX/CenterY，不动 RenderTransformOrigin）",
               "center=" + F(cx) + "," + F(cy));

            int hits = 0;
            double pw = plateBefore.Width, ph = plateBefore.Height;
            Point[] probes = new Point[]
            {
                new Point(1, 1), new Point(pw - 1, 1), new Point(1, ph - 1),
                new Point(pw - 1, ph - 1), new Point(pw / 2, ph / 2)
            };
            for (int i = 0; i < probes.Length; i++)
            {
                IInputElement hh = toggle.Surface.InputHitTest(probes[i]);
                if (hh != null && ReferenceEquals(hh, toggle.Surface)) hits++;
            }
            Ok(hits == probes.Length, "按下期间命中面仍然命中自己（命中区域不变）",
               hits + "/" + probes.Length);

            /* ---------------------------------------- 2. 松开 */
            Head("2. 松开 → 弹簧恢复（一次软超调，最终精确归 1）");

            Up(toggle.Surface);
            List<double[]> rel = new List<double[]>();
            for (int i = 0; i < 30; i++)
            {
                Pump(16);
                rel.Add(new double[] { toggle.X.Value, toggle.Y.Value });
            }

            double minX = 1;
            for (int i = 0; i < rel.Count; i++) if (rel[i][0] < minX) minX = rel[i][0];
            double overshoot = (1.0 - minX) / Math.Max(amp, 1e-9);

            Ok(minX < 1.0, "回程有反向超调（弹簧的「抖一下」本身就是回弹）",
               "minX=" + F(minX) + " 模型超调比=" + F(Squish.OvershootRatio));
            Ok(overshoot <= Squish.OvershootRatio * 1.6 + 0.02,
               "超调克制（单周期，不超过模型预测的 1.6 倍）",
               "实测=" + F(overshoot) + " 模型=" + F(Squish.OvershootRatio));

            Ok(Math.Abs(toggle.X.Value - 1.0) < 1e-9 && Math.Abs(toggle.Y.Value - 1.0) < 1e-9,
               "最终精确回到 1（不是 0.9999）", F(toggle.X.Value) + " / " + F(toggle.Y.Value));
            Ok(ReferenceEquals(toggle.Visual.RenderTransform, originalAtRest),
               "松开后返回元素原来的 RenderTransform（不吞别人的变换）",
               "rt=" + (toggle.Visual.RenderTransform == null ? "null"
                       : toggle.Visual.RenderTransform.GetType().Name));
            Ok(Squish.PressedCount == 0, "没有残留的按下态", "pressed=" + Squish.PressedCount);

            /* ---------------------------------------- 3. 快速连点 */
            Head("3. 快速点击（40ms 一次 × 6）");

            List<double[]> rapid = new List<double[]>();
            for (int i = 0; i < 6; i++)
            {
                Down(toggle.Surface);
                Pump(20);
                rapid.Add(new double[] { toggle.X.Value, toggle.Y.Value });
                Up(toggle.Surface);
                Pump(20);
                rapid.Add(new double[] { toggle.X.Value, toggle.Y.Value });
            }
            Pump(600);

            bool finite = true, bounded = true;
            double maxJump = 0;
            for (int i = 0; i < rapid.Count; i++)
            {
                double x = rapid[i][0], y = rapid[i][1];
                if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
                    finite = false;
                if (x < 0.9 || x > 1 + Squish.Intensity * 1.6 + 0.02) bounded = false;
                if (y < 0.9 || y > 1.1) bounded = false;
                if (i > 0)
                {
                    double d = Math.Abs(rapid[i][0] - rapid[i - 1][0]);
                    if (d > maxJump) maxJump = d;
                }
            }
            Ok(finite, "连点过程中没有 NaN / Infinity", "");
            Ok(bounded, "连点过程中形状始终在合理带内", "");
            Ok(maxJump < 0.06, "连点没有跳变", "maxJump=" + F(maxJump));
            Ok(Math.Abs(toggle.X.Value - 1.0) < 1e-9
               && ReferenceEquals(toggle.Visual.RenderTransform, originalAtRest),
               "连点结束后精确归 1 并还原变换", F(toggle.X.Value));

            /* ---------------------------------------- 4. 取消路径 */
            Head("4. 取消 / 丢事件 / 离开 / 重建 / 关窗");

            Check(toggle, originalAtRest, "4a 窗口级 MouseUp",
                  delegate { Squish.ReleaseAll("probe-window-up"); }, toggle);

            Check(reset, null, "4b LostMouseCapture（UAC / 切桌面 / 元素被移出树）",
                  delegate { Raise(reset.Surface, UIElement.LostMouseCaptureEvent); }, reset);

            Check(reset, null, "4c MouseLeave（按住拖出控件）",
                  delegate { Raise(reset.Surface, UIElement.MouseLeaveEvent); }, reset);

            /* 4d 内容重建：Forget 必须归位、清空名单、并摘掉窗口钩子 */
            Down(reset.Surface);
            Pump(60);
            double squishedBefore = Math.Abs(reset.X.Value - 1.0);
            Squish.Forget();
            double afterForget = Math.Abs(reset.X.Value - 1.0);
            Ok(squishedBefore > 1e-6, "Forget 之前确实处于压扁状态", "|x-1|=" + F(squishedBefore));
            Ok(afterForget < 1e-9, "Forget（内容重建）立刻归位", "|x-1|=" + F(afterForget));
            Ok(Squish.Count == 0, "Forget 之后名单为空（不会一直堆死元素）", "count=" + Squish.Count);

            DropCard(c0);

            /* ---------------------------------------- 5. 减少动态效果 */
            Head("5. 减少动态效果（系统「动画效果」关闭）");
            Card c5 = MakeCard("timer", w, h);
            SquishTarget red = Squish.Find("timer-toggle");

            Squish.OverrideReduced(true);
            Ok(Squish.Reduced, "开关读到「减弱」", Squish.DescribeReduced());

            if (red == null)
            {
                Ok(false, "取不到 timer-toggle", "missing");
            }
            else
            {
                Transform origRed = red.Visual.RenderTransform;
                Down(red.Surface);
                bool still = true;
                double maxDev = 0;
                for (int i = 0; i < 12; i++)
                {
                    Pump(16);
                    double dx = Math.Abs(red.X.Value - 1.0), dy = Math.Abs(red.Y.Value - 1.0);
                    if (dx > maxDev) maxDev = dx;
                    if (dy > maxDev) maxDev = dy;
                    if (dx > 1e-9 || dy > 1e-9) still = false;
                }
                Ok(still, "开着「减弱」时按下完全不形变", "最大偏移=" + F(maxDev));
                Ok(ReferenceEquals(red.Visual.RenderTransform, origRed),
                   "开着「减弱」时连变换都不挂", "");

                Up(red.Surface);
                Pump(500);
                Ok(Math.Abs(red.X.Value - 1.0) < 1e-9 && Math.Abs(red.Y.Value - 1.0) < 1e-9,
                   "开着「减弱」时松开仍然精确归 1",
                   F(red.X.Value) + "/" + F(red.Y.Value));
            }

            /* 中途打开开关：已经压扁的必须立刻回 1，不能冻住 */
            Squish.OverrideReduced(false);
            if (red != null)
            {
                Down(red.Surface);
                Pump(90);
                double squished = Math.Abs(red.X.Value - 1.0);
                Squish.OverrideReduced(true);
                Up(red.Surface);
                Pump(60);
                Ok(squished > 1e-6, "先按下（未减弱）确实压扁了", "|x-1|=" + F(squished));
                Ok(Math.Abs(red.X.Value - 1.0) < 1e-9,
                   "中途打开「减弱」后松开立刻归 1（不冻在压扁状态）", "x=" + F(red.X.Value));
            }
            Squish.OverrideReduced(null);
            DropCard(c5);

            /* ---------------------------------------- 6. 点击链路 */
            Head("6. 点击链路没被命中面破坏");
            Card c6 = MakeCard("timer", w, h);
            SquishTarget plus = Squish.Find("timer-plus");

            if (plus == null)
            {
                Ok(false, "取不到 timer-plus", "missing");
            }
            else
            {
                Transform o6 = plus.Visual.RenderTransform;
                Down(plus.Surface);
                Pump(40);
                Up(plus.Surface);
                /* 松开的弹簧是 0.40s，必须跑满再比变换 —— 只等 220ms 就断言
                   「已经还原」会必然失败（探针自己踩过一次）。 */
                Pump(650);
                Ok(Get(c6, "timer-plus") >= 1, "点「+1 分」动作仍然触发", "taps=" + Get(c6, "timer-plus"));
                Ok(ReferenceEquals(plus.Visual.RenderTransform, o6), "点击后变换还原", "");

                FrameworkElement hitEl = plus.Surface.InputHitTest(new Point(2, 2)) as FrameworkElement;
                Ok(hitEl != null && (hitEl.Tag as string) == "act",
                   "命中到的元素带 Tag=act（按住按钮不会把窗口拖走）",
                   hitEl == null ? "no hit" : ("tag=" + (hitEl.Tag as string)));

                /* 300ms 防抖仍然有效：同一元素连点两次只算一次 */
                int before2 = Get(c6, "timer-plus");
                Down(plus.Surface); Up(plus.Surface); Pump(40);
                Down(plus.Surface); Up(plus.Surface); Pump(60);
                Ok(Get(c6, "timer-plus") - before2 <= 1,
                   "300ms 防抖没被破坏（同元素连点只算一次）",
                   "delta=" + (Get(c6, "timer-plus") - before2));
            }

            /* Uid 继承 */
            bool uidKept = true;
            int uidChecked = 0;
            SquishTarget[] all = Squish.Snapshot();
            for (int i = 0; i < all.Length; i++)
            {
                FrameworkElement v = all[i].Visual, s = all[i].Surface;
                if (v != null && !string.IsNullOrEmpty(v.Uid))
                {
                    uidChecked++;
                    if (s.Uid != v.Uid) uidKept = false;
                }
            }
            Ok(uidKept, "Uid 从原元素继承到命中面（Uid=del 之类仍然可辨）", "checked=" + uidChecked);
            DropCard(c6);

            /* ---------------------------------------- 7. 宽度上限 */
            Head("7. 幅度上限：宽控件不吃满 5%");

            /* 纯宽度函数，直接量：400 DIP 的行按 5% 要左右各长 10px，必须被 4px 封顶。 */
            CheckCap(400);
            CheckCap(120);
            CheckCap(34);

            Card c7 = MakeCard("todo", w, h);
            SquishTarget widest = null;
            SquishTarget[] s7 = Squish.Snapshot();
            for (int i = 0; i < s7.Length; i++)
            {
                if (widest == null || s7[i].Visual.ActualWidth > widest.Visual.ActualWidth)
                    widest = s7[i];
            }
            if (widest != null && widest.Visual.ActualWidth > 0)
            {
                double a7 = Squish.AmplitudeFor(widest);
                Ok(widest.Visual.ActualWidth * a7 <= Squish.MaxGrowthPx + 1e-9,
                   "真实版面里最宽的可点控件也受 MaxGrowthPx 约束",
                   "width=" + F(widest.Visual.ActualWidth) + " amp=" + F(a7)
                   + " 增量=" + F(widest.Visual.ActualWidth * a7) + "px");
            }
            else
            {
                Ok(false, "找不到可点控件", "none");
            }
            DropCard(c7);

            /* ---------------------------------------- 8. 日志 */
            Head("8. 埋点：按压 / 松开在岛自己的日志里留痕");
            Card c8 = MakeCard("timer", w, h);
            SquishTarget t8 = Squish.Find("timer-toggle");
            if (t8 != null)
            {
                Down(t8.Surface);
                Pump(80);
                Up(t8.Surface);
                Pump(80);
            }
            int press = 0, release = 0;
            for (int i = 0; i < c8.Diag.Count; i++)
            {
                if (c8.Diag[i].StartsWith("squish press")) press++;
                if (c8.Diag[i].StartsWith("squish release")) release++;
            }
            Ok(press >= 1, "日志里有 squish press", "n=" + press);
            Ok(release >= 1, "日志里有 squish release", "n=" + release);
            DropCard(c8);

            return _fail;
        }

        static void CheckCap(double width)
        {
            Canvas host = new Canvas();
            host.Width = width + 20;
            host.Height = 80;
            Border b = new Border();
            b.Width = width;
            b.Height = 30;
            b.Background = Brushes.Transparent;
            Ui.At(host, b, 0, 0);

            SquishTarget t = Squish.Wrap(b, "cap-" + (int)width);
            Window w = new Window();
            w.WindowStyle = WindowStyle.None;
            w.AllowsTransparency = true;
            w.Background = Brushes.Transparent;
            w.Width = host.Width;
            w.Height = host.Height;
            w.Left = -4000;
            w.Top = -4000;
            w.Content = host;
            w.Show();
            Pump(80);

            double amp = Squish.AmplitudeFor(t);
            double growth = b.ActualWidth * amp;
            Ok(growth <= Squish.MaxGrowthPx + 1e-9 && amp <= Squish.Intensity + 1e-9,
               "宽度 " + (int)width + " 的控件：增量被上限卡住",
               "amp=" + F(amp) + " 增量=" + F(growth) + "px");

            Squish.Forget();
            w.Close();
            Pump(40);
        }

        /* 通用取消用例：按下 → 走某条取消路径 → 必须回到 1 且还原变换。 */
        static void Check(SquishTarget t, Transform expectRt, string label,
                          Action cancel, SquishTarget same)
        {
            Transform orig = t.Visual.RenderTransform;
            Down(t.Surface);
            Pump(70);
            double squished = Math.Abs(t.X.Value - 1.0);
            cancel();
            Pump(600);
            bool ok = Math.Abs(t.X.Value - 1.0) < 1e-9
                      && Math.Abs(t.Y.Value - 1.0) < 1e-9
                      && t.Surface != null;
            bool rtOk = true;
            if (same != null && expectRt != null)
                rtOk = ReferenceEquals(t.Visual.RenderTransform, expectRt);
            Ok(squished > 1e-6 && ok && rtOk, label + " → 归位且还原变换",
               "|x-1|=" + F(squished) + " -> " + F(t.X.Value));
        }

        /* ------------------------------------------------------------ 渲染 */

        /* 只画一张静止版面存成 PNG。同一份源码、同样的 DPI，
           改动前后各跑一次，两张 PNG 必须逐字节相等 ——
           这就是「没按的时候画面一点没变、布局一点没变」的证据。 */
        static int RenderOnly(string path)
        {
            double w = 436, h = 216;
            Look look = Look.ByKey("glass");
            Art art = Art.ByKey("aurora");
            Snap snap = new Snap();

            Built built = Layout.Build("timer", "expanded", w, h, look, art, snap,
                                       new CardActions(), false, false);
            Canvas root = new Canvas();
            root.Width = w + 48;
            root.Height = h + 50;
            root.Background = Brushes.Transparent;
            Ui.At(root, built.Root, 24, 16);

            Window win = new Window();
            win.WindowStyle = WindowStyle.None;
            win.AllowsTransparency = true;
            win.Background = Brushes.Transparent;
            win.ResizeMode = ResizeMode.NoResize;
            win.ShowInTaskbar = false;
            win.Width = root.Width;
            win.Height = root.Height;
            win.Left = -4000;
            win.Top = -4000;
            win.Content = root;
            win.Show();

            Pump(240);

            RenderTargetBitmap rtb = new RenderTargetBitmap(
                (int)Math.Round(root.Width), (int)Math.Round(root.Height), 96, 96,
                PixelFormats.Pbgra32);
            rtb.Render(root);

            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                enc.Save(fs);
            }
            Console.WriteLine("[ok] rendered " + (int)root.Width + "x" + (int)root.Height
                              + " -> " + path);
            win.Close();
            return 0;
        }

        /* ------------------------------------------------------------ 工具 */

        static void Bump(Card c, string k)
        {
            if (!c.Taps.ContainsKey(k)) c.Taps[k] = 0;
            c.Taps[k] = c.Taps[k] + 1;
        }

        static int Get(Card c, string k)
        {
            return c.Taps.ContainsKey(k) ? c.Taps[k] : 0;
        }

        /* 真实路由事件。MouseLeftButtonDown/Up 的委托类型是 MouseButtonEventHandler，
           拿 MouseEventArgs 去 RaiseEvent 会抛 InvalidCastException —— 探针自己踩过，
           产品代码里没有这个问题。 */
        static void Raise(IInputElement el, RoutedEvent ev)
        {
            UIElement u = el as UIElement;
            if (u == null) return;

            if (ev == UIElement.MouseLeftButtonDownEvent || ev == UIElement.MouseLeftButtonUpEvent)
            {
                MouseButtonEventArgs mb = new MouseButtonEventArgs(Mouse.PrimaryDevice,
                    Environment.TickCount, MouseButton.Left);
                mb.RoutedEvent = ev;
                u.RaiseEvent(mb);
                return;
            }

            MouseEventArgs a = new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount);
            a.RoutedEvent = ev;
            u.RaiseEvent(a);
        }

        static void Down(IInputElement el) { Raise(el, UIElement.MouseLeftButtonDownEvent); }
        static void Up(IInputElement el) { Raise(el, UIElement.MouseLeftButtonUpEvent); }

        static Point CenterOf(FrameworkElement el, Visual root)
        {
            try
            {
                Point p = el.TransformToAncestor(root).Transform(new Point(0, 0));
                return new Point(p.X + el.ActualWidth / 2, p.Y + el.ActualHeight / 2);
            }
            catch { return new Point(double.NaN, double.NaN); }
        }

        static List<SquishPlate> FindPlates(DependencyObject root)
        {
            List<SquishPlate> found = new List<SquishPlate>();
            Walk(root, found);
            return found;
        }

        static void Walk(DependencyObject d, List<SquishPlate> found)
        {
            if (d == null) return;
            SquishPlate p = d as SquishPlate;
            if (p != null) found.Add(p);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), found);
        }

        /* 跑一段时间，期间让 Dispatcher 正常处理渲染与动画。 */
        static void Pump(double ms)
        {
            if (ms <= 0) ms = 1;
            DispatcherFrame frame = new DispatcherFrame();
            DispatcherTimer t = new DispatcherTimer(DispatcherPriority.Background);
            t.Interval = TimeSpan.FromMilliseconds(ms);
            t.Tick += delegate { t.Stop(); frame.Continue = false; };
            t.Start();
            Dispatcher.PushFrame(frame);
        }
    }
}