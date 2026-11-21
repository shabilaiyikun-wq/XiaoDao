using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinIsland
{
    /* ══ 待办页的离线截图 ═════════════════════════════════════════════════

       把 desktop/Layout.cs 里的待办页真的搭出来、真的排一次版、再光栅化成
       PNG —— 不开窗口、不动用户数据（AppDir 指到临时沙箱）。

       为什么要有这个：物理测试（verify/tower-tests.cmd）证明的是求解器，
       自检里的断言证明的是「数据和视图对得上」，但没有一条能证明**看着对**。
       版面这种事，能编译、断言全过，照样可以一行字压在按钮上、纸片糊成一团。
       唯一可靠的验收是把它画出来看。

       输出四张图，对应四种状态：
         01-open.png    普通待办（含一条已到点、一条快到的）
         02-done.png    已完成视图（正文带删除线、勾子实心）
         03-empty.png   空列表（提示语）
         04-single.png  只有一条（塔最矮的样子）

       跑法：desktop\verify\todo-shot.cmd [输出目录]
       默认输出到 work\todo-shot\。 */

    static class TodoShot
    {
        const double W = 468, H = 292;
        const double Scale = 3.0;          /* 3x 光栅化，字才不会糊成一片 */

        static string _out;
        static int _bad;      /* Shot/Geo/Ink 内部攒的失败，最后并进退出码 */

        [STAThread]
        static int Main(string[] args)
        {
            _out = args != null && args.Length > 0
                ? args[0]
                : Path.Combine(Path.GetTempPath(), "todo-shot");
            try
            {
                Directory.CreateDirectory(_out);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[x] cannot create " + _out + ": " + ex.Message);
                return 1;
            }

            /* 沙箱：Todos/Todos 都是「第一次访问时读盘」，所以必须在任何
               Todos.* 调用之前把根目录换掉，否则读的是用户真正的待办。 */
            string sandbox = Path.Combine(Path.GetTempPath(), "WinIsland-todo-shot");
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            Directory.CreateDirectory(sandbox);
            AppDir.SetRoot(sandbox);

            int bad = 0;
            try
            {
                /* 顺序很重要：Todos 是「第一次访问时读盘」的进程级单例，
                   所以「一条都没有」的那个状态只能最先画 —— 一旦 Add 过，
                   这个进程里就再也回不到真正的空沙箱了。 */
                bad += ShotEmptyOpen();
                bad += ShotOpen();
                bad += ShotDone();
                bad += ShotEmptyDone();
                bad += ShotSingle();
                bad += ShotLook("frost", "05-frost.png");
                bad += ShotLook("graphite", "06-graphite.png");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[x] " + ex.GetType().Name + ": " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                return 1;
            }

            Console.WriteLine();
            int total = bad + _bad;
            if (total == 0)
            {
                Console.WriteLine("TODO SHOT OK -> " + _out);
                return 0;
            }
            Console.WriteLine("TODO SHOT FAILED bad=" + total + " (states=" + bad
                              + " layout=" + _bad + ")");
            return 1;
        }

        /* ── 00 待办视图为空（进程刚起来、沙箱里什么都没有）───────────── */
        static int ShotEmptyOpen()
        {
            if (Todos.Items().Length != 0)
            {
                Console.WriteLine("  [x] empty-open must run before anything is added, found "
                                  + Todos.Items().Length);
                return 1;
            }

            Look look = Look.ByKey("glass");
            Art art = Art.ByKey("a");
            Built built = Layout.Build("todo", "expanded", W, H, look, art, new Snap(),
                                       new CardActions(), false);

            TodoTower tw = built.Extra as TodoTower;
            int bad = 0;
            if (tw == null) bad++;
            else
            {
                string err = tw.SettleNow();
                Console.WriteLine("  empty-open: " + tw.Describe() + " settle='" + err + "'");
                if (tw.CardCount != 0) { bad++; Console.WriteLine("  [x] want 0 cards, got " + tw.CardCount); }
                if (err.Length > 0) bad++;
                if (!HasText(built.Root, "写一条")) { bad++; Console.WriteLine("  [x] open hint missing"); }
            }

            Shot(built, "00-empty-open.png");
            built.Halt();
            return bad;
        }

        /* ── 01 普通待办：五条，一条已到点、一条 40 分钟后到点 ────────── */
        static int ShotOpen()
        {
            Todos.AddWithDue("把 Todo Tower 的物理接进岛里", DateTime.Now.AddMinutes(-3));
            Todos.Add("回 Nadia 的邮件（她只要一句话）");
            Todos.AddWithDue("23:00 前把域名续了", DateTime.Now.AddMinutes(40));
            Todos.Add("看一遍 WebView2 的输入透传文档");
            Todos.Add("把 pet-demo 的 demo 跑一次");

            Look look = Look.ByKey("glass");
            Art art = Art.ByKey("a");
            Built built = Layout.Build("todo", "expanded", W, H, look, art, new Snap(),
                                       new CardActions(), false);
            built.Refresh(new Snap());

            TodoTower tw = built.Extra as TodoTower;
            if (tw != null) Console.WriteLine("  open: " + tw.Describe() + " settle='" + tw.SettleNow() + "'");

            Shot(built, "01-open.png");
            built.Halt();
            return tw == null ? 1 : 0;
        }

        /* ── 02 已完成视图 ───────────────────────────────────────────── */
        static int ShotDone()
        {
            /* 勾掉两条，再进「已完成」那一摞 */
            TodoItem[] items = Todos.Items();
            int struck = 0;
            for (int i = 0; i < items.Length && struck < 2; i++)
            {
                if (items[i].Done) continue;
                Todos.Toggle(i);
                struck++;
            }

            Look look = Look.ByKey("glass");
            Art art = Art.ByKey("a");
            Built built = Layout.Build("todo", "expanded", W, H, look, art, new Snap(),
                                       new CardActions(), false);
            built.Refresh(new Snap());

            TodoTower tw = built.Extra as TodoTower;
            int bad = 0;
            if (tw == null) bad++;
            else
            {
                tw.ToggleMode();
                string err = tw.SettleNow();
                Console.WriteLine("  done: " + tw.Describe() + " settle='" + err + "'");
                if (err.Length > 0) bad++;
                if (tw.CardCount != 2)
                {
                    Console.WriteLine("  [x] done view should hold 2 cards, got " + tw.CardCount);
                    bad++;
                }
            }

            Shot(built, "02-done.png");
            built.Halt();
            return bad;
        }

        /* ── 03 已完成视图为空（清空已完成之后停在「已完成」这一摞）─────── */
        static int ShotEmptyDone()
        {
            Todos.ClearDone();
            Look look = Look.ByKey("glass");
            Art art = Art.ByKey("a");
            Built built = Layout.Build("todo", "expanded", W, H, look, art, new Snap(),
                                       new CardActions(), false);

            TodoTower tw = built.Extra as TodoTower;
            int bad = 0;
            if (tw == null) bad++;
            else
            {
                tw.ToggleMode();                    /* 待办 → 已完成 */
                string err = tw.SettleNow();
                Console.WriteLine("  empty-done: " + tw.Describe() + " settle='" + err + "'");
                if (tw.CardCount != 0) { bad++; Console.WriteLine("  [x] want 0 cards, got " + tw.CardCount); }
                if (err.Length > 0) bad++;
                if (!HasText(built.Root, "还没有完成的待办"))
                {
                    bad++;
                    Console.WriteLine("  [x] done-mode hint missing");
                }
            }

            Shot(built, "03-empty-done.png");
            built.Halt();
            return bad;
        }

        /* ── 04 只有一条 ─────────────────────────────────────────────── */
        static int ShotSingle()
        {
            /* 清干净，只留一条 */
            TodoItem[] items = Todos.Items();
            for (int i = items.Length - 1; i >= 0; i--)
                if (!items[i].Done) Todos.Toggle(i);
            Todos.ClearDone();
            Todos.Add("只剩这一条");

            Look look = Look.ByKey("obsidian");
            Art art = Art.ByKey("a");
            Built built = Layout.Build("todo", "expanded", W, H, look, art, new Snap(),
                                       new CardActions(), false);

            TodoTower tw = built.Extra as TodoTower;
            int bad = 0;
            if (tw == null) bad++;
            else
            {
                string err = tw.SettleNow();
                Console.WriteLine("  single: " + tw.Describe() + " settle='" + err + "'");
                if (err.Length > 0) bad++;
                if (tw.CardCount != 1) { bad++; Console.WriteLine("  [x] want 1 card, got " + tw.CardCount); }
            }

            Shot(built, "04-single.png");
            built.Halt();
            return bad;
        }

        /* ── 05/06 换外观：灰条在浅色和更亮的深色外观里都得站得住 ────────
           frost 是浅色外观，灰条要比面板暗；graphite 的底色比 obsidian 亮，
           灰条得跟着抬一档。这两种是最容易和面板糊在一起的，所以也渲出来量。 */
        static int ShotLook(string lookKey, string file)
        {
            Look look = Look.ByKey(lookKey);
            Art art = Art.ByKey("a");
            Built built = Layout.Build("todo", "expanded", W, H, look, art, new Snap(),
                                       new CardActions(), false);

            TodoTower tw = built.Extra as TodoTower;
            int bad = 0;
            if (tw == null) bad++;
            else
            {
                string err = tw.SettleNow();
                Console.WriteLine("  " + lookKey + ": " + tw.Describe() + " settle='" + err + "'");
                if (err.Length > 0) bad++;
                if (tw.CardCount == 0) { bad++; Console.WriteLine("  [x] " + lookKey + ": no cards"); }
            }

            Shot(built, file);
            built.Halt();
            return bad;
        }

        /* ── 光栅化 ────────────────────────────────────────────────────
           给它一块深底色再画：岛是叠在桌面上的，纸片用的是半透明的白，
           没有底色就什么都看不出来（这跟真实运行时是一样的前提）。 */
        static void Shot(Built built, string name)
        {
            Border host = new Border();
            host.Width = W;
            host.Height = H;
            host.Background = new SolidColorBrush(Color.FromRgb(24, 25, 28));
            host.Child = built.Root;

            host.Measure(new Size(W, H));
            host.Arrange(new Rect(0, 0, W, H));
            host.UpdateLayout();
            /* 排完版再画一次：布局里有些位置只有在 Arrange 之后才有意义 */
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Loaded,
                new Action(delegate { host.UpdateLayout(); }));

            int pw = (int)Math.Round(W * Scale), ph = (int)Math.Round(H * Scale);
            RenderTargetBitmap bmp = new RenderTargetBitmap(pw, ph, 96 * Scale, 96 * Scale,
                                                            PixelFormats.Pbgra32);
            bmp.Render(host);

            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            string path = Path.Combine(_out, name);
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                enc.Save(fs);
            Console.WriteLine("  -> " + path + "  (" + pw + "x" + ph + ")");

            /* 光栅化之后再量一遍：这一遍量的是**真的画出来的**位置，不是
               Layout 里那些算式的自我复述。 */
            Geo(built, name);
            Ink(bmp, name, Cards(built), ExpectedCardArea(built));
        }

        /* ── 几何：每个字到底被排在哪儿 ───────────────────────────────
           没有肉眼看图，就用矩形说话：字必须在面板里、按钮必须在塔下面、
           字不能在左右留白里。这三条要是破了，「能编译、断言全过、看着错版」
           就会一起混过去。 */
        static void Geo(Built built, string name)
        {
            Border host = built.Root.Parent as Border;
            if (host == null)
            {
                /* Shot 里 host 是 built.Root 的父；没有就自己找根 */
                Console.WriteLine("  [x] " + name + ": no host to measure against");
                _bad++;
                return;
            }

            List<TextBlock> texts = new List<TextBlock>();
            CollectTextBlocks(built.Root, texts);

            double roomTop = 0, roomBot = 0;
            FrameworkElement root = built.Root;
            TodoTower tw = built.Extra as TodoTower;
            int inside = 0, oob = 0;

            for (int i = 0; i < texts.Count; i++)
            {
                TextBlock tb = texts[i];
                if (tb.ActualWidth <= 0 || tb.ActualHeight <= 0) continue;
                Rect r;
                try { r = tb.TransformToAncestor(host).TransformBounds(
                          new Rect(0, 0, tb.ActualWidth, tb.ActualHeight)); }
                catch { continue; }
                inside++;
                if (r.Left < -0.5 || r.Top < -0.5 || r.Right > W + 0.5 || r.Bottom > H + 0.5)
                {
                    oob++;
                    if (oob <= 3)
                        Console.WriteLine("  [x] " + name + ": text '" + Clip(tb.Text)
                                          + "' outside the panel at " + R(r));
                }
            }
            if (oob > 0) _bad++;

            /* 「写一条」必须在塔下面，而且要和塔错开。
               房间底边从**塔那个画布**量，不背 Layout 的算式：
               横幅有没有出现会改变 listTop，照抄算式就会把正常的版面
               判成重叠（这个探针自己先踩了一次）。 */
            Rect? add = Find(texts, host, "写一条");
            Rect? room = RectOf(tw == null ? null : tw.Root, host);
            if (add == null)
            {
                Console.WriteLine("  [x] " + name + ": the add button is not in the tree");
                _bad++;
            }
            else if (room != null)
            {
                roomBot = room.Value.Bottom;
                if (add.Value.Top < roomBot - 0.5)
                {
                    Console.WriteLine("  [x] " + name + ": add button overlaps the tower (top="
                                      + Math.Round(add.Value.Top, 1) + " roomBottom="
                                      + Math.Round(roomBot, 1) + ")");
                    _bad++;
                }
                if (room.Value.Width < 400 || room.Value.Height < 140)
                {
                    Console.WriteLine("  [x] " + name + ": the tower room is too small ("
                                      + Math.Round(room.Value.Width, 1) + "x"
                                      + Math.Round(room.Value.Height, 1) + ")");
                    _bad++;
                }
            }

            /* 正文字不能压到勾选方块上：取一张纸片的字数一下 */
            if (tw != null && tw.CardCount > 0)
            {
                List<Border> cards = new List<Border>();
                CollectCards(built.Root, cards);
                for (int i = 0; i < cards.Count; i++)
                {
                    Canvas inner = cards[i].Child as Canvas;
                    if (inner == null || inner.Children.Count < 2) continue;
                    Border check = inner.Children[0] as Border;
                    TextBlock label = null;
                    for (int k = 1; k < inner.Children.Count; k++)
                    {
                        TextBlock t = inner.Children[k] as TextBlock;
                        if (t != null && t.TextDecorations != TextDecorations.Strikethrough)
                        { label = t; break; }
                        if (t != null && label == null) label = t;
                    }
                    if (check == null || label == null) continue;
                    double checkRight = Canvas.GetLeft(check) + check.Width;
                    double labelLeft = Canvas.GetLeft(label);
                    if (labelLeft < checkRight - 0.5)
                    {
                        Console.WriteLine("  [x] " + name + ": card text overlaps the check box ("
                                          + Math.Round(labelLeft, 1) + " < " + Math.Round(checkRight, 1) + ")");
                        _bad++;
                    }
                    if (label.Width <= 8)
                    {
                        Console.WriteLine("  [x] " + name + ": card text has no room (w=" + Math.Round(label.Width, 1) + ")");
                        _bad++;
                    }
                }

                /* ── 拖动余量 + 宽度随文本 ─────────────────────────────────────
                   两条都在这里守着：
                   1. 最宽的那张纸片必须仍然在两侧留出真实距离（不然被墙
                      夹住，横向拖不动 —— 这个错误已经犯过一次）；
                   2. 宽度必须**随正文变化**：所有卡片一样宽说明它又变回
                      固定宽度了，那短待办会被撑成一大片空白。 */
                if (cards.Count != tw.CardCount)
                {
                    Console.WriteLine("  [x] " + name + ": found " + cards.Count
                                      + " card visuals for " + tw.CardCount
                                      + " cards -- the marker no longer matches, "
                                      + "the checks below would pass on nothing");
                    _bad++;
                }
                else if (cards.Count > 0)
                {
                    double widest = 0, narrowest = double.MaxValue;
                    string widths = "";
                    for (int i = 0; i < cards.Count; i++)
                    {
                        double w = cards[i].Width;
                        if (w > widest) widest = w;
                        if (w < narrowest) narrowest = w;
                        widths += (i > 0 ? "," : "") + Math.Round(w);
                    }
                    double chh = cards[0].Height;
                    double sideRoom = (tw.RoomWidth - widest) / 2;
                    double stackH = tw.CardCount * chh;
                    double topRoom = tw.RoomHeight - stackH;
                    Console.WriteLine("  drag " + name + ": widths=[" + widths + "] h="
                                      + Math.Round(chh) + " room=" + Math.Round(tw.RoomWidth)
                                      + "x" + Math.Round(tw.RoomHeight)
                                      + " sideRoom=" + Math.Round(sideRoom, 1)
                                      + " topRoom=" + Math.Round(topRoom, 1));

                    if (sideRoom < 40)
                    {
                        Console.WriteLine("  [x] " + name + ": the widest card leaves only "
                                          + Math.Round(sideRoom, 1) + "px per side -- it "
                                          + "cannot be dragged sideways");
                        _bad++;
                    }
                    if (topRoom < 18)
                    {
                        Console.WriteLine("  [x] " + name + ": only " + Math.Round(topRoom, 1)
                                          + "px above the stack -- a lifted card hits the ceiling");
                        _bad++;
                    }
                    /* 三条以上正文长度不同的待办，宽度必须真的不一样 */
                    if (cards.Count >= 3 && widest - narrowest < 8)
                    {
                        Console.WriteLine("  [x] " + name + ": every card is "
                                          + Math.Round(widest) + "px wide -- the width no longer "
                                          + "follows the text");
                        _bad++;
                    }
                    if (narrowest < 90 || widest > tw.RoomWidth)
                    {
                        Console.WriteLine("  [x] " + name + ": a card width is out of range ("
                                          + Math.Round(narrowest) + ".." + Math.Round(widest) + ")");
                        _bad++;
                    }
                }
            }

            Console.WriteLine("  geo " + name + ": texts=" + inside + " oob=" + oob
                              + " addTop=" + (add == null ? "n/a" : Math.Round(add.Value.Top, 1).ToString())
                              + " roomBottom=" + Math.Round(roomBot, 1));

            /* ── 命中面：按纸片不能把整座岛拖走 ───────────────────────
               IslandWindow.IsActionHit 从事件源往上最多走 8 层找 Tag="act"。
               纸片上的勾选方块、正文、纸片本身都必须是「act」，而且必须在
               8 层以内找得到；空白的房间画布则必须**没有** act ——
               否则点列表空白处就再也拖不动岛了。 */
            if (tw != null)
            {
                List<Border> cards = new List<Border>();
                CollectCards(built.Root, cards);
                for (int i = 0; i < cards.Count && i < 2; i++)
                {
                    Canvas inner = cards[i].Child as Canvas;
                    if (inner == null || inner.Children.Count == 0) continue;
                    DependencyObject probe = inner.Children[0];      /* 勾选方块 */
                    int depth = ActDepth(probe);
                    if (depth < 0 || depth > 8)
                    {
                        Console.WriteLine("  [x] " + name + ": the check box is not an action hit (depth="
                                          + depth + ")");
                        _bad++;
                    }
                }
                if (ActDepth(tw.Root) >= 0 && ActDepth(tw.Root) <= 8)
                {
                    Console.WriteLine("  [x] " + name + ": the empty room is an action hit, "
                                      + "so the island can no longer be dragged from it");
                    _bad++;
                }
            }
        }

        /* 从 d 往上找第一个 Tag=="act" 的层数（IsActionHit 的同一个约定），
           找不到返回 -1。 */
        static int ActDepth(DependencyObject d)
        {
            int depth = 0;
            while (d != null && depth <= 12)
            {
                FrameworkElement el = d as FrameworkElement;
                if (el != null && (el.Tag as string) == "act") return depth;
                d = VisualTreeHelper.GetParent(d);
                depth++;
            }
            return -1;
        }

        static double RoomBottom()
        {
            /* 和 Layout.ExpandedTodo 同一套算式：listTop + rowH*5 */
            int bannerRows = 1;
            return bannerRows > 0 ? 66 + 150 : 46 + 150;
        }

        static int Cards(Built built)
        {
            TodoTower tw = built.Extra as TodoTower;
            return tw == null ? 0 : tw.CardCount;
        }

        /* 这些卡片本该在「卡片带」里占多少面积：把每张纸片的宽高加起来，
           除以那条带的面积。用来判断「画出来的东西是不是真的在那儿」，
           而不是拿一个固定的百分比去套所有情形。 */
        static double ExpectedCardArea(Built built)
        {
            TodoTower tw = built.Extra as TodoTower;
            if (tw == null || tw.CardCount == 0) return 0;

            List<Border> cards = new List<Border>();
            CollectCards(built.Root, cards);
            if (cards.Count == 0) return 0;

            double area = 0, bandH = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                area += cards[i].Width * cards[i].Height;
                bandH = Math.Max(bandH, cards[i].Height);
            }
            /* 带高 = 卡片带占整幅的比例（0.16..0.62），宽就是面板宽 */
            double band = W * (H * (0.62 - 0.16));
            return band > 0 ? area / band : 0;
        }

        static Rect? RectOf(FrameworkElement el, FrameworkElement host)
        {
            if (el == null) return null;
            if (el.ActualWidth <= 0 || el.ActualHeight <= 0) return null;
            try
            {
                return el.TransformToAncestor(host).TransformBounds(
                    new Rect(0, 0, el.ActualWidth, el.ActualHeight));
            }
            catch { return null; }
        }

        /* ── 墨迹：画出来的东西落在该落的地方 ─────────────────────────
           逐行统计「和底色不一样」的像素。它不能证明好看，但能证明
           「这一块真的有内容」和「留白里没有东西漏出去」—— 一块空面板、
           一个字跑到面板外、整页没画出来，这三种都躲不过去。 */
        static void Ink(RenderTargetBitmap bmp, string name, int cardCount, double expectCardArea)
        {
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            int stride = w * 4;
            byte[] px = new byte[stride * h];
            bmp.CopyPixels(px, stride, 0);

            /* 底色 (24,25,28) 允许一点误差：半透明叠加后会有细微差别 */
            double cardBand = BandInk(px, w, h, stride, 0.16, 0.62);
            double btnBand = BandInk(px, w, h, stride, 0.72, 0.90);
            double leftGutter = BandInkCols(px, w, h, stride, 0.0, 0.030);
            double rightGutter = BandInkCols(px, w, h, stride, 0.975, 1.0);

            Console.WriteLine("  ink " + name + ": cards=" + Pct(cardBand)
                              + " buttons=" + Pct(btnBand)
                              + " leftGutter=" + Pct(leftGutter)
                              + " rightGutter=" + Pct(rightGutter));

            TodoTower tw = null;   /* 卡片带是不是该有东西，由调用方通过 _bad 判定 */
            /* 阈值不能凭感觉定一个百分比：卡片宽是跟着正文走的，一张窄纸条
               在整条带宽里只占很小一块。这里按「这些卡片本该覆盖多少面积」
               折算，只要实际墨迹不少于预期的一个零头，就说明真的画出来了。
               曾经用固定 1% 判，结果 116 宽的单张卡（实际 0.9%）被误报成
               「什么都没画」—— 断言比被测代码更容易错。 */
            double expect = expectCardArea;
            if (cardCount > 0 && expect > 0 && cardBand < expect * 0.15)
            {
                Console.WriteLine("  [x] " + name + ": " + cardCount
                                  + " cards cover " + Pct(cardBand) + " of the band, expected "
                                  + "about " + Pct(expect) + " -- looks like nothing was drawn");
                _bad++;
            }
            if (cardCount == 0 && cardBand > 0.30)
            {
                /* 空状态却有一大片墨：多半是残留的纸片没摘干净 */
                Console.WriteLine("  [x] " + name + ": empty list but the card band is "
                                  + Pct(cardBand) + " inked");
                _bad++;
            }
            if (btnBand < 0.005)
            {
                Console.WriteLine("  [x] " + name + ": nothing drawn in the button band");
                _bad++;
            }
            if (leftGutter > 0.02 || rightGutter > 0.02)
            {
                Console.WriteLine("  [x] " + name + ": ink in the side gutters (L="
                                  + Pct(leftGutter) + " R=" + Pct(rightGutter) + ")");
                _bad++;
            }
        }

        static double BandInk(byte[] px, int w, int h, int stride, double y0, double y1)
        {
            int r0 = (int)(y0 * h), r1 = (int)(y1 * h);
            int hit = 0, total = 0;
            for (int y = r0; y < r1; y += 2)
            {
                int off = y * stride;
                for (int x = 0; x < w; x += 2)
                {
                    total++;
                    if (IsInk(px, off + x * 4)) hit++;
                }
            }
            return total == 0 ? 0 : (double)hit / total;
        }

        static double BandInkCols(byte[] px, int w, int h, int stride, double x0, double x1)
        {
            int c0 = (int)(x0 * w), c1 = (int)(x1 * w);
            int hit = 0, total = 0;
            for (int y = 0; y < h; y += 2)
            {
                int off = y * stride;
                for (int x = c0; x < c1; x += 2)
                {
                    total++;
                    if (IsInk(px, off + x * 4)) hit++;
                }
            }
            return total == 0 ? 0 : (double)hit / total;
        }

        static bool IsInk(byte[] px, int i)
        {
            int b = px[i], g = px[i + 1], r = px[i + 2];
            return Math.Abs(r - 24) > 6 || Math.Abs(g - 25) > 6 || Math.Abs(b - 28) > 6;
        }

        static string Pct(double v)
        {
            return Math.Round(v * 100, 1).ToString() + "%";
        }

        static string Clip(string s)
        {
            if (s == null) return "";
            s = s.Replace("\n", " ");
            return s.Length <= 24 ? s : s.Substring(0, 24) + "…";
        }

        static string R(Rect r)
        {
            return "(" + Math.Round(r.Left, 1) + "," + Math.Round(r.Top, 1) + ","
                   + Math.Round(r.Width, 1) + "x" + Math.Round(r.Height, 1) + ")";
        }

        static void CollectTextBlocks(DependencyObject d, List<TextBlock> into)
        {
            if (d == null) return;
            TextBlock tb = d as TextBlock;
            if (tb != null) into.Add(tb);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) CollectTextBlocks(VisualTreeHelper.GetChild(d, i), into);
        }

        static void CollectCards(DependencyObject d, List<Border> into)
        {
            if (d == null) return;
            Border b = d as Border;
            /* 按标记认，不按宽度认：宽度一改，按宽度匹配会匹配不到东西，
               后面的断言就全部「通过」了 —— 那种测试比没有还坏。 */
            if (b != null && (b.Uid == "tw-card")) into.Add(b);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) CollectCards(VisualTreeHelper.GetChild(d, i), into);
        }

        static Rect? Find(List<TextBlock> texts, FrameworkElement host, string label)
        {
            for (int i = 0; i < texts.Count; i++)
            {
                TextBlock tb = texts[i];
                /* 精确相等，不是包含：空列表那句提示语里也有「写一条」，
                   用子串匹配会把它当成按钮，然后报一个根本不存在的重叠。 */
                if (tb.Text == null || tb.Text != label) continue;
                if (tb.ActualWidth <= 0) continue;
                try
                {
                    return tb.TransformToAncestor(host).TransformBounds(
                        new Rect(0, 0, tb.ActualWidth, tb.ActualHeight));
                }
                catch { return null; }
            }
            return null;
        }

        static bool HasText(DependencyObject root, string needle)
        {
            List<string> all = new List<string>();
            Collect(root, all);
            for (int i = 0; i < all.Count; i++)
            {
                string t = all[i];
                if (t == null) continue;
                if (t.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        static void Collect(DependencyObject d, List<string> into)
        {
            if (d == null) return;
            TextBlock tb = d as TextBlock;
            if (tb != null && tb.Text != null) into.Add(tb.Text);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Collect(VisualTreeHelper.GetChild(d, i), into);
        }
    }
}