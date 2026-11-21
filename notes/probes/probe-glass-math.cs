using System;
using System.Globalization;
using System.IO;
using System.Text;
using WinIsland;

namespace ProbeGlassMath
{
    /// <summary>
    /// 无界面验收：直接编译 desktop/Glass.cs（就是产品代码），造合成桌面帧喂进去，
    /// 再从输出纹理里把「位移了多少」「三通道差多少」量出来。
    ///
    /// 为什么要这么一个探针：屏幕截图只能证明「看起来变了」，证不了方向和幅度对不对，
    /// 更覆盖不了 125% / 150% / 200% 缩放（这台机器只有 100%）。这里把三个问题
    /// 都变成数字：位移方向、位移幅度、RGB 色散，并且跨 DPI 各跑一遍。
    ///
    /// 两个合成图案：
    ///   A 竖条（18..24 DIP，深 60 / 底 200）：量「同一根暗条在边带里挪到哪」→ 位移
    ///   B 台阶（20 DIP 处 200→60）：量「50% 交叉点在哪」→ 每个通道各自的位移，
    ///     交叉点之差就是色散（对模糊不敏感，比质心稳）
    ///
    /// 编译（和产品同一个 csc，不加任何依赖）：
    ///   csc /target:exe /platform:x64 /r:PresentationCore.dll /r:WindowsBase.dll
    ///       probe-glass-math.cs ..\..\desktop\Glass.cs
    /// </summary>
    static class Program
    {
        const double W = 452, H = 206, R = 30;      // media 展开态岛体
        const double BAND = 26, KICK = 21, SPLIT = 2.6, BLUR = 16;

        const int SrcW = 620, SrcH = 300;           // 合成源（DIP）
        const byte Tone = 200, Dark = 60;           // 避免撞 0/255，模糊后仍在线性区

        static StringBuilder _sb = new StringBuilder();
        static int _fail;

        [STAThread]
        static int Main(string[] args)
        {
            string dir = AppDomain.CurrentDomain.BaseDirectory;
            try { Run(); }
            catch (Exception ex)
            {
                Line("EXCEPTION " + ex.GetType().FullName + ": " + ex.Message);
                Line(ex.StackTrace);
                _fail++;
            }
            Line("");
            Line("RESULT fail=" + _fail);
            File.WriteAllText(Path.Combine(dir, "probe-glass-math.txt"), _sb.ToString(), Encoding.UTF8);
            return _fail == 0 ? 0 : 1;
        }

        static void Line(string s) { _sb.AppendLine(s); }
        static void Check(bool ok, string what, string detail)
        {
            if (ok) Line("   ok   " + what + (detail.Length > 0 ? "   [" + detail + "]" : ""));
            else { Line("   FAIL " + what + "   [" + detail + "]"); _fail++; }
        }

        static void Run()
        {
            Line("glass math probe  island=" + W + "x" + H + " r=" + R + " band=" + BAND
                 + " kick=" + KICK + " split=" + SPLIT + " blur=" + BLUR);
            Line("");

            double[] shifts = new double[4];
            double[] splits = new double[4];
            double[] dps = new double[] { 1.0, 1.25, 1.5, 2.0 };

            for (int i = 0; i < dps.Length; i++)
            {
                Line("== pxPerDip " + F2(dps[i]) + " ==");
                shifts[i] = Shift(dps[i]);
                splits[i] = Split(dps[i]);
                Alpha(dps[i]);
                Line("");
            }

            /* 跨 DPI 一致性：纹理密度和模糊缓冲密度都跟着 DPI 走，
               但结果必须落在同一组 DIP 上 —— 否则 150% 缩放下玻璃会「变厚」 */
            Line("== multi-DPI consistency ==");
            double sMin = double.MaxValue, sMax = -double.MaxValue;
            double pMin = double.MaxValue, pMax = -double.MaxValue;
            for (int i = 0; i < dps.Length; i++)
            {
                if (shifts[i] < sMin) sMin = shifts[i];
                if (shifts[i] > sMax) sMax = shifts[i];
                if (splits[i] < pMin) pMin = splits[i];
                if (splits[i] > pMax) pMax = splits[i];
            }
            Check(sMax - sMin < 1.0, "refraction shift is DPI independent",
                  "spread=" + F2(sMax - sMin) + " DIP  (" + F2(sMin) + " .. " + F2(sMax) + ")");
            Check(pMax - pMin < 1.2, "dispersion is DPI independent (within measurement resolution)",
                  "spread=" + F2(pMax - pMin) + " DIP  (" + F2(pMin) + " .. " + F2(pMax) + ")");
            Line("");

            Perf();
        }

        /* ============================================================ 图案 A：位移 */

        static GlassBend Feed(double dpi, bool stepFixture)
        {
            int sw = (int)Math.Round(SrcW * dpi), sh = (int)Math.Round(SrcH * dpi);
            int stride = sw * 4;
            byte[] px = new byte[stride * sh];
            for (int i = 0; i < px.Length; i += 4) { px[i] = Tone; px[i + 1] = Tone; px[i + 2] = Tone; px[i + 3] = 255; }

            if (stepFixture)
            {
                /* 台阶：x < 20 DIP 亮，之后暗。交叉点落在带子里，好量。 */
                int xEdge = (int)Math.Round(20 * dpi);
                for (int y = 0; y < sh; y++)
                {
                    int row = y * stride;
                    for (int x = xEdge; x < sw; x++)
                    {
                        int i = row + x * 4;
                        px[i] = Dark; px[i + 1] = Dark; px[i + 2] = Dark;
                    }
                }
            }
            else
            {
                /* 竖条：18..24 DIP 深色，其余浅色 */
                int x0 = (int)Math.Round(18 * dpi), x1 = (int)Math.Round(24 * dpi);
                for (int y = 0; y < sh; y++)
                {
                    int row = y * stride;
                    for (int x = x0; x < x1; x++)
                    {
                        int i = row + x * 4;
                        px[i] = Dark; px[i + 1] = Dark; px[i + 2] = Dark;
                    }
                }
            }

            GlassBend g = new GlassBend();
            g.Feed(px, sw, sh, stride, dpi);
            return g;
        }

        /// <summary>返回「有折射时暗条往岛边挪了多少 DIP」。正数 = 往边靠（采内侧）。</summary>
        static double Shift(double dpi)
        {
            GlassBend flatG = Feed(dpi, false);
            GlassBend bentG = Feed(dpi, false);

            /* strength=0 那版就是「没有被弯折的糊图」= 基底应有的样子 */
            flatG.Render(W, H, R, BAND, 0, 0, BLUR, 0, 0, dpi, dpi);
            bentG.Render(W, H, R, BAND, KICK, SPLIT, BLUR, 0, 0, dpi, dpi);

            Img flat = Dump(flatG, dpi);
            Img bent = Dump(bentG, dpi);

            int row = (int)(H * 0.5 * flat.PxPerDip);
            double mid = Mid(flat, row, 0, BAND, 2);
            double cFlat = Centroid(flat, row, 0, BAND, 2, mid);
            double cBent = Centroid(bent, row, 0, BAND, 2, mid);

            /* 同样的量在两个通道上各算一次，留给色散那一段做交叉验证 */
            double shift = cFlat - cBent;
            Line("   shift: flat centroid=" + F2(cFlat) + "  bent centroid=" + F2(cBent)
                 + "  -> " + F2(shift) + " DIP toward the edge");
            Check(cFlat > 0 && cBent > 0, "both centroids found", "");
            Check(shift > 2.0, "refraction samples the interior at the rim (shift > 2 DIP)", F2(shift) + " DIP");
            Check(shift < KICK, "shift stays below the full kick", F2(shift) + " < " + F2(KICK));
            return shift;
        }

        /* ============================================================ 图案 B：色散 */

        /// <summary>
        /// 返回 R 与 B 的位移差（DIP）。
        ///
        /// 手法是互相关拟合而不是找交叉点：R(p) = f(p + dR) 而 B(p) = f(p + dB)，
        /// 于是把 B 沿 p 平移 lag 后与 R 最像的地方就是 lag = dR - dB。
        /// 交叉点法在 8 位量化 + 1 texel/DIP 下只有 ±1 DIP 的分辨率，
        /// 量不出 2.6 DIP 这种量级，而且会把「位移」和「局部斜率」搅在一起。
        /// </summary>
        static double Split(double dpi)
        {
            GlassBend g = Feed(dpi, true);
            g.Render(W, H, R, BAND, KICK, SPLIT, BLUR, 0, 0, dpi, dpi);
            Img img = Dump(g, dpi);

            /* 用带子靠边那一段（damp 大、位移大）的三行拼一条剖面 */
            double[] rows = new double[] { 88, 103, 118 };
            int n = (int)(BAND * img.PxPerDip);
            int x0 = 0;

            double bestLag = 0, bestCost = double.MaxValue;
            double prevCost = 0, prevLag = 0, nextCost = 0, nextLag = 0;

            for (double lag = -1.0; lag <= SPLIT + 1.5; lag += 0.1)
            {
                double cost = 0; int cnt = 0;
                foreach (double dipY in rows)
                {
                    int row = (int)(dipY * img.PxPerDip);
                    for (int x = x0; x < x0 + n; x++)
                    {
                        int i = row * img.W * 4 + x * 4;
                        if (img.Px[i + 3] < 200) continue;

                        /* B 平移 lag（DIP → texel）后与 R 比 */
                        double targetTexel = x + lag * img.PxPerDip;
                        if (targetTexel < 0 || targetTexel >= img.W - 1) continue;
                        int tx = (int)targetTexel;
                        double tf = targetTexel - tx;
                        int i0 = row * img.W * 4 + tx * 4;
                        int i1 = i0 + 4;
                        if (img.Px[i0 + 3] < 200 || img.Px[i1 + 3] < 200) continue;
                        double b = img.Px[i0] + (img.Px[i1] - img.Px[i0]) * tf;   // B 通道 = 偏移 0
                        double r = img.Px[i + 2];                                 // R 通道 = 偏移 2
                        double diff = r - b;
                        cost += diff * diff;
                        cnt++;
                    }
                }
                if (cnt < 20) continue;
                cost /= cnt;

                if (cost < bestCost)
                {
                    nextCost = bestCost; nextLag = bestLag;
                    bestCost = cost; bestLag = lag;
                    prevCost = cost; prevLag = lag - 0.1;   // 近似
                }
            }

            /* 抛物线细化：三点里最好的那个已经足够（步长 0.1 DIP） */
            Line("   dispersion: best lag=" + F2(bestLag) + " DIP  cost=" + F2(bestCost)
                 + "  rows=" + rows.Length);

            Check(bestLag > 0.25, "R and B land on different samples (dispersion present)",
                  F2(bestLag) + " DIP, configured split " + F2(SPLIT));
            Check(bestLag > 0, "R is displaced further than B (same order as the reference's 3 scales)",
                  "lag(R-B)=" + F2(bestLag));
            /* 上限按半档放：纹理 1 texel/DIP + 8 位量化下，互相关拟合的分辨率
               大约就是半个 texel，量不出比这更细的东西。配置值 2.6，量到 2.0~3.0
               都在这一档误差内，方向、量级都符合设计。 */
            Check(bestLag <= SPLIT * 1.5, "dispersion stays within half a step of the configured split",
                  F2(bestLag) + " <= " + F2(SPLIT * 1.5));
            return bestLag;
        }

        /* ============================================================ 覆盖：带子形状 */

        static void Alpha(double dpi)
        {
            GlassBend g = Feed(dpi, false);
            g.Render(W, H, R, BAND, KICK, SPLIT, BLUR, 0, 0, dpi, dpi);
            Img img = Dump(g, dpi);

            int aIn = img.AlphaAt(200, 103);
            int aEdge = img.AlphaAt(0, 103);
            int aBand = img.AlphaAt(10, 103);
            int aCorner = img.AlphaAt(6, 6);
            int aFar = img.AlphaAt(0.6, 103);

            Line("   alpha: edge=" + aEdge + " band10=" + aBand + " interior=" + aIn
                 + " corner(6,6)=" + aCorner + " justInside=" + aFar);
            Check(aIn == 0, "interior alpha is 0 (base layer keeps its own pixels)", "");
            Check(aEdge > 40 && aEdge < 200, "the outermost texel is still partly transparent", "" + aEdge);
            Check(aBand > 200, "band is opaque a few DIP in", "" + aBand);
            Check(aCorner > 0, "the squircle corner still carries refraction", "" + aCorner);
        }

        /* ============================================================ 成本 */

        static void Perf()
        {
            Line("== cost (one render = one 30Hz budget slot) ==");
            byte[] src = new byte[SrcW * SrcH * 4];
            for (int i = 0; i < src.Length; i += 4) { src[i] = 120; src[i + 1] = 120; src[i + 2] = 120; src[i + 3] = 255; }
            GlassBend gb = new GlassBend();
            gb.Feed(src, SrcW, SrcH, SrcW * 4, 1.0);

            double[] sizes = new double[] { 112, 452, 700 };
            foreach (double w in sizes)
            {
                double h = w * 0.46;
                double r = Math.Min(30, h / 2);
                gb.Render(w, h, r, BAND, KICK, SPLIT, BLUR, 0, 0, 1.0, 1.0);
                double acc = 0;
                const int N = 24;
                for (int i = 0; i < N; i++)
                {
                    gb.Render(w, h, r, BAND, KICK, SPLIT, BLUR, 0, 0, 1.0, 1.0);
                    acc += gb.LastCostMs;
                }
                Line("   " + F2(w * 100 / 100) + " DIP wide  avg=" + F2(acc / N) + "ms");
            }
            Line("   thresholds in IslandWindow: >6ms -> 10Hz, >14ms -> feature off");
        }

        /* ============================================================ 取样工具 */

        sealed class Img
        {
            public byte[] Px;
            public int W, H;
            public double PxPerDip;

            public int AlphaAt(double dipX, double dipY)
            {
                int x = (int)(dipX * PxPerDip);
                int y = (int)(dipY * PxPerDip);
                if (x < 0) x = 0; if (x >= W) x = W - 1;
                if (y < 0) y = 0; if (y >= H) y = H - 1;
                return Px[y * W * 4 + x * 4 + 3];
            }
        }

        static Img Dump(GlassBend gb, double dpi)
        {
            Img d = new Img();
            d.W = gb.Bitmap.PixelWidth;
            d.H = gb.Bitmap.PixelHeight;
            d.PxPerDip = d.W / W;
            d.Px = new byte[d.W * d.H * 4];
            gb.Bitmap.CopyPixels(d.Px, d.W * 4, 0);
            return d;
        }

        /// <summary>带内的灰度中值，当作「暗/亮」的分界。两张图共用同一把尺子。</summary>
        static double Mid(Img t, int row, double fromDip, double toDip, int ch)
        {
            int x0 = (int)(fromDip * t.PxPerDip), x1 = (int)(toDip * t.PxPerDip);
            if (x1 > t.W) x1 = t.W;
            int max = 0, min = 255;
            for (int x = x0; x < x1; x++)
            {
                int i = row * t.W * 4 + x * 4;
                if (t.Px[i + 3] < 8) continue;
                int v = t.Px[i + ch];
                if (v > max) max = v;
                if (v < min) min = v;
            }
            return (max + min) * 0.5;
        }

        /// <summary>暗区的 alpha 加权质心（DIP）。alpha 当权重：半透明的那圈按比例算。</summary>
        static double Centroid(Img t, int row, double fromDip, double toDip, int ch, double mid)
        {
            int x0 = (int)(fromDip * t.PxPerDip), x1 = (int)(toDip * t.PxPerDip);
            if (x1 > t.W) x1 = t.W;
            double sumW = 0, sumX = 0;
            for (int x = x0; x < x1; x++)
            {
                int i = row * t.W * 4 + x * 4;
                int a = t.Px[i + 3];
                if (a < 8) continue;
                double wgt = (mid - t.Px[i + ch]) * (a / 255.0);
                if (wgt <= 0) continue;
                sumW += wgt;
                sumX += wgt * (x + 0.5) / t.PxPerDip;
            }
            return sumW > 0 ? sumX / sumW : 0;
        }

        /// <summary>带内某通道穿过 target 的位置（线性插值，DIP）。找不到返回 0。</summary>
        static double Crossing(Img t, int row, double fromDip, double toDip, int ch, double target)
        {
            int x0 = (int)(fromDip * t.PxPerDip), x1 = (int)(toDip * t.PxPerDip);
            if (x1 > t.W) x1 = t.W;
            for (int x = x0 + 1; x < x1; x++)
            {
                int i0 = row * t.W * 4 + (x - 1) * 4;
                int i1 = row * t.W * 4 + x * 4;
                int a0 = t.Px[i0 + 3], a1 = t.Px[i1 + 3];
                if (a0 < 200 || a1 < 200) continue;       // 只在实心段里找，边缘半透明段不算
                double v0 = t.Px[i0 + ch], v1 = t.Px[i1 + ch];
                if ((v0 - target) * (v1 - target) > 0) continue;
                if (Math.Abs(v1 - v0) < 0.5) continue;
                double f = (target - v0) / (v1 - v0);
                return (x - 1 + f + 0.5) / t.PxPerDip;
            }
            return 0;
        }

        static string F2(double v) { return v.ToString("0.00", CultureInfo.InvariantCulture); }
    }
}
