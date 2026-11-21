using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WinIsland
{
    /* 会话偏好：上次用的场景 / 外观 / 素材。

       为什么必须落盘：场景切换现在做成高频操作了，一键一个来回，
       但如果切完重启回到 media，那些切换全白费。所以这三项跟着 ui.txt 走。

       为什么不复用窗口位置文件：window.txt 是「拖动」写的，每拖一次写一次，
       混进来会让位置和偏好互相踩。 */

    static class Prefs
    {
        /* 自检会遍历所有场景和外观，那不是在替用户做选择，不能覆盖用户偏好 */
        public static bool Enabled = true;

        public static string Scene = "";
        public static string Look = "";
        public static string Art = "";

        /* 允许被截图 / 录屏捕捉。默认开 —— 岛是桌面上的一件东西，
           别人截屏、录屏、开会共享屏幕时理应拍得到它。
           默认开还有一个更实际的原因：默认值选「拍不到」，用户第一次发现
           截图里没有岛时根本不知道有这个开关。

           关掉 = WDA_EXCLUDEFROMCAPTURE，岛在所有抓屏里消失（含 OBS、录屏、
           Win+Shift+S）。开着的时候实时桌面采样会拍到自己（回授），
           所以开启时折射层退回启动瞬间那张整屏快照（见 IslandWindow）。 */
        public static bool Capture = true;

        /* 中转站：从岛上拖出去之后，要不要把这一项从暂存里移除。
           默认关 —— 拖出去多数只是「顺便用一下」，回头还想再用；
           真想清走的用行上的 ✕ 或右键菜单，都只有一下。 */
        public static bool TransferOut = false;

        /* 桌宠开关。默认开 —— 正常启动就该看得见桌宠，这是这一版的产品决定，
           不是「高级选项」。关掉记在 ui.txt 的 pet=0，下次启动不再自动拉起。

           注意它只表示「用户想不想要桌宠」。某一次启动到底有没有桌宠，
           还要看这一次是不是自检/抓帧模式（见 Program.cs 的 diagnostic）——
           那些模式一律不自动拉桌宠，也绝不回写这一项。 */
        public static bool Pet = true;

        /* 天气城市。空 = 按网络出口自动定位。
           填了就用填的 —— 这台机器挂着代理，IP 出口在美国，自动定位会给出加州。 */
        public static string WeatherCity = "";

        public static void Load()
        {
            string[] lines = AppDir.ReadLines("ui.txt");
            for (int i = 0; i < lines.Length; i++)
            {
                int eq = lines[i].IndexOf('=');
                if (eq <= 0) continue;
                string k = lines[i].Substring(0, eq).Trim();
                string v = lines[i].Substring(eq + 1).Trim();
                if (v.Length == 0) continue;

                if (k == "scene") Scene = v;
                else if (k == "look") Look = v;
                else if (k == "art") Art = v;
                else if (k == "capture") Capture = (v != "0");
                else if (k == "transferOut") TransferOut = (v != "0");
                else if (k == "pet") Pet = (v != "0");
                else if (k == "weatherCity") WeatherCity = AppDir.Decode(v);
            }
        }

        public static void Save(string scene, string look, string art)
        {
            if (!Enabled) return;
            Scene = scene; Look = look; Art = art;

            List<string> lines = new List<string>();
            lines.Add("scene=" + scene);
            lines.Add("look=" + look);
            lines.Add("art=" + art);
            /* 写全量：这个函数是整体重写 ui.txt，漏一项就等于把那一项清掉 */
            lines.Add("capture=" + (Capture ? "1" : "0"));
            lines.Add("transferOut=" + (TransferOut ? "1" : "0"));
            lines.Add("pet=" + (Pet ? "1" : "0"));
            /* 城市可能是中文，走 base64；空值也照写一行（去城市 = 自动定位） */
            lines.Add("weatherCity=" + AppDir.Encode(WeatherCity == null ? "" : WeatherCity));
            AppDir.WriteLines("ui.txt", lines);
        }

        /// <summary>
        /// 只改了一个偏好（比如天气城市）之后把它落盘。
        /// 场景/外观/取材这三项由 Save 管，这里原样带上当前值，不覆盖任何人。
        /// </summary>
        public static void Flush()
        {
            Save(Scene, Look, Art);
        }
    }

    /* 本地数据目录。%APPDATA%\WinIsland —— 和窗口位置文件放在一起。 */
    static class AppDir
    {
        /* 数据目录可以被顶掉。用途只有一个：自检要写待办、剪贴板、中转站这些
           **有副作用**的 store，如果直接写 %APPDATA%\WinIsland，跑一次自检就会
           往用户的真实待办里塞一条「自检：15 分钟后到点」，还会把列表第 0 条
           ——也就是用户最老的那一条——勾成已完成（Todos.Toggle(0)）。
           所以自检必须先 SetRoot 到一个临时目录（见 Program.cs）。
           正常启动永远不调它，Root 仍是 %APPDATA%\WinIsland。 */
        static string _override;

        public static void SetRoot(string dir) { _override = dir; }

        public static string Root
        {
            get
            {
                string d = _override;
                if (string.IsNullOrEmpty(d))
                    d = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinIsland");
                try { if (!Directory.Exists(d)) Directory.CreateDirectory(d); }
                catch { }
                return d;
            }
        }

        public static string FilePath(string name) { return Path.Combine(Root, name); }

        /* 一行一条，内容 base64：剪贴板里什么字符都有，换行会把行格式拆坏 */
        public static string Encode(string s)
        {
            try { return Convert.ToBase64String(Encoding.UTF8.GetBytes(s)); }
            catch { return ""; }
        }

        public static string Decode(string s)
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
            catch { return ""; }
        }

        public static string[] ReadLines(string name)
        {
            try
            {
                string p = FilePath(name);
                if (!File.Exists(p)) return new string[0];
                return File.ReadAllLines(p, Encoding.UTF8);
            }
            catch { return new string[0]; }
        }

        public static void WriteLines(string name, List<string> lines)
        {
            try { File.WriteAllLines(FilePath(name), lines.ToArray(), Encoding.UTF8); }
            catch { }
        }
    }

    /* ------------------------------------------------------------- 剪贴板历史

       用 GetClipboardSequenceNumber 轮询，而不是 AddClipboardFormatListener + 窗口消息：
       后者要求有一个真实 HWND，还要在 UI 线程收 WM_CLIPBOARDUPDATE，一忙就漏；
       前者是纯计数器，任何线程都能读，300ms 一次的开销可以忽略。

       读写都用裸 Win32：WPF 的 Clipboard 类要求 STA，而这里跑在后台线程。 */
    static class Clips
    {
        public const int Cap = 30;

        const uint CF_UNICODETEXT = 13;
        const uint GMEM_MOVEABLE = 0x0002;

        [DllImport("user32.dll")]
        static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")]
        static extern bool OpenClipboard(IntPtr owner);
        [DllImport("user32.dll")]
        static extern bool CloseClipboard();
        [DllImport("user32.dll")]
        static extern bool EmptyClipboard();
        [DllImport("user32.dll")]
        static extern IntPtr GetClipboardData(uint fmt);
        [DllImport("user32.dll")]
        static extern IntPtr SetClipboardData(uint fmt, IntPtr mem);
        [DllImport("user32.dll")]
        static extern bool IsClipboardFormatAvailable(uint fmt);
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalLock(IntPtr h);
        [DllImport("kernel32.dll")]
        static extern bool GlobalUnlock(IntPtr h);
        [DllImport("kernel32.dll")]
        static extern IntPtr GlobalFree(IntPtr h);

        static readonly object _lock = new object();
        static List<string> _items = null;
        static int _version;
        static uint _skipSeq;
        static bool _started;

        public static int Version() { return Volatile.Read(ref _version); }

        public static string[] Items()
        {
            lock (_lock)
            {
                Ensure();
                return _items.ToArray();
            }
        }

        public static int Count()
        {
            lock (_lock)
            {
                Ensure();
                return _items.Count;
            }
        }

        static void Ensure()
        {
            if (_items != null) return;
            _items = new List<string>();
            string[] lines = AppDir.ReadLines("clips.txt");
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                string t = AppDir.Decode(lines[i]);
                if (t.Length > 0) _items.Add(t);
                if (_items.Count >= Cap) break;
            }
        }

        static void Save()
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < _items.Count; i++) lines.Add(AppDir.Encode(_items[i]));
            AppDir.WriteLines("clips.txt", lines);
        }

        public static void Start()
        {
            lock (_lock)
            {
                if (_started) return;
                _started = true;
                Ensure();
                _skipSeq = GetClipboardSequenceNumber();
            }

            Thread t = new Thread(Loop);
            t.IsBackground = true;
            t.Name = "island-clip";
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        static void Loop()
        {
            while (true)
            {
                Thread.Sleep(300);
                try
                {
                    uint seq = GetClipboardSequenceNumber();
                    if (seq == _skipSeq) continue;
                    _skipSeq = seq;

                    string text = Read();
                    if (text == null) continue;
                    text = text.Trim();
                    if (text.Length == 0) continue;
                    if (text.Length > 2000) text = text.Substring(0, 2000);

                    Add(text);
                }
                catch { }
            }
        }

        public static void Add(string text)
        {
            bool changed = false;
            lock (_lock)
            {
                Ensure();
                /* 已经存在就提到最前面，而不是留两条一样的 */
                for (int i = 0; i < _items.Count; i++)
                {
                    if (_items[i] == text) { _items.RemoveAt(i); break; }
                }
                _items.Insert(0, text);
                while (_items.Count > Cap) _items.RemoveAt(_items.Count - 1);
                Save();
                changed = true;
            }
            if (changed) Interlocked.Increment(ref _version);
        }

        public static void CopyAt(int index)
        {
            string text = null;
            lock (_lock)
            {
                Ensure();
                if (index >= 0 && index < _items.Count) text = _items[index];
            }
            if (text == null) return;
            Write(text);
        }

        public static void Clear()
        {
            lock (_lock)
            {
                Ensure();
                _items.Clear();
                Save();
            }
            Interlocked.Increment(ref _version);
        }

        /* 当前剪贴板里的文字（待办卡片「收下剪贴板」用） */
        public static string Read()
        {
            if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return null;
            if (!OpenClipboard(IntPtr.Zero)) return null;
            try
            {
                IntPtr h = GetClipboardData(CF_UNICODETEXT);
                if (h == IntPtr.Zero) return null;
                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { GlobalUnlock(h); }
            }
            catch { return null; }
            finally { CloseClipboard(); }
        }

        public static bool Write(string text)
        {
            if (text == null) return false;
            if (!OpenClipboard(IntPtr.Zero)) return false;
            try
            {
                if (!EmptyClipboard()) return false;

                int bytes = (text.Length + 1) * 2;
                IntPtr h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)(uint)bytes);
                if (h == IntPtr.Zero) return false;

                IntPtr p = GlobalLock(h);
                if (p == IntPtr.Zero) { GlobalFree(h); return false; }
                char[] cs = text.ToCharArray();
                Marshal.Copy(cs, 0, p, cs.Length);
                Marshal.WriteInt16(p, cs.Length * 2, 0);
                GlobalUnlock(h);

                if (SetClipboardData(CF_UNICODETEXT, h) == IntPtr.Zero)
                {
                    GlobalFree(h);
                    return false;
                }
                /* 系统接管了这块内存，不能自己释放 */

                _skipSeq = GetClipboardSequenceNumber();
                return true;
            }
            catch { return false; }
            finally { CloseClipboard(); }
        }
    }

    /* ------------------------------------------------------------- 待办

       岛没有键盘焦点（WS_EX_NOACTIVATE），所以不能弹输入框。
       新增待办的路径是「先把文字复制下来，再点卡片上的收下剪贴板」。 */
    class TodoItem
    {
        public string Text;
        public bool Done;
        /* 到点提醒的时刻；MinValue 表示不提醒 */
        public DateTime Due = DateTime.MinValue;
        /* 已经提醒过一次（只存在内存里，重启后到点的还会再提醒一次） */
        public bool Alerted;

        public bool HasDue { get { return Due != DateTime.MinValue; } }
        public bool IsDue { get { return HasDue && !Done && DateTime.Now >= Due; } }

        public string DueText
        {
            get
            {
                if (!HasDue) return "";
                DateTime d = Due;
                if (d.Date == DateTime.Now.Date) return d.ToString("HH:mm");
                if (d.Date == DateTime.Now.Date.AddDays(1)) return "明天 " + d.ToString("HH:mm");
                return d.ToString("M/d HH:mm");
            }
        }
    }

    static class Todos
    {
        static readonly object _lock = new object();
        static List<TodoItem> _items = null;
        static int _version;

        public static int Version() { return Volatile.Read(ref _version); }

        public static TodoItem[] Items()
        {
            lock (_lock)
            {
                Ensure();
                return _items.ToArray();
            }
        }

        static void Ensure()
        {
            if (_items != null) return;
            _items = new List<TodoItem>();
            string[] lines = AppDir.ReadLines("todos.txt");
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.Length < 3) continue;

                /* 格式：done|dueTicks|base64   （老文件只有 done|base64，dueTicks 缺省 0） */
                string[] parts = ln.Split(new char[] { '|' }, 3);
                TodoItem it = new TodoItem();
                it.Done = ln[0] == '1';

                if (parts.Length == 3)
                {
                    long ticks = 0;
                    long.TryParse(parts[1], out ticks);
                    if (ticks > 0) { try { it.Due = new DateTime(ticks); } catch { } }
                    it.Text = AppDir.Decode(parts[2]);
                }
                else if (parts.Length == 2)
                {
                    it.Text = AppDir.Decode(parts[1]);
                }

                if (it.Text != null && it.Text.Length > 0) _items.Add(it);
            }
        }

        static void Save()
        {
            List<string> lines = new List<string>();
            for (int i = 0; i < _items.Count; i++)
            {
                TodoItem it = _items[i];
                lines.Add((it.Done ? "1" : "0") + "|"
                          + (it.HasDue ? it.Due.Ticks.ToString() : "0") + "|"
                          + AppDir.Encode(it.Text));
            }
            AppDir.WriteLines("todos.txt", lines);
        }

        public static void Start()
        {
            lock (_lock) { Ensure(); }
        }

        /* dueMinutes > 0 表示「多少分钟后到点」 */
        public static void Add(string text, int dueMinutes)
        {
            AddDue(text, dueMinutes > 0 ? DateTime.Now.AddMinutes(dueMinutes) : DateTime.MinValue);
        }

        /* 自检专用：直接指定到点时刻。
           生产路径只有 Add(text, minutes)，而「到点提醒」这条要验就得有一条
           已经到点的待办 —— 用 Add(1) 然后等一分钟不叫验证，叫加班。 */
        public static void AddWithDue(string text, DateTime due)
        {
            AddDue(text, due);
        }

        static void AddDue(string text, DateTime due)
        {
            if (text == null) return;
            text = text.Trim();
            if (text.Length == 0) return;
            if (text.Length > 120) text = text.Substring(0, 120);

            lock (_lock)
            {
                Ensure();
                for (int i = 0; i < _items.Count; i++)
                    if (_items[i].Text == text) return;      /* 同样的别再记一遍 */

                TodoItem it = new TodoItem();
                it.Text = text;
                it.Done = false;
                it.Due = due;
                _items.Add(it);
                Save();
            }
            Interlocked.Increment(ref _version);
        }

        public static void Add(string text) { Add(text, 0); }

        public static void Toggle(int index)
        {
            lock (_lock)
            {
                Ensure();
                if (index < 0 || index >= _items.Count) return;
                _items[index].Done = !_items[index].Done;
                _items[index].Alerted = true;      /* 手动勾掉就不再提醒 */
                Save();
            }
            Interlocked.Increment(ref _version);
        }

        public static void ClearDone()
        {
            lock (_lock)
            {
                Ensure();
                for (int i = _items.Count - 1; i >= 0; i--)
                    if (_items[i].Done) _items.RemoveAt(i);
                Save();
            }
            Interlocked.Increment(ref _version);
        }

        /* 某一条现在是勾上还是没勾。
           给「勾完待办 → 桌宠庆祝」用：Toggle 是翻转，翻完得回头看方向 ——
           取消勾选不该庆祝。越界或还没加载就返回 false，不抛。 */
        public static bool IsDone(int index)
        {
            lock (_lock)
            {
                Ensure();
                if (index < 0 || index >= _items.Count) return false;
                return _items[index].Done;
            }
        }

        /* 取出「刚到这个点、还没提醒过」的那一条，并标记为已提醒。
           返回 null 表示这一轮没有需要提醒的。 */
        public static TodoItem TakeDue()
        {
            lock (_lock)
            {
                Ensure();
                for (int i = 0; i < _items.Count; i++)
                {
                    TodoItem it = _items[i];
                    if (it.Done || !it.HasDue || it.Alerted) continue;
                    if (DateTime.Now < it.Due) continue;
                    it.Alerted = true;
                    return it;
                }
                return null;
            }
        }

        /* 卡片上要优先显示到点的那条 */
        public static string DueBanner()
        {
            lock (_lock)
            {
                Ensure();
                for (int i = 0; i < _items.Count; i++)
                    if (_items[i].IsDue) return "到点：" + _items[i].Text;
                return "";
            }
        }

        public static string CountText()
        {
            lock (_lock)
            {
                Ensure();
                int done = 0;
                for (int i = 0; i < _items.Count; i++) if (_items[i].Done) done++;
                return done + " / " + _items.Count;
            }
        }
    }
}