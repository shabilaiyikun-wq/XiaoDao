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
    /* ══ 剪贴板页 / 中转站页的离线截图 + 几何验收 ═══════════════════════════

       把 Layout 里这两页真的搭出来、真的排一次版、再光栅化成 PNG ——
       不开窗口、不动用户数据（AppDir 指到临时沙箱，文件也是现造的）。

       为什么要有这个：这两页这一版换了版面（剪贴板从「一列透明字」改成
       「一条一条的实心条」、中转站从「行」改成「大图标 + 名字」），
       能编译、能跑、看着错版这三件事完全可以同时成立。唯一可靠的验收是
       把它画出来看，并且用矩形和像素把「画在哪儿」量下来。

       它验三件事：
         1. 剪贴条的数量 / 尺寸 / 间距 / 材质 —— 材质是**和待办纸片比色**，
            不是拿另一个常量去比常量，所以「跟待办条相同」这句话真的被验了。
         2. 中转站的格子按 6×2 铺开、图标是 48px 原生位图、名字在图标下面、
            整格可命中（Tag="act"，按格子不会把整座岛拖走）。
         3. 像素：每一格真的画出了东西（图标带和名字带都有墨），
            没有出现「控件在但什么都没画出来」。

       输出（默认 work\panel-shot\）：
         01-clip.png          6 条剪贴记录（含多行、超长）
         02-clip-frost.png    浅色外观下的同一页
         03-clip-empty.png    空剪贴板
         04-transfer.png      12 项（含文件夹、.exe、一个已丢失的）
         05-transfer-frost.png
         06-transfer-hover.png 拖动悬停 → 整块投放区
         07-transfer-empty.png

       跑法：desktop\verify\panel-shot.cmd [输出目录]
       退出码 0 = 全部断言通过。 */

    static class PanelShot
    {
        const double W = 468, H = 268;
        const double Scale = 3.0;          /* 3x 光栅化，字才不会糊成一片 */

        /* 和 Layout 里那两个常量必须一致：条高 30、条距 4、最多 6 条。
           这里重算一遍是**故意的** —— 验收要独立算一次，不能拿被验的代码
           自己算出的数字来验它自己。 */
        const double BarH = 30;
        const double BarGap = 4;
        const int BarMax = 6;

        static string _out;
        static int _bad;

        /* 最近一次光栅化的结果。像素断言直接在这上面采样 ——
           画出来的东西才算数，控件树里的属性只说明「打算画什么」。 */
        static RenderTargetBitmap _bmp;
        static Color _bmpBase;

        static readonly Color Base = Color.FromRgb(24, 25, 28);   /* 面板底色（深） */
        static readonly Color BaseLight = Color.FromRgb(247, 248, 251);

        [STAThread]
        static int Main(string[] args)
        {
            _out = args != null && args.Length > 0
                ? args[0]
                : Path.Combine(Path.GetTempPath(), "panel-shot");
            try { Directory.CreateDirectory(_out); }
            catch (Exception ex)
            {
                Console.WriteLine("[x] cannot create " + _out + ": " + ex.Message);
                return 1;
            }

            /* 沙箱：Clips/Transfer 都是「第一次访问时读盘」，所以换根必须在
               任何 Clips.* / Transfer.* 之前。 */
            string sandbox = Path.Combine(Path.GetTempPath(), "WinIsland-panel-shot");
            try
            {
                if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
                Directory.CreateDirectory(sandbox);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[x] sandbox: " + ex.Message);
                return 1;
            }
            AppDir.SetRoot(sandbox);

            int bad = 0;
            try
            {
                SeedClips();
                bad += ShotClip("glass", "01-clip.png", true);
                bad += ShotClip("frost", "02-clip-frost.png", true);
                Clips.Clear();
                bad += ShotClip("glass", "03-clip-empty.png", false);

                SeedTransfer();
                bad += ShotTransfer("glass", "04-transfer.png", false);
                bad += ShotTransfer("frost", "05-transfer-frost.png", false);
                bad += ShotTransfer("glass", "06-transfer-hover.png", true);

                Transfer.Clear();
                bad += ShotTransfer("glass", "07-transfer-empty.png", false);

                /* 只有两项时图标也要靠左上排（桌面图标的规矩），
                   不能被拉成两块半宽的板 —— 这条是版面里那个「列数不随条目数缩」
                   的直接验收。 */
                SeedTransferFew();
                bad += ShotTransfer("glass", "08-transfer-few.png", false);
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
                Console.WriteLine("PANEL SHOT OK -> " + _out);
                return 0;
            }
            Console.WriteLine("PANEL SHOT FAILED bad=" + total + " (states=" + bad
                              + " layout=" + _bad + ")");
            return 1;
        }

        /* ── 造数据 ───────────────────────────────────────────────────── */

        /* 六条有代表性的剪贴内容：多行、超长、带连续空白、纯 URL、中文、
           以及一条只有几个字的。真实剪贴板里这几类占绝大多数。 */
        static void SeedClips()
        {
            string[] texts = new string[]
            {
                "把这条复制下来，看看它在条里被压成一行之后还认得出来吗：\n" +
                "第一行是正文，第二行是补充说明。",
                "https://example.com/a/very/long/path/that/keeps/going/and/going/" +
                "until/it/definitely/does/not/fit/in/one/bar?with=query&and=more",
                "连续的空格     和\t制表符\t\t都该被折成一个空格，而不是把字顶到右边去",
                "git commit -m \"feat(island): clipboard bars + transfer icons\"",
                "简短一条",
                "带编号的清单：1) 起线程 2) 读剪贴板 3) 折行 4) 落盘 —— " +
                "这一条故意写得很长，用来确认省略号真的出现在条的右边界内侧。"
            };

            List<string> lines = new List<string>();
            for (int i = 0; i < texts.Length; i++) lines.Add(AppDir.Encode(texts[i]));
            AppDir.WriteLines("clips.txt", lines);
        }

        /* 12 项 + 1 个文件夹 + 1 个真 exe + 1 个幽灵路径。
           造真文件是为了让 shell 真的返回图标 —— 空路径也能画，但那验不到
           大图标那条链路（Transfer.IconLarge）。 */
        static void SeedTransfer()
        {
            string dir = Path.Combine(Path.GetTempPath(), "WinIsland-panel-shot-files");
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
            }
            catch (Exception ex) { Console.WriteLine("  ! files: " + ex.Message); }

            string[] names = new string[]
            {
                "季度汇报-最终版.pptx", "设计稿 v3.png", "note.txt", "archive.zip",
                "report.docx", "data.csv", "clip.mp4", "song.flac",
                "一个特别特别长的文件名用来测试两行之后会不会被省略号截断.md",
                "config.json", "photo.jpg", "installer.msi"
            };

            string bin = Environment.GetFolderPath(Environment.SpecialFolder.System);
            List<string> paths = new List<string>();
            for (int i = 0; i < names.Length; i++)
            {
                string p = Path.Combine(dir, names[i]);
                try { File.WriteAllText(p, "x"); }
                catch (Exception) { }
                paths.Add(p);
            }
            string folder = Path.Combine(dir, "素材文件夹");
            try { Directory.CreateDirectory(folder); } catch (Exception) { }
            paths.Add(folder);

            /* 真 exe：只有这一类会走「按完整路径问 shell 要图标」那条路 */
            paths.Add(Path.Combine(bin, "notepad.exe"));

            /* 幽灵路径：Add 会就地判 Missing，版面上应该压暗 + 名字变强调色 */
            paths.Add(Path.Combine(dir, "已经删掉的文件.pdf"));

            int added = Transfer.Add(paths.ToArray());
            Console.WriteLine("  seed transfer added=" + added + " total=" + Transfer.Count()
                              + " missing=" + Transfer.MissingCount());
        }

        /* 只有两项的情形：验「靠左上排」而不是「把两格拉宽」。 */
        static void SeedTransferFew()
        {
            string dir = Path.Combine(Path.GetTempPath(), "WinIsland-panel-shot-files");
            string[] names = new string[] { "方案.md", "截图.png" };
            List<string> paths = new List<string>();
            for (int i = 0; i < names.Length; i++)
            {
                string p = Path.Combine(dir, names[i]);
                try { File.WriteAllText(p, "x"); } catch (Exception) { }
                paths.Add(p);
            }
            Transfer.Add(paths.ToArray());
            Console.WriteLine("  seed transfer(few) total=" + Transfer.Count());
        }

        /* ── 剪贴板页 ─────────────────────────────────────────────────── */

        static int ShotClip(string lookKey, string file, bool withItems)
        {
            Look look = Look.ByKey(lookKey);
            Art art = Art.ByKey("aurora");
            int before = _bad;

            Built built = Layout.Build("clip", "expanded", W, H, look, art, new Snap(),
                                       new CardActions(), false);
            Shot(built, file, lookKey == "frost", file.StartsWith("01-"));

            int expect = withItems ? Math.Min(Clips.Count(), BarMax) : 0;
            int got = 0;
            List<FrameworkElement> bars = CollectUid(built.Root, "cbar");
            for (int i = 0; i < bars.Count; i++)
            {
                Border border = bars[i].Parent as Border;
                if (border == null) continue;      /* 只算「条」本身 */
                got++;
            }

            Console.WriteLine("  clip " + lookKey + ": bars=" + got + " expect=" + expect
                              + " items=" + Clips.Count());
            if (got != expect)
            {
                Console.WriteLine("  [x] " + file + ": bar count " + got + " != " + expect);
                _bad++;
            }

            if (!withItems)
            {
                if (!HasText(built.Root, "还没有剪贴记录"))
                {
                    Console.WriteLine("  [x] " + file + ": empty state text missing");
                    _bad++;
                }
            }
            else
            {
                CheckClipBars(built, look, file);
            }

            built.Halt();
            return _bad - before;
        }

        /* 剪贴条的几何与材质。 */
        static void CheckClipBars(Built built, Look look, string file)
        {
            double listW = W - Layout.PadExp * 2;
            double listTop = 46;
            double listH = H - 46 - Layout.PadExp;

            List<FrameworkElement> marks = CollectUid(built.Root, "cbar");
            List<Border> bars = new List<Border>();
            for (int i = 0; i < marks.Count; i++)
            {
                /* 里层 Canvas 的父就是那条 Border。命中面（SquishPlate）包着 Border，
                   所以「有 Border 爹的 cbar」正好每条一个。 */
                Border b = marks[i].Parent as Border;
                if (b != null) bars.Add(b);
            }
            if (bars.Count == 0)
            {
                Console.WriteLine("  [x] " + file + ": no clip bars found");
                _bad++;
                return;
            }

            /* 材质基准：现搭一张待办纸片，直接比颜色。
               比常量更硬 —— 以后谁改了待办条的颜色，这里立刻红。
               必须用**同一个外观**搭：frost 和 glass 的灰条本来就不同色。 */
            Color cardBg = Colors.Transparent, cardEdge = Colors.Transparent;
            double cardRadius = -1;
            if (TodoSample(look, out cardBg, out cardEdge, out cardRadius) == false)
            {
                Console.WriteLine("  [x] " + file + ": could not build a todo card to compare against");
                _bad++;
            }

            for (int i = 0; i < bars.Count; i++)
            {
                Border b = bars[i];
                Rect r = RectOf(b, built.Root);

                if (Math.Abs(b.Width - listW) > 0.5 || Math.Abs(b.Height - BarH) > 0.5)
                {
                    Console.WriteLine("  [x] " + file + ": bar[" + i + "] size "
                                      + Math.Round(b.Width) + "x" + Math.Round(b.Height)
                                      + " != " + listW + "x" + BarH);
                    _bad++;
                }
                if (Math.Abs(r.Left - Layout.PadExp) > 0.6)
                {
                    Console.WriteLine("  [x] " + file + ": bar[" + i + "] left "
                                      + Math.Round(r.Left, 1) + " != PadExp");
                    _bad++;
                }
                double wantTop = listTop + i * (BarH + BarGap);
                if (Math.Abs(r.Top - wantTop) > 0.6)
                {
                    Console.WriteLine("  [x] " + file + ": bar[" + i + "] top "
                                      + Math.Round(r.Top, 1) + " != " + wantTop);
                    _bad++;
                }
                if (r.Bottom > listTop + listH + 0.6)
                {
                    Console.WriteLine("  [x] " + file + ": bar[" + i + "] bottom "
                                      + Math.Round(r.Bottom, 1) + " overflows the list");
                    _bad++;
                }

                SolidColorBrush bg = b.Background as SolidColorBrush;
                SolidColorBrush edge = b.BorderBrush as SolidColorBrush;
                if (bg == null || edge == null)
                {
                    Console.WriteLine("  [x] " + file + ": bar[" + i + "] has no solid material");
                    _bad++;
                }
                else
                {
                    if (cardRadius > 0 && bg.Color != cardBg)
                    {
                        Console.WriteLine("  [x] " + file + ": bar[" + i + "] bg "
                                          + Hex(bg.Color) + " != todo card bg " + Hex(cardBg));
                        _bad++;
                    }
                    if (cardRadius > 0 && edge.Color != cardEdge)
                    {
                        Console.WriteLine("  [x] " + file + ": bar[" + i + "] edge "
                                          + Hex(edge.Color) + " != todo card edge " + Hex(cardEdge));
                        _bad++;
                    }
                    if (b.BorderThickness.Left != 1)
                    {
                        Console.WriteLine("  [x] " + file + ": bar[" + i + "] border thickness "
                                          + b.BorderThickness.Left + " != 1");
                        _bad++;
                    }
                    if (cardRadius > 0 && Math.Abs(b.CornerRadius.TopLeft - cardRadius) > 0.01)
                    {
                        Console.WriteLine("  [x] " + file + ": bar[" + i + "] radius "
                                          + b.CornerRadius.TopLeft + " != todo card " + cardRadius);
                        _bad++;
                    }
                }

                /* 正文不许压到右边那颗「复制」的坑位上 */
                List<TextBlock> texts = new List<TextBlock>();
                CollectTextBlocks(b.Child, texts);
                for (int k = 0; k < texts.Count; k++)
                {
                    TextBlock tb = texts[k];
                    if (tb.ActualWidth <= 0 || tb.Text == null || tb.Text.Length == 0) continue;
                    Rect tr = RectOf(tb, built.Root);
                    if (tr.Right > r.Right - 18)
                    {
                        Console.WriteLine("  [x] " + file + ": bar[" + i + "] text right "
                                          + Math.Round(tr.Right, 1) + " runs into the copy slot ("
                                          + Math.Round(r.Right - 18, 1) + ")");
                        _bad++;
                    }
                    double midOk = Math.Abs((tr.Top + tr.Height / 2) - (r.Top + BarH / 2));
                    if (midOk > 2.0)
                    {
                        Console.WriteLine("  [x] " + file + ": bar[" + i + "] text is not vertically "
                                          + "centred (" + Math.Round(midOk, 1) + "px off)");
                        _bad++;
                    }
                    /* 条高 30 里塞不下两行：正文一旦折行就是版面坏了 */
                    if (tr.Height > 22)
                    {
                        Console.WriteLine("  [x] " + file + ": bar[" + i + "] text is "
                                          + Math.Round(tr.Height, 1) + "px tall -- it wrapped");
                        _bad++;
                    }
                }
            }

            Console.WriteLine("  clip layout: bars=" + bars.Count
                              + " pitch=" + (BarH + BarGap)
                              + " material=bg" + Hex(cardBg) + "/edge" + Hex(cardEdge)
                              + "/r" + Math.Round(cardRadius, 1));

            PixelClipBars(bars, built.Root, look, file);
        }

        /* 像素级：条真的被画成那个材质了吗、条和条之间真的有缝吗、
           正文真的落在条里吗、两条之间有没有互相压住。

           这一组是给「控件树对了但画面不对」兜底的：属性可以是对的，
           而被别的东西盖住、被裁掉、被半透明背景糊掉，只有像素能说话。 */
        static void PixelClipBars(List<Border> bars, Visual root, Look look, string file)
        {
            if (_bmp == null || bars.Count == 0) return;

            double listRight = W - Layout.PadExp;
            int fillOk = 0, edgeOk = 0, gapOk = 0, textOk = 0;

            for (int i = 0; i < bars.Count; i++)
            {
                Rect r = RectOf(bars[i], root);

                /* ① 条内部：必须是实心 CardBg。
                   采样的 y 要选在正文下沿之下（正文占 6~24，条高 30），
                   否则采到的是字本身 —— 第一版就栽在这儿。 */
                Color fill = PixelAt(r.Left + 60, r.Top + BarH - 2.5);
                if (Near(fill, look.CardBg, 6)) fillOk++;
                else
                {
                    Console.WriteLine("  [x] " + file + ": bar[" + i + "] painted "
                                      + Hex(fill) + " instead of CardBg " + Hex(look.CardBg));
                    _bad++;
                }

                /* ② 上沿那 1px 描边：CardEdge（浅色外观里比填充暗，深色里更亮） */
                Color edge = PixelAt(r.Left + 60, r.Top + 0.2);
                if (Near(edge, look.CardEdge, 10)) edgeOk++;
                else
                {
                    Console.WriteLine("  [x] " + file + ": bar[" + i + "] top edge painted "
                                      + Hex(edge) + " instead of CardEdge " + Hex(look.CardEdge));
                    _bad++;
                }

                /* ③ 条间距：两条之间必须是底色，不能被条的描边或阴影连成一片 */
                if (i > 0)
                {
                    double gapY = r.Top - BarGap / 2;
                    Color gap = PixelAt(r.Left + 60, gapY);
                    if (Near(gap, _bmpBase, 8)) gapOk++;
                    else
                    {
                        Console.WriteLine("  [x] " + file + ": the gap above bar[" + i + "] is "
                                          + Hex(gap) + " -- the bars are not visually separated");
                        _bad++;
                    }
                }

                /* ④ 正文：条里必须真的有一片和底色不同的像素（字画上去了） */
                double ink = BandInk(r.Left + 34, r.Right - 22, r.Top + 4, r.Top + BarH - 4,
                                     look.CardBg);
                if (ink > 0.02) textOk++;
                else
                {
                    Console.WriteLine("  [x] " + file + ": bar[" + i + "] has no text ink ("
                                      + Pct(ink) + ")");
                    _bad++;
                }
            }

            /* 条不许重叠：第 i 条的底边必须在第 i+1 条的顶边之上 */
            for (int i = 1; i < bars.Count; i++)
            {
                Rect a = RectOf(bars[i - 1], root), b = RectOf(bars[i], root);
                if (a.Bottom > b.Top + 0.5)
                {
                    Console.WriteLine("  [x] " + file + ": bar[" + (i - 1) + "] overlaps bar[" + i + "]");
                    _bad++;
                }
                if (Math.Abs(b.Left - listRight + b.Width) > 1.0)
                {
                    Console.WriteLine("  [x] " + file + ": bar[" + i + "] does not reach the right edge");
                    _bad++;
                }
            }

            Console.WriteLine("  clip pixels: fill=" + fillOk + "/" + bars.Count
                              + " edge=" + edgeOk + "/" + bars.Count
                              + " gap=" + gapOk + "/" + (bars.Count - 1)
                              + " text=" + textOk + "/" + bars.Count);
        }

        /* 现搭待办页，取第一张纸片的材质，作为「同一种条」的比色基准。
           look 必须跟着被验的那一页走：这四套外观的灰条本来就不同色。 */
        static bool TodoSample(Look look, out Color bg, out Color edge, out double radius)
        {
            bg = Colors.Transparent;
            edge = Colors.Transparent;
            radius = -1;

            Todos.Add("比色用的待办条", 0);
            Built built = Layout.Build("todo", "expanded", W, H, look,
                                       Art.ByKey("aurora"), new Snap(), new CardActions(), false);
            TodoTower tower = built.Extra as TodoTower;
            if (tower != null) tower.SettleNow();

            List<Border> cards = CollectUid2(built.Root, "tw-card");
            bool ok = false;
            if (cards.Count > 0)
            {
                SolidColorBrush bgb = cards[0].Background as SolidColorBrush;
                SolidColorBrush edb = cards[0].BorderBrush as SolidColorBrush;
                if (bgb != null && edb != null)
                {
                    bg = bgb.Color;
                    edge = edb.Color;
                    radius = cards[0].CornerRadius.TopLeft;
                    ok = true;
                }
            }
            built.Halt();
            /* 比色用的那条待办故意留在沙箱里：Todos 没有 Clear()，
               而且 -- 这本来就是临时目录，跑完整个进程就没了。 */
            return ok;
        }

        /* ── 中转站页 ─────────────────────────────────────────────────── */

        static int ShotTransfer(string lookKey, string file, bool hover)
        {
            Look look = Look.ByKey(lookKey);
            Art art = Art.ByKey("aurora");
            int before = _bad;

            bool wasHover = Transfer.Hover;
            Transfer.Hover = hover;

            Built built = Layout.Build("transfer", "expanded", W, H, look, art, new Snap(),
                                       new CardActions(), false);
            Shot(built, file, lookKey == "frost", file.StartsWith("04-"));

            Transfer.View[] items = Transfer.Items();
            List<FrameworkElement> cells = CollectUid(built.Root, "trow");

            if (hover)
            {
                Console.WriteLine("  transfer hover: cells=" + cells.Count + " (expect 0)");
                if (cells.Count != 0)
                {
                    Console.WriteLine("  [x] " + file + ": the drop zone should replace the icons");
                    _bad++;
                }
                if (!HasText(built.Root, "松手暂存"))
                {
                    Console.WriteLine("  [x] " + file + ": drop hint missing");
                    _bad++;
                }
            }
            else if (items.Length == 0)
            {
                Console.WriteLine("  transfer empty: cells=" + cells.Count + " (expect 0)");
                if (cells.Count != 0)
                {
                    Console.WriteLine("  [x] " + file + ": empty page still draws cells");
                    _bad++;
                }
                if (!HasText(built.Root, "把文件拖到这里暂存"))
                {
                    Console.WriteLine("  [x] " + file + ": empty hint missing");
                    _bad++;
                }
            }
            else
            {
                CheckCells(built, items, cells, file);
            }

            built.Halt();
            Transfer.Hover = wasHover;
            return _bad - before;
        }

        static void CheckCells(Built built, Transfer.View[] items,
                               List<FrameworkElement> cells, string file)
        {
            /* 和 Layout 里同一套反算，但在这里独立算一遍 */
            double listW = W - Layout.PadExp * 2;
            double listH = H - 46 - Layout.PadExp;
            int cols = (int)Math.Floor(listW / 64.0);
            int rows = (int)Math.Round(listH / 96.0);
            double cellW = listW / cols;
            double cellH = listH / rows;
            int cap = cols * rows;
            bool more = items.Length > cap;
            /* 装不下时最后一格让给「还有 N 项」那块 —— 它没有 trow 标记
               （它不接手势），所以按格子数算时要减一。 */
            int expect = more ? cap - 1 : items.Length;

            Console.WriteLine("  transfer grid: " + cols + "x" + rows + " cell="
                              + Math.Round(cellW, 1) + "x" + Math.Round(cellH, 1)
                              + " cells=" + cells.Count + " expect=" + expect
                              + " items=" + items.Length + (more ? " +overflow" : ""));

            if (cells.Count != expect)
            {
                Console.WriteLine("  [x] " + file + ": cell count " + cells.Count
                                  + " != " + expect);
                _bad++;
            }

            if (more)
            {
                /* 溢出那块必须真的报数：装不下却默默截断，用户会以为东西丢了 */
                string want = "还有 " + (items.Length - expect) + " 项";
                if (!HasText(built.Root, want))
                {
                    Console.WriteLine("  [x] " + file + ": overflow tile does not say '" + want + "'");
                    _bad++;
                }
                else
                {
                    Console.WriteLine("  transfer overflow: '" + want + "'");
                }
            }

            List<int> iconSizes = new List<int>();
            for (int i = 0; i < cells.Count; i++)
            {
                FrameworkElement cell = cells[i];
                Rect r = RectOf(cell, built.Root);
                int col = i % cols, row = i / cols;
                double wantX = Layout.PadExp + col * cellW;
                double wantY = 46 + row * cellH;

                if (Math.Abs(r.Left - wantX) > 1.0 || Math.Abs(r.Top - wantY) > 1.0)
                {
                    Console.WriteLine("  [x] " + file + ": cell[" + i + "] at ("
                                      + Math.Round(r.Left, 1) + "," + Math.Round(r.Top, 1)
                                      + ") != (" + Math.Round(wantX, 1) + "," + Math.Round(wantY, 1) + ")");
                    _bad++;
                }
                if ((cell.Tag as string) != "act")
                {
                    Console.WriteLine("  [x] " + file + ": cell[" + i + "] is not an action hit "
                                      + "-- pressing it would drag the island");
                    _bad++;
                }
                if (r.Right > W - Layout.PadExp + 1.0 || r.Bottom > 46 + listH + 1.0)
                {
                    Console.WriteLine("  [x] " + file + ": cell[" + i + "] overflows the list area");
                    _bad++;
                }

                /* 图标：必须是大的。16px 那张小图标放大到 48 只会糊成一块，
                   所以这里真的去看位图的像素宽度。 */
                Image img = FindChild<Image>(cell);
                Rect? iconRect = null;
                if (img != null)
                {
                    BitmapSource bs = img.Source as BitmapSource;
                    if (bs != null) iconSizes.Add(bs.PixelWidth);
                    iconRect = RectOf(img, built.Root);
                    if (Math.Abs(img.Width - 48) > 0.5 || Math.Abs(img.Height - 48) > 0.5)
                    {
                        Console.WriteLine("  [x] " + file + ": cell[" + i + "] icon box "
                                          + Math.Round(img.Width) + " != 48");
                        _bad++;
                    }
                }
                else
                {
                    TextBlock fb = FindChild<TextBlock>(cell);
                    if (fb != null) iconRect = RectOf(fb, built.Root);
                }

                /* 名字：必须在图标下面，且在格子里 */
                TextBlock label = null;
                List<TextBlock> texts = new List<TextBlock>();
                CollectTextBlocks(cell, texts);
                for (int k = 0; k < texts.Count; k++)
                {
                    /* 名字是那唯一一颗有宽度约束、且不在 ✕ 键里的字段 */
                    if (texts[k].TextWrapping == TextWrapping.Wrap) { label = texts[k]; break; }
                }
                if (label == null)
                {
                    Console.WriteLine("  [x] " + file + ": cell[" + i + "] has no wrapped name label");
                    _bad++;
                }
                else
                {
                    Rect lr = RectOf(label, built.Root);
                    if (iconRect != null && lr.Top < iconRect.Value.Bottom - 1.0)
                    {
                        Console.WriteLine("  [x] " + file + ": cell[" + i + "] name starts above "
                                          + "the icon's bottom");
                        _bad++;
                    }
                    if (lr.Right > r.Right + 0.5 || lr.Left < r.Left - 0.5)
                    {
                        Console.WriteLine("  [x] " + file + ": cell[" + i + "] name spills out "
                                          + "sideways");
                        _bad++;
                    }
                    if (lr.Bottom > r.Bottom + 0.5)
                    {
                        Console.WriteLine("  [x] " + file + ": cell[" + i + "] name spills out "
                                          + "below the cell");
                        _bad++;
                    }
                    if (lr.Height > 30)
                    {
                        Console.WriteLine("  [x] " + file + ": cell[" + i + "] name is "
                                          + Math.Round(lr.Height, 1) + "px tall (more than 2 lines)");
                        _bad++;
                    }
                }

                /* 悬停前：选中框和 ✕ 都该是收起的。这两件东西常驻显示的话，
                   12 格会变成 12 个 ✕ 和 12 个方块，页面立刻脏掉。 */
                List<Border> borders = new List<Border>();
                CollectBorders(cell, borders);
                int shown = 0;
                for (int k = 0; k < borders.Count; k++)
                {
                    if (borders[k].Visibility == Visibility.Visible) shown++;
                }
                if (shown != 0)
                {
                    Console.WriteLine("  [x] " + file + ": cell[" + i + "] shows " + shown
                                      + " decoration border(s) before hover");
                    _bad++;
                }
            }

            if (iconSizes.Count > 0)
            {
                int small = 0;
                string all = "";
                for (int i = 0; i < iconSizes.Count; i++)
                {
                    if (iconSizes[i] < 32) small++;
                    all += (i > 0 ? "," : "") + iconSizes[i];
                }
                Console.WriteLine("  transfer icons px=[" + all + "]");
                if (small > 0)
                {
                    Console.WriteLine("  [x] " + file + ": " + small
                                      + " icon(s) came back smaller than 32px -- the large-icon "
                                      + "path (SHGetImageList) did not work");
                    _bad++;
                }
            }
            else
            {
                Console.WriteLine("  ! " + file + ": no shell icons at all (glyph fallback in use)");
            }

            /* 像素：每一格的图标带要真的画出图标（有颜色、有细节），
               名字带要真的画出字。控件都在、但画面空白，这一条会抓住。 */
            int iconInkOk = 0, nameInkOk = 0, colorful = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                Rect r = RectOf(cells[i], built.Root);
                double contentH = 48 + 6 + 28;
                double y0 = Math.Max(4, Math.Round((r.Height - contentH) / 2));
                double ix = Math.Round((r.Width - 48) / 2);

                double inkIcon = BandInk(r.Left + ix + 2, r.Left + ix + 46,
                                         r.Top + y0 + 2, r.Top + y0 + 46, _bmpBase);
                if (inkIcon > 0.10) iconInkOk++;

                int distinct = DistinctColors(r.Left + ix + 2, r.Left + ix + 46,
                                              r.Top + y0 + 2, r.Top + y0 + 46);
                if (distinct >= 8) colorful++;

                double inkName = BandInk(r.Left + 6, r.Right - 6,
                                         r.Top + y0 + 48 + 6, r.Top + y0 + 48 + 6 + 28,
                                         _bmpBase);
                if (inkName > 0.03) nameInkOk++;
            }

            Console.WriteLine("  transfer pixels: iconInk=" + iconInkOk + "/" + cells.Count
                              + " colorful=" + colorful + "/" + cells.Count
                              + " nameInk=" + nameInkOk + "/" + cells.Count);

            if (iconInkOk < cells.Count)
            {
                Console.WriteLine("  [x] " + file + ": " + (cells.Count - iconInkOk)
                                  + " cell(s) painted no icon");
                _bad++;
            }
            if (colorful < cells.Count)
            {
                Console.WriteLine("  [!!] " + file + ": " + (cells.Count - colorful)
                                  + " cell(s) have a flat (single-colour) icon -- looks like the "
                                  + "glyph fallback rather than a shell icon");
            }
            if (nameInkOk < cells.Count)
            {
                Console.WriteLine("  [x] " + file + ": " + (cells.Count - nameInkOk)
                                  + " cell(s) painted no name");
                _bad++;
            }

            /* 长名字必须真的折成两行：一行 11px 文字大约 14 高，两行 26 左右。
               只验「不超过 30」会让「全被截成一行 + 省略号」也算过。 */
            List<TextBlock> every = new List<TextBlock>();
            CollectTextBlocks(built.Root, every);
            bool sawTwoLines = false;
            for (int i = 0; i < every.Count; i++)
            {
                if (every[i].ActualHeight >= 20 && every[i].TextWrapping == TextWrapping.Wrap)
                    sawTwoLines = true;
            }
            if (!sawTwoLines)
            {
                Console.WriteLine("  [x] " + file + ": no name label ever wrapped to a second line "
                                  + "-- long file names are being cut to one line");
                _bad++;
            }
        }

        /* ── 光栅化 ──────────────────────────────────────────────────── */

        static void Shot(Built built, string name, bool light)
        {
            Shot(built, name, light, false);
        }

        static void Shot(Built built, string name, bool light, bool ascii)
        {
            Border host = new Border();
            host.Width = W;
            host.Height = H;
            host.Background = new SolidColorBrush(light ? BaseLight : Base);
            host.Child = built.Root;

            host.Measure(new Size(W, H));
            host.Arrange(new Rect(0, 0, W, H));
            host.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Loaded,
                new Action(delegate { host.UpdateLayout(); }));

            int pw = (int)Math.Round(W * Scale), ph = (int)Math.Round(H * Scale);
            RenderTargetBitmap bmp = new RenderTargetBitmap(pw, ph, 96 * Scale, 96 * Scale,
                                                           PixelFormats.Pbgra32);
            bmp.Render(host);
            _bmp = bmp;
            _bmpBase = light ? BaseLight : Base;

            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            string path = Path.Combine(_out, name);
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                enc.Save(fs);
            Console.WriteLine("  -> " + path + "  (" + pw + "x" + ph + ")");

            /* 画出去了没有：整页的墨量。0 就是「控件都在，但一个像素都没画」。 */
            double ink = PageInk(bmp, light);
            Console.WriteLine("  ink " + name + ": " + Pct(ink));
            if (ink < 0.01)
            {
                Console.WriteLine("  [x] " + name + ": nothing was actually drawn");
                _bad++;
            }

            if (ascii) AsciiDump(bmp, name);
        }

        /* 把这页压成一张终端能读的灰度图。

           为什么要它：读不了 PNG 的时候（命令行、CI、没有图片查看器的机器），
           「版面到底长什么样」就只剩数字可看，而数字很难说出「六条是分开的」。
           这张图能：条是横排的实心带、中间有缝、右边一格一格分开，一眼看得出。 */
        static void AsciiDump(RenderTargetBitmap bmp, string name)
        {
            const int cols = 120;
            const string ramp = " .:-=+*#%@";

            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            int rows = (int)Math.Floor(cols * (double)h / w / 2.1);
            if (rows < 8) rows = 8;

            byte[] px = new byte[w * h * 4];
            bmp.CopyPixels(px, w * 4, 0);

            double sx = (double)w / cols, sy = (double)h / rows;
            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            for (int r = 0; r < rows; r++)
            {
                int y0 = (int)(r * sy), y1 = Math.Min(h, (int)((r + 1) * sy));
                for (int c = 0; c < cols; c++)
                {
                    int x0 = (int)(c * sx), x1 = Math.Min(w, (int)((c + 1) * sx));

                    double sum = 0;
                    int n = 0;
                    for (int y = y0; y < y1; y += 2)
                    {
                        int off = y * w * 4;
                        for (int x = x0; x < x1; x += 2)
                        {
                            int i = off + x * 4;
                            sum += 0.2126 * px[i + 2] + 0.7152 * px[i + 1] + 0.0722 * px[i];
                            n++;
                        }
                    }
                    double v = n == 0 ? 0 : sum / n;
                    int idx = (int)Math.Floor((v - 16) / (255 - 16) * ramp.Length);
                    if (idx < 0) idx = 0;
                    if (idx >= ramp.Length) idx = ramp.Length - 1;
                    sb.Append(ramp[idx]);
                }
                sb.Append('\n');
            }

            Console.WriteLine("  ascii " + name + ":");
            Console.Write(sb.ToString());
            Console.WriteLine();
        }

        static double PageInk(RenderTargetBitmap bmp, bool light)
        {
            int w = bmp.PixelWidth, h = bmp.PixelHeight;
            byte[] px = new byte[w * h * 4];
            bmp.CopyPixels(px, w * 4, 0);

            Color baseC = light ? BaseLight : Base;
            int hit = 0, total = 0;
            for (int y = 0; y < h; y += 3)
            {
                int off = y * w * 4;
                for (int x = 0; x < w; x += 3)
                {
                    total++;
                    int i = off + x * 4;
                    if (Math.Abs(px[i] - baseC.B) > 8 || Math.Abs(px[i + 1] - baseC.G) > 8
                        || Math.Abs(px[i + 2] - baseC.R) > 8) hit++;
                }
            }
            return total == 0 ? 0 : (double)hit / total;
        }

        /* ── 小工具 ──────────────────────────────────────────────────── */

        /* 采样一个 DIP 坐标的像素。3x 光栅化 → 像素坐标 = DIP × Scale。 */
        static Color PixelAt(double x, double y)
        {
            if (_bmp == null) return Colors.Transparent;
            int px = (int)Math.Round(x * Scale);
            int py = (int)Math.Round(y * Scale);
            if (px < 0) px = 0;
            if (py < 0) py = 0;
            if (px >= _bmp.PixelWidth) px = _bmp.PixelWidth - 1;
            if (py >= _bmp.PixelHeight) py = _bmp.PixelHeight - 1;

            byte[] one = new byte[4];
            _bmp.CopyPixels(new Int32Rect(px, py, 1, 1), one, 4, 0);
            return Color.FromArgb(one[3], one[2], one[1], one[0]);   /* BGRA */
        }

        static bool Near(Color a, Color b, int tol)
        {
            return Math.Abs(a.R - b.R) <= tol && Math.Abs(a.G - b.G) <= tol
                   && Math.Abs(a.B - b.B) <= tol;
        }

        /* 一片矩形里「和参照色不同」的像素占比 —— 用来判断这片区域有没有东西。 */
        static double BandInk(double x0, double x1, double y0, double y1, Color over)
        {
            if (_bmp == null || x1 <= x0 || y1 <= y0) return 0;
            int px0 = Math.Max(0, (int)Math.Round(x0 * Scale));
            int px1 = Math.Min(_bmp.PixelWidth - 1, (int)Math.Round(x1 * Scale));
            int py0 = Math.Max(0, (int)Math.Round(y0 * Scale));
            int py1 = Math.Min(_bmp.PixelHeight - 1, (int)Math.Round(y1 * Scale));
            int w = px1 - px0 + 1, h = py1 - py0 + 1;
            if (w <= 0 || h <= 0) return 0;

            byte[] buf = new byte[w * h * 4];
            _bmp.CopyPixels(new Int32Rect(px0, py0, w, h), buf, w * 4, 0);

            int hit = 0, total = 0;
            for (int y = 0; y < h; y++)
            {
                int off = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    total++;
                    int i = off + x * 4;
                    if (Math.Abs(buf[i] - over.B) > 10 || Math.Abs(buf[i + 1] - over.G) > 10
                        || Math.Abs(buf[i + 2] - over.R) > 10) hit++;
                }
            }
            return total == 0 ? 0 : (double)hit / total;
        }

        /* 一片矩形里出现了多少种明显不同的颜色 —— 图标（有颜色、有细节）和
           「一枚字形」（一种颜色）在这条上分得很开。 */
        static int DistinctColors(double x0, double x1, double y0, double y1)
        {
            if (_bmp == null || x1 <= x0 || y1 <= y0) return 0;
            int px0 = Math.Max(0, (int)Math.Round(x0 * Scale));
            int px1 = Math.Min(_bmp.PixelWidth - 1, (int)Math.Round(x1 * Scale));
            int py0 = Math.Max(0, (int)Math.Round(y0 * Scale));
            int py1 = Math.Min(_bmp.PixelHeight - 1, (int)Math.Round(y1 * Scale));
            int w = px1 - px0 + 1, h = py1 - py0 + 1;
            if (w <= 0 || h <= 0) return 0;

            byte[] buf = new byte[w * h * 4];
            _bmp.CopyPixels(new Int32Rect(px0, py0, w, h), buf, w * 4, 0);

            HashSet<int> seen = new HashSet<int>();
            for (int y = 0; y < h; y++)
            {
                int off = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    int i = off + x * 4;
                    /* 6 bit/通道就够了：抗锯齿产生的邻近色不该算成新颜色 */
                    int key = ((buf[i] >> 2) << 12) | ((buf[i + 1] >> 2) << 6) | (buf[i + 2] >> 2);
                    seen.Add(key);
                }
            }
            return seen.Count;
        }

        static Rect RectOf(FrameworkElement el, Visual root)
        {
            try
            {
                return el.TransformToAncestor(root).TransformBounds(
                    new Rect(0, 0, el.ActualWidth > 0 ? el.ActualWidth : el.Width,
                             el.ActualHeight > 0 ? el.ActualHeight : el.Height));
            }
            catch (Exception) { return new Rect(0, 0, 0, 0); }
        }

        static List<FrameworkElement> CollectUid(DependencyObject d, string uid)
        {
            List<FrameworkElement> into = new List<FrameworkElement>();
            Walk(d, uid, into, null);
            return into;
        }

        static List<Border> CollectUid2(DependencyObject d, string uid)
        {
            List<FrameworkElement> all = CollectUid(d, uid);
            List<Border> into = new List<Border>();
            for (int i = 0; i < all.Count; i++)
            {
                Border b = all[i] as Border;
                if (b != null) into.Add(b);
            }
            return into;
        }

        static void Walk(DependencyObject d, string uid, List<FrameworkElement> into,
                         List<Border> borders)
        {
            if (d == null) return;
            FrameworkElement fe = d as FrameworkElement;
            if (fe != null && fe.Uid == uid) into.Add(fe);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), uid, into, borders);
        }

        static void CollectTextBlocks(DependencyObject d, List<TextBlock> into)
        {
            if (d == null) return;
            TextBlock tb = d as TextBlock;
            if (tb != null) into.Add(tb);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) CollectTextBlocks(VisualTreeHelper.GetChild(d, i), into);
        }

        static void CollectBorders(DependencyObject d, List<Border> into)
        {
            if (d == null) return;
            Border b = d as Border;
            if (b != null) into.Add(b);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) CollectBorders(VisualTreeHelper.GetChild(d, i), into);
        }

        static T FindChild<T>(DependencyObject d) where T : class
        {
            if (d == null) return null;
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                DependencyObject c = VisualTreeHelper.GetChild(d, i);
                T hit = c as T;
                if (hit != null) return hit;
                T deep = FindChild<T>(c);
                if (deep != null) return deep;
            }
            return null;
        }

        static bool HasText(DependencyObject root, string needle)
        {
            List<TextBlock> all = new List<TextBlock>();
            CollectTextBlocks(root, all);
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].Text != null
                    && all[i].Text.IndexOf(needle, StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        static string Hex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        static string Pct(double v)
        {
            return Math.Round(v * 100, 1) + "%";
        }
    }
}
