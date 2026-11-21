/* ============================================================================
   液态连接 · 表面张力滞回（原型）

   ---------------------------------------------------------------------------
   来源与许可（MIT）
   ---------------------------------------------------------------------------
   本文件的滞回状态机、三个阈值常数和 neckWaist 曲线，是从下面这个 MIT 项目
   移植并改写为 C# 的实质移植：

       FluidKit — https://github.com/runvendo/fluidkit
       src/liquid/tension.ts       -> TensionField / neckWaist / *_STRETCH
       Copyright (c) 2026 Yousef Hindy
       MIT License

   完整许可正文见 desktop/LICENSES/fluidkit-MIT.txt。
   ---------------------------------------------------------------------------

   参考实现的核心思想（照搬，且认为是对的）：

     拉伸比 stretch = 圆心距 / 半径和，1.0 = 刚好相切。
       · 没连上的一对，要 stretch < CONNECT_STRETCH(1.02) 才连 —— 也就是
         「真的碰上了」才开始有颈，而不是一进视野就连；
       · 连上的一对，要 stretch > SNAP_STRETCH(1.30) 才断 —— 中间这段宽度
         就是滞回带，专门用来消掉临界距离上的抖动；
       · 颈的粗细随拉伸变细，但**有下限**（MIN_WAIST_FACTOR 0.6），
         断开那一刻颈还很粗，是「掐断」不是「拉成一条丝再断」。

   为什么必须有滞回（不是可有可无的打磨）
   ---------------------------------------------------------------------------
   本项目真实的那一对（岛体右端弧 ↔ 消息小球）静止时 stretch = 1.3125，
   而断开阈值是 1.30 —— 只超出 0.4 DIP。也就是说**它天生就坐在临界距离上**：
   没有滞回的话，布局抖动半个 DIP、DPI 取整差一点、球自己的缩放动画走一帧，
   都会让它反复跨越阈值，画面上就是那条颈一帧亮一帧灭。滞回把这 0.4 DIP
   变成一个「已经断了就一直断，必须真的碰回去才重新连」的确定状态。

   为什么在参考实现之上还加了三处
   ---------------------------------------------------------------------------
   1) **绝对最小颈宽 MinNeckWidth（DIP）**。
      参考实现的粗细是相对量：radius 1.5 DIP 的两个球，按 0.55 的 waist 算出来的
      颈部只有零点几个 DIP —— 渲染出来就是一条会断断续续的抗锯齿细丝，也就是
      本任务明确禁止的「细丝」。所以这里把「量出来的颈宽」当硬门槛：
      低于门槛就当作没连上，宁可不画，也不画一条丝。

   2) **状态只在几何真的画出来之后才记账**。
      参考实现是先 `connected.add(key)` 再把 path 字符串拼上去，而 bridgePath
      在「包含 / 退化 / 够不着」时会返回空串 —— 于是会出现「状态说连着、画面
      上什么都没有」。反过来那一刻如果紧接着又满足条件，就会变成一帧有一帧无。
      这里改成：几何没产出 → 状态一并清掉。状态和画面永远同一帧同一个结论。

   3) **配对键与顺序无关**。
      参考实现的 key 是 `${a.id}|${b.id}`，依赖遍历顺序。只要调用方换了 body 的
      顺序（增删一个球、换个排序），同一对就换了 key，滞回状态整体丢失，
      表现就是「什么都没动，颈突然没了」。这里用排序后的 id 做 key。

   断开的三条硬要求（对应任务里的「不能留下细丝、裂口或一帧闪烁」）
   ---------------------------------------------------------------------------
   · 不留细丝：MinNeckWidth 门槛 + 参考实现自带的 MIN_WAIST_FACTOR 双重保证；
   · 不留裂口：颈与两个体是**同一个 PathGeometry、同一条 nonzero 填充**，
     不是三段拼图，所以接缝处不存在「两个形状各画一半」的缝；
   · 不留一帧闪烁：断开是这一帧**原子**发生的 —— 状态先清、几何这一帧就不产出，
     不做淡出、不留残影、不跨帧补画。探针用逐帧像素断言盯这一点。
   ============================================================================ */

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace WinIsland
{
    /// <summary>张力常数与颈宽曲线。数默认值全部来自参考实现，改了要说明为什么。</summary>
    static class LiquidTension
    {
        /// <summary>没连上的一对开始连的拉伸比上限（参考实现 CONNECT_STRETCH）。</summary>
        public const double ConnectStretch = 1.02;
        /// <summary>连上的一对断开的拉伸比上限（参考实现 SNAP_STRETCH）。</summary>
        public const double SnapStretch = 1.30;
        /// <summary>颈宽系数的下限占比（参考实现 MIN_WAIST_FACTOR）。</summary>
        public const double MinWaistFactor = 0.60;
        /// <summary>满颈时的 waist 基数（参考实现 BASE_WAIST）。</summary>
        public const double BaseWaist = 0.55;
        /// <summary>从 Stretch=0.95 到 SnapStretch 之间把 waist 收到底的跨度（参考实现 0.35）。</summary>
        public const double WaistFalloff = 0.35;

        /// <summary>
        /// 绝对最小颈宽（DIP）。低于这个宽度不画颈 —— 这是参考实现没有的项，
        /// 是本任务「连接颈必须保持可控最小宽度」的落点。
        /// 取 2.0 是因为球体描边 1.2px + 抗锯齿在 150% DPI 下，窄于 2 DIP 的颈
        /// 会出现肉眼可见的断续（探针对 1.5/2.5 DIP 的小球专门验过）。
        /// </summary>
        public const double DefaultMinNeckWidth = 2.0;

        /// <summary>
        /// 给定拉伸比下的颈粗系数（0..1）。越拉越细，但不会细过 MIN_WAIST_FACTOR 倍。
        /// 与参考实现 neckWaist 完全一致。
        /// </summary>
        public static double NeckWaist(double stretch)
        {
            double t = (SnapStretch - stretch) / WaistFalloff;
            if (t > 1) t = 1;
            if (t < MinWaistFactor) t = MinWaistFactor;
            return BaseWaist * t;
        }

        /// <summary>
        /// 滞回状态机。一个场景一个实例；同一对 body 每帧给同一个 id。
        /// 声明成 public 是因为它是唯一带状态的对象，探针要能自己造一个来驱动
        /// 逐帧场景（内部 static 类里的嵌套类默认 private，那样探针够不着）。
        /// </summary>
        public sealed class Field
        {
            /* 只存「现在连着」这一件事。值用 bool 而不是 HashSet 是为了让
               TryGetValue 一次拿到「有没有」和「是什么」，查询路径不分叉。 */
            readonly Dictionary<string, bool> _connected = new Dictionary<string, bool>();

            /// <summary>最小颈宽（DIP）。0 表示不启用门槛（退回参考实现的纯相对语义）。</summary>
            public double MinNeckWidth = DefaultMinNeckWidth;

            /// <summary>把两个体本身也画进同一个几何（探针验「并集有没有洞」时用）。</summary>
            public bool IncludeBodies;

            /// <summary>最近一次产出的颈宽（诊断/探针用，不参与判定）。断开后回到 NaN。</summary>
            public double LastNeckWidth = double.NaN;

            /// <summary>上一次因为「窄于最小颈宽」而被挡掉的颈宽（诊断用）。</summary>
            public double LastSuppressedWidth = double.NaN;

            /// <summary>最近一次算出的 waist（诊断用）。</summary>
            public double LastWaist = double.NaN;

            /// <summary>当前连着的对数。</summary>
            public int ConnectedPairs { get { return _connected.Count; } }

            /* -------------------------------------------------- 配对键 */

            /// <summary>
            /// 与遍历顺序无关的配对键。用序数比较（不是区域性比较），
            /// 否则不同 locale 下 "i" 和 "I" 的相对大小不一样，key 会飘。
            /// </summary>
            public static string Key(string idA, string idB)
            {
                if (idA == null) idA = "";
                if (idB == null) idB = "";
                return string.CompareOrdinal(idA, idB) <= 0
                    ? idA + "|" + idB
                    : idB + "|" + idA;
            }

            /// <summary>这一对现在是否连着（只读，不推进状态）。</summary>
            public bool IsConnected(LiquidBody a, LiquidBody b)
            {
                if (a == null || b == null) return false;
                bool v;
                return _connected.TryGetValue(Key(a.Id, b.Id), out v) && v;
            }

            /// <summary>有没有任意一条颈挂着这个 id。</summary>
            public bool ConnectedTo(string id)
            {
                if (id == null) return false;
                foreach (KeyValuePair<string, bool> kv in _connected)
                {
                    if (!kv.Value) continue;
                    int bar = kv.Key.IndexOf('|');
                    if (bar < 0) continue;
                    if (kv.Key.Substring(0, bar) == id) return true;
                    if (kv.Key.Substring(bar + 1) == id) return true;
                }
                return false;
            }

            /// <summary>清状态（全部，或按 key 前缀/包含关系清）。</summary>
            public void Clear()
            {
                _connected.Clear();
            }

            public void ClearMatching(string id)
            {
                if (id == null) { _connected.Clear(); return; }
                List<string> drop = null;
                foreach (KeyValuePair<string, bool> kv in _connected)
                {
                    int bar = kv.Key.IndexOf('|');
                    if (bar < 0) continue;
                    if (kv.Key.Substring(0, bar) == id || kv.Key.Substring(bar + 1) == id)
                    {
                        if (drop == null) drop = new List<string>();
                        drop.Add(kv.Key);
                    }
                }
                if (drop == null) return;
                for (int i = 0; i < drop.Count; i++) _connected.Remove(drop[i]);
            }

            /// <summary>
            /// 断开一对：清状态，同时把诊断量归零。
            /// 归零很重要 —— 否则报告里会读出「上一帧的颈宽」，看起来像还挂着一根颈。
            /// </summary>
            void Drop(string key)
            {
                _connected.Remove(key);
                LastNeckWidth = double.NaN;
                LastWaist = double.NaN;
            }

            /* -------------------------------------------------- 主入口 */

            /// <summary>
            /// 推出这一帧所有连接颈的几何。没有颈时返回 Geometry.Empty，不是 null
            /// —— 调用方可以直接挂到 Path.Data 上，不用每帧判空。
            /// IncludeBodies 为 true 时把两个体也一起 Appned 进来（nonzero 并集）。
            /// </summary>
            public Geometry Bridges(IList<LiquidBody> bodies)
            {
                return Build(bodies, IncludeBodies);
            }

            /// <summary>体 + 颈合成一个几何（nonzero 并集），给「有没有洞」的断言用。</summary>
            public Geometry Coverage(IList<LiquidBody> bodies)
            {
                return Build(bodies, true);
            }

            Geometry Build(IList<LiquidBody> bodies, bool withBodies)
            {
                if (bodies == null || bodies.Count < 1) return Geometry.Empty;

                PathGeometry g = null;
                bool anyBridge = false;
                bool anyBody = false;

                for (int i = 0; i < bodies.Count; i++)
                {
                    LiquidBody a = bodies[i];
                    if (a == null) continue;

                    if (withBodies && a.R > LiquidGeometry.DegenerateRadius)
                    {
                        if (g == null) { g = NewGeo(); }
                        LiquidGeometry.AppendDisc(g, a);
                        anyBody = true;
                    }

                    for (int j = i + 1; j < bodies.Count; j++)
                    {
                        LiquidBody b = bodies[j];
                        if (b == null) continue;

                        string key = Key(a.Id, b.Id);

                        /* 退化体不参与，历史状态也要清 —— 否则球缩到 0 再长回来，
                           会带着一个「上一世连着」的旧状态复活。 */
                        if (a.R <= LiquidGeometry.DegenerateRadius
                            || b.R <= LiquidGeometry.DegenerateRadius)
                        {
                            Drop(key);
                            continue;
                        }

                        double stretch = LiquidGeometry.Stretch(a, b);

                        bool was;
                        bool wasConnected = _connected.TryGetValue(key, out was) && was;

                        /* 滞回就这两行：连着的用宽阈值，没连的用窄阈值。 */
                        bool isConnected = wasConnected
                            ? stretch < SnapStretch
                            : stretch < ConnectStretch;

                        if (!isConnected)
                        {
                            Drop(key);
                            continue;
                        }

                        double waist = NeckWaist(stretch);
                        BridgeShape shape;
                        if (!LiquidGeometry.TryBridge(a, b, waist,
                                LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach,
                                out shape))
                        {
                            /* 几何画不出来（包含 / 够不着）→ 状态一起清。
                               不能留一个「连着但没画」的悬空状态（参考实现的 bug）。 */
                            Drop(key);
                            continue;
                        }

                        /* 硬门槛：颈比 MinNeckWidth 细就不画。
                           注意必须在**写下状态之前**判，且失败时不要把状态翻过来 ——
                           一旦写成「先置 true 再撤销」，这一对就会每帧在
                           「连上 → 太细撤掉 → 连上」之间来回，正好制造一帧闪烁。 */
                        if (MinNeckWidth > 0 && shape.NeckWidth < MinNeckWidth)
                        {
                            Drop(key);
                            LastSuppressedWidth = shape.NeckWidth;
                            continue;
                        }

                        _connected[key] = true;
                        LastNeckWidth = shape.NeckWidth;
                        LastWaist = waist;

                        if (g == null) { g = NewGeo(); }
                        LiquidGeometry.AppendBridge(g, shape);
                        anyBridge = true;
                    }
                }

                if (g == null) return Geometry.Empty;
                if (!anyBridge && !anyBody)
                {
                    /* 只剩体、或者什么都没剩下：清干净状态，别把空几何冻结成单例。 */
                    return Geometry.Empty;
                }
                g.Freeze();
                return g;
            }

            static PathGeometry NewGeo()
            {
                PathGeometry g = new PathGeometry();
                /* nonzero：体和颈互相叠加，不是互相挖洞。和 SVG 那边 clip-path
                   的填充规则是同一个语义，也是参考实现依赖的那一条。 */
                g.FillRule = FillRule.Nonzero;
                return g;
            }

            /* ------------------------------------------ 有效断开距离（探针用） */

            /// <summary>
            /// 在给定半径下「实际会在哪里断」：能画出的、颈宽仍不小于 MinNeckWidth 的
            /// **最大**拉伸比。超过它就更远、颈更细，一定断。
            ///
            /// 返回 NaN 表示这对半径在任何距离上都画不出一条够宽的颈（典型是小球），
            /// 也就是这对**永远不连** —— 这是有意的：宁可不连，也不画细丝。
            ///
            /// 为什么从 SnapStretch 往下扫而不是二分：颈宽对 stretch 单调递减，
            /// 但「深度重叠到一定程度后并集本来就是一个体、几何直接不给颈」，
            /// 于是可行域是 [下限, 上限] 而不是半轴。从上限往下扫到的第一处
            /// 可行点就是答案，不会踩到包含那一段。
            /// </summary>
            public double EffectiveSnapStretch(double r1, double r2)
            {
                const int Steps = 600;
                double need = MinNeckWidth;
                double best = double.NaN;

                for (int k = 0; k <= Steps; k++)
                {
                    double st = SnapStretch - (SnapStretch - 0.40) * k / Steps;
                    double w = WidthAt(r1, r2, st);
                    if (double.IsNaN(w)) continue;
                    if (need > 0 && w < need) continue;
                    best = st;
                    break;
                }
                return best;
            }

            static double WidthAt(double r1, double r2, double stretch)
            {
                LiquidBody a = new LiquidBody("a", 0, 0, r1);
                LiquidBody b = new LiquidBody("b", (r1 + r2) * stretch, 0, r2);
                BridgeShape s;
                if (!LiquidGeometry.TryBridge(a, b, LiquidTension.NeckWaist(stretch),
                        LiquidGeometry.DefaultHandle, LiquidGeometry.DefaultReach, out s))
                    return double.NaN;
                return s.NeckWidth;
            }
        }
    }
}