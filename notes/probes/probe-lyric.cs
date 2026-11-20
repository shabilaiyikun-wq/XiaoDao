using System;
using WinIsland;

class ProbeLyric
{
    static void Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        string[] cases = new string[]
        {
            "最佳损友",
            "K歌之王 (粤语)",
            "倒带 (Live)",
            "屋顶 - 周杰伦",
            "01. 情歌",
            "【高音质】富士山下",
            "晴天花海（官方版）",
            "告白气球 - 周杰伦 (Live)",
            "全世界笑点下降1000倍而我不变【第二集】",
            "演员【无损】"
        };

        Console.WriteLine("=== 清洗对照 (原始 -> CleanTitle -> HardClean) ===");
        for (int i = 0; i < cases.Length; i++)
        {
            Console.WriteLine("  '" + cases[i] + "'");
            Console.WriteLine("      clean: '" + Lyric.CleanTitle(cases[i]) + "'");
            Console.WriteLine("      hard : '" + Lyric.HardClean(cases[i]) + "'");
        }

        Console.WriteLine("");
        Console.WriteLine("=== 真实取词 ===");
        Show("最佳损友", "陈奕迅");
        Show("K歌之王 (粤语)", "陈奕迅");
        Show("这是一个完全不存在的歌名zzzqqq", "某某");
    }

    static void Show(string t, string a)
    {
        LyricDoc d = null;
        try { d = Lyric.Fetch(t, a); }
        catch (Exception ex) { Console.WriteLine("  [" + t + "] 抛异常 " + ex.GetType().Name); return; }
        if (d == null) { Console.WriteLine("  [" + t + "] -> null （界面显示「无同步歌词」）"); return; }
        Console.WriteLine("  [" + t + "] -> " + d.Count + " 行, 来源 " + d.Source);
    }
}