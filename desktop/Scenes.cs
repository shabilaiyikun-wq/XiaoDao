using System;

namespace WinIsland
{
    /* 场景尺寸表。

       从 Ui.cs 里搬出来的唯一原因是**可测试性**：层级切换的形变目标就是这张表
       里的数字，验证程序要拿同一份数据去跑弹簧，而不是在测试里另抄一遍
       52x30 / 112x38 / 452x206 —— 抄一遍就等于给「改一处忘一处」留了门。
       本文件刻意不引用任何 WPF 类型，所以可以单独编进测试程序。 */

    /// <summary>一个活动的两档尺寸。</summary>
    class SceneDef
    {
        public string Key;
        public string Name;
        public double CompW, CompH, ExpW, ExpH;
    }

    static class Scenes
    {
        public const double IdleW = 52, IdleH = 30;
        /* 第一层带天气时的宽度：左右各 6 内边距，中间是球（16.8）和
           「图标 + 最长温度 -18°」（约 45）。
           没有天气数据时仍按 52 —— 没东西可显示就不该占地方，
           这也是「存了天气才变宽」而不是一律变宽的原因。 */
        public const double IdleWeatherW = 82;
        /* 第二层（有音乐、未悬停）：只放封面 + 律动，不放文字 */
        public const double PlayW = 112, PlayH = 38;

        public static readonly SceneDef[] All = new SceneDef[]
        {
            Make("media", "媒体",   248, 38, 452, 206),
            Make("timer", "计时器", 188, 38, 436, 216),
            /* 天气紧跟计时器：这两个是「抬眼就想知道」的常驻信息，
                放在一起，滚轮切到它们不用划过整条列表。尺寸比照剪贴板/中转站 ——
                卡片是「大温度 + 三个指标 + 三天预报」，268 高刚好一屏读完；
                再多就得压行距，读起来反而慢。 */
            Make("weather", "天气", 200, 38, 468, 268),
            Make("clip",  "剪贴板", 200, 38, 468, 268),
            Make("todo",  "待办",   200, 38, 468, 292),
            /* 中转站：尺寸比照剪贴板页（同为「表头 + 列表」结构）。
               注意 Scenes.Get 对未知 key 是静默回退 All[0] 的 ——
               这里漏加一条，页面看着就像没生效。 */
            Make("transfer", "中转站", 200, 38, 468, 268)
        };

        static SceneDef Make(string k, string n, double cw, double ch, double ew, double eh)
        {
            SceneDef d = new SceneDef();
            d.Key = k; d.Name = n;
            d.CompW = cw; d.CompH = ch; d.ExpW = ew; d.ExpH = eh;
            return d;
        }

        public static SceneDef Get(string key)
        {
            for (int i = 0; i < All.Length; i++) if (All[i].Key == key) return All[i];
            return All[0];
        }

        public static int IndexOf(string key)
        {
            for (int i = 0; i < All.Length; i++) if (All[i].Key == key) return i;
            return 0;
        }
    }
}