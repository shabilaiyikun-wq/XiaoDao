using System;
using System.Windows.Media;

namespace WinIsland
{
    /// <summary>一套外观的完整取色。不是同壳换色：投影、边缘、颗粒、镜面强度都各有一套值。</summary>
    class Look
    {
        public string Key;
        public Color Ink1;
        public Color Ink2;          // 已带 alpha
        public Color Ink3;
        public Color Track;
        public Color BtnBg;
        public Color BtnHover;
        public Color TileBg;
        public Color TintTop;
        public Color TintMid;
        public Color TintBottom;
        public bool Refract;         // 真实采样桌面
        public Color RefractWash;    // 压在采样之上的冷色/暗色

        /* 玻璃边缘折射（只有 glass 那套给非零值，其余外观全 0 = 一个像素都不变）。
           三项都按 DIP 记，换分辨率不用改数值：
             Bend      折射带宽度（从岛边往里多少 DIP 还在弯折背景）
             BendKick  位移强度（岛边处的最大位移 = 一半）
             BendSplit RGB 色散（R 与 B 通道的最大位移差） */
        public double Bend;
        public double BendKick;
        public double BendSplit;

        public double Grain;         // 颗粒不透明度
        public double Edge;          // 边缘环强度
        public double Spec;          // 指针镜面强度
        public double ShadowAlpha;   // 落影浓度
        public Color ShadowColor;

        public static Look Obsidian()
        {
            Look l = new Look();
            l.Key = "obsidian";
            l.Ink1 = C("#F4F5F9");
            l.Ink2 = A("#F4F5F9", 0.70);
            l.Ink3 = A("#F4F5F9", 0.48);
            l.Track = A("#FFFFFF", 0.14);
            l.BtnBg = A("#FFFFFF", 0.10);
            l.BtnHover = A("#FFFFFF", 0.17);
            l.TileBg = A("#FFFFFF", 0.09);
            l.TintTop = C("#1C1E26");
            l.TintMid = C("#16181F");
            l.TintBottom = C("#101116");
            l.Refract = false;
            l.RefractWash = Colors.Transparent;
            l.Grain = 0.045;
            l.Edge = 0.85;
            l.Spec = 1.0;
            l.ShadowAlpha = 0.62;
            l.ShadowColor = Colors.Black;
            return l;
        }

        public static Look Graphite()
        {
            Look l = Obsidian();
            l.Key = "graphite";
            l.TintTop = C("#2B2F3B");
            l.TintMid = C("#22252F");
            l.TintBottom = C("#171922");
            l.Grain = 0.060;
            l.Edge = 1.0;
            l.ShadowAlpha = 0.55;
            return l;
        }

        public static Look Frost()
        {
            Look l = new Look();
            l.Key = "frost";
            l.Ink1 = C("#14161D");
            l.Ink2 = A("#14161D", 0.74);
            l.Ink3 = A("#14161D", 0.52);
            l.Track = A("#14161D", 0.16);
            l.BtnBg = A("#14161D", 0.06);
            l.BtnHover = A("#14161D", 0.12);
            l.TileBg = A("#14161D", 0.07);
            l.TintTop = C("#FFFFFF");
            l.TintMid = C("#F7F8FB");
            l.TintBottom = C("#EEF0F5");
            l.Refract = false;
            l.RefractWash = Colors.Transparent;
            l.Grain = 0.030;
            l.Edge = 0.60;
            l.Spec = 0.55;
            l.ShadowAlpha = 0.20;
            l.ShadowColor = C("#14161D");
            return l;
        }

        public static Look Glass()
        {
            Look l = Obsidian();
            l.Key = "glass";
            l.Ink1 = C("#FBFCFF");
            l.Ink2 = A("#FBFcFF", 0.82);
            l.Ink3 = A("#FBFcFF", 0.62);
            l.Track = A("#FFFFFF", 0.20);
            l.BtnBg = A("#FFFFFF", 0.14);
            l.BtnHover = A("#FFFFFF", 0.22);
            l.TileBg = A("#FFFFFF", 0.13);
            l.TintTop = A("#FFFFFF", 0.16);
            l.TintMid = A("#FFFFFF", 0.04);
            l.TintBottom = A("#000000", 0.14);
            l.Refract = true;
            l.RefractWash = A("#0E1016", 0.44);
            /* 折射参数按岛体实际尺寸定：展开态 452x206，半高 103 ——
               26 DIP 的带子约占高度的 1/8，视觉上正好是「一圈厚玻璃」，
               再宽就从边缘效果变成整个岛都在变形了。
               21 DIP 的位移强度 = 岛边最多把背景拉进来 10.5 DIP，
               比基底那条 16 DIP 的模糊半径略小，弯折看得出来但不会错位。
               2.6 DIP 的色散在这个尺寸上是「仔细看才有」的色边，
               照着参考仓库默认 cab=2（CSS px）量级给。 */
            l.Bend = 26;
            l.BendKick = 21;
            l.BendSplit = 2.6;
            l.Grain = 0.050;
            l.Edge = 1.0;
            l.Spec = 1.0;
            l.ShadowAlpha = 0.55;
            return l;
        }

        public static Look ByKey(string key)
        {
            if (key == "graphite") return Graphite();
            if (key == "frost") return Frost();
            if (key == "glass") return Glass();
            return Obsidian();
        }

        public static Color C(string hex)
        {
            return (Color)ColorConverter.ConvertFromString(hex);
        }

        public static Color A(string hex, double alpha)
        {
            Color c = C(hex);
            c.A = (byte)Math.Round(Math.Max(0, Math.Min(1, alpha)) * 255);
            return c;
        }
    }

    /// <summary>取材：封面三色 + 强调色三级（装饰 / 压白字实心 / 当文字）。</summary>
    class Art
    {
        public string Key;
        public Color C1, C2, C3;
        public Color Acc;      // 装饰面：进度、波形、光环
        public Color AccOn;    // 实心压白字面
        public Color AccInk;   // 当文字/图形用的可读版

        public static Art Make(string key, string c1, string c2, string c3, string acc, string on, string ink)
        {
            Art a = new Art();
            a.Key = key;
            a.C1 = Look.C(c1);
            a.C2 = Look.C(c2);
            a.C3 = Look.C(c3);
            a.Acc = Look.C(acc);
            a.AccOn = Look.C(on);
            a.AccInk = Look.C(ink);
            return a;
        }

        public static Art Aurora()
        {
            return Make("aurora", "#9A8CF5", "#5A4AC8", "#3EC8E8", "#B4A8FF", "#5342C2", "#B4A8FF");
        }
        public static Art Ember()
        {
            return Make("ember", "#FFA87A", "#D8492C", "#FFD166", "#FFB08A", "#A9340D", "#FFB08A");
        }
        public static Art Mint()
        {
            return Make("mint", "#6EE7B7", "#0F9B96", "#BEF264", "#5EEAD4", "#0A6157", "#5EEAD4");
        }
        public static Art Rose()
        {
            return Make("rose", "#FB9FC8", "#A03389", "#FFC86B", "#F9B0D6", "#8F1D5C", "#F9B0D6");
        }
        public static Art Steel()
        {
            return Make("steel", "#9DC0F7", "#3F6FD8", "#A5E9FF", "#9DC9FD", "#1B49B8", "#9DC9FD");
        }

        public static Art ByKey(string key)
        {
            if (key == "ember") return Ember();
            if (key == "mint") return Mint();
            if (key == "rose") return Rose();
            if (key == "steel") return Steel();
            return Aurora();
        }
    }
}