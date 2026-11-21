using System;
using System.Windows;

namespace WinIsland
{
    class IslandApp : Application
    {
    }

    static class Program
    {
        static string Arg(string[] a, string name, string fallback)
        {
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] == name && i + 1 < a.Length) return a[i + 1];
                if (a[i].StartsWith(name + "=")) return a[i].Substring(name.Length + 1);
            }
            return fallback;
        }

        static bool Flag(string[] a, string name)
        {
            return Array.IndexOf(a, name) >= 0;
        }

        [STAThread]
        static int Main(string[] args)
        {
            Native.MakeDpiAware();

            /* 外观 / 场景 / 素材的默认值留空：构造器要靠它区分「用户指定了」和
                「没指定，应该从 ui.txt 恢复上次的选择」。 */
            string look = Arg(args, "--look", "");
            string scene = Arg(args, "--scene", "");
            string art = Arg(args, "--art", "");
            string state = Arg(args, "--state", "auto");
            string log = Arg(args, "--log", "");
            bool selftest = Flag(args, "--selftest");
            bool wdaProbe = Flag(args, "--wda-probe");
            bool noWda = Flag(args, "--no-wda");
            /* 强制「拍不到」。默认允许截图/录屏捕捉（见 Prefs.Capture），
               验证实时桌面采样时必须先打开排除，否则采样会拍到自己。 */
            bool excludeCapture = Flag(args, "--exclude-capture");
            bool liveProbe = Flag(args, "--live-probe");
            bool heartbeat = Flag(args, "--heartbeat");
            /* 调试用：启动后直接把「写一条」的输入窗弹出来，
               这样验收脚本不用去猜按钮坐标也能验证输入链路 */
            bool todoOpen = Flag(args, "--todo-open");
            /* 消息提醒：微信/QQ 来消息时亮小球。这一版只把信号打进日志，
               小球 UI 还没接（见 Notify.cs 头部的可行性结论）。 */
            bool notify = Flag(args, "--notify");
            /* 不等真消息，启动就亮一颗球，用来肉眼确认位置/点击 */
            bool notifyTest = Flag(args, "--notify-test");
            /* 逐帧记录动画参数，给自动化验收用 */
            bool notifyTrace = Flag(args, "--notify-trace");
            /* 桌宠：正常启动时和岛同进程共存（见 PetController）。
               下面三个是**独立诊断模式** —— 只起桌宠、不建岛，行为和以前完全一致。 */
            bool pet = Flag(args, "--pet");
            bool petDemo = Flag(args, "--pet-demo");
            bool petSelfTest = Flag(args, "--pet-selftest");
            /* 这一次启动不要桌宠。只作用于本次，不写 ui.txt ——
               验收脚本用它拿到「只有岛」的画面，不能因此改掉用户的偏好。 */
            bool noPet = Flag(args, "--no-pet");
            /* --no-pet 的对称项：这一次启动**要**桌宠，同样只作用于本次、
               不写 ui.txt。验收要在用户 pet=0 的机器上跑真实拖拽，
               又不能为了跑一次测试去改人家的偏好 —— 所以需要这一条。 */
            bool petOn = Flag(args, "--pet-on");
            /* 岛内集成版桌宠的取证跑法：自渲染 + 像素度量 + JSON/PNG，
               只验收集成版，独立 demo 不在这条路上。 */
            bool petProbe = Flag(args, "--pet-probe");
            int freezeShot = 0;
            string freezeArg = Arg(args, "--freeze-shot", "");
            if (freezeArg != "") { int.TryParse(freezeArg, out freezeShot); }

            /* 诊断 / 抓帧模式：这些跑法都在替脚本取证，桌宠是另一张 topmost 窗口，
               会混进抓屏像素，也会让「画面里这点动静是岛还是桌宠」变得没法判读。
               所以这些模式一律不自动拉桌宠 —— 但**不动**用户的 pet 偏好。 */
            bool diagnostic = selftest || wdaProbe || liveProbe || heartbeat
                              || freezeShot > 0 || todoOpen
                              || notify || notifyTest || notifyTrace
                              || excludeCapture || petProbe;
            bool petAuto = !diagnostic && !noPet;

            IslandApp app = new IslandApp();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            /* 控制器要在任何窗口之前拿到日志落点，否则第一个 "[pet]" 行会丢 */
            PetController.Init(log);

            app.Startup += delegate
            {
                /* 独立诊断：桌宠自己一个窗口，不建岛。这是 --pet 系列原本的行为，
                   原样保留 —— 不要改成「顺便也建个岛」，那条路是用来单独调桌宠的。 */
                if (pet || petDemo || petSelfTest)
                {
                    PetWindow pw = new PetWindow(log, petDemo);

                    if (petSelfTest)
                    {
                        pw.Loaded += delegate
                        {
                            System.Windows.Threading.DispatcherTimer probe =
                                new System.Windows.Threading.DispatcherTimer(
                                    System.Windows.Threading.DispatcherPriority.Background);
                            probe.Interval = TimeSpan.FromMilliseconds(700);
                            probe.Tick += delegate
                            {
                                probe.Stop();
                                string report = pw.RunSelfTest();
                                bool ok = report.IndexOf("\"ok\": true") >= 0;
                                Environment.ExitCode = ok ? 0 : 1;
                                app.Shutdown();
                            };
                            probe.Start();
                        };
                    }

                    pw.Show();
                    return;
                }

                /* 正常启动：先建岛，岛 Loaded 之后再按偏好拉桌宠（见 IslandWindow.OnLoaded）。
                   顺序不能反 —— 桌宠是 topmost，先建它再建岛，用户看到的第一帧里
                   岛要从桌宠背后弹出来，观感是「桌宠被压了一下」。 */
                IslandWindow win = new IslandWindow(look, scene, art, state, selftest, log, wdaProbe, noWda, excludeCapture, liveProbe, freezeShot, heartbeat, todoOpen, notify || notifyTest, notifyTest, notifyTrace, petAuto, petOn, petProbe);
                win.Show();
            };

            app.Run();
            return Environment.ExitCode;
        }
    }
}