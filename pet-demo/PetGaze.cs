using System;

namespace WinIsland
{
    /* 注视数学 —— 移植自 Bencho 的 eye tracker（MIT）。

       上游：https://github.com/cabralorenzo/bencho/blob/main/blocks/eye-tracker/Watcher.tsx
       版权：Copyright (c) 2026 Lorenzo Cabra，MIT License。许可全文见本文件末尾。

       移植的范围只有四件事（上游那份组件的其余部分 —— 挤压形变、打字气泡、
       点击回话、形状 morph —— 一概不取）：

         1. 方向归一化：把「光标相对球心的偏移」折成单位方向再乘饱和后的幅值，
            所以远处给的是同一个方向，不会像逐轴钳制那样把 37° 掰成 45°
            （见 Follow 的注释，这在本项目里改了 demo 的一个真实偏差）。
         2. tanh 距离饱和：far = min(cap, tanh(d / reach) * follow * 1.5)。
            reach 在 1.1 个球半径处 —— 上游是 max(60, min(卡片宽,高) * 0.28)，
            对一颗 100 单位的球来说也正好约 1.1 个半径。这就是「适合桌宠的小尺寸」：
            桌宠停靠时球半径只有 8.4 DIP，拿 60px 去当 reach 会让眼睛几乎不转。
         3. 双轴弹簧：k = 0.06、d = 0.34（以 1/60 秒为单位）。这里换算成
            频率 / 阻尼比交给项目自带的 PetSpring，不再另写一份积分器。
         4. 双眼共享 yaw / pitch：yaw = asin(clamp(x))、pitch = -asin(clamp(y))。
            两只眼睛都从这一对角度读出来，所以它们不可能各指一个方向。

       **不**移植的：上游用 SVG 模拟出来的透视缩窄（`k` / `sx` / `opacity` 三项）。
       那是因为 SVG 里的眼睛只是一块平面矩形，只能靠缩放假装它在球面上。
       本项目的眼睛是真正的 3D 网格、挂在 1.015 球面上的切平面贴片，
       正交投影会按真实几何把它压窄、并用 z-buffer 把它挡掉 ——
       再叠一层模拟缩放就是同一件事做两遍，边缘会窄过头。 */

    /// <summary>注视数学：方向 / 饱和 / 球面转向。纯函数，可重放，可单测。</summary>
    static class PetGaze
    {
        /* ---- 距离饱和（上游 aim() 里的三个常数） ---- */

        /// <summary>上游 `Math.min(0.85, ...)` 的 1.5 倍增益。</summary>
        public const double TanhGain = 1.5;

        /// <summary>
        /// 饱和上限，单位是「注视量」—— 本项目的 ±1，等于 Pet3D.GazeShift 的
        /// 0.62 球面折算。上游写 0.85（那是球面半径的比例），两者不是同一个单位：
        /// 上游 0.85 的球面点 → asin(0.85) ≈ 58°；这里 1.0 → asin(0.62) ≈ 38°。
        /// 取 1.0 是因为往下游送的只有这一种单位，多一个换算就多一处会写错的地方；
        /// 38° 的转向配上切平面贴片的真实压窄，看起来已经是「整颗球转过头去」。
        /// </summary>
        public const double MaxAmount = 1.0;

        /// <summary>缺省跟随强度（上游默认 follow = 60，桌宠拖文件时用满一些）。</summary>
        public const double DefaultFollow = 80.0;

        /// <summary>
        /// reach = 半径 * 这个倍数。上游是 max(60, min(卡片宽,高) * 0.28)，
        /// 对一个边长 100 单位的球来说 = 1.1 个半径。
        /// </summary>
        public const double ReachRadii = 1.1;

        /// <summary>reach 的下限（DIP）。球再小也不让它塌成 0，否则 0/0。</summary>
        public const double MinReachDip = 4.0;

        /* ---- 双轴弹簧（上游 tick() 里的 k / d，单位 1/60 秒） ---- */

        /// <summary>上游 `const k = 0.06`。</summary>
        public const double SpringStiffness = 0.06;

        /// <summary>上游 `const d = 0.34 - bounce * 0.22`，bounce 取默认 0 时的值。</summary>
        public const double SpringDampingFactor = 0.34;

        /// <summary>上游把 dt 归一成「多少个 1/60 秒」。</summary>
        public const double SpringRefHz = 60.0;

        /* 上游 v += ((target - x) * k - v * d) * dt; x += v * dt
           是「时间以帧为单位」的半隐式欧拉：k 就是 ω²、d 就是 2ζω。
           换成项目里 PetSpring 的 (频率, 阻尼比) 形式：
               ω = sqrt(k) * 60 = 14.697 rad/s
               ζ = d / (2 sqrt(k)) = 0.694
           两者积分同一条连续方程，只是 PetSpring 内部按 1/120 s 细分，
           所以换采样率不会像上游那样改手感。 */
        public static readonly double SpringFrequency = Math.Sqrt(SpringStiffness) * SpringRefHz;
        public static readonly double SpringZeta = SpringDampingFactor / (2.0 * Math.Sqrt(SpringStiffness));

        public static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /// <summary>
        /// 偏移 → 注视量。dx / dy 是「光标相对球心」的屏上偏移（DIP，屏幕坐标系
        /// 的 y 向下为正 —— 调用方负责把 y 翻成向上），radius 是球在屏上的半径。
        ///
        /// 方向先归一化，再乘饱和后的幅值：这样远处只是「方向不变、幅值到顶」，
        /// 而不是逐轴钳制（δx=+300, δy=-400 逐轴钳会得到 (1,-1)，
        /// 把真实的 37° 变成 45° —— 上游的 far 乘单位向量的写法就是为了这个）。
        /// </summary>
        public static void Follow(double dx, double dy, double radius,
                                 double follow, out double gazeX, out double gazeY)
        {
            double d = Math.Sqrt(dx * dx + dy * dy);
            if (d < 1e-9) { gazeX = 0; gazeY = 0; return; }

            double reach = radius * ReachRadii;
            if (reach < MinReachDip) reach = MinReachDip;

            if (follow < 0) follow = 0;
            if (follow > 100) follow = 100;

            double far = Math.Tanh(d / reach) * (follow / 100.0) * TanhGain;
            if (far > MaxAmount) far = MaxAmount;

            gazeX = dx / d * far;
            gazeY = dy / d * far;
        }

        public static void Follow(double dx, double dy, double radius,
                                  out double gazeX, out double gazeY)
        {
            Follow(dx, dy, radius, DefaultFollow, out gazeX, out gazeY);
        }

        /// <summary>球的 y 向上、屏幕的 y 向下：这一处翻转集中在这里，别处不再翻。</summary>
        public static void FollowScreen(double dx, double dyDown, double radius,
                                        double follow, out double gazeX, out double gazeY)
        {
            Follow(dx, -dyDown, radius, follow, out gazeX, out gazeY);
        }

        public static void FollowScreen(double dx, double dyDown, double radius,
                                        out double gazeX, out double gazeY)
        {
            Follow(dx, -dyDown, radius, DefaultFollow, out gazeX, out gazeY);
        }

        /// <summary>
        /// 注视量 → 球面转向。上游：`as = v => Math.asin(clamp(v, -0.92, 0.92))`，
        /// `yaw = as(t.x)`、`pitch = -as(t.y)`。0.92 的钳制保证 asin 不会吃到 ±1
        /// 那个奇异点（那里导数无穷大，一帧的抖动就能把眼睛甩到背面）。
        /// </summary>
        public static void Turn(double gazeX, double gazeY, out double yaw, out double pitch)
        {
            yaw = Asin(Clamp(gazeX, -1.0, 1.0));
            pitch = -Asin(Clamp(gazeY, -1.0, 1.0));
        }

        public const double AsinLimit = 0.92;

        public static double Asin(double v)
        {
            return Math.Asin(Clamp(v, -AsinLimit, AsinLimit));
        }

        /// <summary>
        /// 球面上一个点，绕 y 轴转 yaw、再绕 x 轴转 pitch 之后落在哪里。
        /// 上游对两只眼睛用的就是这一组式子（眼睛先按 x0 摆在赤道两侧，
        /// z0 = sqrt(1 - x0²)，转完再取屏幕 x / y、用 z 当深度）。
        ///
        /// 这里额外保留起点自身的 y（上游把眼睛摆在 y = 0 的赤道上，
        /// 桌宠的脸整体上移 0.04，所以起点是球面上一个一般点）。
        /// </summary>
        public static void RotatePoint(double x, double y, double z,
                                       double yaw, double pitch,
                                       out double outX, out double outY, out double outZ)
        {
            double cy = Math.Cos(yaw), sy = Math.Sin(yaw);
            double cp = Math.Cos(pitch), sp = Math.Sin(pitch);

            double x1 = x * cy + z * sy;
            double z1 = -x * sy + z * cy;
            double y2 = y * cp - z1 * sp;
            double z2 = y * sp + z1 * cp;

            outX = x1; outY = y2; outZ = z2;
        }
    }

    /// <summary>
    /// 双轴弹簧跟随的注视目标。两个轴各一条弹簧、同样的 k / d —— 上游就是
    /// 「一对 x/y 速度分量」，不是两条独立的缓动曲线。
    ///
    /// 为什么必须弹簧而不是直接给目标：DragOver 的到达频率跟鼠标一样是抖的，
    /// 而桌宠每帧只推进一次；把抖动原样喂进 yaw/pitch，眼睛会在两个角度之间
    /// 一帧一跳（"快速跨越目标平滑" 就是判这一条）。弹簧同时负责「停下时
    /// 自己到位」——不需要额外的收敛判据。
    /// </summary>
    sealed class PetGazeTrack
    {
        readonly PetSpring _x = new PetSpring(0);
        readonly PetSpring _y = new PetSpring(0);

        public double X { get { return _x.X; } }
        public double Y { get { return _y.X; } }

        public double TargetX { get { return _x.Target; } }
        public double TargetY { get { return _y.Target; } }

        /// <summary>还在动（离目标超过 1e-4 或仍有速度）。诊断与自检用。</summary>
        public bool Moving
        {
            get
            {
                return Math.Abs(_x.X - _x.Target) > 1e-4 || Math.Abs(_x.Velocity) > 1e-4
                    || Math.Abs(_y.X - _y.Target) > 1e-4 || Math.Abs(_y.Velocity) > 1e-4;
            }
        }

        public void Aim(double x, double y)
        {
            _x.Target = PetGaze.Clamp(x, -1.0, 1.0);
            _y.Target = PetGaze.Clamp(y, -1.0, 1.0);
        }

        /// <summary>固定步长推进一次。step 用秒，PetSpring 自己按 1/120 s 细分。</summary>
        public void Advance(double step)
        {
            _x.Advance(step, PetGaze.SpringFrequency, PetGaze.SpringZeta);
            _y.Advance(step, PetGaze.SpringFrequency, PetGaze.SpringZeta);
        }

        /// <summary>跳到目标（不滑）。只在「换了一份全新的注视源」时用。</summary>
        public void Snap(double x, double y)
        {
            _x.Target = _x.X = PetGaze.Clamp(x, -1.0, 1.0);
            _y.Target = _y.X = PetGaze.Clamp(y, -1.0, 1.0);
            _x.Velocity = 0;
            _y.Velocity = 0;
        }

        public void Rest()
        {
            _x.Target = 0;
            _y.Target = 0;
        }
    }
}

/* ── 上游许可全文（MIT），移植部分受它约束 ────────────────────────────

   MIT License

   Copyright (c) 2026 Lorenzo Cabra

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

   上游的 Bencho 名称与山羊标志不在该许可内；本移植只取算法，不含任何品牌素材。 */