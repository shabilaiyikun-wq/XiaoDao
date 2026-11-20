/* 【旧实现 · 不参与编译】
   形状几何已迁到 ..\pet-demo\PetShape.cs。两份都叫 PetShape，加回编译列表必然重复定义。 */
using System;
using System.Windows;
using System.Windows.Media;

namespace WinIsland
{
    /* 桌宠的形状与脸部几何。

       形状一律用「径向 profile」描述：r(θ) 归一化到 0..1，出几何时再乘半径。
       好处是任意两个形状都能逐点插值 —— morph 就是 64 个半径的线性插值，
       不需要路径变形库，也不会出现两条不同点数路径对不上的问题。

       眼睛是「洞」：Geometry.Combine(body, eyes, Exclude) 从身体上抠掉。
       所以眼睛滑到边缘会被身体自动剪掉，不用额外写裁剪代码。

       眼睛还活在球面上：按到球心的距离做正交投影压缩，转到背面就不画。
       这一步是「贴纸」和「有生命」的分界线。 */

    static class PetShape
    {
        public const int Samples = 64;

        const double TAU = Math.PI * 2.0;
        const double TILT = 0.34;   /* 静止时两只眼都向右倾 ≈ 19.5°，即 "\\" */

        /* 形状目录。浮点索引 = 在相邻两个形状之间插值。
           只放「星形域」形状（每个角度只有一个半径），这样所有形状都能互相插值。 */
        public static readonly string[] Names = new string[] { "circle", "squircle", "hexagon", "blob", "cloud" };

        /* 换形状的抽取池：多放几个 circle，让桌宠大部分时间就是个球，
           偶尔才变成别的形状。全随机的话第一眼会像 bug 而不是特性。 */
        static readonly int[] Pool = new int[] { 0, 0, 0, 1, 0, 3, 1, 2, 0, 4 };

        /// <summary>单个形状在角度 theta 处的归一化半径。</summary>
        static double RadiusOf(int id, double theta)
        {
            double c = Math.Cos(theta);
            double s = Math.Sin(theta);
            switch (id)
            {
                case 1:
                    /* squircle：超椭圆 |x|^4 + |y|^4 = 1 的极坐标形式 */
                    return Math.Pow(Math.Pow(Math.Abs(c), 4.0) + Math.Pow(Math.Abs(s), 4.0), -0.25);
                case 2:
                {
                    /* 正六边形，顶点落在 0° */
                    double m = theta + Math.PI / 6.0;
                    m = m - Math.Floor(m / (Math.PI / 3.0)) * (Math.PI / 3.0) - Math.PI / 6.0;
                    return Math.Cos(Math.PI / 6.0) / Math.Cos(m);
                }
                case 3:
                    /* blob：低频起伏，看起来像一团软的东西 */
                    return 1.0 + 0.055 * Math.Cos(3.0 * theta + 0.7) + 0.028 * Math.Sin(5.0 * theta);
                case 4:
                    /* cloud：六瓣 */
                    return 1.0 + 0.062 * Math.Cos(6.0 * theta) + 0.026 * Math.Cos(3.0 * theta + 2.0);
                default:
                    return 1.0;
            }
        }

        /// <summary>浮点形状索引 → 逐点插值出的半径表。</summary>
        public static double[] Profile(double shapeIndex)
        {
            int last = Names.Length - 1;
            if (shapeIndex < 0) shapeIndex = 0;
            if (shapeIndex > last) shapeIndex = last;

            int i0 = (int)Math.Floor(shapeIndex);
            int i1 = i0 + 1;
            if (i1 > last) i1 = last;
            double f = shapeIndex - i0;

            double[] r = new double[Samples];
            for (int i = 0; i < Samples; i++)
            {
                double th = TAU * i / Samples;
                double a = RadiusOf(i0, th);
                double b = RadiusOf(i1, th);
                r[i] = a + (b - a) * f;
            }
            return r;
        }

        /// <summary>确定性伪随机：给定片段号抽出形状。纯函数，可重放。</summary>
        public static int PickShape(double seg)
        {
            if (seg < 0) return 0;
            uint h = Hash((uint)((int)seg + 1) * 2654435761u);
            return Pool[h % (uint)Pool.Length];
        }

        public static uint Hash(uint x)
        {
            x ^= x >> 16;
            x *= 0x7feb352du;
            x ^= x >> 15;
            x *= 0x846ca68bu;
            x ^= x >> 16;
            return x;
        }

        /// <summary>身体轮廓，按球面朝向做轻微透视压缩。</summary>
        public static StreamGeometry Body(double radius, double shapeIndex, double squashX,
                                          double squashY, double yaw, double pitch)
        {
            double[] prof = Profile(shapeIndex);
            double yawSin = Math.Sin(yaw);
            double pitchSin = Math.Sin(pitch);
            double turnScale = 1.0 - 0.18 * Math.Abs(yawSin);
            double pitchScale = 1.0 - 0.10 * Math.Abs(pitchSin);
            StreamGeometry g = new StreamGeometry();
            using (StreamGeometryContext c = g.Open())
            {
                for (int i = 0; i < Samples; i++)
                {
                    double th = TAU * i / Samples - yaw * 0.12;
                    double rr = radius * prof[i];
                    Point pt = new Point(rr * Math.Cos(th) * squashX * turnScale,
                                         rr * Math.Sin(th) * squashY * pitchScale);
                    if (i == 0) c.BeginFigure(pt, true, true);
                    else c.LineTo(pt, true, false);
                }
            }
            g.Freeze();
            return g;
        }

        public static StreamGeometry Body(double radius, double shapeIndex, double squashX, double squashY)
        {
            return Body(radius, shapeIndex, squashX, squashY, 0, 0);
        }

        /// <summary>将两只眼睛投影到球面，并按表情改变眼形。</summary>
        public static Geometry EyePair(double radius, double gazeX, double gazeY, double yaw,
                                        double pitch, double lid, PetExpression expression,
                                        double eyeW, double eyeH)
        {
            GeometryGroup group = new GeometryGroup();
            double open = 1.0 - lid;
            if (open < 0) open = 0;

            double widthScale = 1.0;
            double heightScale = 1.0;
            double tilt = TILT;
            if (expression == PetExpression.Happy) heightScale = 0.62;
            else if (expression == PetExpression.Surprised) { widthScale = 1.18; heightScale = 1.22; }
            else if (expression == PetExpression.Sleepy) heightScale = 0.52;
            else if (expression == PetExpression.Focused) { widthScale = 0.86; heightScale = 0.84; }
            else if (expression == PetExpression.Sad) tilt = -0.30;
            else if (expression == PetExpression.Angry) tilt = 0.42;

            double yawCos = Math.Cos(yaw);
            double yawSin = Math.Sin(yaw);
            double pitchCos = Math.Cos(pitch);
            double pitchSin = Math.Sin(pitch);

            for (int k = 0; k < 2; k++)
            {
                double side = k == 0 ? -1.0 : 1.0;
                double localX = side * 0.30 + gazeX * 0.42;
                double localY = gazeY * 0.52 - 0.04;
                double d2 = localX * localX + localY * localY;
                if (d2 > 0.92 * 0.92)
                {
                    double d = Math.Sqrt(d2);
                    localX = localX / d * 0.92;
                    localY = localY / d * 0.92;
                    d2 = 0.92 * 0.92;
                }

                double localZ = Math.Sqrt(1.0 - d2);
                double pitchY = localY * pitchCos - localZ * pitchSin;
                double pitchZ = localY * pitchSin + localZ * pitchCos;
                double x = localX * yawCos + pitchZ * yawSin;
                double z = pitchZ * yawCos - localX * yawSin;
                if (z < 0.12) continue;

                double w = eyeW * widthScale * Math.Max(0.12, Math.Abs(yawCos));
                double h = eyeH * heightScale;
                double eyeOpen = open;
                if (expression == PetExpression.Wink && side > 0) eyeOpen = 0.03;
                double keep = expression == PetExpression.Sleepy ? 0.24 : 0.12;
                double hh = h * (keep + (1.0 - keep) * eyeOpen);

                EllipseGeometry e = new EllipseGeometry(new Point(0, 0), w * z, hh);
                TransformGroup tg = new TransformGroup();
                tg.Children.Add(new RotateTransform(tilt * 180.0 / Math.PI));
                tg.Children.Add(new TranslateTransform(radius * x * 0.95, radius * pitchY * 0.95));
                e.Transform = tg;
                group.Children.Add(e);
            }

            if (group.Children.Count == 0) return null;
            group.Freeze();
            return group;
        }

        public static Geometry EyePair(double radius, double gazeX, double gazeY, double yaw,
                                       double lid, double eyeW, double eyeH)
        {
            return EyePair(radius, gazeX, gazeY, yaw, 0, lid,
                           PetExpression.Neutral, eyeW, eyeH);
        }

        /// <summary>整个脸：身体轮廓。</summary>
        /// <remarks>
        /// 这里刻意**不**把眼睛从身体上挖掉。
        /// 官方 Grok Bot 用的是 Exclude 挖孔（"eyes are holes, not overlays"），
        /// 那套成立的前提是它有一块已知底色的侧边栏垫在背后 —— 洞透出的是 UI 背景色。
        /// 桌宠背后是用户的桌面：深色壁纸下 (0x0A,0x0A,0x0C) 的球和透出的桌面只差
        /// 十来个灰阶，眼睛几乎等于消失，换个壁纸整个脸的颜色就变了。
        /// 所以改成「球画实心，眼睛单独一层实色，用球几何做 Clip」——
        /// 眼睛照样被球体裁掉超出部分，但颜色由我们说了算。
        /// 顺带每帧少一次 Geometry.Combine（整个渲染里最贵的一步）。
        /// </remarks>
        public static Geometry Face(double radius, double shapeIndex, double squashX, double squashY,
                                    double yaw, double pitch)
        {
            return Body(radius, shapeIndex, squashX, squashY, yaw, pitch);
        }

        public static Geometry Face(double radius, double shapeIndex, double squashX, double squashY)
        {
            return Face(radius, shapeIndex, squashX, squashY, 0, 0);
        }
    }
}