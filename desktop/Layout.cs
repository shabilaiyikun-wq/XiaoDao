using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace WinIsland
{
    /// <summary>构建好的内容：根元素 + 一组「活更新」委托。</summary>
    class Built
    {
        public FrameworkElement Root;

        /* 每个元素自己知道怎么用新快照刷新自己，Layout 之外不需要懂版面结构。
           每秒跑一次，只改文字和进度，不重建卡片（重建会打断 Wave/Breathe 动画）。 */
        public List<Action<Snap>> Live = new List<Action<Snap>>();

        /* 这张脸被摘掉时要停下来的东西（自己挂帧回调的控件，比如待办塔）。
           以前只有 Live：清掉更新器就够了，因为没人持有帧回调。塔会持有，
           而 CompositionTarget.Rendering 是静态事件 —— 不摘就是「窗口关了
           进程还活着」和「一张摘下来的脸还在每帧算物理」。 */
        public List<Action> Stop = new List<Action>();

        /* 自检用的把手：版面把自己搭出来的关键控件挂在这儿，别的路径不读它。
           待办塔要有它才验得动 —— 「勾掉一张，上面那些掉下来」这件事发生在
           可视树和物理世界之间，只看数据层是看不见的。 */
        public object Extra;

        public void Refresh(Snap s)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                try { Live[i](s); }
                catch { }
            }
        }

        /// <summary>停掉这张脸上所有自己跑的东西。重复调用是安全的。</summary>
        public void Halt()
        {
            Live.Clear();
            for (int i = 0; i < Stop.Count; i++)
            {
                try { Stop[i](); }
                catch { }
            }
            Stop.Clear();
        }
    }

    /// <summary>各活动的具体版面。全部绝对定位——尺寸都是已知常量。</summary>
    static class Layout
    {
        public const double PadExp = 18;
        public const double PadComp = 9;

        /* 第一层那颗球的直径系数。岛内集成版要拿同一个数字去定桌宠球体的尺寸 ——
           两边各写一个 0.56，改一处忘一处就会差几像素，所以放在这里当唯一来源。 */
        public const double IdleOrbRatio = 0.56;

        /// <summary>第一层那颗球的直径（DIP）。52×30 → 16.8。</summary>
        public static double IdleOrbSize(double w, double h)
        {
            return Math.Min(w, h) * IdleOrbRatio;
        }

        /* 埋点出口。Layout 是静态类，拿不到窗口的 Trace，
           由 IslandWindow 在构造时接上去。点击到底有没有到达元素、
           有没有被 300ms 防抖吞掉，全靠这里留下的两行日志分辨。 */
        public static Action<string> Diag;

        public static Built Build(string scene, string state, double w, double h,
                                  Look look, Art art, Snap s, CardActions act, bool auto)
        {
            return Build(scene, state, w, h, look, art, s, act, auto, false);
        }

        /* petDocked：第一层的紫色球**不画**，位置留给桌宠。

           为什么由 Layout 决定而不是建完再从树上摘掉：那颗球是一个完整的
           Grid（球体 + 绕圈的高光），摘它得先把它从 canvas 里认出来。
           层状态一进来就知道「这次要不要留白」，是最干净的一刀。

           注意只影响 idle 这一层：第二/三层的版面里本来没有球。 */
        public static Built Build(string scene, string state, double w, double h,
                                  Look look, Art art, Snap s, CardActions act, bool auto,
                                  bool petDocked)
        {
            Built built = new Built();
            Canvas root = new Canvas();
            root.Width = w;
            root.Height = h;

            if (state == "idle")
            {
                BuildIdle(root, built, w, h, look, art, s, petDocked);
                built.Root = root;
                return built;
            }

            if (state == "compact")
            {
                if (auto) BuildPlaying(root, built, w, h, look, art, s);
                else BuildCompact(root, built, scene, w, h, look, art, s, act);
                built.Root = root;
                return built;
            }

            BuildExpanded(root, built, scene, w, h, look, art, s, act);
            built.Root = root;
            return built;
        }

        /* 会自己更新的封面。卡片只在层变化时构建一次，封面要是建完就不管，
           点下一首之后就会永远停在上一个封面。这里把它挂进 Live，
           跟着 Media.ArtVersion() 走：版本一变就重画。 */
        static FrameworkElement LiveCover(Built built, double size, double r, Art art)
        {
            Canvas host = new Canvas();
            host.Width = size;
            host.Height = size;
            Ui.At(host, Ui.CoverFrom(Media.CachedArt(), size, r, art), 0, 0);

            int ver = Media.ArtVersion();
            built.Live.Add(delegate(Snap n)
            {
                int v = Media.ArtVersion();
                if (v == ver) return;
                ver = v;
                Ui.Refresh(host, Ui.CoverFrom(Media.CachedArt(), size, r, art));
            });
            return host;
        }

        /* 第二层：有音乐在放，但鼠标没过来。不放任何文字——只有封面和律动。
           曲名/艺人/歌词等信息留到第三层展开时再看，这样这一层能一直很小、很安静。

           试过在这里塞一行歌词（并为此把宽度从 112 拉到 184），结论是：
           这一层一旦有字，它就从「一个状态指示器」变成了「一个缩水的歌词条」，
           既不如原来的安静，也不如展开态完整。收起时就该只是「在放歌」这件事本身。 */
        static void BuildPlaying(Canvas root, Built built, double w, double h,
                                 Look look, Art art, Snap s)
        {
            double cy = h / 2;
            double coverSize = 22;
            FrameworkElement cover = LiveCover(built, coverSize, 7, art);
            Ui.At(root, cover, 9, cy - coverSize / 2);

            double barsW = 58;
            double barsH = Math.Min(16, h - 18);
            FrameworkElement bars = Ui.Rhythm(art, look, s.Playing, barsW, barsH);
            Ui.At(root, bars, 9 + coverSize + 10, cy - barsH / 2);

            /* 播放/暂停只在切换时重建一次，不要每秒重建（动画会重启，看着像抽帧） */
            bool wasPlaying = s.Playing;
            built.Live.Add(delegate(Snap n)
            {
                if (n.Playing == wasPlaying) return;
                wasPlaying = n.Playing;
                Ui.Refresh(bars, Ui.Rhythm(art, look, n.Playing, barsW, barsH));
            });
        }

        /* 可点的元素统一打上 Tag="act"：IslandWindow 拖动窗口前会检查这个标记，
           否则按住按钮也会把窗口拖走。 */
        static void OnTap(FrameworkElement el, Action a)
        {
            OnTap(el, a, null);
        }

        static void OnTap(FrameworkElement el, Action a, string name)
        {
            /* 按压回弹也从这里挂：OnTap 是「明确可点的岛内控件」的唯一入口，
               于是「哪些东西有按压反馈」由这一处决定，不在十几个调用点各写一遍，
               也不可能漏掉某个按钮、或者反过来把整座岛、拖放过程和桌宠算进去。

               返回值不一定是 el 本身 —— Squish.Wrap 会给它套一层不变形的命中面，
               变形只发生在里层元素上。所以下面所有处理程序都必须挂在返回值上，
               挂在参数上就等于挂到了「不参与命中测试的那个元素」上，点击会静默失效。 */
            SquishTarget target = Squish.Wrap(el, name);
            FrameworkElement surface = target == null ? el : target.Surface;

            surface.Tag = "act";
            surface.Cursor = System.Windows.Input.Cursors.Hand;

            /* 按下/松开/离开三个指针事件由 Squish.Wrap 自己接（它要抓鼠标捕获），
               这里只负责「点了之后做什么」。按下反馈和下面的动作是同一个时刻，
               所以不会被 300ms 防抖吞掉 —— 那管的是「动作只认一次」，
               不是「这一次看起来不像被按过」。 */

            /* 同一个元素 300ms 内只认一次点击 —— 用时间防抖，不用 ClickCount。

               踩过的坑：先是在 MouseLeftButtonUp 上判断 ClickCount==1，无效
               （WPF 只在 Down 上维护连击计数）；改到 Down 上判断之后，
               双击确实只触发一次了，但 ClickCount 是**整个窗口共享、按时间算**的：
               你在 GetDoubleClickTime（默认 500ms）内点第二次，哪怕点的是另一个
               按钮，拿到的也是 2，被这行直接吞掉。番茄钟因此变成「能开始、
               不能暂停、不能重置」—— 开始是那一下的第一击，永远正常；
               之后的点击只要手快点就落在双击时限里，全被吃掉。

               时间防抖只关心「同一个元素、间隔多久」，既挡住了双击连开两个输入窗，
               也不会误伤旁边的另一个按钮。 */
            int lastMs = 0;
            surface.MouseLeftButtonDown += delegate(object s, System.Windows.Input.MouseButtonEventArgs ev)
            {
                if (ev.ChangedButton != System.Windows.Input.MouseButton.Left) return;

                /* 日志分三种情况，一眼能分清是哪一层出问题：
                   「tap xxx」      → 点击确实到了元素，逻辑层有问题
                   「tap xxx muted」→ 被 300ms 防抖吃掉（同一元素点太快）
                   一条都没有      → 点击根本没到元素（命中/层叠/穿透有问题） */
                int now = Environment.TickCount;
                if (lastMs != 0 && now - lastMs < 300)
                {
                    if (Diag != null) Diag("tap " + (name == null ? "?" : name) + " muted "
                                           + (now - lastMs) + "ms");
                    return;
                }
                lastMs = now;
                if (Diag != null) Diag("tap " + (name == null ? "?" : name)
                                       + (a == null ? " (handler null!)" : ""));
                if (a != null) a();
            };
        }

        /* ------------------------------------------------------------- 紧凑态 */
        static void BuildCompact(Canvas root, Built built, string scene, double w, double h,
                                 Look look, Art art, Snap s, CardActions act)
        {
            double cy = h / 2;
            double x = PadComp;
            double textX;

            if (scene == "media")
            {
                FrameworkElement cover = LiveCover(built, 22, 7, art);
                Ui.At(root, cover, x, cy - 11);
                textX = x + 22 + 8;
                TwoLine(root, built, textX, cy, s.MediaTitle, s.MediaSub, look, w,
                        delegate(Snap n) { return n.MediaTitle; },
                        delegate(Snap n) { return n.MediaSub; });

                FrameworkElement wave = Ui.Wave(art, look, s.Playing);
                Ui.At(root, wave, w - PadComp - 18, cy - 8);
                /* 紧凑态不接管点击：那时候整条胶囊都是拖拽把手，点一下要能拖走。 */
            }
            else if (scene == "timer")
            {
                FrameworkElement tile = Ui.Tile(Ui.GClock, 22, 7, look, art);
                Ui.At(root, tile, x, cy - 11);
                textX = x + 22 + 8;
                TwoLine(root, built, textX, cy, s.TimerText, "专注 · 番茄钟", look, w,
                        delegate(Snap n) { return n.TimerText; },
                        delegate(Snap n) { return "专注 · 番茄钟"; });

                TextBlock tail = Ui.Text(s.TimerPctText, 11, FontWeights.Medium, look.Ink3, true);
                tail.Width = 34;
                tail.TextAlignment = TextAlignment.Right;
                Ui.At(root, tail, w - PadComp - 34, cy - 7);
                built.Live.Add(delegate(Snap n) { tail.Text = n.TimerPctText; });
            }
            else if (scene == "clip")
            {
                FrameworkElement tile = Ui.Tile(Ui.GDoc, 22, 7, look, art);
                Ui.At(root, tile, x, cy - 11);
                textX = x + 22 + 8;
                TwoLine(root, built, textX, cy, Clips.Count() + " 条", "剪贴板历史", look, w,
                        delegate(Snap n) { return Clips.Count() + " 条"; },
                        delegate(Snap n) { return "剪贴板历史"; });
            }
            else if (scene == "todo")
            {
                FrameworkElement tile = Ui.Tile(Ui.GCalendar, 22, 7, look, art);
                Ui.At(root, tile, x, cy - 11);
                textX = x + 22 + 8;
                TwoLine(root, built, textX, cy, Todos.CountText(), "待办", look, w,
                        delegate(Snap n) { return Todos.CountText(); },
                        delegate(Snap n) { return "待办"; });
            }
            else
            {
                /* 兜底走待办：场景表里只剩 media / timer / clip / todo，
                   未知 key（比如旧配置里存的 volume）落到这里不会出空卡。 */
                FrameworkElement tile = Ui.Tile(Ui.GCalendar, 22, 7, look, art);
                Ui.At(root, tile, x, cy - 11);
                textX = x + 22 + 8;
                TwoLine(root, built, textX, cy, Todos.CountText(), "待办", look, w,
                        delegate(Snap n) { return Todos.CountText(); },
                        delegate(Snap n) { return "待办"; });
            }
        }

        /// <summary>紧凑态的两行文字：标题在基线附近略上，副标题略下。</summary>
        static void TwoLine(Canvas root, Built built, double x, double cy,
                            string title, string sub, Look look, double w,
                            Func<Snap, string> titleOf, Func<Snap, string> subOf)
        {
            double avail = w - x - PadComp - 52;
            if (avail < 40) avail = 40;

            TextBlock t = Ui.Text(title, 12, FontWeights.SemiBold, look.Ink1, false);
            t.Width = avail;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.At(root, t, x, cy - 12);

            TextBlock s = Ui.Text(sub, 10, FontWeights.Normal, look.Ink3, false);
            s.Width = avail;
            s.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.At(root, s, x, cy + 1);

            built.Live.Add(delegate(Snap n) { t.Text = titleOf(n); });
            built.Live.Add(delegate(Snap n) { s.Text = subOf(n); });
        }

        /* ------------------------------------------------------------- 展开态 */
        static void BuildExpanded(Canvas root, Built built, string scene, double w, double h,
                                  Look look, Art art, Snap s, CardActions act)
        {
            if (scene == "media") ExpandedMedia(root, built, w, h, look, art, s, act);
            else if (scene == "timer") ExpandedTimer(root, built, w, h, look, art, s, act);
            else if (scene == "clip") ExpandedClip(root, built, w, h, look, art, s, act);
            else if (scene == "transfer") ExpandedTransfer(root, built, w, h, look, art, s, act);
            else if (scene == "weather") ExpandedWeather(root, built, w, h, look, art, s, act);
            else ExpandedTodo(root, built, w, h, look, art, s, act);

            Pager(root, w, h, look, art, scene, s, act);
        }

        /* 卡片底部那一排指示点：4 个场景，点哪个跳哪个。

           为什么要有：滚轮解决「顺着走」，但要从剪贴板跳到媒体得滚三格；
           点一下直达。它同时是「现在在第几个、一共几个」的唯一可见提示。

           为什么是 20×20 的命中区：点本身只有 5~7px，直接点几乎瞄不准，
           命中区放大到 20 才能随手点中（和列表行同样的处理）。

           点有三种形态：
             实心大点   = 当前场景
             空心环     = 这个场景里有正在发生的事（在放歌 / 在计时 / 有待办到点）
             小灰点     = 其余 */
        static void Pager(Canvas root, double w, double h, Look look, Art art,
                          string scene, Snap s, CardActions act)
        {
            SceneDef[] all = Scenes.All;
            int n = all.Length;
            if (n <= 0) return;

            double cell = 20;
            double startX = Math.Round((w - n * cell) / 2);
            double centerY = h - 15;

            Color acc = Ui.AccInk(look, art);

            for (int i = 0; i < n; i++)
            {
                SceneDef d = all[i];
                bool cur = d.Key == scene;
                bool live = !cur && LiveHint(d.Key, s);

                Border hit = Row(cell, cell);
                FrameworkElement dot;
                if (cur)
                {
                    Ellipse e1 = new Ellipse();
                    e1.Width = 7; e1.Height = 7;
                    e1.Fill = Ui.Br(acc);
                    dot = e1;
                }
                else if (live)
                {
                    Ellipse e2 = new Ellipse();
                    e2.Width = 7; e2.Height = 7;
                    e2.Fill = Brushes.Transparent;
                    e2.Stroke = Ui.Br(Fade(acc, 0.85));
                    e2.StrokeThickness = 1.4;
                    dot = e2;
                }
                else
                {
                    Ellipse e3 = new Ellipse();
                    e3.Width = 5; e3.Height = 5;
                    e3.Fill = Ui.Br(Fade(look.Ink3, 0.40));
                    dot = e3;
                }
                dot.HorizontalAlignment = HorizontalAlignment.Center;
                dot.VerticalAlignment = VerticalAlignment.Center;
                hit.Child = dot;
                hit.Cursor = System.Windows.Input.Cursors.Hand;
                Ui.At(root, hit, startX + i * cell, centerY - cell / 2);

                string key = d.Key;
                OnTap(hit, delegate { if (act.Scene != null) act.Scene(key); }, "pager-" + key);
            }
        }

        /* 这个场景里有没有「正在发生」的事 —— 指示点用它打空心环 */
        static bool LiveHint(string key, Snap s)
        {
            if (key == "media") return s.MediaHas && s.Playing;
            if (key == "timer") return s.TimerRunning;
            if (key == "todo") return Todos.DueBanner().Length > 0;
            /* 天气没有「事件」，但数据过期是件该被看见的事：
               空心环在这里的意思是「这份数据旧了，值得点进去看一眼」。 */
            if (key == "weather") return s.WxHas && s.Wx.Stale;
            return false;
        }

        static Color Fade(Color c, double a)
        {
            double v = a < 0 ? 0 : (a > 1 ? 1 : a);
            return Color.FromArgb((byte)Math.Round(255 * v), c.R, c.G, c.B);
        }

        static void ExpandedMedia(Canvas root, Built built, double w, double h,
                                  Look look, Art art, Snap s, CardActions act)
        {
            double rightX = PadExp + 104 + 16;
            double rightW = w - PadExp - rightX;

            /* 有真封面就画真封面，没有就退回渐变块。
               在「去掉底部指示点那一行」的内容区里居中，否则卡片长高之后
               封面会被整体压下去，和右上那栏文字错位。 */
            double contentH = h - 20;
            FrameworkElement cover = LiveCover(built, 104, 17, art);
            Ui.At(root, cover, PadExp, (contentH - 104) / 2);

            /* 进度条回来了 —— 但只读，不能拖。

               上次删它是我的判断失误：`canSeek=False` 只表示「不能拖」，
               不代表读不到位置。实测 QQ 音乐给的是真位置（78.234/277.04）。
               播放器不给时间轴时（B 站 pos=0/0、duration=0），整块连时间码一起隐藏，
               不会留一条永远 0:00 的死条。 */
            double barW = rightW;
            FrameworkElement bar = Ui.Bar(s.MediaPct, barW, 5, look, art);
            Ui.At(root, bar, rightX, 104);

            TextBlock posL = Ui.Text(s.MediaPos, 12, FontWeights.Medium, look.Ink3, true);
            Ui.At(root, posL, rightX, 110);

            TextBlock durL = Ui.Text(s.MediaDur, 12, FontWeights.Medium, look.Ink3, true);
            durL.Width = rightW;
            durL.TextAlignment = TextAlignment.Right;
            Ui.At(root, durL, rightX, 110);

            built.Live.Add(delegate(Snap n)
            {
                bool on = n.MediaHas && n.Media.Duration > 0;
                Visibility v = on ? Visibility.Visible : Visibility.Collapsed;
                bar.Visibility = v;
                posL.Visibility = v;
                durL.Visibility = v;
                if (!on) return;
                Ui.Refresh(bar, Ui.Bar(n.MediaPct, barW, 5, look, art));
                posL.Text = n.MediaPos;
                durL.Text = n.MediaDur;
            });

            TextBlock title = Ui.Text(s.MediaTitle, 18, FontWeights.SemiBold, look.Ink1, false);
            title.Width = rightW;
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.At(root, title, rightX, 20);
            built.Live.Add(delegate(Snap n) { title.Text = n.MediaTitle; });

            TextBlock artist = Ui.Text(s.MediaSub, 15, FontWeights.Normal, look.Ink2, false);
            artist.Width = rightW;
            artist.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.At(root, artist, rightX, 48);
            built.Live.Add(delegate(Snap n) { artist.Text = n.MediaSub; });

            /* 歌词：作者栏下面一行，跟着播放位置走。
               没有歌词就整块不显示（留空，不动别的元素位置）。
               换行时淡入一下，不然一行字硬跳太生硬。 */
            TextBlock lyric = Ui.Text("", 14, FontWeights.Normal, look.Ink3, false);
            lyric.Width = rightW;
            lyric.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.At(root, lyric, rightX, 72);
            built.Live.Add(delegate(Snap n)
            {
                string want = Media.LyricLine(n.Media.Position);
                int st = Media.LyricState();

                /* 查不到就明说「无同步歌词」，别留一片空白让人以为坏了 */
                if (want.Length == 0)
                {
                    if (!n.MediaHas) want = "";
                    else if (st == 2) want = "无同步歌词";
                    else want = "正在找歌词…";
                }

                if (want == lyric.Text) return;

                lyric.Text = want;
                lyric.Visibility = want.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
                if (want.Length > 0)
                {
                    DoubleAnimation fade = new DoubleAnimation(0.25, 1.0,
                        new Duration(TimeSpan.FromMilliseconds(260)));
                    fade.EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut };
                    lyric.BeginAnimation(UIElement.OpacityProperty, fade);
                }
            });

            double by = 128;
            FrameworkElement prev = Ui.IconButton(Ui.GPrev, 32, look, look.Ink2);
            Ui.At(root, prev, rightX, by + 2);
            OnTap(prev, act.Prev);

            /* 主按钮跟着播放状态换图标 —— 同样是 live 更新，构建时取一次是不够的 */
            FrameworkElement prim = Ui.PrimaryRound(s.Playing ? Ui.GPause : Ui.GPlay, 36, art);
            TextBlock primGlyph = ((Border)prim).Child as TextBlock;
            Ui.At(root, prim, rightX + 38, by);
            OnTap(prim, act.PlayPause);
            built.Live.Add(delegate(Snap n)
            {
                if (primGlyph == null) return;
                string want = n.Playing ? Ui.GPause : Ui.GPlay;
                if (primGlyph.Text != want) primGlyph.Text = want;
            });

            FrameworkElement next = Ui.IconButton(Ui.GNext, 32, look, look.Ink2);
            Ui.At(root, next, rightX + 80, by + 2);
            OnTap(next, act.Next);

            FrameworkElement wave = Ui.Wave(art, look, s.Playing);
            Ui.At(root, wave, w - PadExp - 18, by + 10);
        }

        static void ExpandedTimer(Canvas root, Built built, double w, double h,
                                  Look look, Art art, Snap s, CardActions act)
        {
            double ringSize = 124;
            double contentH = h - 20;      /* 底部那 20 是给指示点留的 */
            FrameworkElement ring = Ui.Ring(ringSize, 8, s.TimerPct, look, art);
            Ui.At(root, ring, PadExp, (contentH - ringSize) / 2);
            built.Live.Add(delegate(Snap n)
            {
                Ui.Refresh(ring, Ui.Ring(ringSize, 8, n.TimerPct, look, art));
            });

            double x = PadExp + ringSize + 18;

            TextBlock label = Ui.Text(s.TimerRunning ? "专注中 · 番茄钟" : "已暂停 · 番茄钟",
                14, FontWeights.Medium, look.Ink3, false);
            Ui.At(root, label, x, 32);
            built.Live.Add(delegate(Snap n)
            {
                label.Text = n.TimerRunning ? "专注中 · 番茄钟" : "已暂停 · 番茄钟";
            });

            TextBlock num = Ui.Text(s.TimerText, 52, FontWeights.Bold, look.Ink1, true);
            Ui.At(root, num, x - 2, 52);
            built.Live.Add(delegate(Snap n) { num.Text = n.TimerText; });

            double by = 124;
            /* 图标必须挂 live 更新器：RefreshNow() 只跑 Live 里的更新函数，
               不会重建整棵树。以前这里只按构建时的 s.TimerRunning 取了一次图标，
               点了开始之后倒计时在走、标题也变了，但按钮上还是那个 ▶ ——
               看起来就像「开始按钮点不了」。 */
            FrameworkElement toggle = Ui.IconButton(
                s.TimerRunning ? Ui.GPause : Ui.GPlay, 34, look, look.Ink1);
            TextBlock toggleGlyph = ((Border)toggle).Child as TextBlock;
            Ui.At(root, toggle, x, by);
            OnTap(toggle, act.TimerToggle, "timer-toggle");
            built.Live.Add(delegate(Snap n)
            {
                if (toggleGlyph == null) return;
                string want = n.TimerRunning ? Ui.GPause : Ui.GPlay;
                if (toggleGlyph.Text != want) toggleGlyph.Text = want;
            });

            FrameworkElement reset = Ui.TextButton("重置", 32, 16, false, look, art);
            Ui.At(root, reset, x + 44, by + 1);
            OnTap(reset, act.TimerReset, "timer-reset");

            FrameworkElement plus = Ui.TextButton("+1 分", 32, 16, false, look, art);
            Ui.At(root, plus, x + 118, by + 1);
            OnTap(plus, act.TimerPlus, "timer-plus");
        }

        static Border Row(double w, double h)
        {
            Border b = new Border();
            b.Width = w;
            b.Height = h;
            b.Background = Brushes.Transparent;
            b.Tag = "act";
            b.Cursor = System.Windows.Input.Cursors.Hand;
            return b;
        }

        /* 中转站。468×268。

           表头和其他页同构（标题 + 数量 + 清空 + 清理丢失），下面不是「行」，
           而是一格一格的桌面图标：上面一个大图标、下面两行名字。

           为什么不再是行：这一页的全部意义是把东西**拖出去**。行是 30 高的
           窄条，按住往外拖得先瞄准那条缝；一格 72×102 的方靶子按住就能走。
           图标也不再是主角旁边那 14px 的配角 —— 48px 的图标 + 两行名字是
           资源管理器那一套，一眼认东西，不用读文件名。

           拖动悬停时整块换成一个大投放区，不再叠在列表上面：
           拖到半途看见的是「松手暂存」，而不是自己暂存的东西被一层虚线压住。 */
        static void ExpandedTransfer(Canvas root, Built built, double w, double h,
                                     Look look, Art art, Snap s, CardActions act)
        {
            TextBlock head = Ui.Text("中转站", 15, FontWeights.SemiBold, look.Ink1, false);
            Ui.At(root, head, PadExp, 16);

            /* 表头右侧从右往左排：数量 / 清空 / 清理丢失。

               位置用固定偏移算，不读控件的 Width ——
               Ui.TextButton 返回的 Border 没设 Width（NaN），
               拿它做减法是 NaN。剪贴板页也是这么排的。 */

            TextBlock cnt = Ui.Text(Transfer.Count() + " 项", 13, FontWeights.Medium, look.Ink3, true);
            cnt.Width = 56;
            cnt.TextAlignment = TextAlignment.Right;
            Ui.At(root, cnt, w - PadExp - 56, 19);

            double clrX = w - PadExp - 56 - 6 - 50;
            FrameworkElement clr = Ui.TextButton("清空", 26, 12, false, look, art);
            Ui.At(root, clr, clrX, 13);
            OnTap(clr, act.ClearTransfer);

            const double qlW = 66;
            double qlX = clrX - 6 - qlW;
            int missing = Transfer.MissingCount();
            FrameworkElement ql = Ui.TextButton("清理丢失", 26, 7, false, look, art);
            ql.Width = qlW;
            Ui.At(root, ql, qlX, 13);
            ql.Visibility = missing > 0 ? Visibility.Visible : Visibility.Collapsed;
            OnTap(ql, act.ClearTransferMissing, "transfer-clear-missing");

            double listW = w - PadExp * 2;
            double listH = h - 46 - PadExp;
            Canvas list = new Canvas();
            list.Width = listW;
            list.Height = listH;
            Ui.At(root, list, PadExp, 46);

            int ver = Transfer.Version();
            RebuildTransferList(list, listW, listH, look, art, act);
            built.Live.Add(delegate(Snap n)
            {
                int v = Transfer.Version();
                if (v == ver) return;
                ver = v;
                RebuildTransferList(list, listW, listH, look, art, act);
                cnt.Text = Transfer.Count() + " 项";
                int m = Transfer.MissingCount();
                ql.Visibility = m > 0 ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        /* 虚线投放区。StrokeDashArray 是「线长,间隔」按线宽的倍数算的。 */
        static FrameworkElement DropZone(double w, double h, Look look, Art art, bool active)
        {
            Color acc = Ui.AccInk(look, art);
            Rectangle r = new Rectangle();
            r.Width = w;
            r.Height = h;
            r.RadiusX = 13;
            r.RadiusY = 13;
            r.StrokeThickness = active ? 1.7 : 1.2;
            r.Stroke = Ui.AB(acc, active ? 0.88 : 0.40);
            r.Fill = Ui.AB(acc, active ? 0.11 : 0.035);
            r.StrokeDashArray = new DoubleCollection(new double[] { 4, 4 });
            return r;
        }

        static void ZoneText(Canvas list, double listW, double listH, Look look,
                             string big, string tip, Color glyphColor, string glyph)
        {
            FrameworkElement g = Ui.Glyph(glyph, 26, glyphColor);
            Ui.At(list, g, (listW - 26) / 2, listH / 2 - 30);

            TextBlock b = Ui.Text(big, 15, FontWeights.SemiBold, look.Ink1, false);
            b.Width = listW;
            b.TextAlignment = TextAlignment.Center;
            Ui.At(list, b, 0, listH / 2 + 4);

            TextBlock t = Ui.Text(tip, 12, FontWeights.Normal, look.Ink3, false);
            t.Width = listW;
            t.TextAlignment = TextAlignment.Center;
            Ui.At(list, t, 0, listH / 2 + 26);
        }

        /* ── 中转站的东西画成桌面图标 ─────────────────────────────────

           一格的理想宽度 / 高度。宽度是「两行名字还能读」的下限，
           高度是「48 图标 + 两行名字 + 上下各一点空气」。

           为什么按这个尺寸反算行列、而不是写死 6×2：列表区的宽高是算出来的
           （w - 36 / h - 64），写死行列就等于把两处算式抄了一遍，改窗口尺寸
           时它们会不同步。这里只给「一格至少多宽、一列大概多高」，由可用面积
           反推行列 —— 于是 432×204 得到 6×2、cellW=72、cellH=102。 */
        const double IconCellMinW = 64;
        const double IconCellIdealH = 96;
        const double IconGlyphSize = 48;
        const double IconLabelH = 28;          /* 两行 11px 名字 */
        const double IconLabelGap = 6;
        const double IconNameRoom = 10;        /* 名字左右各让 5，避免贴到格子边 */

        static void RebuildTransferList(Canvas list, double listW, double listH,
                                        Look look, Art art, CardActions act)
        {
            list.Children.Clear();

            /* 拖动悬停：整块变成投放区。
               不叠在图标上面 —— 拖到半途看见的是「松手暂存」，而不是自己的
               东西被一层虚线压住。 */
            if (Transfer.Hover)
            {
                Ui.At(list, DropZone(listW, listH, look, art, true), 0, 0);
                ZoneText(list, listW, listH, look, "松手暂存", "文件不会被复制，只记路径",
                         Ui.AccInk(look, art), Ui.GFolder);
                return;
            }

            Transfer.View[] items = Transfer.Items();
            if (items.Length == 0)
            {
                Ui.At(list, DropZone(listW, listH, look, art, false), 0, 0);
                ZoneText(list, listW, listH, look, "把文件拖到这里暂存",
                         "支持一次拖入多个文件或文件夹", look.Ink3, Ui.GFolder);
                return;
            }

            int cols = (int)Math.Floor(listW / IconCellMinW);
            if (cols < 1) cols = 1;
            int rows = (int)Math.Round(listH / IconCellIdealH);
            if (rows < 1) rows = 1;
            /* 列数不随条目数缩：桌面上三个图标也是靠左上排三个 72 的格子，
               不会把格子拉成三块 144 宽的大板。 */

            double cellW = listW / cols;
            double cellH = listH / rows;

            int cap = cols * rows;
            bool more = items.Length > cap;
            /* 装不下就让最后一格去报数 —— 默默截断等于让用户以为东西没了 */
            int show = more ? cap - 1 : items.Length;
            if (show < 0) show = 0;

            for (int i = 0; i < show; i++)
            {
                Ui.At(list, TransferCell(items[i], cellW, cellH, look, art, act),
                      0 + (i % cols) * cellW, (i / cols) * cellH);
            }

            if (more)
            {
                int rest = items.Length - show;
                Ui.At(list, TransferMore(rest, cellW, cellH, look),
                      (show % cols) * cellW, (show / cols) * cellH);
            }
        }

        /* 一格：上面一个大图标，下面两行名字。

           命中面是整格：按住图标、按住名字、按在格子空白处，都该能把这一项
           拖出去。图标和文字都关掉命中测试，所以拖拽起点永远是这一格本身，
           不会出现「按在文件名上拖不动」这种事。

           选中高亮和 ✕ 都由悬停态控制：桌面上没有常驻的删除键，
           这一格也不该有。 */
        static FrameworkElement TransferCell(Transfer.View v, double cellW, double cellH,
                                             Look look, Art art, CardActions act)
        {
            string p = v.Path;
            Color ink = v.Missing ? Ui.AccInk(look, art) : look.Ink1;

            Canvas cell = new Canvas();
            cell.Width = cellW;
            cell.Height = cellH;
            /* 透明底是「能被命中」的前提：WPF 里背景为 null 的面板不参与命中测试 */
            cell.Background = Brushes.Transparent;
            cell.Tag = "act";                     /* 按格子不要拖动整个岛 */
            /* 行的身份标记。窗口的右键 Preview 处理靠它让路给这一格的菜单。
               不用 Tag —— Tag 已经被上面那行占了。 */
            cell.Uid = "trow";
            cell.Cursor = System.Windows.Input.Cursors.Hand;

            /* 悬停的选中框：桌面图标选中是「一层薄薄的高光 + 一圈细边」。
               常驻一条实心条会让 12 格变成 12 个方块，反而看不出是图标。 */
            Border sel = new Border();
            sel.Width = cellW - 2;
            sel.Height = cellH - 2;
            sel.CornerRadius = new CornerRadius(8);
            sel.Background = Ui.AB(look.Ink1, 0.13);
            sel.BorderThickness = new Thickness(1);
            sel.BorderBrush = Ui.AB(Ui.AccInk(look, art), 0.42);
            sel.IsHitTestVisible = false;
            sel.Visibility = Visibility.Collapsed;
            Ui.At(cell, sel, 1, 1);

            double contentH = IconGlyphSize + IconLabelGap + IconLabelH;
            double y0 = Math.Max(4, Math.Round((cellH - contentH) / 2));
            double ix = Math.Round((cellW - IconGlyphSize) / 2);

            /* 图标：系统给的大图标优先（Transfer.IconLarge，48px 原生），
               取不到退回一枚字形 —— 空一格比画个错的强。 */
            System.Windows.Media.ImageSource ico = Transfer.IconLarge(p, v.Dir);
            if (ico != null)
            {
                Image img = new Image();
                img.Source = ico;
                img.Width = IconGlyphSize;
                img.Height = IconGlyphSize;
                img.Stretch = Stretch.Uniform;
                img.IsHitTestVisible = false;
                /* 丢失的项一律压暗：一张看着还挺正常的图标指向一个已经不存在的
                   文件，比灰掉更容易骗人。 */
                if (v.Missing) img.Opacity = 0.35;
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                Ui.At(cell, img, ix, y0);
            }
            else
            {
                TextBlock fb = Ui.Glyph(Ui.GFolder, IconGlyphSize * 0.72,
                                        v.Missing ? Fade(look.Ink3, 0.5) : look.Ink3);
                fb.Width = IconGlyphSize;
                fb.TextAlignment = TextAlignment.Center;
                fb.IsHitTestVisible = false;
                Ui.At(cell, fb, ix, y0 + Math.Round(IconGlyphSize * 0.14));
            }

            /* 名字：两行，居中。超长按字符截断加省略号（WPF 在 Wrap + 定高
               时会trim最后一行），所以固定 Height 是必需的，动态高度会自己撑长。 */
            TextBlock name = Ui.Text(Transfer.Name(p), 11, FontWeights.Normal, ink, false);
            name.Width = cellW - IconNameRoom;
            name.TextAlignment = TextAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Height = IconLabelH;
            name.IsHitTestVisible = false;
            Ui.At(cell, name, IconNameRoom / 2, y0 + IconGlyphSize + IconLabelGap);

            /* 悬停出现的移除键。位置贴在图标右上角 —— 桌面图标样的一格没有
               「行尾」可以放它。
               标记用 Uid 而不是 Tag：OnTap 内部会把 Tag 覆写成 "act"，
               两套标记抢同一个属性必然有一头失效（见 Row() 上面的说明）。 */
            Border del = Ui.SolidBar(16, 16, look);
            del.CornerRadius = new CornerRadius(8);
            del.Uid = "del";
            del.IsHitTestVisible = true;
            del.Visibility = Visibility.Collapsed;
            TextBlock dg = Ui.Glyph(Ui.GClose, 9, look.Ink2);
            dg.HorizontalAlignment = HorizontalAlignment.Center;
            dg.VerticalAlignment = VerticalAlignment.Center;
            dg.IsHitTestVisible = false;
            del.Child = dg;
            /* 贴图标右上角；y0 在卡片被压得很矮时会算出负值，夹一下别让它
               半个身子跑到格子外面去。 */
            Ui.At(cell, del, ix + IconGlyphSize - 11, Math.Max(2, y0 - 5));
            OnTap(del, delegate { if (act.TransferRemove != null) act.TransferRemove(p); },
                  "transfer-remove");

            /* 悬停：选中框 + ✕。MouseEnter/Leave 用的是 IsMouseOver 的语义，
               指针移到格子里的图标/文字上不会误触发 Leave。 */
            cell.MouseEnter += delegate
            {
                sel.Visibility = Visibility.Visible;
                del.Visibility = Visibility.Visible;
            };
            cell.MouseLeave += delegate
            {
                sel.Visibility = Visibility.Collapsed;
                del.Visibility = Visibility.Collapsed;
            };

            WireTransferCell(cell, p, v.Missing, act);
            return cell;
        }

        /* 装不下时的最后一格：+N。

           它只报数，不接手势 —— 点它没有去处（要看全部得自己整理），
           给一个能点但什么都不做的格子比一个明显是数字的格子更误导人。 */
        static FrameworkElement TransferMore(int rest, double cellW, double cellH, Look look)
        {
            Canvas cell = new Canvas();
            cell.Width = cellW;
            cell.Height = cellH;

            double contentH = IconGlyphSize + IconLabelGap + IconLabelH;
            double y0 = Math.Max(4, Math.Round((cellH - contentH) / 2));

            TextBlock plus = Ui.Text("+" + rest, 20, FontWeights.SemiBold, look.Ink2, true);
            plus.Width = cellW;
            plus.TextAlignment = TextAlignment.Center;
            Ui.At(cell, plus, 0, y0 + Math.Round((IconGlyphSize - 26) / 2));

            TextBlock tip = Ui.Text("还有 " + rest + " 项", 11, FontWeights.Normal,
                                    look.Ink3, false);
            tip.Width = cellW - IconNameRoom;
            tip.TextAlignment = TextAlignment.Center;
            Ui.At(cell, tip, IconNameRoom / 2, y0 + IconGlyphSize + IconLabelGap);
            return cell;
        }

        /* 一格的三套手势：点开 / 按住拖出 / 右键菜单。

           为什么不用 OnTap：它是在 **MouseLeftButtonDown** 上触发的，
           那样「按住往外拖」会在按下的瞬间先打开文件，拖放根本起不来。
           所以这里手写 Down/Up/Move：Up 且没超过阈值才算点击。

           阈值 4px 和原来一样。走的是按下点与当前点的**距离**，不是时间：
           按住不动再拖，一样能拖出去。 */
        static void WireTransferCell(FrameworkElement cell, string path, bool missing,
                                     CardActions act)
        {
            bool armed = false;
            bool dragged = false;
            Point down = new Point(0, 0);

            cell.PreviewMouseLeftButtonDown += delegate(object s,
                System.Windows.Input.MouseButtonEventArgs e)
            {
                if (IsDel(e.OriginalSource)) { armed = false; return; }
                armed = true;
                dragged = false;
                down = e.GetPosition(cell);
            };

            cell.MouseMove += delegate(object s, System.Windows.Input.MouseEventArgs e)
            {
                if (!armed) return;
                if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
                {
                    armed = false;
                    return;
                }
                Point p = e.GetPosition(cell);
                if (Math.Abs(p.X - down.X) < 4 && Math.Abs(p.Y - down.Y) < 4) return;

                armed = false;
                dragged = true;
                if (missing)
                {
                    if (Diag != null) Diag("drag-out skipped: file missing");
                    return;
                }
                if (act.TransferDragOut != null) act.TransferDragOut(path);
            };

            cell.MouseLeftButtonUp += delegate(object s,
                System.Windows.Input.MouseButtonEventArgs e)
            {
                bool wasArmed = armed;
                armed = false;
                if (!wasArmed || dragged) return;
                if (IsDel(e.OriginalSource)) return;
                if (missing)
                {
                    if (Diag != null) Diag("open skipped: file missing");
                    return;
                }
                if (act.TransferOpen != null) act.TransferOpen(path);
            };

            cell.MouseRightButtonUp += delegate(object s,
                System.Windows.Input.MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (act.TransferMenu != null) act.TransferMenu(path);
            };
        }

        /* 往祖先里找 Uid="del"，判断这次按下是不是落在移除键上 */
        static bool IsDel(object src)
        {
            DependencyObject d = src as DependencyObject;
            for (int i = 0; d != null && i < 6; i++)
            {
                FrameworkElement fe = d as FrameworkElement;
                if (fe != null && fe.Uid == "del") return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        /* 剪贴板的版面常数。

           条高 30、条距 4、最多 6 条：6*30 + 5*4 = 200，列表区正好 204 ——
           一行都不需要滚动，也不会在底部留一条不规则的缝。

           条内文字的左边界和待办纸片对齐（7 + 17 + 8 = 32）：这两页是同一种
           数据形状（一条一条的短文本），滚轮来回切的时候字不该跳一列。 */
        const double ClipBarH = 30;
        const double ClipBarGap = 4;
        const int ClipBarMax = 6;
        const double ClipGlueX = 7;
        const double ClipGlueBox = 17;
        const double ClipTextX = ClipGlueX + ClipGlueBox + 8;

        /* 剪贴板页。468×268。

           版面：表头（标题 + 数量 + 清空）+ 一列剪贴条。

           为什么每条一个条，而不是原来那一列「透明底的字」：剪贴板里最常见的是
           一段多行的话，几行字上下叠着、间距又和条距一样时，两条长文本和
           「一条三行的话」在视觉上分不开 —— 复制错了还得点一下才知道。
           给它一条实心的条，边界就摆在那儿了。

           材质取自 Ui.SolidBar（待办纸片用的同一块）：同一种「一条东西」
           在这座岛上就该是同一种材质。 */
        static void ExpandedClip(Canvas root, Built built, double w, double h,
                                 Look look, Art art, Snap s, CardActions act)
        {
            TextBlock head = Ui.Text("剪贴板历史", 15, FontWeights.SemiBold, look.Ink1, false);
            Ui.At(root, head, PadExp, 16);

            TextBlock cnt = Ui.Text(Clips.Count() + " 条", 13, FontWeights.Medium, look.Ink3, true);
            cnt.Width = 56;
            cnt.TextAlignment = TextAlignment.Right;
            Ui.At(root, cnt, w - PadExp - 56, 19);

            FrameworkElement clr = Ui.TextButton("清空", 26, 12, false, look, art);
            Ui.At(root, clr, w - PadExp - 56 - 6 - 56, 13);
            OnTap(clr, act.ClearClips);

            double listW = w - PadExp * 2;
            double listH = h - 46 - PadExp;
            Canvas list = new Canvas();
            list.Width = listW;
            list.Height = listH;
            Ui.At(root, list, PadExp, 46);

            int ver = Clips.Version();
            RebuildClipList(list, listW, listH, look, act);
            built.Live.Add(delegate(Snap n)
            {
                int v = Clips.Version();
                if (v == ver) return;
                ver = v;
                RebuildClipList(list, listW, listH, look, act);
                cnt.Text = Clips.Count() + " 条";
            });
        }

        static void RebuildClipList(Canvas list, double listW, double listH, Look look,
                                    CardActions act)
        {
            list.Children.Clear();
            string[] items = Clips.Items();
            int show = items.Length < ClipBarMax ? items.Length : ClipBarMax;

            if (show == 0)
            {
                ZoneText(list, listW, listH, look, "还没有剪贴记录",
                         "复制任何文字，就会出现在这里", Fade(look.Ink3, 0.62), Ui.GDoc);
                return;
            }

            /* 悬停时条要亮一档、右边要冒出「复制」那颗字形。两种颜色各算一次
               就够了，不要在每次 MouseEnter 里重新 Mix。 */
            Color hotBg = Ui.Mix(look.CardBg, look.Ink1, 0.07);
            Color coldBg = look.CardBg;

            for (int i = 0; i < show; i++)
            {
                /* 材质：和待办纸片同一块（Ui.SolidBar） */
                Border bar = Ui.SolidBar(listW, ClipBarH, look);
                bar.Cursor = System.Windows.Input.Cursors.Hand;

                Canvas inner = new Canvas();
                inner.Width = listW;
                inner.Height = ClipBarH;
                /* 身份标记（自检/验收靠它认条，不按宽度认）。
                   挂在里层而不是条本身：OnTap 会把条换成一层命中面，
                   Uid 跟着被复制一份，数条数就会数出一倍。 */
                inner.Uid = "cbar";
                bar.Child = inner;

                TextBlock glue = Ui.Glyph(Ui.GDoc, 12, look.Ink3);
                glue.Width = ClipGlueBox;
                glue.TextAlignment = TextAlignment.Center;
                glue.IsHitTestVisible = false;
                Ui.At(inner, glue, ClipGlueX, Math.Round((ClipBarH - 16) / 2));

                /* 右边这 22 是「复制」那颗字形的固定坑位：悬停才显示，
                   但宽度一直占着 —— 否则鼠标一进一出，正文会重新截一次，
                   看着像条目自己在抖。 */
                const double tailW = 22;

                TextBlock tb = Ui.Text(OneLine(items[i]), 13, FontWeights.Normal,
                                       look.Ink1, false);
                tb.Width = listW - ClipTextX - tailW - 10;
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
                tb.IsHitTestVisible = false;      /* 按在字上也算按在这条上 */
                Ui.At(inner, tb, ClipTextX, Math.Round((ClipBarH - 18) / 2));

                TextBlock copy = Ui.Glyph(Ui.GCopy, 11, Ui.Mix(look.Ink3, look.Ink1, 0.5));
                copy.Width = 16;
                copy.TextAlignment = TextAlignment.Center;
                copy.IsHitTestVisible = false;
                copy.Visibility = Visibility.Collapsed;
                Ui.At(inner, copy, listW - tailW - 2, Math.Round((ClipBarH - 14) / 2));

                /* 悬停：条亮一档 + 露出「复制」。这只是把「点一下会发生什么」
                   提前说清楚 —— 整条本来就已经是一个按钮。 */
                bar.MouseEnter += delegate
                {
                    bar.Background = Ui.Br(hotBg);
                    copy.Visibility = Visibility.Visible;
                };
                bar.MouseLeave += delegate
                {
                    bar.Background = Ui.Br(coldBg);
                    copy.Visibility = Visibility.Collapsed;
                };

                int idx = i;
                /* 先 At 再 OnTap：OnTap 会给元素套一层命中面（Squish.Wrap），
                   套的时候要读它现在的父容器和 Canvas.Left/Top。顺序反过来，
                   元素还没有父容器，就套不上命中面了（见 Squish.Wrap 的退路）。 */
                Ui.At(list, bar, 0, i * (ClipBarH + ClipBarGap));
                OnTap(bar, delegate { if (act.CopyClip != null) act.CopyClip(idx); },
                      "clip-" + idx);
            }
        }

        /* 一条剪贴内容压成一行。

           条只有 30 高，放不下换行；多行内容在这里被折成单行（连续空白并成
           一个空格），超出宽度的部分交给 TextTrimming 画省略号。2000 字的内容
           也量得动（NoWrap 只量一次行宽），所以不需要在这里截断 —— 截断会
           把「余下还有多少」这件事变得更不准确。 */
        static string OneLine(string text)
        {
            if (text == null) return "";
            System.Text.StringBuilder sb = new System.Text.StringBuilder(text.Length);
            bool pendingSpace = false;
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if (ch == '\r' || ch == '\n' || ch == '\t' || ch == ' ' || ch == '\u3000')
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }
                if (pendingSpace)
                {
                    sb.Append(' ');
                    pendingSpace = false;
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        static void ExpandedTodo(Canvas root, Built built, double w, double h,
                                 Look look, Art art, Snap s, CardActions act)
        {
            /* 字号整体上调一档：岛不大，12px 在 1.5 缩放下确实吃力。
               头部 15、按钮 12.5。 */
            TextBlock head = Ui.Text("待办", 15, FontWeights.SemiBold, look.Ink1, false);
            Ui.At(root, head, PadExp, 16);

            TextBlock cnt = Ui.Text(Todos.CountText(), 13, FontWeights.Medium, look.Ink3, true);
            cnt.Width = 64;
            cnt.TextAlignment = TextAlignment.Right;
            Ui.At(root, cnt, w - PadExp - 64, 19);

            /* 到点的那条单独在顶部支一条横幅，保证一眼能看到 */
            string banner = Todos.DueBanner();
            if (banner.Length > 0)
            {
                TextBlock due = Ui.Text(banner, 13, FontWeights.SemiBold,
                    Ui.AccInk(look, art), false);
                due.Width = w - PadExp * 2;
                due.TextTrimming = TextTrimming.CharacterEllipsis;
                Ui.At(root, due, PadExp, 40);
            }

            double listTop = banner.Length > 0 ? 66 : 46;
            double listW = w - PadExp * 2;
            /* 塔的房间比原来那 5 行高一点（150 → 156）：多出来的 6px 是塔顶
               上方的空气，让手里的卡片能往上举。再多就会撞到下面的按钮
               （横幅出现时 listTop=66，66+156+10+30 = 262，页码在 267）。 */
            double roomH = 156;

            TodoTower tower = new TodoTower(listW, roomH, look, art, act, false);
            Ui.At(root, tower.Root, PadExp, listTop);
            built.Stop.Add(delegate { tower.Stop(); });
            built.Extra = tower;                 /* 自检用，见 Built.Extra */

            double by = listTop + roomH + 10;
            FrameworkElement add = Ui.TextButton("写一条", 30, 16, true, look, art);
            Ui.At(root, add, PadExp, by);
            OnTap(add, act.AddTodo);

            /* ── 已完成的那一摞 ──────────────────────────────────────
               原来这一页把已完成的也列在同一张单子上、点一下能改回来。
               纸片会被勾走（这是这个块的全部意义），所以「改回来」得另有一个
               入口：同一个位置上的这一颗按钮把塔换成「已完成」那一摞 ——
               同一套物理、同一套勾选逻辑，只是取哪几条待办做纸片不同。
               这样既没有把已完成藏起来，也没有为了塞两摞而把字挤窄。 */
            Border mode = (Border)Ui.TextButton("已完成 0", 30, 14, false, look, art);
            TextBlock modeLabel = mode.Child as TextBlock;
            Ui.At(root, mode, PadExp + 92, by);
            OnTap(mode, delegate { tower.ToggleMode(); }, "todo-mode");

            Border clr = (Border)Ui.TextButton("清空", 30, 14, false, look, art);
            Ui.At(root, clr, w - PadExp - 56, by);
            clr.Visibility = Visibility.Collapsed;
            OnTap(clr, act.ClearDoneTodos, "todo-clear-done");

            /* 表头/按钮这些「塔外面」的字跟数据走；塔自己跟版本对账 */
            Action syncChrome = delegate
            {
                int done = 0;
                TodoItem[] all = Todos.Items();
                for (int i = 0; i < all.Length; i++) if (all[i].Done) done++;

                cnt.Text = Todos.CountText();
                if (modeLabel != null)
                    modeLabel.Text = tower.DoneMode ? "返回待办" : ("已完成 " + done);
                clr.Visibility = tower.DoneMode && done > 0
                    ? Visibility.Visible : Visibility.Collapsed;
            };

            int ver = Todos.Version();
            syncChrome();
            built.Live.Add(delegate(Snap n)
            {
                int v = Todos.Version();
                if (v == ver) return;
                ver = v;
                tower.Refresh();
                syncChrome();
            });
        }

        /* ================================================================ 天气 */

        /* 第一层带天气时，左右内边距。6 是量出来的：球 16.8 + 左 6 之后，
           右边留给「图标 + 最长温度（-18°）」还有 8 DIP 的空档，
           再小就会在零下天气里贴到一起。桌宠停靠时也要用这个值对齐球位。 */
        public const double IdleWeatherPadX = 6;

        /* 第一层：球贴在左边，右边显示「图标 + 温度」。

           这一层的语义是「状态条」而不是「一枚居中的徽章」：眼睛扫过右上角
           就知道现在几度，球只是告诉你岛还活着。所以两端对齐，不是整体居中。

           为什么只在有数据时才变宽：没东西可显示就不该占地方 ——
           拿不到天气的那台机器上，岛应该还是原来那颗 52×30、球居中的样子，
           一个像素都不该变。宽度由 Scenes.IdleWeatherW 定，
           岛心不动（窗口按展开尺寸建，小尺寸时在窗口里居中）。 */
        static void BuildIdle(Canvas root, Built built, double w, double h,
                              Look look, Art art, Snap s, bool petDocked)
        {
            /* petDocked 时球的坑由桌宠自己画（见 PetIsland 的 _petDock）。
               但**只有没天气**时才能整层退出：有天气的话右边那句温度还得画，
               否则桌宠一开，第一层的天气就整个消失了。 */
            if (petDocked && !(s.WxHas && w > Scenes.IdleW + 1)) return;

            const double padX = IdleWeatherPadX;

            double size = IdleOrbSize(w, h);
            bool withWx = s.WxHas && w > Scenes.IdleW + 1;

            if (!withWx)
            {
                /* 第一层：只剩一颗在转的球，别的一概没有 */
                FrameworkElement orb = Ui.Orb(art, look, size);
                Ui.At(root, orb, (w - size) / 2, (h - size) / 2);
                return;
            }

            /* 用 Grid + HorizontalAlignment 而不是手算 x：
               温度从「9°」变成「-18°」时宽度会变，右对齐让右边固定、
               数字往左长 —— 手算的居中会跟着跳一格。 */
            Grid layer = new Grid();
            layer.Width = w;
            layer.Height = h;

            FrameworkElement orbEl = Ui.Orb(art, look, size);
            orbEl.HorizontalAlignment = HorizontalAlignment.Left;
            orbEl.VerticalAlignment = VerticalAlignment.Center;
            orbEl.Margin = new Thickness(padX, 0, 0, 0);
            /* 桌宠占着球位时不画这颗球：同一格画两个球会看起来像重影。
               球位那格由 PetIsland 的桌宠填，位置也用同一个 padX 对齐。 */
            if (!petDocked) layer.Children.Add(orbEl);

            StackPanel weather = new StackPanel();
            weather.Orientation = Orientation.Horizontal;
            weather.HorizontalAlignment = HorizontalAlignment.Right;
            weather.VerticalAlignment = VerticalAlignment.Center;
            weather.Margin = new Thickness(0, 0, padX, 0);

            double iconSize = 13;
            Canvas iconHost = new Canvas();
            iconHost.Width = iconSize;
            iconHost.Height = iconSize;
            iconHost.Margin = new Thickness(0, 0, 4, 0);
            iconHost.VerticalAlignment = VerticalAlignment.Center;
            iconHost.Children.Add(WeatherIcon(s.Wx.Kind, iconSize));
            weather.Children.Add(iconHost);

            TextBlock temp = Ui.Text(s.Wx.TempText, 12, FontWeights.SemiBold,
                                     look.Ink1, true);
            temp.VerticalAlignment = VerticalAlignment.Center;
            weather.Children.Add(temp);

            layer.Children.Add(weather);
            Ui.At(root, layer, 0, 0);

            /* 温度会变，图标会换。层级不重建，靠 Live 改文字 + 换图标 */
            bool haveKind = true;
            WKind curKind = s.Wx.Kind;
            built.Live.Add(delegate(Snap n)
            {
                if (!n.WxHas) return;
                temp.Text = n.Wx.TempText;
                WKind k = n.Wx.Kind;
                if (haveKind && k == curKind) return;
                curKind = k;
                haveKind = true;
                iconHost.Children.Clear();
                iconHost.Children.Add(WeatherIcon(k, iconSize));
            });
        }

        /* 展开的天气卡。468×268。

           版面：左边一个大图标 + 大温度，右边城市/地区/新鲜度，
           下面一条分隔线，再下面三项指标（体感/湿度/风），
           再下面三天预报（今天 / 明天 / 后天），最底一行日出日落。

           为什么是三天不是七天：七天得压成一条细线才塞得下，读起来反而慢。
           数据层抓的是七天（Weather.ForecastDays），卡片只画前三天。 */
        static void ExpandedWeather(Canvas root, Built built, double w, double h,
                                    Look look, Art art, Snap s, CardActions act)
        {
            double left = PadExp;
            double rightEdge = w - PadExp;
            double contentW = w - PadExp * 2;

            double iconSize = 72;

            /* ---------------------------------------------------------- 图标 */
            Canvas iconHost = new Canvas();
            iconHost.Width = iconSize;
            iconHost.Height = iconSize;
            iconHost.Children.Add(WeatherIcon(s.WxHas ? s.Wx.Kind : WKind.Cloud, iconSize));
            Ui.At(root, iconHost, left, 10);

            /* ---------------------------------------------------------- 温度 */
            TextBlock temp = Ui.Text(s.WxTempText, 40, FontWeights.Bold, look.Ink1, true);
            Ui.At(root, temp, left + iconSize + 10, 2);

            /* ------------------------------------------------------ 天气描述 */
            TextBlock desc = Ui.Text(s.WxDesc, 13.5, FontWeights.Medium, look.Ink2, false);
            desc.MaxWidth = 168;
            desc.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.At(root, desc, left + iconSize + 13, 58);

            /* -------------------------------------------- 右上：城市 / 地区 / 时间 */
            double blockW = 150;
            double blockX = rightEdge - blockW;

            TextBlock place = Ui.Text(s.WxPlace, 14, FontWeights.SemiBold, look.Ink1, false);
            place.Width = blockW;
            place.TextAlignment = TextAlignment.Right;
            place.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.At(root, place, blockX, 12);

            TextBlock where = Ui.Text(s.WxWhere, 11, FontWeights.Normal, look.Ink3, false);
            where.Width = blockW;
            where.TextAlignment = TextAlignment.Right;
            where.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.At(root, where, blockX, 34);

            TextBlock age = Ui.Text(s.WxAge, 11, FontWeights.Normal, look.Ink3, false);
            age.Width = blockW;
            age.TextAlignment = TextAlignment.Right;
            Ui.At(root, age, blockX, 52);

            Rule(root, left, 88, contentW, look);

            /* ------------------------------------------------------ 两种版面 */
            if (!s.WxHas)
            {
                EmptyWeather(root, w, h, look, art, s, act, left, contentW);
                WatchWeather(built, look, temp, desc, place, where, age, iconHost, iconSize);
                return;
            }

            /* ------------------------------------------------ 三项指标 */
            double cell = 138;
            double gap = 9;

            string[] labels = new string[] { "体感", "湿度", "风速" };
            string[] values = new string[]
            {
                s.Wx.HasFeels ? Math.Round(s.Wx.Feels) + "°" : "--",
                s.Wx.HasHumidity ? s.Wx.Humidity + "%" : "--",
                s.Wx.HasWind ? s.Wx.Wind.ToString("0.0") + " km/h" : "--"
            };

            TextBlock[] valBlocks = new TextBlock[3];
            for (int i = 0; i < 3; i++)
            {
                double cx = left + i * (cell + gap);

                TextBlock lb = Ui.Text(labels[i], 11, FontWeights.Normal, look.Ink3, false);
                Ui.At(root, lb, cx, 98);

                TextBlock vb = Ui.Text(values[i], 15, FontWeights.SemiBold, look.Ink1, true);
                Ui.At(root, vb, cx, 112);
                valBlocks[i] = vb;
            }

            Rule(root, left, 140, contentW, look);

            /* ------------------------------------------------ 三天预报 */
            TextBlock[] dayLabels = new TextBlock[3];
            TextBlock[] dayTemps = new TextBlock[3];
            Canvas[] dayIcons = new Canvas[3];

            for (int i = 0; i < 3; i++)
            {
                double cx = left + i * (cell + gap);
                WeatherDay d = (i < s.Wx.Days.Count) ? s.Wx.Days[i] : null;

                /* 今天/明天/后天 用 Ink2 而不是 Ink3：Ink3 只有 0.48 透明，
                   10.5px 的中文在这个浓度下几乎糊进玻璃底里。
                   这三行是整块预报的标题，读者是先看见它们再读温度的。 */
                TextBlock dl = Ui.Text(d != null ? d.Label : "—", 10.5,
                                       FontWeights.Normal, look.Ink2, false);
                dl.Width = cell;
                dl.TextAlignment = TextAlignment.Center;
                Ui.At(root, dl, cx, 148);
                dayLabels[i] = dl;

                Canvas di = new Canvas();
                di.Width = 26;
                di.Height = 26;
                WKind k = d != null ? Weather.Kind(d.Code, true) : WKind.Cloud;
                di.Children.Add(WeatherIcon(k, 26));
                Ui.At(root, di, cx + (cell - 26) / 2, 164);
                dayIcons[i] = di;

                string txt = d != null
                    ? Math.Round(d.High) + "° / " + Math.Round(d.Low) + "°"
                    : "-- / --";
                TextBlock dt = Ui.Text(txt, 13, FontWeights.Medium, look.Ink1, true);
                dt.Width = cell;
                dt.TextAlignment = TextAlignment.Center;
                Ui.At(root, dt, cx, 194);
                dayTemps[i] = dt;
            }

            /* ------------------------------------------------ 日出日落 */
            if (s.Wx.Sunrise.Length > 0 && s.Wx.Sunset.Length > 0)
            {
                string sun = "日出 " + s.Wx.Sunrise + "   ·   日落 " + s.Wx.Sunset;
                TextBlock st = Ui.Text(sun, 11, FontWeights.Normal, look.Ink3, true);
                st.Width = contentW;
                st.TextAlignment = TextAlignment.Center;
                Ui.At(root, st, left, 222);
            }

            /* ------------------------------------------------ 点城市 = 换城市 */
            OnTap(RowOf(root, blockX, 8, blockW, 58), act.PickCity, "weather-city");

            WatchWeather(built, look, temp, desc, place, where, age, iconHost, iconSize);
            WatchForecast(built, dayLabels, dayIcons, dayTemps, valBlocks, look);
        }

        /* 没有数据时的版面：不画一堆 --，直接说清楚下一步该干什么。 */
        static void EmptyWeather(Canvas root, double w, double h, Look look, Art art,
                                 Snap s, CardActions act, double left, double contentW)
        {
            TextBlock big = Ui.Text("还没有天气数据", 14, FontWeights.SemiBold, look.Ink1, false);
            big.Width = contentW;
            big.TextAlignment = TextAlignment.Center;
            Ui.At(root, big, left, 104);

            /* 具体的错误已经在描述行（s.WxDesc）说了一遍，这里不重复，
               只说下一步该干什么 —— 同一句话在一张卡上出现两次，读起来像坏了。 */
            TextBlock t = Ui.Text("取数要联网。IP 定位不准时，可以在右键菜单里指定城市。",
                                  11.5, FontWeights.Normal, look.Ink3, false);
            t.Width = contentW;
            t.TextAlignment = TextAlignment.Center;
            t.TextWrapping = TextWrapping.Wrap;
            Ui.At(root, t, left, 128);

            double btnW = 84, btnH = 30;
            double totalW = btnW * 2 + 10;
            double bx = left + (contentW - totalW) / 2;

            FrameworkElement pick = Ui.TextButton("选择城市", btnH, 14, true, look, art);
            Ui.At(root, pick, bx, 172);
            OnTap(pick, act.PickCity, "weather-pick");

            FrameworkElement again = Ui.TextButton("重新获取", btnH, 14, false, look, art);
            Ui.At(root, again, bx + btnW + 10, 172);
            OnTap(again, act.RefreshWeather, "weather-retry");
        }

        /// <summary>一个透明的点击区域。Row 那套默认 20×20，这里要自定义大小。</summary>
        static Border RowOf(Canvas root, double x, double y, double w, double h)
        {
            Border b = Row(w, h);
            Ui.At(root, b, x, y);
            return b;
        }

        /// <summary>温度/城市/描述/图标跟着数据走。层级不重建，只改这几个元素。</summary>
        static void WatchWeather(Built built, Look look, TextBlock temp, TextBlock desc,
                                 TextBlock place, TextBlock where, TextBlock age,
                                 Canvas iconHost, double iconSize)
        {
            int ver = Weather.Version();
            WKind curKind = WKind.Cloud;

            built.Live.Add(delegate(Snap n)
            {
                int v = Weather.Version();
                if (v == ver) return;
                ver = v;

                temp.Text = n.WxTempText;
                desc.Text = n.WxDesc;
                place.Text = n.WxPlace;
                where.Text = n.WxWhere;
                age.Text = n.WxAge;

                if (!n.WxHas) return;
                WKind k = n.Wx.Kind;
                if (k == curKind) return;
                curKind = k;
                iconHost.Children.Clear();
                iconHost.Children.Add(WeatherIcon(k, iconSize));
            });
        }

        /// <summary>预报与三项指标。数据换了才动，否则每 400ms 白改一遍。</summary>
        static void WatchForecast(Built built, TextBlock[] dayLabels, Canvas[] dayIcons,
                                  TextBlock[] dayTemps, TextBlock[] vals, Look look)
        {
            int ver = Weather.Version();
            built.Live.Add(delegate(Snap n)
            {
                int v = Weather.Version();
                if (v == ver) return;
                ver = v;
                if (!n.WxHas) return;

                if (vals.Length >= 3)
                {
                    vals[0].Text = n.Wx.HasFeels ? Math.Round(n.Wx.Feels) + "°" : "--";
                    vals[1].Text = n.Wx.HasHumidity ? n.Wx.Humidity + "%" : "--";
                    vals[2].Text = n.Wx.HasWind ? n.Wx.Wind.ToString("0.0") + " km/h" : "--";
                }

                for (int i = 0; i < 3 && i < dayLabels.Length; i++)
                {
                    WeatherDay d = (i < n.Wx.Days.Count) ? n.Wx.Days[i] : null;
                    if (d == null) continue;

                    dayLabels[i].Text = d.Label;
                    dayTemps[i].Text = Math.Round(d.High) + "° / " + Math.Round(d.Low) + "°";

                    dayIcons[i].Children.Clear();
                    dayIcons[i].Children.Add(WeatherIcon(Weather.Kind(d.Code, true), 26));
                }
            });
        }

        /* 一条分隔线。0.22 的墨色在四种外观里都够淡 —— 它是分栏，不是边框。 */
        static void Rule(Canvas root, double x, double y, double width, Look look)
        {
            Rectangle r = new Rectangle();
            r.Width = width;
            r.Height = 1;
            r.Fill = Ui.Br(Fade(look.Ink3, 0.22));
            Ui.At(root, r, x, y);
        }

        /* ------------------------------------------------------------ 天气图标

           全部手绘，不用 emoji。理由：emoji 在不同机器上落到的字体不一样
           （Segoe UI Emoji / Noto / 各手机厂自带），同一个码点可能画出完全
           不同的东西；而且彩色 emoji 压在这套玻璃面板上，和旁边手写字的
           质感差得很远。这里每种都是 2~6 个 WPF 形状，配色固定，
           四种外观下都读得出来。 */

        static readonly Color WxSunCol = Color.FromRgb(0xFF, 0xC4, 0x48);
        static readonly Color WxSunCore = Color.FromRgb(0xFF, 0xA8, 0x2C);
        static readonly Color WxMoonCol = Color.FromRgb(0xF6, 0xEC, 0xC8);
        static readonly Color WxCloudCol = Color.FromRgb(0xEE, 0xF2, 0xFA);
        static readonly Color WxCloudShade = Color.FromRgb(0xA8, 0xB4, 0xC8);
        static readonly Color WxRainCol = Color.FromRgb(0x60, 0xAA, 0xFF);
        static readonly Color WxSnowCol = Color.FromRgb(0xDC, 0xEC, 0xFF);
        static readonly Color WxBoltCol = Color.FromRgb(0xFF, 0xD0, 0x40);

        public static FrameworkElement WeatherIcon(WKind kind, double size)
        {
            Canvas c = new Canvas();
            c.Width = size;
            c.Height = size;
            double s = size;

            switch (kind)
            {
                case WKind.Sun:
                    SunShape(c, s * 0.5, s * 0.5, s * 0.28, s);
                    break;

                case WKind.Moon:
                    MoonShape(c, s * 0.5, s * 0.5, s * 0.30);
                    break;

                case WKind.Partly:
                    SunShape(c, s * 0.36, s * 0.34, s * 0.17, s);
                    CloudShape(c, s * 0.54, s * 0.62, s * 0.78);
                    break;

                case WKind.PartlyNight:
                    MoonShape(c, s * 0.34, s * 0.34, s * 0.22);
                    CloudShape(c, s * 0.54, s * 0.62, s * 0.78);
                    break;

                case WKind.Cloud:
                    CloudShape(c, s * 0.5, s * 0.52, s * 0.88);
                    break;

                case WKind.Fog:
                    CloudShape(c, s * 0.5, s * 0.42, s * 0.84);
                    Bar(c, s * 0.20, s * 0.72, s * 0.60, s * 0.07, WxCloudShade);
                    Bar(c, s * 0.28, s * 0.86, s * 0.44, s * 0.07, WxCloudShade);
                    break;

                case WKind.Drizzle:
                    CloudShape(c, s * 0.5, s * 0.40, s * 0.84);
                    Drop(c, s * 0.34, s * 0.72, s * 0.10, WxRainCol);
                    Drop(c, s * 0.58, s * 0.72, s * 0.10, WxRainCol);
                    break;

                case WKind.Rain:
                    CloudShape(c, s * 0.5, s * 0.38, s * 0.84);
                    Drop(c, s * 0.30, s * 0.66, s * 0.16, WxRainCol);
                    Drop(c, s * 0.50, s * 0.70, s * 0.16, WxRainCol);
                    Drop(c, s * 0.70, s * 0.66, s * 0.16, WxRainCol);
                    break;

                case WKind.Shower:
                    SunShape(c, s * 0.30, s * 0.28, s * 0.14, s);
                    CloudShape(c, s * 0.54, s * 0.50, s * 0.76);
                    Drop(c, s * 0.40, s * 0.76, s * 0.14, WxRainCol);
                    Drop(c, s * 0.64, s * 0.76, s * 0.14, WxRainCol);
                    break;

                case WKind.Snow:
                    CloudShape(c, s * 0.5, s * 0.38, s * 0.84);
                    Flake(c, s * 0.32, s * 0.76, s * 0.10);
                    Flake(c, s * 0.52, s * 0.84, s * 0.10);
                    Flake(c, s * 0.72, s * 0.76, s * 0.10);
                    break;

                case WKind.Thunder:
                    CloudShape(c, s * 0.5, s * 0.38, s * 0.84);
                    Bolt(c, s * 0.50, s * 0.60, s * 0.30);
                    break;

                default:
                    CloudShape(c, s * 0.5, s * 0.52, s * 0.88);
                    break;
            }

            return c;
        }

        /// <summary>太阳：一个圆 + 八根光芒。
        /// 圆盘占 0.56 倍尺寸、光芒伸到 0.91 倍 —— 这两个数不是随便取的：
        /// 再小，它在 26px 的预报行里会比云系图标轻一大截，一行三个图标看着就不齐。</summary>
        static void SunShape(Canvas c, double cx, double cy, double r, double s)
        {
            for (int i = 0; i < 8; i++)
            {
                double a = Math.PI * i / 4.0;
                double x1 = cx + Math.Cos(a) * r * 1.30;
                double y1 = cy + Math.Sin(a) * r * 1.30;
                double x2 = cx + Math.Cos(a) * r * 1.62;
                double y2 = cy + Math.Sin(a) * r * 1.62;

                Line ln = new Line();
                ln.X1 = x1; ln.Y1 = y1; ln.X2 = x2; ln.Y2 = y2;
                ln.Stroke = Ui.Br(WxSunCol);
                ln.StrokeThickness = Math.Max(1.1, s * 0.075);
                ln.StrokeStartLineCap = PenLineCap.Round;
                ln.StrokeEndLineCap = PenLineCap.Round;
                c.Children.Add(ln);
            }

            Ellipse e = new Ellipse();
            e.Width = r * 2;
            e.Height = r * 2;
            RadialGradientBrush g = new RadialGradientBrush();
            g.GradientStops.Add(new GradientStop(WxSunCol, 0.0));
            g.GradientStops.Add(new GradientStop(WxSunCore, 1.0));
            e.Fill = g;
            Canvas.SetLeft(e, cx - r);
            Canvas.SetTop(e, cy - r);
            c.Children.Add(e);
        }

        /// <summary>月亮：一个圆减去偏一点的另一个圆，得到弯月。</summary>
        static void MoonShape(Canvas c, double cx, double cy, double r)
        {
            EllipseGeometry full = new EllipseGeometry(new Point(cx, cy), r, r);
            EllipseGeometry bite = new EllipseGeometry(
                new Point(cx + r * 0.58, cy - r * 0.30), r * 0.94, r * 0.94);

            Path p = new Path();
            p.Data = Geometry.Combine(full, bite, GeometryCombineMode.Exclude, null);
            p.Fill = Ui.Br(WxMoonCol);
            c.Children.Add(p);
        }

        /// <summary>云：三个圆 + 底部一条圆角横条。</summary>
        static void CloudShape(Canvas c, double cx, double cy, double w)
        {
            double r1 = w * 0.19, r2 = w * 0.25, r3 = w * 0.17;

            Ellipse a = new Ellipse();
            a.Width = r1 * 2; a.Height = r1 * 2;
            a.Fill = Ui.Br(WxCloudCol);
            Canvas.SetLeft(a, cx - w * 0.30 - r1);
            Canvas.SetTop(a, cy - r1 * 0.6);
            c.Children.Add(a);

            Rectangle baseBar = new Rectangle();
            baseBar.Width = w * 0.72;
            baseBar.Height = w * 0.26;
            baseBar.RadiusX = w * 0.13;
            baseBar.RadiusY = w * 0.13;
            baseBar.Fill = Ui.Br(WxCloudCol);
            Canvas.SetLeft(baseBar, cx - w * 0.36);
            Canvas.SetTop(baseBar, cy - w * 0.02);
            c.Children.Add(baseBar);

            Ellipse b = new Ellipse();
            b.Width = r2 * 2; b.Height = r2 * 2;
            b.Fill = Ui.Br(WxCloudCol);
            Canvas.SetLeft(b, cx - r2);
            Canvas.SetTop(b, cy - r2 - w * 0.03);
            c.Children.Add(b);

            Ellipse d = new Ellipse();
            d.Width = r3 * 2; d.Height = r3 * 2;
            d.Fill = Ui.Br(WxCloudCol);
            Canvas.SetLeft(d, cx + w * 0.20 - r3);
            Canvas.SetTop(d, cy - r3 * 0.4);
            c.Children.Add(d);
        }

        /// <summary>一滴雨。上宽下尖，用一条圆头的粗线，比多边形省事也更好看。</summary>
        static void Drop(Canvas c, double cx, double top, double len, Color col)
        {
            Line ln = new Line();
            ln.X1 = cx; ln.Y1 = top;
            ln.X2 = cx; ln.Y2 = top + len;
            ln.Stroke = Ui.Br(col);
            ln.StrokeThickness = Math.Max(1.2, len * 0.30);
            ln.StrokeStartLineCap = PenLineCap.Round;
            ln.StrokeEndLineCap = PenLineCap.Round;
            c.Children.Add(ln);
        }

        /// <summary>一条细横条（雾、沙尘）。</summary>
        static void Bar(Canvas c, double x, double y, double w, double h, Color col)
        {
            Rectangle r = new Rectangle();
            r.Width = w;
            r.Height = h;
            r.RadiusX = h / 2;
            r.RadiusY = h / 2;
            r.Fill = Ui.Br(col);
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            c.Children.Add(r);
        }

        /// <summary>雪花：三条交叉的短线。</summary>
        static void Flake(Canvas c, double cx, double cy, double r)
        {
            for (int i = 0; i < 3; i++)
            {
                double a = Math.PI * i / 3.0;
                Line ln = new Line();
                ln.X1 = cx - Math.Cos(a) * r;
                ln.Y1 = cy - Math.Sin(a) * r;
                ln.X2 = cx + Math.Cos(a) * r;
                ln.Y2 = cy + Math.Sin(a) * r;
                ln.Stroke = Ui.Br(WxSnowCol);
                ln.StrokeThickness = Math.Max(1.1, r * 0.26);
                ln.StrokeStartLineCap = PenLineCap.Round;
                ln.StrokeEndLineCap = PenLineCap.Round;
                c.Children.Add(ln);
            }
        }

        /// <summary>闪电：一条折线多边形。</summary>
        static void Bolt(Canvas c, double cx, double top, double hgt)
        {
            double wdt = hgt * 0.55;

            PathFigure fig = new PathFigure();
            fig.StartPoint = new Point(cx + wdt * 0.10, top);
            fig.Segments.Add(new LineSegment(new Point(cx - wdt * 0.42, top + hgt * 0.56), true));
            fig.Segments.Add(new LineSegment(new Point(cx + wdt * 0.02, top + hgt * 0.56), true));
            fig.Segments.Add(new LineSegment(new Point(cx - wdt * 0.16, top + hgt), true));
            fig.Segments.Add(new LineSegment(new Point(cx + wdt * 0.46, top + hgt * 0.40), true));
            fig.Segments.Add(new LineSegment(new Point(cx + wdt * 0.02, top + hgt * 0.40), true));
            fig.IsClosed = true;

            PathGeometry pg = new PathGeometry();
            pg.Figures.Add(fig);

            Path p = new Path();
            p.Data = pg;
            p.Fill = Ui.Br(WxBoltCol);
            c.Children.Add(p);
        }

    }
}
