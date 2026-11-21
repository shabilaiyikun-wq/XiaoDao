using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WRect = System.Windows.Shapes.Rectangle;

namespace WinIsland
{
    /* 截图：Alt+A 唤起 → 整屏定格 → 拖选区域 → 复制到剪贴板 + 存一张 PNG。

       为什么先定格整屏再让用户框选：
       反过来做（选完再抓屏）有两个问题——拖选期间屏幕上发生的任何变化都会漏进去，
       而且遮罩窗口本身会被拍进画面。先抓一帧冻结下来，遮罩只画在这一帧上面，
       用户看到的就是最终结果，不存在「选完发现内容对不上」。

       为什么用 RegisterHotKey 而不是键盘钩子：
       岛是 WS_EX_NOACTIVATE 的，永远拿不到键盘焦点，收不到 KeyDown；
       系统级 RegisterHotKey 不需要焦点，也不用低级钩子（那会拖慢全机输入）。

       为什么不用手动把岛藏起来再抓：
       OnSourceInit 里已经给窗口设了 WDA 排除（WDA_EXCLUDEFROMCAPTURE），
       抓屏本来就拍不到岛——日志里「岛中心亮度 239.4 掉到 21.2」就是证据。 */
    static class Shot
    {
        const int WM_HOTKEY = 0x0312;
        const int HotkeyId = 0xA1;
        const uint MOD_ALT = 0x0001;
        const uint MOD_NOREPEAT = 0x4000;
        const uint VK_A = 0x41;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("gdi32.dll")]
        internal static extern bool DeleteObject(IntPtr hObject);

        /* ---- 低级键盘钩子：RegisterHotKey 抢不到 Alt+A 时的备用通道 ---- */
        const int WH_KEYBOARD_LL = 13;
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const int VK_MENU = 0x12;

        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vk);

        static IntPtr _hwnd = IntPtr.Zero;
        static Window _island;
        static HwndSourceHook _hook;
        static HookProc _llProc;
        static IntPtr _llHook = IntPtr.Zero;
        static int _llLastMs;
        static bool _hooked;
        static double _scale = 1.0;
        static bool _busy;
        static bool _registered;
        static Action<string> _log;

        /* 正在框选。岛的 16ms 采样看这个标志就先别动形态，
           否则遮罩上拖鼠标会被当成一次悬停/移出，白白重建几次卡片。 */
        public static bool Busy { get { return _busy; } }

        /* 热键被别的程序占用时注册会失败，菜单里仍然能截图，所以这只是状态。 */
        public static bool Registered { get { return _registered; } }

        public static void Attach(Window island, IntPtr hwnd, double scale, Action<string> log)
        {
            _island = island;
            _hwnd = hwnd;
            _log = log;
            _scale = scale > 0 ? scale : 1.0;

            if (_hwnd == IntPtr.Zero) { Log("hotkey skipped: hwnd null"); return; }

            if (_hook == null)
            {
                _hook = new HwndSourceHook(WndProc);
                HwndSource src = HwndSource.FromHwnd(_hwnd);
                if (src != null) src.AddHook(_hook);
                else Log("hotkey skipped: no HwndSource");
            }

            _registered = RegisterHotKey(_hwnd, HotkeyId, MOD_ALT | MOD_NOREPEAT, VK_A);
            int err = Marshal.GetLastWin32Error();
            Log("hotkey alt+a registered=" + _registered + " err=" + err);

            /* 1409 = ERROR_HOTKEY_ALREADY_REGISTERED。Alt+A 是被抢得最凶的快捷键之一
               （QQ / 微信 / Snipaste / 各种输入法都注册它）。抢不到就退到低级钩子，
               否则按 Alt+A 什么都不会发生 —— 而用户要的就是这个键。 */
            if (!_registered) InstallLLHook();
        }

        /* 备用通道：全局低级键盘钩子。

           代价要说清楚：钩子会拦截全系统的按键流，每次按键都要等这个回调返回。
           所以回调里只做「读 vkCode + 比一次键状态」，真正的截图动作 BeginInvoke 出去，
           绝不能在钩子里做重活。只有 RegisterHotKey 失败时才会走到这里。 */
        static void InstallLLHook()
        {
            if (_hooked) return;
            try
            {
                _llProc = new HookProc(LLProc);      /* 必须留住引用，否则会被 GC 回收 */
                _llHook = SetWindowsHookEx(WH_KEYBOARD_LL, _llProc,
                                           GetModuleHandle(null), 0);
                _hooked = _llHook != IntPtr.Zero;
                Log("hotkey fallback hook installed=" + _hooked
                    + " err=" + Marshal.GetLastWin32Error());
            }
            catch (Exception ex)
            {
                Log("hotkey fallback hook failed: " + ex.Message);
            }
        }

        static IntPtr LLProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    KBDLLHOOKSTRUCT k = (KBDLLHOOKSTRUCT)Marshal.PtrToStructure(
                        lParam, typeof(KBDLLHOOKSTRUCT));
                    if (k.vkCode == VK_A && (GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
                    {
                        int now = Environment.TickCount;
                        if (_llLastMs == 0 || now - _llLastMs > 400)
                        {
                            _llLastMs = now;
                            Log("hook alt+a fired");
                            if (Application.Current != null)
                                Application.Current.Dispatcher.BeginInvoke(
                                    new Action(Begin));
                        }
                    }
                }
            }
            /* 永远放行：不吞键。真吞了会把占用 Alt+A 的那个程序的功能一起废掉。 */
            return CallNextHookEx(_llHook, nCode, wParam, lParam);
        }

        public static void Detach()
        {
            if (_hwnd != IntPtr.Zero && _registered) UnregisterHotKey(_hwnd, HotkeyId);
            _registered = false;
            if (_llHook != IntPtr.Zero) UnhookWindowsHookEx(_llHook);
            _llHook = IntPtr.Zero;
            _hooked = false;
        }

        /* 给菜单和日志用：现在 Alt+A 到底靠哪条通道生效 */
        public static string HotkeyState
        {
            get
            {
                if (_registered) return "Alt+A (RegisterHotKey)";
                if (_hooked) return "Alt+A (low-level hook)";
                return "Alt+A (未生效)";
            }
        }

        static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == WM_HOTKEY && w.ToInt32() == HotkeyId)
            {
                handled = true;
                Log("hotkey alt+a pressed");
                Begin();
            }
            return IntPtr.Zero;
        }

        public static void Begin()
        {
            if (_busy) { Log("shot ignored: already capturing"); return; }

            /* 先抓帧，再藏岛 —— 顺序不能反。

               岛默认是允许被截图拍到的（WDA_NONE），这时候它真的会出现在
               抓到的像素里。如果先藏后抓，用户自己按 Alt+A 永远拍不到岛：
               刚在菜单里打开「允许截图与录屏捕捉」，自己的截图工具却拍不到，
               说不过去。

               藏岛仍然必须做：岛也是 Topmost，不藏会浮在遮罩上面挡住选区和鼠标。
               所以顺序是「抓（岛在画面里）→ 藏（给遮罩让路）→ 交互 → 还原」。 */
            System.Drawing.Bitmap bmp = Grab();
            Visibility was = Visibility.Visible;
            if (_island != null)
            {
                was = _island.Visibility;
                _island.Visibility = Visibility.Hidden;
            }

            if (bmp == null)
            {
                if (_island != null) _island.Visibility = was;
                Log("shot failed: nothing grabbed");
                return;
            }

            _busy = true;
            try
            {
                ShotOverlay ov = new ShotOverlay(bmp, _scale, delegate(System.Drawing.Bitmap shot)
                {
                    _busy = false;
                    if (_island != null) _island.Visibility = was;
                    if (shot == null) { Log("shot cancelled"); return; }
                    Deliver(shot);
                });
                ov.Show();
                ov.Activate();
            }
            catch (Exception ex)
            {
                _busy = false;
                if (_island != null) _island.Visibility = was;
                Log("shot overlay failed: " + ex.Message);
            }
        }

        /* SystemParameters 给的是逻辑像素，CopyFromScreen 要物理像素，乘一次缩放。 */
        static System.Drawing.Bitmap Grab()
        {
            double l = SystemParameters.VirtualScreenLeft;
            double t = SystemParameters.VirtualScreenTop;
            double w = SystemParameters.VirtualScreenWidth;
            double h = SystemParameters.VirtualScreenHeight;
            if (w < 2 || h < 2) return null;

            int x = (int)Math.Round(l * _scale);
            int y = (int)Math.Round(t * _scale);
            int pw = (int)Math.Round(w * _scale);
            int ph = (int)Math.Round(h * _scale);
            if (pw < 2 || ph < 2) return null;

            System.Drawing.Bitmap bmp = new System.Drawing.Bitmap(
                pw, ph, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(pw, ph),
                                     System.Drawing.CopyPixelOperation.SourceCopy);
                }
            }
            catch (Exception ex)
            {
                bmp.Dispose();
                Log("shot grab failed: " + ex.Message);
                return null;
            }
            Log("shot grabbed " + pw + "x" + ph + " at " + x + "," + y + " scale=" + _scale);
            return bmp;
        }

        static void Deliver(System.Drawing.Bitmap shot)
        {
            /* 剪贴板：WPF 的 Clipboard 要求 STA，这里就在 UI 线程，可以直用。
               剪贴板被别的进程占着很常见，失败也不算失败——PNG 已经落盘了。 */
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                hbmp = shot.GetHbitmap();
                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(
                    hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                /* 必须先 Freeze 再释放 HBITMAP。
                   CreateBitmapSourceFromHBitmap 返回的源是延迟读取的（拿着句柄，
                   真正取像素时才去读），先 DeleteObject 的话剪贴板里就是一张空图/黑图。 */
                src.Freeze();
                Clipboard.SetImage(src);
                Log("shot copied " + shot.Width + "x" + shot.Height);
            }
            catch (Exception ex)
            {
                Log("shot clipboard failed: " + ex.Message);
            }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            }

            try
            {
                string dir = Path.Combine(AppDir.Root, "shots");
                Directory.CreateDirectory(dir);
                string full = Path.Combine(dir,
                    "shot-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
                shot.Save(full, System.Drawing.Imaging.ImageFormat.Png);
                Log("shot saved " + full);
            }
            catch (Exception ex)
            {
                Log("shot save failed: " + ex.Message);
            }

            shot.Dispose();
        }

        internal static void Log(string s)
        {
            if (_log != null) _log(s);
        }
    }
/* 定格画面 + 可调整的框选。

       交互分两段：
         第一段  拖出一个初始选区，松开鼠标后框**不消失**
         第二段  框还在：内部拖动 = 整体移动，八个把手 = 改大小，
                 工具条 ✓ = 完成，✗ / Esc / 右键 = 取消，双击框内 = 完成，
                 点框外空白 = 重新拖一个

       为什么松手不直接出图：上一版松手即完成，框歪了只能重来。截图这件事
       「差几个像素」是常态，微调比重新拖一次快得多。

       遮罩用四块矩形而不是「大矩形挖洞」：Path 在 Stretch=None 且不给尺寸时会
       按几何包围盒自我定尺寸，坐标整体偏掉；Stretch=Fill 又会把几何拉伸变形。 */
    class ShotOverlay : Window
    {
        const double Tol = 7;        /* 把手命中容差（DIP） */
        const double MinSize = 4;    /* 小于这个尺寸的框没有意义 */
        const double HandleSize = 7;

        enum Mode { Idle, Dragging, Adjust, Moving, Resizing }

        readonly System.Drawing.Bitmap _bmp;
        readonly double _scale;
        readonly Action<System.Drawing.Bitmap> _done;

        Canvas _layer;
        WRect _maskTop, _maskBottom, _maskLeft, _maskRight;
        WRect _frame;
        WRect[] _handles;
        Border _bar;
        TextBlock _barSize;

        Mode _mode;
        Rect _rect;
        Rect _rect0;
        Point _grab;
        int _handle;
        bool _reported;
        bool _restoreOnFail;
        /* 唤起时默认给的是**整屏选区**，这个标记表示「用户还没动过它」。
           在它成立时按下鼠标：拖动 = 自己框一块，只点一下 = 确认整屏。
           两者靠「松手时有没有拖出有效尺寸」区分，不靠点击次数 ——
           所以不会和「点一下确认」抢同一个动作。 */
        bool _fresh;
        /* 这一轮拖动是从默认整屏那一下开始的（用来判定「单击 = 截全屏」） */
        bool _freshDrag;
        System.Drawing.Bitmap _result;

        public ShotOverlay(System.Drawing.Bitmap bmp, double scale,
                           Action<System.Drawing.Bitmap> done)
        {
            _bmp = bmp;
            _scale = scale > 0 ? scale : 1.0;
            _done = done;

            /* 唤起即整屏选中。以前这里是个空框、状态 Idle，于是必须先拖一下
               才有选区 —— 而「先点一下再拖」又和「点一下确认」撞车。
               现在默认就有选区：点一下就是截全屏，拖动才是自己框。 */
            _rect = new Rect(0, 0,
                             Math.Max(1, SystemParameters.VirtualScreenWidth),
                             Math.Max(1, SystemParameters.VirtualScreenHeight));
            _mode = Mode.Adjust;
            _fresh = true;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Cursor = Cursors.Cross;
            Title = "WinIsland Shot";

            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            Grid root = new Grid();
            Content = root;

            Image img = new Image();
            img.Stretch = Stretch.Fill;
            img.Source = Freeze(bmp);
            root.Children.Add(img);

            _layer = new Canvas();
            root.Children.Add(_layer);

            Brush shade = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
            _maskTop = Mask(shade);
            _maskBottom = Mask(shade);
            _maskLeft = Mask(shade);
            _maskRight = Mask(shade);

            _frame = new WRect();
            _frame.Stroke = new SolidColorBrush(Color.FromRgb(0x5A, 0xB0, 0xFF));
            _frame.StrokeThickness = 1.5;
            _frame.Fill = Brushes.Transparent;
            _frame.Visibility = Visibility.Collapsed;
            _frame.IsHitTestVisible = false;
            _layer.Children.Add(_frame);

            Brush hFill = new SolidColorBrush(Color.FromRgb(0x5A, 0xB0, 0xFF));
            Brush hLine = new SolidColorBrush(Colors.White);
            _handles = new WRect[8];
            for (int i = 0; i < 8; i++)
            {
                WRect h = new WRect();
                h.Width = HandleSize;
                h.Height = HandleSize;
                h.Fill = hFill;
                h.Stroke = hLine;
                h.StrokeThickness = 1;
                h.Visibility = Visibility.Collapsed;
                h.IsHitTestVisible = false;
                _layer.Children.Add(h);
                _handles[i] = h;
            }

            BuildBar();
            root.Children.Add(Hint());

            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            MouseRightButtonDown += delegate { Cancel(); };
            PreviewKeyDown += OnKey;
            /* 再挂一道 KeyDown：遮罩里没有可聚焦的子元素，
               键盘焦点落在窗口本身时不同版本输入栈的路由不完全一致。 */
            KeyDown += OnKey;

            Loaded += delegate
            {
                Shot.Log("shot open default " + Math.Round(_rect.Width) + "x"
                         + Math.Round(_rect.Height) + " fresh=" + _fresh
                         + " mode=" + _mode);
                Update();
            };
        }

        /* 工具条：尺寸文字 + ✓ + ✗。

           自己搭小圆角按钮而不是用 Ui.IconButton —— 那些是跟 Look 走的，
           遮罩永远是深色底，不该受用户当前外观影响。 */
        void BuildBar()
        {
            _barSize = new TextBlock();
            _barSize.FontFamily = new FontFamily(Ui.FontUi);
            _barSize.FontSize = 12;
            _barSize.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2));
            _barSize.VerticalAlignment = VerticalAlignment.Center;
            _barSize.Margin = new Thickness(0, 0, 9, 0);

            StackPanel sp = new StackPanel();
            sp.Orientation = Orientation.Horizontal;
            sp.Children.Add(_barSize);
            sp.Children.Add(BarButton("\uE73E", Colors.White,
                Color.FromRgb(0x2E, 0x8B, 0xFF),
                delegate { Accept(); }, new Thickness(0, 0, 6, 0)));
            sp.Children.Add(BarButton("\uE711", Color.FromRgb(0xE8, 0xEC, 0xF2),
                Color.FromRgb(0x33, 0x38, 0x42),
                delegate { Cancel(); }, new Thickness(0)));

            _bar = new Border();
            _bar.Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x10, 0x12, 0x16));
            _bar.CornerRadius = new CornerRadius(9);
            _bar.Padding = new Thickness(10, 5, 6, 5);
            _bar.Child = sp;
            _bar.Visibility = Visibility.Collapsed;

            /* 整条工具条吃掉点击：不然点在按钮之间的空隙会被窗口的 OnDown
               接走，当成「在框内按下」开始移动选框，手感很怪。 */
            _bar.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
            };
            _layer.Children.Add(_bar);
        }

        Border BarButton(string glyph, Color fg, Color bg, Action act, Thickness margin)
        {
            Border b = new Border();
            b.Width = 26;
            b.Height = 26;
            b.CornerRadius = new CornerRadius(7);
            b.Background = new SolidColorBrush(bg);
            b.Cursor = Cursors.Hand;
            b.Margin = margin;

            TextBlock t = Ui.Glyph(glyph, 12, fg);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;

            b.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                /* 必须吃掉：否则事件继续冒泡到窗口的 OnDown，
                   会被当成「在框外按下」重新起一个选区。 */
                e.Handled = true;
                if (act != null) act();
            };
            return b;
        }

        WRect Mask(Brush shade)
        {
            WRect r = new WRect();
            r.Fill = shade;
            r.IsHitTestVisible = false;
            _layer.Children.Add(r);
            return r;
        }

        /* 冻结的整屏图转成 WPF 源。

           不走 PNG 编码中转：3840×2400 编一次要几百毫秒，遮罩出来会明显一顿。
           HBITMAP 直转是内存拷贝。Freeze() 之后源不再依赖句柄，
           所以可以立刻 DeleteObject，不会泄漏 GDI 对象。 */
        static BitmapSource Freeze(System.Drawing.Bitmap bmp)
        {
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                hbmp = bmp.GetHbitmap();
                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(
                    hbmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally
            {
                if (hbmp != IntPtr.Zero) Shot.DeleteObject(hbmp);
            }
        }

        FrameworkElement Hint()
        {
            Border b = new Border();
            b.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x10, 0x12, 0x16));
            b.CornerRadius = new CornerRadius(9);
            b.Padding = new Thickness(12, 6, 12, 6);
            b.HorizontalAlignment = HorizontalAlignment.Center;
            b.VerticalAlignment = VerticalAlignment.Top;
            b.Margin = new Thickness(0, 18, 0, 0);
            b.IsHitTestVisible = false;

            TextBlock t = new TextBlock();
            t.FontFamily = new FontFamily(Ui.FontUi);
            t.FontSize = 13;
            t.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xEC, 0xF2));
            t.Text = "点一下截全屏  ·  拖动可自己框选  ·  松手后拖边角微调  ·  Enter 完成  ·  Esc 取消";
            b.Child = t;
            return b;
        }

        double ViewW() { return ActualWidth > 1 ? ActualWidth : Width; }
        double ViewH() { return ActualHeight > 1 ? ActualHeight : Height; }

        Point Clamp(Point p)
        {
            double cw = ViewW(), ch = ViewH();
            return new Point(Math.Max(0, Math.Min(cw, p.X)),
                             Math.Max(0, Math.Min(ch, p.Y)));
        }

        /* ------------------------------------------------------------ 鼠标 */

        void OnDown(object sender, MouseButtonEventArgs e)
        {
            Point p = e.GetPosition(this);

            if (e.ClickCount == 2 && _mode != Mode.Idle && _rect.Contains(p))
            {
                Accept();
                return;
            }

            if (_mode == Mode.Idle)
            {
                _restoreOnFail = false;
                BeginDrag(p);
                return;
            }

            /* 默认整屏还没被动过：按下就意味着「我要自己框一块」，
               而不是「搬动这个满屏的框」（满屏框也搬不动）。
               于是直接起新框；如果松手时没拖出尺寸，就按「单击 = 截全屏」处理。 */
            if (_fresh)
            {
                _rect0 = _rect;
                _restoreOnFail = true;
                _freshDrag = true;
                BeginDrag(p);
                return;
            }

            int h = HitHandle(p);
            if (h != 0)
            {
                _handle = h;
                _rect0 = _rect;
                _grab = p;
                _mode = Mode.Resizing;
                Shot.Log("shot resize handle=" + h);
                return;
            }

            if (_rect.Contains(p))
            {
                _rect0 = _rect;
                _grab = p;
                _mode = Mode.Moving;
                return;
            }

            /* 点在框外空白：重新起一个选区。
               先把当前框存下来 —— 如果只是点了一下、没拖出有效尺寸，
               按取消处理太粗暴了，应该回到刚才那个已经调好的框。 */
            _rect0 = _rect;
            _restoreOnFail = true;
            BeginDrag(p);
        }

        void BeginDrag(Point p)
        {
            _mode = Mode.Dragging;
            _grab = p;
            _rect = new Rect(p, p);
            Update();
        }

        void OnMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(this);

            if (_mode == Mode.Dragging)
            {
                _rect = new Rect(_grab, Clamp(p));
                Update();
                return;
            }

            if (_mode == Mode.Moving)
            {
                double cw = ViewW(), ch = ViewH();
                Rect r = new Rect(_rect0.X + (p.X - _grab.X),
                                  _rect0.Y + (p.Y - _grab.Y),
                                  _rect0.Width, _rect0.Height);
                if (r.X < 0) r.X = 0;
                if (r.Y < 0) r.Y = 0;
                if (r.X + r.Width > cw) r.X = cw - r.Width;
                if (r.Y + r.Height > ch) r.Y = ch - r.Height;
                _rect = r;
                Update();
                return;
            }

            if (_mode == Mode.Resizing)
            {
                _rect = ResizeTo(_rect0, _handle, p);
                Update();
                return;
            }

            UpdateCursor(p);
        }

        void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (_mode == Mode.Dragging)
            {
                bool ok = _rect.Width >= MinSize && _rect.Height >= MinSize;

                if (ok)
                {
                    _restoreOnFail = false;
                    _fresh = false;
                    _freshDrag = false;
                    _mode = Mode.Adjust;
                    Update();
                    Shot.Log("shot drag done " + Math.Round(_rect.Width) + "x"
                             + Math.Round(_rect.Height));
                    return;
                }

                /* 从默认整屏那一下开始、又没有拖出尺寸 —— 这就是「点了一下」。
                   把整屏选区还原回来并直接确认：点一下即截全屏。 */
                if (_freshDrag)
                {
                    _freshDrag = false;
                    _rect = _rect0;
                    _mode = Mode.Adjust;
                    Update();
                    Shot.Log("shot click = full screen "
                             + Math.Round(_rect.Width) + "x" + Math.Round(_rect.Height));
                    Accept();
                    return;
                }

                /* 没拖出有效尺寸。如果进来之前本来就有框，说明这只是
                   「点了一下空白」，回到原来那个已经调好的框，不要弄丢。 */
                if (_restoreOnFail && _rect0.Width >= MinSize && _rect0.Height >= MinSize)
                {
                    _restoreOnFail = false;
                    _rect = _rect0;
                    _mode = Mode.Adjust;
                    Update();
                    return;
                }

                /* 本来就没有框：多半是把「点击」当成了「拖动」，退回空白态 */
                _restoreOnFail = false;
                _mode = Mode.Idle;
                _rect = new Rect(0, 0, 0, 0);
                Update();
            }

            if (_mode == Mode.Moving || _mode == Mode.Resizing)
            {
                _mode = Mode.Adjust;
                Update();
            }
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { e.Handled = true; Cancel(); return; }
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (_mode != Mode.Idle) Accept();
            }
        }

        /* ------------------------------------------------------------ 命中 */

        /* 1=NW 2=N 3=NE 4=W 5=E 6=SW 7=S 8=SE，0 = 不在把手上 */
        int HitHandle(Point p)
        {
            Rect r = _rect;
            bool nearL = Math.Abs(p.X - r.X) <= Tol;
            bool nearR = Math.Abs(p.X - (r.X + r.Width)) <= Tol;
            bool nearT = Math.Abs(p.Y - r.Y) <= Tol;
            bool nearB = Math.Abs(p.Y - (r.Y + r.Height)) <= Tol;
            bool inX = p.X >= r.X - Tol && p.X <= r.X + r.Width + Tol;
            bool inY = p.Y >= r.Y - Tol && p.Y <= r.Y + r.Height + Tol;

            if (nearL && nearT) return 1;
            if (nearR && nearT) return 3;
            if (nearL && nearB) return 6;
            if (nearR && nearB) return 8;
            if (nearT && inX) return 2;
            if (nearB && inX) return 7;
            if (nearL && inY) return 4;
            if (nearR && inY) return 5;
            return 0;
        }

        static Cursor HandleCursor(int h)
        {
            if (h == 1 || h == 8) return Cursors.SizeNWSE;
            if (h == 3 || h == 6) return Cursors.SizeNESW;
            if (h == 2 || h == 7) return Cursors.SizeNS;
            return Cursors.SizeWE;
        }

        void UpdateCursor(Point p)
        {
            /* 默认整屏时整屏都在框内 —— 用 SizeAll 会让人以为可以把整屏框搬走 */
            if (_fresh) { Cursor = Cursors.Cross; return; }
            int h = HitHandle(p);
            if (h != 0) { Cursor = HandleCursor(h); return; }
            if (_rect.Contains(p)) { Cursor = Cursors.SizeAll; return; }
            Cursor = Cursors.Cross;
        }

        /* 按把手改边。边界夹在视口内，同时保一个最小尺寸，
           拖过头的时候是「顶住」而不是让框翻面。 */
        Rect ResizeTo(Rect r0, int h, Point p)
        {
            double cw = ViewW(), ch = ViewH();
            double l = r0.X;
            double t = r0.Y;
            double rr = r0.X + r0.Width;
            double bb = r0.Y + r0.Height;
            double dx = p.X - _grab.X;
            double dy = p.Y - _grab.Y;

            if (h == 1 || h == 4 || h == 6) l = r0.X + dx;
            if (h == 3 || h == 5 || h == 8) rr = r0.X + r0.Width + dx;
            if (h == 1 || h == 2 || h == 3) t = r0.Y + dy;
            if (h == 6 || h == 7 || h == 8) bb = r0.Y + r0.Height + dy;

            l = Math.Max(0, Math.Min(cw, l));
            rr = Math.Max(0, Math.Min(cw, rr));
            t = Math.Max(0, Math.Min(ch, t));
            bb = Math.Max(0, Math.Min(ch, bb));

            if (rr - l < MinSize)
            {
                if (h == 1 || h == 4 || h == 6) l = Math.Max(0, rr - MinSize);
                else rr = Math.Min(cw, l + MinSize);
            }
            if (bb - t < MinSize)
            {
                if (h == 1 || h == 2 || h == 3) t = Math.Max(0, bb - MinSize);
                else bb = Math.Min(ch, t + MinSize);
            }

            return new Rect(l, t, rr - l, bb - t);
        }

        /* ------------------------------------------------------------ 绘制 */

        static void Place(WRect r, double x, double y, double w, double h)
        {
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            r.Width = Math.Max(0, w);
            r.Height = Math.Max(0, h);
        }

        void ShowHandles(bool on)
        {
            Visibility v = on ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < _handles.Length; i++)
            {
                if (_handles[i].Visibility != v) _handles[i].Visibility = v;
            }
        }

        void Update()
        {
            double cw = ViewW(), ch = ViewH();
            Rect r = _rect;
            bool has = _mode != Mode.Idle && r.Width >= 1 && r.Height >= 1;

            if (!has)
            {
                /* 还没开始：整屏压暗 */
                Place(_maskTop, 0, 0, cw, ch);
                Place(_maskBottom, 0, ch, cw, 0);
                Place(_maskLeft, 0, ch, 0, 0);
                Place(_maskRight, cw, ch, 0, 0);
                _frame.Visibility = Visibility.Collapsed;
                _bar.Visibility = Visibility.Collapsed;
                ShowHandles(false);
                return;
            }

            /* 四块遮罩围出选框。相邻边取同一个数值，接缝才不会有 1px 亮线。 */
            Place(_maskTop, 0, 0, cw, r.Y);
            Place(_maskBottom, 0, r.Y + r.Height, cw, ch - (r.Y + r.Height));
            Place(_maskLeft, 0, r.Y, r.X, r.Height);
            Place(_maskRight, r.X + r.Width, r.Y, cw - (r.X + r.Width), r.Height);

            _frame.Visibility = Visibility.Visible;
            /* 整屏框的描边是沿线居中的，画在 0/Width 处会被窗口裁掉一半。
               默认整屏时往里收 1px，让那条线整条可见 —— 只影响提示，不影响截取范围。 */
            if (_fresh)
                Place(_frame, r.X + 1, r.Y + 1,
                      Math.Max(0, r.Width - 2), Math.Max(0, r.Height - 2));
            else
                Place(_frame, r.X, r.Y, r.Width, r.Height);

            _barSize.Text = ((int)Math.Round(r.Width * _scale)) + " × "
                            + ((int)Math.Round(r.Height * _scale));
            _bar.Visibility = Visibility.Visible;
            PositionBar(cw, ch, r);

            /* 拖初始框的时候不显示把手，减少干扰。
               默认整屏也不显示：那些把手正好落在屏幕四边，一半在窗口之外，
               既拉不动也不好看；一旦自己框出一块，把手就回来了。 */
            bool adj = !_fresh && (_mode == Mode.Adjust || _mode == Mode.Moving
                                   || _mode == Mode.Resizing);
            ShowHandles(adj);
            if (adj) PositionHandles(r);
        }

        void PositionHandles(Rect r)
        {
            double cx = r.X + r.Width / 2;
            double cy = r.Y + r.Height / 2;
            double half = HandleSize / 2;

            double[] px = new double[] { r.X, cx, r.X + r.Width, r.X,
                                         r.X + r.Width, r.X, cx, r.X + r.Width };
            double[] py = new double[] { r.Y, r.Y, r.Y, cy,
                                         cy, r.Y + r.Height, r.Y + r.Height, r.Y + r.Height };

            for (int i = 0; i < _handles.Length; i++)
            {
                Canvas.SetLeft(_handles[i], px[i] - half);
                Canvas.SetTop(_handles[i], py[i] - half);
            }
        }

        /* 工具条优先贴选框右下；下面放不下放上面，都放不下就压进框内底部。
           宽度靠 Measure 拿到真实值，不猜。 */
        void PositionBar(double cw, double ch, Rect r)
        {
            _bar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double bw = _bar.DesiredSize.Width;
            double bh = _bar.DesiredSize.Height;
            if (bw <= 0) bw = 130;
            if (bh <= 0) bh = 36;

            double bx = r.X + r.Width - bw;
            double by = r.Y + r.Height + 8;

            if (by + bh > ch - 4) by = r.Y - bh - 8;
            if (by < 4) by = r.Y + r.Height - bh - 8;
            if (by < 4) by = 4;

            if (bx < 4) bx = 4;
            if (bx + bw > cw - 4) bx = cw - bw - 4;
            if (bx < 4) bx = 4;

            Canvas.SetLeft(_bar, bx);
            Canvas.SetTop(_bar, by);
        }

        /* ------------------------------------------------------------ 交付 */

        void Accept()
        {
            Rect r = _rect;
            if (_mode == Mode.Idle || r.Width < MinSize || r.Height < MinSize)
            {
                Cancel();
                return;
            }

            int x = (int)Math.Round(r.X * _scale);
            int y = (int)Math.Round(r.Y * _scale);
            int w = (int)Math.Round(r.Width * _scale);
            int h = (int)Math.Round(r.Height * _scale);

            x = Math.Max(0, Math.Min(_bmp.Width - 1, x));
            y = Math.Max(0, Math.Min(_bmp.Height - 1, y));
            w = Math.Min(w, _bmp.Width - x);
            h = Math.Min(h, _bmp.Height - y);

            if (w >= 2 && h >= 2)
            {
                try
                {
                    _result = _bmp.Clone(new System.Drawing.Rectangle(x, y, w, h),
                                         _bmp.PixelFormat);
                    Shot.Log("shot accept " + w + "x" + h + " from " + x + "," + y);
                }
                catch (Exception ex)
                {
                    Shot.Log("shot crop failed: " + ex.Message);
                }
            }
            Close();
        }

        void Cancel()
        {
            _result = null;
            Close();
        }

        /* 结果只在这里交付一次：Accept、Cancel、Esc、被系统关掉，
           都保证 _done 恰好调一次，_busy 不会漏着不放。 */
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            if (_reported) return;
            _reported = true;

            try { if (_bmp != null) _bmp.Dispose(); } catch { }

            if (_done != null) _done(_result);
        }
    }
}
