using System;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using WpfApplication = System.Windows.Application;

namespace WinIsland
{
    sealed class PetDemoApp : WpfApplication
    {
        NotifyIcon _tray;
        PetWindow _pet;

        public void RunDemo(bool autoDemo, bool selfTest, string logPath)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Startup += delegate
            {
                _pet = new PetWindow(logPath, autoDemo);
                _pet.Show();
                if (selfTest) RunSelfTest();
                else ShowTray();
            };
            Run();
        }

        void ShowTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("戳一下", null, delegate { _pet.Poke(); });
            menu.Items.Add("转一圈", null, delegate { _pet.Spin(); });
            ToolStripMenuItem state = new ToolStripMenuItem("表情");
            state.DropDownItems.Add("开心", null, delegate { _pet.SetState("happy"); });
            state.DropDownItems.Add("思考", null, delegate { _pet.SetState("thinking"); });
            state.DropDownItems.Add("困倦", null, delegate { _pet.SetState("sleepy"); });
            state.DropDownItems.Add("好奇", null, delegate { _pet.SetState("curious"); });
            state.DropDownItems.Add("随机", null, delegate { _pet.SetState("idle"); });
            menu.Items.Add(state);
            ToolStripMenuItem reference = new ToolStripMenuItem("参考动画");
            foreach (PetClipGroup group in ReferenceClips.Groups)
            {
                ToolStripMenuItem submenu = new ToolStripMenuItem(group.Name);
                foreach (string clip in group.Clips)
                {
                    string clipName = clip;
                    submenu.DropDownItems.Add(clipName, null,
                        delegate { _pet.PlayClip(clipName); });
                }
                reference.DropDownItems.Add(submenu);
            }
            menu.Items.Add(reference);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("退出桌宠", null, delegate { Shutdown(); });
            _tray = new NotifyIcon();
            _tray.Icon = System.Drawing.SystemIcons.Application;
            _tray.Text = "桌宠 Demo";
            _tray.ContextMenuStrip = menu;
            _tray.Visible = true;
        }

        void RunSelfTest()
        {
            DispatcherTimer timer = new DispatcherTimer(DispatcherPriority.Background);
            timer.Interval = TimeSpan.FromMilliseconds(700);
            timer.Tick += delegate
            {
                timer.Stop();
                string report = _pet.RunSelfTest();
                bool ok = report.IndexOf("\"ok\": true") >= 0;
                Environment.ExitCode = ok ? 0 : 1;
                Shutdown();
            };
            timer.Start();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
            }
            base.OnExit(e);
        }
    }

    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            Native.MakeDpiAware();
            bool selfTest = Array.IndexOf(args, "--selftest") >= 0;
            bool autoDemo = Array.IndexOf(args, "--demo") >= 0;
            string logPath = "";
            for (int i = 0; i < args.Length; i++)
                if (args[i].StartsWith("--log=")) logPath = args[i].Substring(6);

            PetDemoApp app = new PetDemoApp();
            app.RunDemo(autoDemo, selfTest, logPath);
            return Environment.ExitCode;
        }
    }
}
