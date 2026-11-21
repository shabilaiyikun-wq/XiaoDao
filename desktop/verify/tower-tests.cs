using System;
using System.Collections.Generic;

/* ══ 待办塔的离线验收 ═════════════════════════════════════════════════

   只编 desktop/Tower.cs（纯物理，不引用 WPF），不开窗口、不碰任何用户数据，
   在内存里把塔搭起来、解到静止，然后断言这些事：

     1. 一摞纸片叠得住：落定后每张都在它该在的高度、几乎水平、没有 NaN、
        全都还在房间里；
     2. 站着不动的塔不会自己爬（接触记忆没被扔掉）；
     3. 抽走最下面那张，上面整摞真的掉下来 —— 掉的距离就是一张卡的高度；
     4. 用手端起一张、挪到房间另一头、松手：还在房间里，厚度不变，没有丢；
     5. 用 4000px/s 甩出去：被夹到上限、仍然回到房间里，不会飞走；
     6. 空房间：没有纸片时步进不出错，四堵墙也不动；
     7. 纸片大半悬空会翻下去 —— 这条是「力矩真的在算」的证据，不是补间；
     8. 所有纸片的坐标、角度全过程都是有限值。

   跑法：desktop\verify\tower-tests.cmd（编译 + 运行，退出码 0 = 全过）。 */

namespace WinIsland
{
    static class TowerTests
    {
        const double Dt = 1.0 / 240.0;

        /* 岛上的真实尺寸（和 TodoTower.cs 一致）：432x156 的房间，5 张 26 高的卡片。
           宽度**不再是个常数**：每张纸片的宽度跟着它那条待办的正文走
           （116..344 之间）。所以这里用「一张中等长度的纸条」当代表值，
           另外用一个混宽度的用例覆盖长短不一时才会出现的行为。 */
        const double RoomW = 432, RoomH = 156;
        const double CardH = 26;
        const double CardW = 300;        /* 代表值，不是唯一值 */
        const double Hw = CardW / 2, Hh = CardH / 2;
        const double K = CardH / 46.0;
        const double Mu = 0.85 - 0.45 * 0.65;
        const double Grav = (900 + 50 / 100.0 * 3300) * K;

        static readonly double[] Lean = new double[] { -0.42, 0.68, -0.15, 0.51, -0.86 };

        static int bad, asserts;

        static int Main(string[] args)
        {
            Console.WriteLine("[tower-tests] room=" + RoomW + "x" + RoomH
                              + " card=" + CardW + "x" + CardH
                              + " K=" + Math.Round(K, 3)
                              + " g=" + Math.Round(Grav, 1));

            TestStackStands();
            TestNoCreep();
            TestStrikeDropsTheRest();
            TestHandCarries();
            TestThrowIsCapped();
            TestEmptyRoom();
            TestTipping();
            TestMixedWidthsTopple();

            Console.WriteLine();
            if (bad == 0)
            {
                Console.WriteLine("TOWER TESTS OK (" + asserts + " asserts)");
                return 0;
            }
            Console.WriteLine("TOWER TESTS FAILED bad=" + bad + " of " + asserts + " asserts");
            return 1;
        }

        /* ── 1. 叠得住 ─────────────────────────────────────────────── */
        static void TestStackStands()
        {
            TowerWorld w = Stack(5);

            int n = Count(w);
            Check(n == 5, "stack: 5 cards in the world (got " + n + ")");

            for (int i = 0; i < n; i++)
            {
                TowerBody b = BySlot(w, i);
                /* slot 0 是最上面那张，所以它的 y 是 hh + (n-1)*卡高
                   （和 TodoTower.Build 里同一套摆法） */
                double want = Hh + (n - 1 - i) * CardH;
                Check(Math.Abs(b.Y - want) < 1.6,
                      "stack: card " + i + " rests at y=" + Round(b.Y) + " (want " + want + ")");
                Check(Math.Abs(b.A) < 0.06,
                      "stack: card " + i + " is level (a=" + Round(b.A, 3) + ")");
                Check(Inside(b), "stack: card " + i + " inside the room");
            }
            AllFinite(w, "stack");

            /* 塔顶应该正好是 5 张卡的高度 */
            double top = TopEdge(w);
            Check(Math.Abs(top - 5 * CardH) < 2.0,
                  "stack: top edge at " + Round(top) + " (want " + (5 * CardH) + ")");
        }

        /* ── 2. 站着不动就不许爬 ───────────────────────────────────── */
        static void TestNoCreep()
        {
            TowerWorld w = Stack(5);
            double[] before = new double[5];
            for (int i = 0; i < 5; i++) before[i] = BySlot(w, i).Y;

            for (int i = 0; i < 900; i++) Tower.Step(w, Dt, null);   /* 3.75s */

            double worst = 0;
            for (int i = 0; i < 5; i++)
                worst = Math.Max(worst, Math.Abs(BySlot(w, i).Y - before[i]));

            Check(worst < 0.5, "creep: worst drift " + Round(worst, 3) + "px over 900 steps (<0.5)");
        }

        /* ── 3. 抽走下面那张，上面整摞掉下来 ───────────────────────── */
        static void TestStrikeDropsTheRest()
        {
            TowerWorld w = Stack(5);
            TowerBody bottom = BySlot(w, 4);          /* slot 4 = 最下面 */
            double topBefore = TopEdge(w);
            int id = bottom.Id;

            /* 世界里的「抽走」就是把它从 bodies 里摘掉，接触一起摘掉
               （TodoTower.Strike 做的正是这一步，然后才写数据） */
            Tower.Remove(w, id);
            Tower.Settle(w, Dt, 2000);

            Check(Count(w) == 4, "strike: 4 cards left (got " + Count(w) + ")");
            double topAfter = TopEdge(w);
            double drop = topBefore - topAfter;
            Check(Math.Abs(drop - CardH) < 2.5,
                  "strike: the stack above fell " + Round(drop) + "px (want " + CardH + ")");
            Check(TopEdge(w) < topBefore - 10, "strike: it really moved down");

            for (int i = 0; i < Count(w); i++)
            {
                TowerBody b = BySlot(w, i);
                Check(Inside(b), "strike: card " + i + " still inside the room");
                Check(Math.Abs(b.A) < 0.25, "strike: card " + i + " did not end up on its side (a="
                                            + Round(b.A, 3) + ")");
            }
            AllFinite(w, "strike");
        }

        /* ── 4. 手端着一张走一趟 ─────────────────────────────────────
           用**真实**的卡片尺寸（302x26）：现在两侧各 65px，横向够走了。
           这条要是过不了，说明卡片又被做回「跟房间一样大」了。 */
        static void TestHandCarries()
        {
            double hw = Hw, hh = Hh;
            TowerWorld w = Playground(4, hw, hh);
            int n0 = Count(w);

            TowerBody took = BySlot(w, 0);
            int id = took.Id;
            double startX = took.X, startY = took.Y;

            /* 拿起来：质量变成四倍（不是不可动），世界知道谁在手里 */
            Tower.SetMass(took, 4);
            w.Held = id;

            /* 端到房间左上角：横向走到允许的最大偏移 */
            double tx = RoomW / 2 - Hw - 2, ty = RoomH - Hh - 2;
            for (int i = 0; i < 420; i++)
            {
                TowerBody b = Tower.Find(w, id);
                Tower.Steer(w, b, tx, ty, Dt, 0, 0);
                Tower.Step(w, Dt, null);
            }

            TowerBody held = Tower.Find(w, id);
            double err = Tower.Hypot(held.X - tx, held.Y - ty);
            Check(err < 12, "hand: carried to within " + Round(err) + "px of the target");
            double travelled = Tower.Hypot(held.X - startX, held.Y - startY);
            Check(travelled > 60,
                  "hand: it actually travelled (" + Round(travelled) + "px, "
                  + "side room is " + Round((RoomW - CardW) / 2, 1) + "px each side)");
            /* 横向必须真的挪开了：这是「左右拖得动」的直接断言 */
            Check(Math.Abs(held.X - startX) > 40,
                  "hand: it moved sideways by " + Round(Math.Abs(held.X - startX)) + "px");
            Check(Count(w) == n0, "hand: no card lost while carrying (got " + Count(w) + ")");

            /* 松手：重量还回去、速度留着、位置夹回房间 */
            w.Held = -1;
            Tower.SetMass(held, 1);
            double vmax = 1800 * (CardH / 46.0);
            held.Vx = Tower.Clamp(held.Vx, -vmax, vmax);
            held.Vy = Tower.Clamp(held.Vy, -vmax, vmax);

            Tower.Settle(w, Dt, 2000);
            Check(Count(w) == n0, "hand: still " + n0 + " cards after release (got " + Count(w) + ")");
            for (int i = 0; i < Count(w); i++)
            {
                TowerBody b = BySlot(w, i);
                Check(Inside(b, hw, hh), "hand: card " + i + " inside the room after release");
            }
            AllFinite(w, "hand");
        }

        /* ── 5. 甩出去也飞不走 ─────────────────────────────────────── */
        static void TestThrowIsCapped()
        {
            double hw = Hw, hh = Hh;
            TowerWorld w = Playground(3, hw, hh);
            TowerBody b = BySlot(w, 0);
            double vmax = 1800 * (CardH / 46.0);

            b.Vx = 4000; b.Vy = 3000; b.W = 40;
            b.Vx = Tower.Clamp(b.Vx, -vmax, vmax);
            b.Vy = Tower.Clamp(b.Vy, -vmax, vmax);
            Check(Math.Abs(b.Vx) <= vmax + 1e-9, "throw: vx clamped to " + Round(b.Vx, 1));

            Tower.Settle(w, Dt, 2400);
            Check(Inside(b, hw, hh), "throw: it came back inside the room at "
                                     + Round(b.X) + "," + Round(b.Y));
            Check(b.Finite, "throw: state is finite");
            AllFinite(w, "throw");
        }

        /* ── 6. 空房间 ─────────────────────────────────────────────── */
        static void TestEmptyRoom()
        {
            TowerWorld w = new TowerWorld();
            w.K = K; w.Gravity = Grav; w.Restitution = 0;
            TowerRoom r = Room(RoomW, RoomH);
            Tower.Walls(w, r, Mu, Hw, Hh);

            Check(Count(w) == 0, "empty: no cards");
            for (int i = 0; i < 200; i++) Tower.Step(w, Dt, null);
            Check(Count(w) == 0, "empty: still no cards after 200 steps");
            Check(w.Bodies.Count == 4, "empty: four walls, got " + w.Bodies.Count);
            for (int i = 0; i < w.Bodies.Count; i++)
                Check(w.Bodies[i].IsStatic, "empty: wall " + w.Bodies[i].Id + " is static");
            Check(w.Arbiters.Count == 0, "empty: no contacts, got " + w.Arbiters.Count);
        }

        /* ── 7. 大半悬空就翻下去 ───────────────────────────────────── */
        static void TestTipping()
        {
            TowerWorld w = new TowerWorld();
            w.K = 1; w.Gravity = 2000; w.Restitution = 0;
            TowerRoom r = Room(RoomW, RoomH);
            Tower.Walls(w, r, Mu, 60, 14);

            /* 下面这张 120 宽（半宽 60），上面那张的中心在 95 —— 比支撑边缘
               已经出去 35px：重心越过了支撑边缘，除了力矩没有别的可能让它倒下 */
            TowerBody under = Tower.Box(1, 0, 14, 60, 14, 1.0, 1);
            TowerBody over = Tower.Box(2, 95, 42, 60, 14, 1.0, 1);
            w.Bodies.Add(under);
            w.Bodies.Add(over);

            double a0 = over.A;
            for (int i = 0; i < 1000; i++) Tower.Step(w, Dt, null);
            double y1 = over.Y;
            for (int i = 0; i < 200; i++) Tower.Step(w, Dt, null);
            double drift = Math.Abs(over.Y - y1);

            Check(Math.Abs(over.A - a0) > 0.2,
                  "tipping: the overhanging card turned (a=" + Round(over.A, 3) + "rad)");
            Check(over.Y < 34, "tipping: it came down (y=" + Round(over.Y) + ", started at 42)");
            Check(drift < 0.5, "tipping: it came to rest (drift " + Round(drift, 3) + "px)");
            Check(over.Finite, "tipping: finite");
        }

        /* ── 8. 长短不一：窄纸条压在宽纸条边缘会翻下去 ───────────────
           宽度跟着正文走之后才有的行为。所有纸片同宽时，房间里根本没有
           横向余量把一张卡片的重心推出下面那张的边缘；长短不一就有了 ——
           一张短纸条斜搭在长纸条的一头，重心在外面，它就该掉下去。
           这条同时证明「宽高是逐张的」真的进了求解器，而不是只画在屏幕上。 */
        static void TestMixedWidthsTopple()
        {
            TowerWorld w = new TowerWorld();
            w.K = K; w.Gravity = Grav; w.Restitution = 0;
            TowerRoom r = Room(RoomW, RoomH);
            /* 墙按最宽的那一档放，和 TodoTower.Build 一致 */
            Tower.Walls(w, r, Mu, 344 / 2.0, Hh);

            /* 下面窄（120 宽，半宽 60）、上面宽（200 宽，半宽 100）。
               上面那张中心放在 100：它覆盖 0..200，下面那张只托到 60，
               重心在支撑边缘外面 40px —— 它必须翻下去。
               全部座标都在房间内（100+100 = 200 < 216），不靠墙兜底。 */
            double narrowHw = 60, wideHw = 100;
            double offset = 100;
            if (offset + wideHw > RoomW / 2)
            {
                Console.WriteLine("  [x] mixed: the test itself places the card outside the room ("
                                  + (offset + wideHw) + " > " + (RoomW / 2) + ")");
                bad++;
                return;
            }

            TowerBody under = Tower.Box(1, 0, Hh, narrowHw, Hh, 1.0, 1);
            TowerBody over = Tower.Box(2, offset, Hh * 3, wideHw, Hh, 1.0, 1);
            w.Bodies.Add(under);
            w.Bodies.Add(over);

            double y0 = over.Y;
            for (int i = 0; i < 1200; i++) Tower.Step(w, Dt, null);

            Check(true, "mixed: narrow-under-wide setup is inside the room");
            Check(over.Y < y0 - 4,
                  "mixed: the wide card overhanging the narrow one fell (y "
                  + Round(y0) + " -> " + Round(over.Y) + ")");
            Check(Math.Abs(over.A) > 0.15,
                  "mixed: it turned on the way down (a=" + Round(over.A, 3) + "rad)");
            Check(over.Finite, "mixed: finite");
            Check(Count(w) == 2, "mixed: both cards still in the world");
        }

        /* ── 工具 ──────────────────────────────────────────────────── */

        static TowerWorld Stack(int n)
        {
            TowerWorld w = new TowerWorld();
            w.K = K; w.Gravity = Grav; w.Restitution = 0;
            TowerRoom r = Room(RoomW, RoomH);
            Tower.Walls(w, r, Mu, Hw, Hh);

            for (int slot = 0; slot < n; slot++)
            {
                double x = Lean[slot % Lean.Length] * 0.45 * 16 * K;
                double y = Hh + (n - 1 - slot) * CardH;
                w.Bodies.Add(Tower.Box(slot, x, y, Hw, Hh, Mu, 1));
            }

            Tower.Settle(w, Dt, 1500);
            return w;
        }

        static TowerWorld Playground(int n)
        {
            return Playground(n, Hw, Hh);
        }

        static TowerWorld Playground(int n, double hw, double hh)
        {
            TowerWorld w = new TowerWorld();
            w.K = K; w.Gravity = Grav; w.Restitution = 0;
            TowerRoom r = Room(RoomW, RoomH);
            Tower.Walls(w, r, Mu, hw, hh);

            for (int slot = 0; slot < n; slot++)
            {
                double y = hh + (n - 1 - slot) * (hh * 2);
                w.Bodies.Add(Tower.Box(slot, 0, y, hw, hh, Mu, 1));
            }
            Tower.Settle(w, Dt, 1500);
            return w;
        }

        static TowerRoom Room(double w, double h)
        {
            TowerRoom r = new TowerRoom();
            r.L = -w / 2;
            r.R = w / 2;
            r.Floor = 0;
            r.Top = h;
            return r;
        }

        /* 卡片按「从上到下」的槽位取：slot 0 是最上面那张 */
        static List<TowerBody> Cards(TowerWorld w)
        {
            List<TowerBody> list = new List<TowerBody>();
            for (int i = 0; i < w.Bodies.Count; i++)
                if (w.Bodies[i].Id < Tower.IdFloor) list.Add(w.Bodies[i]);
            list.Sort(delegate(TowerBody a, TowerBody b)
            {
                if (a.Y > b.Y) return -1;
                if (a.Y < b.Y) return 1;
                return a.Id.CompareTo(b.Id);
            });
            return list;
        }

        static TowerBody BySlot(TowerWorld w, int slot)
        {
            List<TowerBody> list = Cards(w);
            if (slot < 0 || slot >= list.Count) return null;
            return list[slot];
        }

        static int Count(TowerWorld w)
        {
            int n = 0;
            for (int i = 0; i < w.Bodies.Count; i++)
                if (w.Bodies[i].Id < Tower.IdFloor) n++;
            return n;
        }

        static double TopEdge(TowerWorld w)
        {
            double top = 0;
            for (int i = 0; i < w.Bodies.Count; i++)
            {
                TowerBody b = w.Bodies[i];
                if (b.Id >= Tower.IdFloor) continue;
                if (b.Y + b.Hh > top) top = b.Y + b.Hh;
            }
            return top;
        }

        static bool Inside(TowerBody b)
        {
            return Inside(b, b.Hw, b.Hh);
        }

        static bool Inside(TowerBody b, double hw, double hh)
        {
            double l = -RoomW / 2 + hw - 1.5, r = RoomW / 2 - hw + 1.5;
            double fl = hh - 1.5, tp = RoomH - hh + 1.5;
            return b.X >= l && b.X <= r && b.Y >= fl && b.Y <= tp;
        }

        static void AllFinite(TowerWorld w, string tag)
        {
            for (int i = 0; i < w.Bodies.Count; i++)
            {
                TowerBody b = w.Bodies[i];
                if (b.Id >= Tower.IdFloor) continue;
                Check(b.Finite, tag + ": body " + b.Id + " is finite");
            }
        }

        static void Check(bool ok, string msg)
        {
            asserts++;
            if (ok)
            {
                Console.WriteLine("  PASS " + msg);
                return;
            }
            bad++;
            Console.WriteLine("  FAIL " + msg);
        }

        static string Round(double v)
        {
            return Math.Round(v, 2).ToString();
        }

        static string Round(double v, int digits)
        {
            return Math.Round(v, digits).ToString();
        }
    }
}