using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinIsland
{
    /* 消息提醒：判断「微信/QQ 有没有来消息」。

       目标只有一件事：来消息了就亮一颗小球，点小球把那个应用调出来。
       **不读消息内容、不读发送者**，所以不碰数据库、不注入、不做 hook。

       ## 为什么信号必须「按进程归属」

       任务栏红点是最直觉的信号，但它属于整个任务栏 —— 红一下没法知道是微信还是
       QQ，而小球必须知道该亮哪一颗。所以这里只收**能归到具体进程**的信号。

       微信 4.x（实测 4.2.0.13）把两条正路都堵死了：

       1. UI 自动化：主窗口 UIA 树下只有 3 个元素（Qt 自绘层 MMUIRenderSubWindowHW
          底下什么都没有），读不到会话列表，也读不到未读数。
       2. 系统通知平台：它不在
          HKCU\...\CurrentVersion\Notifications\Settings 里（同机只有 QQ 在），
          toast 是自己画的窗口，UserNotificationListener 看不到。

       QQ 9.9（NT/Electron）相反：**注册了**系统通知平台，将来可以直接拿到内容；
       但当前需求不需要内容，所以两个应用先走同一套窗口信号，代码统一。

       ## 信号：窗口「出现/重新可见」

       来消息时客户端会弹一个 toast 窗口（或者在托盘里闪一下主窗）。这些都是一次
       top-level HWND 的 visible 变化，按 PID 归属，天然分清微信还是 QQ。

       用轮询而不是 SetWinEventHook，理由：
       - hook 的回调委托必须一直活到 UnhookWinEvent，C# 里很容易被 GC 提前收走，
         收了就是「静默不工作了」，很难查；
       - hook 需要消息泵，而这里挂在 WPF 的 UI 线程上，泵是别人的，时序不可控；
       - toast 窗口活几秒，400ms 轮询足够，代价是每 400ms 一次 EnumWindows（几百个
         窗口，几十微秒）。 */
    static class Notify
    {
        public enum App { WeChat, QQ }

        /* 日志出口，由 IslandWindow 接到 Trace 上。 */
        public static Action<string> Diag;

        /* 判定「来消息了」。UI 层订阅它去亮球。
           去抖之后才会触发，所以订阅者不用自己防重复。 */
        public static event Action<App> Arrived;

        static void Log(string s) { if (Diag != null) Diag(s); }

        /* 400ms 一次。toast 通常活 3-5 秒，这个密度不会漏。
           再快只是白烧 CPU —— 每次都要 EnumWindows 一遍。 */
        const int PollMs = 400;

        /* 同一应用在这个窗口内只报一次。微信一条消息可能同时冒出多个窗口
           （toast + 托盘 helper + 内嵌页），不去抖就会连亮三下。 */
        const int DebounceMs = 2500;

        /* 怎么把 toast 和主窗口分开：**只能靠面积**。
           4.x 的 toast 和主窗口类名都是 Qt51514QWindowIcon，类名区分不了。
           实测：主窗 1000x700 量级（>700k），托盘 helper 1280x745（953k），
           toast 300x120 量级（36k）。取 520k 一刀切。
           小窗口那一头也要卡：Base_PowerMessageWindow 是 0x0，
           Chrome_SystemMessageWindow 是 135x37 这种常驻隐藏窗。 */
        const long MaxToastArea = 520000;
        const int MinToastW = 90;
        const int MinToastH = 28;

        class Win
        {
            public long Hwnd;
            public uint Pid;
            public bool Visible;
            public int X, Y, W, H;
            public string Cls = "";
            public string Title = "";
        }

        /* 常驻的消息泵窗口，永远不会是「来消息了」。
           这些是实测出来的（不是猜的）：微信 4.2 每个进程都挂着
           Base_PowerMessageWindow 0x0、Chrome_SystemMessageWindow 202x56、
           DisplayICC_SystemMessageWindow 202x56，Chromium 系还有 IME /
           MSCTFIME UI。它们常驻且尺寸正好落在 toast 的判定区间里。 */
        static readonly string[] NoiseClasses = new string[] {
            "Base_PowerMessageWindow",
            "Chrome_SystemMessageWindow",
            "DisplayICC_SystemMessageWindow",
            "Qt51514WxTrayIconMessageWindowClass",
            "IME",
            "MSCTFIME UI",
            "GDI+ Hook Window Class",
            "TXGuiFoundation"
        };

        /* 主窗/托盘窗的标题。这两个标题的窗口是应用外壳，不是提示窗：
           用户自己打开或最小化微信，不该亮球。 */
        static bool IsChrome(Win w, App app)
        {
            if (app == App.WeChat && w.Title == "微信") return true;
            if (app == App.QQ && w.Title == "QQ") return true;
            return false;
        }

        static bool IsNoise(string cls)
        {
            for (int i = 0; i < NoiseClasses.Length; i++)
                if (NoiseClasses[i] == cls) return true;
            return false;
        }

        static DispatcherTimer _timer;
        static Dictionary<long, bool> _seen = new Dictionary<long, bool>();
        static Dictionary<uint, App> _pidApp = new Dictionary<uint, App>();
        static DateTime _pidStamp = DateTime.MinValue;
        static DateTime _firedWx = DateTime.MinValue;
        static DateTime _firedQq = DateTime.MinValue;
        static bool _started;

        /* ---------------------------------------------------------------- 生命周期 */

        public static void Start()
        {
            if (_started) return;
            _started = true;

            RefreshPids();
            /* 基线：启动时已经存在的窗口一律先记成「已知」。
               不这么做第一轮会把所有窗口当成新出现的，开机就亮一片。 */
            _seen = new Dictionary<long, bool>();
            List<Win> now = Snapshot();
            for (int i = 0; i < now.Count; i++) _seen[now[i].Hwnd] = now[i].Visible;

            Log("notify watching " + _pidApp.Count + " processes / " + _seen.Count + " windows");
            Describe();

            _timer = new DispatcherTimer(DispatcherPriority.Background);
            _timer.Interval = TimeSpan.FromMilliseconds(PollMs);
            _timer.Tick += OnTick;
            _timer.Start();

            StartShellHook();
        }

        /* ------------------------------------------------------------ 外壳钩子

           `HSHELL_FLASH`：应用**请求注意**时 Windows 会发这个（闪任务栏就是它的
           可见表现）。新消息正是这个场景，所以它比轮询窗口直接得多 —— 事件驱动、
           按 HWND 归属，不用猜 toast 长什么样。

           轮询那条路仍然留着：钩子只在窗口真的要闪时才来，如果客户端把
           「闪任务栏」关掉了，就还得靠窗口出现。两条取或。

           为什么要自己的 HwndSource：`RegisterShellHookWindow` 绑在一个窗口上，
           绑到岛的主窗上会和它自己的消息处理混在一起（穿透样式那些改扩展样式的
           操作也在收消息）。这里单独建一个 HWND_MESSAGE 消息窗，互不干扰。 */
        const int HWND_MESSAGE = -3;
        const int HSHELL_FLASH_MASK = 6;

        static HwndSource _hook;
        static int _shellMsg;

        static void StartShellHook()
        {
            if (_hook != null) return;
            try
            {
                _shellMsg = RegisterWindowMessageW("SHELLHOOK");
                HwndSourceParameters p = new HwndSourceParameters("WinIslandNotifyHook");
                p.Width = 0;
                p.Height = 0;
                p.WindowStyle = 0;
                p.ExtendedWindowStyle = 0;
                p.ParentWindow = new IntPtr(HWND_MESSAGE);
                _hook = new HwndSource(p);
                _hook.AddHook(ShellProc);
                bool ok = RegisterShellHookWindow(_hook.Handle);
                Log("notify shell hook msg=0x" + _shellMsg.ToString("X") + " registered=" + ok);
            }
            catch (Exception ex)
            {
                Log("notify shell hook failed: " + ex.Message);
            }
        }

        static void StopShellHook()
        {
            if (_hook == null) return;
            try
            {
                DeregisterShellHookWindow(_hook.Handle);
                _hook.RemoveHook(ShellProc);
                _hook.Dispose();
            }
            catch { }
            _hook = null;
        }

        static IntPtr ShellProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg != _shellMsg) return IntPtr.Zero;

            /* 低 15 位是比较用的：不同来源对 HSHELL_FLASH 给的是 0x8006 还是 6
               并不一致，按低 15 位比能同时接住两种。 */
            int code = wParam.ToInt32() & 0x7FFF;
            if (code != HSHELL_FLASH_MASK) return IntPtr.Zero;

            IntPtr target = lParam;
            if (target == IntPtr.Zero) return IntPtr.Zero;

            uint pid;
            GetWindowThreadProcessId(target, out pid);

            /* 子进程（WeChatAppEx 等）可能是刚起的，先刷一次进程表 */
            RefreshPids();

            App app;
            if (!_pidApp.TryGetValue(pid, out app)) return IntPtr.Zero;

            Fire(app, "shell flash pid=" + pid);
            return IntPtr.Zero;
        }

        public static void Stop()
        {
            StopShellHook();
            if (_timer != null) { _timer.Stop(); _timer = null; }
            _started = false;
        }

        /* 把当前认到的窗口全部打一遍。排查「为什么不亮」时，
           先看这里有没有把 toast 归到正确的应用上。 */
        public static void Describe()
        {
            List<Win> now = Snapshot();
            for (int i = 0; i < now.Count; i++)
            {
                Win w = now[i];
                Log("notify win pid=" + w.Pid + " vis=" + w.Visible
                    + " " + w.W + "x" + w.H + " at " + w.X + "," + w.Y
                    + " plausible=" + Plausible(w)
                    + " cls='" + w.Cls + "' title='" + w.Title + "'");
            }
        }

        /* ---------------------------------------------------------------- 轮询 */

        static void OnTick(object sender, EventArgs e)
        {
            RefreshPids();
            if (_pidApp.Count == 0) { _seen = new Dictionary<long, bool>(); return; }

            List<Win> now = Snapshot();
            Dictionary<long, bool> next = new Dictionary<long, bool>(now.Count);

            for (int i = 0; i < now.Count; i++)
            {
                Win w = now[i];
                next[w.Hwnd] = w.Visible;

                bool wasVisible;
                bool known = _seen.TryGetValue(w.Hwnd, out wasVisible);

                if (!w.Visible) continue;
                if (!Plausible(w)) continue;

                bool appeared = !known;
                bool revealed = known && !wasVisible;
                if (!appeared && !revealed) continue;

                App app;
                if (!_pidApp.TryGetValue(w.Pid, out app)) continue;

                /* 主窗出现/最小化不是消息，是用户自己在动它 */
                if (IsChrome(w, app)) continue;

                Fire(app, (appeared ? "new window" : "window shown")
                          + " " + w.W + "x" + w.H + " at " + w.X + "," + w.Y
                          + " cls='" + w.Cls + "' title='" + w.Title + "'");
            }

            _seen = next;
        }

        static void Fire(App app, string why)
        {
            DateTime now = DateTime.UtcNow;
            DateTime last = app == App.WeChat ? _firedWx : _firedQq;
            if ((now - last).TotalMilliseconds < DebounceMs)
            {
                Log("notify " + app + " suppressed: " + why);
                return;
            }
            if (app == App.WeChat) _firedWx = now; else _firedQq = now;

            Log("notify " + app + " ARRIVED: " + why);
            if (Arrived != null)
            {
                try { Arrived(app); }
                catch (Exception ex) { Log("notify handler failed: " + ex.Message); }
            }
        }

        /* ---------------------------------------------------------------- 进程 */

        /* 进程表缓存 3 秒。Process.GetProcesses 一次几百个对象，
           400ms 跑一遍纯属浪费；而 WeChatAppEx 这类子进程是随时起落的，
           又不能只取一次。 */
        static void RefreshPids()
        {
            if ((DateTime.UtcNow - _pidStamp).TotalMilliseconds < 3000) return;
            _pidStamp = DateTime.UtcNow;

            Dictionary<uint, App> map = new Dictionary<uint, App>();
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch { return; }

            for (int i = 0; i < all.Length; i++)
            {
                Process p = all[i];
                try
                {
                    string n = p.ProcessName;
                    App app;
                    if (TryApp(n, out app)) map[(uint)p.Id] = app;
                }
                catch { }
                try { p.Dispose(); } catch { }
            }
            _pidApp = map;
        }

        /* 只认这几个进程名。注意不能写 QQ 的模糊匹配 —— 这台机器上还有
           QQMusic.exe，匹配宽了听歌都会被当成 QQ 来消息。 */
        static bool TryApp(string proc, out App app)
        {
            app = App.WeChat;
            if (proc == "Weixin" || proc == "WeChat" || proc == "WeChatAppEx") { app = App.WeChat; return true; }
            if (proc == "QQ") { app = App.QQ; return true; }
            return false;
        }

        /* ---------------------------------------------------------------- 窗口 */

        static bool Plausible(Win w)
        {
            /* 最小化窗口停在 -32000。IsWindowVisible 对最小化窗口返回 true，
               所以不排掉它，用户一最小化微信就会被当成「来消息」。 */
            if (w.X <= -30000 || w.Y <= -30000) return false;
            if (IsNoise(w.Cls)) return false;
            if (w.W < MinToastW || w.H < MinToastH) return false;
            if ((long)w.W * (long)w.H > MaxToastArea) return false;
            return true;
        }

        static List<Win> Snapshot()
        {
            List<Win> list = new List<Win>();
            if (_pidApp.Count == 0) return list;

            EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                if (!_pidApp.ContainsKey(pid)) return true;

                RECT r;
                if (!GetWindowRect(h, out r)) return true;

                Win w = new Win();
                w.Hwnd = h.ToInt64();
                w.Pid = pid;
                w.Visible = IsWindowVisible(h);
                w.X = r.L;
                w.Y = r.T;
                w.W = r.R - r.L;
                w.H = r.B - r.T;

                StringBuilder sb = new StringBuilder(256);
                GetClassNameW(h, sb, 256);
                w.Cls = sb.ToString();

                StringBuilder tb = new StringBuilder(512);
                GetWindowTextW(h, tb, 512);
                w.Title = tb.ToString();

                list.Add(w);
                return true;
            }, IntPtr.Zero);

            return list;
        }

        /* 点击小球把微信/QQ 调出来。

   —— 三个**实测证实**的坑，改之前先看：

   坑一：旧 `FindMainWindow` 的面积兜底没有任何可见性过滤。本机实测：Weixin.exe
   没跑、只剩 WeChatAppEx 的时候，它挑中的是
   `Chrome_WidgetWin_0 1280x745 vis=False`（隐藏窗口）。对隐藏窗口
   `SetForegroundWindow` 必然失败。

   坑二（最要命）：失败之后旧代码会 `Process.Start(ExePathOf(app))`。而
   `ExePathOf` 是**遍历运行中的进程**取 `MainModule.FileName` —— 也就是说它只在
   应用**已经在跑**时拿得到路径。于是那条"启动"分支从来不是启动，只可能是
   **再开一个实例**。微信被开第二份之后抢同一个数据目录，就卡住了。
   所以这条分支直接删掉，不留。

   坑三：整条链全是跨进程的**同步窗口消息**（`ShowWindow` 会往目标线程发
   WM_WINDOWPOSCHANGING 并等它返回）。放在 UI 线程上，对方忙多久岛就卡多久 ——
   用户看到的就是"岛卡死"。现在整段丢到线程池，岛继续渲染。 */
public static bool Activate(App app)
{
    System.Threading.ThreadPool.QueueUserWorkItem(delegate { ActivateCore(app); });
    return true;
}

/* 逐步计时：以后万一还卡，日志里能直接看出是哪一步慢，不用再猜。 */
static void ActivateCore(App app)
{
    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

    RefreshPids();
    long tPids = sw.ElapsedMilliseconds;

    IntPtr hwnd = FindMainWindow(app);
    long tFind = sw.ElapsedMilliseconds;

    bool focused = false;
    if (hwnd != IntPtr.Zero) focused = BringToFront(hwnd);
    long tFront = sw.ElapsedMilliseconds;

    /* 找不到能激活的窗口就到此为止。绝不 Process.Start —— 见坑二。 */
    Log("notify activate " + app
        + " focused=" + (focused ? 1 : 0)
        + " pids=" + tPids + "ms"
        + " find=" + (tFind - tPids) + "ms"
        + " front=" + (tFront - tFind) + "ms"
        + " total=" + sw.ElapsedMilliseconds + "ms");
}

static bool BringToFront(IntPtr hwnd)
{
    uint fgThread = 0;
    bool attached = false;
    try
    {
        /* 缩在 -32000 的是最小化，SW_RESTORE 能拉回来 */
        ShowWindow(hwnd, SW_RESTORE);
        ShowWindow(hwnd, SW_SHOW);

        IntPtr fg = GetForegroundWindow();
        fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out _fgPid);
        uint me = GetCurrentThreadId();

        if (fgThread != 0 && fgThread != me)
            attached = AttachThreadInput(me, fgThread, true);

        return SetForegroundWindow(hwnd);
    }
    catch (Exception ex)
    {
        Log("notify bring-to-front failed: " + ex.Message);
        return false;
    }
    finally
    {
        /* 解挂必须无条件执行。输入队列一旦挂上没解开，本线程的输入处理就和对方
           线程永久绑在一起 —— 那本身就是另一种"卡死"。旧代码只在正常路径解挂。 */
        if (attached)
        {
            try { AttachThreadInput(GetCurrentThreadId(), fgThread, false); }
            catch { }
        }
    }
}

/* GetWindowThreadProcessId 的 out 参数需要一个真实变量（C# 5 不能内联声明），
   这里的 pid 用不上，只借它拿线程 id。 */
static uint _fgPid;

/* 主窗口不能按「面积最大」挑。

   实测两个陷阱：微信那个 1920x1117 的 `Qt51514WxTrayIconMessageWindowClass`
   托盘 helper 比主窗大得多，而且**是隐藏的**；WeChatAppEx 常驻的
   `Chrome_WidgetWin_0` 也是 1280x745 隐藏窗。旧代码只比面积，于是拿隐藏窗去
   `SetForegroundWindow`，必然失败。

   现在的顺序：
     1) 标题精确匹配（最小化时面积判据完全失效，只有标题靠得住）；
     2) 兜底只接受**可见**、非噪声类、非零尺寸、不在 -32000 的窗口。 */
static IntPtr FindMainWindow(App app)
{
    IntPtr byTitle = IntPtr.Zero;
    IntPtr byArea = IntPtr.Zero;
    long bestArea = 0;
    Dictionary<uint, App> map = _pidApp;

    EnumWindows(delegate(IntPtr h, IntPtr p)
    {
        uint pid;
        GetWindowThreadProcessId(h, out pid);
        App a;
        if (!map.TryGetValue(pid, out a) || a != app) return true;

        /* 噪声类先排掉，顺带省一次跨进程的 GetWindowTextW */
        StringBuilder cb = new StringBuilder(256);
        GetClassNameW(h, cb, 256);
        if (IsNoise(cb.ToString())) return true;

        StringBuilder tb = new StringBuilder(512);
        GetWindowTextW(h, tb, 512);
        string title = tb.ToString();

        RECT r;
        if (!GetWindowRect(h, out r)) return true;
        int w = r.R - r.L;
        int hh = r.B - r.T;
        if (w <= 0 || hh <= 0) return true;

        bool isMainTitle = (app == App.WeChat && title == "微信")
                        || (app == App.QQ && title == "QQ");
        /* 标题命中就直接定下来：主窗最小化时只有 237x39，面积比不过隐藏 helper */
        if (isMainTitle && byTitle == IntPtr.Zero) { byTitle = h; return true; }

        /* 兜底必须是用户真的看得见、能激活的窗口 */
        if (!IsWindowVisible(h)) return true;
        if (r.L <= -30000 || r.T <= -30000) return true;

        long area = (long)w * (long)hh;
        if (area > bestArea) { bestArea = area; byArea = h; }
        return true;
    }, IntPtr.Zero);

    if (byTitle != IntPtr.Zero) return byTitle;
    return byArea;
}        /* 找该应用「最大的那个可见窗口」当主窗。藏到离屏的也算，
           因为 ShowWindow(SW_RESTORE) 能把位置拉回来。 */
        static IntPtr FindWindowOf(App app)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = 0;
            Dictionary<uint, App> map = _pidApp;

            EnumWindows(delegate(IntPtr h, IntPtr p)
            {
                uint pid;
                GetWindowThreadProcessId(h, out pid);
                App a;
                if (!map.TryGetValue(pid, out a) || a != app) return true;

                RECT r;
                if (!GetWindowRect(h, out r)) return true;
                long area = (long)(r.R - r.L) * (long)(r.B - r.T);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);

            return best;
        }

        /* ---------------------------------------------------------------- Win32 */

        const int SW_RESTORE = 9;
        const int SW_SHOW = 5;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int L, T, R, B; }

        delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumProc cb, IntPtr lparam);

        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassNameW(IntPtr hwnd, StringBuilder sb, int max);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowTextW(IntPtr hwnd, StringBuilder sb, int max);

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hwnd, int cmd);

        [DllImport("user32.dll")]
        static extern bool SetForegroundWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();

        [DllImport("kernel32.dll")]
        static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        static extern bool AttachThreadInput(uint attach, uint to, bool doAttach);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int RegisterWindowMessageW(string name);

        [DllImport("user32.dll")]
        static extern bool RegisterShellHookWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool DeregisterShellHookWindow(IntPtr hwnd);
    }
}