using System;

namespace WinIsland
{
    // Native adaptation of Qa/vt/tc/Lx/Eo/bx/Xb in grok-player.js.
    // Upstream and scope are recorded in ANIMATION-PORT.md.
    //
    // 两条曲线的系数必须与上游 grok-recipes.json 的 constants.ease 逐个对上，
    // 否则每一段关键帧的「手感」都不是参考里的那个：
    //   ease.nr = [0.42, 0, 0.58, 1]  —— 普通关键帧之间的插值（对齐的 ease-in-out）
    //   ease.dx = [0.40, 0, 0.20, 1]  —— 片段之间 0.35s 桥接的插值
    // 这里曾经把 dx 填进了 Smooth，又给 Bridge 另调了一条 (0.22,0.7,0.25,1)，
    // 结果是所有片段的起步都偏快、收尾偏拖，桥接反而比参考更软。
    struct PetEase
    {
        public double X1, Y1, X2, Y2;
        public PetEase(double x1, double y1, double x2, double y2)
        { X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; }
        public static readonly PetEase Smooth = new PetEase(0.42, 0, 0.58, 1);
        public static readonly PetEase Bridge = new PetEase(0.4, 0, 0.2, 1);
    }

    static class AnimationMath
    {
        public static double Clamp(double v, double lo, double hi) { return Math.Min(hi, Math.Max(lo, v)); }
        public static double Lerp(double a, double b, double t) { return a + (b - a) * t; }
        static double Cubic(double t, double a, double b)
        { double u = 1 - t; return 3 * u * u * t * a + 3 * u * t * t * b + t * t * t; }
        public static double Bezier(double x, PetEase e)
        {
            double t = x;
            for (int i = 0; i < 8; i++)
            {
                double u = 1 - t, error = Cubic(t, e.X1, e.X2) - x;
                double slope = 3 * u * u * e.X1 + 6 * u * t * (e.X2 - e.X1) + 3 * t * t * (1 - e.X2);
                if (Math.Abs(error) < 1e-6 || Math.Abs(slope) < 1e-6) break;
                t = Clamp(t - error / slope, 0, 1);
            }
            return Cubic(t, e.Y1, e.Y2);
        }
        public static double NearestAngle(double target, double current)
        { return target + 360 * Math.Floor((current - target) / 360 + 0.5); }
        public static double Pulse(double t, double start, double end)
        {
            if (t <= start || t >= end) return 0;
            double s = Math.Sin(Math.PI * (t - start) / (end - start)); return s * s;
        }
    }

    sealed class PetRandom
    {
        uint state;
        public PetRandom(uint seed) { state = seed; }
        public double Next()
        {
            unchecked
            {
                state += 1831565813u;
                uint n = (state ^ (state >> 15)) * (state | 1u);
                n ^= n + (n ^ (n >> 7)) * (n | 61u);
                return (n ^ (n >> 14)) / 4294967296.0;
            }
        }
        public int[] Shuffle(int count, int previous)
        {
            int[] a = new int[count]; for (int i = 0; i < count; i++) a[i] = i;
            for (int i = count - 1; i > 0; i--)
            { int j = (int)(Next() * (i + 1)), v = a[i]; a[i] = a[j]; a[j] = v; }
            int first = Array.FindIndex(a, delegate(int v) { return v != previous; });
            if (first > 0) { int v = a[0]; a[0] = a[first]; a[first] = v; }
            return a;
        }
    }

    sealed class PetSpring
    {
        public double X, Velocity, Target;
        public PetSpring(double value) { X = Target = value; }
        public void Advance(double seconds, double frequency, double damping)
        {
            int count = Math.Max(1, (int)Math.Ceiling(seconds * 120)); double dt = seconds / count;
            for (int i = 0; i < count; i++)
            {
                Velocity += (-2 * damping * frequency * Velocity - frequency * frequency * (X - Target)) * dt;
                X += Velocity * dt;
                if (double.IsNaN(X) || double.IsInfinity(X) || double.IsNaN(Velocity) || double.IsInfinity(Velocity))
                { X = Target; Velocity = 0; }
            }
        }
    }

    sealed class PetKey
    {
        public readonly double Time; public readonly double[] Value; public readonly PetEase Ease;
        public PetKey(double time, params double[] value) : this(time, PetEase.Smooth, value) { }
        public PetKey(double time, PetEase ease, params double[] value) { Time = time; Value = value; Ease = ease; }
    }
    sealed class PetTrack
    {
        readonly PetKey[] keys;
        public PetTrack(params PetKey[] values) { keys = values; }
        public double[] Sample(double time)
        {
            if (keys.Length == 0) return null;
            if (time <= keys[0].Time) return keys[0].Value;
            if (time >= keys[keys.Length - 1].Time) return keys[keys.Length - 1].Value;
            int i = 0; while (i < keys.Length - 2 && keys[i + 1].Time <= time) i++;
            PetKey a = keys[i], b = keys[i + 1];
            double t = AnimationMath.Bezier((time - a.Time) / Math.Max(b.Time - a.Time, 1e-9), a.Ease);
            double[] result = new double[a.Value.Length];
            for (int j = 0; j < result.Length; j++) result[j] = AnimationMath.Lerp(a.Value[j], b.Value[j], t);
            return result;
        }
    }
    struct PetImpulse
    {
        /* 三种脉冲。以前是 bool Spin，两态就够；加跳跃之后三态，
           再堆 bool 会出现「Spin 和 Jump 同时为真」这种没有意义的状态。 */
        public const int KindPoke = 0;
        public const int KindSpin = 1;
        public const int KindJump = 2;

        public int Kind; public double Age;
        public double Duration
        {
            get
            {
                if (Kind == KindSpin) return 0.9;
                if (Kind == KindJump) return 1.15;
                return 0.62;
            }
        }
        public void Apply(ref PetFrame f)
        {
            double t = Math.Min(1, Age / Duration);
            if (Kind == KindSpin) { f.Yaw += Math.PI * (1 - Math.Cos(Math.PI * t)); return; }
            if (Kind == KindJump)
            {
                /* 跳跃：下蹲蓄力 → 腾空 → 落地压缩 → 收。
                   和戳的区别不只是高度：戳（22.2px）是「被碰了一下」的单段弹跳，
                   跳跃有明确的三段（蓄力/腾空/落地），才读得出「它自己跳起来了」。

                   OffsetY 是负的向上（见 Position 轨道里 OffsetY=-v[1]*74，
                   以及戳那条 -22.2）。这里峰值 52px，约两倍身位。 */
                double crouch = AnimationMath.Pulse(t, 0, 0.22);   /* 下蹲 */
                double land = AnimationMath.Pulse(t, 0.74, 1);      /* 落地缓冲 */
                double air = AnimationMath.Pulse(t, 0.22, 0.74);    /* 腾空 */
                f.OffsetY -= 52.0 * Math.Sin(Math.PI * AnimationMath.Clamp((t - 0.16) / 0.62, 0, 1));
                f.SquashX *= 1 + 0.11 * (crouch + land) - 0.06 * air;
                f.SquashY *= 1 - 0.15 * (crouch + land) + 0.10 * air;
                return;
            }
            {
                double contact = AnimationMath.Pulse(t, 0, 0.18) + AnimationMath.Pulse(t, 0.82, 1);
                double air = AnimationMath.Pulse(t, 0.18, 0.82);
                f.OffsetY -= 22.2 * Math.Sin(Math.PI * AnimationMath.Clamp((t - 0.14) / 0.72, 0, 1));
                f.SquashX *= 1 + 0.08 * contact - 0.04 * air;
                f.SquashY *= 1 - 0.10 * contact + 0.06 * air;
            }
        }
    }
}
