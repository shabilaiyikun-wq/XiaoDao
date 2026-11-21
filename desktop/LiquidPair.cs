/* ============================================================================
   液态连接原型 · 真实对象对的常数镜像

   ---------------------------------------------------------------------------
   为什么要有这个文件
   ---------------------------------------------------------------------------
   原型要能**单独编译、单独验证**（build-liquid.cmd 只编几何 + 张力 + 探针，
   不编整个 WinIsland.exe）。而探针要做的事是「拿真实常数去算真实的那一对」，
   这些常数散在 Ui.cs（Scenes）和 IslandWindow.cs（NotifyGap / NotifyBall）里，
   后面这两个文件拖着一大堆 WPF 依赖，编不进独立的探针程序。

   所以这里放一份镜像。谁是谁的唯一来源写清楚，避免出现「两份数不一样、
   验证的和跑起来的不是同一组」这种最没意义的错误：

     IdleW/IdleH/PlayW/PlayH  ← 唯一来源 Ui.cs（Scenes）
                                 正式构建时由 Program.cs 在跑探针前覆盖一次，
                                 所以 Ui.cs 改了而这里忘了改会被探针抓到。
     NotifyGap / NotifyBall   ← 唯一来源 IslandWindow.cs（私有常量）
                                 这两个没法在运行时读到（private const 被内联进
                                 了 IslandWindow 的 IL），只能靠这里对齐；
                                 改动时必须同时改两处，decision note 里已列为已知缺口。

   数值来源全部是编译期常量，没有运行时测量 —— 探针量的和正式代码用的是同一组数。
   ============================================================================ */

namespace WinIsland
{
    static class LiquidPair
    {
        /* 岛体在 idle / 第二层（播放中）的尺寸，单位 DIP。来源：Ui.cs Scenes */
        public static double IdleW = 52;
        public static double IdleH = 30;
        public static double PlayW = 112;
        public static double PlayH = 38;

        /* 消息小球：挂在与岛体之间的缝隙上。来源：IslandWindow.cs 私有常量 */
        public static double NotifyGap = 10;
        public static double NotifyBall = 34;

        /// <summary>圆角半径的算法，与 IslandWindow.Apply 里 `r = Math.Min(30, h / 2)` 同源。</summary>
        public static double IslandRadius(double h)
        {
            return System.Math.Min(30, h / 2);
        }

        /// <summary>
        /// 岛体右端是否**正好**是个半圆。h &lt;= 60 时 r = h/2，右端整段都是圆弧，
        /// 圆心 (w - r, h/2) —— 这时「圆 ↔ 圆」的参考算法是精确适用的，
        /// 不是近似。h &gt; 60 时右侧有一段直边，参考算法就不适用了（见 S2 结论）。
        /// </summary>
        public static bool RightEndIsArc(double h)
        {
            return IslandRadius(h) >= h / 2 - 1e-9;
        }

        /// <summary>
        /// 静止时这对的拉伸比。推导见 S2：
        /// 因为两者同心高、小球正好落在缝隙外侧，所以 圆心距 = r岛 + r球 + 缝隙。
        /// </summary>
        public static double RestStretch(double islandH)
        {
            if (!RightEndIsArc(islandH)) return double.NaN;
            double r1 = IslandRadius(islandH);
            double r2 = NotifyBall / 2;
            return (r1 + r2 + NotifyGap) / (r1 + r2);
        }
    }
}