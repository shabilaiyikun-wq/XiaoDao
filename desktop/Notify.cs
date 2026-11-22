using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
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

        /* HSHELL_FLASH = 0x8006，而 HSHELL_REDRAW = 0x0006 —— 两者只差最高位。
           按低 15 位比较会把两者折叠成一个值（0x8006 & 0x7FFF == 0x0006），
           所以这里必须整值比较、分开处理。两个都是**真的**信号，不能只留一个。

           ## 为什么 REDRAW 必须收（这条踩过一次，别再删）

           上一轮把判定收紧成「只认 0x8006」，理由是 REDRAW 会让改标题也亮球。
           结果微信来消息的提醒**整个消失了**：微信 4.x 全程不调
           FlashWindowEx，它把「有未读」这件事反映成托盘/标题的重画 ——
           也就是 HSHELL_REDRAW，而不是 HSHELL_FLASH。只认 FLASH 等于把微信
           这一路彻底掐断（QQ 反而两个都可能给）。

           所以两个都收，但按可信度分级，见 ShellProc。 */
        const int HSHELL_FLASH = 0x8006;
        const int HSHELL_REDRAW = 0x0006;

        /* REDRAW 的冷却单独一档，比 FLASH 的 2500ms 长得多。

           实测（work/shellhook-probe.exe，本机 70s）：QQ 音乐换曲时它的
           TXGuiFoundation 托盘窗在 40ms 内连打 4 发 REDRAW。也就是说 REDRAW
           天然会成串出现，两秒的去抖挡不住「一首歌亮一次球」这种误报。 */
        const int RedrawCooldownMs = 20000;

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

            int code = wParam.ToInt32();
            bool flash = code == HSHELL_FLASH;
            bool redraw = code == HSHELL_REDRAW;
            if (!flash && !redraw) return IntPtr.Zero;

            IntPtr target = lParam;
            if (target == IntPtr.Zero) return IntPtr.Zero;

            uint pid;
            GetWindowThreadProcessId(target, out pid);

            /* 子进程（WeChatAppEx 等）可能是刚起的，先刷一次进程表 */
            RefreshPids();

            App app;
            if (!_pidApp.TryGetValue(pid, out app)) return IntPtr.Zero;

            if (flash)
            {
                /* 应用明确「请求注意」：可信度最高，直接报。 */
                LogShellEvent(app, code, "", "accepted (flash)");
                Fire(app, "shell flash pid=" + pid);
                return IntPtr.Zero;
            }

            /* ---- 以下是 REDRAW：微信唯一给的信号，但它也是噪声的主要来源 ---- */

            StringBuilder cb = new StringBuilder(256);
            GetClassNameW(target, cb, 256);
            string cls = cb.ToString();

            /* 注意：这里**不能**用 IsNoise。那张表是给「窗口出现」那条路用的，
               里面包含 Qt51514WxTrayIconMessageWindowClass —— 而微信「有未读」
               恰恰就是这个托盘窗在重画。拿 IsNoise 挡红画等于把信号又掐一次。
               红画只排真正会高频重画的输入法/文本服务窗。 */
            if (IsChatty(cls)) return IntPtr.Zero;

            /* 0x0 的窗口重画不携带任何信息 */
            RECT rr;
            if (GetWindowRect(target, out rr))
            {
                if (rr.R - rr.L <= 0 && rr.B - rr.T <= 0) return IntPtr.Zero;
            }

            /* 你正看着这个应用的时候不用提醒你 —— 这一条就能挡掉绝大部分
               「在微信里打字、标题跟着变」引起的 REDRAW。 */
            if (ForegroundIs(app)) return IntPtr.Zero;

            LogShellEvent(app, code, cls, "accepted");
            FireRedraw(app, "shell redraw pid=" + pid + " cls='" + cls + "'");
            return IntPtr.Zero;
        }

        /* 真正吵的只有文本服务那一类：焦点一动就重画，和消息毫无关系。 */
        static readonly string[] ChattyClasses = new string[] {
            "IME",
            "MSCTFIME UI"
        };

        static bool IsChatty(string cls)
        {
            for (int i = 0; i < ChattyClasses.Length; i++)
                if (ChattyClasses[i] == cls) return true;
            return false;
        }

        /* 上一次打「收到什么 shell 事件」的时间。限速是为了不把日志冲掉：
           每发只值一行，且只在排查「为什么不亮」时才需要。 */
        static DateTime _lastEventLog = DateTime.MinValue;

        static void LogShellEvent(App app, int code, string cls, string verdict)
        {
            DateTime now = DateTime.UtcNow;
            if ((now - _lastEventLog).TotalMilliseconds < 3000) return;
            _lastEventLog = now;
            Log("notify shell event 0x" + code.ToString("X4") + " " + app
                + " cls='" + cls + "' -> " + verdict);
        }

        /* 前台窗口是不是这个应用自己的。跨进程查询，只在红画事件上走一次，
           不在 400ms 轮询路径上。 */
        static bool ForegroundIs(App app)
        {
            IntPtr fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint pid;
            GetWindowThreadProcessId(fg, out pid);
            App a;
            return _pidApp.TryGetValue(pid, out a) && a == app;
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

        /* REDRAW 那一档。除了自己那档长冷却，还要**同时**更新 FLASH 的去抖时间戳，
           否则「红画亮一颗 → 2.5 秒后真的 FLASH 又亮一颗」会连亮两下。 */
        static DateTime _redrawWx = DateTime.MinValue;
        static DateTime _redrawQq = DateTime.MinValue;

        static void FireRedraw(App app, string why)
        {
            DateTime now = DateTime.UtcNow;
            DateTime last = app == App.WeChat ? _redrawWx : _redrawQq;
            long age = (long)(now - last).TotalMilliseconds;
            if (age < RedrawCooldownMs)
            {
                /* 冷却期内的红画不写日志：一首歌 4 发，全写会把有用的行冲掉 */
                return;
            }

            DateTime lastFire = app == App.WeChat ? _firedWx : _firedQq;
            if ((now - lastFire).TotalMilliseconds < DebounceMs) return;

            if (app == App.WeChat) { _redrawWx = now; _firedWx = now; }
            else { _redrawQq = now; _firedQq = now; }

            Log("notify " + app + " ARRIVED (redraw, cooldown " + RedrawCooldownMs + "ms): " + why);
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

    int wasIconic = 0, wasVisible = 0;
    bool focused = false;
    if (hwnd != IntPtr.Zero)
    {
        /* 记录调用前的真实状态：卡死投诉里最能区分的两个量就是它俩 */
        wasIconic = IsIconic(hwnd) ? 1 : 0;
        wasVisible = IsWindowVisible(hwnd) ? 1 : 0;
        focused = BringToFront(hwnd, app);
    }
    else if (app == App.WeChat)
    {
        focused = TryWakeFromTray();
    }
    long tFront = sw.ElapsedMilliseconds;

    /* 激活之后目标还答不答消息 —— 这就是"卡死"的可观测定义。
       打进日志，下次再出问题不用靠回忆。 */
    int hung = 0;
    if (hwnd != IntPtr.Zero && IsHungAppWindow(hwnd)) hung = 1;

    /* 找不到能激活的窗口就到此为止。绝不 Process.Start —— 见坑二。 */
    Log("notify activate " + app
        + " focused=" + (focused ? 1 : 0)
        + " wasIconic=" + wasIconic
        + " wasVisible=" + wasVisible
        + " hwnd=0x" + hwnd.ToInt64().ToString("X")
        + (hung == 1 ? " TARGET-HUNG" : "")
        + " pids=" + tPids + "ms"
        + " find=" + (tFind - tPids) + "ms"
        + " front=" + (tFront - tFind) + "ms"
        + " total=" + sw.ElapsedMilliseconds + "ms");
}

/* 等某个窗口状态真的成立。返回它是否成立过。 */
static bool WaitFor(IntPtr hwnd, Func<IntPtr, bool> pred, int budgetMs)
{
    int spent = 0;
    while (spent < budgetMs)
    {
        if (pred(hwnd)) return true;
        System.Threading.Thread.Sleep(20);
        spent += 20;
    }
    return pred(hwnd);
}

static bool BringToFront(IntPtr hwnd, App app)
{
    /* ---- 坑四（最容易把人坑死的一条）：AttachThreadInput 不能挂到目标自己身上 ----

       旧代码先 `ShowWindow(SW_SHOW)`、**之后**才取 `GetForegroundWindow()`。
       而 SW_SHOW 本身就会把目标变成前台窗口 —— 于是取到的"当前前台"就是目标
       自己，`AttachThreadInput` 把自己的线程挂到了**目标的 UI 线程**上。

       这一步没有任何好处（借用前台权利的意图是"借用别人的"，不是借用自己的），
       却把目标的输入队列（焦点/键盘状态/鼠标捕获/活动窗口）和一个**不泵消息**
       的线程池线程共享了。挂着的这段时间里目标应用的输入处理要靠本线程配合；
       一旦解挂慢了、失败了，或者本线程被 CLR 回收去跑别的事，目标就**假死**。
       表现就是："点球把微信叫出来，微信界面卡死"。

       所以前台窗口必须在**任何 ShowWindow 之前**取，并且要排除目标自己的线程。 */
    IntPtr fgBefore = GetForegroundWindow();
    uint fgThread = 0;
    bool fgIsSelf = false;

    if (fgBefore != IntPtr.Zero && fgBefore != hwnd)
    {
        uint fgPid;
        uint t = GetWindowThreadProcessId(fgBefore, out fgPid);
        App other;

        /* 前台本来就是目标应用（可见的另一张窗）→ 借它没意义，反而自己咬自己 */
        bool sameApp = _pidApp.TryGetValue(fgPid, out other) && other == app;

        /* 前台是**我们自己**（用户刚点过岛，岛成了前台）→ 同样绝不能挂。
           `_pidApp` 里只有微信/QQ 的 pid，查不到自己，所以必须单独判一次；
           否则线程池线程会挂到岛的 UI 线程上，把岛自己的输入队列一起拖死。 */
        fgIsSelf = (fgPid == (uint)System.Diagnostics.Process.GetCurrentProcess().Id);

        if (!sameApp && !fgIsSelf) fgThread = t;
    }

    try
    {
        if (!IsWindowVisible(hwnd))
        {
            Log("notify bring-to-front: refusing hidden window 0x" + hwnd.ToInt64().ToString("X"));
            return false;
        }

        /* 只还原真正最小化的窗口；托盘隐藏由系统托盘按钮负责唤起。 */
        if (IsIconic(hwnd))
        {
            ShowWindowAsync(hwnd, SW_RESTORE);
            if (!WaitFor(hwnd, delegate(IntPtr h) { return !IsIconic(h); }, 900))
            {
                Log("notify bring-to-front: minimized window did not restore");
                return false;
            }
        }

        /* ---- 先直接试，绝大多数情况下到这就结束了 ----

           用户在岛上刚点过鼠标，本进程拿得到前台权利。走这条路时我们
           **完全不碰任何人的输入队列**，也就不可能拖住别人。 */
        if (SetForegroundWindow(hwnd)) return true;

        /* ---- 只有被前台锁挡住，才退到「借用前台线程权利」这一步 ----

           这一步是唯一能拖垮别人输入队列的操作，所以门槛卡到最严：
             · 目标是应用自己的线程 → 不挂（见坑四）
             · 目标是我们自己的进程   → 不挂（挂自己咬自己，会拖死岛）
             · 消息队列只在真要挂之前才建，挂完立刻解开 */
        bool attached = false;
        try
        {
            uint me = GetCurrentThreadId();
            if (fgThread != 0 && fgThread != me)
            {
                /* AttachThreadInput 要求调用线程有消息队列。线程池线程默认没有，
                   在没队列的线程上挂接行为未定义，所以先建出队列。 */
                MSG dummy;
                PeekMessageW(out dummy, IntPtr.Zero, 0, 0, PM_NOREMOVE);

                attached = AttachThreadInput(me, fgThread, true);
                if (attached)
                    Log("notify bring-to-front: input attach fallback, fg thread=" + fgThread);
            }
            return SetForegroundWindow(hwnd);
        }
        finally
        {
            /* 解挂必须无条件执行。输入队列一旦挂上没解开，两个线程的输入处理
               就永久绑在一起 —— 那本身就是另一种"卡死"。 */
            if (attached)
            {
                try { AttachThreadInput(GetCurrentThreadId(), fgThread, false); }
                catch { }
            }
        }
    }
    catch (Exception ex)
    {
        Log("notify bring-to-front failed: " + ex.Message);
        return false;
    }
}

static bool IsWechatTrayName(string name)
{
    if (String.IsNullOrWhiteSpace(name)) return false;
    string value = name.Trim();
    if (value.StartsWith("微信输入法", StringComparison.Ordinal)) return false;
    return value.StartsWith("微信", StringComparison.Ordinal)
        || value.StartsWith("WeChat", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("Weixin", StringComparison.OrdinalIgnoreCase);
}

static List<AutomationElement> FindWechatTrayButtons(AutomationElement root, bool taskbarRoot)
{
    List<AutomationElement> targets = new List<AutomationElement>();
    if (root == null) return targets;

    AutomationElementCollection items = root.FindAll(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
    for (int i = 0; i < items.Count; i++)
    {
        try
        {
            AutomationElement item = items[i];
            AutomationElement.AutomationElementInformation info = item.Current;
            if (!info.IsEnabled || info.IsOffscreen || !IsWechatTrayName(info.Name)) continue;
            if (taskbarRoot && info.ClassName != "SystemTray.NormalButton") continue;

            System.Windows.Rect bounds = info.BoundingRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.Width > 200 || bounds.Height > 200) continue;

            object pattern;
            if (!item.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) continue;

            bool duplicate = false;
            for (int j = 0; j < targets.Count; j++)
            {
                AutomationElement.AutomationElementInformation previous = targets[j].Current;
                if (previous.Name == info.Name && previous.BoundingRectangle == bounds)
                {
                    duplicate = true;
                    break;
                }
            }
            if (!duplicate) targets.Add(item);
        }
        catch { }
    }
    return targets;
}

static bool TryInvokeUniqueWechatButton(List<AutomationElement> targets, string location)
{
    if (targets.Count != 1)
    {
        if (targets.Count > 1) Log("notify tray wake: ambiguous 微信 buttons in " + location);
        return false;
    }

    try
    {
        object pattern;
        if (!targets[0].TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) return false;
        ((InvokePattern)pattern).Invoke();
        Log("notify tray wake: invoked unique 微信 button in " + location);
        return true;
    }
    catch (Exception ex)
    {
        Log("notify tray wake: invoke failed: " + ex.Message);
        return false;
    }
}

static bool TryOpenOverflow(IntPtr taskbar, AutomationElement taskbarElement)
{
    IntPtr tray = FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", IntPtr.Zero);
    IntPtr legacyButton = tray == IntPtr.Zero ? IntPtr.Zero
        : FindWindowExW(tray, IntPtr.Zero, "Button", IntPtr.Zero);
    if (legacyButton != IntPtr.Zero)
    {
        try
        {
            AutomationElement button = AutomationElement.FromHandle(legacyButton);
            object pattern;
            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
            {
                ((InvokePattern)pattern).Invoke();
                return true;
            }
        }
        catch { }
    }

    if (taskbarElement == null) return false;
    AutomationElementCollection buttons = taskbarElement.FindAll(TreeScope.Descendants,
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
    List<AutomationElement> toggles = new List<AutomationElement>();
    for (int i = 0; i < buttons.Count; i++)
    {
        try
        {
            AutomationElement item = buttons[i];
            AutomationElement.AutomationElementInformation info = item.Current;
            string label = (info.Name ?? "").ToLowerInvariant();
            bool isOverflow = info.AutomationId == "SystemTrayIcon"
                && (label.Contains("hidden") || label.Contains("隐藏") || label.Contains("隠れて"));
            if (!info.IsEnabled || info.IsOffscreen || !isOverflow) continue;
            object pattern;
            if (item.TryGetCurrentPattern(InvokePattern.Pattern, out pattern)) toggles.Add(item);
        }
        catch { }
    }
    return TryInvokeUniqueWechatButton(toggles, "the overflow toggle");
}

static bool TryWakeFromTray()
{
    try
    {
        IntPtr taskbar = FindWindowW("Shell_TrayWnd", IntPtr.Zero);
        if (taskbar == IntPtr.Zero) return false;
        AutomationElement taskbarElement = AutomationElement.FromHandle(taskbar);
        IntPtr tray = FindWindowExW(taskbar, IntPtr.Zero, "TrayNotifyWnd", IntPtr.Zero);

        List<AutomationElement> targets = new List<AutomationElement>();
        AddTrayTargets(targets, FindWechatTrayButtons(taskbarElement, true));
        if (tray != IntPtr.Zero)
            AddTrayTargets(targets, FindWechatTrayButtons(AutomationElement.FromHandle(tray), false));
        if (TryInvokeUniqueWechatButton(targets, "the visible taskbar tray")) return true;
        if (targets.Count > 1) return false;

        if (!TryOpenOverflow(taskbar, taskbarElement))
        {
            Log("notify tray wake: could not open the hidden-icons area");
            return false;
        }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            System.Threading.Thread.Sleep(80);
            IntPtr overflow = FindWindowW("TopLevelWindowForOverflowXamlIsland", IntPtr.Zero);
            if (overflow == IntPtr.Zero) overflow = FindWindowW("NotifyIconOverflowWindow", IntPtr.Zero);
            if (overflow == IntPtr.Zero || !IsWindowVisible(overflow)) continue;

            targets = FindWechatTrayButtons(AutomationElement.FromHandle(overflow), false);
            if (TryInvokeUniqueWechatButton(targets, "the hidden-icons area")) return true;
            if (targets.Count > 1) return false;
        }
    }
    catch (Exception ex)
    {
        Log("notify tray wake failed: " + ex.Message);
    }
    return false;
}

static void AddTrayTargets(List<AutomationElement> target, List<AutomationElement> source)
{
    for (int i = 0; i < source.Count; i++)
    {
        bool duplicate = false;
        AutomationElement.AutomationElementInformation current = source[i].Current;
        for (int j = 0; j < target.Count; j++)
        {
            AutomationElement.AutomationElementInformation existing = target[j].Current;
            if (existing.Name == current.Name && existing.BoundingRectangle == current.BoundingRectangle)
            {
                duplicate = true;
                break;
            }
        }
        if (!duplicate) target.Add(source[i]);
    }
}

/* 微信只接受可见且标题精确匹配的主窗；托盘隐藏时交给系统托盘按钮唤起。
   QQ 保留可见窗口的面积兜底。 */
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
        if (!IsWindowVisible(h)) return true;

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
        /* 标题命中就直接定下来：最小化窗口仍可见，托盘隐藏窗口已在上方排除。 */
        if (isMainTitle && byTitle == IntPtr.Zero) { byTitle = h; return true; }

        /* 兜底只接受用户真的看得见、能激活的窗口 */
        if (r.L <= -30000 || r.T <= -30000) return true;

        long area = (long)w * (long)hh;
        if (area > bestArea) { bestArea = area; byArea = h; }
        return true;
    }, IntPtr.Zero);

    if (byTitle != IntPtr.Zero) return byTitle;
    if (app == App.WeChat) return IntPtr.Zero;
    return byArea;
}

        /* ---------------------------------------------------------------- Win32 */

        const int SW_RESTORE = 9;

        /* PeekMessageW 的 PM_NOREMOVE：只为了让本线程建出输入队列，
           不取走任何消息。AttachThreadInput 之前必须做。 */
        const uint PM_NOREMOVE = 0;

        [StructLayout(LayoutKind.Sequential)]
        struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public int x;
            public int y;
            public uint lPrivate;
        }

        [DllImport("user32.dll")]
        static extern bool PeekMessageW(out MSG m, IntPtr hwnd, uint min, uint max, uint remove);

        [DllImport("user32.dll")]
        static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        static extern bool IsHungAppWindow(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int L, T, R, B; }

        [DllImport("user32.dll")]
        static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowW(string className, IntPtr windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string className, IntPtr windowName);

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
