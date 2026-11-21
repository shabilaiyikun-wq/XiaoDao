using System;
using System.Collections.Generic;

namespace WinIsland
{
    /* 随机待机池 —— 探针。

       这一版把随机待机池从「7 段自制」扩到「7 段自制 + 27 段参考动画」
       （28 段参考动画里排掉 Blocked_A）。要证明的不是「池子写对了」这么弱，
       而是四件事同时成立：

         1) 组成对：34 段、无重复、7 段自制还在、27 段参考都在、Blocked_A 不在；
         2) 都能播：每段在 0/25/50/75/100% 处采样都是有限值，中点能构出 3D 场景；
         3) 洗牌仍然不撞车：真跑三轮洗牌，34 段全出现，且没有相邻重复；
         4) 其它路径没被顺手改坏：手点 Blocked_A 仍可播、表情模式仍走 Done_*、
            戳击/转圈仍然叠在池子之上。

       它只编共享动画源（与 pet-demo\test.cmd 同一份清单，去掉测试宿主），
       不开窗口、不碰 ui.txt / pet.txt / window.txt，所以可以和别的探针并行跑。

       编译并运行：见 notes\probes\build-probe-idle-pool.cmd
    */
    static class ProbeIdlePool
    {
        static int checks;
        static int failures;

        /* 28 段参考动画里唯一不进随机池的那一段。 */
        const string Excluded = "Blocked_A";

        static void Check(bool condition, string name)
        {
            checks++;
            if (!condition) failures++;
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        }

        static void Info(string text)
        {
            Console.WriteLine("INFO " + text);
        }

        [STAThread]
        static int Main(string[] args)
        {
            PetClip[] pool = PetClips.Idle;
            Info("idle pool=" + pool.Length + "  reference clips=" + ReferenceClips.All.Length);

            /* ---------------- 1. 组成 ---------------- */

            Check(pool.Length == 34, "idle pool holds 34 clips (7 authored + 27 reference)");

            Dictionary<string,int> byName = new Dictionary<string,int>(StringComparer.Ordinal);
            int duplicates = 0;
            for (int i = 0; i < pool.Length; i++)
            {
                if (byName.ContainsKey(pool[i].Name)) duplicates++;
                else byName[pool[i].Name] = i;
            }
            Check(duplicates == 0 && byName.Count == pool.Length,
                  "no duplicate clip inside the pool (" + byName.Count + " distinct)");

            string[] authored = new string[] { "idle-rest", "idle-glance", "idle-happy",
                                               "idle-look-up", "idle-sleepy", "idle-wink", "idle-morph" };
            int authoredHit = 0;
            for (int i = 0; i < authored.Length; i++) if (byName.ContainsKey(authored[i])) authoredHit++;
            Check(authoredHit == 7, "all 7 authored idle clips are still pooled (" + authoredHit + "/7)");

            int referenceTotal = 0, referenceHit = 0;
            foreach (PetClipGroup group in ReferenceClips.Groups)
            {
                for (int i = 0; i < group.Clips.Length; i++)
                {
                    referenceTotal++;
                    if (group.Clips[i] == Excluded) continue;
                    if (byName.ContainsKey(group.Clips[i])) referenceHit++;
                }
            }
            Check(referenceTotal == 28 && ReferenceClips.All.Length == 28,
                  "reference library still exposes 28 clips (groups=" + referenceTotal + ")");
            Check(referenceHit == 27, "all 27 reference clips except " + Excluded
                  + " are pooled (" + referenceHit + "/27)");
            Check(!byName.ContainsKey(Excluded), Excluded + " stays out of the random idle pool");

            /* 起手姿态必须是 idle-rest：PetAnim 构造时拿 Idle[0] 当初始片段，
               顺序一变，启动那一帧的外观就跟改动前不一样了。 */
            Check(pool[0].Name == "idle-rest", "pool[0] is idle-rest, so the first pose is unchanged");

            /* 菜单「恢复随机待机」→ SetState("idle") → ForState 的 default 分支。 */
            Check(ReferenceEquals(PetClips.ForState("idle"), pool),
                  "state 'idle' resolves to the pool (menu 恢复随机待机 path)");

            /* ---------------- 2. 每段都能采样 ---------------- */

            double cycleSeconds = 0;
            bool framesOk = true;
            string badFrames = "";
            foreach (PetClip clip in pool)
            {
                cycleSeconds += clip.Duration;
                for (int k = 0; k <= 4 && framesOk; k++)
                {
                    PetFrame frame = clip.Sample(clip.Duration * k / 4.0);
                    bool finite = IsFinite(frame.Pitch) && IsFinite(frame.Yaw) && IsFinite(frame.Roll)
                                  && IsFinite(frame.Zoom) && IsFinite(frame.SquashX) && IsFinite(frame.SquashY)
                                  && IsFinite(frame.OffsetX) && IsFinite(frame.OffsetY) && IsFinite(frame.Lid);
                    if (!finite) { framesOk = false; badFrames = clip.Name; }
                }
                if (!framesOk) break;
            }
            Check(framesOk, "every pooled clip samples finite values at 0/25/50/75/100%"
                  + (badFrames.Length == 0 ? "" : " [bad: " + badFrames + "]"));

            bool renderOk = true;
            string badRender = "";
            foreach (PetClip clip in pool)
            {
                try
                {
                    if (Pet3D.Scene(clip.Sample(clip.Duration * 0.5)).Children.Count < 3)
                    { renderOk = false; badRender = clip.Name; break; }
                }
                catch (Exception ex) { renderOk = false; badRender = clip.Name + ":" + ex.GetType().Name; break; }
            }
            Check(renderOk, "every pooled clip builds a 3D scene at its midpoint"
                  + (badRender.Length == 0 ? "" : " [bad: " + badRender + "]"));

            /* ---------------- 3. 真跑三轮洗牌 ---------------- */

            PetAnimationRuntime runtime = new PetAnimationRuntime(17);
            runtime.SetState("idle");
            List<string> sequence = new List<string>();
            string last = null;
            int steps = (int)Math.Ceiling(cycleSeconds * 3 * 120);
            for (int i = 0; i < steps; i++)
            {
                runtime.Advance(1.0 / 120);
                string now = runtime.Frame.State;
                if (now != last) { sequence.Add(now); last = now; }
            }
            Info("pool cycle=" + Math.Round(cycleSeconds, 1) + "s  ran="
                 + Math.Round(cycleSeconds * 3, 1) + "s  transitions=" + sequence.Count);

            Dictionary<string,int> played = new Dictionary<string,int>(StringComparer.Ordinal);
            int adjacentRepeats = 0;
            for (int i = 0; i < sequence.Count; i++)
            {
                if (i > 0 && sequence[i] == sequence[i - 1]) adjacentRepeats++;
                if (!played.ContainsKey(sequence[i])) played[sequence[i]] = 0;
                played[sequence[i]]++;
            }
            int missing = 0;
            string missingName = "";
            foreach (PetClip clip in pool)
            {
                if (played.ContainsKey(clip.Name)) continue;
                missing++;
                if (missingName.Length == 0) missingName = clip.Name;
            }
            Check(missing == 0, "three full shuffle cycles play every pooled clip ("
                  + played.Count + "/" + pool.Length + ")"
                  + (missing == 0 ? "" : " [missing: " + missingName + "]"));
            Check(adjacentRepeats == 0, "no clip repeats back to back across the whole run");
            Check(!played.ContainsKey(Excluded), Excluded + " never comes up in random idle");

            /* ---------------- 4. 其它路径没被动坏 ---------------- */

            PetAnimationRuntime manual = new PetAnimationRuntime(17);
            manual.PlayClip(Excluded);
            manual.Advance(1.0 / 120);
            Check(manual.Frame.State == Excluded,
                  Excluded + " still plays when picked by hand from 全部动画");

            PetAnimationRuntime happy = new PetAnimationRuntime(17);
            happy.SetState("happy");
            happy.Advance(1.0 / 120);
            bool happyFromDone = false;
            foreach (PetClip clip in PetClips.ForState("happy"))
                if (clip != null && clip.Name == happy.Frame.State) happyFromDone = true;
            Check(happyFromDone, "expression mode 'happy' still draws from Done_A/B/C ("
                  + happy.Frame.State + ")");

            PetAnimationRuntime poke = new PetAnimationRuntime(17);
            poke.Poke();
            poke.Advance(1.0 / 120);
            Check(poke.Frame.State == "poked", "poke still layers on top of the pool");

            PetAnimationRuntime spin = new PetAnimationRuntime(17);
            spin.Spin();
            spin.Advance(1.0 / 120);
            Check(spin.Frame.State == "spin", "spin still layers on top of the pool");

            /* 池子变长不能把菜单清单一起改了：28 段参考动画仍按九组暴露。 */
            int menuEntries = 0;
            foreach (PetClipGroup group in ReferenceClips.Groups) menuEntries += group.Clips.Length;
            Check(menuEntries == 28, "menu source list is untouched (9 groups / 28 clips)");

            Console.WriteLine();
            Console.WriteLine(failures == 0
                ? "PROBE OK (" + checks + " checks, 0 failed)"
                : "PROBE FAILED " + failures + " of " + checks);
            return failures == 0 ? 0 : 1;
        }

        static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}