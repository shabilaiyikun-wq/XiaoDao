using System;
using System.Windows;
using System.Windows.Media;

namespace WinIsland
{
    /// <summary>
    /// 连续圆角（超椭圆）。等价于 CSS 的 corner-shape: superellipse(4)。
    ///
    /// 参数方程（以每个角的圆心为原点，半径 r，指数 n）：
    ///   x = cx + r * sign(cos t) * |cos t|^(2/n)
    ///   y = cy + r * sign(sin t) * |sin t|^(2/n)
    ///
    /// n=4 时 2/n=0.5，即 sqrt。比对圆弧：t=225° 处圆给出 0.293r，超椭圆给出 0.159r，
    /// 更贴角，转角处曲率连续——这就是「看起来更贵」的来源。
    /// </summary>
    static class Squircle
    {
        const int Seg = 26;   // 每个角的分段数
        public const double N = 4.0;
        const double CapsuleN = 2.0;

        /// <summary>第一、二层用胶囊端帽，展开态仍用原超椭圆；过渡区平滑恢复。</summary>
        public static double IslandExponent(double h)
        {
            double start = Scenes.PlayH;
            const double end = 60.0;
            if (h <= start) return CapsuleN;
            if (h >= end) return N;
            return CapsuleN + (N - CapsuleN) * (h - start) / (end - start);
        }

        public static StreamGeometry Build(double w, double h, double r)
        {
            return Build(w, h, r, N);
        }

        public static StreamGeometry Build(double w, double h, double r, double exponent)
        {
            if (w <= 0) w = 1;
            if (h <= 0) h = 1;
            if (r > w / 2) r = w / 2;
            if (r > h / 2) r = h / 2;
            if (exponent < CapsuleN) exponent = CapsuleN;

            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext c = g.Open())
            {
                if (r <= 0.01)
                {
                    c.BeginFigure(new Point(0, 0), true, true);
                    c.LineTo(new Point(w, 0), true, false);
                    c.LineTo(new Point(w, h), true, false);
                    c.LineTo(new Point(0, h), true, false);
                    g.Freeze();
                    return g;
                }

                c.BeginFigure(new Point(r, 0), true, true);
                c.LineTo(new Point(w - r, 0), true, false);
                Arc(c, w - r, r, r, 270, 360, exponent);      // 右上
                c.LineTo(new Point(w, h - r), true, false);
                Arc(c, w - r, h - r, r, 0, 90, exponent);     // 右下
                c.LineTo(new Point(r, h), true, false);
                Arc(c, r, h - r, r, 90, 180, exponent);       // 左下
                c.LineTo(new Point(0, r), true, false);
                Arc(c, r, r, r, 180, 270, exponent);          // 左上，回到起点
            }
            g.Freeze();
            return g;
        }

        static void Arc(StreamGeometryContext c, double cx, double cy, double r,
                        double from, double to, double exponent)
        {
            for (int i = 1; i <= Seg; i++)
            {
                double t = (from + (to - from) * i / Seg) * Math.PI / 180.0;
                double ct = Math.Cos(t);
                double st = Math.Sin(t);
                double x = cx + r * Math.Sign(ct) * Math.Pow(Math.Abs(ct), 2.0 / exponent);
                double y = cy + r * Math.Sign(st) * Math.Pow(Math.Abs(st), 2.0 / exponent);
                c.LineTo(new Point(x, y), true, false);
            }
        }

        /// <summary>点是否在超椭圆内（用于点击穿透的命中判定）。</summary>
        public static bool Contains(Point p, double w, double h, double r)
        {
            return Contains(p, w, h, r, N);
        }

        public static bool Contains(Point p, double w, double h, double r, double exponent)
        {
            if (p.X < 0 || p.Y < 0 || p.X > w || p.Y > h) return false;
            if (r > w / 2) r = w / 2;
            if (r > h / 2) r = h / 2;
            if (r <= 0.01) return true;

            double cx = p.X < r ? r : (p.X > w - r ? w - r : p.X);
            double cy = p.Y < r ? r : (p.Y > h - r ? h - r : p.Y);
            double dx = p.X - cx;
            double dy = p.Y - cy;
            if (dx == 0 || dy == 0) return true;      // 直边带内，必在内部

            double u = Math.Abs(dx) / r;
            double v = Math.Abs(dy) / r;
            if (exponent < CapsuleN) exponent = CapsuleN;
            return Math.Pow(u, exponent) + Math.Pow(v, exponent) <= 1.0;
        }
    }

    /// <summary>半隐式欧拉弹簧，定步长细分，重定向时保留位置与速度。</summary>
    class Spring
    {
        public double Value;
        public double Velocity;
        public double Target;
        public double From;

        public Spring(double v) { Value = v; Target = v; From = v; }

        public void Step(double dt, double k, double c)
        {
            int steps = (int)Math.Ceiling(dt / (1.0 / 240.0));
            if (steps < 1) steps = 1;
            double h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                double a = -k * (Value - Target) - c * Velocity;
                Velocity += a * h;
                Value += Velocity * h;
            }
        }

        public bool Settled
        {
            get { return Math.Abs(Value - Target) < 0.35 && Math.Abs(Velocity) < 12; }
        }

        public void Snap()
        {
            Value = Target;
            Velocity = 0;
        }

        public void Aim(double target)
        {
            From = Value;
            Target = target;
        }
    }
}
