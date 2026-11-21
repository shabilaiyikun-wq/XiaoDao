using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinIsland
{
    /* ══ 待办塔（岛内视图）════════════════════════════════════════════════

       把 Bencho 的 Todo Tower 那套交互接到岛上的待办页：
         · 待办是**纸片**，不是行；它们叠成塔站着，由真物理撑着（Tower.cs）
         · 勾掉一条 → 那张纸片被抽走，上面那些失去支撑、在重力下掉下来
         · 按住一张纸片拖走 → 它撞开别的、被别的挡住；松手带着手上的速度
           飞出去或落回塔上
         · 把纸片拖到哪儿都不会丢：房间的四壁是实体，位置每帧都会夹回房间
           内，数值一旦不是有限值就被放回天花板下面（见 Tick 里的兜底）

       ── 和参考实现的差别（都是「岛和浏览器不一样」逼出来的）─────────────
       1. 卡片**必须明显小于房间**，两个方向都是。参考里卡片 206 宽、房间
          300（0.69），两侧各留 47px；一开始这里把卡片做成「房间宽减 24」，
          纸片几乎贴着墙，左右只能挪 12px、塔顶上面也只有 10px —— 那不是
          「能拖」，那是一摞钉死的行。现在的取值见 CardWidthRatio / Headroom：
          两侧各 65px、塔顶上方 24px，握着它横竖都走得动。
          代价是正文比原来早 3 个字换省略号。要让字更长只能把面板加宽，
          那是另一个决定（会改岛的footprint），不夹在这里做。
       2. 参考里只有一摞「未完成」。岛上原来那一页把已完成的也列出来、
          点一下能改回来，清空已完成的按钮也在。为了不砍掉这个能力，
          这里保留了一个「已完成 N / 返回待办」的切换：两种视图是同一座塔，
          同一套物理，只是取哪几条待办做纸片不同。
       3. 参考的尺寸/重力是在 46 高的卡片上调的，这里按 K = 卡片高/46 整
          体缩放（见 Tower.cs 文件头），手感一致。 */

    /// <summary>岛上待办页的那座塔：版面、纸片视觉、指针交互、帧循环。</summary>
    class TodoTower
    {
        /* 一页最多几张。原来是「最多列 5 行」，这里保持 5 张纸片。 */
        public const int Capacity = 5;

        /* ── 几何：卡片多宽 ──────────────────────────────────────────────
           卡片的宽度**跟着它自己那条待办的正文走**：一条「续域名」是一张
           几指宽的小纸条，一条长待办是一张长条。固定宽度会把短待办撑成
           一大片空白，也会让所有纸片长得一样 —— 那是表格行，不是纸条。
           高度仍然统一（叠塔需要一致的高度，否则摞不齐）。

           上下限：
             · 太窄就不像卡片了（勾选方块 + 几个字）：MinCardW；
             · 太宽就横move 不动了（会被两边的墙卡住）：宽的一侧必须
               仍然留在房间里，见 MinSideRoom。 */
        const double MinCardW = 116;

        /* 纸片最宽时，两侧各至少留这么多：这是「能左右拖」的硬约束，
           不是审美。 */
        const double MinSideRoom = 44;

        /* 塔顶到天花板之间留的高度：手能把手里的卡片往上举多高。
           不留这段，卡片一拿起来就顶到天花板，纵向同样拖不动。 */
        const double Headroom = 24;

        /* 新条目从天花板落进来时离顶多远 */
        const double TopInset = 8;

        /* ── 固定的时间步 ────────────────────────────────────────────
           求解器按 240Hz 走，不管屏幕多少帧。一摞东西是靠冲量互相顶住的，
           步长了，一张纸片一步就能陷进去很深，求解器要花十步把它推回来 ——
           那就是一座在抖的塔。步子小，它才站得住。 */
        const double Dt = 1.0 / 240.0;
        const int MaxSteps = 16;

        /* 每一个都能慢到这个程度、并持续这么多步，就算静止 */
        const double RestV = 6;
        const double RestW = 0.04;
        const int RestN = 60;

        /* 指针移动多少像素才算「拿起来」，而不是「点了一下」 */
        const double Grab = 4;
        const double VmaxBase = 1800;
        const double ScrapeBase = 150;

        /* 写死的歪斜，不是随机：每一份塔都要画出同一座站着的塔 */
        static readonly double[] Lean = new double[] { -0.42, 0.68, -0.15, 0.51, -0.86 };

        /* 勾掉之后往外滑的那一下（秒） */
        const double OutLife = 0.24;

        /// <summary>一张纸片的视觉与它在数据里的身份。</summary>
        class Card
        {
            public int BodyId;
            public string Key;        /* 待办正文；Todos.Add 去重，所以它就是唯一键 */
            public int Index;         /* 在 Todos.Items() 里的下标 */
            public double W, H;       /* 这张纸片自己的尺寸（宽随正文走） */
            public double Hw { get { return W / 2; } }
            public double Hh { get { return H / 2; } }
            public Border Element;
            public TextBlock Label;
            public TextBlock Tag;
            public Border Check;
            public TextBlock CheckGlyph;
            public RotateTransform Spin;
        }

        /// <summary>正在往外滑的那张（已经从世界里摘掉了）。</summary>
        class Going
        {
            public Card View;
            public double X, Y, A;
            public double T;
        }

        /// <summary>指针抓着的那张。</summary>
        class Grip
        {
            public int Id;
            public double Lx, Ly;     /* 抓在纸片的哪个位置（纸片自身坐标） */
            public double Ox, Oy;     /* 按下时的画布坐标，用来判断有没有真的拖起来 */
            public double Tx, Ty;     /* 目标（世界坐标） */
            public double Vx, Vy;     /* 指针速度（世界 px/s，已平滑） */
            public double T;          /* 上一次事件的时刻（秒） */
            public bool Live;
            public Card View;
        }

        readonly Canvas _room = new Canvas();
        readonly double _roomW, _roomH;
        readonly double _cardH, _hh;      /* 高度统一 */
        readonly double _minW, _maxW;      /* 宽度的上下限（每张卡自己算） */
        readonly double _k;            /* 尺寸缩放，见 Tower.cs 文件头 */
        readonly double _friction;
        readonly double _gravity;
        readonly double _slip = 0.45;

        readonly Look _look;
        readonly Art _art;
        readonly CardActions _act;

        readonly TowerWorld _world = new TowerWorld();
        readonly TowerRoom _bounds = new TowerRoom();
        readonly List<Card> _cards = new List<Card>();
        readonly List<Going> _going = new List<Going>();
        readonly TextBlock _hint;

        readonly Random _rand = new Random(20260101);

        Grip _grip;
        int _hand = -1;
        int _nextId = 1;
        bool _doneMode;
        bool _stopped;

        /* 帧循环 */
        bool _running;
        readonly System.Diagnostics.Stopwatch _clock = new System.Diagnostics.Stopwatch();
        double _lastT;
        double _acc;
        int _calm;
        double _lastHitMs = -10000;

        public TodoTower(double roomW, double roomH, Look look, Art art,
                         CardActions act, bool doneMode)
        {
            _roomW = roomW;
            _roomH = roomH;
            _look = look;
            _art = art;
            _act = act;
            _doneMode = doneMode;

            /* 卡片高：把房间高度分给容量，但**先扣掉塔顶上方那段空气**。
               不扣的话塔会顶到天花板（5 张 28 高 = 140，房间 150，只剩 10px），
               手里的卡片一抬就撞顶，纵向也拖不动。
               5 张、房间 156、留 24 → (156-24)/5 = 26。 */
            double ch = Math.Floor((roomH - Headroom) / Capacity);
            if (ch < 16) ch = 16;
            if (ch > 34) ch = 34;
            _cardH = ch;
            /* 宽度的上下限。上限由「拖得动」定，不是由「能塞多少字」定：
               最宽的纸片也要在两侧各留下 MinSideRoom，否则它会被墙夹住。 */
            _maxW = Math.Floor(roomW - MinSideRoom * 2);
            _minW = Math.Min(MinCardW, _maxW);
            _hh = _cardH / 2;
            _k = _cardH / 46.0;
            _friction = 0.85 - _slip * 0.65;
            _gravity = (900 + 50 / 100.0 * 3300) * _k;

            _world.K = _k;
            _world.Gravity = _gravity;
            _world.Restitution = 0;      /* 砖头落地就是落地，不弹 */

            _bounds.L = -roomW / 2;
            _bounds.R = roomW / 2;
            _bounds.Floor = 0;
            _bounds.Top = roomH;

            _room.Width = roomW;
            _room.Height = roomH;
            /* 背景留空：房间本身不吃点击，点空白处仍然是「拖走整座岛」。
               纸片自己有底色，所以只有纸片能被按到。 */
            _room.Background = null;

            _hint = Ui.Text("", 13, FontWeights.Normal, _look.Ink3, false);
            _hint.Width = roomW;
            _hint.TextAlignment = TextAlignment.Center;
            _hint.IsHitTestVisible = false;

            Build();
        }

        /// <summary>带 alpha 的同色：Ui.AB 给的是画刷，字形/描边那里要的是 Color。</summary>
        static Color Fade(Color c, double a)
        {
            if (a < 0) a = 0;
            if (a > 1) a = 1;
            return Color.FromArgb((byte)Math.Round(255 * a), c.R, c.G, c.B);
        }

        /// <summary>一行字的自然宽度（不限宽、不换行时它自己要占多宽）。</summary>
        static double MeasureW(TextBlock t)
        {
            t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = t.DesiredSize.Width;
            /* 量之前先量一次空的：TextBlock 在没进可视树时 DesiredSize 可能是
               0（字体回退要现场解析），再量一次就稳了。 */
            if (w <= 0)
            {
                t.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                w = t.DesiredSize.Width;
            }
            return w > 0 ? w : 0;
        }

        /* 强调色压在灰条上：Art 备了两种强调色 —— AccInk（当文字用的浅版）
           和 AccOn（实心面上的深版）。哪一种读得清，取决于纸片是灰底还是
           浅底，而不是取决于外观名字：frost 的灰条是浅的，浅紫的 AccInk
           压上去对比度只有 1.38，等于看不见「到点」这两个字。
           所以按灰条的亮度选，不写死某一种外观。 */
        Color AccOnBar()
        {
            Color bg = _look.CardBg;
            /* 相对亮度的粗略版够了：这里只需要判断「浅底还是深底」 */
            double lum = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
            return lum > 0.5 ? _art.AccOn : _art.AccInk;
        }

        /// <summary>房间画布：交给 Layout 定位到列表区。</summary>
        public Canvas Root { get { return _room; } }

        /// <summary>当前是「已完成」视图。</summary>
        public bool DoneMode { get { return _doneMode; } }

        /// <summary>世界里的纸片数（不含正在滑出的那张）。</summary>
        public int CardCount { get { return _cards.Count; } }

        /// <summary>静止了吗（帧循环已经停帧）。</summary>
        public bool Calm { get { return !_running; } }

        /// <summary>房间尺寸（自检用：别让测试自己复述 Layout 的算式）。</summary>
        public double RoomWidth { get { return _roomW; } }
        public double RoomHeight { get { return _roomH; } }

        /// <summary>手里有东西吗（自检用：拖拽状态机的可观察输出）。</summary>
        public bool HandVisible { get { return _hand >= 0; } }

        /// <summary>自检用：直接推进 n 步物理（不依赖帧回调）。</summary>
        public void SettleStepForTest(int steps)
        {
            if (_stopped) return;
            for (int i = 0; i < steps; i++)
            {
                Grip g = _grip;
                if (g != null && g.Live)
                {
                    TowerBody b = Tower.Find(_world, g.Id);
                    if (b != null)
                    {
                        double co = Math.Cos(b.A), si = Math.Sin(b.A);
                        double px = g.Tx - (co * g.Lx - si * g.Ly);
                        double py = g.Ty - (si * g.Lx + co * g.Ly);
                        Tower.Steer(_world, b, px, py, Dt, g.Vx, g.Vy);
                    }
                }
                Tower.Step(_world, Dt, OnHit);
            }
            Draw();
        }

        /// <summary>已经完成、还没被清掉的条数。</summary>
        public int DoneCount()
        {
            TodoItem[] items = Todos.Items();
            int n = 0;
            for (int i = 0; i < items.Length; i++) if (items[i].Done) n++;
            return n;
        }

        /* ── 搭塔 ────────────────────────────────────────────────────
           一次把当前视图里的待办全摆进来：最上面那张是列表里最老的一条
           （和原来的行序一致，读起来不用换脑子）。 */
        void Build()
        {
            TodoItem[] items = Todos.Items();
            List<int> want = Wanted(items);
            _world.Bodies.Clear();
            _world.Arbiters.Clear();
            _world.Held = -1;
            _room.Children.Clear();
            _cards.Clear();
            _going.Clear();
            _hand = -1;
            _grip = null;

            /* 墙、地板、天花板是世界的一部分，刚被清掉，必须在这里重新放好；
               不放的话纸片会穿过地板一直往下掉 —— 而「找不到」正是最难查的
               那种丢卡片。Walls 是幂等的：位置没变就不动，接触记忆保住。
               这里的 hw/hh 只用于「把越界的纸片放回房间」，此刻还没有纸片，
               所以传最宽的那一档就够。 */
            Tower.Walls(_world, _bounds, _friction, _maxW / 2, _hh);

            int n = want.Count;
            for (int slot = 0; slot < n; slot++)
            {
                TodoItem it = items[want[slot]];
                /* TODOS[0] 先读，所以它是最上面那张 */
                double x = Lean[slot % Lean.Length] * _slip * 16 * _k;
                double y = _hh + (n - 1 - slot) * _cardH;
                AddCard(it, want[slot], x, y, 0, slot == 0);
            }

            ShowHint();
            Draw();
            Start();
        }

        /// <summary>当前视图要显示的下标（最多 Capacity 个），保持数据顺序。</summary>
        List<int> Wanted(TodoItem[] items)
        {
            List<int> want = new List<int>();
            for (int i = 0; i < items.Length && want.Count < Capacity; i++)
                if (items[i].Done == _doneMode) want.Add(i);
            return want;
        }

        Card AddCard(TodoItem it, int index, double x, double y, double a, bool top)
        {
            Card c = new Card();
            c.BodyId = _nextId++;
            c.Key = it.Text;
            c.Index = index;
            c.Element = BuildCardVisual(c, it);      /* 里面会定下 c.W / c.H */
            c.Spin = new RotateTransform(0, c.Hw, c.Hh);
            c.Element.RenderTransform = c.Spin;

            TowerBody b = Tower.Box(c.BodyId, x, y, c.Hw, c.Hh, _friction, 1);
            b.A = a;
            _world.Bodies.Add(b);

            _cards.Add(c);
            _room.Children.Add(c.Element);
            if (top) BringToFront(c);
            return c;
        }

        /* 纸片长什么样。参考里的砖是「一块平的石板 + 一条细边」，
           没有任何阴影：六块一模一样的矩形叠在一起时，把边和影留给
           一整套阴影去画，反而会在深色外观下变成一片发白的边。

           长度跟着正文走：先造出那行字、量出它的自然宽度，再决定这张纸片
           多宽。所以顺序是「字 → 宽度 → 卡片」，不是「宽度 → 截字」。 */
        Border BuildCardVisual(Card c, TodoItem it)
        {
            double box = Math.Min(17, _cardH - 9);
            double textX = 7 + box + 8;
            double rightPad = 10;

            string dueTag = it.IsDue ? "到点" : it.DueText;
            bool hasTag = dueTag.Length > 0 && !it.Done;
            double tagW = hasTag ? (it.IsDue ? 34 : Math.Min(66, dueTag.Length * 8 + 8)) : 0;

            /* 实心灰条上的字：Ink3 是给半透明面板准备的（48~52% alpha），压在
               一条中灰色上会糊掉；已完成用 Ink2，未完成用 Ink1。 */
            Color fg = it.IsDue ? AccOnBar()
                        : (it.Done ? _look.Ink2 : _look.Ink1);
            TextBlock label = Ui.Text(it.Text, 13, FontWeights.Normal, fg, false);
            if (it.Done) label.TextDecorations = TextDecorations.Strikethrough;

            /* 量自然宽度：Ui.Text 是 NoWrap 且不带 Width，所以 DesiredSize
               就是这行字自己的宽度（含字体自带的左右留白）。 */
            double textW = Math.Ceiling(MeasureW(label));
            double want = textX + textW + rightPad + tagW;
            double cardW = Math.Round(Tower.Clamp(want, _minW, _maxW));
            if (cardW < _minW) cardW = _minW;
            if (cardW > _maxW) cardW = _maxW;

            c.W = cardW;
            c.H = _cardH;

            Border b = new Border();
            b.Width = cardW;
            b.Height = _cardH;
            /* 实心灰条：不透明、圆角小一点。半透明薄板叠在一起时靠描边分辨，
               实心的靠颜色本身分辨；圆角大了会让一摞变成一串珠子。 */
            b.CornerRadius = new CornerRadius(Math.Min(6, _cardH * 0.24));
            b.Background = new SolidColorBrush(_look.CardBg);
            b.BorderThickness = new Thickness(1);
            b.BorderBrush = new SolidColorBrush(_look.CardEdge);
            b.Tag = "act";                       /* 按纸片不要拖动整个岛 */
            b.Cursor = Cursors.Hand;
            /* 稳定标记：验收脚本靠它认出「这是纸片」，不要去比宽度 ——
               几何一调，按宽度匹配的测试就会静悄悄地匹配不到任何东西，
               然后一路报「通过」。 */
            b.Uid = "tw-card";

            Canvas inner = new Canvas();
            inner.Width = cardW;
            inner.Height = _cardH;
            b.Child = inner;

            /* ── 勾选方块 ────────────────────────────────────────────
               参考里这个方块故意做得很安静：这一块讲的是「别的东西怎么了」，
               不是这个方块自己的动效。 */
            Border check = new Border();
            check.Width = box;
            check.Height = box;
            check.CornerRadius = new CornerRadius(5);
            check.BorderThickness = new Thickness(1.4);
            /* 灰条上的方框要比面板上的实一点，否则陷进灰色里看不见 */
            check.BorderBrush = Ui.AB(_look.Ink1, 0.45);
            check.Background = Brushes.Transparent;
            check.Tag = "act";
            check.Cursor = Cursors.Hand;
            TextBlock glyph = Ui.Glyph(it.Done ? "\uE73A" : "\uE739",
                                       box * 0.72, Fade(_look.Ink1, 0.86));
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            glyph.VerticalAlignment = VerticalAlignment.Center;
            check.Child = glyph;
            check.Visibility = Visibility.Visible;

            Ui.At(inner, check, 7, (_cardH - box) / 2);
            c.Check = check;
            c.CheckGlyph = glyph;

            /* ── 正文 ──────────────────────────────────────────────── */
            double labelW = cardW - textX - rightPad - tagW;
            label.Width = labelW;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.IsHitTestVisible = false;      /* 按在字上也算按在纸片上 */
            Ui.At(inner, label, textX, Math.Round((_cardH - 17) / 2));
            c.Label = label;

            if (hasTag)
            {
                TextBlock tag = Ui.Text(dueTag, 11, FontWeights.Medium,
                    it.IsDue ? AccOnBar() : _look.Ink2, true);
                tag.Width = tagW;
                tag.TextAlignment = TextAlignment.Right;
                tag.IsHitTestVisible = false;
                Ui.At(inner, tag, cardW - tagW - 9, Math.Round((_cardH - 15) / 2));
                c.Tag = tag;
            }

            if (it.IsDue)
            {
                /* 到点这条仍然是实心灰条 —— 一摞里突然冒出一块彩色会显得
                   像是别的东西混进来了。改成镶一道强调色的脊：一眼能认出
                   是哪一条，但它还是一张纸。 */
                b.BorderBrush = new SolidColorBrush(AccOnBar());
                b.BorderThickness = new Thickness(1.6);
            }

            /* 按纸片 = 拿起来；按方块 = 勾掉。方块先吃到事件并标记已处理，
               于是纸片上的「拿起」不会跟着触发。 */
            check.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ChangedButton != MouseButton.Left) return;
                e.Handled = true;
                Strike(c);
            };

            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                Take(c, e);
            };

            return b;
        }

        /* ── 数据变了：对账 ──────────────────────────────────────────
           勾走一张、加进一条、清空已完成 —— 数据层的版本号变了就走到这里。

           对账而不是重建：世界（和它的接触记忆）必须活着，否则「勾掉一张、
           上面那些砸下来」会变成「整座塔重置」。 */
        public void Refresh()
        {
            if (_stopped) return;

            TodoItem[] items = Todos.Items();
            List<int> want = Wanted(items);
            bool changed = false;

            /* 要显示的正文集合 */
            HashSet<string> keep = new HashSet<string>();
            for (int i = 0; i < want.Count; i++) keep.Add(items[want[i]].Text);

            /* 1) 数据里已经没有这张了 → 立刻摘掉（不播滑出动画：它本来
                  就不是被勾掉的，是数据变了） */
            for (int i = _cards.Count - 1; i >= 0; i--)
            {
                Card c = _cards[i];
                if (keep.Contains(c.Key)) continue;
                if (_hand == c.BodyId) EndGrip(false);
                Tower.Remove(_world, c.BodyId);
                _room.Children.Remove(c.Element);
                _cards.RemoveAt(i);
                changed = true;
            }

            /* 2) 留下来的：下标可能变了（清空已完成会让下标整体前移），
                  正文也可能改了，都按正文重新认一次 */
            for (int slot = 0; slot < want.Count; slot++)
            {
                TodoItem it = items[want[slot]];
                Card c = FindCard(it.Text);
                if (c == null) continue;
                c.Index = want[slot];
                c.Label.Text = it.Text;
                c.Label.Foreground = Ui.Br(it.IsDue ? AccOnBar()
                                               : (it.Done ? _look.Ink2 : _look.Ink1));
                if (it.Done) c.Label.TextDecorations = TextDecorations.Strikethrough;
                else c.Label.TextDecorations = null;
                c.CheckGlyph.Text = it.Done ? "\uE73A" : "\uE739";
                if (c.Tag != null) c.Tag.Text = it.DueText;
            }

            /* 3) 新来的：从天花板下掉进来 */
            for (int slot = 0; slot < want.Count; slot++)
            {
                TodoItem it = items[want[slot]];
                if (FindCard(it.Text) != null) continue;
                if (IsGoing(it.Text)) continue;          /* 刚勾走、还在往外滑 */

                double x = Lean[slot % Lean.Length] * _slip * 16 * _k;
                double y = _roomH - _hh - TopInset / 2;
                TowerBody top = TopBody();
                if (top != null)
                {
                    double above = top.Y + _hh + _cardH * 0.55;
                    if (above < y) y = above;
                }
                Card c = AddCard(it, want[slot], x, y, 0, true);
                /* 落下之前给一点点旋转，别像「插进去」 */
                TowerBody b = Tower.Find(_world, c.BodyId);
                if (b != null) b.A = (slot % 2 == 0 ? 1 : -1) * 0.012;
                changed = true;
            }

            /* 结构没变（只是正文/标签要对一下）就别去吵醒一座已经静止的塔 */
            if (!changed) return;

            ShowHint();
            Draw();
            Start();
        }

        Card FindCard(string key)
        {
            for (int i = 0; i < _cards.Count; i++)
                if (_cards[i].Key == key) return _cards[i];
            return null;
        }

        bool IsGoing(string key)
        {
            for (int i = 0; i < _going.Count; i++)
                if (_going[i].View.Key == key) return true;
            return false;
        }

        TowerBody TopBody()
        {
            TowerBody top = null;
            for (int i = 0; i < _world.Bodies.Count; i++)
            {
                TowerBody b = _world.Bodies[i];
                if (b.Id >= Tower.IdFloor) continue;
                if (top == null || b.Y > top.Y) top = b;
            }
            return top;
        }

        /* ── 视图切换：待办 / 已完成 ─────────────────────────────────
           两座塔共用一份世界逻辑，切的时候干脆重搭一座（而不是把纸片
           一张张换掉）：整摞换掉的那一下本来就该是「换了一摞东西」。 */
        public void ToggleMode()
        {
            _doneMode = !_doneMode;
            Build();
        }

        /* ── 勾掉一张 ────────────────────────────────────────────────
           顺序很要紧：先把世界里的它摘掉（上面那些当场失去支撑），
           再写数据。数据一改，IslandWindow 会同步跑一次 RefreshNow，
           那一路会回到 Refresh() —— 那时候这张已经在 _going 里，不会再被
           当成「新来的」塞回塔上。 */
        void Strike(Card c)
        {
            if (_stopped) return;
            if (c == null || !_cards.Contains(c)) return;
            if (_hand >= 0) return;                  /* 手里有东西时不勾 */
            for (int i = 0; i < _going.Count; i++)
                if (_going[i].View == c) return;      /* 同一张正在滑出 */

            TowerBody b = Tower.Find(_world, c.BodyId);
            if (b == null) return;

            /* 勾的是「数据里的那一条」。下标当场按正文重新解析一次：
               对账可能还没来得及跑，用旧的 Index 会勾错人。 */
            TodoItem[] items = Todos.Items();
            int index = -1;
            for (int i = 0; i < items.Length; i++)
                if (items[i].Text == c.Key) { index = i; break; }
            if (index < 0) return;

            Going g = new Going();
            g.View = c;
            g.X = b.X;
            g.Y = b.Y;
            g.A = b.A;
            g.T = 0;
            _going.Add(g);

            Scrape(b);
            Tower.Remove(_world, c.BodyId);
            _cards.Remove(c);

            /* 勾选动作本身还是数据层那一个（Todos.Toggle） */
            if (_act != null && _act.ToggleTodo != null) _act.ToggleTodo(index);

            ShowHint();
            Start();
        }

        /* 一张纸片离开时，压在它上面的那些会被带一下 —— 砖头离开砖堆
           从来不是彬彬有礼的 */
        void Scrape(TowerBody gone)
        {
            double scrape = ScrapeBase * _k;
            for (int i = 0; i < _world.Bodies.Count; i++)
            {
                TowerBody c = _world.Bodies[i];
                if (c.Id >= Tower.IdFloor) continue;
                if (c.Id == gone.Id) continue;
                if (c.Y <= gone.Y) continue;
                double far = Math.Max(1, (c.Y - gone.Y) / _cardH);
                c.Vx += (_rand.NextDouble() * 2 - 1) * (0.3 + _slip) * scrape / far;
            }
        }

        /* ── 手 ──────────────────────────────────────────────────────
           下面三个 *Grip* 方法就是这套交互的全部状态机：拿起、移动、
           松手。鼠标事件只是把它们接上指针（Take/OnGripMove/OnGripUp 三个
           适配器各两行）。这样分不是为了好看 —— 事件适配器除了
           「取一下指针坐标」之外没有逻辑，自检就能在没有鼠标的情况下
           走完同一条路径，验证「拖动+放手不会把纸片弄丢、也不会改动数据」。 */

        void Take(Card c, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            /* 按下就是「我要动它」，但还没改世界 —— 到底是不是拖起来，
               要等指针真的移动了 4px 才知道（轻点一下不该把塔端起来） */
            Point p = e.GetPosition(_room);
            if (!GrabAt(c, p.X, p.Y)) return;

            /* 捕获与事件挂的是鼠标那一层，状态机在上面三个方法里。
               捕获是为了「拖到窗口外面再松手」也收得到 up。 */
            c.Element.CaptureMouse();
            c.Element.MouseMove += OnGripMove;
            c.Element.MouseLeftButtonUp += OnGripUp;
            c.Element.LostMouseCapture += OnGripLost;
            e.Handled = true;
        }

        /// <summary>自检用：按在第 slot 张纸片上（画布坐标）。</summary>
        public bool GrabSlot(int slot, double px, double py)
        {
            if (slot < 0 || slot >= _cards.Count) return false;
            return GrabAt(_cards[slot], px, py);
        }

        bool GrabAt(Card c, double px, double py)
        {
            if (_stopped) return false;
            if (_grip != null || _hand >= 0) return false;
            if (c == null || !_cards.Contains(c)) return false;

            TowerBody b = Tower.Find(_world, c.BodyId);
            if (b == null) return false;

            double wx = px - _roomW / 2;
            double wy = _roomH - py;

            Grip g = new Grip();
            g.Id = c.BodyId;
            g.View = c;
            g.Ox = px; g.Oy = py;
            g.Tx = wx; g.Ty = wy;
            g.T = Now();

            /* 抓在纸片的哪个位置：拿住角上和拿住中间，甩起来不一样 */
            double co = Math.Cos(-b.A), si = Math.Sin(-b.A);
            double dx = wx - b.X, dy = wy - b.Y;
            g.Lx = co * dx - si * dy;
            g.Ly = si * dx + co * dy;
            _grip = g;
            return true;
        }

        void OnGripMove(object sender, MouseEventArgs e)
        {
            MoveGripAt(e.GetPosition(_room).X, e.GetPosition(_room).Y);
        }

        /// <summary>指针移到（画布坐标）。超过 4px 才算把它端起来。</summary>
        public void MoveGripAt(double px, double py)
        {
            Grip g = _grip;
            if (g == null) return;

            /* 夹回房间用的是**这张纸片自己**的半宽半高：纸条长短不一，
               拿统一尺寸夹会让长纸条穿墙、短纸条凭空停在半空。 */
            Card gc = g.View;
            double ghw = gc != null ? gc.Hw : _maxW / 2;
            double ghh = gc != null ? gc.Hh : _hh;

            double l = _bounds.L + ghw, r = _bounds.R - ghw;
            double fl = _bounds.Floor + ghh, tp = _bounds.Top - ghh;
            double wx = Tower.Clamp(px - _roomW / 2, l, r);
            double wy = Tower.Clamp(_roomH - py, fl, tp);

            double now = Now();
            double dt = now - g.T;
            if (dt < 0.004) dt = 0.004;
            /* 平滑：指针事件来得不均匀，一次迟到的事件会被读成「突然停下」 */
            g.Vx = g.Vx * 0.5 + ((wx - g.Tx) / dt) * 0.5;
            g.Vy = g.Vy * 0.5 + ((wy - g.Ty) / dt) * 0.5;
            g.T = now;
            g.Tx = wx;
            g.Ty = wy;

            if (!g.Live)
            {
                if (Tower.Hypot(px - g.Ox, py - g.Oy) < Grab) return;
                g.Live = true;
                _hand = g.Id;

                TowerBody b = Tower.Find(_world, g.Id);
                if (b != null)
                {
                    /* ── 在手里它是「重」，不是「不可动」──────────────
                       不能设成 0 质量：那样手就成了不可阻挡的推土机，端着一张
                       纸片从塔脚扫过去，会把整摞铲起来扔掉。四倍重，速度还是
                       朝指针设的 —— 它照样能开路，但撞到的纸片是被推开的，
                       不是被发射的；地板和墙也照样挡得住它。 */
                    Tower.SetMass(b, 4);

                    /* ── 它跟墙滑，跟纸片抓 ─────────────────────────
                       世界知道谁在手里，用它让手里的那张对地板和墙没有摩擦
                       （竖着被抬起来时不会卡在墙上），而骑在它上面的纸片
                       抓得很牢、跟着走（Tower.Step 里那段 CARRY）。 */
                    _world.Held = g.Id;

                    /* ── 回到水平的最短那条路 ────────────────────────
                       翻滚过的纸片可能带着好几圈的角度，直接抹平会让它转着
                       穿过整堆，看着像「这张纸片取不下来」。折到半圈之内：
                       侧躺的转四分之一，倒扣的转半圈。抓点也要跟着在折过的
                       那一帧里重读，否则纸片会在手底下跳一下。 */
                    double wrapped = b.A - Math.Round(b.A / (2 * Math.PI)) * 2 * Math.PI;
                    b.A = wrapped;

                    Scrape(b);
                    BringToFront(g.View);
                }
                Start();
            }
        }

        void OnGripUp(object sender, MouseButtonEventArgs e)
        {
            EndGrip(true);
        }

        void OnGripLost(object sender, MouseEventArgs e)
        {
            /* 捕获丢了（点到了别处 / 窗口失活）：按松手处理，纸片绝不会
               卡在「手里」这个状态里出不来 */
            EndGrip(true);
        }

        public void EndGrip(bool release)
        {
            Grip g = _grip;
            _grip = null;
            if (g == null) return;

            Card c = g.View;
            if (c != null)
            {
                c.Element.MouseMove -= OnGripMove;
                c.Element.MouseLeftButtonUp -= OnGripUp;
                c.Element.LostMouseCapture -= OnGripLost;
                if (c.Element.IsMouseCaptured) c.Element.ReleaseMouseCapture();
            }

            if (!g.Live)
            {
                _hand = -1;
                return;
            }

            _hand = -1;
            _world.Held = -1;

            TowerBody b = Tower.Find(_world, g.Id);
            if (b != null)
            {
                /* 重量还回去，速度留着 —— 甩一下就是扔出去 */
                Tower.SetMass(b, 1);
                double vmax = VmaxBase * _k;
                b.Vx = Tower.Clamp(b.Vx, -vmax, vmax);
                b.Vy = Tower.Clamp(b.Vy, -vmax, vmax);
                b.W = Tower.Clamp(b.W, -6, 6);
                /* 兜底：任何时候松手，纸片都必须在房间里（用它自己的宽高） */
                b.X = Tower.Clamp(b.X, _bounds.L + b.Hw, _bounds.R - b.Hw);
                b.Y = Tower.Clamp(b.Y, _bounds.Floor + b.Hh, _bounds.Top - b.Hh);
                if (!b.Finite)
                {
                    b.X = 0; b.Y = _bounds.Top - b.Hh; b.A = 0;
                    b.Vx = 0; b.Vy = 0; b.W = 0;
                }
                else if (!release)
                {
                    /* 不是「松手」，而是这张脸被摘掉了（换层 / 换外观）：
                       别再让它带着手上的速度飞出去，安静地停在那儿 */
                    b.Vx = 0; b.Vy = 0; b.W = 0;
                }
            }
            Draw();
            if (release) Start();
        }

        void BringToFront(Card c)
        {
            if (c == null || c.Element == null) return;
            if (!_room.Children.Contains(c.Element)) return;
            _room.Children.Remove(c.Element);
            _room.Children.Add(c.Element);
        }

        /* ── 帧循环 ──────────────────────────────────────────────────
           参考用 requestAnimationFrame，并在「所有东西都慢下来了」之后
           自己停帧；WPF 这边同样挂在合成器的帧事件上，静止即摘掉回调，
           岛不因为一座站着不动的塔而一直重绘。 */
        void Start()
        {
            if (_stopped || _running) return;
            _running = true;
            _calm = 0;
            _acc = 0;
            if (!_clock.IsRunning) _clock.Start();
            _lastT = Now();
            CompositionTarget.Rendering += Tick;
        }

        void StopLoop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= Tick;
        }

        /// <summary>卸载（换层/换外观把这张脸摘掉时调用）。</summary>
        public void Stop()
        {
            _stopped = true;
            EndGrip(false);
            StopLoop();
            _clock.Stop();
        }

        double Now()
        {
            return _clock.Elapsed.TotalSeconds;
        }

        void Tick(object sender, EventArgs e)
        {
            if (_stopped) { StopLoop(); return; }

            double t = Now();
            double dt = t - _lastT;
            _lastT = t;
            if (dt < 0) dt = 0;
            if (dt > MaxSteps * Dt) dt = MaxSteps * Dt;

            Grip g = _grip;

            /* 手还按着，但按键其实早松了（在窗口外面松的手、或者捕获丢了）：
               按松手处理，绝不让纸片停在「手里」这个状态 */
            if (g != null && !Native.LeftButtonDown()) EndGrip(true);

            g = _grip;
            if (g != null)
            {
                /* 停住的指针不发任何事件，它最后那个速度会被一直留着，
                   把纸片带过一个已经不动的手 */
                if (t - g.T > 0.034)
                {
                    g.Vx *= 0.4;
                    g.Vy *= 0.4;
                }
            }

            _acc += dt;
            int steps = 0;
            while (_acc >= Dt && steps < MaxSteps)
            {
                if (g != null && g.Live)
                {
                    TowerBody b = Tower.Find(_world, g.Id);
                    if (b != null)
                    {
                        /* 手把纸片引到「指针抓着它的那个位置」：拉扯有上限，
                           而且不会继续往它已经顶住的东西里推 */
                        double co = Math.Cos(b.A), si = Math.Sin(b.A);
                        double px = g.Tx - (co * g.Lx - si * g.Ly);
                        double py = g.Ty - (si * g.Lx + co * g.Ly);
                        Tower.Steer(_world, b, px, py, Dt, g.Vx, g.Vy);
                    }
                }

                Tower.Step(_world, Dt, OnHit);
                _acc -= Dt;
                steps++;
            }

            /* 数值兜底：真有哪张纸片算出非有限值，就把它放回天花板下面，
               而不是让它「画不出来、也再也拿不起来」 */
            for (int i = 0; i < _world.Bodies.Count; i++)
            {
                TowerBody b = _world.Bodies[i];
                if (b.Id >= Tower.IdFloor) continue;
                if (b.Finite) continue;
                b.X = 0; b.Y = _bounds.Top - b.Hh; b.A = 0;
                b.Vx = 0; b.Vy = 0; b.W = 0;
                List<long> drop = new List<long>();
                foreach (KeyValuePair<long, TowerArbiter> kv in _world.Arbiters)
                    if (kv.Value.A.Id == b.Id || kv.Value.B.Id == b.Id) drop.Add(kv.Key);
                for (int k = 0; k < drop.Count; k++) _world.Arbiters.Remove(drop[k]);
            }

            /* 还有一个外力把手里的纸片推出房间的可能（大速度 + 大重叠）：
               夹回来，位置永远在房间里。逐张用它自己的宽高 —— 长纸条和
               短纸条的边界不一样。 */
            for (int i = 0; i < _world.Bodies.Count; i++)
            {
                TowerBody b = _world.Bodies[i];
                if (b.Id >= Tower.IdFloor) continue;
                b.X = Tower.Clamp(b.X, _bounds.L + b.Hw, _bounds.R - b.Hw);
                b.Y = Tower.Clamp(b.Y, _bounds.Floor + b.Hh, _bounds.Top - b.Hh);
            }

            /* 正在往外滑的那几张 */
            for (int i = _going.Count - 1; i >= 0; i--)
            {
                Going gg = _going[i];
                gg.T += dt;
                if (gg.T >= OutLife)
                {
                    _room.Children.Remove(gg.View.Element);
                    _going.RemoveAt(i);
                    /* 滑完最后一张时，空列表的提示要跟着出现 */
                    ShowHint();
                }
            }

            bool moving = g != null && g.Live;
            double restV = RestV * _k;
            for (int i = 0; i < _world.Bodies.Count; i++)
            {
                TowerBody b = _world.Bodies[i];
                if (b.Id >= Tower.IdFloor) continue;
                if (Tower.Hypot(b.Vx, b.Vy) > restV || Math.Abs(b.W) > RestW) moving = true;
            }

            _calm = moving ? 0 : _calm + steps;
            Draw();

            if (_calm < RestN || _going.Count > 0 || (g != null && g.Live))
            {
                return;
            }

            for (int i = 0; i < _world.Bodies.Count; i++)
            {
                TowerBody b = _world.Bodies[i];
                b.Vx = 0; b.Vy = 0; b.W = 0;
            }
            StopLoop();
        }

        void OnHit(double speed)
        {
            /* 撞击音效在参考里由合成器负责；岛上没有音频通路（也不该为了
               一块待办去开音频），这里只把最快的一次留在诊断里 */
            double ms = Now() * 1000;
            if (speed > 700 && ms - _lastHitMs > 180) _lastHitMs = ms;
        }

        /* ── 画 ────────────────────────────────────────────────────── */

        void Draw()
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                Card c = _cards[i];
                TowerBody b = Tower.Find(_world, c.BodyId);
                if (b == null) continue;
                Place(c, b.X, b.Y, b.A, 1.0);
            }

            for (int i = 0; i < _going.Count; i++)
            {
                Going g = _going[i];
                double p = g.T / OutLife;
                if (p < 0) p = 0;
                if (p > 1) p = 1;
                /* 先侧滑出去、之后才淡出：先看见方块被勾上，再看见它走 */
                double slide = p < 0.4 ? p / 0.4 : 1;
                double ease = 1 - (1 - slide) * (1 - slide) * (1 - slide);
                double fade = p < 0.35 ? 1 : (1 - (p - 0.35) / 0.65);
                if (fade < 0) fade = 0;
                Place(g.View, g.X + g.View.W * 0.95 * ease, g.Y, g.A + 0.09 * ease, fade);
            }
        }

        /* 逐张按自己的宽高落位：纸片长短不一，居中要各自居中 */
        void Place(Card c, double x, double y, double a, double opacity)
        {
            Canvas.SetLeft(c.Element, _roomW / 2 + x - c.Hw);
            Canvas.SetTop(c.Element, _roomH - y - c.Hh);
            c.Spin.Angle = -a * 180.0 / Math.PI;
            c.Element.Opacity = opacity;
        }

        void ShowHint()
        {
            bool empty = _cards.Count == 0 && _going.Count == 0;
            if (!empty)
            {
                if (_room.Children.Contains(_hint)) _room.Children.Remove(_hint);
                return;
            }

            _hint.Text = _doneMode ? "还没有完成的待办" : "点「写一条」，直接在岛上写";
            if (!_room.Children.Contains(_hint)) _room.Children.Add(_hint);
            Canvas.SetLeft(_hint, 0);
            Canvas.SetTop(_hint, Math.Round(_roomH / 2 - 9));
        }

        /* 诊断用：自检把这一行写进日志，座标和「静没静」都看得见 */
        public string Describe()
        {
            string s = "tower mode=" + (_doneMode ? "done" : "open")
                       + " cards=" + _cards.Count
                       + " going=" + _going.Count
                       + " hand=" + _hand
                       + " calm=" + Calm
                       + " h=" + Math.Round(_cardH)
                       + " w=" + Math.Round(MinCardWidth()) + ".." + Math.Round(MaxCardWidth())
                       + "/room " + Math.Round(_roomW);
            for (int i = 0; i < _cards.Count; i++)
            {
                TowerBody b = Tower.Find(_world, _cards[i].BodyId);
                if (b == null) continue;
                s += " [" + i + "](" + Math.Round(b.X, 1) + "," + Math.Round(b.Y, 1)
                     + "," + Math.Round(b.A, 2) + ',' + Math.Round(_cards[i].W) + ")";
            }
            return s;
        }

        /// <summary>实测：最窄/最宽的那张纸片（自检和验收都读它）。</summary>
        public double MinCardWidth()
        {
            double w = double.MaxValue;
            for (int i = 0; i < _cards.Count; i++)
                if (_cards[i].W < w) w = _cards[i].W;
            return w == double.MaxValue ? 0 : w;
        }

        public double MaxCardWidth()
        {
            double w = 0;
            for (int i = 0; i < _cards.Count; i++)
                if (_cards[i].W > w) w = _cards[i].W;
            return w;
        }

        /* 自检用：把塔一直解到静止（不等帧），并且断言纸片都还在房间里。
           返回一句话，出了问题就是它。 */
        public string SettleNow()
        {
            if (_stopped) return "stopped";
            Tower.Settle(_world, Dt, 1200);
            for (int i = 0; i < _world.Bodies.Count; i++)
            {
                TowerBody b = _world.Bodies[i];
                if (b.Id >= Tower.IdFloor) continue;
                if (!b.Finite) return "non-finite body " + b.Id;
                if (b.X < _bounds.L - 1 || b.X > _bounds.R + 1
                    || b.Y < _bounds.Floor - 1 || b.Y > _bounds.Top + 1)
                    return "body " + b.Id + " left the room at "
                           + Math.Round(b.X, 1) + "," + Math.Round(b.Y, 1);
            }
            Draw();
            return "";
        }

        /* 自检用：不经过鼠标，直接走「勾掉第 i 张」 */
        public bool StrikeForTest(int slot)
        {
            if (slot < 0 || slot >= _cards.Count) return false;
            Strike(_cards[slot]);
            return true;
        }

        /* 自检用：把那座塔在最上面那张的高度读出来（判断「上面那些掉下来了」） */
        public double HighestTop()
        {
            TowerBody top = TopBody();
            if (top == null) return 0;
            return top.Y + top.Hh;
        }

        public double FloorLevel { get { return _bounds.Floor; } }
    }
}