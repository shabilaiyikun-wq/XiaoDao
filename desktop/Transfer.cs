using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace WinIsland
{
    /* 中转站：拖进来的文件先存着，之后再拖出去。

       两条从打样延续下来的硬约束：

       1. **只存路径引用，不复制文件本体。** 所以「文件被占用 / 无权限」这类问题
          大半不存在——真正搬运字节的是资源管理器或目标应用，出错它自己会报。
          代价是源文件被移走/删除后引用就断了，靠启动校验标灰兜住。

       2. **所有磁盘 IO 都离开 UI 线程。** 离线 UNC 上 File.Exists 能卡几十秒，
          而这颗岛只有 468px 宽，卡一下就是整个桌面转圈。打样版里 SizeText
          直接对网络路径返回 "—" 绕过去，这里改成后台线程真去问。 */
    static class Transfer
    {
        /* 一条暂存项。Missing / Size 由校验线程写，UI 只读。 */
        class Item
        {
            public string Path;
            public bool Dir;
            public long Added;
            public bool Missing;
            public string Size = "";
        }

        /* 交给 Layout 的快照。重建时复制一份，UI 不用持锁。 */
        public class View
        {
            public string Path;
            public bool Dir;
            public bool Missing;
            public string Size;
        }

        const string FileName = "transfer.txt";
        const int Cap = 50;

        static readonly object _gate = new object();
        static List<Item> _items;
        static int _version;
        static bool _started;

        static readonly Dictionary<string, BitmapSource> _icons =
            new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);

        /* 拖动正悬停在岛上。

           为什么放在这里而不是 Snap 里：Layout 是静态类，只拿得到 Snap 和
           CardActions 两个入参，而这个标志要在 DragEnter 的同一帧就生效
           （等 400ms 的 Live 刷新会看到明显延迟）。 */
        public static bool Hover;

        /* 埋点出口。Transfer 是静态类，拿不到窗口的 Trace，
           由 IslandWindow 在构造时接上去（和 Layout.Diag 一个路子）。 */
        public static Action<string> Diag;

        static void Log(string s)
        {
            if (Diag != null) Diag(s);
        }

        public static int Version() { return Volatile.Read(ref _version); }

        public static int Count()
        {
            lock (_gate) { Ensure(); return _items.Count; }
        }

        public static int MissingCount()
        {
            lock (_gate)
            {
                Ensure();
                int n = 0;
                for (int i = 0; i < _items.Count; i++) if (_items[i].Missing) n++;
                return n;
            }
        }

        public static View[] Items()
        {
            lock (_gate)
            {
                Ensure();
                View[] a = new View[_items.Count];
                for (int i = 0; i < _items.Count; i++)
                {
                    Item it = _items[i];
                    View v = new View();
                    v.Path = it.Path;
                    v.Dir = it.Dir;
                    v.Missing = it.Missing;
                    v.Size = it.Size;
                    a[i] = v;
                }
                return a;
            }
        }

        /* ------------------------------------------------------------ 增删 */

        /// <summary>返回真正新增的条数（同路径去重之后）。新项排在最前面。</summary>
        public static int Add(string[] paths)
        {
            if (paths == null || paths.Length == 0) return 0;

            int added = 0;
            lock (_gate)
            {
                Ensure();

                /* 倒着插到 0 号位，整批的顺序才跟用户拖进来的一致 */
                for (int i = paths.Length - 1; i >= 0; i--)
                {
                    string p = paths[i];
                    if (p == null) continue;
                    p = p.Trim();
                    if (p.Length == 0) continue;

                    /* 规范化失败（超长 / 非法字符 / 保留名）就原样留着，
                       显示和拖出各自兜住，绝不在这里抛。 */
                    string full;
                    try { full = Path.GetFullPath(p); }
                    catch (Exception) { full = p; }

                    if (IndexOf(full) >= 0) continue;
                    if (_items.Count >= Cap) break;

                    Item it = new Item();
                    it.Path = full;
                    it.Dir = SafeIsDir(full);
                    it.Added = DateTime.Now.Ticks;

                    /* 刚拖进来的东西几乎肯定还在，就地快速校验一次；
                       网络路径不在这里碰磁盘（UI 线程），交给校验线程。 */
                    if (!full.StartsWith("\\\\"))
                    {
                        it.Missing = !Exists(full, it.Dir);
                        it.Size = SizeText(full, it.Dir);
                    }

                    _items.Insert(0, it);
                    added++;
                }

                if (added > 0)
                {
                    Save();
                    Interlocked.Increment(ref _version);
                }
            }
            Log("transfer add in=" + paths.Length + " added=" + added + " total=" + Count());
            return added;
        }

        public static void Remove(string path)
        {
            lock (_gate)
            {
                Ensure();
                int i = IndexOf(path);
                if (i < 0) return;
                _items.RemoveAt(i);
                Save();
                Interlocked.Increment(ref _version);
            }
        }

        /// <summary>只清掉已经丢失的那些，正常项留着。</summary>
        public static int ClearMissing()
        {
            lock (_gate)
            {
                Ensure();
                int n = 0;
                for (int i = _items.Count - 1; i >= 0; i--)
                {
                    if (!_items[i].Missing) continue;
                    _items.RemoveAt(i);
                    n++;
                }
                if (n > 0)
                {
                    Save();
                    Interlocked.Increment(ref _version);
                }
                return n;
            }
        }

        public static void Clear()
        {
            lock (_gate)
            {
                Ensure();
                if (_items.Count == 0) return;
                _items.Clear();
                Save();
                Interlocked.Increment(ref _version);
            }
        }

        /* ------------------------------------------------------------ 校验 */

        /* 启动时把已有引用过一遍：源文件被删/被移走的标成丢失。

           跑在后台线程上，因为它会碰到网络路径。校验完把版本号加一，
           UI 下一次 Live 刷新就自己重建列表。 */
        public static void Start()
        {
            lock (_gate)
            {
                if (_started) return;
                _started = true;
                Ensure();
            }

            Thread t = new Thread(ValidateLoop);
            t.IsBackground = true;
            t.Name = "island-transfer";
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        static void ValidateLoop()
        {
            List<Item> snapshot;
            lock (_gate)
            {
                Ensure();
                snapshot = new List<Item>(_items);
            }

            bool changed = false;
            int missing = 0;
            for (int i = 0; i < snapshot.Count; i++)
            {
                Item it = snapshot[i];
                bool ok = Exists(it.Path, it.Dir);
                if (it.Missing == ok) { it.Missing = !ok; changed = true; }

                string sz = SizeText(it.Path, it.Dir);
                if (sz != it.Size) { it.Size = sz; changed = true; }
                if (it.Missing) missing++;
            }

            Log("transfer validated n=" + snapshot.Count + " missing=" + missing
                + " changed=" + changed);

            if (changed) Interlocked.Increment(ref _version);
        }

        static bool Exists(string path, bool dir)
        {
            try
            {
                if (path.Length == 0) return false;
                return dir ? Directory.Exists(path) : File.Exists(path);
            }
            catch (Exception) { return false; }
        }

        static bool SafeIsDir(string path)
        {
            try
            {
                if (path.StartsWith("\\\\")) return false;   /* 网络路径交给校验线程 */
                return Directory.Exists(path);
            }
            catch (Exception) { return false; }
        }

        /* ------------------------------------------------------------ 显示 */

        public static string Name(string path)
        {
            string n = "";
            try { n = Path.GetFileName(path); }
            catch (Exception) { n = ""; }
            if (n == null || n.Length == 0) n = path;
            return n;
        }

        public static string SizeText(string path, bool dir)
        {
            try
            {
                if (path.Length == 0) return "—";
                if (dir) return "文件夹";          /* 不递归算：网络/大目录会卡死 */
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists) return "—";
                return Human(fi.Length);
            }
            catch (Exception) { return "—"; }
        }

        static string Human(long n)
        {
            if (n < 0) return "—";
            if (n < 1024) return n + " B";
            double k = n / 1024.0;
            if (k < 1024) return Math.Round(k, k < 10 ? 1 : 0) + " KB";
            double m = k / 1024.0;
            if (m < 1024) return Math.Round(m, m < 10 ? 1 : 0) + " MB";
            return Math.Round(m / 1024.0, 1) + " GB";
        }

        /* ------------------------------------------------------------ 图标 */

        /* 系统图标，按扩展名缓存。

           故意用 SHGFI_USEFILEATTRIBUTES：它只按扩展名查注册表，**完全不碰磁盘**。
           换成真实路径查询能给 .exe / .lnk 拿到各自的图标，但代价是每行都可能
           触发一次 IO，网络路径上能把 UI 拖住——为了几行列表不值得。
           代价说清楚：所有 .exe 共用同一个图标，没有缩略图。 */
        public static BitmapSource Icon(string path, bool dir)
        {
            string key = dir ? "<dir>" : ExtOf(path);
            lock (_icons)
            {
                BitmapSource hit;
                if (_icons.TryGetValue(key, out hit)) return hit;

                BitmapSource bs = LoadShellIcon(path, dir);
                _icons[key] = bs;          /* null 也缓存，免得每次重建都重试 */
                return bs;
            }
        }

        static string ExtOf(string path)
        {
            try
            {
                string e = Path.GetExtension(path);
                return e == null || e.Length == 0 ? "<none>" : e.ToLower();
            }
            catch (Exception) { return "<none>"; }
        }

        const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const uint SHGFI_ICON = 0x000000100;
        const uint SHGFI_SMALLICON = 0x000000001;
        const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
                                           ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        static BitmapSource LoadShellIcon(string path, bool dir)
        {
            SHFILEINFO info = new SHFILEINFO();
            uint attr = dir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
            uint flags = SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES;

            IntPtr ok = IntPtr.Zero;
            try
            {
                ok = SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);
            }
            catch (Exception) { return null; }

            if (ok == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                /* CreateBitmapSourceFromHIcon 是惰性的 —— 不 Freeze 就 DestroyIcon，
                   拿到的是个指向已释放图标的空壳。 */
                bs.Freeze();
                return bs;
            }
            catch (Exception) { return null; }
            finally { DestroyIcon(info.hIcon); }
        }

        /* ------------------------------------------------------------ 落盘 */

        /* 一行一条：base64(路径) \t 目录标记 \t 加入时间

           用 AppDir.Encode 而不是自己拼转义：路径里合法地可以出现制表符和换行，
           手写转义迟早漏一个，base64 一次解决。 */
        static void Ensure()
        {
            if (_items != null) return;

            _items = new List<Item>();
            string[] lines = AppDir.ReadLines(FileName);
            for (int i = 0; i < lines.Length; i++)
            {
                string ln = lines[i];
                if (ln.Length == 0) continue;
                if (ln == "v1") continue;

                string[] parts = ln.Split('\t');
                if (parts.Length < 1) continue;

                string p = AppDir.Decode(parts[0]);
                if (p.Length == 0) continue;

                Item it = new Item();
                it.Path = p;
                it.Dir = parts.Length > 1 && parts[1] == "d";
                long added = 0;
                if (parts.Length > 2) long.TryParse(parts[2], out added);
                it.Added = added;

                if (IndexOf(p) >= 0) continue;
                _items.Add(it);
                if (_items.Count >= Cap) break;
            }
        }

        static void Save()
        {
            List<string> lines = new List<string>();
            lines.Add("v1");
            for (int i = 0; i < _items.Count; i++)
            {
                Item it = _items[i];
                lines.Add(AppDir.Encode(it.Path) + "\t" + (it.Dir ? "d" : "f")
                          + "\t" + it.Added);
            }
            AppDir.WriteLines(FileName, lines);
        }

        static int IndexOf(string full)
        {
            for (int i = 0; i < _items.Count; i++)
            {
                if (Same(_items[i].Path, full)) return i;
            }
            return -1;
        }

        static bool Same(string a, string b)
        {
            return string.Equals(TrimEnd(a), TrimEnd(b), StringComparison.OrdinalIgnoreCase);
        }

        static string TrimEnd(string s)
        {
            if (s.Length > 3 && (s[s.Length - 1] == '\\' || s[s.Length - 1] == '/'))
                return s.Substring(0, s.Length - 1);
            return s;
        }
    }
}