/* ============================================================================
   液态连接 · 隔离验证探针

   这是一条**独立入口**：不建岛、不建桌宠、不碰任何正式可视树，只在内存里
   渲染几何、量像素、写 JSON/PNG。默认启动路径完全不会走到这里。

   跑法：
     WinIsland.exe --liquid-probe [--liquid-out DIR]     （正式 exe 的验证入口）
     LiquidProbe.exe  [--out DIR]                        （build-liquid.cmd 产物）

   验什么（对应任务里点名的每一项）：

     S1  参考实现回归      —— 把 FluidKit 自己 tests/liquid/geometry.test.ts 的四个
                              用例照搬过来，确认移植没有走形。
     S2  真实对象对        —— 岛体右端弧 ↔ 消息小球，用真实常数
                              （IdleW/IdleH/PlayW/PlayH/NotifyGap/NotifyBall）算。
     S3  不同半径 / 方向    —— 4 组半径 × 8 个方向 × 3 档拉伸，断言全部有限、
                              非 NaN，且颈宽随拉伸单调不增。
     S4  滞回 / 快速反向    —— 逐帧扫过阈值再扫回来，断言状态转换恰好 2 次；
                              再做「临界正反抖动」差分实验：无滞回必然抖、有滞回必不抖。
     S5  相切 / 重叠 / 远离 / 包含 —— 四态边界各自断言，含最小颈宽门槛。
     S6  光栅 + DPI        —— 96/120/144/192 四档 DPI 渲染，量**画出来的**颈宽，
                              断言它按 dpi/96 缩放（不是细丝）、断开那一帧带内零像素
                              （无残留细丝/裂口）、且单调退出路径只断一次（无一帧闪烁）。

   判定：任何一条断言不成立，进程退出码 1，并在 JSON 里列出具体失败项。
   ============================================================================ */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinIsland
{
    static class LiquidProbe
    {
        /* ============================================================ 入口 */

        public static int Run(string outDir, Action<string> log)
        {
            if (log == null) log = delegate(string s) { };

            if (outDir == null || outDir.Length == 0)
                outDir = Path.Combine(Path.GetTempPath(), "liquid-probe");

            try { Directory.CreateDirectory(outDir); }
            catch (Exception ex)
            {
                log("[liquid] 输出目录建不出来: " + ex.Message);
                return 1;
            }

            List<string> fails = new List<string>();
            StringBuilder report = new StringBuilder();

            report.AppendLine("液态连接原型 · 隔离验证");
            report.AppendLine("输出目录: " + outDir);
            report.AppendLine();

            try
            {
                SectionS1_ReferenceRegression(fails, report);
                SectionS2_RealPair(fails, report);
                SectionS3_RadiusDirection(fails, report);
                SectionS4_Hysteresis(fails, report);
                SectionS5_Boundaries(fails, report);
                SectionS6_RasterDpi(fails, report, outDir);
            }
            catch (Exception ex)
            {
                fails.Add("探针自身抛异常: " + ex.GetType().Name + ": " + ex.Message);
                report.AppendLine("!! 探针自身抛异常: " + ex);
            }

            bool ok = fails.Count == 0;
            report.AppendLine();
            /* 把「跑了多少条断言」也写进结论。

               这是为了堵住假绿：S1 的缠绕方向断言曾经因为 `s` 恒为 null 而
               一条都没执行过，报告却照样写「全部断言通过」。以后任何一段
               断言被整体跳过，这条计数会先露馅。 */
            report.AppendLine("断言执行 " + _checks + " 条，失败 " + fails.Count + " 条");
            report.AppendLine(ok
                ? "结论: 全部断言通过"
                : ("结论: " + fails.Count + " 条断言失败"));
            for (int i = 0; i < fails.Count; i++)
                report.AppendLine("  [FAIL] " + fails[i]);

            log(report.ToString());

            try
            {
                File.WriteAllText(Path.Combine(outDir, "liquid-probe.txt"),
                                  report.ToString(), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(outDir, "liquid-probe.json"),
                                  BuildJson(ok, fails, outDir), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                log("[liquid] 报告写不出来: " + ex.Message);
                return 1;
            }

            log("[liquid] ok=" + (ok ? "true" : "false") + "  dir=" + outDir);
            return ok ? 0 : 1;
        }

        /* ------------------------------------------------------------ 断言 */

        /* 执行过的断言条数。用途见结论行那里的注释：断言被整段跳过时，
           失败数是 0，但条数会明显少于预期。 */
        static int _checks;

        static void Check(List<string> fails, bool cond, string what)
        {
            _checks++;
            if (!cond) fails.Add(what);
        }

        static void CheckNear(List<string> fails, double got, double want, double tol, string what)
        {
            _checks++;
            if (double.IsNaN(got) || Math.Abs(got - want) > tol)
                fails.Add(what + " (got " + F(got) + ", want " + F(want) + " +/-" + F(tol) + ")");
        }

        static string F(double v)
        {
            if (double.IsNaN(v)) return "NaN";
            if (double.IsInfinity(v)) return v > 0 ? "+inf" : "-inf";
            return v.ToString("0.####", CultureInfo.InvariantCulture);
        }

        /* ================================================================ S1

           参考实现自带测试的四个用例照搬，确认移植没走形。
           （对应 fluidkit/tests/liquid/geometry.test.ts 的 bridgePath 那组） */

        static void SectionS1_ReferenceRegression(List<string> fails, StringBuilder r)
        {
            r.AppendLine("[S1] 参考实现回归（FluidKit geometry.test.ts 用例）");

            LiquidBody a = new LiquidBody("a", 0, 0, 20);
            BridgeShape s;

            /* 近距用例的形状单独留一份。

               下面三条「不应该产出颈部」的用例会把 s 置成 null（TryBridge 失败时
               shape = null），所以到缠绕方向那一段时 s 必然是 null —— 原来的
               `if (s != null)` 因此恒假，两条断言从来没执行过，而报告仍然写
               「全部断言通过」。方向错了 nonzero 填充会在颈中间挖个洞，
               偏偏是最难自查的一环，所以断言必须挂在这个真的产出颈部的形状上。 */
            BridgeShape neck = null;

            /* 近距离 → 有颈 */
            bool near = LiquidGeometry.TryBridge(a, new LiquidBody("b", 45, 0, 20), 0.5,
                LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out s);
            Check(fails, near, "S1 近距两圆应当产出颈部");
            if (near)
            {
                neck = s;
                Check(fails, s.Finite(), "S1 颈部形状含 NaN");
                Check(fails, s.NeckWidth > 0, "S1 近距颈宽应当为正");
                r.AppendLine("     近距(d=45) 颈宽 = " + F(s.NeckWidth) + " DIP");
            }

            /* 超出 reach → 无颈。reach 1.6 → maxD = 40*1.6 = 64 */
            Check(fails, !LiquidGeometry.TryBridge(a, new LiquidBody("b", 100, 0, 20), 0.5,
                    LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out s),
                  "S1 超出 reach(d=100) 不应产出颈部");

            /* 包含 → 无颈 */
            Check(fails, !LiquidGeometry.TryBridge(new LiquidBody("a", 0, 0, 30),
                    new LiquidBody("b", 2, 0, 5), 0.5,
                    LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out s),
                  "S1 一圆包含另一圆时不应产出颈部");

            /* 退化半径 → 无颈 */
            Check(fails, !LiquidGeometry.TryBridge(new LiquidBody("a", 0, 0, 0.2),
                    new LiquidBody("b", 10, 0, 10), 0.5,
                    LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out s),
                  "S1 退化半径(0.2) 不应产出颈部");

            /* 缠绕方向。屏幕坐标 y 向下，p1→p3→p4→p2 实际走的是**负**向（逆时针），
               而圆盘是正向 —— 两者相反，nonzero 下会互相抵消、颈中间破洞。
               所以这里的契约不是「面积必须为正」，而是：
                 · 面积非零（四点多边形真的成环）；
                 · BridgeNeedsReverse 恰好在这个符号上触发（归一化接对了线）。
               真正的「不挖洞」由下面那条 nonzero 并集断言负责 —— 那才是用户
               能看见的东西。 */
            if (neck != null)
            {
                double area = LiquidGeometry.SignedArea(new Point[] { neck.P1, neck.P3, neck.P4, neck.P2 });
                Check(fails, Math.Abs(area) > 1.0,
                      "S1 颈部轮廓面积不应为 0（四点没成环）");
                Check(fails, LiquidGeometry.BridgeNeedsReverse(neck) == (area < 0),
                      "S1 缠绕方向归一化应当与鞋带面积符号一致");
                r.AppendLine("     缠绕面积 = " + F(area)
                             + "（负 = 与圆盘反向，AppendBridge 会自动反向）");
            }
            else
            {
                /* 近距都没产出颈部的话上面已经记过失败了；这里再明确记一条，
                   免得「没形状 → 整段跳过」又被当成通过。 */
                fails.Add("S1 近距用例没有形状，缠绕方向断言无法执行");
            }

            /* 并集无洞：体和颈合成一个 nonzero 几何后，两圆心连线上的点必须都在里面。
               这条直接对应「不能有裂口」—— 缝在 nonzero 并集里会表现为 FillContains=false。 */
            LiquidTension.Field f = new LiquidTension.Field();
            f.IncludeBodies = true;
            LiquidBody ba = new LiquidBody("a", 0, 0, 20);
            LiquidBody bb = new LiquidBody("b", 40 * 0.99, 0, 20);
            Geometry cov = f.Coverage(new LiquidBody[] { ba, bb });
            bool solid = true;
            for (int k = 1; k <= 19; k++)
            {
                Point p = new Point(40 * 0.99 * k / 20.0, 0);
                if (!cov.FillContains(p)) solid = false;
            }
            Check(fails, solid, "S1 体 + 颈的 nonzero 并集在轴线上不应有洞（裂口）");
            r.AppendLine("     轴线并集连续 = " + (solid ? "是" : "否"));

            /* IncludeBodies 的回归：曾经这里 Bridges() 把 withBodies 写死成 false，
               于是「体 + 颈」的几何里其实只有颈，证据图上也只剩中间那一段。
               断言「没有颈的时候，带体的几何也必须是满的两个圆盘」把这条路堵死。 */
            LiquidTension.Field fFar = new LiquidTension.Field();
            fFar.IncludeBodies = true;
            Geometry farGeo = fFar.Bridges(new LiquidBody[]
            {
                new LiquidBody("a", 0, 0, 20),
                new LiquidBody("b", 200, 0, 20)
            });
            Check(fails, !ReferenceEquals(farGeo, Geometry.Empty),
                  "S1 IncludeBodies=true 时即使没有颈也应当产出体的几何");
            Check(fails, farGeo.FillContains(new Point(-10, 0))
                      && farGeo.FillContains(new Point(190, 0)),
                  "S1 IncludeBodies=true 时两个圆盘内部应当被填充");
            r.AppendLine();
        }

        /* ================================================================ S2

           真实对象对：岛体右端弧 ↔ 消息小球。

           **为什么选这一对**（选择原因，也是结论的一部分）：

             · 几何是精确的，不是近似的。岛体圆角半径 r = min(30, h/2)；idle 的
               h = 30、第二层 h = 38，两种情况下 r 都恰好等于 h/2 —— 此时岛体右端
               **整段就是一个正半圆**，圆心 (w - r, h/2)。消息小球挂在同一水平中线上，
               于是这一对严格是两个圆，正是参考算法适用的形状，不需要任何近似。
             · 量全是编译期常量（IdleW/IdleH/PlayW/PlayH/NotifyGap/NotifyBall），
               没有运行时测量 —— 探针量的和正式代码用的是同一组数。
             · 它是本工程里唯一「一个圆刻意与一块体之间留了一条显式缝隙」的地方
               （NotifyGap = 10 DIP），也就是唯一真正需要连接颈的地方。
             · 它不涉及玻璃材质、不涉及桌宠动画，符合「一次只动一处」的约束。 */

        static void SectionS2_RealPair(List<string> fails, StringBuilder r)
        {
            r.AppendLine("[S2] 真实对象对：岛体右端弧 ↔ 消息小球");

            double ballR = LiquidPair.NotifyBall / 2;
            double gap = LiquidPair.NotifyGap;
            r.AppendLine("     小球 r2 = " + F(ballR) + "，显式缝隙 = " + F(gap) + " DIP");

            /* --- idle 52×30 --- */
            double rIdle = LiquidPair.IslandRadius(LiquidPair.IdleH);
            bool idleIsArc = LiquidPair.RightEndIsArc(LiquidPair.IdleH);
            double stIdle = LiquidPair.RestStretch(LiquidPair.IdleH);
            r.AppendLine("     idle    : h=" + F(LiquidPair.IdleH) + " 岛端弧 r1=" + F(rIdle)
                         + "  右端是正半圆=" + (idleIsArc ? "是" : "否")
                         + "  stretch=" + F(stIdle)
                         + "  (断开阈值 " + F(LiquidTension.SnapStretch) + ")");
            Check(fails, idleIsArc, "S2 idle 的岛端应当正好是半圆（r == h/2），算法才精确适用");
            Check(fails, stIdle > LiquidTension.SnapStretch,
                  "S2 idle 静止 stretch 应当超过断开阈值");

            /* --- 第二层 112×38 --- */
            double rPlay = LiquidPair.IslandRadius(LiquidPair.PlayH);
            bool playIsArc = LiquidPair.RightEndIsArc(LiquidPair.PlayH);
            double stPlay = LiquidPair.RestStretch(LiquidPair.PlayH);
            r.AppendLine("     playing : h=" + F(LiquidPair.PlayH) + " 岛端弧 r1=" + F(rPlay)
                         + "  右端是正半圆=" + (playIsArc ? "是" : "否")
                         + "  stretch=" + F(stPlay));
            Check(fails, playIsArc, "S2 第二层的岛端也应当是半圆");
            Check(fails, stPlay < LiquidTension.SnapStretch,
                  "S2 第二层静止 stretch 应当在断开阈值以内");

            /* 这一对静止时天生就横跨在临界距离上：算出来给报告用 */
            double dIdle = (rIdle + ballR) + gap;
            double dPlay = (rPlay + ballR) + gap;
            double snapIdleD = LiquidTension.SnapStretch * (rIdle + ballR);
            double snapPlayD = LiquidTension.SnapStretch * (rPlay + ballR);
            r.AppendLine("     idle    距断开线 " + F(dIdle - snapIdleD)
                         + " DIP（正 = 已断；只差这么一点）");
            r.AppendLine("     playing 距断开线 " + F(dPlay - snapPlayD)
                         + " DIP（负 = 仍连着）");

            /* 临界岛端弧半径：1 + gap/(r+ballR) = SnapStretch → r* = gap/(Snap-1) - ballR */
            double rStar = gap / (LiquidTension.SnapStretch - 1.0) - ballR;
            r.AppendLine("     临界岛端弧半径 r* = " + F(rStar)
                         + " DIP  →  r<r* 断，r>r* 连");
            Check(fails, rStar > rIdle && rStar < rPlay,
                  "S2 idle 与第二层应当分别落在临界半径两侧（这是冲突的量化依据）");

            /* --- 用真实数字跑场：滑出动画那种「先重叠、再退开」 --- */
            LiquidTension.Field f = new LiquidTension.Field();
            LiquidBody isl = new LiquidBody("island", rIdle, 0, rIdle);
            LiquidBody ball = new LiquidBody("ball", rIdle + dIdle, 0, ballR);
            LiquidBody[] pair = new LiquidBody[] { isl, ball };

            Geometry g0 = f.Bridges(pair);
            Check(fails, ReferenceEquals(g0, Geometry.Empty),
                  "S2 idle 静止位在「从未连过」时不应产出颈部");

            /* 喂到重叠（stretch 0.95）：这就是球从岛后滑出时必然经过的状态 */
            ball.X = rIdle + (rIdle + ballR) * 0.95;
            f.Bridges(pair);
            r.AppendLine("     经过重叠后 ConnectedPairs = " + f.ConnectedPairs
                         + "  颈宽 = " + F(f.LastNeckWidth));

            /* 逐帧退回静止位 */
            bool heldIdle = false;
            double wIdle = double.NaN;
            for (int i = 0; i <= 80; i++)
            {
                double dd = (rIdle + ballR) * 0.95
                          + ((dIdle - (rIdle + ballR) * 0.95) * i / 80.0);
                ball.X = rIdle + dd;
                f.Bridges(pair);
                heldIdle = f.IsConnected(isl, ball);
                wIdle = f.LastNeckWidth;
            }
            r.AppendLine("     idle 退到静止位仍连着? " + (heldIdle ? "是" : "否")
                         + "  最后颈宽 = " + F(wIdle));
            Check(fails, !heldIdle,
                  "S2 idle 退到静止位时应已断开（不挂残颈）");

            /* 第二层：同样流程，但静止位在阈值内 → 会**永久**挂一根颈 */
            LiquidTension.Field fp = new LiquidTension.Field();
            LiquidBody islP = new LiquidBody("island", rPlay, 0, rPlay);
            LiquidBody ballP = new LiquidBody("ball", rPlay + dPlay, 0, ballR);
            LiquidBody[] pairP = new LiquidBody[] { islP, ballP };
            ballP.X = rPlay + (rPlay + ballR) * 0.95;
            fp.Bridges(pairP);
            ballP.X = rPlay + dPlay;
            double wPlay = double.NaN;
            for (int i = 0; i < 4; i++) { fp.Bridges(pairP); wPlay = fp.LastNeckWidth; }
            bool heldPlay = fp.IsConnected(islP, ballP);
            r.AppendLine("     playing 静止位仍连着? " + (heldPlay ? "是" : "否")
                         + "  颈宽 = " + F(wPlay) + " DIP");
            /* 这条是**冲突证据**，不是缺陷：第二层下静止位会永久挂一根颈，
               把小球的 NotifyGap 缝隙填掉。断言它确实发生，报告才有据可依。 */
            Check(fails, heldPlay,
                  "S2 第二层静止位应当仍连着（这是「永久挂颈」冲突的证据；变了说明常数改了）");
            r.AppendLine();
        }

        /* ================================================================ S3

           不同半径 × 不同方向 × 三档拉伸。断言：全部有限、不 NaN、颈宽单调。 */

        static void SectionS3_RadiusDirection(List<string> fails, StringBuilder r)
        {
            r.AppendLine("[S3] 不同半径 / 不同方向矩阵");

            double[][] radii = new double[][]
            {
                new double[] { 15, 17 },   /* 真实那一对 */
                new double[] { 20, 20 },   /* 等径 */
                new double[] { 8, 30 },    /* 悬殊 */
                new double[] { 2.5, 1.5 }  /* 小球 */
            };
            double[] stretches = new double[] { 0.90, 1.00, 1.15 };

            int cases = 0;
            int finite = 0;

            for (int ri = 0; ri < radii.Length; ri++)
            {
                double r1 = radii[ri][0];
                double r2 = radii[ri][1];
                for (int di = 0; di < 8; di++)
                {
                    double ang = di * Math.PI / 4.0;
                    for (int si = 0; si < stretches.Length; si++)
                    {
                        double st = stretches[si];
                        double d = (r1 + r2) * st;
                        LiquidBody a = new LiquidBody("a", 0, 0, r1);
                        LiquidBody b = new LiquidBody("b",
                            Math.Cos(ang) * d, Math.Sin(ang) * d, r2);

                        BridgeShape s;
                        bool got = LiquidGeometry.TryBridge(a, b, LiquidTension.NeckWaist(st),
                            LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out s);
                        cases++;
                        if (!got) continue;
                        finite++;
                        Check(fails, s.Finite(),
                              "S3 出现 NaN/Inf  r=" + F(r1) + "/" + F(r2)
                              + " dir=" + (di * 45) + "deg stretch=" + F(st));
                        if (s.Finite())
                            Check(fails, s.NeckWidth > 0,
                                  "S3 颈宽应当为正  r=" + F(r1) + "/" + F(r2)
                                  + " dir=" + (di * 45) + "deg");
                    }
                }
            }
            r.AppendLine("     用例 " + cases + " 个，产出颈部 " + finite + " 个");
            Check(fails, finite > cases / 2,
                  "S3 大多数用例应当产出颈部（少于一半说明退化了）");

            /* 单调性：stretch 从 0.95 增到 1.30，颈宽必须单调不增。

               为什么下限取 0.95 而不是更小：再往里就接近「一个圆被另一个包住」的
               退化边界，那时参考算法给出的形状本身就在塌缩（并集早就是一个体，
               颈这个概念已经不成立）。滞回真正会用到的区间是
               [ConnectStretch, SnapStretch) = [1.02, 1.30)，全在下面这段里，
               所以「不会越拉越粗」这条在会用到的范围内是成立的。 */
            for (int ri = 0; ri < 3; ri++)
            {
                double r1 = radii[ri][0];
                double r2 = radii[ri][1];
                double prev = double.MaxValue;
                int breaks = 0;
                for (int k = 0; k <= 40; k++)
                {
                    double st = 0.95 + (1.30 - 0.95) * k / 40.0;
                    double d = (r1 + r2) * st;
                    LiquidBody a = new LiquidBody("a", 0, 0, r1);
                    LiquidBody b = new LiquidBody("b", d, 0, r2);
                    BridgeShape s;
                    if (!LiquidGeometry.TryBridge(a, b, LiquidTension.NeckWaist(st),
                            LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out s))
                        continue;
                    if (s.NeckWidth > prev + 1e-6) breaks++;
                    prev = s.NeckWidth;
                }
                Check(fails, breaks == 0,
                      "S3 颈宽应当随拉伸单调不增 r=" + F(r1) + "/" + F(r2)
                      + " (违反 " + breaks + " 次)");
            }
            r.AppendLine();
        }

        /* ================================================================ S4

           滞回：a) 语义转换次数；b) 临界抖动差分实验。 */

        static void SectionS4_Hysteresis(List<string> fails, StringBuilder r)
        {
            r.AppendLine("[S4] 滞回 / 快速反向移动");

            double r1 = 20, r2 = 20;
            double sum = r1 + r2;
            LiquidBody a = new LiquidBody("a", 0, 0, r1);
            LiquidBody b = new LiquidBody("b", sum * 1.5, 0, r2);
            LiquidTension.Field f = new LiquidTension.Field();
            LiquidBody[] pair = new LiquidBody[] { a, b };

            int transitions = 0;
            bool prev = false;
            bool first = true;

            for (int k = 0; k <= 2000; k++)
            {
                double st = 1.50 - (1.50 - 0.60) * k / 2000.0;
                b.X = sum * st;
                f.Bridges(pair);
                bool now = f.IsConnected(a, b);
                if (!first && now != prev) transitions++;
                prev = now;
                first = false;
            }
            int afterDown = transitions;

            for (int k = 0; k <= 2000; k++)
            {
                double st = 0.60 + (1.50 - 0.60) * k / 2000.0;
                b.X = sum * st;
                f.Bridges(pair);
                bool now = f.IsConnected(a, b);
                if (now != prev) transitions++;
                prev = now;
            }

            r.AppendLine("     由远及近转换 " + afterDown + " 次，往返总转换 " + transitions + " 次");
            Check(fails, afterDown == 1, "S4 由远及近应当恰好连上 1 次");
            Check(fails, transitions == 2, "S4 往返一趟应当恰好 2 次状态转换");

            /* 实测连接点 / 断开点 */
            LiquidTension.Field f2 = new LiquidTension.Field();
            b.X = sum * 1.5;
            f2.Bridges(pair);
            double connectAt = double.NaN;
            for (int k = 0; k <= 4000; k++)
            {
                double st = 1.50 - (1.50 - 0.60) * k / 4000.0;
                b.X = sum * st;
                f2.Bridges(pair);
                if (f2.IsConnected(a, b)) { connectAt = st; break; }
            }
            double snapAt = double.NaN;
            for (int k = 0; k <= 4000; k++)
            {
                double st = 0.90 + (1.50 - 0.90) * k / 4000.0;
                b.X = sum * st;
                f2.Bridges(pair);
                if (!f2.IsConnected(a, b)) { snapAt = st; break; }
            }
            r.AppendLine("     实测连接 stretch≈" + F(connectAt)
                         + "（阈值 " + F(LiquidTension.ConnectStretch) + "）"
                         + "  实测断开 stretch≈" + F(snapAt)
                         + "（阈值 " + F(LiquidTension.SnapStretch) + "）");
            Check(fails, !double.IsNaN(connectAt)
                  && Math.Abs(connectAt - LiquidTension.ConnectStretch) < 0.01,
                  "S4 实测连接点应贴近 CONNECT_STRETCH");
            Check(fails, !double.IsNaN(snapAt)
                  && snapAt >= LiquidTension.SnapStretch - 0.02,
                  "S4 实测断开点不应早于 SNAP_STRETCH");

            /* --- 差分实验：临界距离上正反抖动 240 帧 ---

               两个子用例，测的是两件不同的事：
                 (a) 抖动幅度**跨过**断开线：滞回允许断一次（那是真实退出），
                     但断完之后不许再翻回来 —— 也就是翻转次数 <= 1。
                     无滞回对照在这个带里会疯狂开关。
                 (b) 抖动幅度**完全落在**滞回带内：必须一次都不翻。 */
            double snapDist = LiquidTension.SnapStretch * sum;
            int naiveFlips = 0;
            int fieldFlips = 0;

            LiquidTension.Field f3 = new LiquidTension.Field();
            b.X = sum * 0.95;
            f3.Bridges(pair);
            Check(fails, f3.IsConnected(a, b), "S4 先接近到 stretch 0.95 时应当连上");

            bool fieldPrev = true;
            bool naivePrev = true;
            for (int k = 0; k < 240; k++)
            {
                double jitter = 0.5 * Math.Sin(k * 1.7);
                b.X = snapDist + jitter;

                f3.Bridges(pair);
                bool now = f3.IsConnected(a, b);
                if (now != fieldPrev) fieldFlips++;
                fieldPrev = now;

                bool naive = (b.X / sum) < LiquidTension.SnapStretch;
                if (naive != naivePrev) naiveFlips++;
                naivePrev = naive;
            }
            r.AppendLine("     (a) 跨断开线抖动 240 帧: 有滞回翻转 " + fieldFlips
                         + " 次，无滞回对照翻转 " + naiveFlips + " 次");
            Check(fails, naiveFlips >= 10,
                  "S4 对照实验应当剧烈抖动（否则该实验没有鉴别力）");
            Check(fails, fieldFlips <= 1,
                  "S4 跨线抖动最多只应断一次（断完不许再翻回来，否则就是一帧闪烁）");

            /* (b) 抖动整体落在带内 → 必须 0 次翻转 */
            LiquidTension.Field f4 = new LiquidTension.Field();
            b.X = sum * 0.95;
            f4.Bridges(pair);
            Check(fails, f4.IsConnected(a, b), "S4 (b) 前置连接失败");

            int bandFlips = 0;
            bool bandPrev = true;
            double bandCenter = LiquidTension.SnapStretch * sum - 0.6;  /* 41.0，离断开线 0.6 */
            for (int k = 0; k < 240; k++)
            {
                double jitter = 0.5 * Math.Sin(k * 1.3);
                b.X = bandCenter + jitter;
                f4.Bridges(pair);
                bool now = f4.IsConnected(a, b);
                if (now != bandPrev) bandFlips++;
                bandPrev = now;
            }
            r.AppendLine("     (b) 带内抖动 240 帧: 翻转 " + bandFlips + " 次（应为 0）");
            Check(fails, bandFlips == 0,
                  "S4 抖动完全落在滞回带内时不应有任何翻转");

            /* --- 快速反向移动：在两档之间来回跳，模拟拖拽时手抖 --- */
            LiquidTension.Field f5 = new LiquidTension.Field();
            b.X = sum * 0.95;
            f5.Bridges(pair);
            int reverseFlips = 0;
            bool reversePrev = true;
            for (int k = 0; k < 200; k++)
            {
                b.X = sum * (k % 2 == 0 ? 1.05 : 1.25);
                f5.Bridges(pair);
                bool now = f5.IsConnected(a, b);
                if (now != reversePrev) reverseFlips++;
                reversePrev = now;
            }
            r.AppendLine("     快速反向 (1.05 <-> 1.25) x200: 翻转 " + reverseFlips + " 次（应为 0）");
            Check(fails, reverseFlips == 0,
                  "S4 快速反向移动落在带内时不应翻转");
            r.AppendLine();
        }

        /* ================================================================ S5 */

        static void SectionS5_Boundaries(List<string> fails, StringBuilder r)
        {
            r.AppendLine("[S5] 相切 / 重叠 / 远离 / 包含");

            double r1 = 20, r2 = 20;
            LiquidBody a = new LiquidBody("a", 0, 0, r1);
            BridgeShape s;

            /* 相切：stretch 恰好 1.0 → 在连接阈值内，应当有颈，且远粗于细丝 */
            LiquidBody tan = new LiquidBody("b", r1 + r2, 0, r2);
            bool okTan = LiquidGeometry.TryBridge(a, tan, LiquidTension.NeckWaist(1.0),
                LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out s);
            Check(fails, okTan, "S5 相切时应当产出颈部");
            if (okTan && s != null)
            {
                r.AppendLine("     相切 颈宽 = " + F(s.NeckWidth) + " DIP");
                Check(fails, s.NeckWidth >= LiquidTension.DefaultMinNeckWidth,
                      "S5 相切颈宽应不低于最小颈宽（否则一接上就是细丝）");
            }

            /* 重叠 10% → 仍有颈 */
            Check(fails, LiquidGeometry.TryBridge(a, new LiquidBody("b", (r1 + r2) * 0.90, 0, r2),
                    LiquidTension.NeckWaist(0.90),
                    LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out s),
                  "S5 重叠时应当产出颈部");

            /* 远离 → 场不画。

               注意这里要分行验证，两层职责不同：
                 · 几何层 TryBridge 到 reach(1.6) 为止**仍然能**画出来 —— 这是必须的，
                   否则滞回带 [1.02, 1.30) 里的颈根本画不出来，滞回也就无从谈起；
                 · 场层才是那个说「不画」的地方（它按 snap 阈值判）。
               把这两层混在一起断言，会把「正确的分层」误判成 bug。 */
            Check(fails, LiquidGeometry.TryBridge(a, new LiquidBody("b", (r1 + r2) * 1.35, 0, r2),
                    LiquidTension.NeckWaist(1.35),
                    LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out s),
                  "S5 几何层在 reach 内应当能画出颈（滞回带全靠这一点）");

            LiquidTension.Field fFar = new LiquidTension.Field();
            LiquidBody fa = new LiquidBody("a", 0, 0, r1);
            LiquidBody fb = new LiquidBody("b", (r1 + r2) * 1.35, 0, r2);
            Check(fails, ReferenceEquals(fFar.Bridges(new LiquidBody[] { fa, fb }), Geometry.Empty),
                  "S5 远离(stretch 1.35) 时场不应产出颈部");

            /* 包含 → 无颈（并集本来就是一个体） */
            Check(fails, !LiquidGeometry.TryBridge(new LiquidBody("a", 0, 0, 40),
                    new LiquidBody("b", 3, 0, 6), LiquidTension.NeckWaist(0.1),
                    LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out s),
                  "S5 包含时不应产出颈部");

            /* 最小颈宽门槛：小球对几何上算得出来，但颈细于门槛 → 场必须拒绝画 */
            LiquidTension.Field f = new LiquidTension.Field();
            LiquidBody t1 = new LiquidBody("a", 0, 0, 2.5);
            LiquidBody t2 = new LiquidBody("b", 3.5, 0, 1.5);
            double stTiny = 3.5 / (2.5 + 1.5);
            BridgeShape raw;
            bool possible = LiquidGeometry.TryBridge(t1, t2, LiquidTension.NeckWaist(stTiny),
                LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out raw);
            Geometry gTiny = f.Bridges(new LiquidBody[] { t1, t2 });
            r.AppendLine("     小球 2.5/1.5: 几何可算=" + (possible ? "是" : "否")
                         + "  原始颈宽=" + F(raw == null ? double.NaN : raw.NeckWidth)
                         + "  门槛=" + F(f.MinNeckWidth)
                         + "  场是否画=" + (ReferenceEquals(gTiny, Geometry.Empty) ? "否" : "是"));
            if (possible && raw != null && raw.NeckWidth < f.MinNeckWidth)
                Check(fails, ReferenceEquals(gTiny, Geometry.Empty),
                      "S5 颈宽低于门槛时场必须不画（否则就是一条细丝）");

            /* 有效断开距离：门槛把断开点往内推。

               r=2.5/1.5 这一对的期望要写准：它在几何上**能**画颈（量到 2.7 DIP，
               刚过 2.0 门槛），但能连上的区间被压得非常窄 —— 有效断开点 0.9985
               已经低于 CONNECT_STRETCH(1.02)，也就是「只有几乎整个重叠进去才连」。
               真要被判成「永不连接」需要再小一档（见下一组 1.0/0.8）。 */
            double es1 = f.EffectiveSnapStretch(20, 20);
            double es2 = f.EffectiveSnapStretch(15, 17);
            double es3 = f.EffectiveSnapStretch(2.5, 1.5);
            double es4 = f.EffectiveSnapStretch(1.0, 0.8);
            r.AppendLine("     有效断开 stretch: 20/20 → " + F(es1)
                         + "；15/17 → " + F(es2)
                         + "；2.5/1.5 → " + F(es3)
                         + "；1.0/0.8 → " + F(es4));
            Check(fails, !double.IsNaN(es1) && es1 <= LiquidTension.SnapStretch + 1e-9,
                  "S5 有效断开点不应超过 SNAP_STRETCH");
            Check(fails, !double.IsNaN(es2) && es2 <= LiquidTension.SnapStretch + 1e-9,
                  "S5 真实半径的有效断开点不应超过 SNAP_STRETCH");
            Check(fails, !double.IsNaN(es3) && es3 < LiquidTension.ConnectStretch,
                  "S5 2.5/1.5 小球对的有效断开点应当被门槛压到连接阈值以内");
            Check(fails, double.IsNaN(es4),
                  "S5 1.0/0.8 过小的一对应当被判为「永不连接」（够不到最小颈宽）");
            r.AppendLine();
        }

        /* ================================================================ S6 */

        static void SectionS6_RasterDpi(List<string> fails, StringBuilder r, string outDir)
        {
            r.AppendLine("[S6] 光栅 + DPI（96 / 120 / 144 / 192）");

            double r1 = 20, r2 = 20;
            double[] dpis = new double[] { 96, 120, 144, 192 };
            double dipW = 200, dipH = 120;
            double cy = dipH / 2;

            double[] measured = new double[dpis.Length];
            double[] analytic = new double[dpis.Length];

            for (int i = 0; i < dpis.Length; i++)
            {
                double dpi = dpis[i];

                /* 版心：两体的距离由 stretch 决定，整体水平居中 */
                double dOn = (r1 + r2) * 1.15;
                double cxA = (dipW - dOn) / 2;
                double cxB = cxA + dOn;

                LiquidTension.Field f = new LiquidTension.Field();
                f.IncludeBodies = true;
                LiquidBody a = new LiquidBody("a", cxA, cy, r1);
                LiquidBody b = new LiquidBody("b", cxB, cy, r2);
                LiquidBody[] pair = new LiquidBody[] { a, b };

                /* 先靠近（建立连接），再退到滞回带内的目标位置 */
                b.X = cxA + (r1 + r2) * 0.95;
                f.Bridges(pair);
                b.X = cxB;

                Geometry gOn = f.Bridges(pair);
                Check(fails, !ReferenceEquals(gOn, Geometry.Empty),
                      "S6 dpi " + F(dpi) + ": 滞回带内应当仍连着");

                Frame on = Render(gOn, dipW, dipH, dpi);
                int x0 = on.PxOf(a.X + a.R);
                int x1 = on.PxOf(b.X - b.R);
                double total;
                double minRun = on.MinVerticalRun(x0, x1, out total);
                measured[i] = minRun;

                BridgeShape sh;
                LiquidGeometry.TryBridge(a, b, LiquidTension.NeckWaist(1.15),
                    LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out sh);
                analytic[i] = sh == null ? double.NaN : sh.NeckWidth;
                double wantPx = analytic[i] * dpi / 96.0;

                r.AppendLine("     dpi " + F(dpi) + ": 颈宽 解析=" + F(analytic[i])
                             + " DIP → 期望 " + F(wantPx) + "px  实测 " + F(minRun) + "px");

                Check(fails, minRun > 0, "S6 dpi " + F(dpi) + ": 连着时带内应有像素");
                Check(fails, Math.Abs(minRun - wantPx) <= Math.Max(2.5, wantPx * 0.30),
                      "S6 dpi " + F(dpi) + ": 实测颈宽应接近解析值");
                Check(fails, minRun >= LiquidTension.DefaultMinNeckWidth * dpi / 96.0 - 2.0,
                      "S6 dpi " + F(dpi) + ": 画出来的颈不应细于最小颈宽");

                /* --- 断开的**最后一帧**：颈必须还很粗（掐断，不是拉成丝） --- */
                LiquidTension.Field fb = new LiquidTension.Field();
                LiquidBody ab = new LiquidBody("a", cxA, cy, r1);
                LiquidBody bb = new LiquidBody("b", cxA, cy, r2);
                LiquidBody[] pairB = new LiquidBody[] { ab, bb };
                bb.X = cxA + (r1 + r2) * 0.95;
                fb.Bridges(pairB);                          /* 连上 */
                bb.X = cxA + (r1 + r2) * 1.298;             /* 逼近断开线但还没断 */
                Geometry gLast = fb.Bridges(pairB);
                Check(fails, !ReferenceEquals(gLast, Geometry.Empty),
                      "S6 dpi " + F(dpi) + ": 断开线前一帧应当仍连着");

                Frame last = Render(gLast, dipW, dipH, dpi);
                double tLast;
                double lastRun = last.MinVerticalRun(last.PxOf(ab.X + ab.R),
                                                     last.PxOf(bb.X - bb.R), out tLast);
                r.AppendLine("            断开前一帧带内最细 " + F(lastRun) + "px"
                             + "（门槛 " + F(LiquidTension.DefaultMinNeckWidth * dpi / 96.0)
                             + "px）");
                Check(fails, lastRun >= LiquidTension.DefaultMinNeckWidth * dpi / 96.0 - 2.0,
                      "S6 dpi " + F(dpi) + ": 断开的最后一帧颈不应细成丝");

                /* --- 越过断开线：同一帧里必须彻底没有（不留残影/裂口） --- */
                bb.X = cxA + (r1 + r2) * 1.302;
                fb.IncludeBodies = false;
                Geometry gOff = fb.Bridges(pairB);
                Check(fails, ReferenceEquals(gOff, Geometry.Empty),
                      "S6 dpi " + F(dpi) + ": 越过断开阈值后应是空几何");

                Frame off = Render(gOff, dipW, dipH, dpi);
                int residual = off.CountInBand(off.PxOf(ab.X + ab.R), off.PxOf(bb.X - bb.R));
                r.AppendLine("            断开帧带内像素 = " + residual + "（应为 0）");
                Check(fails, residual == 0,
                      "S6 dpi " + F(dpi) + ": 断开后两体之间不应残留任何像素（细丝/裂口）");

                /* 反证这一带确实能被检测到：只画颈（不画体）时，同一条带里必须有像素。
                   否则上面那个 residual==0 就是空几何给的假绿。 */
                LiquidTension.Field fRe = new LiquidTension.Field();
                fRe.IncludeBodies = false;
                LiquidBody ar = new LiquidBody("a", cxA, cy, r1);
                LiquidBody br = new LiquidBody("b", cxB, cy, r2);
                LiquidBody[] pairR = new LiquidBody[] { ar, br };
                br.X = cxA + (r1 + r2) * 0.95;
                fRe.Bridges(pairR);
                br.X = cxB;
                Geometry bare = fRe.Bridges(pairR);
                Frame bareF = Render(bare, dipW, dipH, dpi);
                int barePix = bareF.CountInBand(bareF.PxOf(ar.X + ar.R),
                                               bareF.PxOf(br.X - br.R));
                Check(fails, barePix > 0,
                      "S6 dpi " + F(dpi) + ": 反证失败——只画颈时同一条带里应当有像素");
            }

            /* DPI 缩放：实测像素宽按 dpi/96 线性放大 */
            double basePx = measured[0];
            for (int i = 1; i < dpis.Length; i++)
            {
                double ratio = dpis[i] / 96.0;
                CheckNear(fails, measured[i] / Math.Max(1e-9, basePx), ratio, 0.15,
                          "S6 DPI 缩放下颈宽应按比例放大（" + F(dpis[i]) + "）");
            }

            /* 单调退出路径只断一次（无一帧闪烁） */
            {
                LiquidTension.Field f = new LiquidTension.Field();
                LiquidBody a = new LiquidBody("a", 60, cy, r1);
                LiquidBody b = new LiquidBody("b", 60, cy, r2);
                LiquidBody[] pair = new LiquidBody[] { a, b };
                b.X = 60 + (r1 + r2) * 0.95;
                f.Bridges(pair);

                int flips = 0;
                bool prev = true;
                int frames = 120;
                for (int k = 0; k <= frames; k++)
                {
                    double st = 0.95 + (1.45 - 0.95) * k / frames;
                    b.X = 60 + (r1 + r2) * st;
                    f.Bridges(pair);
                    bool now = f.IsConnected(a, b);
                    if (now != prev) flips++;
                    prev = now;
                }
                r.AppendLine("     单调退出 121 帧内状态翻转 " + flips + " 次（应为 1）");
                Check(fails, flips == 1,
                      "S6 单调退出路径上应当只断一次（多于 1 次即一帧闪烁）");
            }

            WriteEvidencePngs(fails, r, outDir);
            r.AppendLine();
        }

        /* ---------------------------------------------------------- 量像素 */

        sealed class Frame
        {
            public byte[] Px;
            public int W;
            public int H;
            public double Dpi;

            public int PxOf(double dipX)
            {
                int x = (int)Math.Round(dipX * Dpi / 96.0);
                if (x < 0) x = 0;
                if (x > W - 1) x = W - 1;
                return x;
            }

            public bool On(int x, int y)
            {
                return Px[(y * W + x) * 4 + 3] > 40;
            }

            public int CountInBand(int x0, int x1)
            {
                int n = 0;
                for (int x = x0; x < x1 && x < W; x++)
                    for (int y = 0; y < H; y++)
                        if (On(x, y)) n++;
                return n;
            }

            public double MinVerticalRun(int x0, int x1, out double total)
            {
                total = 0;
                double best = double.MaxValue;
                for (int x = x0; x <= x1 && x < W; x++)
                {
                    int n = 0;
                    for (int y = 0; y < H; y++) if (On(x, y)) n++;
                    total += n;
                    if (n > 0 && n < best) best = n;
                }
                return best == double.MaxValue ? 0 : best;
            }
        }

        static Frame Render(Geometry g, double dipW, double dipH, double dpi)
        {
            int pw = (int)Math.Ceiling(dipW * dpi / 96.0);
            int ph = (int)Math.Ceiling(dipH * dpi / 96.0);

            DrawingVisual v = new DrawingVisual();
            using (DrawingContext dc = v.RenderOpen())
            {
                Geometry draw = (g == null) ? Geometry.Empty : g;
                dc.DrawGeometry(Brushes.White, null, draw);
            }
            RenderTargetBitmap rtb = new RenderTargetBitmap(pw, ph, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(v);

            Frame f = new Frame();
            f.W = pw;
            f.H = ph;
            f.Dpi = dpi;
            f.Px = new byte[pw * ph * 4];
            rtb.CopyPixels(f.Px, pw * 4, 0);
            return f;
        }

        /* ---------------------------------------------------------- 证据图 */

        static void WriteEvidencePngs(List<string> fails, StringBuilder r, string outDir)
        {
            try
            {
                /* 1) 矩阵图：3 组半径 × 8 个方向 */
                double[] st = new double[] { 0.90, 1.00, 1.15 };
                double[][] radii = new double[][]
                {
                    new double[] { 15, 17 },
                    new double[] { 20, 20 },
                    new double[] { 8, 30 }
                };

                int cols = 8;
                int rows = 3;
                double tile = 92, pad = 8, gapX = 8, gapY = 22;
                double sheetW = cols * (tile + gapX) + pad * 2;
                double sheetH = rows * (tile + gapY) + pad * 2 + 20;

                DrawingVisual v = new DrawingVisual();
                using (DrawingContext dc = v.RenderOpen())
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1A)), null,
                                     new Rect(0, 0, sheetW, sheetH));

                    for (int ri = 0; ri < rows; ri++)
                    {
                        for (int di = 0; di < cols; di++)
                        {
                            double r1 = radii[ri][0];
                            double r2 = radii[ri][1];
                            double ang = di * Math.PI / 4.0;
                            double d = (r1 + r2) * st[ri];
                            LiquidBody a = new LiquidBody("a", 0, 0, r1);
                            LiquidBody b = new LiquidBody("b", Math.Cos(ang) * d,
                                                          Math.Sin(ang) * d, r2);

                            LiquidTension.Field f = new LiquidTension.Field();
                            f.IncludeBodies = true;
                            /* 先靠近再退开，让滞回生效（矩阵要展示的是「有颈的样子」） */
                            b.X *= 0.95; b.Y *= 0.95;
                            f.Bridges(new LiquidBody[] { a, b });
                            b.X = Math.Cos(ang) * d; b.Y = Math.Sin(ang) * d;
                            Geometry g = f.Bridges(new LiquidBody[] { a, b });

                            double span = (r1 + r2) * 2.4 + 12;
                            double scale = tile / span;
                            double cx = pad + di * (tile + gapX) + tile / 2;
                            double cyTile = pad + 20 + ri * (tile + gapY) + tile / 2;

                            TransformGroup tg = new TransformGroup();
                            tg.Children.Add(new ScaleTransform(scale, scale));
                            tg.Children.Add(new TranslateTransform(cx, cyTile));
                            dc.PushTransform(tg);
                            dc.DrawGeometry(new SolidColorBrush(
                                Color.FromRgb(0x4F, 0xC3, 0xF7)), null, g);
                            dc.Pop();

                            DrawLabel(dc, "r" + r1.ToString("0", CultureInfo.InvariantCulture)
                                       + "/" + r2.ToString("0", CultureInfo.InvariantCulture)
                                       + "  " + (di * 45) + "deg  s"
                                       + st[ri].ToString("0.00", CultureInfo.InvariantCulture),
                                       cx - tile / 2, cyTile - tile / 2 - 12, 7.5);
                        }
                    }
                    DrawLabel(dc, "liquid neck matrix — pair pre-connected, then stretched to "
                               + "s0.90 / s1.00 / s1.15 (rows)", pad, 3, 10.5);
                }
                SavePng(v, sheetW, sheetH, 96, Path.Combine(outDir, "liquid-matrix.png"));

                /* 2) 交接证据：真实半径 15/17，1.28 有颈 / 1.32 已断 */
                double rr1 = 15, rr2 = 17;
                double dipW = 120, dipH = 80;
                double cy2 = dipH / 2;

                for (int pass = 0; pass < 2; pass++)
                {
                    LiquidTension.Field f = new LiquidTension.Field();
                    f.IncludeBodies = true;
                    LiquidBody a = new LiquidBody("island", 34, cy2, rr1);
                    LiquidBody b = new LiquidBody("ball", 34, cy2, rr2);
                    LiquidBody[] pair = new LiquidBody[] { a, b };
                    b.X = 34 + (rr1 + rr2) * 0.95;
                    f.Bridges(pair);
                    b.X = 34 + (rr1 + rr2) * (pass == 0 ? 1.28 : 1.3200);
                    Geometry g = f.Bridges(pair);

                    DrawingVisual dv = new DrawingVisual();
                    using (DrawingContext dc = dv.RenderOpen())
                    {
                        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x11, 0x14, 0x1A)),
                                         null, new Rect(0, 0, dipW, dipH));
                        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0x07, 0xC1, 0x60)),
                                        null, g);
                        DrawLabel(dc, pass == 0
                            ? "r15/r17  stretch 1.280 — neck held"
                            : "r15/r17  stretch 1.320 — snapped, gap clean",
                            new Point(6, 5), 9);
                    }
                    SavePng(dv, dipW, dipH, 144,
                            Path.Combine(outDir, pass == 0
                                ? "liquid-connected.png" : "liquid-snapped.png"));
                }

                r.AppendLine("     证据图已写入 " + outDir);
            }
            catch (Exception ex)
            {
                fails.Add("S6 证据图写不出来: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        static void DrawLabel(DrawingContext dc, string text, Point at, double size)
        {
            /* pixelsPerDip 固定 1.0：这里的坐标全是 DIP，整张图又是交给
               RenderTargetBitmap(vw, vh, dpi, dpi, ...) 按 dpi/96 放大的，
               所以「度量按 DIP 走、缩放交给位图」才是对的口径。
               传屏幕真实 DPI 会让文字在一张 DIP 画布上被放大两次。 */
            FormattedText ft = new FormattedText(text, CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), size,
                new SolidColorBrush(Color.FromRgb(0xC8, 0xD2, 0xE0)), 1.0);
            dc.DrawText(ft, at);
        }

        static void DrawLabel(DrawingContext dc, string text, double x, double y, double size)
        {
            DrawLabel(dc, text, new Point(x, y), size);
        }

        static void SavePng(Visual v, double dipW, double dipH, double dpi, string path)
        {
            int pw = (int)Math.Ceiling(dipW * dpi / 96.0);
            int ph = (int)Math.Ceiling(dipH * dpi / 96.0);
            RenderTargetBitmap rtb = new RenderTargetBitmap(pw, ph, dpi, dpi, PixelFormats.Pbgra32);
            rtb.Render(v);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                enc.Save(fs);
        }

        /* ------------------------------------------------------------ JSON */

        static string BuildJson(bool ok, List<string> fails, string outDir)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"ok\": ").Append(ok ? "true" : "false").Append(",\n");
            sb.Append("  \"prototype\": \"liquid-neck\",\n");
            sb.Append("  \"enabled\": false,\n");
            sb.Append("  \"shipped\": false,\n");
            sb.Append("  \"subjectPair\": \"island right-end arc <-> notify ball\",\n");
            sb.Append("  \"dir\": \"").Append(Esc(outDir)).Append("\",\n");
            sb.Append("  \"pair\": {\n");
            sb.Append("    \"idleH\": ").Append(F(LiquidPair.IdleH)).Append(",\n");
            sb.Append("    \"playH\": ").Append(F(LiquidPair.PlayH)).Append(",\n");
            sb.Append("    \"notifyGap\": ").Append(F(LiquidPair.NotifyGap)).Append(",\n");
            sb.Append("    \"notifyBall\": ").Append(F(LiquidPair.NotifyBall)).Append(",\n");
            sb.Append("    \"idleRestStretch\": ").Append(F(LiquidPair.RestStretch(LiquidPair.IdleH))).Append(",\n");
            sb.Append("    \"playRestStretch\": ").Append(F(LiquidPair.RestStretch(LiquidPair.PlayH))).Append("\n");
            sb.Append("  },\n");
            sb.Append("  \"constants\": {\n");
            sb.Append("    \"connectStretch\": ").Append(F(LiquidTension.ConnectStretch)).Append(",\n");
            sb.Append("    \"snapStretch\": ").Append(F(LiquidTension.SnapStretch)).Append(",\n");
            sb.Append("    \"minWaistFactor\": ").Append(F(LiquidTension.MinWaistFactor)).Append(",\n");
            sb.Append("    \"baseWaist\": ").Append(F(LiquidTension.BaseWaist)).Append(",\n");
            sb.Append("    \"minNeckWidth\": ").Append(F(LiquidTension.DefaultMinNeckWidth)).Append("\n");
            sb.Append("  },\n");
            sb.Append("  \"failureCount\": ").Append(fails.Count).Append(",\n");
            sb.Append("  \"failures\": [");
            for (int i = 0; i < fails.Count; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("\n    \"").Append(Esc(fails[i])).Append("\"");
            }
            if (fails.Count > 0) sb.Append("\n  ");
            sb.Append("]\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        static string Esc(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }
    }
}
