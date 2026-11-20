using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace WinIsland
{
    /* 「写一条待办」的小输入窗。

       为什么必须是一个独立窗口：岛本身是 WS_EX_NOACTIVATE，永远不会拿到键盘焦点 ——
       这是它「不打扰」的前提（点它不会把你的前台窗口顶掉）。但要在上面直接写字，
       就必须有一个能聚焦的窗口。所以折中：写字时开一个临时小窗，写完立刻关掉，
       焦点马上还给用户原来的窗口。

       它长得和岛一致（同样的超椭圆圆角、同样的玻璃底色），位置贴在岛的下方，
       视觉上像是岛自己长出来的一块，而不是蹦出一个陌生的对话框。 */
    class TodoInput
    {
        public static TodoInput Current;

        Window _win;
        TextBox _box;
        int _dueMinutes;       /* 0 = 不提醒 */
        Action<string, int> _onDone;

        public bool IsOpen { get { return _win != null && _win.IsVisible; } }

        /* dueMinutes: 0 不提醒；否则从现在起多少分钟后到点 */
        public static void Open(Window owner, Rect islandScreen, Look look, Art art,
                                Action<string, int> onDone)
        {
            if (Current != null && Current.IsOpen) { Current.Focus(); return; }

            TodoInput t = new TodoInput();
            t.Build(owner, islandScreen, look, art, onDone);
            Current = t;
        }

        void Build(Window owner, Rect r, Look look, Art art, Action<string, int> onDone)
        {
            _onDone = onDone;

            double w = Math.Max(360, r.Width);
            double h = 108;

            _win = new Window();
            _win.WindowStyle = WindowStyle.None;
            _win.AllowsTransparency = true;
            _win.Background = Brushes.Transparent;
            _win.ShowInTaskbar = false;
            _win.Topmost = true;
            _win.ResizeMode = ResizeMode.NoResize;
            _win.Width = w;
            _win.Height = h;
            _win.WindowStartupLocation = WindowStartupLocation.Manual;
            _win.Left = r.Left;
            _win.Top = r.Top + r.Height + 10;
            _win.ShowActivated = true;

            Canvas root = new Canvas();
            root.Width = w;
            root.Height = h;

            /* 底板：和岛同一套圆角与配色 */
            Border panel = new Border();
            panel.Width = w;
            panel.Height = h;
            panel.CornerRadius = new CornerRadius(18);
            panel.Background = Ui.Br(Color.FromArgb(0xF2, 0x14, 0x16, 0x1A));
            panel.BorderBrush = Ui.Br(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            panel.BorderThickness = new Thickness(1);
            Ui.At(root, panel, 0, 0);

            TextBlock hint = Ui.Text("写一条待办，回车确认", 11.5, FontWeights.Normal,
                Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), false);
            Ui.At(root, hint, 20, 12);

            _box = new TextBox();
            _box.Width = w - 40;
            _box.FontSize = 15;
            _box.FontFamily = new FontFamily(Ui.FontUi);
            _box.Foreground = Brushes.White;
            _box.CaretBrush = Brushes.White;
            _box.Background = Brushes.Transparent;
            _box.BorderThickness = new Thickness(0);
            _box.Padding = new Thickness(0, 4, 0, 4);
            _box.MaxLength = 120;
            _box.AcceptsReturn = false;
            Ui.At(root, _box, 20, 32);

            /* 分隔线 */
            Rectangle line = new Rectangle();
            line.Width = w - 40;
            line.Height = 1;
            line.Fill = Ui.Br(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF));
            Ui.At(root, line, 20, 62);

            /* 提醒预设：不用键盘也能选，都是相对时间，不弹日历 */
            string[] names = new string[] { "不提醒", "15 分钟", "1 小时", "3 小时", "明天 9:00" };
            int[] mins = new int[] { 0, 15, 60, 180, -1 };
            double bx = 20;
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                FrameworkElement chip = Chip(names[i], look, art, i == 0);
                chip.Tag = "act";
                chip.Cursor = Cursors.Hand;
                double cw = names[i].Length * 12 + 18;
                chip.Width = cw;
                chip.Height = 26;
                Ui.At(root, chip, bx, 72);
                bx += cw + 6;

                FrameworkElement target = chip;
                chip.MouseLeftButtonUp += delegate
                {
                    _dueMinutes = mins[idx];
                    for (int k = 0; k < root.Children.Count; k++)
                    {
                        Border b = root.Children[k] as Border;
                        if (b != null && b.Tag is string && (string)b.Tag == "chip") { }
                    }
                    /* 重画 chips 太绕，直接把选中的那个描边点亮 */
                    HighlightChip(target, root);
                };
            }

            _win.Content = root;

            _box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.Key == Key.Enter) { Commit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
            };
            _win.Deactivated += delegate { Close(); };
            _win.Loaded += delegate
            {
                _box.Focus();
                Keyboard.Focus(_box);
            };

            _win.Show();
            _win.Activate();
            _box.Focus();
        }

        void HighlightChip(FrameworkElement chosen, Canvas root)
        {
            for (int i = 0; i < root.Children.Count; i++)
            {
                Border b = root.Children[i] as Border;
                if (b == null) continue;
                if (b == chosen) b.BorderBrush = Ui.Br(Color.FromArgb(0xFF, 0x8A, 0xB4, 0xF8));
                else if (b.Tag as string == "chip")
                    b.BorderBrush = Ui.Br(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
            }
        }

        static FrameworkElement Chip(string label, Look look, Art art, bool first)
        {
            Border b = new Border();
            b.Tag = "chip";
            b.CornerRadius = new CornerRadius(9);
            b.Background = Ui.Br(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
            b.BorderThickness = new Thickness(1);
            b.BorderBrush = Ui.Br(Color.FromArgb(first ? (byte)0xFF : (byte)0x22,
                (byte)0x8A, (byte)0xB4, (byte)0xF8));
            TextBlock t = Ui.Text(label, 11.5, FontWeights.Medium,
                Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF), false);
            t.HorizontalAlignment = HorizontalAlignment.Center;
            t.VerticalAlignment = VerticalAlignment.Center;
            b.Child = t;
            return b;
        }

        void Focus()
        {
            if (_win == null) return;
            _win.Activate();
            if (_box != null) _box.Focus();
        }

        void Commit()
        {
            string text = _box.Text == null ? "" : _box.Text.Trim();
            if (text.Length == 0) { Close(); return; }

            int due = _dueMinutes;
            /* 「明天 9:00」是绝对时间：换算成相对分钟，数据层只存绝对时刻 */
            if (due == -1)
            {
                DateTime t = DateTime.Now.Date.AddDays(1).AddHours(9);
                due = (int)Math.Max(1, Math.Round((t - DateTime.Now).TotalMinutes));
            }
            if (_onDone != null) _onDone(text, due);
            Close();
        }

        void Close()
        {
            if (_win == null) return;
            Window w = _win;
            _win = null;
            try { w.Close(); } catch { }
            if (Current == this) Current = null;
        }
    }
}