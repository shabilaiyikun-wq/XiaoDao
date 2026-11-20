using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WinIsland
{
    /* 一次采样拿到的媒体快照。字段全公开，UI 直接读。 */
    class NowPlaying
    {
        public bool Has;
        public string App = "";
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public string Status = "Closed";
        public double Position;
        public double Duration;
        public bool CanNext;
        public bool CanPrev;
        public bool CanSeek;
        public bool CanPlayPause;

        public bool Playing { get { return Status == "Playing"; } }

        public string PrettyAppName { get { return Media.PrettyApp(App); } }
    }

    /* SMTC 访问全部在一个独立的 MTA 工作线程上完成，UI 线程只读发布出来的快照。

       为什么必须这样：IAsyncOperation 的完成回调要派发回调用方的线程上下文。
       如果在 WPF 的 STA/UI 线程上 Thread.Sleep 自旋等它，那个线程就被堵住了，
       回调永远进不来 -> Status 一直 Started -> 超时返回 null -> 下一轮再堵。
       实测 QQ 音乐正是这样把岛卡死的（同一套调用在 MTA 线程上 65ms 全跑完）。
       bilibili 之前没炸，只是因为它的属性已经同步完成，压根没进等待分支。

       顺带解决了另一个问题：封面读取（OpenReadAsync + DataReader）也不用再
       在布局构建时同步跑，UI 只拿已经解好的字节数组。 */
    static class Media
    {
        const int CmdNone = 0, CmdPlayPause = 1, CmdNext = 2, CmdPrev = 3, CmdSeek = 4;
        const int PollMs = 400;
        const int CallCapMs = 800;      /* 单次调用上限；卡住就放弃这一轮 */

        static Thread _worker;
        static volatile bool _started;

        /* 发布区：工作线程写，UI 线程读。都是不可变对象，整体换引用即可。 */
        static volatile NowPlaying _snap = new NowPlaying();
        static volatile byte[] _art;

        static int _cmd = CmdNone;
        static volatile int _polls;
        static volatile string _lastError = "";
        static int _fails;

        /* 命令队列。单槽 Interlocked 会丢命令（连点两下只生效一次），
           而且命令得记住「用户当时看的是哪个播放器」—— 被打断之后
           Current() 很可能选到那个正在播的别的应用，toggle 就打偏了。 */
        class Cmd
        {
            public int Kind;
            public string App;
            public double Seek;
        }

        static readonly Queue<Cmd> _cmds = new Queue<Cmd>();

        /* 工作线程侧的行为轨迹：命令打给了谁、结果如何。
           UI 线程每轮取走写进日志 —— 以前命令失败是完全静默的，
           「点了没反应」连个痕迹都查不到。 */
        static readonly Queue<string> _trace = new Queue<string>();

        static void T(string s)
        {
            lock (_trace)
            {
                _trace.Enqueue(s);
                while (_trace.Count > 40) _trace.Dequeue();
            }
        }

        public static string[] DrainTrace()
        {
            lock (_trace)
            {
                if (_trace.Count == 0) return null;
                string[] a = _trace.ToArray();
                _trace.Clear();
                return a;
            }
        }

        static void Enqueue(int kind, double seek)
        {
            NowPlaying np = _snap;
            Cmd c = new Cmd();
            c.Kind = kind;
            c.App = np == null ? "" : np.App;
            c.Seek = seek;
            lock (_cmds) _cmds.Enqueue(c);
        }

        public static void Start()
        {
            if (_started) return;
            _started = true;
            _worker = new Thread(Loop);
            _worker.IsBackground = true;
            _worker.Name = "island-media";
            _worker.SetApartmentState(ApartmentState.MTA);
            _worker.Start();
        }

        /* UI 线程调用：只读引用，不做任何 COM */
        public static NowPlaying Snapshot() { return _snap; }

        public static byte[] CachedArt() { return _art; }

        public static string LastError() { return _lastError; }

        /* 等第一轮采样出来（自检/启动时用），不阻塞太久 */
        public static NowPlaying WaitFirst(int ms)
        {
            Start();
            int waited = 0;
            while (_polls < 1 && waited < ms) { Thread.Sleep(30); waited += 30; }
            return _snap;
        }

        /* UI 线程调用：投递命令，立刻返回，不等待结果 */
        public static void RequestPlayPause() { Enqueue(CmdPlayPause, -1); }
        public static void RequestNext() { Enqueue(CmdNext, -1); }
        public static void RequestPrev() { Enqueue(CmdPrev, -1); }
        public static void RequestSeek(double seconds) { Enqueue(CmdSeek, seconds); }

        /* ------------------------------------------------------------ 工作线程 */

        static void Loop()
        {
            while (true)
            {
                try
                {
                    int cmd = Interlocked.Exchange(ref _cmd, CmdNone);
                    if (cmd != CmdNone) ExecLegacy(cmd);

                    /* 队列里的命令一条都不许丢 */
                    Cmd c = null;
                    lock (_cmds) { if (_cmds.Count > 0) c = _cmds.Dequeue(); }
                    if (c != null) Exec(c);

                    /* 只取一次会话：ReadCore 和 RefreshArt 共用同一个 s。
                       之前各调一次 Current()，多会话时可能前后选到不同的播放器，
                       封面就会跟曲名对不上。 */
                    GlobalSystemMediaTransportControlsSession s = Current();
                    NowPlaying np = ReadCore(s);
                    _snap = np;
                    RefreshArt(s, np);
                    EnsureLyric(np);

                    _fails = 0;
                    _lastError = "";
                }
                catch (Exception ex)
                {
                    _fails++;
                    _lastError = ex.GetType().Name + ": " + ex.Message;
                    /* 连续失败就退避，别把工作线程也打成忙循环 */
                    if (_fails > 3) Thread.Sleep(PollMs * 3);
                }
                _polls++;
                Thread.Sleep(PollMs);
            }
        }

        static T Wait<T>(IAsyncOperation<T> op)
        {
            if (op == null) return default(T);
            int waited = 0;
            while (op.Status == AsyncStatus.Started && waited < CallCapMs)
            {
                Thread.Sleep(2);
                waited += 2;
            }
            if (op.Status != AsyncStatus.Completed) return default(T);
            return op.GetResults();
        }

        static GlobalSystemMediaTransportControlsSessionManager _mgr;

        /* 兼容旧调用点：没有队列版本时按「当前会话」执行 */
        static void ExecLegacy(int cmd)
        {
            Cmd c = new Cmd();
            c.Kind = cmd;
            c.App = "";
            c.Seek = -1;
            Exec(c);
        }

        static string CmdName(int k)
        {
            switch (k)
            {
                case CmdPlayPause: return "play";
                case CmdNext: return "next";
                case CmdPrev: return "prev";
                case CmdSeek: return "seek";
            }
            return "?";
        }

        /* 按 AppId 找回用户当时看的那个会话 */
        static GlobalSystemMediaTransportControlsSession Find(string app)
        {
            if (app == null || app.Length == 0) return null;
            GlobalSystemMediaTransportControlsSessionManager m = Manager();
            if (m == null) return null;

            IReadOnlyList<GlobalSystemMediaTransportControlsSession> all;
            try { all = m.GetSessions(); }
            catch { return null; }
            if (all == null) return null;

            for (int i = 0; i < all.Count; i++)
            {
                try
                {
                    if (string.Equals(all[i].SourceAppUserModelId, app,
                                      StringComparison.OrdinalIgnoreCase))
                        return all[i];
                }
                catch { }
            }
            return null;
        }

        static void Exec(Cmd c)
        {
            /* 优先打给用户当时看的那个会话；它已经退出了才退回 Current() */
            GlobalSystemMediaTransportControlsSession s = Find(c.App);
            bool targeted = s != null;
            if (s == null) s = Current();
            if (s == null) { T("cmd " + CmdName(c.Kind) + " -> no session"); return; }

            string who;
            try { who = PrettyApp(s.SourceAppUserModelId); }
            catch { who = "?"; }

            switch (c.Kind)
            {
                case CmdPlayPause: PlayPause(s, who, targeted); return;
                case CmdNext:
                    T("cmd next -> " + who + " ok=" + Wait(s.TrySkipNextAsync()));
                    return;
                case CmdPrev:
                    T("cmd prev -> " + who + " ok=" + Wait(s.TrySkipPreviousAsync()));
                    return;
                case CmdSeek:
                    if (c.Seek >= 0)
                        T("cmd seek -> " + who + " " + Math.Round(c.Seek, 1) + "s ok="
                          + Wait(s.TryChangePlaybackPositionAsync((long)(c.Seek * 10000000.0))));
                    return;
            }
        }

        /* 用「目标状态」而不是 toggle。

           toggle 在被打断之后很容易回 false（播放器自己还占着状态机），
           而明确的 Play/Pause 单调用往往还能通。原来只调 toggle 并且把返回值丢了，
           表现就是「点了没反应」而且日志里什么都没有 —— 这种静默失败最难查。

           还要处理更讨厌的一种：调用回 True，但播放器其实没动（被打断后常见）。
           所以下发之后要核对状态，没变就换一条路再推一次，最后把结果如实记下来。
           顺带把三个控制位一起记下来：谁能用、谁不能用，一眼就看得见。 */
        static void PlayPause(GlobalSystemMediaTransportControlsSession s, string who, bool targeted)
        {
            bool playing = false;
            bool canToggle = true, canPlay = true, canPause = true;
            try
            {
                GlobalSystemMediaTransportControlsSessionPlaybackInfo info = s.GetPlaybackInfo();
                playing = info.PlaybackStatus
                          == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (info.Controls != null)
                {
                    canToggle = info.Controls.IsPlayPauseToggleEnabled;
                    canPlay = info.Controls.IsPlayEnabled;
                    canPause = info.Controls.IsPauseEnabled;
                }
            }
            catch { }

            bool want = !playing;             /* 想让它变成的状态 */
            string head = "cmd play -> " + who + (targeted ? "" : "(fallback)")
                          + " was=" + (playing ? "Playing" : "Paused")
                          + " ctl[toggle=" + canToggle + " play=" + canPlay
                          + " pause=" + canPause + "]";

            /* 第一条路：明确的 play / pause */
            bool ok = playing ? Wait(s.TryPauseAsync()) : Wait(s.TryPlayAsync());
            string via = playing ? "pause" : "play";

            if (Reached(s, want)) { T(head + " " + via + "=True"); return; }
            if (!ok) T(head + " " + via + "=False -> retry");

            /* 第二条路：toggle（有些播放器只实现这个） */
            bool t = Wait(s.TryTogglePlayPauseAsync());
            if (Reached(s, want)) { T(head + " " + via + " -> toggle=True"); return; }

            /* 第三条路：再来一次明确调用。被打断的播放器有时要第二下才缓过来 */
            bool ok2 = playing ? Wait(s.TryPauseAsync()) : Wait(s.TryPlayAsync());
            bool done = Reached(s, want);
            T(head + " " + via + "=False toggle=" + t + " retry=" + ok2
              + " -> " + (done ? "ok" : "STILL " + Status(s)));
        }

        static bool Reached(GlobalSystemMediaTransportControlsSession s, bool wantPlaying)
        {
            /* 给播放器一点反应时间：立刻读多半还是旧状态，会误判成失败 */
            for (int i = 0; i < 6; i++)
            {
                Thread.Sleep(120);
                try
                {
                    bool now = s.GetPlaybackInfo().PlaybackStatus
                               == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    if (now == wantPlaying) return true;
                }
                catch { return false; }
            }
            return false;
        }

        static string Status(GlobalSystemMediaTransportControlsSession s)
        {
            try { return s.GetPlaybackInfo().PlaybackStatus.ToString(); }
            catch { return "?"; }
        }

        static GlobalSystemMediaTransportControlsSessionManager Manager()
        {
            if (_mgr != null) return _mgr;
            try { _mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync()); }
            catch { _mgr = null; }
            return _mgr;
        }

        /* 正在播的优先；全都没在播时，选「时间轴最近更新过」的那个。
           不能直接取 all[0]：机器上同时挂着好几个暂停的会话时（比如 B 站 + QQ 音乐），
           列表顺序是注册顺序，不是「你最后在用的那个」。 */
        static GlobalSystemMediaTransportControlsSession Current()
        {
            GlobalSystemMediaTransportControlsSessionManager m = Manager();
            if (m == null) return null;
            IReadOnlyList<GlobalSystemMediaTransportControlsSession> all;
            try { all = m.GetSessions(); }
            catch { _mgr = null; return null; }
            if (all == null || all.Count == 0) return null;

            GlobalSystemMediaTransportControlsSession newest = null;
            DateTimeOffset newestAt = DateTimeOffset.MinValue;

            for (int i = 0; i < all.Count; i++)
            {
                GlobalSystemMediaTransportControlsSession s = all[i];
                try
                {
                    if (s.GetPlaybackInfo().PlaybackStatus
                        == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                        return s;

                    try
                    {
                        DateTimeOffset at = s.GetTimelineProperties().LastUpdatedTime;
                        if (newest == null || at > newestAt) { newestAt = at; newest = s; }
                    }
                    catch { if (newest == null) newest = s; }
                }
                catch { if (newest == null) newest = s; }
            }

            return newest != null ? newest : all[0];
        }

        static NowPlaying ReadCore(GlobalSystemMediaTransportControlsSession s)
        {
            NowPlaying np = new NowPlaying();
            _props = null;
            if (s == null) return np;

            np.Has = true;
            try { np.App = s.SourceAppUserModelId; }
            catch { np.App = ""; }

            try
            {
                GlobalSystemMediaTransportControlsSessionPlaybackInfo info = s.GetPlaybackInfo();
                np.Status = info.PlaybackStatus.ToString();
                if (info.Controls != null)
                {
                    np.CanNext = info.Controls.IsNextEnabled;
                    np.CanPrev = info.Controls.IsPreviousEnabled;
                    np.CanSeek = info.Controls.IsPlaybackPositionEnabled;
                    np.CanPlayPause = info.Controls.IsPlayPauseToggleEnabled;
                }
            }
            catch { }

            try
            {
                GlobalSystemMediaTransportControlsSessionTimelineProperties tl = s.GetTimelineProperties();
                np.Position = tl.Position.TotalSeconds;
                np.Duration = tl.EndTime.TotalSeconds;
            }
            catch { }

            try
            {
                /* 属性对象留给 RefreshArt 复用：封面就在同一个对象上，
                   不必为了拿缩略图再把播放器问一遍 */
                GlobalSystemMediaTransportControlsSessionMediaProperties p = Wait(s.TryGetMediaPropertiesAsync());
                _props = p;
                if (p != null)
                {
                    np.Title = p.Title == null ? "" : p.Title;
                    np.Artist = p.Artist == null ? "" : p.Artist;
                    np.Album = p.AlbumTitle == null ? "" : p.AlbumTitle;
                }
            }
            catch { }

            return np;
        }

        /* ------------------------------------------------------------------ 封面 */

        static string _artKey = "";
        static GlobalSystemMediaTransportControlsSessionMediaProperties _props;

        /* 封面版本号：换了封面就 +1。UI 每轮比一下这个数，变了就重画封面元素
           —— 否则卡片建好之后封面再也不会更新（切歌后仍显示上一首的图）。 */
        static int _artVersion;

        public static int ArtVersion() { return Volatile.Read(ref _artVersion); }

        /* 只在 (应用 + 专辑 + 曲名) 变了才去取字节，否则 400ms 一次会把播放器问烦。
           取失败就清空，不能留着上一首的封面冒充新歌。 */
        static void RefreshArt(GlobalSystemMediaTransportControlsSession s, NowPlaying np)
        {
            if (s == null || np == null || !np.Has)
            {
                if (_art != null || _artKey.Length > 0)
                {
                    _art = null;
                    _artKey = "";
                    Interlocked.Increment(ref _artVersion);
                }
                return;
            }

            string key = np.App + "\u0001" + np.Album + "\u0001" + np.Title;
            if (key == _artKey) return;
            _artKey = key;

            byte[] buf = null;
            try
            {
                GlobalSystemMediaTransportControlsSessionMediaProperties p = _props;
                if (p != null && p.Thumbnail != null)
                {
                    IRandomAccessStreamWithContentType rs = Wait(p.Thumbnail.OpenReadAsync());
                    if (rs != null && rs.Size > 0)
                    {
                        uint size = (uint)rs.Size;
                        if (size <= 4 * 1024 * 1024)
                        {
                            buf = new byte[size];
                            DataReader dr = new DataReader(rs);
                            Wait(dr.LoadAsync(size));
                            dr.ReadBytes(buf);
                            dr.Dispose();
                        }
                    }
                }
            }
            catch
            {
                buf = null;
            }

            /* 整块替换，不要先清空 —— 先清空会让界面闪一下占位渐变，
               贴图只有 60KB、读取 10ms 级，直接换掉更干净。 */
            _art = buf;
            Interlocked.Increment(ref _artVersion);
        }

        /* ---------------------------------------------------------------- 歌词 */

        /* 歌词另外起一条短线程去抓：HTTP 最长 6 秒，不能压在这条 400ms 的轮询上，
           否则曲名和进度会跟着卡住。抓完换引用，UI 侧只读。 */
        static volatile LyricDoc _lyric;
        static int _lyricVersion;
        static string _lyricKey = "";
        static string _lyricFetching = "";

        /* 上一次尝试的时间点。用来给「没查到」加一个过期时间：

           原来同一首歌一旦查过就永不重试，于是一次超时、一次 429 限流、
           或者开机时还没连上网，这首歌就永久停在「无同步歌词」上。
           lrclib 对未注册客户端约 90 次/分，而一首歌最多打 8 次请求，
           快速切歌很容易撞上限流 —— 撞上就再也不试，这不能接受。 */
        static int _lyricAttempt;
        const int LyricRetryMs = 10 * 60 * 1000;

        public static int LyricVersion() { return Volatile.Read(ref _lyricVersion); }

        public static int LyricLines()
        {
            LyricDoc d = _lyric;
            return d == null ? 0 : d.Count;
        }

        /* 0 = 还在查（或还没开始），1 = 有词，2 = 查过了确实没有。
           界面靠这个把「正在加载」和「没有歌词」区分开，
           而不是一律显示空白 —— 空白会让人以为是坏了。 */
        public static int LyricState()
        {
            LyricDoc d = _lyric;
            if (d != null && d.Count > 0) return 1;
            if (_lyricFetching.Length > 0) return 0;
            if (_lyricKey.Length > 0) return 2;
            return 0;
        }

        public static string LyricSource()
        {
            LyricDoc d = _lyric;
            return d == null ? "" : d.Source;
        }

        /* 按播放位置取当前行。UI 线程调用，纯计算，不碰 COM、不碰网络。 */
        public static string LyricLine(double pos)
        {
            return Lyric.LineAt(_lyric, pos);
        }

        static void EnsureLyric(NowPlaying np)
        {
            if (np == null || !np.Has)
            {
                if (_lyric != null || _lyricKey.Length > 0)
                {
                    _lyric = null;
                    _lyricKey = "";
                    Interlocked.Increment(ref _lyricVersion);
                }
                return;
            }

            string key = np.Title + "\u0001" + np.Artist;
            if (key == _lyricFetching) return;

            if (key == _lyricKey)
            {
                /* 有词就不必再查 */
                if (_lyric != null) return;

                /* 没查到过：可能是当时超时/限流，隔一段时间放一次重试。
                   注意这里用的是 TickCount 相减，溢出也照样正确。 */
                if (Environment.TickCount - _lyricAttempt < LyricRetryMs) return;
            }

            _lyricFetching = key;
            _lyricAttempt = Environment.TickCount;
            string t = np.Title;
            string a = np.Artist;
            double dur = np.Duration;

            Thread th = new Thread(delegate()
            {
                LyricDoc doc = null;
                try { doc = Lyric.Fetch(t, a, dur); }
                catch { doc = null; }

                _lyric = doc;               /* 抓不到就是 null，界面自动不显示 */
                _lyricKey = key;
                Interlocked.Increment(ref _lyricVersion);
                _lyricFetching = "";
            });
            th.IsBackground = true;
            th.Name = "island-lyric";
            th.Start();
        }

        /* ---------------------------------------------------------------- 命名 */

        /* 把 AUMID / 路径变成人能读的名字。
           Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic -> ZuneMusic
           com.bilibili.bilibiliPC                               -> bilibiliPC
           QQMusic.exe                                           -> QQMusic
           C:\Program Files\VideoLAN\VLC\vlc.exe                  -> vlc

           顺序很关键：必须先剥 .exe 再取「最后一段」。
           反过来会把 QQMusic.exe 当成点分 AUMID，切出个 "exe"（实测踩过）。 */
        public static string PrettyApp(string aumid)
        {
            if (aumid == null || aumid.Length == 0) return "媒体";

            string s = aumid;

            int bang = s.IndexOf('!');           /* 去掉 !App 后缀 */
            if (bang > 0) s = s.Substring(0, bang);

            int slash = s.LastIndexOf('\\');     /* 桌面程序：取文件名 */
            bool fromPath = slash >= 0;
            if (fromPath) s = s.Substring(slash + 1);

            if (s.Length > 4 && s.ToLower().EndsWith(".exe"))
                s = s.Substring(0, s.Length - 4);

            if (!fromPath)
            {
                int us = s.IndexOf('_');         /* 去掉包族哈希 */
                if (us > 0) s = s.Substring(0, us);
                int dot = s.LastIndexOf('.');    /* 打包应用：取最后一段 */
                if (dot > 0 && dot < s.Length - 1) s = s.Substring(dot + 1);
            }

            return s.Length == 0 ? "媒体" : s;
        }
    }
}