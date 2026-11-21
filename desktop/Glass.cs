using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinIsland
{
    /// <summary>
    /// 玻璃边缘折射（liquid glass）在 WPF 上的移植实现。
    ///
    /// ── 移植来源 ────────────────────────────────────────────────────────────
    /// 算法移植自 nikdelvin/liquid-glass（https://github.com/nikdelvin/liquid-glass），
    /// 原文是 Astro 组件 + SVG 滤镜（feImage 造位移图 → feDisplacementMap 三次
    /// → feColorMatrix 分通道 → feBlend screen）。WPF 这条渲染路径上没有 SVG 滤镜，
    /// 也没有可用的像素着色器（见下），所以只搬算法，不搬实现：
    ///
    ///   1. 位移图（src/utils/liquidGlass.ts getDisplacementMap）：
    ///      边上一圈中性值 128，内侧一块「圆角矩形 + blur(depth)」把中间拉回中性，
    ///      横竖各一条 15%*radius 起坡的线性渐变分别驱动 X/Y 方向的位移。
    ///      这里用解析式复现同一条场：u/v 是那两条渐变，t 是内侧中性块的软过渡。
    ///   2. 边缘折射（feDisplacementMap）：位移量 = scale * (通道值 - 0.5)。
    ///   3. RGB 色散（三次 feDisplacementMap + feColorMatrix + screen）：
    ///      R/G/B 用三个不同的 scale（strength + 2*cab / +cab / +0），
    ///      于是三个通道被拉向不同的采样点，边缘出现色边。
    ///      screen 叠加三次「只留一个通道」的图 = 逐通道各采一次再拼回去，
    ///      所以这里直接按通道取样，不需要真的做三张图。
    ///
    /// 没有搬的部分（都是 Web 平台专属，和本项目无关）：
    ///   · backdrop-filter 链（blur → 滤镜 → blur brightness saturate）：
    ///     像素工程里的模糊/色调由 IslandWindow 原有的 BlurEffect + wash + tint 负责，
    ///     本类只管「边缘位移」这一件事，不改动既有的模糊与配色行为。
    ///   · 背景图片自旋、Safari 降级、ResizeObserver 重绘。
    ///
    /// ── 为什么是 CPU 而不是 shader ─────────────────────────────────────────
    /// 这项工程的构建链只有 .NET Framework 4.8 自带的 csc（desktop/build.cmd），
    /// 没有 MSBuild / SDK / fxc，也就没有编译 HLSL 的手段；而 WinIsland 的主窗口是
    /// AllowsTransparency=true 的分层窗口，WPF 在这条路上退回软件光栅化，
    /// ShaderEffect（PixelShader ps_2_0）不在软件路径的支持范围内。
    /// 结论：自定义 shader 在这条链上既做不出来也用不起来，所以位移在 CPU 上算，
    /// 只算「边带」那一圈（不是整块岛），结果写进一张 WriteableBitmap 交给 WPF 合成。
    /// 这样不引入任何原生依赖、不新增窗口、不新增抓屏。
    ///
    /// ── 许可 ───────────────────────────────────────────────────────────────
    /// 算法与位移图结构来自上述仓库，其许可为：
    ///
    ///   MIT License
    ///   Copyright (c) 2025 Nikita Stadnik
    ///
    ///   Permission is hereby granted, free of charge, to any person obtaining a copy
    ///   of this software and associated documentation files (the "Software"), to deal
    ///   in the Software without restriction, including without limitation the rights
    ///   to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
    ///   copies of the Software, and to permit persons to whom the Software is
    ///   furnished to do so, subject to the following conditions:
    ///
    ///   The above copyright notice and this permission notice shall be included in all
    ///   copies or substantial portions of the Software.
    ///
    ///   THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
    ///   IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
    ///   FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
    ///   AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
    ///   LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
    ///   OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
    ///   SOFTWARE.
    ///
    /// 完整许可见 desktop/LICENSES/liquid-glass-MIT.txt（原文）与
    /// THIRD-PARTY-NOTICES.md（按文件的移植边界索引）。
    /// </summary>
    sealed class GlassBend
    {
        /* ---------------------------------------------------------- 源：桌面帧

           折射要采的是「岛底下那张桌面」，采样范围比岛大一圈（位移会把视线推到
           带外）。基底那层已经由 BlurEffect(16) 糊过了，折射层必须糊到同一个量级，
           否则边上会出现「一圈更清楚的玻璃」——那不是折射，是接缝。

           所以这里保存的不是原始帧，而是「降采样 + 盒式模糊(3 遍)」的一份小图：
           模糊半径按 DIP 算，和基底的 16 DIP 对齐；降采样让成本与 DPI 解耦
           （144% 缩放下不需要 2 倍的像素量去表示一张本来就很糊的图）。 */
        byte[] _src;             // BGRA，已模糊
        byte[] _raw;             // 抓屏原样的临时副本（Marshal.Copy 用，复用）
        byte[] _tmp;             // 模糊中间缓冲（复用）
        int _sw, _sh, _stride;
        double _q = 1.0;         // 模糊缓冲 px / 抓屏 px
        double _bps = 0.5;       // 模糊缓冲 px / DIP
        bool _srcOk;

        /* -------------------------------------------------------- 目标：边带纹理 */
        byte[] _tex;
        int _tw, _th;
        double _tps = 1.0;       // 纹理 px / DIP
        WriteableBitmap _bmp;

        public WriteableBitmap Bitmap { get { return _bmp; } }

        /// <summary>
        /// 门卫用的「能不能开始画」。只看源有没有喂进来。
        ///
        /// 不能用 Ready：Ready 还要求 _bmp 存在，而 _bmp 本身是在第一次 Render 里
        /// 才创建的 —— 拿 Ready 当门卫会自锁（第一次永远进不去，之后一直空转）。
        /// 这个坑在验收里表现为「日志里没有 GLASS bend，玻璃看着正常但边缘没弯」。
        /// </summary>
        public bool HasSource { get { return _srcOk && _sw > 0 && _sh > 0; } }

        /// <summary>真的画出来了（源和纹理都在），给日志/诊断用。</summary>
        public bool Ready { get { return _srcOk && _bmp != null; } }
        public double LastCostMs;
        public int Renders;
        public int FeedCount;

        /// <summary>模糊缓冲里每 DIP 多少像素。给外面写日志用。</summary>
        public double SourcePxPerDip { get { return _bps; } }
        public int SourceWidth { get { return _sw; } }
        public int SourceHeight { get { return _sh; } }

        /* ====================================================================
           喂源
           ==================================================================== */

        /// <summary>
        /// 用刚抓到的桌面帧刷新源。ptr 是 32bpp 的 BGRA（GDI BitBlt 出来 alpha
        /// 字节无意义，一律按不透明处理）。
        /// </summary>
        public void Feed(IntPtr ptr, int w, int h, int stride, double pxPerDip)
        {
            if (ptr == IntPtr.Zero || w <= 0 || h <= 0 || stride < w * 4) return;

            int need = stride * h;
            if (_raw == null || _raw.Length < need) _raw = new byte[need];
            Marshal.Copy(ptr, _raw, 0, need);
            Feed(_raw, w, h, stride, pxPerDip);
        }

        /// <summary>字节数组版本的 Feed（启动快照走这条）。</summary>
        public void Feed(byte[] bgra, int w, int h, int stride, double pxPerDip)
        {
            if (bgra == null || w <= 0 || h <= 0 || stride < w * 4) return;
            if (pxPerDip <= 0.1) pxPerDip = 1.0;

            /* 目标密度：固定 0.75 px/DIP。

               为什么不是「跟着 DPI 走」：盒式模糊的半径只能取整数像素，
               一旦缓冲密度随 DPI 变，同一个 16 DIP 的模糊在 100% 下是 7px、
               在 200% 下是 12px，两次的核不一样，量出来的色散会差半 DIP ——
               也就是「换个显示器玻璃变味」。固定成 DIP 密度之后，四档缩放
               模糊的是同一张图，差别只剩最终纹理的分辨率（重采样而已）。

               再按「最长边不超过 640」压一次：启动快照是整屏（1707 宽），
               不压的话一次性要多算 4 倍的像素，而它只在头 700ms 用得上。 */
            double bps = 0.75;
            int longest = w > h ? w : h;
            if (longest * (bps / pxPerDip) > 640.0)
                bps = 640.0 * pxPerDip / longest;

            double q = bps / pxPerDip;
            int bw = (int)Math.Round(w * q);
            int bh = (int)Math.Round(h * q);
            if (bw < 4) bw = 4;
            if (bh < 4) bh = 4;
            if (bw > w) bw = w;
            if (bh > h) bh = h;
            q = (double)bw / w;

            int bstride = bw * 4;
            if (_src == null || _sw != bw || _sh != bh || _stride != bstride)
            {
                _src = new byte[bstride * bh];
                _tmp = new byte[bstride * bh];
                _sw = bw; _sh = bh; _stride = bstride;
            }
            _q = q;
            _bps = bps;

            /* 最近邻降采样：源本来就是一张要糊掉的图，采样点差半个像素没意义 */
            for (int y = 0; y < bh; y++)
            {
                int sy = (int)(y / q);
                if (sy >= h) sy = h - 1;
                int srow = sy * stride;
                int drow = y * bstride;
                for (int x = 0; x < bw; x++)
                {
                    int sx = (int)(x / q);
                    if (sx >= w) sx = w - 1;
                    int si = srow + sx * 4;
                    int di = drow + x * 4;
                    _src[di] = bgra[si];
                    _src[di + 1] = bgra[si + 1];
                    _src[di + 2] = bgra[si + 2];
                    _src[di + 3] = 255;
                }
            }

            /* 与基底 BlurEffect(16 DIP) 对齐。WPF 的 Radius 是核半径而不是 sigma，
               实测它的扩散大致落在 radius 上，所以这里按「盒式 3 遍 ≈ 半径 r 的高斯」
               取 r ≈ 16*boltzmann…(见 verify/glass-bend.ps1 的接缝测量)，
               系数取 0.75 让折射层略糊一点：糊过头只是「更厚」，糊不够会露结构。 */
            int rb = (int)Math.Round(16.0 * bps * 0.75);
            if (rb < 1) rb = 1;
            BoxBlur(rb);
            _srcOk = true;
            FeedCount++;
        }

        void BoxBlur(int r)
        {
            for (int pass = 0; pass < 3; pass++)
            {
                BoxH(_src, _tmp, _sw, _sh, _stride, r);
                BoxV(_tmp, _src, _sw, _sh, _stride, r);
            }
        }

        static void BoxH(byte[] src, byte[] dst, int w, int h, int stride, int r)
        {
            int div = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                int sb = 0, sg = 0, sr = 0;
                for (int k = -r; k <= r; k++)
                {
                    int x = k < 0 ? 0 : (k >= w ? w - 1 : k);
                    int i = row + x * 4;
                    sb += src[i]; sg += src[i + 1]; sr += src[i + 2];
                }
                for (int x = 0; x < w; x++)
                {
                    int o = row + x * 4;
                    dst[o] = (byte)(sb / div);
                    dst[o + 1] = (byte)(sg / div);
                    dst[o + 2] = (byte)(sr / div);
                    dst[o + 3] = 255;

                    int xa = x + r + 1; if (xa >= w) xa = w - 1;
                    int xr = x - r; if (xr < 0) xr = 0;
                    int ia = row + xa * 4;
                    int ir = row + xr * 4;
                    sb += src[ia] - src[ir];
                    sg += src[ia + 1] - src[ir + 1];
                    sr += src[ia + 2] - src[ir + 2];
                }
            }
        }

        static void BoxV(byte[] src, byte[] dst, int w, int h, int stride, int r)
        {
            int div = 2 * r + 1;
            for (int x = 0; x < w; x++)
            {
                int col = x * 4;
                int sb = 0, sg = 0, sr = 0;
                for (int k = -r; k <= r; k++)
                {
                    int y = k < 0 ? 0 : (k >= h ? h - 1 : k);
                    int i = y * stride + col;
                    sb += src[i]; sg += src[i + 1]; sr += src[i + 2];
                }
                for (int y = 0; y < h; y++)
                {
                    int o = y * stride + col;
                    dst[o] = (byte)(sb / div);
                    dst[o + 1] = (byte)(sg / div);
                    dst[o + 2] = (byte)(sr / div);
                    dst[o + 3] = 255;

                    int ya = y + r + 1; if (ya >= h) ya = h - 1;
                    int yr = y - r; if (yr < 0) yr = 0;
                    int ia = ya * stride + col;
                    int ir = yr * stride + col;
                    sb += src[ia] - src[ir];
                    sg += src[ia + 1] - src[ir + 1];
                    sr += src[ia + 2] - src[ir + 2];
                }
            }
        }

        /* ====================================================================
           渲染边带
           ==================================================================== */

        /// <summary>
        /// 把「岛体矩形范围内」的折射结果写进 WriteableBitmap。
        ///
        /// 参数全部是 DIP：w/h = 岛体尺寸，r = 圆角（与 Squircle.Build 同参），
        /// band = 折射带宽度（从岛边往里多少 DIP 还在折射），
        /// strength = 位移强度（岛边处的最大位移 = strength/2），
        /// chroma = R 与 B 通道的额外位移差（0 = 无色散），
        /// blurDip = 基底模糊半径（只用于边缘覆盖率衰减，和上面 16 的对齐一致）。
        ///
        /// originPxX/Y 是「岛体左上角」在抓屏源里的像素坐标，
        /// pxPerDip 是抓屏源的像素密度 —— 两者合起来就是 DeskViewbox 那套换算。
        /// </summary>
        public bool Render(double w, double h, double r, double band, double strength,
                           double chroma, double blurDip,
                           double originPxX, double originPxY, double pxPerDip,
                           double texPxPerDip)
        {
            if (!_srcOk) return false;
            if (w < 4 || h < 4) return false;
            if (band < 1) band = 1;
            if (strength < 0) strength = 0;
            if (chroma < 0) chroma = 0;

            long t0 = Stopwatch.GetTimestamp();

            /* 纹理密度：跟 DPI 走，但封顶 2 px/DIP 与 1600 长边，
               免得 4K/200% 下一次性铺出几兆的缓冲。 */
            double tps = texPxPerDip;
            if (tps < 0.75) tps = 0.75;
            if (tps > 2.0) tps = 2.0;
            int tw = (int)Math.Ceiling(w * tps);
            int th = (int)Math.Ceiling(h * tps);
            if (tw < 4) tw = 4;
            if (th < 4) th = 4;
            if (tw > 1600) { tps *= 1600.0 / tw; tw = 1600; }
            if (th > 1600) { tps *= 1600.0 / th; th = 1600; }

            if (_bmp == null || _tw != tw || _th != th)
            {
                _tex = new byte[tw * th * 4];
                _bmp = new WriteableBitmap(tw, th, 96, 96, PixelFormats.Bgra32, null);
                _tw = tw; _th = th;
            }
            _tps = (double)tw / w;

            Array.Clear(_tex, 0, _tex.Length);

            /* 位移图的两条渐变：起点是半径的 15%（照抄原实现），
               到 100% - 同一个值收口，夹在中间线性过渡；
               渐变之外夹在端点值上 —— 也就是「左边 +1、右边 -1」。 */
            double x0 = Math.Ceiling(r * 0.15) / w;      // 归一化到 [0,1]
            double x1 = 1.0 - Math.Ceiling(r * 0.15) / w;
            double y0 = Math.Ceiling(r * 0.15) / h;
            double y1 = 1.0 - Math.Ceiling(r * 0.15) / h;
            if (x1 - x0 < 1e-4) { x0 = 0.0; x1 = 1.0; }
            if (y1 - y0 < 1e-4) { y0 = 0.0; y1 = 1.0; }

            double srcPxPerDip = _q * pxPerDip;          // 模糊缓冲 px / DIP
            double scaleR = strength + 2.0 * chroma;
            double scaleG = strength + chroma;
            double scaleB = strength;

            for (int ty = 0; ty < th; ty++)
            {
                double dyDip = (ty + 0.5) / _tps;
                int drow = ty * tw * 4;

                /* 纵向位移的 u（原图的 Y 渐变）：上边 +1、下边 -1 */
                double ny = (dyDip / h - y0) / (y1 - y0);
                if (ny < 0) ny = 0; else if (ny > 1) ny = 1;
                double v = 1.0 - 2.0 * ny;

                for (int tx = 0; tx < tw; tx++)
                {
                    double dxDip = (tx + 0.5) / _tps;

                    /* ── 到岛边的距离（正值在内部） ── */
                    double edge = EdgeDistance(dxDip, dyDip, w, h, r);

                    if (edge <= 0 || edge >= band) continue;   // 岛外 / 带外：不画，留透明

                    /* t：0 = 贴着岛边（位移拉满），1 = 带到内侧尽头（位移归零）。
                       对应原实现里那块 blur(depth) 的中性圆角矩形。 */
                    double t = edge / band;
                    t = t * t * (3.0 - 2.0 * t);        // smoothstep
                    double damp = 1.0 - t;

                    double nx = (dxDip / w - x0) / (x1 - x0);
                    if (nx < 0) nx = 0; else if (nx > 1) nx = 1;
                    double u = 1.0 - 2.0 * nx;

                    /* 原实现里位移 = scale * (通道值 - 0.5)，通道 0..1 → ±0.5*scale。
                       u/v 在 +1 那一端指向岛内，所以边带看到的是「被压向边缘的内侧」。 */
                    double dxR = 0.5 * scaleR * u * damp;
                    double dxG = 0.5 * scaleG * u * damp;
                    double dxB = 0.5 * scaleB * u * damp;
                    double dyR = 0.5 * scaleR * v * damp;
                    double dyG = 0.5 * scaleG * v * damp;
                    double dyB = 0.5 * scaleB * v * damp;

                    /* alpha：带到内侧尽头（位移已经归零）再收到 0，
                       收到 0 的位置上两层像素本来就一样，所以这里不会有接缝。
                       外面再乘一条「边缘覆盖率」：基底那层被 BlurEffect 在元素边界
                       上糊出了半透明，折射层不跟着淡就会在岛边比基底更实。 */
                    double tail = (edge - band * 0.80) / (band * 0.20);
                    if (tail < 0) tail = 0; else if (tail > 1) tail = 1;
                    tail = tail * tail * (3.0 - 2.0 * tail);
                    double fade = 1.0 - tail;

                    double cov = blurDip > 0.01 ? edge / blurDip : 1.0;
                    if (cov > 1) cov = 1;
                    cov = 0.5 + 0.5 * cov;

                    int a = (int)(255.0 * fade * cov + 0.5);
                    if (a <= 0) continue;
                    if (a > 255) a = 255;

                    /* ── 逐通道取样（原实现的三次 feDisplacementMap + screen） ── */
                    double bx = originPxX * _q;
                    double by = originPxY * _q;

                    int iB = SampleChannel(dxDip + dxB, dyDip + dyB, bx, by, srcPxPerDip, 0);
                    int iG = SampleChannel(dxDip + dxG, dyDip + dyG, bx, by, srcPxPerDip, 1);
                    int iR = SampleChannel(dxDip + dxR, dyDip + dyR, bx, by, srcPxPerDip, 2);

                    int o = drow + tx * 4;
                    _tex[o] = (byte)iB;
                    _tex[o + 1] = (byte)iG;
                    _tex[o + 2] = (byte)iR;
                    _tex[o + 3] = (byte)a;
                }
            }

            _bmp.WritePixels(new System.Windows.Int32Rect(0, 0, tw, th), _tex, tw * 4, 0);

            LastCostMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            Renders++;
            return true;
        }

        /// <summary>
        /// 到岛边的有号距离（内部为正）。
        ///
        /// 岛体不是圆角矩形，是「直边 + 超椭圆圆角」（见 Squircle.cs，
        /// |x/r|^4 + |y/r|^4 = 1）。用圆角矩形的距离场会在四个角上把带子切掉一截
        /// （超椭圆在对角线上比圆弧鼓得更外），所以圆角段用超椭圆的隐式函数
        /// 除一次梯度当作距离 —— 这是个一次近似的 SDF，在边界附近足够准，
        /// 而带子只有 26 DIP 宽，误差完全看不出来。
        /// </summary>
        static double EdgeDistance(double x, double y, double w, double h, double r)
        {
            double ax = Math.Abs(x - w * 0.5);
            double ay = Math.Abs(y - h * 0.5);
            double straightX = w * 0.5 - ax;      // 到左右边
            double straightY = h * 0.5 - ay;      // 到上下边

            if (r <= 0.01) return Math.Min(straightX, straightY);

            double qx = ax - (w * 0.5 - r);
            double qy = ay - (h * 0.5 - r);
            if (qx <= 0 || qy <= 0) return Math.Min(straightX, straightY);

            double u = qx / r, v = qy / r;
            double u2 = u * u, v2 = v * v;
            double f = u2 * u2 + v2 * v2 - 1.0;                 // < 0 → 在角内
            double gx = 4.0 * u * u2 / r;
            double gy = 4.0 * v * v2 / r;
            double grad = Math.Sqrt(gx * gx + gy * gy);
            if (grad < 1e-6) return Math.Min(straightX, straightY);

            double d = -f / grad;
            /* 两个近似取小的那个：直边估计偏大，除梯度估计在角平分线附近略偏大，
               小的那个离"最近边界"更近，带子不会在角上被撑宽。 */
            return Math.Min(d, Math.Min(straightX, straightY));
        }

        /// <summary>
        /// 在某条通道上取一个样本，双线性插值。
        ///
        /// 为什么必须插值：模糊缓冲只有 0.6 px/DIP，而色散要的是「R 与 B 相差
        /// 2~3 DIP」这种亚像素级的偏移。最近邻取样会把它量化成整像素跳变，
        /// 于是 100% 和 200% 缩放下量出来的色散一个 4.0 一个 2.5 DIP，
        /// 玻璃在换显示器时「变味」。插值之后偏移是连续的，四档 DPI 结果一致。
        /// </summary>
        int SampleChannel(double dipX, double dipY, double baseX, double baseY,
                          double srcPxPerDip, int ch)
        {
            double sx = baseX + dipX * srcPxPerDip - 0.5;   // 像素中心在 i+0.5
            double sy = baseY + dipY * srcPxPerDip - 0.5;

            int ix = (int)Math.Floor(sx);
            int iy = (int)Math.Floor(sy);
            double tx = sx - ix;
            double ty = sy - iy;

            int x0 = ix < 0 ? 0 : (ix >= _sw ? _sw - 1 : ix);
            int x1 = ix + 1 < 0 ? 0 : (ix + 1 >= _sw ? _sw - 1 : ix + 1);
            int y0 = iy < 0 ? 0 : (iy >= _sh ? _sh - 1 : iy);
            int y1 = iy + 1 < 0 ? 0 : (iy + 1 >= _sh ? _sh - 1 : iy + 1);

            int r0 = y0 * _stride + ch;
            int r1 = y1 * _stride + ch;
            double v00 = _src[r0 + x0 * 4];
            double v10 = _src[r0 + x1 * 4];
            double v01 = _src[r1 + x0 * 4];
            double v11 = _src[r1 + x1 * 4];

            double top = v00 + (v10 - v00) * tx;
            double bot = v01 + (v11 - v01) * tx;
            return (int)(top + (bot - top) * ty + 0.5);
        }

        public void Reset()
        {
            _src = null; _tmp = null; _srcOk = false;
            _sw = _sh = _stride = 0;
        }
    }
}
