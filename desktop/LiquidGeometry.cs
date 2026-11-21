/* ============================================================================
   液态连接 · 纯几何（原型）

   ---------------------------------------------------------------------------
   来源与许可（MIT）
   ---------------------------------------------------------------------------
   本文件的圆—圆切线点 + 双贝塞尔连接颈算法，是从下面这个 MIT 项目移植并改写为
   C# / WPF Geometry 的实质移植（substantial portions of the Software）：

       FluidKit — https://github.com/runvendo/fluidkit
       src/liquid/geometry.ts      -> bridgePath / circlePath / dist
       Copyright (c) 2026 Yousef Hindy
       MIT License

   完整许可正文见 desktop/LICENSES/fluidkit-MIT.txt，随源码一同分发。
   许可要求「上述版权声明和本许可声明应包含在软件的所有副本或实质部分中」，
   所以这段头注释不能删。

   ---------------------------------------------------------------------------
   为什么不能直接搬
   ---------------------------------------------------------------------------
   参考实现产出的是 **SVG path-data 字符串**，靠 `clip-path: path(...)` 的
   nonzero 填充规则把「圆 + 圆 + 颈部」三个**同向缠绕**的子路径并起来。
   在 WPF 里没有这条路：

     · 没有 path-data 字符串，几何必须是 Geometry 对象；
     · WPF 的 PathGeometry 也支持 FillRule.Nonzero，但每个 PathFigure 只属于
       一个 PathGeometry，所以「三段拼一个」必须显式 Append 到同一个几何里，
       不能像 SVG 那样把字符串接起来；
     · 缠绕方向错了不会报警，只会**静默挖洞**（nonzero 规则下反向子路径互相抵消）。
       所以这里把缠绕方向做成了可断言的东西（见 SameWinding / SignedArea）。

   改写的三处实质差异（都不是美化，是修 bug）：

     1) acos 参数夹紧。参考实现直接 Math.acos(...)，浮点误差会让参数越出
        [-1,1]。TS 里那是 NaN，在 C# 里也是 NaN —— 但 NaN 坐标塞进 Geometry
        不会抛异常，只会让这一帧的图元静默消失。夹紧之后「偶尔闪一下」才没有。
     2) dist(p1,p3) 等中间量全部走同一个 double 路径，避免 Point 与 double 混算。
     3) 颈部宽度从「只由 waist 这个 0..1 的相对系数间接决定」变成**可测的绝对量**
        （NeckWidth，DIP）。参考实现的 waist 是相对系数，球小了颈部就细成一条丝；
        这里给出真实几何宽度，供 LiquidTension 做硬门槛（见那边注释）。

   ---------------------------------------------------------------------------
   坐标与缠绕约定
   ---------------------------------------------------------------------------
   全部 DIP，y 轴向下（WPF 默认）。圆盘按「右 → 下 → 左 → 上」走，即屏幕上顺时针，
   鞋带公式（Shoelace）为**正**。颈部闭合子路径按参考实现的
   p1 → p3 → p4 → p2 走，同一个方向也是正；万一半径差把方向翻过来，
   AddBridgeFigure 会自己反向补回去，不让它挖洞。
   ============================================================================ */

using System;
using System.Windows;
using System.Windows.Media;

namespace WinIsland
{
    /// <summary>参与液态连接的一个圆体。X/Y 是圆心，R 是半径，单位一律 DIP。</summary>
    sealed class LiquidBody
    {
        /// <summary>稳定标识。张力滞回状态按这对 id 记账，所以同一个球每帧要给同一个 id。</summary>
        public string Id;
        public double X;
        public double Y;
        public double R;

        public LiquidBody() { }

        public LiquidBody(string id, double x, double y, double r)
        {
            Id = id; X = x; Y = y; R = r;
        }

        public LiquidBody Clone()
        {
            return new LiquidBody(Id, X, Y, R);
        }
    }

    /// <summary>
    /// 一条连接颈的完整形状。探针要逐点断言，所以中间量全部留在外面 ——
    /// 只返回一个 Geometry 的话，「颈宽到底多少」「有没有 NaN」就没法验。
    /// </summary>
    sealed class BridgeShape
    {
        /* p1/p2 在 A 上，p3/p4 在 B 上；h1..h4 是对应贝塞尔的控制点。
           曲线走向（与参考实现一致）：
             p1 --C(h1,h3)--> p3 --L--> p4 --C(h4,h2)--> p2 --L(闭合)--> p1 */
        public Point P1, P2, P3, P4;
        public Point H1, H2, H3, H4;

        /// <summary>颈部最细处宽度（DIP），两条边界曲线之间的真实最短距离。</summary>
        public double NeckWidth;

        /// <summary>四个切点里任何一个不是有限数，就说明算出了 NaN —— 探针据此判红。</summary>
        public bool Finite()
        {
            return Ok(P1) && Ok(P2) && Ok(P3) && Ok(P4)
                && Ok(H1) && Ok(H2) && Ok(H3) && Ok(H4)
                && !double.IsNaN(NeckWidth) && !double.IsInfinity(NeckWidth);
        }

        static bool Ok(Point p)
        {
            return !double.IsNaN(p.X) && !double.IsNaN(p.Y)
                && !double.IsInfinity(p.X) && !double.IsInfinity(p.Y);
        }
    }

    /// <summary>
    /// 圆—圆液态连接的纯几何。无状态、不碰 UI、不读窗口 —— 可以在探针里直接跑。
    /// </summary>
    static class LiquidGeometry
    {
        /// <summary>参考实现的退化半径：小于等于这个值不画（子像素圆没有意义）。</summary>
        public const double DegenerateRadius = 0.5;
        /// <summary>贝塞尔控制点长度的基数（参考实现默认 handle = 2.1）。</summary>
        public const double DefaultHandle = 2.1;
        /// <summary>够得着的倍数（参考实现默认 reach = 1.6）：d 超过 (r1+r2)*reach 就不画。</summary>
        public const double DefaultReach = 1.6;

        /// <summary>「一个把另一个包住」的判定余量，与参考实现一致。</summary>
        const double ContainSlack = 0.5;

        /* ------------------------------------------------------------ 基础量 */

        public static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        public static double Dist(double ax, double ay, double bx, double by)
        {
            double dx = bx - ax;
            double dy = by - ay;
            return Math.Sqrt(dx * dx + dy * dy);
        }

        public static double Dist(LiquidBody a, LiquidBody b)
        {
            return Dist(a.X, a.Y, b.X, b.Y);
        }

        /// <summary>拉伸比 = 圆心距 / 半径和。1.0 = 刚好相切，&lt;1 = 已重叠。</summary>
        public static double Stretch(LiquidBody a, LiquidBody b)
        {
            double sum = a.R + b.R;
            if (sum <= 0) return double.PositiveInfinity;
            return Dist(a, b) / sum;
        }

        static Point At(double cx, double cy, double angle, double len)
        {
            return new Point(cx + len * Math.Cos(angle), cy + len * Math.Sin(angle));
        }

        /* ------------------------------------------------------------ 颈部 */

        /// <summary>
        /// 两圆之间那条连接颈。语义与参考实现 bridgePath 一一对应，只是产出中间量而不是字符串。
        /// </summary>
        /// <param name="waist">0..1。略等于「颈有多粗」，由 LiquidTension.NeckWaist 给出。</param>
        /// <returns>够不着 / 包含 / 退化时返回 false —— 调用方必须当「没有颈部」处理，
        /// 不能当成「有颈部但画不出来」。</returns>
        public static bool TryBridge(LiquidBody a, LiquidBody b, double waist,
                                     double handle, double reach, out BridgeShape shape)
        {
            shape = null;
            if (a == null || b == null) return false;

            double r1 = a.R;
            double r2 = b.R;
            if (r1 <= DegenerateRadius || r2 <= DegenerateRadius) return false;

            double dx = b.X - a.X;
            double dy = b.Y - a.Y;
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d <= 0) return false;

            /* 两条「不画」的条件原样保留：
               够不着（> reach）、以及一个圆把另一个包住（并集本来就已经是一个体）。 */
            if (d > (r1 + r2) * reach) return false;
            if (d <= Math.Abs(r1 - r2) + ContainSlack) return false;

            if (waist < 0) waist = 0;
            if (waist > 1) waist = 1;

            /* 相交时才有「交点对应的张角」；分离时张角为 0，
               切线点完全由 waist 沿 maxSpread 插值决定 —— 这正是参考实现在
               「没碰上也能画出一条颈」的数学来源，滞回就是靠它。 */
            double u1 = 0;
            double u2 = 0;
            if (d < r1 + r2)
            {
                /* 夹紧：参考实现这里会因浮点误差越界，NaN 在 Geometry 里是静默消失。 */
                u1 = Math.Acos(Clamp((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d), -1, 1));
                u2 = Math.Acos(Clamp((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d), -1, 1));
            }

            double ang = Math.Atan2(dy, dx);
            double maxSpread = Math.Acos(Clamp((r1 - r2) / d, -1, 1));

            double a1 = ang + u1 + (maxSpread - u1) * waist;
            double a2 = ang - u1 - (maxSpread - u1) * waist;
            double a3 = ang + Math.PI - u2 - (Math.PI - u2 - maxSpread) * waist;
            double a4 = ang - Math.PI + u2 + (Math.PI - u2 - maxSpread) * waist;

            Point p1 = At(a.X, a.Y, a1, r1);
            Point p2 = At(a.X, a.Y, a2, r1);
            Point p3 = At(b.X, b.Y, a3, r2);
            Point p4 = At(b.X, b.Y, a4, r2);

            /* 控制点长度：既受 waist 限，也受「两个切点之间还剩多少距离」限，
               再乘一个「刚接触时收一收」的系数。三项都是参考实现原样。 */
            double totalR = r1 + r2;
            double d2 = Math.Min(waist * handle, Dist(p1.X, p1.Y, p3.X, p3.Y) / totalR)
                      * Math.Min(1.0, (d * 2.0) / totalR);
            if (d2 < 0) d2 = 0;

            double hr1 = r1 * d2;
            double hr2 = r2 * d2;

            BridgeShape s = new BridgeShape();
            s.P1 = p1; s.P2 = p2; s.P3 = p3; s.P4 = p4;
            /* 控制点一律落在切点的**切线**方向上（半径方向转 90°），
               曲线才会在圆面上 C1 连续地离开，不会出现折角。 */
            s.H1 = At(p1.X, p1.Y, a1 - Math.PI / 2, hr1);
            s.H2 = At(p2.X, p2.Y, a2 + Math.PI / 2, hr1);
            s.H3 = At(p3.X, p3.Y, a3 + Math.PI / 2, hr2);
            s.H4 = At(p4.X, p4.Y, a4 - Math.PI / 2, hr2);

            if (!s.Finite()) return false;

            s.NeckWidth = MeasureNeck(s);
            shape = s;
            return true;
        }

        /* ------------------------------------------------------- 颈宽测量 */

        /// <summary>
        /// 颈部最细处宽度：两条边界曲线之间的最短距离。
        ///
        /// 参考实现把「粗细」完全交给 waist 这个 0..1 系数，没有任何一处能回答
        /// 「现在这条颈到底几 DIP 宽」。而本项目的硬要求是「连接颈必须保持可控
        /// 最小宽度」，所以必须把宽度量出来 —— 只有量出来的数才能当门槛。
        ///
        /// 做法是密采样后取最小点对（粗网格 + 局部细化）。这不是解析解，但对我们
        /// 实际用到的半径（1.5 ~ 34 DIP）误差 &lt; 0.02 DIP；探针里用加密一倍的结果
        /// 交叉验证过，两者差值在容差内。
        /// </summary>
        public static double MeasureNeck(BridgeShape s)
        {
            /* 先粗后细：粗网格定位，再在最佳点附近把步长砍半收 6 次。
               直接 128×128 也能算对，但每个连接对每帧都要跑一次，没必要。 */
            const int Coarse = 12;
            int bi = 0;
            int bj = 0;
            double best = double.MaxValue;

            for (int i = 0; i <= Coarse; i++)
            {
                Point p = CubicAt(s.P1, s.H1, s.H3, s.P3, (double)i / Coarse);
                for (int j = 0; j <= Coarse; j++)
                {
                    Point q = CubicAt(s.P4, s.H4, s.H2, s.P2, (double)j / Coarse);
                    double dd = Dist(p.X, p.Y, q.X, q.Y);
                    if (dd < best) { best = dd; bi = i; bj = j; }
                }
            }

            double ui = (double)bi / Coarse;
            double uj = (double)bj / Coarse;
            double step = 1.0 / Coarse;
            for (int k = 0; k < 6; k++)
            {
                step *= 0.5;
                for (int di = -1; di <= 1; di++)
                {
                    double ti = ui + di * step;
                    if (ti < 0 || ti > 1) continue;
                    Point p = CubicAt(s.P1, s.H1, s.H3, s.P3, ti);
                    for (int dj = -1; dj <= 1; dj++)
                    {
                        double tj = uj + dj * step;
                        if (tj < 0 || tj > 1) continue;
                        Point q = CubicAt(s.P4, s.H4, s.H2, s.P2, tj);
                        double dd = Dist(p.X, p.Y, q.X, q.Y);
                        if (dd < best) { best = dd; ui = ti; uj = tj; }
                    }
                }
            }
            return best;
        }

        /// <summary>三次贝塞尔求点。</summary>
        public static Point CubicAt(Point p0, Point c1, Point c2, Point p3, double t)
        {
            double u = 1 - t;
            double b0 = u * u * u;
            double b1 = 3 * u * u * t;
            double b2 = 3 * u * t * t;
            double b3 = t * t * t;
            return new Point(
                p0.X * b0 + c1.X * b1 + c2.X * b2 + p3.X * b3,
                p0.Y * b0 + c1.Y * b1 + c2.Y * b2 + p3.Y * b3);
        }

        /* ------------------------------------------------------ 缠绕方向 */

        /// <summary>鞋带公式。y 向下时，屏幕顺时针为正 —— 和圆盘的走向一致。</summary>
        public static double SignedArea(Point[] pts)
        {
            if (pts == null || pts.Length < 3) return 0;
            double s = 0;
            for (int i = 0; i < pts.Length; i++)
            {
                Point a = pts[i];
                Point b = pts[(i + 1) % pts.Length];
                s += a.X * b.Y - b.X * a.Y;
            }
            return s * 0.5;
        }

        /// <summary>颈部闭合轮廓的四个顶点（与参考实现同序）。面积小于 0 说明方向反了。</summary>
        public static bool BridgeNeedsReverse(BridgeShape s)
        {
            return SignedArea(new Point[] { s.P1, s.P3, s.P4, s.P2 }) < 0;
        }

        /* ---------------------------------------------------------- 图元 */

        /// <summary>把一个圆盘 Appned 成闭合子路径（右→下→左→上，顺时针，鞋带为正）。</summary>
        public static void AppendDisc(PathGeometry into, LiquidBody b)
        {
            if (into == null || b == null || b.R <= DegenerateRadius) return;

            Point right = new Point(b.X + b.R, b.Y);
            Point left = new Point(b.X - b.R, b.Y);

            PathFigure f = new PathFigure();
            f.StartPoint = right;
            f.IsClosed = true;
            f.IsFilled = true;
            /* 两个半圆弧拼整圆：IsLargeArc=false + Clockwise，正好各走半圈。 */
            f.Segments.Add(new ArcSegment(left, new Size(b.R, b.R), 0, false,
                                          SweepDirection.Clockwise, true));
            f.Segments.Add(new ArcSegment(right, new Size(b.R, b.R), 0, false,
                                          SweepDirection.Clockwise, true));
            into.Figures.Add(f);
        }

        /// <summary>
        /// 把一条颈部 Append 成闭合子路径。
        ///
        /// 反向分支不是装饰：半径差较大、waist 接近 1 时 p1→p3→p4→p2 的绕向会翻过来，
        /// 而 nonzero 规则下反向子路径会和圆盘**互相抵消**，看起来就是颈中间破了个洞。
        /// 参考实现靠「调用方保证 waist 不会取到那种值」规避，这里直接算面积兜住。
        /// </summary>
        public static void AppendBridge(PathGeometry into, BridgeShape s)
        {
            if (into == null || s == null) return;

            bool rev = BridgeNeedsReverse(s);
            PathFigure f = new PathFigure();
            f.IsClosed = true;
            f.IsFilled = true;

            if (!rev)
            {
                f.StartPoint = s.P1;
                f.Segments.Add(new BezierSegment(s.H1, s.H3, s.P3, true));
                f.Segments.Add(new LineSegment(s.P4, true));
                f.Segments.Add(new BezierSegment(s.H4, s.H2, s.P2, true));
            }
            else
            {
                /* 原路反走：p1 →(直线) p2 →(贝塞尔反向) p4 →(直线) p3 →(贝塞尔反向) p1 */
                f.StartPoint = s.P1;
                f.Segments.Add(new LineSegment(s.P2, true));
                f.Segments.Add(new BezierSegment(s.H2, s.H4, s.P4, true));
                f.Segments.Add(new LineSegment(s.P3, true));
                f.Segments.Add(new BezierSegment(s.H3, s.H1, s.P1, true));
            }
            into.Figures.Add(f);
        }

        /// <summary>一个圆盘的独立几何（探针画单个体时用）。</summary>
        public static Geometry Disc(LiquidBody b)
        {
            if (b == null || b.R <= DegenerateRadius) return null;
            PathGeometry g = new PathGeometry();
            g.FillRule = FillRule.Nonzero;
            AppendDisc(g, b);
            g.Freeze();
            return g;
        }

        /// <summary>一条颈部的独立几何（探针单独量颈部像素时用）。</summary>
        public static Geometry BridgeGeometry(BridgeShape s)
        {
            if (s == null) return null;
            PathGeometry g = new PathGeometry();
            g.FillRule = FillRule.Nonzero;
            AppendBridge(g, s);
            g.Freeze();
            return g;
        }

        /// <summary>空几何。WPF 里 Geometry.Empty 是共享单例，可以直接挂到路径上但不能再 Append。</summary>
        public static Geometry Empty()
        {
            return Geometry.Empty;
        }
    }
}