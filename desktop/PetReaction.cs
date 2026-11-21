using System;
using System.Collections.Generic;

namespace WinIsland
{
    /* ============================================================ 事件 → 反应

       桌宠的动作 API（Poke/Spin/SetState/PlayClip，见 PetStage）本来就有，
       缺的是「什么时候该动」。这个文件就是那一层：一张表 + 一个裁决器。

       为什么单独成文件、且一行 WPF 都不碰：
       冷却和优先级抢占是最容易写错的东西（差一毫秒、优先级反了、回绕算错），
       而它们跟窗口、渲染、Dispatcher 一点关系都没有。放在纯类里，
       就能被 pet-demo/test.cmd 离线断言 —— 不用开窗、不用等真实的微信消息
       或二十分钟番茄钟走完，也能证明「消息风暴只反应一次」这种规则是成立的。

       接事件的那一侧（IslandWindow）只负责在正确时机喊一声 PetReact("notify")，
       播什么、播多久、能不能播，全由这里决定。 */

    sealed class PetReaction
    {
        /// <summary>事件名。也是冷却表的键，必须唯一。</summary>
        public string Event;

        /// <summary>目标语义状态；空串 = 只放脉冲，不改状态。</summary>
        public string State;

        /// <summary>附带转一圈。</summary>
        public bool Spin;

        /// <summary>附带跳一下（比挤压高得多，带蓄力与落地）。</summary>
        public bool Jump;

        /// <summary>附带挤压回弹。</summary>
        public bool Poke;

        /// <summary>越大越强势：保护期内更低优先级的事件一律让路。</summary>
        public int Priority;

        /// <summary>同一事件的最小间隔（毫秒）。</summary>
        public int CooldownMs;

        /// <summary>&gt;0：这段时间内不许更低优先级打断，到点回 idle。</summary>
        public int HoldMs;

        public PetReaction(string evt, string state, int priority, int cooldownMs, int holdMs)
        {
            Event = evt;
            State = state;
            Priority = priority;
            CooldownMs = cooldownMs;
            HoldMs = holdMs;
        }
    }

    static class PetReactions
    {
        /// <summary>落回随机待机用的状态名。桌宠自己的随机池就是它。</summary>
        public const string IdleState = "idle";

        /* 四个事件的具体配方。调观感只改这里，不要动接线的代码 ——
           接线那侧的职责是「什么时候喊」，这里负责「喊了之后播什么」。

           为什么是数组不是 switch：随机待机池要按「被绑定的动作」自动排除片段
           （见 PetClips.BuildIdlePool），那就必须能**枚举**绑定表。
           用 switch 的话，排除名单得在另一处再抄一遍，抄漏抄多都不会报错。
           七条配方线性找一遍的代价可以忽略。 */
        static readonly PetReaction[] Table = new PetReaction[]
        {
            /* 来消息：探头看一眼就收回。优先级最低那档、冷却最长 ——
               微信连发时宁可只反应一次，也不要把桌宠打成抽帧。 */
            new PetReaction("notify", "curious", 20, 2500, 1500),

            /* 番茄钟结束：收工庆祝。优先级最高（能打断别的反应），并且转一圈。 */
            Spin("timer.done", "happy", 40, 1000, 3000),

            /* 待办到点：和岛展开 todo 卡同步，语义一致。 */
            new PetReaction("todo.due", "thinking", 30, 3000, 2500),

            /* 文件进中转站：只挤一下，不改状态。拖一堆文件是连续动作，冷却短。 */
            Poke(new PetReaction("transfer.drop", "", 20, 1200, 800)),

            /* 戳桌宠：冷却只有 150ms（戳要「手一碰就有反应」），
               但不能是 0，否则双击会在两帧内叠两层挤压。 */
            Poke(new PetReaction("pet.poke", "", 25, 150, 0)),

            /* 勾完一条待办：这才是真正值得庆祝的时刻（比「到点」更值）。
               优先级略低于番茄钟结束 —— 两个撞上时让收工那个说话。
               勾完成的组合是「跳 + 转一圈」：跳跃本身就有蓄力与落地，
               和转圈叠在一起才像庆祝，单独转圈太斯文。 */
            Spin("todo.done", "happy", 35, 1500, 2500, true),

            /* 新加一条待办：轻轻想一下，别喧宾夺主。 */
            new PetReaction("todo.add", "thinking", 20, 2000, 1500),

            /* 鼠标离开岛：你不理它了，它就犯困。
               冷却 20 秒 —— 鼠标一天进出岛几百次，不这样限流会变成一直在打哈欠。 */
            new PetReaction("island.leave", "sleepy", 15, 20000, 2000),

            /* 打开右键菜单：抬头看一眼。菜单就在球边上，这个反应最自然。 */
            new PetReaction("menu.open", "curious", 25, 3000, 2000),

            /* 拖岛：被拎着走，晃两下（Rotation_C）。 */
            new PetReaction("island.drag", "dizzy", 30, 4000, 1500),

            /* 换场景：转一下头看新卡片。优先级最低、冷却偏长 ——
               场景会被自动状态机改，挂太灵敏就一直在扭头。 */
            new PetReaction("scene.change", "curious", 15, 4000, 1800)
        };

        static PetReaction Poke(PetReaction r) { r.Poke = true; return r; }
        static PetReaction Spin(string evt, string state, int priority, int cooldownMs, int holdMs)
        {
            return Spin(evt, state, priority, cooldownMs, holdMs, false);
        }
        static PetReaction Spin(string evt, string state, int priority, int cooldownMs, int holdMs,
                                bool jumpToo)
        {
            PetReaction r = new PetReaction(evt, state, priority, cooldownMs, holdMs);
            r.Spin = true;
            r.Jump = jumpToo;
            return r;
        }

        /// <summary>按事件名取配方；不认识就返回 null（调用方会记一行 skip，不静默当成功）。</summary>
        public static PetReaction Get(string evt)
        {
            if (evt == null) return null;
            for (int i = 0; i < Table.Length; i++)
            {
                if (Table[i] != null && Table[i].Event == evt) return Table[i];
            }
            return null;
        }

        /// <summary>全部已接线的事件名。菜单/探针要列清单时用，别在别处再抄一份。</summary>
        public static string[] Events()
        {
            List<string> names = new List<string>(Table.Length);
            for (int i = 0; i < Table.Length; i++)
            {
                if (Table[i] != null) names.Add(Table[i].Event);
            }
            return names.ToArray();
        }

        /// <summary>
        /// 所有被绑定用到的语义状态（去重，不含 idle）。
        ///
        /// 这是「被绑定的动作要移出随机动画池」那条规则的**唯一来源**：
        /// PetClips.BuildIdlePool 拿它反查每个状态用到哪些片段，然后把这些片段
        /// 从池子里排掉。绑了新事件不用再去找池子改名单 —— 那边是推导出来的。
        /// </summary>
        public static string[] BoundStates()
        {
            List<string> states = new List<string>();
            for (int i = 0; i < Table.Length; i++)
            {
                PetReaction r = Table[i];
                if (r == null || r.State == null || r.State.Length == 0) continue;
                if (r.State == IdleState) continue;
                if (!states.Contains(r.State)) states.Add(r.State);
            }
            return states.ToArray();
        }
    }

    /* ============================================================ 命中判定

       桌宠是个圆球，不是方块：透明四角必须继续穿透，否则鼠标会「停在一块看不见的墙上」。
       所以命中用圆形判定，并且放在这个纯逻辑文件里 —— 这样「点球心能中、点圆外不中、
       直径没算出来时不中」这几条能被离线断言，不用移真鼠标去试。 */
    static class PetHit
    {
        /// <summary>屏幕 DIP 坐标 (x,y) 是否落在以 (cx,cy) 为心、直径 dia 的圆内。</summary>
        public static bool Circle(double x, double y, double cx, double cy, double dia)
        {
            if (dia <= 0) return false;
            double r = dia / 2;
            double dx = x - cx;
            double dy = y - cy;
            return dx * dx + dy * dy <= r * r;
        }
    }

    /* ============================================================ 裁决

       两条规则，都按时间：
         · 同事件冷却 —— 冷却期内重复出现直接丢掉；
         · 优先级抢占 —— 高优先级还在保护期内，后来的低优先级不许打断。

       时基必须是单调毫秒（IslandWindow._clock.ElapsedMilliseconds）。
       不要用 Environment.TickCount：它是 Int32，24.9 天回绕成负数，
       `now < last + cooldown` 那套写法会当场失效（滚轮那次就是这么坏的）。
       就算传进来的是 long，也统一用 `now - last < cooldown` 这种差值比较，
       它对时间倒退和回绕都不会算出错误结论。 */
    sealed class PetReactionPolicy
    {
        readonly Dictionary<string, long> _lastMs = new Dictionary<string, long>();
        long _busyUntilMs;
        int _busyPriority;

        public void Reset()
        {
            _lastMs.Clear();
            _busyUntilMs = 0;
            _busyPriority = 0;
        }

        /// <summary>这次事件该不该播。返回 false 时**不要**产生任何副作用。</summary>
        public bool Accept(PetReaction r, long nowMs)
        {
            if (r == null) return false;

            long last;
            if (_lastMs.TryGetValue(r.Event, out last))
            {
                if (nowMs - last < r.CooldownMs) return false;
            }

            /* 还在高优先级动作的保护期里：更低优先级一律让路。
               同优先级允许通过 —— 否则「番茄钟结束」连着两次只会有一次有反应。 */
            if (nowMs < _busyUntilMs && r.Priority < _busyPriority) return false;

            _lastMs[r.Event] = nowMs;
            if (r.HoldMs > 0)
            {
                _busyUntilMs = nowMs + r.HoldMs;
                _busyPriority = r.Priority;
            }
            return true;
        }

        /// <summary>当前是否处在某个反应的保护期内（诊断用）。</summary>
        public bool Busy(long nowMs) { return nowMs < _busyUntilMs; }
    }
}