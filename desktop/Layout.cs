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

        public void Refresh(Snap s)
        {
            for (int i = 0; i < Live.Count; i++)
            {
                try { Live[i](s); }
                catch { }
            }
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
                if (!petDocked)
                {
                    /* 第一层：只剩一颗在转的球，别的一概没有 */
                    double size = IdleOrbSize(w, h);
                    FrameworkElement orb = Ui.Orb(art, look, size);
                    Ui.At(root, orb, (w - size) / 2, (h - size) / 2);
                }
                /* petDocked 时这里留一个空 canvas：版面和尺寸不变，
                   球的坑由 IslandWindow 那一层填（见 _petDock）。 */
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

                TextBlock tail = Ui.Text(s.TimerPctText, 10.5, FontWeights.Medium, look.Ink3, true);
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

            TextBlock t = Ui.Text(title, 11.5, FontWeights.SemiBold, look.Ink1, false);
            t.Width = avail;
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.At(root, t, x, cy - 11.5);

            TextBlock s = Ui.Text(sub, 9.5, FontWeights.Normal, look.Ink3, false);
            s.Width = avail;
            s.TextTrimming = TextTrimming.CharacterEllipsis;
            Ui.At(root, s, x, cy + 1.5);

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

            TextBlock posL = Ui.Text(s.MediaPos, 11.5, FontWeights.Medium, look.Ink3, true);
            Ui.At(root, posL, rightX, 110);

            TextBlock durL = Ui.Text(s.MediaDur, 11.5, FontWeights.Medium, look.Ink3, true);
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

            TextBlock artist = Ui.Text(s.MediaSub, 14.5, FontWeights.Normal, look.Ink2, false);
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

        /* 中转站。

           版面故意和剪贴板页同构：表头（标题 + 数量 + 清空）+ 行列表，
           行高 30、行距 4、最多 6 行 —— 两页放在一起切换时不会跳。

           拖动悬停时整块换成一个大投放区，不再叠在列表上面：
           拖到半途看见的是「松手暂存」，而不是自己的文件列表被一层虚线压住。 */
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

            double qlX = clrX - 6 - 66;
            int missing = Transfer.MissingCount();
            FrameworkElement ql = Ui.TextButton("清理丢失", 26, 10, false, look, art);
            Ui.At(root, ql, qlX, 13);
            ql.Visibility = missing > 0 ? Visibility.Visible : Visibility.Collapsed;
            OnTap(ql, act.ClearTransferMissing, "transfer-clear-missing");

            double listW = w - PadExp * 2;
            double rowH = 30;
            double listH = h - 46 - PadExp;
            Canvas list = new Canvas();
            list.Width = listW;
            list.Height = listH;
            Ui.At(root, list, PadExp, 46);

            int ver = Transfer.Version();
            RebuildTransferList(list, listW, listH, rowH, look, art, act);
            built.Live.Add(delegate(Snap n)
            {
                int v = Transfer.Version();
                if (v == ver) return;
                ver = v;
                RebuildTransferList(list, listW, listH, rowH, look, art, act);
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

        static void TransferZoneText(Canvas list, double listW, double listH, Look look,
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

        static void RebuildTransferList(Canvas list, double listW, double listH, double rowH,
                                        Look look, Art art, CardActions act)
        {
            list.Children.Clear();

            /* 拖动悬停：整块变成投放区 */
            if (Transfer.Hover)
            {
                Ui.At(list, DropZone(listW, listH, look, art, true), 0, 0);
                TransferZoneText(list, listW, listH, look, "松手暂存", "文件不会被复制，只记路径",
                                 Ui.AccInk(look, art), Ui.GFolder);
                return;
            }

            Transfer.View[] items = Transfer.Items();
            if (items.Length == 0)
            {
                Ui.At(list, DropZone(listW, listH, look, art, false), 0, 0);
                TransferZoneText(list, listW, listH, look, "把文件拖到这里暂存",
                                 "支持一次拖入多个文件或文件夹", look.Ink3, Ui.GFolder);
                return;
            }

            int show = items.Length < 6 ? items.Length : 6;
            for (int i = 0; i < show; i++)
            {
                Transfer.View v = items[i];
                string p = v.Path;

                Border row = Row(listW, rowH);
                /* 行的身份标记。窗口的右键 Preview 处理靠它让路给行菜单。
                   不用 Tag —— Tag 已经被 Row() 设成 "act"（防拖窗口）。 */
                row.Uid = "trow";
                Canvas inner = new Canvas();
                row.Child = inner;

                /* 图标：系统图标优先，取不到退回字形。丢失项一律走灰字形，
                   免得一个指向已删除文件的图标看起来还挺正常。 */
                bool iconDone = false;
                if (!v.Missing)
                {
                    System.Windows.Media.ImageSource ico = Transfer.Icon(p, v.Dir);
                    if (ico != null)
                    {
                        Image img = new Image();
                        img.Source = ico;
                        img.Width = 14;
                        img.Height = 14;
                        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                        Ui.At(inner, img, 0, 8);
                        iconDone = true;
                    }
                }
                if (!iconDone) Ui.At(inner, Ui.Glyph(Ui.GFolder, 12, look.Ink3), 0, 7);

                Color ink = v.Missing ? look.Ink3 : look.Ink1;
                TextBlock tb = Ui.Text(Transfer.Name(p), 14, FontWeights.Normal, ink, false);
                /* 右侧让出：尺寸 66 + 间隔 18 + 移除键 16 */
                tb.Width = listW - 22 - 84;
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
                Ui.At(inner, tb, 22, 5);

                TextBlock sz = Ui.Text(v.Missing ? "文件已丢失" : v.Size,
                                       12, FontWeights.Medium,
                                       v.Missing ? Ui.AccInk(look, art) : look.Ink3, true);
                sz.Width = 66;
                sz.TextAlignment = TextAlignment.Right;
                Ui.At(inner, sz, listW - 66 - 18, 7);

                /* 悬停出现的移除键。

                   标记用 Uid 而不是 Tag：OnTap 内部会把 Tag 覆写成 "act"
                   （那是给「按住按钮不拖窗口」用的），两套标记抢同一个属性
                   必然有一头失效。Uid 是空着的，正好用来放这个语义。 */
                FrameworkElement del = Ui.Glyph(Ui.GClose, 11, look.Ink3);
                del.Uid = "del";
                del.Width = 16;
                del.Height = 16;
                del.Visibility = Visibility.Collapsed;
                Ui.At(inner, del, listW - 16, 7);
                OnTap(del, delegate { if (act.TransferRemove != null) act.TransferRemove(p); },
                      "transfer-remove");

                row.MouseEnter += delegate { del.Visibility = Visibility.Visible; };
                row.MouseLeave += delegate { del.Visibility = Visibility.Collapsed; };

                WireTransferRow(row, inner, p, v.Missing, act);

                Ui.At(list, row, 0, i * (rowH + 4));
            }
        }

        /* 一行文件的三套手势：点开 / 按住拖出 / 右键菜单。

           为什么不用 OnTap：它是在 **MouseLeftButtonDown** 上触发的，
           那样「按住行开始拖出」会在按下的瞬间先打开文件，拖放根本起不来。
           所以这里手写 Down/Up/Move：Up 且没超过阈值才算点击。 */
        static void WireTransferRow(Border row, Canvas inner, string path, bool missing,
                                    CardActions act)
        {
            bool armed = false;
            bool dragged = false;
            Point down = new Point(0, 0);

            row.PreviewMouseLeftButtonDown += delegate(object s,
                System.Windows.Input.MouseButtonEventArgs e)
            {
                if (IsDel(e.OriginalSource)) { armed = false; return; }
                armed = true;
                dragged = false;
                down = e.GetPosition(row);
            };

            row.MouseMove += delegate(object s, System.Windows.Input.MouseEventArgs e)
            {
                if (!armed) return;
                if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed)
                {
                    armed = false;
                    return;
                }
                Point p = e.GetPosition(row);
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

            row.MouseLeftButtonUp += delegate(object s,
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

            row.MouseRightButtonUp += delegate(object s,
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

            double rowH = 30;
            double listW = w - PadExp * 2;
            Canvas list = new Canvas();
            list.Width = listW;
            list.Height = h - 46 - PadExp;
            Ui.At(root, list, PadExp, 46);

            int ver = Clips.Version();
            RebuildClipList(list, listW, rowH, look, act);
            built.Live.Add(delegate(Snap n)
            {
                int v = Clips.Version();
                if (v == ver) return;
                ver = v;
                RebuildClipList(list, listW, rowH, look, act);
                cnt.Text = Clips.Count() + " 条";
            });
        }

        static void RebuildClipList(Canvas list, double listW, double rowH, Look look,
                                    CardActions act)
        {
            list.Children.Clear();
            string[] items = Clips.Items();
            int show = items.Length < 6 ? items.Length : 6;

            if (show == 0)
            {
                TextBlock empty = Ui.Text("复制一段文字，就会出现在这里", 13, FontWeights.Normal,
                    look.Ink3, false);
                Ui.At(list, empty, 0, 4);
                return;
            }

            for (int i = 0; i < show; i++)
            {
                string txt = items[i].Replace("\r", " ").Replace("\n", " ").Trim();
                if (txt.Length > 90) txt = txt.Substring(0, 90) + "…";

                Border row = Row(listW, rowH);
                Canvas inner = new Canvas();
                row.Child = inner;

                Ui.At(inner, Ui.Glyph(Ui.GDoc, 12, look.Ink3), 0, 7);

                TextBlock tb = Ui.Text(txt, 14, FontWeights.Normal, look.Ink1, false);
                tb.Width = listW - 22;
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
                Ui.At(inner, tb, 22, 5);

                int idx = i;
                /* 先 At 再 OnTap：OnTap 会给元素套一层命中面（Squish.Wrap），
                   套的时候要读它现在的父容器和 Canvas.Left/Top。顺序反过来，
                   元素还没有父容器，就套不上命中面了（见 Squish.Wrap 的退路）。 */
                Ui.At(list, row, 0, i * rowH);
                OnTap(row, delegate { if (act.CopyClip != null) act.CopyClip(idx); });
            }
        }

        static void ExpandedTodo(Canvas root, Built built, double w, double h,
                                 Look look, Art art, Snap s, CardActions act)
        {
            /* 字号整体上调一档：岛不大，12px 在 1.5 缩放下确实吃力。
               头部 15、行 14.5、按钮 14。行高也从 22 提到 30，行与行不糊在一起。 */
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
                TextBlock due = Ui.Text(banner, 12.5, FontWeights.SemiBold,
                    Ui.AccInk(look, art), false);
                due.Width = w - PadExp * 2;
                due.TextTrimming = TextTrimming.CharacterEllipsis;
                Ui.At(root, due, PadExp, 40);
            }

            double listTop = banner.Length > 0 ? 66 : 46;
            double rowH = 30;
            double listW = w - PadExp * 2;
            Canvas list = new Canvas();
            list.Width = listW;
            list.Height = rowH * 5;
            Ui.At(root, list, PadExp, listTop);

            double by = listTop + rowH * 5 + 10;
            FrameworkElement add = Ui.TextButton("写一条", 30, 16, true, look, art);
            Ui.At(root, add, PadExp, by);
            OnTap(add, act.AddTodo);

            FrameworkElement clr = Ui.TextButton("清空已完成", 30, 14, false, look, art);
            Ui.At(root, clr, PadExp + 92, by);
            OnTap(clr, act.ClearDoneTodos);

            int ver = Todos.Version();
            RebuildTodoList(list, listW, rowH, look, art, act);
            built.Live.Add(delegate(Snap n)
            {
                int v = Todos.Version();
                if (v == ver) return;
                ver = v;
                RebuildTodoList(list, listW, rowH, look, art, act);
                cnt.Text = Todos.CountText();
            });
        }

        static void RebuildTodoList(Canvas list, double listW, double rowH, Look look, Art art,
                                    CardActions act)
        {
            list.Children.Clear();
            TodoItem[] items = Todos.Items();
            int show = items.Length < 5 ? items.Length : 5;

            if (show == 0)
            {
                TextBlock empty = Ui.Text("点「写一条」，直接在岛上写", 13,
                    FontWeights.Normal, look.Ink3, false);
                Ui.At(list, empty, 0, 4);
                return;
            }

            for (int i = 0; i < show; i++)
            {
                TodoItem it = items[i];
                Border row = Row(listW, rowH);
                Canvas inner = new Canvas();
                row.Child = inner;

                Color tick = it.Done ? Ui.AccInk(look, art) : look.Ink3;
                if (it.IsDue) tick = Ui.AccInk(look, art);
                Ui.At(inner, Ui.Glyph(it.Done ? "\uE73A" : "\uE739", 14, tick), 0, 6);

                /* 到点的条目加「到点」标签，并把正文提亮，别混在灰字里 */
                string dueTag = it.IsDue ? "到点" : it.DueText;
                bool hasTag = dueTag.Length > 0;
                double tagW = hasTag ? (it.IsDue ? 34 : Math.Min(70, dueTag.Length * 8 + 10)) : 0;

                Color fg = it.Done ? look.Ink3 : look.Ink1;
                if (it.IsDue) fg = Ui.AccInk(look, art);

                TextBlock tb = Ui.Text(it.Text, 14, FontWeights.Normal, fg, false);
                tb.Width = listW - 24 - tagW;
                tb.TextTrimming = TextTrimming.CharacterEllipsis;
                if (it.Done) tb.TextDecorations = TextDecorations.Strikethrough;
                Ui.At(inner, tb, 24, 5);

                if (hasTag)
                {
                    TextBlock tag = Ui.Text(dueTag, 11, FontWeights.Medium,
                        it.IsDue ? Ui.AccInk(look, art) : look.Ink3, true);
                    tag.Width = tagW;
                    tag.TextAlignment = TextAlignment.Right;
                    Ui.At(inner, tag, listW - tagW, 9);
                }

                int idx = i;
                /* 同上：先落位，再套命中面 */
                Ui.At(list, row, 0, i * rowH);
                OnTap(row, delegate { if (act.ToggleTodo != null) act.ToggleTodo(idx); });
            }
        }

    }
}
