using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace WinIsland
{
    /// <summary>绘制原语。全部用绝对定位，因为尺寸都是已知常量。</summary>
    static class Ui
    {
        public const string FontUi = "Segoe UI Variable Display, Segoe UI Variable Text, Segoe UI";
        public const string FontIcon = "Segoe Fluent Icons, Segoe MDL2 Assets";

        public const string GPlay = "\uE768";
        public const string GPause = "\uE769";
        public const string GPrev = "\uE892";
        public const string GNext = "\uE893";
        public const string GVolume = "\uE767";
        public const string GCalendar = "\uE787";
        public const string GDoc = "\uE8A5";
        public const string GClock = "\uE823";
        public const string GPin = "\uE707";
        public const string GBell = "\uEA8F";
        public const string GVideo = "\uE714";
        public const string GClose = "\uE711";
        public const string GRefresh = "\uE72C";
        public const string GFolder = "\uE8B7";
        public const string GCopy = "\uE8C8";

        public static Color Mix(Color a, Color b, double t)
        {
            if (t < 0) t = 0; if (t > 1) t = 1;
            return Color.FromArgb(
                (byte)(a.A + (b.A - a.A) * t),
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        /// <summary>当文字/图形用的可读强调色：浅底外观要把亮色压深。</summary>
        public static Color AccInk(Look look, Art art)
        {
            if (look.Key == "frost") return Mix(art.Acc, Look.C("#0E1015"), 0.58);
            return art.Acc;
        }

        public static SolidColorBrush Br(Color c)
        {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /* 带 alpha 的同色画刷。Look.A 吃的是 hex 字符串，Layout 里手上只有
           Color，所以在这里补一个按 Color 来的版本。 */
        public static SolidColorBrush AB(Color c, double a)
        {
            if (a < 0) a = 0;
            if (a > 1) a = 1;
            SolidColorBrush b = new SolidColorBrush(
                Color.FromArgb((byte)Math.Round(255 * a), c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        public static TextBlock Text(string s, double size, FontWeight weight, Color c, bool tabular)
        {
            TextBlock t = new TextBlock();
            t.Text = s;
            t.FontFamily = new FontFamily(FontUi);
            t.FontSize = size;
            t.FontWeight = weight;
            t.Foreground = Br(c);
            t.TextWrapping = TextWrapping.NoWrap;
            t.SnapsToDevicePixels = false;
            if (tabular) Typography.SetNumeralAlignment(t, FontNumeralAlignment.Tabular);
            return t;
        }

        public static TextBlock Glyph(string g, double size, Color c)
        {
            TextBlock t = new TextBlock();
            t.Text = g;
            t.FontFamily = new FontFamily(FontIcon);
            t.FontSize = size;
            t.Foreground = Br(c);
            t.TextWrapping = TextWrapping.NoWrap;
            return t;
        }

        public static void At(Canvas host, UIElement el, double x, double y)
        {
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
            host.Children.Add(el);
        }

        /// <summary>把元素裁成连续圆角。</summary>
        public static void ClipSquircle(FrameworkElement el, double w, double h, double r)
        {
            el.Clip = Squircle.Build(w, h, r);
        }

        /// <summary>生成式封面：底色渐变 + 两团色斑 + 斜向反光，裁成连续圆角。</summary>
        public static FrameworkElement Cover(double size, double r, Art art)
        {
            Grid g = new Grid();
            g.Width = size;
            g.Height = size;

            Rectangle baseRect = new Rectangle();
            LinearGradientBrush lg = new LinearGradientBrush();
            lg.StartPoint = new Point(0, 0);
            lg.EndPoint = new Point(1, 1);
            lg.GradientStops.Add(new GradientStop(art.C1, 0.0));
            lg.GradientStops.Add(new GradientStop(art.C2, 0.62));
            lg.GradientStops.Add(new GradientStop(art.C2, 1.0));
            baseRect.Fill = lg;
            g.Children.Add(baseRect);

            Ellipse e1 = new Ellipse();
            e1.Width = size * 1.1;
            e1.Height = size * 1.1;
            RadialGradientBrush rg1 = new RadialGradientBrush();
            rg1.GradientStops.Add(new GradientStop(Look.A("#FFFFFF", 0.55), 0.0));
            rg1.GradientStops.Add(new GradientStop(Look.A("#FFFFFF", 0.0), 0.62));
            e1.Fill = rg1;
            e1.HorizontalAlignment = HorizontalAlignment.Left;
            e1.VerticalAlignment = VerticalAlignment.Top;
            e1.Margin = new Thickness(-size * 0.3, -size * 0.42, 0, 0);
            g.Children.Add(e1);

            Ellipse e2 = new Ellipse();
            e2.Width = size * 1.2;
            e2.Height = size * 1.2;
            RadialGradientBrush rg2 = new RadialGradientBrush();
            rg2.GradientStops.Add(new GradientStop(Look.A(Hex(art.C3), 0.75), 0.0));
            rg2.GradientStops.Add(new GradientStop(Look.A(Hex(art.C3), 0.0), 0.70));
            e2.Fill = rg2;
            e2.HorizontalAlignment = HorizontalAlignment.Right;
            e2.VerticalAlignment = VerticalAlignment.Bottom;
            e2.Margin = new Thickness(0, 0, -size * 0.38, -size * 0.34);
            g.Children.Add(e2);

            Rectangle sheen = new Rectangle();
            LinearGradientBrush sg = new LinearGradientBrush();
            sg.StartPoint = new Point(0, 0);
            sg.EndPoint = new Point(0.7, 1);
            sg.GradientStops.Add(new GradientStop(Look.A("#FFFFFF", 0.30), 0.0));
            sg.GradientStops.Add(new GradientStop(Look.A("#FFFFFF", 0.0), 0.46));
            sg.GradientStops.Add(new GradientStop(Look.A("#000000", 0.16), 1.0));
            sheen.Fill = sg;
            g.Children.Add(sheen);

            ClipSquircle(g, size, size, r);
            return g;
        }

        static string Hex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        /// <summary>磁贴：实心可读强调色底 + 白色图标。</summary>
        public static FrameworkElement Tile(string glyph, double size, double r, Look look, Art art)
        {
            Border b = new Border();
            b.Width = size;
            b.Height = size;
            b.Background = Br(art.AccOn);
            b.CornerRadius = new CornerRadius(r);
            TextBlock t = Glyph(glyph, size * 0.46, Colors.White);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            return b;
        }

        /// <summary>图标按钮。</summary>
        public static FrameworkElement IconButton(string glyph, double size, Look look, Color glyphColor)
        {
            Border b = new Border();
            b.Width = size;
            b.Height = size;
            b.Background = Br(Look.A(Hex(look.BtnBg), (double)look.BtnBg.A / 255.0));
            b.CornerRadius = new CornerRadius(size * 0.34);
            TextBlock t = Glyph(glyph, size * 0.50, glyphColor);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            return b;
        }

        /// <summary>实心主按钮（圆形）。</summary>
        public static FrameworkElement PrimaryRound(string glyph, double size, Art art)
        {
            Border b = new Border();
            b.Width = size;
            b.Height = size;
            b.Background = Br(art.AccOn);
            b.CornerRadius = new CornerRadius(size / 2);
            TextBlock t = Glyph(glyph, size * 0.46, Colors.White);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            return b;
        }

        /// <summary>文字按钮。</summary>
        public static FrameworkElement TextButton(string label, double h, double padX, bool primary, Look look, Art art)
        {
            Border b = new Border();
            b.Height = h;
            b.Padding = new Thickness(padX, 0, padX, 0);
            b.CornerRadius = new CornerRadius(h * 0.30);
            b.Background = primary ? Br(art.AccOn) : Br(Look.A(Hex(look.BtnBg), (double)look.BtnBg.A / 255.0));
            TextBlock t = Text(label, 13, FontWeights.SemiBold,
                primary ? Colors.White : look.Ink1, false);
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            b.HorizontalAlignment = HorizontalAlignment.Left;
            return b;
        }

        /* 实心条的材质。**待办纸片用的就是这一块**，剪贴板的历史条也从这里取。

           为什么必须共用一处：这一版要的是「剪贴条和待办条看起来是同一种东西」，
           而那件事只能靠同一段代码保证。三处各抄一遍颜色，改一处漏两处的时候
           没人看得出来，直到两页并排比较才发现材质不一样了。

           为什么是实心而不是半透明薄板：叠在一起的一摞条，靠 1px 描边分辨边界；
           半透明的底会让下面的条透上来，一摞就糊成一片。实心的靠颜色本身分辨，
           所以圆角也要小 —— 圆角一大，一摞条会变成一串珠子。 */
        public static Border SolidBar(double w, double h, Look look)
        {
            Border b = new Border();
            b.Width = w;
            b.Height = h;
            b.CornerRadius = new CornerRadius(Math.Min(6, h * 0.24));
            b.Background = new SolidColorBrush(look.CardBg);
            b.BorderThickness = new Thickness(1);
            b.BorderBrush = new SolidColorBrush(look.CardEdge);
            return b;
        }

        /// <summary>进度条：轨道 + 强调色填充 + 前端亮点。</summary>
        public static FrameworkElement Bar(double pct, double width, double h, Look look, Art art)
        {
            Canvas c = new Canvas();
            c.Width = width;
            c.Height = h;

            Rectangle track = new Rectangle();
            track.Width = width;
            track.Height = h;
            track.RadiusX = h / 2;
            track.RadiusY = h / 2;
            track.Fill = Br(look.Track);
            c.Children.Add(track);

            double fw = Math.Max(h, width * pct);
            Rectangle fill = new Rectangle();
            fill.Width = fw;
            fill.Height = h;
            fill.RadiusX = h / 2;
            fill.RadiusY = h / 2;
            LinearGradientBrush fg = new LinearGradientBrush();
            fg.StartPoint = new Point(0, 0);
            fg.EndPoint = new Point(1, 0);
            fg.GradientStops.Add(new GradientStop(Mix(AccInk(look, art), Colors.White, 0.26), 0));
            fg.GradientStops.Add(new GradientStop(AccInk(look, art), 1));
            fill.Fill = fg;
            c.Children.Add(fill);

            if (h >= 5)
            {
                Ellipse head = new Ellipse();
                head.Width = h * 1.9;
                head.Height = h * 1.9;
                head.Fill = Br(Colors.White);
                Canvas.SetLeft(head, fw - h * 0.95);
                Canvas.SetTop(head, -h * 0.45);
                c.Children.Add(head);
            }
            return c;
        }

        /* 原地换血：host 这个 Canvas 留在原来的布局位置，里面的内容换成 fresh。
           fresh 本身是 Canvas 就把它摊平（进度条/环形/律动条都靠这个保住坐标）；
           不是 Canvas（比如封面那个 Border）就整块塞进去当唯一子元素。 */
        public static void Refresh(FrameworkElement host, FrameworkElement fresh)
        {
            Canvas hc = host as Canvas;
            if (hc == null || fresh == null) return;

            hc.Children.Clear();

            Canvas fc = fresh as Canvas;
            if (fc == null)
            {
                Canvas.SetLeft(fresh, 0);
                Canvas.SetTop(fresh, 0);
                hc.Children.Add(fresh);
                return;
            }

            while (fc.Children.Count > 0)
            {
                UIElement child = fc.Children[0];
                fc.Children.RemoveAt(0);
                hc.Children.Add(child);
            }
        }

        /* 第一层那颗球：径向渐变做球体，一片高光绕着球心转，常转不停。
           比「呼吸点」有生气，又完全不占地方 —— 静止时它就是屏幕上最小的那点动静。 */
        public static FrameworkElement Orb(Art art, Look look, double size)
        {
            Grid g = new Grid();
            g.Width = size;
            g.Height = size;

            Ellipse ball = new Ellipse();
            ball.Width = size;
            ball.Height = size;
            RadialGradientBrush rg = new RadialGradientBrush();
            rg.Center = new Point(0.36, 0.32);
            rg.GradientOrigin = new Point(0.34, 0.29);
            rg.RadiusX = 0.86;
            rg.RadiusY = 0.86;
            rg.GradientStops.Add(new GradientStop(Mix(art.AccOn, Colors.White, 0.62), 0));
            rg.GradientStops.Add(new GradientStop(art.AccOn, 0.5));
            rg.GradientStops.Add(new GradientStop(Mix(art.AccOn, Colors.Black, 0.5), 1));
            ball.Fill = rg;
            g.Children.Add(ball);

            Canvas spin = new Canvas();
            spin.Width = size;
            spin.Height = size;
            spin.RenderTransformOrigin = new Point(0.5, 0.5);
            RotateTransform rt = new RotateTransform(0);
            spin.RenderTransform = rt;

            Ellipse shine = new Ellipse();
            shine.Width = size * 0.4;
            shine.Height = size * 0.2;
            shine.Fill = Br(Mix(Colors.White, art.AccOn, 0.3));
            shine.Opacity = 0.8;
            Canvas.SetLeft(shine, size * 0.31);
            Canvas.SetTop(shine, size * 0.11);
            spin.Children.Add(shine);
            g.Children.Add(spin);

            /* 绕一圈 2.6s，线性 —— 有速度但不催人 */
            DoubleAnimation turn = new DoubleAnimation(0, 360,
                new Duration(TimeSpan.FromSeconds(2.6)));
            turn.RepeatBehavior = RepeatBehavior.Forever;
            rt.BeginAnimation(RotateTransform.AngleProperty, turn);

            return g;
        }

        /* 第二层的律动条。播放时自动来回涨落，暂停时定格。
           注意：这不是电平表 —— 本机拿不到 IAudioMeterInformation（见 style-spec §10.10），
           所以它只表达「在播、在动」，不表达「多大声」。 */
        public static FrameworkElement Rhythm(Art art, Look look, bool playing, double w, double h)
        {
            Canvas c = new Canvas();
            c.Width = w;
            c.Height = h;

            int n = 5;
            double bw = 3.2;
            double gap = n > 1 ? (w - n * bw) / (n - 1) : 0;
            double[] peak = new double[] { 0.46, 0.86, 1.0, 0.7, 0.52 };
            double[] dur = new double[] { 0.62, 0.47, 0.74, 0.55, 0.66 };
            double[] delay = new double[] { 0.0, 0.15, 0.07, 0.24, 0.1 };

            for (int i = 0; i < n; i++)
            {
                Border bar = new Border();
                bar.Width = bw;
                bar.Height = h;
                bar.CornerRadius = new CornerRadius(bw / 2);
                bar.Background = Br(Mix(AccInk(look, art), Colors.White, 0.12));
                bar.RenderTransformOrigin = new Point(0.5, 0.5);
                ScaleTransform st = new ScaleTransform(1, playing ? 0.3 : 0.16);
                bar.RenderTransform = st;
                Canvas.SetLeft(bar, i * (bw + gap));
                Canvas.SetTop(bar, 0);
                c.Children.Add(bar);

                if (playing)
                {
                    DoubleAnimation a = new DoubleAnimation(0.18, peak[i],
                        new Duration(TimeSpan.FromSeconds(dur[i])));
                    a.AutoReverse = true;
                    a.RepeatBehavior = RepeatBehavior.Forever;
                    a.BeginTime = TimeSpan.FromSeconds(delay[i]);
                    a.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
                }
            }
            return c;
        }

        /* 真正的专辑封面：SMTC 给的是图片字节，解成 BitmapImage 当背景刷。
           解不出来（没有封面/格式怪）就退回渐变块，不能因为封面失败就空一块。 */
        public static FrameworkElement CoverFrom(byte[] data, double size, double r, Art art)
        {
            if (data == null || data.Length == 0) return Cover(size, r, art);
            try
            {
                System.Windows.Media.Imaging.BitmapImage bi =
                    new System.Windows.Media.Imaging.BitmapImage();
                bi.BeginInit();
                /* OnLoad：不缓存流引用，MemoryStream 出了作用域也能用 */
                bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bi.StreamSource = new System.IO.MemoryStream(data);
                bi.EndInit();
                bi.Freeze();

                Border b = new Border();
                b.Width = size;
                b.Height = size;
                b.CornerRadius = new CornerRadius(r);

                ImageBrush ib = new ImageBrush(bi);
                ib.Stretch = Stretch.UniformToFill;
                b.Background = ib;
                return b;
            }
            catch
            {
                return Cover(size, r, art);
            }
        }

        /// <summary>环形进度。</summary>
        public static FrameworkElement Ring(double size, double thick, double pct, Look look, Art art)
        {
            Canvas c = new Canvas();
            c.Width = size;
            c.Height = size;
            double r = (size - thick) / 2;
            Point center = new Point(size / 2, size / 2);

            Ellipse track = new Ellipse();
            track.Width = size - thick;
            track.Height = size - thick;
            track.Stroke = Br(look.Track);
            track.StrokeThickness = thick;
            Canvas.SetLeft(track, thick / 2);
            Canvas.SetTop(track, thick / 2);
            c.Children.Add(track);

            double sweep = pct * 360.0;
            if (sweep > 359.5) sweep = 359.5;
            double a0 = -90.0;
            Point p0 = OnCircle(center, r, a0);
            Point p1 = OnCircle(center, r, a0 + sweep);

            PathFigure fig = new PathFigure();
            fig.StartPoint = p0;
            ArcSegment seg = new ArcSegment();
            seg.Point = p1;
            seg.Size = new Size(r, r);
            seg.IsLargeArc = sweep > 180.0;
            seg.SweepDirection = SweepDirection.Clockwise;
            fig.Segments.Add(seg);

            PathGeometry pg = new PathGeometry();
            pg.Figures.Add(fig);

            Path arc = new Path();
            arc.Data = pg;
            arc.Stroke = Br(AccInk(look, art));
            arc.StrokeThickness = thick;
            arc.StrokeStartLineCap = PenLineCap.Round;
            arc.StrokeEndLineCap = PenLineCap.Round;
            c.Children.Add(arc);

            return c;
        }

        static Point OnCircle(Point c, double r, double deg)
        {
            double t = deg * Math.PI / 180.0;
            return new Point(c.X + r * Math.Cos(t), c.Y + r * Math.Sin(t));
        }

        /// <summary>波形：五根竖条，播放时各自呼吸。</summary>
        public static FrameworkElement Wave(Art art, Look look, bool animate)
        {
            double[] hs = new double[] { 0.38, 0.74, 1.0, 0.62, 0.30 };
            double h = 16;
            Canvas c = new Canvas();
            c.Width = 5 * 2 + 4 * 2;
            c.Height = h;
            for (int i = 0; i < 5; i++)
            {
                Rectangle bar = new Rectangle();
                bar.Width = 2;
                bar.Height = h;
                bar.RadiusX = 1;
                bar.RadiusY = 1;
                bar.Fill = Br(AccInk(look, art));
                bar.RenderTransformOrigin = new Point(0.5, 0.5);
                ScaleTransform st = new ScaleTransform(1, hs[i]);
                bar.RenderTransform = st;
                Canvas.SetLeft(bar, i * 4);
                Canvas.SetTop(bar, 0);
                c.Children.Add(bar);

                if (animate)
                {
                    DoubleAnimation grow = new DoubleAnimation();
                    grow.From = hs[i] * 0.45;
                    grow.To = hs[i];
                    grow.Duration = new Duration(TimeSpan.FromMilliseconds(480 + i * 90));
                    grow.AutoReverse = true;
                    grow.RepeatBehavior = RepeatBehavior.Forever;
                    grow.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
                    grow.BeginTime = TimeSpan.FromMilliseconds(i * 120);
                    st.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
                }
            }
            return c;
        }

        /// <summary>分段音量条。</summary>
        public static FrameworkElement Segments(int total, int on, double width, double h, Look look, Art art)
        {
            Canvas c = new Canvas();
            c.Width = width;
            c.Height = h;
            double gap = 3;
            double step = (width - gap * (total - 1)) / total;
            if (step < 1.5) { gap = 2; step = (width - gap * (total - 1)) / total; }
            for (int i = 0; i < total; i++)
            {
                Rectangle r = new Rectangle();
                r.Width = step;
                r.Height = h;
                r.RadiusX = 1.5;
                r.RadiusY = 1.5;
                r.Fill = Br(i < on ? AccInk(look, art) : look.Track);
                Canvas.SetLeft(r, i * (step + gap));
                Canvas.SetTop(r, 0);
                c.Children.Add(r);
            }
            return c;
        }

        /// <summary>呼吸点（空闲态）。</summary>
        public static FrameworkElement Breathe(Art art, Look look)
        {
            Canvas c = new Canvas();
            c.Width = 40;
            c.Height = 18;

            Ellipse halo = new Ellipse();
            halo.Width = 26;
            halo.Height = 26;
            RadialGradientBrush hb = new RadialGradientBrush();
            hb.GradientStops.Add(new GradientStop(Look.A(Hex(AccInk(look, art)), 0.34), 0));
            hb.GradientStops.Add(new GradientStop(Look.A(Hex(AccInk(look, art)), 0.0), 0.72));
            halo.Fill = hb;
            Canvas.SetLeft(halo, -1);
            Canvas.SetTop(halo, -4);
            c.Children.Add(halo);

            Ellipse dot = new Ellipse();
            dot.Width = 7;
            dot.Height = 7;
            dot.Fill = Br(AccInk(look, art));
            Canvas.SetLeft(dot, 8.5);
            Canvas.SetTop(dot, 5.5);
            c.Children.Add(dot);

            DoubleAnimation pulse = new DoubleAnimation();
            pulse.From = 0.55;
            pulse.To = 1.0;
            pulse.Duration = new Duration(TimeSpan.FromMilliseconds(1700));
            pulse.AutoReverse = true;
            pulse.RepeatBehavior = RepeatBehavior.Forever;
            pulse.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
            halo.BeginAnimation(UIElement.OpacityProperty, pulse);

            return c;
        }
    }
}
