/* ============================================================================
   液态连接原型 · 独立探针入口（只被 build-liquid.cmd 编译）

   为什么要单独一个文件：正式构建（build.cmd）编的是 WinIsland.exe，那里已经有
   Program.Main；这个文件再声明一个 Main 会撞车。所以它只进 LiquidProbe.exe
   那个独立产物 —— 让原型能在**不碰主程序**的前提下单独编译、单独验证。

   原型默认关闭，正式 exe 只在显式传 --liquid-probe 时才会走这条路。
   ============================================================================ */

using System;
using System.IO;
using System.Text;

namespace WinIsland
{
    static class LiquidProbeMain
    {
        [STAThread]
        static int Main(string[] args)
        {
            string outDir = null;
            string logPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--out" && i + 1 < args.Length) outDir = args[i + 1];
                else if (args[i].StartsWith("--out=")) outDir = args[i].Substring(6);
                else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[i + 1];
            }

            if (outDir == null || outDir.Length == 0)
                outDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "liquid-probe");

            StringBuilder logSb = new StringBuilder();
            Action<string> log = delegate(string s) { logSb.AppendLine(s); };

            int rc = LiquidProbe.Run(outDir, log);

            /* 没有窗口，唯一的取证途径就是控制台 + 文件 */
            string text = logSb.ToString();
            Console.Out.Write(text);
            Console.Out.Flush();

            if (logPath != null && logPath.Length > 0)
            {
                try { File.WriteAllText(logPath, text, new UTF8Encoding(false)); }
                catch { }
            }
            return rc;
        }
    }
}