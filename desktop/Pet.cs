/* 【旧实现 · 不参与编译】
   桌宠已经整体迁到 ..\pet-demo，由 build.cmd 直接编译那边的 Pet.cs / PetAnim.cs /
   PetShape.cs / PetClips.cs / ReferenceClips.cs / Pet3D.cs / AnimationCore.cs。
   这份文件只是留着查历史差异，不要再加回编译列表 —— 一旦加回，同一个
   WinIsland 命名空间里就会出现两个 PetWindow，和 pet-demo 的版本直接撞车。 */
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

/* System.IO.Path 和 Shapes.Path 撞名。这里 IO 用得少，只能给形状取别名。 */
using ShapePath = System.Windows.Shapes.Path;

namespace WinIsland
{
    /* 桌宠窗口。

       和岛共享同一套范式，理由也一样：
         * 透明 + 无边框 + 不进任务栏 + 不抢激活（WS_EX_NOACTIVATE）；
         * 整窗默认穿透，只有鼠标落在球体上才关掉穿透；
         * 命中不能靠鼠标事件 —— 穿透窗收不到消息，所以轮询 GetCursorPos；
         * 动画挂 CompositionTarget.Rendering，静止就摘掉，不空转。

       和岛不同的地方只有一处：命中的是「球」不是「超椭圆」。
       所以判定用离球心的距离，并且给 6px 容差 —— 视觉不变，但不用像素级瞄准。 */

    class PetWindow : Window
    {
        const double Size = 220;      /* 窗口边长（DIP），球只占中间的一块 */
        const double BodyR = 74;      /* 球半径 */
        const double EyeW = 5.4;      /* 眼睛半宽（睁眼、正面） */
        const double EyeH = 12.5;     /* 眼睛半高 */
        const double HoverSlack = 6;  /* 命中容差 */
        const double DragThreshold = 3.0;

        ShapePath _face;
        ShapePath _eyes;
        TranslateTransform _shift;

        readonly Stopwatch _clock = new Stopwatch();
        DispatcherTimer _hit;
        DispatcherTimer _demo;

        IntPtr _hwnd = IntPtr.Zero;
        double _dpi = 1.0;

        bool _ignoring = true;
        bool _hover;
        bool _down;
        bool _maybeDrag;
        bool _dragging;
        double _downX, _downY;
        double _grabX, _grabY;

        bool _animating;
        double _lastRender = -1;
        long _geomKey = long.MinValue;

        double _gazeX, _gazeY;
        bool _hasGaze;
        double _pokeAt = -1;
        double _spinAt = -1;

        long _frames;
        readonly string _logPath;

        public long Frames { get { return _frames; } }
        public bool Ignoring { get { return _ignoring; } }

        public PetWindow(string logPath, bool demo)
        {
            _logPath = logPath == null ? "" : logPath;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SnapsToDevicePixels = false;
            UseLayoutRounding = false;
            Width = Size;
            Height = Size;
            Title = "桌宠";

            Canvas root = new Canvas();
            root.Width = Size;
            root.Height = Size;
            root.Background = Brushes.Transparent;

            /* 舞台：球和眼睛都以 (0,0) 为球心绘制，整体靠这一个变换挪到窗口中心。
               两个 Path 共用舞台的变换，就不必各自维护一份平移。
               Canvas 默认不裁切，所以路径画出边界也不会被切。 */
            Canvas stage = new Canvas();
            stage.IsHitTestVisible = false;
            _shift = new TranslateTransform(Size / 2.0, Size / 2.0);
            stage.RenderTransform = _shift;

            _face = new ShapePath();
            /* 纯色剪影：官方那套也是单一深色，expression 全交给眼睛 */
            _face.Fill = Ui.Br(Color.FromArgb(0xFF, 0x0A, 0x0A, 0x0C));

            _eyes = new ShapePath();
            /* 眼睛填实色，不再靠挖孔透出背景。
               深色壁纸下挖孔会让眼睛和桌面差十来个灰阶，等于没有。
               超出球体的部分由 _eyes.Clip（即球几何）裁掉，所以边缘照样干净。 */
            _eyes.Fill = Ui.Br(Color.FromArgb(0xFF, 0xF4, 0xF7, 0xFA));

            stage.Children.Add(_face);
            stage.Children.Add(_eyes);
            root.Children.Add(stage);

            Content = root;

            RestorePosition();

            Loaded += OnLoaded;
            SourceInitialized += OnSourceInit;

            if (demo)
            {
                /* 演示模式：自动戳一下、自动转圈，方便肉眼确认动画是活的 */
                _demo = new DispatcherTimer(DispatcherPriority.Background);
                _demo.Interval = TimeSpan.FromSeconds(2.6);
                _demo.Tick += OnDemoTick;
                _demo.Start();
            }
        }

        /* ------------------------------------------------------------ 生命周期 */

        void OnSourceInit(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (_dpi <= 0.1) _dpi = 1.0;

            /* 不抢激活 + 不进任务栏 + 默认穿透，一次写进去 */
            IntPtr style = Native.GetExStyle(_hwnd);
            long v = style.ToInt64();
            v |= Native.WS_EX_NOACTIVATE;
            v |= Native.WS_EX_TOOLWINDOW;
            v |= Native.WS_EX_TRANSPARENT;
            Native.SetExStyle(_hwnd, new IntPtr(v));
            _ignoring = true;

            Trace("hwnd ready dpi=" + _dpi.ToString("0.###"));
        }

        void OnLoaded(object sender, EventArgs e)
        {
            _clock.Start();
            StartHitTimer();
            StartAnimating();
            Trace("loaded left=" + Left.ToString("0.#") + " top=" + Top.ToString("0.#")
                  + " size=" + Size.ToString("0") + " r=" + BodyR.ToString("0")
                  + " work=" + SystemParameters.WorkArea.ToString()
                  + " screen=" + Native.ScreenWidth + "x" + Native.ScreenHeight);
        }

        void OnDemoTick(object sender, EventArgs e)
        {
            double t = _clock.Elapsed.TotalSeconds;
            if (_spinAt < 0) _spinAt = t + 0.4;
            else _pokeAt = t;
            Trace("demo tick t=" + t.ToString("0.0"));
        }

        /* ------------------------------------------------------------ 位置 */

        void RestorePosition()
        {
            Rect wa = SystemParameters.WorkArea;
            Left = Math.Round(wa.Left + (wa.Width - Size) / 2.0);
            Top = Math.Round(wa.Top + wa.Height - Size - 40);

            string[] lines = AppDir.ReadLines("pet.txt");
            if (lines == null || lines.Length < 2) return;

            double x, y;
            if (double.TryParse(lines[0], out x) && double.TryParse(lines[1], out y))
            {
                Left = x;
                Top = y;
                ClampToScreen();
            }
        }

        void SavePosition()
        {
            System.Collections.Generic.List<string> lines = new System.Collections.Generic.List<string>();
            lines.Add(Left.ToString("0.##"));
            lines.Add(Top.ToString("0.##"));
            AppDir.WriteLines("pet.txt", lines);
            Trace("position saved " + Left.ToString("0.#") + "," + Top.ToString("0.#"));
        }

        void ClampToScreen()
        {
            Rect wa = SystemParameters.WorkArea;
            double minX = wa.Left - Size / 2.0;
            double maxX = wa.Right - Size / 2.0;
            double minY = wa.Top - Size / 2.0;
            double maxY = wa.Bottom - Size / 2.0;

            if (Left < minX) Left = minX;
            if (Left > maxX) Left = maxX;
            if (Top < minY) Top = minY;
            if (Top > maxY) Top = maxY;
        }

        /* ------------------------------------------------------------ 动画 */

        void StartAnimating()
        {
            if (_animating) return;
            _animating = true;
            _lastRender = -1;
            CompositionTarget.Rendering += OnFrame;
            Trace("animating");
        }

        void StopAnimating()
        {
            if (!_animating) return;
            _animating = false;
            CompositionTarget.Rendering -= OnFrame;
            Trace("stopped animating");
        }

        void OnFrame(object sender, EventArgs e)
        {
            double t = _clock.Elapsed.TotalSeconds;

            /* 帧率上限：屏幕可能是 120/144Hz，桌宠不需要那么多帧。
               16.6ms 正好是 60fps，掉帧只是少画几帧，动画内容由 Sample 决定。 */
            if (_lastRender >= 0 && t - _lastRender < 0.0166) return;
            _lastRender = t;

            PetInput p = new PetInput();
            p.Time = t;
            p.GazeX = _gazeX;
            p.GazeY = _gazeY;
            p.HasGaze = _hasGaze;
            p.PokeAge = _pokeAt < 0 ? -1 : t - _pokeAt;
            p.SpinAge = _spinAt < 0 ? -1 : t - _spinAt;

            PetFrame f = PetAnim.Sample(p);
            Apply(f);
            _frames++;
        }

        void Apply(PetFrame f)
        {
            /* 几何重建只在参数真的变了的时候做。
               量化步长是「看不出差别的最大步长」，这样呼吸不会导致每帧重建，
               但视线移动、眨眼、变形都能立刻反映出来。 */
            long key = Key(f);

            if (key != _geomKey)
            {
                _geomKey = key;

                Geometry ball = PetShape.Face(BodyR, f.ShapeIndex, f.SquashX, f.SquashY, f.Yaw, f.Pitch);
                _face.Data = ball;

                /* 眼睛用球几何做 Clip：超出的部分被裁掉，眼睛滑到边缘看起来
                   就是「贴着球面转过去」，规则和挖孔完全一致，只是颜色不再受桌面影响。 */
                _eyes.Clip = ball;
                _eyes.Data = PetShape.EyePair(BodyR, f.GazeX, f.GazeY, f.Yaw, f.Pitch, f.Lid, f.Expression, EyeW, EyeH);
            }

            _shift.Y = Size / 2.0 + f.OffsetY;
        }

        static long Q(double v, double step)
        {
            return (long)Math.Round(v / step);
        }

        static long Mix(long a, long b)
        {
            return a * 73856093L ^ b * 19349663L;
        }

        static long Key(PetFrame f)
        {
            long k = Mix(Q(f.GazeX, 0.004), Q(f.GazeY, 0.004));
            k = Mix(k, Q(f.Yaw, 0.02));
            k = Mix(k, Q(f.Pitch, 0.01));
            k = Mix(k, (long)f.Expression);
            k = Mix(k, Q(f.Lid, 0.01));
            k = Mix(k, Q(f.ShapeIndex, 0.004));
            k = Mix(k, Q(f.SquashX, 0.0016));
            k = Mix(k, Q(f.SquashY, 0.0016));
            return k;
        }

        /* ------------------------------------------------------------ 命中与拖动 */

        void StartHitTimer()
        {
            /* 穿透窗收不到鼠标消息，只能主动轮询。
               Input 优先级足够高，10ms 一次（≈100Hz）比人手的点击间隔密得多。 */
            _hit = new DispatcherTimer(DispatcherPriority.Input);
            _hit.Interval = TimeSpan.FromMilliseconds(10);
            _hit.Tick += OnHitTick;
            _hit.Start();
        }

        void OnHitTick(object sender, EventArgs e)
        {
            /* 兜底：物理左键已经松开但我们没收到抬起的边沿（在窗外松手、
               弹了 UAC、切了桌面都会这样）。不检查的话窗口会一直粘着光标。 */
            if (_down && !Native.LeftButtonDown())
            {
                EndPress(true);
            }

            Point screen;
            if (!Native.TryGetCursor(out screen)) return;

            double cursorX = screen.X / _dpi;
            double cursorY = screen.Y / _dpi;

            if (_dragging)
            {
                Left = cursorX - _grabX;
                Top = cursorY - _grabY;
                ClampToScreen();
                return;
            }

            double cx = Left + Size / 2.0;
            double cy = Top + Size / 2.0;
            double dx = cursorX - cx;
            double dy = cursorY - cy;
            double dist = Math.Sqrt(dx * dx + dy * dy);

            /* 迟滞：进去用 R+6，出来要退到 R+10 才算离开。
               没有迟滞的话，鼠标停在球边缘时真实鼠标的微小抖动会让命中
               来回翻，穿透样式跟着每几十毫秒重写一次（真机上见过）。
               岛那边用 slack 解决的是「够得着」，这里解决的是「不抽搐」。 */
            double outSlack = _hover ? HoverSlack + 4.0 : HoverSlack;
            bool inside = dist <= BodyR + outSlack;

            /* 眼神跟随：光标坐标反正已经拿到了，顺手算，不再多调一次系统 API。
               离得越远注意力越弱，太远就交给自动游荡。 */
            if (dist > 4)
            {
                double fall = (dist - BodyR) / 320.0;
                if (fall < 0) fall = 0;
                if (fall > 1) fall = 1;
                double gain = 1.0 - fall * 0.6;
                _gazeX = dx / dist * gain;
                _gazeY = dy / dist * gain;
                _hasGaze = gain > 0.25;
            }

            if (inside != _hover)
            {
                _hover = inside;
                ApplyClickThrough(!inside);
                Trace("hover -> " + inside
                      + " cursor=" + cursorX.ToString("0.#") + "," + cursorY.ToString("0.#")
                      + " local=" + dx.ToString("0.#") + "," + dy.ToString("0.#")
                      + " dist=" + dist.ToString("0.##")
                      + " win=" + Left.ToString("0.#") + "," + Top.ToString("0.#")
                      + " dpi=" + _dpi.ToString("0.###"));
            }

            bool down = Native.LeftButtonDown();

            if (down && !_down)
            {
                _down = true;
                if (inside)
                {
                    _downX = cursorX;
                    _downY = cursorY;
                    _grabX = cursorX - Left;
                    _grabY = cursorY - Top;
                    _maybeDrag = true;
                }
            }
            else if (down && _down && _maybeDrag && !_dragging)
            {
                double mx = cursorX - _downX;
                double my = cursorY - _downY;
                if (mx * mx + my * my > DragThreshold * DragThreshold)
                {
                    _dragging = true;
                    ApplyClickThrough(false);
                    Trace("drag start");
                }
            }
            else if (!down && _down)
            {
                EndPress(inside);
            }
        }

        void EndPress(bool inside)
        {
            _down = false;

            if (_dragging)
            {
                _dragging = false;
                _maybeDrag = false;
                SavePosition();
                ApplyClickThrough(!_hover);
                Trace("drag end");
                return;
            }

            bool wasMaybeDrag = _maybeDrag;
            _maybeDrag = false;

            /* 没拖起来且还落在球上 → 算一次「戳」 */
            if (wasMaybeDrag && inside)
            {
                _pokeAt = _clock.Elapsed.TotalSeconds;
                Trace("poke");
            }
        }

        void ApplyClickThrough(bool ignore)
        {
            if (_hwnd == IntPtr.Zero) return;
            if (ignore == _ignoring) return;
            _ignoring = ignore;

            IntPtr style = Native.GetExStyle(_hwnd);
            long v = style.ToInt64();
            if (ignore) v |= Native.WS_EX_TRANSPARENT;
            else v &= ~(long)Native.WS_EX_TRANSPARENT;
            if (ignore) v |= Native.WS_EX_NOACTIVATE;
            else v |= Native.WS_EX_NOACTIVATE;
            Native.SetExStyle(_hwnd, new IntPtr(v));

            Trace("style write ignore=" + ignore);
        }

        /* ------------------------------------------------------------ 日志 */

        void Trace(string msg)
        {
            if (_logPath == null || _logPath.Length == 0) return;
            try
            {
                File.AppendAllText(_logPath,
                    "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + msg + Environment.NewLine);
            }
            catch { }
        }

        /* ------------------------------------------------------------ 自检 */

        /* 自检要回答两个问题，而且都用像素证据回答：
              1. 球真的被画出来了吗？   → 抓屏统计「近黑」像素比例
              2. 眼睛是「洞」而不是贴上去的白片吗？ → 球内应当出现背景色

           背景用一块纯品红探针窗垫在下面：品红 = 透明区，近黑 = 球体。
           这比「我看了觉得可以」硬得多，也符合岛那边 proveLiveBackdrop 的路子。 */
        public string RunSelfTest()
        {
            string report = "";
            try
            {
                Application.Current.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Loaded);

                double t = _clock.Elapsed.TotalSeconds;

                /* 先验纯函数：同一个输入两次必须给出同样的帧 */
                PetInput probeIn = new PetInput();
                probeIn.Time = 12.345;
                probeIn.HasGaze = true;
                probeIn.GazeX = 0.3;
                probeIn.GazeY = -0.2;
                probeIn.PokeAge = -1;
                probeIn.SpinAge = -1;

                PetFrame a = PetAnim.Sample(probeIn);
                PetFrame b = PetAnim.Sample(probeIn);
                bool deterministic = a.GazeX == b.GazeX && a.Lid == b.Lid
                                     && a.ShapeIndex == b.ShapeIndex && a.SquashX == b.SquashX;

                PetInput pokeProbe = probeIn;
                pokeProbe.PokeAge = 0.10;
                PetFrame pokedFrame = PetAnim.Sample(pokeProbe);
                bool pokeTransition = pokedFrame.State == "poked"
                                   && pokedFrame.Expression == PetExpression.Surprised;

                PetInput spinProbe = probeIn;
                spinProbe.SpinAge = 0.475;
                PetFrame spinningFrame = PetAnim.Sample(spinProbe);
                PetInput recoveredProbe = probeIn;
                recoveredProbe.SpinAge = 0.96;
                PetFrame recoveredFrame = PetAnim.Sample(recoveredProbe);
                bool spinTransition = spinningFrame.State == "spin"
                                   && spinningFrame.Yaw > Math.PI
                                   && spinningFrame.Expression == PetExpression.Happy;
                bool interactionRecovery = recoveredFrame.State == a.State
                                        && recoveredFrame.Yaw == a.Yaw;

                /* Idle animation should visit several reusable action clips. */
                HashSet<string> idleStates = new HashSet<string>();
                HashSet<PetExpression> idleExpressions = new HashSet<PetExpression>();
                for (int i = 0; i < 600; i++)
                {
                    PetInput s = probeIn;
                    s.HasGaze = false;
                    s.Time = i * 0.1;
                    PetFrame idleFrame = PetAnim.Sample(s);
                    idleStates.Add(idleFrame.State);
                    idleExpressions.Add(idleFrame.Expression);
                }
                bool idleClipVariety = idleStates.Count >= 3 && idleExpressions.Count >= 3;

                /* 旋转和俯仰要改变身体投影，表情要改变眼睛几何。 */
                Rect frontBounds = PetShape.Body(BodyR, 0, 1, 1, 0, 0).Bounds;
                Rect sideBounds = PetShape.Body(BodyR, 0, 1, 1, Math.PI / 2.0, 0).Bounds;
                Rect tiltedBounds = PetShape.Body(BodyR, 0, 1, 1, 0, 0.45).Bounds;
                bool turnProjectionChanges = Math.Abs(frontBounds.Width - sideBounds.Width) > 1.0;
                bool pitchProjectionChanges = Math.Abs(frontBounds.Height - tiltedBounds.Height) > 1.0;

                Geometry frontEyes = PetShape.EyePair(BodyR, 0, 0, 0, 0, 0,
                    PetExpression.Neutral, EyeW, EyeH);
                Geometry sideEyes = PetShape.EyePair(BodyR, 0, 0, Math.PI / 2.0, 0, 0,
                    PetExpression.Neutral, EyeW, EyeH);
                Geometry backEyes = PetShape.EyePair(BodyR, 0, 0, Math.PI, 0, 0,
                    PetExpression.Neutral, EyeW, EyeH);
                bool spinHidesFace = frontEyes != null && sideEyes != null && backEyes == null
                                  && ((GeometryGroup)sideEyes).Children.Count < ((GeometryGroup)frontEyes).Children.Count;

                Rect neutralEyes = frontEyes.Bounds;
                Rect surprisedEyes = PetShape.EyePair(BodyR, 0, 0, 0, 0, 0,
                    PetExpression.Surprised, EyeW, EyeH).Bounds;
                bool expressionChangesEyes = Math.Abs(neutralEyes.Width - surprisedEyes.Width) > 1.0
                                          || Math.Abs(neutralEyes.Height - surprisedEyes.Height) > 1.0;

                /* 静止不浮动：连续采样一秒，形状参数应当只受呼吸影响（幅度很小） */
                double minSq = 9, maxSq = -9;
                for (int i = 0; i < 60; i++)
                {
                    PetInput s = probeIn;
                    s.Time = 40.0 + i * 0.016;
                    PetFrame fr = PetAnim.Sample(s);
                    if (fr.SquashX < minSq) minSq = fr.SquashX;
                    if (fr.SquashX > maxSq) maxSq = fr.SquashX;
                }
                double breathRange = maxSq - minSq;

                /* 变形必须藏在眨眼后面：过渡窗口内的帧，lid 必须接近闭合 */
                bool morphBehindBlink = true;
                for (int i = 0; i < 40; i++)
                {
                    PetInput s = probeIn;
                    s.Time = 9.0 + 1.10 + i * 0.005;
                    PetFrame fr = PetAnim.Sample(s);
                    double segLocal = s.Time - Math.Floor(s.Time / 9.0) * 9.0;
                    bool inWindow = segLocal >= 1.05 && segLocal < 1.78;
                    if (inWindow && fr.Lid < 0.9) { morphBehindBlink = false; break; }
                }

                /* Idle morphs should happen briefly and settle back to the reference ball. */
                bool idleShapeReturnsToBall = true;
                bool idleShapeMorphs = false;
                for (int i = 0; i < 1000; i++)
                {
                    PetInput s = probeIn;
                    s.Time = i * 0.1;
                    PetFrame fr = PetAnim.Sample(s);
                    double segLocal = s.Time - Math.Floor(s.Time / 9.0) * 9.0;
                    if (segLocal >= 2.0 && Math.Abs(fr.ShapeIndex) > 0.001)
                    {
                        idleShapeReturnsToBall = false;
                        break;
                    }
                    if (segLocal >= 1.2 && segLocal < 1.3 && Math.Abs(fr.ShapeIndex) > 0.02)
                        idleShapeMorphs = true;
                }

                /* 像素检查固定在中性睁眼帧，避免采样时恰好落在眨眼或困倦片段。 */
                PetFrame visualProbe = new PetFrame();
                visualProbe.ShapeIndex = 0;
                visualProbe.SquashX = 1;
                visualProbe.SquashY = 1;
                visualProbe.Expression = PetExpression.Neutral;
                Apply(visualProbe);

                /* 像素检查：垫一块纯品红在下面 */
                Window probe = new Window();
                probe.WindowStyle = WindowStyle.None;
                probe.ResizeMode = ResizeMode.NoResize;
                probe.ShowInTaskbar = false;
                probe.Topmost = false;
                probe.ShowActivated = false;
                probe.WindowStartupLocation = WindowStartupLocation.Manual;
                probe.Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0xFF));
                probe.Left = Left;
                probe.Top = Top;
                probe.Width = Size;
                probe.Height = Size;
                probe.Show();

                /* 等一次合成，再抓 */
                DispatcherFrame frame = new DispatcherFrame();
                DispatcherTimer wait = new DispatcherTimer(DispatcherPriority.Render);
                wait.Interval = TimeSpan.FromMilliseconds(260);
                wait.Tick += delegate
                {
                    wait.Stop();
                    frame.Continue = false;
                };
                wait.Start();
                Dispatcher.PushFrame(frame);

                int px = (int)Math.Round(Left * _dpi);
                int py = (int)Math.Round(Top * _dpi);
                int pw = (int)Math.Round(Size * _dpi);
                int ph = (int)Math.Round(Size * _dpi);

                BitmapSource shot = Native.Capture(px, py, pw, ph);
                probe.Close();

                int magenta = 0, dark = 0, other = 0, eyePixels = 0;
                double ratio = 0;

                if (shot != null)
                {
                    int stride = pw * 4;
                    byte[] buf = new byte[stride * ph];
                    shot.CopyPixels(buf, stride, 0);

                    double midX = pw / 2.0;
                    double midY = ph / 2.0;
                    double eyeBand = BodyR * _dpi * 0.62;

                    for (int y = 0; y < ph; y++)
                    {
                        for (int x = 0; x < pw; x++)
                        {
                            int i = y * stride + x * 4;
                            byte bp = buf[i];
                            byte gp = buf[i + 1];
                            byte rp = buf[i + 2];

                            bool isMagenta = rp > 200 && gp < 70 && bp > 200;
                            bool isDark = rp < 45 && gp < 45 && bp < 55;
                            bool isEye = rp > 200 && gp > 200 && bp > 200;

                            if (isMagenta) magenta++;
                            else if (isDark) dark++;
                            else other++;

                            /* 球心附近出现近白像素 = 眼睛是实色画上去的（不是挖孔） */
                            if (isEye)
                            {
                                double ex = x - midX;
                                double ey = y - midY;
                                if (ex * ex + ey * ey < eyeBand * eyeBand) eyePixels++;
                            }
                        }
                    }

                    ratio = (double)dark / (pw * ph);
                }

                bool sizeOk = ratio > 0.24 && ratio < 0.44;
                bool eyesOk = eyePixels > 200;
                bool transparentOk = shot != null && magenta > (pw * ph) / 2;

                bool ok = deterministic && pokeTransition && spinTransition && interactionRecovery && idleClipVariety && morphBehindBlink && idleShapeReturnsToBall && idleShapeMorphs && turnProjectionChanges && pitchProjectionChanges && spinHidesFace && expressionChangesEyes && sizeOk && eyesOk && transparentOk;

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"ok\": " + (ok ? "true" : "false") + ",");
                sb.AppendLine("  \"frames\": " + _frames + ",");
                sb.AppendLine("  \"elapsed\": " + t.ToString("0.000") + ",");
                sb.AppendLine("  \"dpi\": " + _dpi.ToString("0.###") + ",");
                sb.AppendLine("  \"deterministic\": " + (deterministic ? "true" : "false") + ",");
                sb.AppendLine("  \"idleClipVariety\": " + (idleClipVariety ? "true" : "false") + ",");
                sb.AppendLine("  \"pokeTransition\": " + (pokeTransition ? "true" : "false") + ",");
                sb.AppendLine("  \"spinTransition\": " + (spinTransition ? "true" : "false") + ",");
                sb.AppendLine("  \"interactionRecovery\": " + (interactionRecovery ? "true" : "false") + ",");
                sb.AppendLine("  \"idleStateCount\": " + idleStates.Count + ",");
                sb.AppendLine("  \"idleExpressionCount\": " + idleExpressions.Count + ",");
                sb.AppendLine("  \"turnProjectionChanges\": " + (turnProjectionChanges ? "true" : "false") + ",");
                sb.AppendLine("  \"spinHidesFace\": " + (spinHidesFace ? "true" : "false") + ",");
                sb.AppendLine("  \"pitchProjectionChanges\": " + (pitchProjectionChanges ? "true" : "false") + ",");
                sb.AppendLine("  \"expressionChangesEyes\": " + (expressionChangesEyes ? "true" : "false") + ",");
                sb.AppendLine("  \"morphBehindBlink\": " + (morphBehindBlink ? "true" : "false") + ",");
                sb.AppendLine("  \"idleShapeReturnsToBall\": " + (idleShapeReturnsToBall ? "true" : "false") + ",");
                sb.AppendLine("  \"idleShapeMorphs\": " + (idleShapeMorphs ? "true" : "false") + ",");
                sb.AppendLine("  \"breathRange\": " + breathRange.ToString("0.0000") + ",");
                sb.AppendLine("  \"bodyRatio\": " + ratio.ToString("0.000") + ",");
                sb.AppendLine("  \"bodyRatioOk\": " + (sizeOk ? "true" : "false") + ",");
                sb.AppendLine("  \"eyePixels\": " + eyePixels + ",");
                sb.AppendLine("  \"eyesOk\": " + (eyesOk ? "true" : "false") + ",");
                sb.AppendLine("  \"transparentOk\": " + (transparentOk ? "true" : "false") + ",");
                sb.AppendLine("  \"px\": { \"magenta\": " + magenta + ", \"dark\": " + dark + ", \"other\": " + other + " }");
                sb.AppendLine("}");
                report = sb.ToString();
            }
            catch (Exception ex)
            {
                report = "{ \"ok\": false, \"error\": \"" + ex.GetType().Name + ": "
                         + ex.Message.Replace("\\", "\\\\").Replace("\"", "'") + "\" }";
            }

            try
            {
                string path = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pet-selftest.json");
                File.WriteAllText(path, report);
            }
            catch { }

            return report;
        }
    }
}