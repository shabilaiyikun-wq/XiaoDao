using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinIsland
{
    /* ══ 天气页的离线截图与纯逻辑校验 ═════════════════════════════════════

       两件事：

       1) 纯逻辑（不碰 WPF、不碰网络）：WMO 码 → 中文/图标类型、
          中文城市名的「广州市 → 广州 / 新疆 → 乌鲁木齐」那套改写。
          这些函数错了，界面看着完全正常 —— 温度不对、城市不对才知道出事，
          而那时候已经在用户机器上了。

       2) 版面：把 desktop/Layout.cs 的天气卡真的搭出来、真的排一次版、
          再光栅化成 PNG。不开窗口、不碰网络、不动用户缓存
          （AppDir 指到临时沙箱，weather.txt 由这个程序自己写）。

       为什么非得画出来看：断言只能证明「字在面板里」，
       证明不了「大温度没压到右边的城市名」。上一页（待办）就是靠这条路
       才发现字压在按钮上的。

       跑法：desktop\verify\weather-shot.cmd [输出目录]
       默认输出到 work\weather-shot\。 */

    static class WeatherShot
    {
        const double W = 468, H = 268;      /* 和 Scenes.All 里 weather 那条一致 */
        const double IdleW = 82, IdleH = 30;
        const double Scale = 3.0;

        static string _out;
        static int _bad;

        [STAThread]
        static int Main(string[] args)
        {
            _out = args != null && args.Length > 0
                ? args[0]
                : Path.Combine(Path.GetTempPath(), "weather-shot");
            try { Directory.CreateDirectory(_out); }
            catch (Exception ex)
            {
                Console.WriteLine("[x] cannot create " + _out + ": " + ex.Message);
                return 1;
            }

            /* 沙箱：天气缓存读的是 AppDir 下的 weather.txt */
            string sandbox = Path.Combine(Path.GetTempPath(), "WinIsland-weather-shot");
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, true);
            Directory.CreateDirectory(sandbox);
            AppDir.SetRoot(sandbox);

            int bad = 0;
            try
            {
                bad += Logic();

                /* 01~04：四种天气各画一张，看图标和文案对不对得起来 */
                bad += Card("clear", 0, true, "01-card-clear.png");
                bad += Card("rain", 63, true, "02-card-rain.png");
                bad += Card("snow", 73, true, "03-card-snow.png");
                bad += Card("thunder", 95, true, "04-card-thunder.png");
                bad += Card("night", 0, false, "05-card-night.png");

                bad += Empty("06-empty.png");
                bad += IdleWithWeather("07-idle-weather.png", "12.4");
                /* 最宽的那一种温度：负数带小数。收起态只有 82 宽，
                   「-18.4°」是它能遇到的最长字符串 —— 这一条过了，
                   别的温度就都装得下。 */
                bad += IdleWithWeather("07b-idle-cold.png", "-18.4");
                bad += IdleNoWeather("08-idle-noweather.png");
            bad += IdlePetDocked("08b-idle-petdock.png");
                bad += IconSheet("09-icons.png");
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
                Console.WriteLine("WEATHER SHOT OK -> " + _out);
                return 0;
            }
            Console.WriteLine("WEATHER SHOT FAILED bad=" + total);
            return 1;
        }

        /* ── 纯逻辑 ─────────────────────────────────────────────────── */

        static int Logic()
        {
            int bad = 0;

            /* WMO 码 → 中文。挑几个有代表性又容易写错的 */
            bad += Eq("Describe(0)", Weather.Describe(0), "晴");
            bad += Eq("Describe(2)", Weather.Describe(2), "多云");
            bad += Eq("Describe(45)", Weather.Describe(45), "雾");
            bad += Eq("Describe(61)", Weather.Describe(61), "小雨");
            bad += Eq("Describe(63)", Weather.Describe(63), "中雨");
            bad += Eq("Describe(73)", Weather.Describe(73), "中雪");
            bad += Eq("Describe(95)", Weather.Describe(95), "雷阵雨");
            bad += Eq("Describe(999)", Weather.Describe(999), "多云");

            /* WMO 码 → 图标类型，白天黑夜要分开 */
            bad += Eq("Kind(0,day)", Weather.Kind(0, true).ToString(), "Sun");
            bad += Eq("Kind(0,night)", Weather.Kind(0, false).ToString(), "Moon");
            bad += Eq("Kind(1,day)", Weather.Kind(1, true).ToString(), "Partly");
            bad += Eq("Kind(1,night)", Weather.Kind(1, false).ToString(), "PartlyNight");
            bad += Eq("Kind(3)", Weather.Kind(3, true).ToString(), "Cloud");
            bad += Eq("Kind(53)", Weather.Kind(53, true).ToString(), "Drizzle");
            bad += Eq("Kind(65)", Weather.Kind(65, true).ToString(), "Rain");
            bad += Eq("Kind(71)", Weather.Kind(71, true).ToString(), "Snow");
            bad += Eq("Kind(81)", Weather.Kind(81, true).ToString(), "Shower");
            bad += Eq("Kind(99)", Weather.Kind(99, true).ToString(), "Thunder");
            bad += Eq("Kind(48)", Weather.Kind(48, true).ToString(), "Fog");

            /* 中文城市名的改写。这几条是地理库那几个坑的全部防线 */
            bad += Eq("BaseName(广州市)", Weather.BaseName("广州市"), "广州");
            bad += Eq("BaseName(北京市)", Weather.BaseName("北京市"), "北京");
            bad += Eq("BaseName(北京)", Weather.BaseName("北京"), "北京");
            bad += Eq("BaseName(Tokyo)", Weather.BaseName("Tokyo"), "Tokyo");
            bad += Eq("BaseName(津市)", Weather.BaseName("津市"), "津市");

            /* 「广州市」要同时查「广州市」和「广州」 */
            bad += Has(Weather.CityQueries("广州市"), "广州", "queries(广州市) 含 广州");
            /* 「新疆」要带上省会「乌鲁木齐」 */
            bad += Has(Weather.CityQueries("新疆"), "乌鲁木齐", "queries(新疆) 含 乌鲁木齐");
            bad += Has(Weather.CityQueries("新疆维吾尔自治区"), "乌鲁木齐",
                       "queries(新疆维吾尔自治区) 含 乌鲁木齐");
            /* 两字简称要补一个「XX市」 */
            bad += Has(Weather.CityQueries("苏州"), "苏州市", "queries(苏州) 含 苏州市");
            /* 省名不该补出「新疆市」这种不存在的东西 */
            bad += NotHas(Weather.CityQueries("新疆"), "新疆市", "queries(新疆) 不该含 新疆市");
            /* 非中文原样返回 */
            bad += Eq("queries(Tokyo).Count", Weather.CityQueries("Tokyo").Count.ToString(), "1");

            /* 缓存往返：写一份假的 weather.txt，读回来要一模一样 */
            bad += CacheRoundTrip();

            /* 指定城市要能存下来 —— 用户点一次「指定城市…」，
               下次开程序还得是他填的那个，不然这个功能等于没做 */
            bad += CityPersist();

            return bad;
        }

        static int CacheRoundTrip()
        {
            int bad = 0;
            SeedCache(3, true);
            Weather.LoadCache();

            WeatherNow wx = Weather.Current();
            if (!wx.Has) { Console.WriteLine("  [x] cache: 读回来 has=False"); return 1; }

            bad += Eq("cache.place", wx.Place, "杭州");
            bad += Eq("cache.region", wx.Region, "浙江省 · 中国");
            bad += Eq("cache.temp", wx.Temp.ToString("0.0", CultureInfo.InvariantCulture), "12.4");
            bad += Eq("cache.feels", wx.HasFeels ? wx.Feels.ToString("0.0", CultureInfo.InvariantCulture) : "none", "9.8");
            bad += Eq("cache.humidity", wx.Humidity.ToString(), "58");
            bad += Eq("cache.code", wx.Code.ToString(), "3");
            bad += Eq("cache.days", wx.Days.Count.ToString(), "7");
            bad += Eq("cache.day0", wx.Days[0].Label, "今天");
            bad += Eq("cache.day3", wx.Days[3].Label, "周四");
            bad += Eq("cache.sunrise", wx.Sunrise, "06:31");
            return bad;
        }

        /* 指定城市要活过一次重启。

           这个功能只在「IP 定位不准」时才被用到 —— 而那正是最容易出问题的机器
           （挂着代理、或者走的是公司的出口）。如果它其实没存下来，用户会看到
           自己填的城市下次打开又变回错的，然后得出「这功能是坏的」这个结论。 */
        static int CityPersist()
        {
            int bad = 0;

            Weather.SetCity("上海");

            string[] lines = AppDir.ReadLines("ui.txt");
            bool found = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i] == null || !lines[i].StartsWith("weatherCity=")) continue;
                found = true;
                bad += Eq("ui.txt weatherCity",
                          AppDir.Decode(lines[i].Substring("weatherCity=".Length)), "上海");
            }
            if (!found)
            {
                bad++;
                Console.WriteLine("  [x] ui.txt 里没有 weatherCity 这一行，城市不会活过重启");
            }

            /* 再走一遍「下次启动」：直接改盘上的文件，再 Load。
               不能只验「静态字段还是刚才那个值」—— 那在 Load 什么都不做时也会通过。 */
            List<string> disk = new List<string>();
            disk.Add("scene=media");
            disk.Add("look=glass");
            disk.Add("art=aurora");
            disk.Add("weatherCity=" + AppDir.Encode("成都"));
            AppDir.WriteLines("ui.txt", disk);

            Prefs.Load();
            bad += Eq("从磁盘读回 weatherCity", Prefs.WeatherCity, "成都");

            /* 留空 = 回到自动定位，这本身是合法操作，不是取消 */
            Weather.SetCity("");
            bad += Eq("清空后静态字段", Prefs.WeatherCity, "");
            bad += Eq("清空后重新 Load", Prefs.WeatherCity, "");

            return bad;
        }

        /* ── 四种天气的卡片 ─────────────────────────────────────────── */

        static int Card(string tag, int code, bool day, string file)
        {
            SeedCache(code, day);
            Weather.LoadCache();

            Snap s = new Snap();
            s.Wx = Weather.Current();
            if (!s.Wx.Has) { Console.WriteLine("  [x] " + tag + ": 缓存没读出来"); return 1; }

            Look look = Look.ByKey("glass");
            Art art = Art.ByKey("aurora");
            Built built = Layout.Build("weather", "expanded", W, H, look, art, s,
                                       new CardActions(), false);

            int bad = 0;
            /* 卡片上必须有这几样东西，少了就是版面漏画 */
            bad += Text(built, s.Wx.TempText, tag + " 温度");
            bad += Text(built, s.Wx.Place, tag + " 城市");
            bad += Text(built, s.Wx.Describe, tag + " 天气描述");
            bad += Text(built, "今天", tag + " 今天");
            bad += Text(built, "明天", tag + " 明天");
            bad += Text(built, "后天", tag + " 后天");
            bad += Text(built, "体感", tag + " 体感标签");
            bad += Text(built, "湿度", tag + " 湿度标签");
            bad += Text(built, "风速", tag + " 风速标签");
            bad += TextContains(built, "日出", tag + " 日出日落");

            /* 画出来再量：这是整条路上唯一能证明「看着对」的一步 */
            Border host = Host(built.Root, W, H);
            RenderTargetBitmap bmp = Shoot(host, W, H, file);

            bad += Hue(tag, bmp, code, day);
            bad += Bands(tag, bmp);
            bad += DayLabelInk(tag, bmp);
            bad += Bounds(built.Root, W, H, file);
            bad += NoOverlap(built.Root, W, H, file);

            /* 首次和空态各打一张字符预览，别的只留数字 —— 输出得能读完 */
            if (tag == "clear" || tag == "snow") Ascii(bmp, file);

            built.Halt();
            return bad;
        }

        /* ── 没有数据 ───────────────────────────────────────────────── */

        static int Empty(string file)
        {
            /* 沙箱里清掉缓存 = 这台机器从没取到过天气 */
            string p = AppDir.FilePath("weather.txt");
            if (File.Exists(p)) File.Delete(p);

            Weather.LoadCache();
            WeatherNow wx = Weather.Current();

            Snap s = new Snap();
            s.Wx = wx;
            s.Wx.Has = false;
            s.Wx.Error = "直连请求被中止";

            Look look = Look.ByKey("glass");
            Art art = Art.ByKey("aurora");
            Built built = Layout.Build("weather", "expanded", W, H, look, art, s,
                                       new CardActions(), false);

            int bad = 0;
            bad += Text(built, "--°", "空态温度占位");
            bad += Text(built, "还没有天气数据", "空态标题");
            bad += Text(built, "选择城市", "空态按钮");
            bad += Text(built, "重新获取", "空态按钮2");

            Border host = Host(built.Root, W, H);
            RenderTargetBitmap bmp = Shoot(host, W, H, file);
            Ascii(bmp, file);
            bad += Bounds(built.Root, W, H, file);

            built.Halt();
            return bad;
        }

        /* ── 第一层：球 + 温度 ──────────────────────────────────────── */

        static int IdleWithWeather(string file, string temp)
        {
            SeedCache(0, true, temp);
            Weather.LoadCache();

            Snap s = new Snap();
            s.Wx = Weather.Current();

            Look look = Look.ByKey("glass");
            Art art = Art.ByKey("aurora");
            Built built = Layout.Build("weather", "idle", IdleW, IdleH, look, art, s,
                                       new CardActions(), false);

            int bad = 0;
            bad += Text(built, s.Wx.TempText, "收起态温度");

            Border host = Host(built.Root, IdleW, IdleH);
            RenderTargetBitmap bmp = Shoot(host, IdleW, IdleH, file);
            Ascii(bmp, file);

            /* 版面要求是「球贴左、温度贴右」，不是整体居中。
               所以这里量的是**墨迹的横向分布**，不是某个元素算出来的坐标：
                 · 球那一团的重心必须落在左三分之一
                 · 温度那一团必须够到右边，右端距边缘不超过内边距
                 · 中间必须有一段真的空的 —— 两团粘在一起就说明宽度不够

               最后那条是关键：只验左右两端都「有墨」，把一个居中的版面
               也能混过去（居中的话两端其实都在中间，但阈值一松就过）。 */
            int stride, pw, ph;
            byte[] px = Pixels(bmp, out stride, out pw, out ph);

            int[] colInk = new int[pw];
            for (int x = 0; x < pw; x++) colInk[x] = 0;
            for (int y = 0; y < ph; y++)
            {
                int off = y * stride;
                for (int x = 0; x < pw; x++)
                {
                    int i = off + x * 4;
                    int lum = (px[i] * 2 + px[i + 1] * 5 + px[i + 2]) / 8;
                    if (lum > 70) colInk[x]++;
                }
            }

            /* 连续有墨的列段 = 一「团」。至少要有两团：球 + 天气。 */
            List<int[]> groups = new List<int[]>();
            int gs = -1;
            for (int x = 0; x <= pw; x++)
            {
                bool on = x < pw && colInk[x] > 0;
                if (on && gs < 0) gs = x;
                else if (!on && gs >= 0)
                {
                    groups.Add(new int[] { gs, x - 1 });
                    gs = -1;
                }
            }

            if (groups.Count < 2)
            {
                bad++;
                Console.WriteLine("  [x] 收起态 " + temp + ": 只量到 " + groups.Count
                                  + " 团墨，球和天气没分开");
            }
            else
            {
                int[] left = groups[0];
                int[] right = groups[groups.Count - 1];
                double leftCx = (left[0] + left[1]) / 2.0 / pw;
                double rightEndPct = right[1] / (double)pw;
                double gapPx = (right[0] - left[1]) / Scale;

                Console.WriteLine("  收起态 " + temp + ": 左团重心="
                                  + Math.Round(leftCx, 2) + "w  右团末端="
                                  + Math.Round(rightEndPct, 2) + "w  中间空档="
                                  + Math.Round(gapPx, 1) + "dip  团数=" + groups.Count);

                if (leftCx > 0.34)
                {
                    bad++;
                    Console.WriteLine("  [x] 收起态 " + temp + ": 球没贴左，重心在 "
                                      + Math.Round(leftCx, 2) + "w（应 <0.34）");
                }
                if (rightEndPct < 0.85)
                {
                    bad++;
                    Console.WriteLine("  [x] 收起态 " + temp + ": 温度没贴右，末端在 "
                                      + Math.Round(rightEndPct, 2) + "w（应 >0.85）");
                }
                if (gapPx < 2)
                {
                    bad++;
                    Console.WriteLine("  [x] 收起态 " + temp + ": 球和天气只隔 "
                                      + Math.Round(gapPx, 1) + "dip，挤在一起了");
                }
            }

            /* 右边距：最右列附近必须是空的，否则温度被切 */
            int edge = 0;
            for (int y = 0; y < ph; y++)
            {
                int i = y * stride + (pw - 2) * 4;
                int lum = (px[i] * 2 + px[i + 1] * 5 + px[i + 2]) / 8;
                if (lum > 70) edge++;
            }
            if (edge > 0)
            {
                bad++;
                Console.WriteLine("  [x] 收起态 " + temp + ": 最右列还有墨，右边距没了");
            }

            bad += Bounds(built.Root, IdleW, IdleH, file);
            built.Halt();
            return bad;
        }

        /* 桌宠停靠时（petDocked=true，本机默认就是开着的）第一层必须**照样有温度**。

           这是一条真实漏过的用例：桌宠占着球位时 Layout 原来整层 return，
           于是「球在左、温度在右」在有桌宠的机器上一片空白 —— 而之前的探针
           只测了 petDocked=false，永远绿。断言只查一件事：右边那半必须有墨。 */
        static int IdlePetDocked(string file)
        {
            SeedCache(0, true, "12.4");
            Weather.LoadCache();

            Snap s = new Snap();
            s.Wx = Weather.Current();

            Look look = Look.ByKey("glass");
            Art art = Art.ByKey("aurora");
            /* 注意两个 bool 都要显式传：9 参数的重载把 petDocked 写死成 false，
               少传一个就会以为在测桌宠，其实测的是普通路径。 */
            Built built = Layout.Build("weather", "idle", IdleW, IdleH, look, art, s,
                                       new CardActions(), true, true);

            int bad = 0;
            bad += Text(built, s.Wx.TempText, "桌宠停靠时温度仍在");

            Border host = Host(built.Root, IdleW, IdleH);
            RenderTargetBitmap bmp = Shoot(host, IdleW, IdleH, file);

            int stride, pw, ph;
            byte[] px = Pixels(bmp, out stride, out pw, out ph);

            /* 只看右半：桌宠会盖住左半，但温度必须在右半留下墨 */
            int rightInk = 0;
            int half = pw / 2;
            for (int y = 0; y < ph; y++)
            {
                int off = y * stride;
                for (int x = half; x < pw; x++)
                {
                    int i = off + x * 4;
                    int lum = (px[i] * 2 + px[i + 1] * 5 + px[i + 2]) / 8;
                    if (lum > 90) rightInk++;
                }
            }
            Console.WriteLine("  桌宠停靠: 右半墨量=" + rightInk);
            if (rightInk < 40)
            {
                bad++;
                Console.WriteLine("  [x] 桌宠停靠时右半只有 " + rightInk
                                  + " 个亮点，温度没画出来（Layout 大概又整层 return 了）");
            }

            built.Halt();
            return bad;
        }

        /* 没有天气时第一层必须和改之前**一模一样**：只有那颗球，一个字都没有。 */
        static int IdleNoWeather(string file)
        {
            Snap s = new Snap();
            s.Wx.Has = false;

            Look look = Look.ByKey("glass");
            Art art = Art.ByKey("aurora");
            Built built = Layout.Build("weather", "idle", Scenes.IdleW, Scenes.IdleH,
                                       look, art, s, new CardActions(), false);

            int bad = 0;
            List<string> texts = new List<string>();
            Collect(built.Root, texts);
            if (texts.Count != 0)
            {
                bad++;
                Console.WriteLine("  [x] 没有天气时第一层不该有任何字，实到 " + texts.Count
                                  + " 条：'" + texts[0] + "'");
            }

            Render(built.Root, Scenes.IdleW, Scenes.IdleH, file);
            built.Halt();
            return bad;
        }

        /* ── 11 种图标排一张对照表 ──────────────────────────────────── */

        static int IconSheet(string file)
        {
            string[] names = new string[] {
                "Sun", "Moon", "Partly", "PartlyNight", "Cloud", "Fog",
                "Drizzle", "Rain", "Shower", "Snow", "Thunder"
            };

            double cell = 64;
            double w = cell * names.Length;
            double h = cell + 22;

            Border host = new Border();
            host.Width = w;
            host.Height = h;
            host.Background = new SolidColorBrush(Color.FromRgb(24, 25, 28));

            Canvas root = new Canvas();
            root.Width = w;
            root.Height = h;
            host.Child = root;

            for (int i = 0; i < names.Length; i++)
            {
                WKind k = (WKind)Enum.Parse(typeof(WKind), names[i]);
                FrameworkElement ic = Layout.WeatherIcon(k, 48);
                Canvas.SetLeft(ic, i * cell + 8);
                Canvas.SetTop(ic, 6);
                root.Children.Add(ic);

                TextBlock lb = Ui.Text(names[i], 9, FontWeights.Normal,
                                       Color.FromRgb(0xC8, 0xCE, 0xDA), false);
                lb.Width = cell;
                lb.TextAlignment = TextAlignment.Center;
                Canvas.SetLeft(lb, i * cell);
                Canvas.SetTop(lb, cell + 2);
                root.Children.Add(lb);
            }

            RenderHost(host, w, h, file);

            /* 11 个格子里每个都得有东西。少画一个，那张对照表上就是一块空白，
               而「空白」在深色底上看起来和「这个图标本来就这样」没有区别。 */
            RenderTargetBitmap bmp = Raster(host, w, h);
            int stride, pw, ph;
            byte[] px = Pixels(bmp, out stride, out pw, out ph);

            int bad = 0;
            for (int i = 0; i < names.Length; i++)
            {
                int ink = BandInk(px, stride, pw, ph, 4, 56, i * cell + 8, i * cell + 56);
                if (ink < 250)
                {
                    bad++;
                    Console.WriteLine("  [x] icons: " + names[i] + " 那个格子画出来是空的，墨=" + ink);
                }
                else
                {
                    Console.Write(" icons:" + names[i] + "=" + ink);
                }
            }
            Console.WriteLine();

            Ascii(bmp, file);
            return bad;
        }

        /* ── 光栅化 ─────────────────────────────────────────────────── */

        static void Render(FrameworkElement content, double w, double h, string name)
        {
            RenderCanvas(content, w, h, name);
        }

        /// <summary>把一个已经成型的 Layout 根塞进深色底上画出来。</summary>
        static void RenderCanvas(FrameworkElement root, double w, double h, string name)
        {
            Border host = new Border();
            host.Width = w;
            host.Height = h;
            host.Background = new SolidColorBrush(Color.FromRgb(24, 25, 28));
            host.Child = root;
            RenderHost(host, w, h, name);
        }

        static void RenderHost(FrameworkElement host, double w, double h, string name)
        {
            Shoot(host, w, h, name);
        }

        /// <summary>排版、光栅化、存 PNG，把那张位图返回给像素分析用。</summary>
        static RenderTargetBitmap Shoot(FrameworkElement host, double w, double h, string name)
        {
            host.Measure(new Size(w, h));
            host.Arrange(new Rect(0, 0, w, h));
            host.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.Loaded,
                new Action(delegate { host.UpdateLayout(); }));

            int pw = (int)Math.Round(w * Scale), ph = (int)Math.Round(h * Scale);
            RenderTargetBitmap bmp = new RenderTargetBitmap(pw, ph, 96 * Scale, 96 * Scale,
                                                            PixelFormats.Pbgra32);
            bmp.Render(host);

            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            string path = Path.Combine(_out, name);
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                enc.Save(fs);
            Console.WriteLine("  -> " + path + "  (" + pw + "x" + ph + ")");
            return bmp;
        }

        static Border Host(FrameworkElement content, double w, double h)
        {
            Border host = new Border();
            host.Width = w;
            host.Height = h;
            host.Background = new SolidColorBrush(Color.FromRgb(24, 25, 28));
            host.Child = content;
            return host;
        }

        /* 图标对照表自己就是一个 Border，走这条 */
        static void Render(Border host, double w, double h, string name)
        {
            RenderHost(host, w, h, name);
        }

        /* ══ 把画出来的像素读回来 ═══════════════════════════════════════════

           版面这种东西，断言写得再细也只证明「算式自洽」。真正要看的是
           光栅化之后每个像素落在哪儿。这里干三件事：

             1) 色相分区计数：图标那块到底画出了橙色（太阳）、蓝色（雨）、
                还是亮白（云）—— 证明图标真的画了，而且画的是对的那种。
             2) 横带有没有东西：顶部 / 指标 / 预报 / 日出 四条带，每条都得有墨。
                哪条静默地变成空白，这里立刻暴露。
             3) ASCII 灰度图：把图缩成字符打出来。没有肉眼看图的条件下，
                这是唯一能判断「有没有东西压在别的东西上」的办法。 */

        /// <summary>把位图重新画一遍并返回它，供像素分析用。</summary>
        static RenderTargetBitmap Raster(FrameworkElement host, double w, double h)
        {
            host.Measure(new Size(w, h));
            host.Arrange(new Rect(0, 0, w, h));
            host.UpdateLayout();
            int pw = (int)Math.Round(w * Scale), ph = (int)Math.Round(h * Scale);
            RenderTargetBitmap bmp = new RenderTargetBitmap(pw, ph, 96 * Scale, 96 * Scale,
                                                            PixelFormats.Pbgra32);
            bmp.Render(host);
            return bmp;
        }

        static byte[] Pixels(RenderTargetBitmap bmp, out int stride, out int w, out int h)
        {
            w = bmp.PixelWidth;
            h = bmp.PixelHeight;
            stride = w * 4;
            byte[] px = new byte[stride * h];
            bmp.CopyPixels(px, stride, 0);
            return px;
        }

        /* 颜色分类。阈值刻意放得很宽 —— 这套界面有四种外观，
           以后换 look 不该让这些断言全部作废。 */
        const int HueOrange = 0, HueBlue = 1, HueBright = 2;

        static bool IsOrange(int r, int g, int b)
        {
            return r > 140 && r > b + 45 && g > 70;
        }
        static bool IsBlue(int r, int g, int b)
        {
            return b > 120 && b > r + 25;
        }
        static bool IsBright(int r, int g, int b)
        {
            int mn = Math.Min(r, Math.Min(g, b));
            return mn > 175;
        }

        /// <summary>某块矩形里三种色各多少像素。</summary>
        static int[] Count(byte[] px, int stride, int w, int h,
                           double x0, double y0, double x1, double y1)
        {
            int[] n = new int[3];
            int ix0 = (int)(x0 * Scale), ix1 = (int)(x1 * Scale);
            int iy0 = (int)(y0 * Scale), iy1 = (int)(y1 * Scale);
            for (int y = Math.Max(0, iy0); y < Math.Min(h, iy1); y++)
            {
                int off = y * stride;
                for (int x = Math.Max(0, ix0); x < Math.Min(w, ix1); x++)
                {
                    int i = off + x * 4;
                    int b = px[i], g = px[i + 1], r = px[i + 2];
                    if (IsOrange(r, g, b)) n[HueOrange]++;
                    if (IsBlue(r, g, b)) n[HueBlue]++;
                    if (IsBright(r, g, b)) n[HueBright]++;
                }
            }
            return n;
        }

        /// <summary>一条横带里有多少「看得见的墨」（亮或饱和的像素）。</summary>
        static int BandInk(byte[] px, int stride, int w, int h,
                           double y0, double y1, double x0, double x1)
        {
            int hit = 0;
            int iy0 = (int)(y0 * Scale), iy1 = (int)(y1 * Scale);
            int ix0 = (int)(x0 * Scale), ix1 = (int)(x1 * Scale);
            for (int y = Math.Max(0, iy0); y < Math.Min(h, iy1); y++)
            {
                int off = y * stride;
                for (int x = Math.Max(0, ix0); x < Math.Min(w, ix1); x++)
                {
                    int i = off + x * 4;
                    int lum = (px[i] * 2 + px[i + 1] * 5 + px[i + 2]) / 8;
                    if (lum > 70) hit++;
                }
            }
            return hit;
        }

        /// <summary>整张图的粗灰度图，直接打进控制台。没有肉眼看图的条件时这是唯一的路。</summary>
        static void Ascii(RenderTargetBitmap bmp, string name)
        {
            int stride, w, h;
            byte[] px = Pixels(bmp, out stride, out w, out h);

            int cols = Math.Min(96, w / 6);
            int rows = Math.Max(1, h / ((w / 4) * 2 / 3 / 1 + 1));
            rows = Math.Min(44, Math.Max(6, (int)Math.Round(cols * (double)h / w / 2.1)));

            Console.WriteLine("  .-- 预览 " + name + " (每字符约 "
                              + Math.Round((double)w / cols / Scale, 1) + " DIP)");

            char[] ramp = new char[] { ' ', '.', ':', '-', '=', '+', '*', '#', '%', '@' };

            for (int ry = 0; ry < rows; ry++)
            {
                StringBuilder sb = new StringBuilder(cols);
                for (int cx = 0; cx < cols; cx++)
                {
                    int x0 = cx * w / cols, x1 = (cx + 1) * w / cols;
                    int y0 = ry * h / rows, y1 = (ry + 1) * h / rows;
                    int sum = 0, cnt = 0;
                    for (int y = y0; y < y1; y += 2)
                    {
                        int off = y * stride;
                        for (int x = x0; x < x1; x += 2)
                        {
                            int i = off + x * 4;
                            sum += (px[i] * 2 + px[i + 1] * 5 + px[i + 2]) / 8;
                            cnt++;
                        }
                    }
                    int lum = cnt == 0 ? 0 : sum / cnt;
                    int k = lum * (ramp.Length - 1) / 255;
                    if (k < 0) k = 0;
                    if (k >= ramp.Length) k = ramp.Length - 1;
                    sb.Append(ramp[k]);
                }
                Console.WriteLine("  |" + sb.ToString() + "|");
            }
            Console.WriteLine("  '--");
        }

        /* ── 版面断言 ───────────────────────────────────────────────── */

        /* 图标那块到底画出了什么颜色。

           这是整套图标的要害：11 种里只有 4 种是「暖」的（太阳、白天多云、
           阵雨、雷），其余都是冷色或亮白。太阳和月亮长得像，
           所以判据不能是「有没有橙色」——月亮 #F6ECC8 的蓝通道有 200，
           过不了 b<130 这一关。真正把两者分开的是「饱和的橙」：
           太阳核心 #FFA82C 的蓝只有 44。 */
        static int Hue(string tag, RenderTargetBitmap bmp, int code, bool day)
        {
            int stride, w, h;
            byte[] px = Pixels(bmp, out stride, out w, out h);

            /* 图标画在 (18,10) 起、72×72 的区域里，留点余量 */
            int[] n = Count(px, stride, w, h, 16, 8, 92, 84);
            int sun = SatOrange(px, stride, w, h, 16, 8, 92, 84);
            int water = DeepBlue(px, stride, w, h, 16, 8, 92, 84);

            int bad = 0;
            WKind kind = Weather.Kind(code, day);

            bool wantSun = kind == WKind.Sun || kind == WKind.Partly
                           || kind == WKind.Shower || kind == WKind.Thunder;
            bool wantWater = kind == WKind.Rain || kind == WKind.Drizzle;
            bool wantBright = kind == WKind.Cloud || kind == WKind.Fog
                              || kind == WKind.Snow || kind == WKind.Moon
                              || kind == WKind.PartlyNight;

            Console.WriteLine("  hue " + tag + " kind=" + kind
                              + " sun=" + sun + " water=" + water + " bright=" + n[HueBright]);

            if (wantSun && sun < 120)
            {
                bad++;
                Console.WriteLine("  [x] " + tag + ": 该有太阳/闪电的橙色，只量到 " + sun);
            }
            if (wantWater && water < 120)
            {
                bad++;
                Console.WriteLine("  [x] " + tag + ": 该有雨的蓝色，只量到 " + water);
            }
            if (wantBright && n[HueBright] < 120)
            {
                bad++;
                Console.WriteLine("  [x] " + tag + ": 该有云/月/雪的亮色，只量到 "
                                  + n[HueBright]);
            }
            /* 夜景不许有饱和橙：这一条和上面白天的太阳橙色是配对的，
               两边一起过才证明「白天画太阳、晚上画月亮」真的生效了。 */
            if (!day && sun > 20)
            {
                bad++;
                Console.WriteLine("  [x] " + tag + ": 夜里不该出现太阳橙，量到 " + sun);
            }
            return bad;
        }

        static int SatOrange(byte[] px, int stride, int w, int h,
                             double x0, double y0, double x1, double y1)
        {
            int hit = 0;
            int ix0 = (int)(x0 * Scale), ix1 = (int)(x1 * Scale);
            int iy0 = (int)(y0 * Scale), iy1 = (int)(y1 * Scale);
            for (int y = Math.Max(0, iy0); y < Math.Min(h, iy1); y++)
            {
                int off = y * stride;
                for (int x = Math.Max(0, ix0); x < Math.Min(w, ix1); x++)
                {
                    int i = off + x * 4;
                    int b = px[i], g = px[i + 1], r = px[i + 2];
                    if (r > 200 && b < 130 && g > 110) hit++;
                }
            }
            return hit;
        }

        static int DeepBlue(byte[] px, int stride, int w, int h,
                            double x0, double y0, double x1, double y1)
        {
            int hit = 0;
            int ix0 = (int)(x0 * Scale), ix1 = (int)(x1 * Scale);
            int iy0 = (int)(y0 * Scale), iy1 = (int)(y1 * Scale);
            for (int y = Math.Max(0, iy0); y < Math.Min(h, iy1); y++)
            {
                int off = y * stride;
                for (int x = Math.Max(0, ix0); x < Math.Min(w, ix1); x++)
                {
                    int i = off + x * 4;
                    int b = px[i], g = px[i + 1], r = px[i + 2];
                    if (b > 170 && b > r + 60 && b > g + 25) hit++;
                }
            }
            return hit;
        }

        /* 四条横带，每条都得有墨。

           哪一条静默地变成空白，光看断言是发现不了的 ——
           「元素存在」和「元素画出了东西」是两件事：
           透明度设错、裁切盒子算错、字色和底色撞了，
           元素全都还在，画面却是空的。 */
        static int Bands(string tag, RenderTargetBitmap bmp)
        {
            int stride, w, h;
            byte[] px = Pixels(bmp, out stride, out w, out h);

            string[] names = new string[] { "顶部", "指标", "预报", "日出" };
            double[,] band = new double[,] {
                { 4, 84 }, { 94, 136 }, { 144, 212 }, { 216, 240 }
            };

            int bad = 0;
            for (int i = 0; i < names.Length; i++)
            {
                int ink = BandInk(px, stride, w, h, band[i, 0], band[i, 1], 18, W - 18);
                if (ink < 400)
                {
                    bad++;
                    Console.WriteLine("  [x] " + tag + ": 「" + names[i] + "」那条带几乎是空的，墨="
                                      + ink);
                }
            }
            return bad;
        }

        /* 「今天/明天/后天」这三行的浓度。

           它们是整块预报的标题，读者先看见它们、再读温度。用 Ink3（0.48 透明）
           时，10.5px 的中文在这个浓度下几乎糊进玻璃底里。这条断言就是那次
           「太浅了」的修复的验收：量那一带的峰值亮度，而不是相信源码里改对了。

           glass 外观下实测：Ink3 → 162，Ink2 → 208。门槛取 165，
           Ink2 稳过、退回 Ink3 必挂 —— 红绿都跑过。 */
        static int DayLabelInk(string tag, RenderTargetBitmap bmp)
        {
            int stride, w, h;
            byte[] px = Pixels(bmp, out stride, out w, out h);

            int peak = 0;
            int iy0 = (int)(146 * Scale), iy1 = (int)(162 * Scale);
            int ix0 = (int)(18 * Scale), ix1 = (int)((W - 18) * Scale);
            for (int y = Math.Max(0, iy0); y < Math.Min(h, iy1); y++)
            {
                int off = y * stride;
                for (int x = Math.Max(0, ix0); x < Math.Min(w, ix1); x++)
                {
                    int i = off + x * 4;
                    int lum = (px[i] * 2 + px[i + 1] * 5 + px[i + 2]) / 8;
                    if (lum > peak) peak = lum;
                }
            }

            Console.WriteLine("  daylabel " + tag + " peak=" + peak
                              + (peak >= 165 ? " (ok, Ink2 level)" : " (too faint)"));

            if (peak < 165)
            {
                Console.WriteLine("  [x] " + tag + ": 预报日期太浅，峰值亮度只有 " + peak
                                  + "（Ink2 实测 208，Ink3 实测 162）");
                return 1;
            }
            return 0;
        }

        /// <summary>所有元素必须落在卡片矩形里。越界 = 一定是把坐标算错了。</summary>
        static int Bounds(FrameworkElement root, double w, double h, string name)
        {
            /* root 的父是 RenderCanvas 建的 Border，坐标系一致 */
            int bad = 0;
            List<FrameworkElement> all = new List<FrameworkElement>();
            CollectEls(root, all);

            for (int i = 0; i < all.Count; i++)
            {
                FrameworkElement el = all[i];
                if (el.ActualWidth <= 0 || el.ActualHeight <= 0) continue;

                try
                {
                    Point p = el.TransformToAncestor(root).Transform(new Point(0, 0));
                    double r = p.X + el.ActualWidth;
                    double b = p.Y + el.ActualHeight;
                    /* 留 1.5 的余量：抗锯齿和 1px 分隔线会在边上探一点头 */
                    if (p.X < -1.5 || p.Y < -1.5 || r > w + 1.5 || b > h + 1.5)
                    {
                        bad++;
                        TextBlock tb = el as TextBlock;
                        Console.WriteLine("  [x] " + name + ": 越界 "
                            + el.GetType().Name
                            + (tb != null ? "('" + tb.Text + "')" : "")
                            + " at " + Math.Round(p.X) + "," + Math.Round(p.Y)
                            + " size " + Math.Round(el.ActualWidth) + "x" + Math.Round(el.ActualHeight)
                            + "  卡片 " + w + "x" + h);
                    }
                }
                catch { }
            }
            return bad;
        }

        /// <summary>
        /// 大温度和右上角那块（城市/地区/时间）不能压在一起。
        /// 这是这张卡最可能出的版面事故：温度是 40px 的大字，右块是定宽右对齐。
        /// </summary>
        static int NoOverlap(FrameworkElement root, double w, double h, string name)
        {
            int bad = 0;
            List<FrameworkElement> all = new List<FrameworkElement>();
            CollectEls(root, all);

            Rect temp = Rect.Empty, block = Rect.Empty;
            for (int i = 0; i < all.Count; i++)
            {
                TextBlock tb = all[i] as TextBlock;
                if (tb == null || tb.Text == null) continue;
                try
                {
                    Point p = tb.TransformToAncestor(root).Transform(new Point(0, 0));
                    Rect rc = new Rect(p.X, p.Y, tb.ActualWidth, tb.ActualHeight);
                    if (tb.FontSize > 30) temp = rc;
                    else if (tb.TextAlignment == TextAlignment.Right) block = rc;
                }
                catch { }
            }

            if (temp.IsEmpty || block.IsEmpty) return 0;   /* 空态没有大温度 */
            if (temp.Right > block.Left + 1.0)
            {
                bad++;
                Console.WriteLine("  [x] " + name + ": 大温度右缘 " + Math.Round(temp.Right)
                                  + " 压到了右侧文字块左缘 " + Math.Round(block.Left));
            }
            return bad;
        }

        static void CollectEls(DependencyObject d, List<FrameworkElement> into)
        {
            if (d == null) return;
            FrameworkElement fe = d as FrameworkElement;
            if (fe != null) into.Add(fe);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) CollectEls(VisualTreeHelper.GetChild(d, i), into);
        }

        /* ── 小工具 ─────────────────────────────────────────────────── */

        static int Eq(string what, string got, string want)
        {
            if (got == want) return 0;
            Console.WriteLine("  [x] " + what + ": got '" + got + "' want '" + want + "'");
            return 1;
        }

        static int Has(List<string> list, string needle, string what)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == needle) return 0;
            Console.WriteLine("  [x] " + what + ": 没找到，实到 [" + string.Join(", ", list.ToArray()) + "]");
            return 1;
        }

        static int NotHas(List<string> list, string needle, string what)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == needle)
                {
                    Console.WriteLine("  [x] " + what + ": 不该有却出现了，实到 ["
                                      + string.Join(", ", list.ToArray()) + "]");
                    return 1;
                }
            return 0;
        }

        static int Text(Built built, string needle, string what)
        {
            if (HasText(built.Root, needle)) return 0;
            Console.WriteLine("  [x] " + what + ": 卡片上没有 '" + needle + "'");
            return 1;
        }

        static int TextContains(Built built, string needle, string what)
        {
            List<string> all = new List<string>();
            Collect(built.Root, all);
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].IndexOf(needle, StringComparison.Ordinal) >= 0) return 0;
            Console.WriteLine("  [x] " + what + ": 没有一条文字含 '" + needle + "'");
            return 1;
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

        /* ── 造一份缓存 ─────────────────────────────────────────────── */

        /// <summary>
        /// 写 weather.txt。格式和 Weather.SaveCache 一致 —— 这里刻意自己拼一遍
        /// 而不是调 SaveCache（它是私有）。两边对不上时 CacheRoundTrip 会立刻炸，
        /// 而那份断言正是这条路的验收。
        /// </summary>
        static void SeedCache(int code, bool day)
        {
            SeedCache(code, day, "12.4");
        }

        static void SeedCache(int code, bool day, string temp)
        {
            List<string> L = new List<string>();
            L.Add("at=" + Now());
            L.Add("place=" + AppDir.Encode("杭州"));
            L.Add("region=" + AppDir.Encode("浙江省 · 中国"));
            L.Add("temp=" + temp);
            L.Add("feels=9.8");
            L.Add("humidity=58");
            L.Add("wind=11.2");
            L.Add("code=" + code);
            L.Add("isday=" + (day ? "1" : "0"));
            L.Add("high=15.2");
            L.Add("low=6.1");
            L.Add("rise=06:31");
            L.Add("set=17:04");
            L.Add("auto=1");

            string[] labels = new string[] { "今天", "明天", "后天", "周四", "周五", "周六", "周日" };
            string[] dates = new string[] {
                "2026-11-22", "2026-11-23", "2026-11-24", "2026-11-25",
                "2026-11-26", "2026-11-27", "2026-11-28"
            };
            for (int i = 0; i < labels.Length; i++)
            {
                L.Add("day=" + AppDir.Encode(labels[i]));
                L.Add("d=" + dates[i]);
                L.Add("hi=" + (15 + i).ToString(CultureInfo.InvariantCulture));
                L.Add("lo=" + (6 + i).ToString(CultureInfo.InvariantCulture));
                L.Add("c=" + code);
                L.Add("r=" + (i * 7));
            }

            AppDir.WriteLines("weather.txt", L);
        }

        static long Now()
        {
            return (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
        }
    }
}