using System;
using System.Collections.Generic;

/* ══ 待办塔的物理 ══════════════════════════════════════════════════════

   一堆硬纸片，在重力下叠成塔。勾掉一条，那张纸片被抽走，上面那些失去
   支撑、在重力下掉下来；用手拿起一张，它撞开别的、被别的挡住，松手时
   带着手上的速度飞出去或落回去。

   ── 出处 ────────────────────────────────────────────────────────────
   本文件是 Bencho 的 Todo Tower 的物理部分移植到 C#：
     · blocks/todo-tower/Tower.tsx 里内联的 lab/rigid.ts（二维刚体求解器）
     · 同文件里的 lab/spring 注释所描述的「settle 就停帧」策略
     · steer()：一只「压不碎东西」的手
   Bencho: https://github.com/cabralorenzo/bencho ，MIT License，
   Copyright (c) 2026 Lorenzo Cabra。许可证全文见
   desktop/LICENSES/bencho-MIT.txt；移植时保留了原实现的数值与判定，
   只把坐标系、时间步和容器换成 C# / .NET Framework 4.8 的写法。

   ── 为什么照搬一个求解器，而不是「让它看起来像掉下来」 ─────────────
   塔要站得住，靠的是一组接触被冲量互相顶住；而「一张纸片大半悬空就会
   翻下去」是力矩的结论，不是一个 if 能编出来的。用一条补间动画假装，
   手一碰就露馅：纸片会穿模、会抖、会在叠到第四张时撑不住。所以这里
   是真正的分离轴 + 顺序冲量 + 摩擦 + warm starting。

   ── 坐标系 ──────────────────────────────────────────────────────────
   单位是本版面自己的像素（DIP），y 轴向上，角度是弧度、逆时针为正。
   这和 WPF 相反，转换只发生在 TodoTower.cs 的绘制那一层：物理不必知道
   y 是往下的。invM 为 0 的物体不响应力：地板、墙、天花板都是，
   「拿在手里的那张」不是（它是有重量的，见 TowerWorld.Held）。

   ── K：一档尺寸缩放 ─────────────────────────────────────────────────
   原实现是在 46 高的卡片、320 高的房间里调出来的。岛上的房间只有 150
   高、卡片 28 高，直接把 2550px/s² 搬过来会快得像掉落动画。所以加速度、
   速度阈值、接触容差这些「带时间的量」统一乘 K = CardH / 46，几何量
   （长度）也按同一比例，于是重力和卡片尺寸保持原来的关系，手感一致。
   无量纲的（摩擦、迭代次数、GRIP/CARRY）不缩放。 */

namespace WinIsland
{
    /// <summary>一个刚体：盒子，带位置、速度、质量与摩擦。</summary>
    class TowerBody
    {
        public int Id;
        public double X, Y;          /* 中心，y 向上 */
        public double A;             /* 弧度，逆时针 */
        public double Vx, Vy, W;     /* 线速度 + 角速度 */
        public double Hw, Hh;        /* 半宽半高 */
        public double InvM, InvI;    /* 0 表示不动 */
        public double Friction;

        public bool IsStatic { get { return InvM == 0 && InvI == 0; } }

        public bool Finite
        {
            get
            {
                return !(double.IsNaN(X + Y + A + Vx + Vy + W)
                         || double.IsInfinity(X + Y + A + Vx + Vy + W));
            }
        }
    }

    /// <summary>一个接触点（已裁到真正相贴的那条棱上）。</summary>
    class TowerContact
    {
        public double X, Y;          /* 接触点，世界坐标 */
        public double Nx, Ny;        /* 法线：从 a 指向 b */
        public double Sep;           /* 分离量：负数表示嵌进去了 */
        public int Key;              /* 特征键：warm starting 靠它认「同一个接触」 */
        public double Pn, Pt;        /* 累计法向/切向冲量 */
        public double Mn, Mt;        /* 有效质量 */
        public double Bias;          /* 位置修正的目标速度 */
        public bool Fresh;           /* 这一步新出现的接触 */
    }

    class TowerArbiter
    {
        public TowerBody A, B;
        public List<TowerContact> Cs;
        public double Friction;
    }

    /// <summary>房间：地板的 y、左右墙的 x、天花板的 y（世界坐标）。</summary>
    class TowerRoom
    {
        public double L, R, Top, Floor;
    }

    class TowerWorld
    {
        public List<TowerBody> Bodies = new List<TowerBody>();
        public double Gravity = 1552;
        public double Restitution;
        public double K = 1;                       /* 尺寸缩放，见文件头 */
        public Dictionary<long, TowerArbiter> Arbiters = new Dictionary<long, TowerArbiter>();
        /* 拿在手里的那张的 id；-1 表示没有 */
        public int Held = -1;
    }

    static class Tower
    {
        /* 墙/地板的 id 远离卡片的 id 段（卡片 0..MAX-1） */
        public const int IdFloor = 100, IdLeft = 101, IdRight = 102, IdRoof = 103;

        /* ── 一个接触最多把两块推开的力度（px/s）─────────────────────
           重叠量按深度成比例修正，而手把一张纸片按进塔里时，重叠可以是
           整整一张卡那么深。不封顶，这个修正就会以「被弹射」的形式到达：
           纸片和它下面那些一起跳起来。封顶之后，重叠在几帧里被缓缓推开。 */
        const double Push = 90;
        /* 低于这个接近速度的接触算「贴着」，不算「撞上」——
           静止接触上还算回弹，就是一摞在嗡嗡抖的塔 */
        const double Rvel = 90;
        const double Slop = 0.5;
        const double Bias = 0.2;
        const int Iter = 12;

        /* 手能给的加速度上限，以及跟随时允许的最高速度 */
        const double SteerSpeed = 2400;
        const double SteerAccel = 40000;
        const double SteerTurn = 14;
        /* 手上向侧向带动的力度：μ·g 的多少倍 */
        const double Grip = 8;
        /* 被端着的塔内部接触的摩擦倍数 */
        const double Carry = 1.6;

        /* 棱编号：一条边两个端点，用来在两步之间认出同一个接触 */
        const int E1 = 1, E2 = 2, E3 = 3, E4 = 4;

        public static double Hypot(double x, double y)
        {
            return Math.Sqrt(x * x + y * y);
        }

        public static double Clamp(double v, double lo, double hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        public static TowerBody Box(int id, double x, double y, double hw, double hh,
                                    double friction, double mass)
        {
            TowerBody b = new TowerBody();
            b.Id = id;
            b.X = x; b.Y = y; b.A = 0;
            b.Hw = hw; b.Hh = hh;
            b.Friction = friction;
            SetMass(b, mass);
            return b;
        }

        /* 质量按盒子算：I = m(w²+h²)/12，用半宽半高写成 12/(m(4hw²+4hh²)) */
        public static void SetMass(TowerBody b, double mass)
        {
            if (mass <= 0)
            {
                b.InvM = 0;
                b.InvI = 0;
                return;
            }
            b.InvM = 1.0 / mass;
            b.InvI = 12.0 / (mass * (4 * b.Hw * b.Hw + 4 * b.Hh * b.Hh));
        }

        /* ── 碰撞 ──────────────────────────────────────────────────── */

        sealed class Clip
        {
            public double X, Y;
            public int[] K = new int[4];   /* 四个端点各来自哪条棱 */
        }

        static int KeyOf(int[] k)
        {
            return k[0] | (k[1] << 4) | (k[2] << 8) | (k[3] << 12);
        }

        /* 参考的那条棱：法线在「被参考物」的局部坐标系里反向，取最反平行的那条边 */
        static void Incident(double hx, double hy, double px, double py,
                             double c, double s, double nx0, double ny0,
                             Clip a, Clip b)
        {
            double nx = -(c * nx0 + s * ny0);
            double ny = -(-s * nx0 + c * ny0);

            if (Math.Abs(nx) > Math.Abs(ny))
            {
                if (nx > 0)
                {
                    a.X = hx; a.Y = -hy; a.K[0] = 0; a.K[1] = 0; a.K[2] = E3; a.K[3] = E4;
                    b.X = hx; b.Y = hy; b.K[0] = 0; b.K[1] = 0; b.K[2] = E4; b.K[3] = E1;
                }
                else
                {
                    a.X = -hx; a.Y = hy; a.K[0] = 0; a.K[1] = 0; a.K[2] = E1; a.K[3] = E2;
                    b.X = -hx; b.Y = -hy; b.K[0] = 0; b.K[1] = 0; b.K[2] = E2; b.K[3] = E3;
                }
            }
            else if (ny > 0)
            {
                a.X = hx; a.Y = hy; a.K[0] = 0; a.K[1] = 0; a.K[2] = E4; a.K[3] = E1;
                b.X = -hx; b.Y = hy; b.K[0] = 0; b.K[1] = 0; b.K[2] = E1; b.K[3] = E2;
            }
            else
            {
                a.X = -hx; a.Y = -hy; a.K[0] = 0; a.K[1] = 0; a.K[2] = E2; a.K[3] = E3;
                b.X = hx; b.Y = -hy; b.K[0] = 0; b.K[1] = 0; b.K[2] = E3; b.K[3] = E4;
            }

            Clip[] both = new Clip[] { a, b };
            for (int i = 0; i < 2; i++)
            {
                Clip v = both[i];
                double x = v.X, y = v.Y;
                v.X = px + c * x - s * y;
                v.Y = py + s * x + c * y;
            }
        }

        /* 把多边形的接触点裁到另一条棱的内侧 */
        static void Clip2(List<Clip> vin, double nx, double ny, double offset, int edge,
                          List<Clip> outv)
        {
            double d0 = nx * vin[0].X + ny * vin[0].Y - offset;
            double d1 = nx * vin[1].X + ny * vin[1].Y - offset;
            if (d0 <= 0) outv.Add(vin[0]);
            if (d1 <= 0) outv.Add(vin[1]);
            if (d0 * d1 < 0)
            {
                double t = d0 / (d0 - d1);
                Clip v = new Clip();
                v.X = vin[0].X + t * (vin[1].X - vin[0].X);
                v.Y = vin[0].Y + t * (vin[1].Y - vin[0].Y);
                Clip src = d0 > 0 ? vin[0] : vin[1];
                v.K[0] = src.K[0]; v.K[1] = src.K[1]; v.K[2] = src.K[2]; v.K[3] = src.K[3];
                if (d0 > 0)
                {
                    v.K[0] = edge;
                    v.K[2] = 0;
                }
                else
                {
                    v.K[1] = edge;
                    v.K[3] = 0;
                }
                outv.Add(v);
            }
        }

        static readonly List<TowerContact> NoContact = new List<TowerContact>();

        /* 分离轴：先在 A 的坐标系里试，再在 B 的；挑一个最深的轴，
           再把对方的棱裁到这条轴的侧棱之间，得到 1~2 个接触点。 */
        public static List<TowerContact> Collide(TowerBody A, TowerBody B)
        {
            double cA = Math.Cos(A.A), sA = Math.Sin(A.A);
            double cB = Math.Cos(B.A), sB = Math.Sin(B.A);
            double dpx = B.X - A.X, dpy = B.Y - A.Y;
            double dAx = cA * dpx + sA * dpy, dAy = -sA * dpx + cA * dpy;
            double dBx = cB * dpx + sB * dpy, dBy = -sB * dpx + cB * dpy;
            double a11 = Math.Abs(cA * cB + sA * sB), a12 = Math.Abs(-cA * sB + sA * cB);
            double a21 = Math.Abs(-sA * cB + cA * sB), a22 = Math.Abs(sA * sB + cA * cB);

            double fAx = Math.Abs(dAx) - A.Hw - (a11 * B.Hw + a12 * B.Hh);
            double fAy = Math.Abs(dAy) - A.Hh - (a21 * B.Hw + a22 * B.Hh);
            if (fAx > 0 || fAy > 0) return NoContact;
            double fBx = Math.Abs(dBx) - (a11 * A.Hw + a21 * A.Hh) - B.Hw;
            double fBy = Math.Abs(dBy) - (a12 * A.Hw + a22 * A.Hh) - B.Hh;
            if (fBx > 0 || fBy > 0) return NoContact;

            const double REL = 0.95, ABS = 0.01;
            int axis = 0;
            double sep = fAx;
            double nx = dAx > 0 ? cA : -cA, ny = dAx > 0 ? sA : -sA;
            if (fAy > REL * sep + ABS * A.Hh)
            {
                axis = 1; sep = fAy;
                nx = dAy > 0 ? -sA : sA; ny = dAy > 0 ? cA : -cA;
            }
            if (fBx > REL * sep + ABS * B.Hw)
            {
                axis = 2; sep = fBx;
                nx = dBx > 0 ? cB : -cB; ny = dBx > 0 ? sB : -sB;
            }
            if (fBy > REL * sep + ABS * B.Hh)
            {
                axis = 3; sep = fBy;
                nx = dBy > 0 ? -sB : sB; ny = dBy > 0 ? cB : -cB;
            }

            double fnx, fny, front, snx, sny, neg, pos;
            int negE, posE;
            Clip incA = new Clip(), incB = new Clip();
            if (axis == 0 || axis == 1)
            {
                fnx = nx; fny = ny;
                double hmain = axis == 0 ? A.Hw : A.Hh;
                double hside = axis == 0 ? A.Hh : A.Hw;
                front = A.X * fnx + A.Y * fny + hmain;
                if (axis == 0) { snx = -sA; sny = cA; negE = E3; posE = E1; }
                else { snx = cA; sny = sA; negE = E2; posE = E4; }
                double side = A.X * snx + A.Y * sny;
                neg = -side + hside;
                pos = side + hside;
                Incident(B.Hw, B.Hh, B.X, B.Y, cB, sB, fnx, fny, incA, incB);
            }
            else
            {
                fnx = -nx; fny = -ny;
                double hmain = axis == 2 ? B.Hw : B.Hh;
                double hside = axis == 2 ? B.Hh : B.Hw;
                front = B.X * fnx + B.Y * fny + hmain;
                if (axis == 2) { snx = -sB; sny = cB; negE = E3; posE = E1; }
                else { snx = cB; sny = sB; negE = E2; posE = E4; }
                double side = B.X * snx + B.Y * sny;
                neg = -side + hside;
                pos = side + hside;
                Incident(A.Hw, A.Hh, A.X, A.Y, cA, sA, fnx, fny, incA, incB);
            }

            List<Clip> inc = new List<Clip>(2);
            inc.Add(incA);
            inc.Add(incB);

            List<Clip> c1 = new List<Clip>(2);
            Clip2(inc, -snx, -sny, neg, negE, c1);
            if (c1.Count < 2) return NoContact;
            List<Clip> c2 = new List<Clip>(2);
            Clip2(c1, snx, sny, pos, posE, c2);
            if (c2.Count < 2) return NoContact;

            List<TowerContact> outv = new List<TowerContact>(c2.Count);
            for (int i = 0; i < c2.Count; i++)
            {
                Clip v = c2[i];
                double s = fnx * v.X + fny * v.Y - front;
                if (s > 0) continue;

                int[] k = v.K;
                int key = axis >= 2
                    ? KeyOf(new int[] { k[2], k[3], k[0], k[1] })
                    : KeyOf(k);

                TowerContact c = new TowerContact();
                c.X = v.X - s * fnx;
                c.Y = v.Y - s * fny;
                c.Nx = nx; c.Ny = ny;
                c.Sep = s;
                c.Key = key;
                c.Fresh = true;
                outv.Add(c);
            }
            return outv.Count == 0 ? NoContact : outv;
        }

        static long PairKey(int a, int b)
        {
            int lo = a < b ? a : b;
            int hi = a < b ? b : a;
            return ((long)lo << 32) | (uint)hi;
        }

        /* 一张纸片离开时把它的接触一起带走，否则压在它上面的那些会
           被一个「来自虚无」的冲量继续托着 */
        public static void Remove(TowerWorld W, int id)
        {
            for (int i = W.Bodies.Count - 1; i >= 0; i--)
                if (W.Bodies[i].Id == id) W.Bodies.RemoveAt(i);

            List<long> drop = new List<long>();
            foreach (KeyValuePair<long, TowerArbiter> kv in W.Arbiters)
                if (kv.Value.A.Id == id || kv.Value.B.Id == id) drop.Add(kv.Key);
            for (int i = 0; i < drop.Count; i++) W.Arbiters.Remove(drop[i]);

            if (W.Held == id) W.Held = -1;
        }

        public static TowerBody Find(TowerWorld W, int id)
        {
            for (int i = 0; i < W.Bodies.Count; i++)
                if (W.Bodies[i].Id == id) return W.Bodies[i];
            return null;
        }

        /* ── 一步 ────────────────────────────────────────────────────
           dt 固定（240Hz）。`hit` 听到这一步最重的一次新撞击（px/s）。 */
        public static void Step(TowerWorld W, double dt, Action<double> hit)
        {
            double inv = 1 / dt;
            double k = W.K <= 0 ? 1 : W.K;
            double push = Push * k, rvel = Rvel * k, slop = Slop * k;
            List<TowerBody> B = W.Bodies;
            double hardest = 0;

            for (int i = 0; i < B.Count; i++)
            {
                for (int j = i + 1; j < B.Count; j++)
                {
                    TowerBody a = B[i], b = B[j];
                    long key = PairKey(a.Id, b.Id);

                    /* 两个都动不了的物体没有接触，也**不能留着**接触。
                       这一句以前写在取 key 之前，于是「本来落在地板上的
                       一张纸片，后来变成不可动」时，它和地板的那条接触
                       留在了表里、每步还在解 —— 两边质量都是 0，1/0，
                       位置直接变 NaN。NaN 的纸片画不出来，也再也拿不起来。 */
                    if (a.InvM == 0 && b.InvM == 0 && a.InvI == 0 && b.InvI == 0)
                    {
                        W.Arbiters.Remove(key);
                        continue;
                    }

                    double r = Hypot(a.Hw, a.Hh) + Hypot(b.Hw, b.Hh);
                    double dx = b.X - a.X, dy = b.Y - a.Y;
                    List<TowerContact> cs = (dx * dx + dy * dy > r * r)
                        ? NoContact
                        : Collide(a, b);
                    if (cs.Count == 0)
                    {
                        W.Arbiters.Remove(key);
                        continue;
                    }

                    TowerArbiter old;
                    if (W.Arbiters.TryGetValue(key, out old) && ReferenceEquals(old.A, a))
                    {
                        for (int ci = 0; ci < cs.Count; ci++)
                        {
                            TowerContact c = cs[ci];
                            for (int oi = 0; oi < old.Cs.Count; oi++)
                            {
                                TowerContact o = old.Cs[oi];
                                if (o.Key != c.Key) continue;
                                c.Pn = o.Pn;
                                c.Pt = o.Pt;
                                c.Fresh = false;
                                break;
                            }
                        }
                    }

                    TowerArbiter arb = new TowerArbiter();
                    arb.A = a; arb.B = b; arb.Cs = cs;
                    arb.Friction = Math.Sqrt(a.Friction * b.Friction);
                    W.Arbiters[key] = arb;
                }
            }

            /* ── 拿着东西时的摩擦 ──────────────────────────────────
               只在「被端着的这一摞」之内加大摩擦（手里的那张 + 骑在它上面的），
               于是它们像一个整体移动；手里的那张对别的一切照旧自由滑动 ——
               地板、墙，以及它原来压着的那些。最后这一条很要紧：跟下面的
               纸片之间也有摩擦的话，正被抽出来的那张会被拖回指针后面，
               而骑在它上面的那些会跑在前面掉下去。 */
            TowerBody held = W.Held < 0 ? null : Find(W, W.Held);
            if (held != null)
            {
                List<TowerBody> carriedList = Riders(W, held);
                HashSet<int> carried = new HashSet<int>();
                carried.Add(held.Id);
                for (int i = 0; i < carriedList.Count; i++) carried.Add(carriedList[i].Id);

                foreach (KeyValuePair<long, TowerArbiter> kv in W.Arbiters)
                {
                    TowerArbiter arb = kv.Value;
                    bool aIn = carried.Contains(arb.A.Id), bIn = carried.Contains(arb.B.Id);
                    if (aIn && bIn)
                        arb.Friction = Carry * Math.Sqrt(arb.A.Friction * arb.B.Friction);
                    else if (arb.A.Id == held.Id || arb.B.Id == held.Id)
                        arb.Friction = 0;
                }
            }

            for (int i = 0; i < B.Count; i++)
            {
                TowerBody b = B[i];
                if (b.InvM == 0) continue;
                b.Vy -= W.Gravity * dt;
                /* 一点空气，让立在角上的纸片在有生之年停下来，而不是很久以后 */
                b.W *= 0.995;
            }

            /* warm start：把上一步的冲量先加回去，堆才站得住 */
            foreach (KeyValuePair<long, TowerArbiter> kv in W.Arbiters)
            {
                TowerArbiter arb = kv.Value;
                for (int ci = 0; ci < arb.Cs.Count; ci++)
                {
                    TowerContact c = arb.Cs[ci];
                    TowerBody a = arb.A, b = arb.B;
                    double r1x = c.X - a.X, r1y = c.Y - a.Y;
                    double r2x = c.X - b.X, r2y = c.Y - b.Y;
                    double rn1 = r1x * c.Nx + r1y * c.Ny;
                    double rn2 = r2x * c.Nx + r2y * c.Ny;
                    double kn = a.InvM + b.InvM
                        + a.InvI * (r1x * r1x + r1y * r1y - rn1 * rn1)
                        + b.InvI * (r2x * r2x + r2y * r2y - rn2 * rn2);
                    c.Mn = kn > 1e-12 ? 1 / kn : 0;

                    double tx = c.Ny, ty = -c.Nx;
                    double rt1 = r1x * tx + r1y * ty;
                    double rt2 = r2x * tx + r2y * ty;
                    double kt = a.InvM + b.InvM
                        + a.InvI * (r1x * r1x + r1y * r1y - rt1 * rt1)
                        + b.InvI * (r2x * r2x + r2y * r2y - rt2 * rt2);
                    c.Mt = kt > 1e-12 ? 1 / kt : 0;

                    double deepest = c.Sep + slop;
                    if (deepest > 0) deepest = 0;
                    c.Bias = Math.Min(push, -Bias * inv * deepest);

                    double dvx = b.Vx - b.W * r2y - a.Vx + a.W * r1y;
                    double dvy = b.Vy + b.W * r2x - a.Vy - a.W * r1x;
                    double vn = dvx * c.Nx + dvy * c.Ny;
                    if (c.Fresh && vn < -rvel)
                    {
                        double rb = -W.Restitution * vn;
                        if (rb > c.Bias) c.Bias = rb;
                        if (-vn > hardest) hardest = -vn;
                    }

                    double px = c.Pn * c.Nx + c.Pt * tx;
                    double py = c.Pn * c.Ny + c.Pt * ty;
                    a.Vx -= a.InvM * px; a.Vy -= a.InvM * py;
                    a.W -= a.InvI * (r1x * py - r1y * px);
                    b.Vx += b.InvM * px; b.Vy += b.InvM * py;
                    b.W += b.InvI * (r2x * py - r2y * px);
                }
            }

            for (int it = 0; it < Iter; it++)
            {
                foreach (KeyValuePair<long, TowerArbiter> kv in W.Arbiters)
                {
                    TowerArbiter arb = kv.Value;
                    TowerBody a = arb.A, b = arb.B;
                    for (int ci = 0; ci < arb.Cs.Count; ci++)
                    {
                        TowerContact c = arb.Cs[ci];
                        double r1x = c.X - a.X, r1y = c.Y - a.Y;
                        double r2x = c.X - b.X, r2y = c.Y - b.Y;

                        double dvx = b.Vx - b.W * r2y - a.Vx + a.W * r1y;
                        double dvy = b.Vy + b.W * r2x - a.Vy - a.W * r1x;
                        double vn = dvx * c.Nx + dvy * c.Ny;
                        double dpn = c.Mn * (-vn + c.Bias);
                        double pn0 = c.Pn;
                        c.Pn = Math.Max(pn0 + dpn, 0);
                        dpn = c.Pn - pn0;
                        double px = dpn * c.Nx, py = dpn * c.Ny;
                        a.Vx -= a.InvM * px; a.Vy -= a.InvM * py;
                        a.W -= a.InvI * (r1x * py - r1y * px);
                        b.Vx += b.InvM * px; b.Vy += b.InvM * py;
                        b.W += b.InvI * (r2x * py - r2y * px);

                        dvx = b.Vx - b.W * r2y - a.Vx + a.W * r1y;
                        dvy = b.Vy + b.W * r2x - a.Vy - a.W * r1x;
                        double tx = c.Ny, ty = -c.Nx;
                        double vt = dvx * tx + dvy * ty;
                        double dpt = c.Mt * -vt;
                        double max = arb.Friction * c.Pn;
                        double pt0 = c.Pt;
                        c.Pt = Math.Min(max, Math.Max(-max, pt0 + dpt));
                        dpt = c.Pt - pt0;
                        px = dpt * tx; py = dpt * ty;
                        a.Vx -= a.InvM * px; a.Vy -= a.InvM * py;
                        a.W -= a.InvI * (r1x * py - r1y * px);
                        b.Vx += b.InvM * px; b.Vy += b.InvM * py;
                        b.W += b.InvI * (r2x * py - r2y * px);
                    }
                }
            }

            for (int i = 0; i < B.Count; i++)
            {
                TowerBody b = B[i];
                b.X += b.Vx * dt;
                b.Y += b.Vy * dt;
                b.A += b.W * dt;
            }

            if (hit != null && hardest > 0) hit(hardest);
        }

        /* ── 一只压不碎东西的手 ──────────────────────────────────────
           把一张纸片朝某个点引导，就像被拿住的那张跟着指针走。

           以前是每一步直接把速度**设成**朝目标的方向 —— 那是不封顶的力：
           往塔里按，手里的纸片会按进去整整一张卡的深度，求解器顶不住
           「每一步都被重写的速度」，等重叠终于解开时是一次性解开的：
           塔炸开。实测：重叠 29.5px，纸片以 970px/s 飞出去。

           两道限制，缺一不可：
            · 手是让物体**加速**朝该去的速度走（封顶 accel），不是把速度
              直接传送过去 —— 于是它能给出的推力是有界的；
            · 它不会继续往「已经顶住的东西」里推：对这个物体每一条比
              一个像素还深的接触，把手的速度沿这条接触法线、朝对方体内
              的那一部分减掉。纸片停在表面上、沿表面滑动，就像一只手
              按着一摞书不会穿过去。 */
        public static void Steer(TowerWorld W, TowerBody b, double tx, double ty, double dt,
                                 double tvx, double tvy)
        {
            double k = W.K <= 0 ? 1 : W.K;
            double speed = SteerSpeed * k;
            double accel = SteerAccel * k;

            /* ── 跟得上，且不甩 ─────────────────────────────────────
               要的速度是**指针自己的速度**，加上一段不超过「剩下的距离
               够它刹住」的接近速度。第一版要的是 距离/(6 步)，只受最高
               速度封顶；加上拉扯封顶之后，它积起的速度比自己能卸掉的
               还多，冲过去、再荡回来：实测在快速移动时落后 344px，
               手停住半秒后还在左右摆 270px。 */
            double ex = tx - b.X, ey = ty - b.Y;
            double dist = Hypot(ex, ey);
            double close = 0;
            if (dist > 0)
            {
                double brake = Math.Min(speed, dist / (dt * 8));
                double sq = Math.Sqrt(1.2 * accel * dist);
                if (sq < brake) brake = sq;
                close = brake / dist;
            }
            double wantX = tvx + ex * close;
            double wantY = tvy + ey * close;
            double len = Hypot(wantX, wantY);
            if (len > speed)
            {
                wantX *= speed / len;
                wantY *= speed / len;
            }

            double dv = accel * dt;
            double vx = b.Vx + Clamp(wantX - b.Vx, -dv, dv);
            /* 重力是 step 加的，手在这里把它托住 */
            double vy = b.Vy + Clamp(wantY - b.Vy, -dv, dv) + W.Gravity * dt;

            List<TowerBody> carried = Riders(W, b);
            HashSet<int> aboard = new HashSet<int>();
            for (int i = 0; i < carried.Count; i++) aboard.Add(carried[i].Id);

            foreach (KeyValuePair<long, TowerArbiter> kv in W.Arbiters)
            {
                TowerArbiter arb = kv.Value;
                if (arb.A.Id != b.Id && arb.B.Id != b.Id) continue;

                TowerBody other = arb.A.Id == b.Id ? arb.B : arb.A;
                /* 绝不把「自己扛着的那些」当成障碍：背上的那一摞是靠自重
                   压下来的，把它读成墙，就永远举不起一张上面有东西的卡片 */
                if (aboard.Contains(other.Id)) continue;

                /* 顶着墙/地板时停在表面上。顶着另一张纸片时，只有真的被
                   压住了才停 —— 骑在手里那张上的纸片会因自重陷进去一两个
                   像素，把这点深度当成墙，就举不起一张上面有摞东西的卡片 */
                double depth = other.IsStatic ? -1.0 : -2.5;
                depth *= k;

                for (int ci = 0; ci < arb.Cs.Count; ci++)
                {
                    TowerContact c = arb.Cs[ci];
                    if (c.Sep > depth) continue;
                    /* 法线从 a 指向 b：朝对方体内是 +n（本物体是 a）或 -n（是 b） */
                    double nx = arb.A.Id == b.Id ? c.Nx : -c.Nx;
                    double ny = arb.A.Id == b.Id ? c.Ny : -c.Ny;
                    double into = vx * nx + vy * ny;
                    if (into > 0)
                    {
                        vx -= into * nx;
                        vy -= into * ny;
                    }
                }
            }

            /* ── 骑在它上面的跟着走 ───────────────────────────────
               每张骑在手边这张上的纸片，朝它的速度加速 —— 能加多快取决于
               摩擦（GRIP），再快就滑了。稳稳地端，摩擦够用，一摞像整体；
               猛拉、猛摇、急停要的更多：骑手只能拿到摩擦给得起的那些，
               落后、滑动，越过边缘就翻下去。

               给的是**速度**匹配，不是速度变化量的传递。以前传的是手给
               手持物带来的那份速度变化：不滑的时候是精确的，但骑手一落后，
               它的摩擦把手持物拖住，手下一次的变化就更小，骑手拿到的更少、
               落得更多 —— 结果连稳稳地端都会一张张滑掉。

               侧向只靠摩擦：μ·g，乘 GRIP，因为指针的起停比任何真手都猛，
               纯 μ·g 之下鼠标一晃就散了。

               向上是下面那张在推，不封顶；**向下只有重力**。没有东西压着
               骑手，所以手停住或下降得比重力更快时，上面那张会继续走、
               然后离开：轻轻一抬它就跳一下，一甩就把它扔到天花板上。 */
            for (int i = 0; i < carried.Count; i++)
            {
                TowerBody r = carried[i];
                double side = r.Friction * Grip * W.Gravity * dt;
                r.Vx += Clamp(vx - r.Vx, -side, side);
                double up = vy - r.Vy;
                if (up > 0) r.Vy += up;
            }

            b.Vx = vx;
            b.Vy = vy;
            b.W = Clamp(-b.A * SteerTurn, -SteerTurn, SteerTurn);
        }

        /* 压在 base 上的全部（直接或间接）：法线从下面那张朝上的接触才算 */
        public static List<TowerBody> Riders(TowerWorld W, TowerBody baseBody)
        {
            List<TowerBody> outv = new List<TowerBody>();
            HashSet<int> seen = new HashSet<int>();
            seen.Add(baseBody.Id);
            Queue<TowerBody> queue = new Queue<TowerBody>();
            queue.Enqueue(baseBody);

            while (queue.Count > 0)
            {
                TowerBody low = queue.Dequeue();
                foreach (KeyValuePair<long, TowerArbiter> kv in W.Arbiters)
                {
                    TowerArbiter arb = kv.Value;
                    bool aIsLow = arb.A.Id == low.Id;
                    if (!aIsLow && arb.B.Id != low.Id) continue;
                    TowerBody other = aIsLow ? arb.B : arb.A;
                    if (seen.Contains(other.Id)) continue;
                    if (other.IsStatic) continue;
                    if (arb.Cs.Count == 0) continue;
                    double up = aIsLow ? arb.Cs[0].Ny : -arb.Cs[0].Ny;
                    if (up < 0.5) continue;
                    seen.Add(other.Id);
                    outv.Add(other);
                    queue.Enqueue(other);
                }
            }
            return outv;
        }

        /* ── 房间的四堵墙 ────────────────────────────────────────────
           就地改，不重建。求解器记得每一步之间每条接触（按两个物体的 id
           索引），这份记忆才是塔能站着不动的根本；每帧新建一块地板等于
           把它扔掉，塔就会慢慢爬。返回「有没有真的动过」，没动过就别去
           吵醒一座已经静止的塔。 */
        public static bool Walls(TowerWorld W, TowerRoom room, double friction, double hw, double hh)
        {
            double l = room.L, r = room.R, top = room.Top, floor = room.Floor;
            double wide = r - l + 400;
            double tall = top - floor + 400;
            double mid = (l + r) / 2;

            bool moved = false;
            moved |= Want(W, IdFloor, mid, floor - 100, wide / 2, 100, friction);
            moved |= Want(W, IdLeft, l - 100, floor + tall / 2 - 200, 100, tall / 2, friction);
            moved |= Want(W, IdRight, r + 100, floor + tall / 2 - 200, 100, tall / 2, friction);
            moved |= Want(W, IdRoof, mid, top + 100, wide / 2, 100, friction);

            if (!moved) return false;

            /* 房间刚刚缩小过：留在外面的纸片放回来，否则墙会从它身上穿过去 */
            for (int i = 0; i < W.Bodies.Count; i++)
            {
                TowerBody b = W.Bodies[i];
                if (b.Id >= IdFloor) continue;
                b.X = Clamp(b.X, l + hw, r - hw);
                b.Y = Clamp(b.Y, floor + hh, top - hh);
            }
            return true;
        }

        static bool Want(TowerWorld W, int id, double x, double y, double hw, double hh, double friction)
        {
            TowerBody b = Find(W, id);
            if (b == null)
            {
                W.Bodies.Add(Box(id, x, y, hw, hh, friction, 0));
                return true;
            }
            double d = Math.Abs(b.X - x) + Math.Abs(b.Y - y)
                       + Math.Abs(b.Hw - hw) + Math.Abs(b.Hh - hh);
            if (d <= 0.5) return false;

            b.X = x; b.Y = y; b.Hw = hw; b.Hh = hh;
            b.Friction = friction;
            b.InvM = 0; b.InvI = 0;
            return true;
        }

        /* 一直走到静止（给自检和「减少动效」用）：不渲染，纯解算 */
        public static void Settle(TowerWorld W, double dt, int maxSteps)
        {
            for (int i = 0; i < maxSteps; i++) Step(W, dt, null);
            for (int i = 0; i < W.Bodies.Count; i++)
            {
                TowerBody b = W.Bodies[i];
                b.Vx = 0; b.Vy = 0; b.W = 0;
            }
        }
    }
}