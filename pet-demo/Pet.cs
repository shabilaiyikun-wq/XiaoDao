using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

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

        readonly Stopwatch _clock = new Stopwatch();

        /* 3D 舞台 + 动画状态都在 PetStage 里，和岛内集成共用同一份实现。
           这里不再自己持有 runtime / ModelVisual3D / 几何 key —— 那三样一旦
           两处各写一遍，「岛内的球」和「出场的球」就会各跑一套动画，
           而那正是这次要避免的事。 */
        readonly PetStage _stage = new PetStage(PetStage.RefDiameter);

        DispatcherTimer _hit;
        DispatcherTimer _demo;
        bool _demoSpinNext = true;

        IntPtr _hwnd = IntPtr.Zero;
        double _dpi = 1.0;

        bool _ignoring = true;
        bool _hover;
        bool _down;
        bool _maybeDrag;
        bool _dragging;
        double _downX, _downY;
        double _grabX, _grabY;

        /* 当前有一个「文件」正被拖到球附近（OLE 已经把我们选为放置目标）。
           只有它成立时才让眼睛跟手；普通鼠标移动、拖窗口、拖文本都不会置位。*/
        bool _dragFiles;
        /* 文件拖拽离开球体之后的延续跟随（见 OnDragLeaveFiles）。
           OLE 只在球内通知我们，所以球外这一段靠「同一次拖拽 + 按键仍按着」延续。*/
        bool _dragFollow;
        /* 可被 OLE 命中的外圈半径（DIP，相对球心）。球内永远可命中；
           球外只在外来拖拽进行中（按键按下）扩到这里 —— 这样文件从稍远处
           拖过来就能被识别，不用先压到球上。代价见 OnHitTick 的注释。*/
        const double ListenR = 260;
        /* 当前是否正处于「可被 OLE 命中」的状态（只是诊断用） */
        bool _oleVisible;
        /* DragOver 诊断用的上一次记录位置（-9999 = 还没记过，NaN 比不过阈值） */
        double _lookLogX = -9999, _lookLogY = -9999;
        double _followLogX = -9999, _followLogY = -9999;

        bool _animating;
        double _lastRender = -1;
        double _lastAnimationTime;

        double _pokeAt = -1;
        double _spinAt = -1;

        long _frames;
        readonly string _logPath;

        public long Frames { get { return _frames; } }
        public bool Ignoring { get { return _ignoring; } }

        /// <summary>当前动画状态名：idle/happy/… 之一，或 PlayClip 之后就是那段动画自己的名字。
        /// 只读。主程序的右键菜单靠它决定「表情模式」和 28 段动画里哪一项该打勾，
        /// 自己再记一份「上次点了什么」会和 runtime 的真实状态漂移。</summary>
        public string State { get { return _stage.State; } }

        public void Poke()
        {
            _pokeAt = _clock.Elapsed.TotalSeconds;
            _stage.Poke();
            Trace("poke command");
        }

        public void Spin()
        {
            _spinAt = _clock.Elapsed.TotalSeconds;
            _stage.Spin();
            Trace("spin command");
        }

        public void SetState(string state) { _stage.SetState(state); }
        public void PlayClip(string clip) { _stage.PlayClip(clip); }

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

            /* 3D 球体由正交相机投影，舞台只负责把画面放到窗口中心。
               PetStage.Shell 是固定尺寸的视口（标称球径的 1.6 倍），比窗口大一圈；
               多出来的部分是透明的，片段里的位移轨道就在这个视口里走，
               不再靠 RenderTransform 挪画面（挪了会连带把位移量重复一次）。 */
            Canvas stage = new Canvas();
            stage.IsHitTestVisible = false;
            stage.Width = Size;
            stage.Height = Size;
            Canvas.SetLeft(_stage.Shell, Size / 2.0 - _stage.ShellSize / 2.0);
            Canvas.SetTop(_stage.Shell, Size / 2.0 - _stage.ShellSize / 2.0);
            stage.Children.Add(_stage.Shell);
            root.Children.Add(stage);

            Content = root;

            /* 文件拖动检测：只接事件，不改投放行为。
               放置目标的注册本来就有（WPF 给每个 HwndSource 都注册了
               OleDropTargetInterface，与 AllowDrop 无关，实测四个用例全 True），
               而在此之前 AllowDrop=false 时 WPF 对任何拖放都回 None ——
               球心这块区域现在就已经在拒绝投放。这里接上事件后仍然一律回 None，
               所以投放行为与改动前逐字一致，多出来的只是「我们知道有人拖文件了」。*/
            AllowDrop = true;
            DragEnter += OnDragEnterFiles;
            DragOver += OnDragOverFiles;
            DragLeave += OnDragLeaveFiles;
            Drop += OnDropFiles;

            RestorePosition();

            Loaded += OnLoaded;
            SourceInitialized += OnSourceInit;
            Closed += OnClosed;

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
            if (_demoSpinNext) Spin(); else Poke();
            _demoSpinNext = !_demoSpinNext;
            Trace("demo tick t=" + _clock.Elapsed.TotalSeconds.ToString("0.0"));
        }

        /* 关窗必须把手上的外部引用全部摘掉，否则进程退出不了、对象也回收不了：
             * CompositionTarget.Rendering 是静态事件 —— 不忘 -= 就永久多一路每帧回调，
               再开一次桌宠就是两路动画叠在一起；
             * DispatcherTimer 挂在 Dispatcher 上，会一直把窗口钉在内存里；
             * Loaded / SourceInitialized / Closed 是本窗口对自身的委托，摘干净不留环。 */
        void OnClosed(object sender, EventArgs e)
        {
            Loaded -= OnLoaded;
            SourceInitialized -= OnSourceInit;
            Closed -= OnClosed;

            StopAnimating();

            if (_hit != null)
            {
                _hit.Stop();
                _hit.Tick -= OnHitTick;
                _hit = null;
            }
            if (_demo != null)
            {
                _demo.Stop();
                _demo.Tick -= OnDemoTick;
                _demo = null;
            }

            _clock.Stop();
            Trace("closed");
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

            /* 一帧只推一次状态：runtime 推进 + 几何按 key 去重后刷新，
               都在 PetStage.Advance 里。这里只剩节流和计数。 */
            _stage.Advance(Math.Max(0, t - _lastAnimationTime));
            _lastAnimationTime = t;
            _frames++;
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

            bool down = Native.LeftButtonDown();

            /* 「能不能被 OLE 找到」和「鼠标是不是在球上」是两件事，必须分开。

               以前穿透样式直接跟着 inside 走，于是 OLE 只有在球内才找得到我们 ——
               文件必须先压到球上才认得出来（用户反馈：要拖很近才识别得到）。

               现在：球内永远可命中；球外只在**有按键按下**（外来拖拽正在进行）
               就把可命中范围扩到 ListenR。这只影响 WindowFromPoint 能不能命中，
               鼠标交互（悬停 / 戳 / 拖窗口）仍然只看 inside —— 在外圈按一下不会
               把桌宠拖走，也不会戳它。*/
            bool oleVisible = inside || (down && dist <= ListenR);

            if (inside != _hover)
            {
                _hover = inside;
                Trace("hover -> " + inside
                      + " cursor=" + cursorX.ToString("0.#") + "," + cursorY.ToString("0.#")
                      + " local=" + dx.ToString("0.#") + "," + dy.ToString("0.#")
                      + " dist=" + dist.ToString("0.##")
                      + " win=" + Left.ToString("0.#") + "," + Top.ToString("0.#")
                      + " dpi=" + _dpi.ToString("0.###"));
            }

            if (oleVisible != _oleVisible)
            {
                _oleVisible = oleVisible;
                Trace("ole visible=" + oleVisible + " dist=" + dist.ToString("0.0"));
            }
            /* 每拍都写一次：ApplyClickThrough 内部对「值没变」直接返回，
               而别的分支（拖窗口、EndPress）也会改这个样式，所以不能只在
               值变化时写 —— 否则会被那些分支留下的状态卡住。*/
            ApplyClickThrough(!oleVisible);

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

            /* 球外延续跟随。走到这里说明光标已经离开球体（URL 那次 DragLeave
               或者一直在外面绕），OLE 不再给位置，所以直接用这一拍的轮询结果。
               一旦松手就彻底结束，眼睛滑回片段自己的张望轨道。*/
            if (_dragFollow)
            {
                if (!down)
                {
                    _dragFollow = false;
                    _dragFiles = false;
                    _stage.LookAway();
                    Trace("drag follow ended look=" + _stage.LookWeight.ToString("0.00"));
                }
                else
                {
                    FollowPointer(dx, dy, dist);
                    /* 诊断：球外这一段靠轮询，日志是唯一证据 */
                    if (Math.Abs(cursorX - _followLogX) + Math.Abs(cursorY - _followLogY) > 12)
                    {
                        _followLogX = cursorX; _followLogY = cursorY;
                        Trace("follow local=" + dx.ToString("0.0") + "," + dy.ToString("0.0")
                              + " dist=" + dist.ToString("0.0")
                              + " gaze=" + _stage.Frame.GazeX.ToString("0.00") + ","
                              + _stage.Frame.GazeY.ToString("0.00")
                              + " look=" + _stage.LookWeight.ToString("0.00"));
                    }
                }
            }
        }

        /* 不限距离：一旦确认是文件拖拽，眼睛就一直看着光标（方向按 BodyR 归一化、
               钳到 ±1，远处只是方向不变、偏转已满）。之前这里有个 220 DIP 的衰减，
               实测下来「远一点就不跟了」，而用户要的是拖文件时一路都跟。*/
        void FollowPointer(double dx, double dy, double dist)
        {
            double gx, gy;
            LookDirection(dx, dy, out gx, out gy);
            _stage.LookAt(gx, gy);
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
                Poke();
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

        /* ------------------------------------------------ 文件拖动时的注视

           规则：只有「正在拖文件」且「光标在球附近」两个条件同时成立，眼睛才
           跟鼠标；普通移动、拖窗口、拖文本一律继续随机张望。两个条件都不是猜的：

             * 是不是文件 —— 由 OLE 交到放置目标手里的 IDataObject
               （GetDataPresent(FileDrop)）。这是唯一能把「拖文件」和「拖文本 /
               框选 / 拖窗口」分开的地方：后面的几种根本没有 IDataObject，
               全局钩子也只能看到按钮和坐标，看不到被拖的内容。
             * 在不在附近 —— OLE 用 WindowFromPoint 投递，而本窗口只有在球附近
               （OnHitTick 的 hover 判定，半径 BodyR+HoverSlack）才关掉
               WS_EX_TRANSPARENT。能收到 DragEnter 本身就等于「鼠标进来了」，
               不需要自己再拿光标位置去比。

           结束与取消都收得到：正常放开走 Drop，拖到别处走 DragLeave，
           按 Esc 取消 OLE 也会发 DragLeave。三个口子都归一成 _dragFiles=false
           和 LookAway()，权重自己滑到 0，眼睛就平滑地回到片段自己的张望轨道。*/
        void OnDragEnterFiles(object sender, DragEventArgs e)
        {
            bool files = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
            LookAtScreenDip(e.GetPosition(this), files);
            Trace("drag enter files=" + files + " follow=" + _dragFiles
                  + " look=" + _stage.LookWeight.ToString("0.00")
                  + " formats=[" + (e.Data == null ? "null" : string.Join(",", e.Data.GetFormats())) + "]");
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        /* 「与窗口无关」的核心：中继窗口和桌宠自己的放置目标都走这里。
           p 是**桌宠窗口本地 DIP**，files 表示 OLE 是否声明了 FileDrop。*/
        internal void LookAtScreenDip(Point p, bool files)
        {
            if (files)
            {
                _dragFiles = true;
                _dragFollow = false;      /* OLE 又在给权威位置了，延续模式让位 */
                LookAtPointer(p);
            }
            else
            {
                /* 拖的是文本 / 链接之类：不跟手，并且保证上一轮的权重归零 */
                _dragFiles = false;
                _stage.LookAway();
            }
        }

        void OnDragOverFiles(object sender, DragEventArgs e)
        {
            Point p = e.GetPosition(this);
            if (_dragFiles) LookAtPointer(p);
            /* 诊断：证明 DragOver 真的重复到达，而且交给渲染的帧确实拿到了视线。
               只在光标明显移动时记一行，拖动时不会刷屏。*/
            if (_dragFiles && (Math.Abs(p.X - _lookLogX) + Math.Abs(p.Y - _lookLogY)) > 6)
            {
                _lookLogX = p.X; _lookLogY = p.Y;
                Trace("drag over p=" + Math.Round(p.X, 1) + "," + Math.Round(p.Y, 1)
                      + " gaze=" + _stage.Frame.GazeX.ToString("0.00") + ","
                      + _stage.Frame.GazeY.ToString("0.00")
                      + " look=" + _stage.LookWeight.ToString("0.00"));
            }
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        void OnDragLeaveFiles(object sender, DragEventArgs e)
        {
            DragLeft();
            e.Handled = true;
        }

        void OnDropFiles(object sender, DragEventArgs e)
        {
            /* 不接受：回答 None，和改动前（AllowDrop=false 时 WPF 自己的回答）一致 */
            Trace("drop refused look=" + _stage.LookWeight.ToString("0.00"));
            DragDropped();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }

        internal void DragLeft()
        {
            /* 文件拖拽离开了球体：此时还没有结束 —— 用户可能只是把文件
               拖到桌宠旁边。OLE 只在球内通知我们，所以从这里开始转入
               「同一次已被 OLE 确认的拖拽 + 按键仍按着」的延续跟随，
               松手（或下一次 Drop）就结束。检测本身仍然只靠 OLE。*/
            if (_dragFiles)
            {
                _dragFollow = true;
                Trace("drag leave -> follow continues look=" + _stage.LookWeight.ToString("0.00"));
            }
            else
            {
                /* 非文件拖拽：本来就没跟，这里保证权重一定是 0 */
                _dragFollow = false;
                _stage.LookAway();
            }
        }

        internal void DragDropped()
        {
            _dragFiles = false;
            _dragFollow = false;
            _stage.LookAway();
        }

        /* 偏移 → 视线方向：+X 向右、+Y 向上（Pet3D 的 Y 轴朝上，
           作者写的 idle-look-up 用的就是 +0.62），所以纵向取反。
           归一化与饱和都走 PetGaze（与岛内那只桌宠同一份实现）：
           先取方向、再乘 tanh 饱和后的幅值。改动前是 dx/BodyR 逐轴钳到 ±1，
           斜着拖时两根轴各自先撞顶，37° 会被掰成 45° —— 远处方向是错的。
           reach 跟着球半径走，所以这里换尺寸也不影响手感。*/
        static void LookDirection(double dx, double dy, out double gx, out double gy)
        {
            PetGaze.FollowScreen(dx, dy, BodyR, out gx, out gy);
        }

        void LookAtPointer(Point p)
        {
            const double centre = Size / 2.0;
            double gx, gy;
            LookDirection(p.X - centre, p.Y - centre, out gx, out gy);
            _stage.LookAt(gx, gy);
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
                /* 判据要跟待机池的组成无关：不再拿绝对 Yaw 去比 π，而是比同一时刻
                   「有冲量」和「没冲量」的差。待机池从 27 段扩到 34 段之后，
                   t=12.8s 落在哪段参考动画已经变了，绝对角度本来就不稳；
                   差值是冲量自己的贡献（Age 0.475 / Duration 0.9 → π·1.087 ≈ 3.42 rad）。*/
                PetFrame spinBaseFrame = PetAnim.Sample(probeIn);
                bool spinTransition = spinningFrame.State == "spin"
                                   && spinningFrame.Expression == PetExpression.Happy
                                   && Math.Abs(spinningFrame.Yaw - spinBaseFrame.Yaw) > 2.0;
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
                bool gazeRangeVisible = Math.Abs(Pet3D.GazeShift(0.85) - Pet3D.GazeShift(-0.85)) > 0.40;
                bool idleExpressionVariety = idleExpressions.Count >= 5;
                PetInput pointerProbeA = probeIn;
                pointerProbeA.Time = 18.2;
                pointerProbeA.HasGaze = true;
                pointerProbeA.GazeX = -0.9;
                pointerProbeA.GazeY = 0.8;
                PetInput pointerProbeB = pointerProbeA;
                pointerProbeB.GazeX = 0.9;
                pointerProbeB.GazeY = -0.8;
                PetFrame pointerFrameA = PetAnim.Sample(pointerProbeA);
                PetFrame pointerFrameB = PetAnim.Sample(pointerProbeB);
                bool gazeIgnoresPointer = Math.Abs(pointerFrameA.GazeX - pointerFrameB.GazeX) < 0.0001
                                       && Math.Abs(pointerFrameA.GazeY - pointerFrameB.GazeY) < 0.0001;
                double minGaze = 1.0, maxGaze = -1.0;
                for (int i = 0; i < 1200; i++)
                {
                    PetInput s = probeIn;
                    s.Time = i * 0.05;
                    s.HasGaze = false;
                    PetFrame wandering = PetAnim.Sample(s);
                    if (wandering.GazeX < minGaze) minGaze = wandering.GazeX;
                    if (wandering.GazeX > maxGaze) maxGaze = wandering.GazeX;
                }
                bool wanderingRangeVisible = maxGaze - minGaze > 1.1;

                /* ------------------------------------------------ 文件拖动注视

                   三条判据 + 一条防穿模：拖文件时跟手、方向对、普通鼠标不跟，
                   松手后平滑回到片段自己的张望。全部走生产用的
                   PetAnimationRuntime，不另写一份逻辑。*/
                const double RampSeconds = 1.0;
                int rampSteps = (int)Math.Round(RampSeconds / PetAnimationRuntime.Step);

                /* 1) 拖文件时跟手，且方向与目标一致 */
                PetAnimationRuntime lookA = new PetAnimationRuntime(17);
                lookA.LookAt(0.85, -0.60);
                double maxLookJump = 0.0;
                double prevGazeX = lookA.Frame.GazeX;
                for (int i = 0; i < rampSteps; i++)
                {
                    lookA.Advance(PetAnimationRuntime.Step);
                    double jump = Math.Abs(lookA.Frame.GazeX - prevGazeX);
                    if (jump > maxLookJump) maxLookJump = jump;
                    prevGazeX = lookA.Frame.GazeX;
                }
                bool pointerGazeFollows = Math.Abs(lookA.Frame.GazeX - 0.85) < 0.02
                                    && Math.Abs(lookA.Frame.GazeY + 0.60) < 0.02
                                    && lookA.LookWeight > 0.999;

                /* 2) 反向：目标换到左下，眼睛必须跟着换向 */
                PetAnimationRuntime lookB = new PetAnimationRuntime(17);
                lookB.LookAt(-0.85, 0.60);
                for (int i = 0; i < rampSteps; i++) lookB.Advance(PetAnimationRuntime.Step);
                bool pointerGazeDirectional = lookB.Frame.GazeX < -0.80
                                        && lookB.Frame.GazeY > 0.55
                                        && Math.Abs(lookB.Frame.GazeX - lookA.Frame.GazeX) > 1.6;

                /* 3) 没被要求跟的时候不动：从没 LookAt 过的 runtime 权重恒为 0。
                      这是「底稿自己跑」的基线，下面 4) 的恢复要跟它逐位比对。*/
                PetAnimationRuntime lookNone = new PetAnimationRuntime(17);
                for (int i = 0; i < rampSteps; i++) lookNone.Advance(PetAnimationRuntime.Step);

                /* 3b) 保持态（第三层）：身体停在中立帧、随机池停摆，眼睛照样跟。
                       这一条取代了旧的 normalMouseNoLook —— 需求从「只有拖文件时跟」
                       改成了「第三层开着就跟」，所以普通鼠标移动现在**就是**要跟的。 */
                PetAnimationRuntime holdA = new PetAnimationRuntime(17);
                holdA.Hold(true);
                holdA.LookAt(0.85, -0.60);
                for (int i = 0; i < rampSteps; i++) holdA.Advance(PetAnimationRuntime.Step);
                PetFrame holdFrame = holdA.Frame;
                bool holdFreezesRandomActions = Math.Abs(holdFrame.SquashX - 1) < 1e-6
                                             && Math.Abs(holdFrame.SquashY - 1) < 1e-6
                                             && Math.Abs(holdFrame.Zoom - 1) < 1e-6
                                             && Math.Abs(holdFrame.Yaw) < 1e-6
                                             && Math.Abs(holdFrame.Pitch) < 1e-6
                                             && Math.Abs(holdFrame.Roll) < 1e-6
                                             && Math.Abs(holdFrame.MorphAmount) < 1e-6
                                             && holdFrame.State == "hold";
                bool holdStillFollowsPointer = Math.Abs(holdFrame.GazeX - 0.85) < 0.02
                                            && Math.Abs(holdFrame.GazeY + 0.60) < 0.02
                                            && holdA.LookWeight > 0.999;

                /* 3c) 退出保持：随机池必须自己回来（换个片段继续跑），
                       而且身体真的重新动起来 —— 只判 State 变了不够。 */
                holdA.LookAway();
                holdA.Hold(false);
                bool holdReleaseRestores = false;
                for (int i = 0; i < rampSteps; i++)
                {
                    holdA.Advance(PetAnimationRuntime.Step);
                    if (holdA.Frame.State != "hold"
                        && (Math.Abs(holdA.Frame.SquashY - holdFrame.SquashY) > 1e-6
                            || Math.Abs(holdA.Frame.Yaw - holdFrame.Yaw) > 1e-6
                            || Math.Abs(holdA.Frame.Lid - holdFrame.Lid) > 1e-6))
                    {
                        holdReleaseRestores = true;
                        break;
                    }
                }

                /* 4) 结束后平滑恢复：先看 1s，再松手 1s，必须回到「从没看过」的
                      同种类 runtime 的同一帧；恢复过程每帧不许跳变。*/
                PetAnimationRuntime lookThenBack = new PetAnimationRuntime(17);
                lookThenBack.LookAt(0.85, -0.60);
                for (int i = 0; i < rampSteps; i++) lookThenBack.Advance(PetAnimationRuntime.Step);
                lookThenBack.LookAway();
                double maxRestoreJump = 0.0;
                prevGazeX = lookThenBack.Frame.GazeX;
                for (int i = 0; i < rampSteps; i++)
                {
                    lookThenBack.Advance(PetAnimationRuntime.Step);
                    double jump = Math.Abs(lookThenBack.Frame.GazeX - prevGazeX);
                    if (jump > maxRestoreJump) maxRestoreJump = jump;
                    prevGazeX = lookThenBack.Frame.GazeX;
                }
                bool pointerGazeRestores = Math.Abs(lookThenBack.Frame.GazeX - lookNone.Frame.GazeX) < 1e-9
                                     && Math.Abs(lookThenBack.Frame.GazeY - lookNone.Frame.GazeY) < 1e-9
                                     && lookThenBack.LookWeight == 0.0;

                /* 进和出都要是「滑」不是「跳」：单帧位移远小于整段行程 */
                bool pointerGazeSmooth = maxLookJump < 0.08 && maxRestoreJump < 0.08;

                /* 4b) 跟随不限距离：tanh 饱和之后 300 / 600 / 1200 DIP 必须给出**同一个**
                       方向与幅值，而且方向不能被逐轴钳制掰弯（0.6 : 0.8 就是 3:4）。
                       以前这里逐轴钳到 ±1，远处会得到 (1,-1) —— 45° 而不是 37°。 */
                bool followReachesAtAnyDistance = true;
                double refGx = 0, refGy = 0;
                double[] probeDists = new double[] { 300, 600, 1200 };
                for (int i = 0; i < probeDists.Length; i++)
                {
                    double gx, gy;
                    LookDirection(probeDists[i] * 0.6, probeDists[i] * 0.8, out gx, out gy);
                    if (i == 0) { refGx = gx; refGy = gy; }
                    else if (Math.Abs(gx - refGx) > 1e-9 || Math.Abs(gy - refGy) > 1e-9)
                        followReachesAtAnyDistance = false;
                }
                /* 光「三处相等」不够，全算成 0 也相等：还要钉住「方向对、幅值满」。 */
                double refLength = Math.Sqrt(refGx * refGx + refGy * refGy);
                if (Math.Abs(refLength - PetGaze.MaxAmount) > 1e-9) followReachesAtAnyDistance = false;
                if (Math.Abs(refGx / refGy - (0.6 / -0.8)) > 1e-9) followReachesAtAnyDistance = false;
                if (!(refGx > 0.5 && refGy < -0.7)) followReachesAtAnyDistance = false;

                /* 5) 满偏转不过球：±1 的视线喂进去，眼睛中心仍在球面内。
                      防穿模本身在 PetShape.EyePair（0.92 球面归一 + z<0.12 剔除）
                      和 Pet3D.GazeShift（±1 钳制）里，这里只钉住「喂满也安全」。*/
                bool extremeLookStaysOnBody = true;
                for (int gx = -1; gx <= 1; gx += 2)
                {
                    for (int gy = -1; gy <= 1; gy += 2)
                    {
                        Geometry extreme = PetShape.EyePair(BodyR, gx, gy, 0, 0, 0,
                            PetExpression.Neutral, EyeW, EyeH);
                        GeometryGroup group = extreme as GeometryGroup;
                        if (group == null) { extremeLookStaysOnBody = false; continue; }
                        for (int i = 0; i < group.Children.Count; i++)
                        {
                            Rect eyeBox = group.Children[i].Bounds;
                            double cxp = eyeBox.Left + eyeBox.Width / 2.0;
                            double cyp = eyeBox.Top + eyeBox.Height / 2.0;
                            if (Math.Sqrt(cxp * cxp + cyp * cyp) > BodyR) extremeLookStaysOnBody = false;
                        }
                    }
                }

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
                RectangleGeometry capsule = frontEyes == null ? null
                    : ((GeometryGroup)frontEyes).Children[0] as RectangleGeometry;
                bool capsuleEyes = capsule != null
                                && Math.Abs(capsule.RadiusX - capsule.Rect.Width / 2.0) < 0.001
                                && Math.Abs(capsule.RadiusY - capsule.Rect.Width / 2.0) < 0.001;

                MeshGeometry3D sphereMesh = Pet3D.SphereMesh(32, 20);
                MeshGeometry3D eyeMesh = Pet3D.EyeMesh(0.3, 0.05, 0.12, 0.30);
                bool sphereMeshOk = sphereMesh != null && sphereMesh.Positions.Count > 500
                                  && sphereMesh.Normals.Count == sphereMesh.Positions.Count
                                  && sphereMesh.TriangleIndices.Count > 2500;
                bool eyeMeshOk = eyeMesh != null && eyeMesh.Positions.Count > 20
                               && eyeMesh.TriangleIndices.Count > 60;
                MeshGeometry3D edgeEye = Pet3D.EyeMesh(0.85, 0.04, 0.20, 0.25);
                double minEyeX = double.PositiveInfinity, maxEyeX = double.NegativeInfinity;
                double minEyeY = double.PositiveInfinity, maxEyeY = double.NegativeInfinity;
                if (edgeEye != null)
                {
                    for (int i = 0; i < edgeEye.Positions.Count; i++)
                    {
                        Point3D point = edgeEye.Positions[i];
                        if (point.X < minEyeX) minEyeX = point.X;
                        if (point.X > maxEyeX) maxEyeX = point.X;
                        if (point.Y < minEyeY) minEyeY = point.Y;
                        if (point.Y > maxEyeY) maxEyeY = point.Y;
                    }
                }
                /* 边缘窄、正面宽 —— 这条以前断的是**相反**的性质：
                   旧实现把眼睛当屏幕平面里的矩形，在 x=0.85 处投影出来
                   仍然是 0.20 宽（等于「不管转到哪都一样宽」的贴纸感）。
                   现在是切平面贴片，同一个尺寸在正面 0.20、在轮廓处只剩
                   right.x 那么多（约 0.105）。所以判据改成：
                   高度基本不变、宽度必须明显变窄、而且是真的球面贴片。 */
                MeshGeometry3D faceEye = Pet3D.EyeMesh(0.0, 0.04, 0.20, 0.25);
                double faceW = 0, faceH = 0, edgeW = 0, edgeH = 0;
                bool faceEyeOnSurface = faceEye != null;
                if (faceEye != null)
                {
                    double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
                    double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
                    for (int i = 0; i < faceEye.Positions.Count; i++)
                    {
                        Point3D point = faceEye.Positions[i];
                        if (point.X < minX) minX = point.X;
                        if (point.X > maxX) maxX = point.X;
                        if (point.Y < minY) minY = point.Y;
                        if (point.Y > maxY) maxY = point.Y;
                        double d2 = point.X * point.X + point.Y * point.Y;
                        double r = Math.Sqrt(d2 + point.Z * point.Z);
                        if (d2 > 0.97 * 0.97 || Math.Abs(r - 1.015) > 0.0001) faceEyeOnSurface = false;
                    }
                    faceW = maxX - minX;
                    faceH = maxY - minY;
                }

                edgeW = maxEyeX - minEyeX;
                edgeH = maxEyeY - minEyeY;
                bool eyeProjectedShapeOk = edgeEye != null && faceEye != null
                    && Math.Abs(faceW - 0.20) < 0.003
                    && Math.Abs(faceH - 0.25) < 0.003
                    && edgeW > 0.08 && edgeW < 0.13
                    && edgeH > 0.24 && edgeH < 0.26
                    && faceEyeOnSurface;
                bool eyeSurfaceOk = true;
                if (eyeMesh != null)
                {
                    for (int i = 0; i < eyeMesh.Positions.Count; i++)
                    {
                        Point3D point = eyeMesh.Positions[i];
                        double d = point.X * point.X + point.Y * point.Y;
                        double radius = Math.Sqrt(d + point.Z * point.Z);
                        if (d > 0.97 * 0.97 || Math.Abs(radius - 1.015) > 0.0001)
                        {
                            eyeSurfaceOk = false;
                            break;
                        }
                    }
                }
                PetFrame extremeGaze = new PetFrame();
                extremeGaze.Expression = PetExpression.Neutral;
                extremeGaze.GazeX = 1.0;
                extremeGaze.GazeY = 1.0;
                Model3DGroup gazeScene = Pet3D.Scene(extremeGaze);
                bool gazeSurfaceOk = gazeScene.Children.Count >= 2;
                for (int i = gazeScene.Children.Count - 2; i < gazeScene.Children.Count; i++)
                {
                    GeometryModel3D eyeModel = gazeScene.Children[i] as GeometryModel3D;
                    MeshGeometry3D mesh = eyeModel == null ? null : eyeModel.Geometry as MeshGeometry3D;
                    if (mesh == null) { gazeSurfaceOk = false; break; }
                    for (int j = 0; j < mesh.Positions.Count; j++)
                    {
                        Point3D point = mesh.Positions[j];
                        double d = point.X * point.X + point.Y * point.Y;
                        double radius = Math.Sqrt(d + point.Z * point.Z);
                        if (d > 0.97 * 0.97 + 0.000001 || Math.Abs(radius - 1.015) > 0.0001)
                        {
                            gazeSurfaceOk = false;
                            break;
                        }
                    }
                }

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

                /* 轮廓转换发生在演示 clip 的闭眼区间。 */
                bool morphBehindBlink = true;
                for (int i = 0; i < 60; i++)
                {
                    PetFrame fr = PetClips.MorphDemo.Sample(0.80 + i * 0.005);
                    if (fr.MorphAmount > 0.05 && fr.Lid < 0.9) { morphBehindBlink = false; break; }
                }

                /* Idle morphs should happen briefly and settle back to the reference ball.

                   采样窗口不能写死。实测：整段「所有片段时长之和」（34 段 = 151.3s）
                   仍然会漏掉一两段 —— 起点 idle-rest 是构造时直接设的，洗牌在第一次
                   NextClip 才发生，所以这一段窗口刚好差一点点，缺的那一段里就包含
                   idle-morph，于是判据假红。这里改成跑到「池子里每段都出现过」为止，
                   并把这件事本身也作为判据（idlePoolFullyPlayed），
                   以后再往池子里加片段，这条会自己跟着走。

                   顺带：用 runtime 逐帧推进，而不是反复调 PetAnim.Sample ——
                   后者每次都从 0 重算，两轮下来会退化成平方复杂度。*/
                bool idleShapeReturnsToBall = true;
                bool idleShapeMorphs = false;
                PetAnimationRuntime shapeRun = new PetAnimationRuntime(17);
                HashSet<string> shapeSeen = new HashSet<string>();
                double idleCycleSeconds = 0;
                for (int i = 0; i < PetClips.Idle.Length; i++)
                    idleCycleSeconds += PetClips.Idle[i].Duration;
                int shapeCap = (int)Math.Ceiling(idleCycleSeconds * 2.2 / PetAnimationRuntime.Step);
                for (int i = 0; i < shapeCap; i++)
                {
                    shapeRun.Advance(PetAnimationRuntime.Step);
                    PetFrame fr = shapeRun.Frame;
                    shapeSeen.Add(fr.State);
                    if (fr.State == "idle-morph" && fr.MorphAmount > 0.02)
                        idleShapeMorphs = true;
                    if (fr.State != "idle-morph" && Math.Abs(fr.MorphAmount) > 0.001)
                        idleShapeReturnsToBall = false;
                    if (idleShapeMorphs && shapeSeen.Count >= PetClips.Idle.Length) break;
                }
                bool idlePoolFullyPlayed = shapeSeen.Count >= PetClips.Idle.Length;

                /* 用偏转视线检查眼睛靠近球面边缘时仍保持胶囊轮廓。 */
                StopAnimating();
                PetFrame visualProbe = new PetFrame();
                visualProbe.ShapeIndex = 0;
                visualProbe.SquashX = 1;
                visualProbe.SquashY = 1;
                visualProbe.Expression = PetExpression.Neutral;
                visualProbe.GazeX = 0.85;
                visualProbe.GazeY = 0.35;
                _stage.ApplyFrame(visualProbe);

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
                Native.KeepTopmost(_hwnd);

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

                if (shot != null)
                {
                    PngBitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(shot));
                    using (FileStream stream = File.Create(System.IO.Path.Combine(
                        AppDomain.CurrentDomain.BaseDirectory, "pet-selftest.png")))
                        encoder.Save(stream);
                }

                int magenta = 0, bodyPixels = 0, purplePixels = 0, basePurplePixels = 0,
                    flatPurplePixels = 0,
                    other = 0, eyePixels = 0;
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
                            bool isEye = rp > 200 && gp > 200 && bp > 200;
                            double bx = x - pw / 2.0;
                            double by = y - ph / 2.0;
                            bool insideBall = bx * bx + by * by
                                           < BodyR * BodyR * _dpi * _dpi * 1.06;
                            bool isBody = insideBall && !isMagenta && !isEye;
                            bool isPurple = insideBall && bp > rp + 8 && rp > gp + 2;
                            if (isPurple && Math.Abs(rp - 0x5A) <= 3
                                && Math.Abs(gp - 0x4A) <= 3 && Math.Abs(bp - 0xC8) <= 3)
                                flatPurplePixels++;
                            bool isBasePurple = insideBall && rp >= 45 && rp <= 140
                                && gp >= 30 && gp <= 115 && bp >= 120
                                && bp > rp + 35 && rp > gp + 2;

                            if (isMagenta) magenta++;
                            else if (isBody) bodyPixels++;
                            else other++;
                            if (isPurple) purplePixels++;
                            if (isBasePurple) basePurplePixels++;

                            /* 球心附近出现近白像素 = 眼睛是实色画上去的（不是挖孔） */
                            if (isEye)
                            {
                                double ex = x - midX;
                                double ey = y - midY;
                                if (ex * ex + ey * ey < eyeBand * eyeBand) eyePixels++;
                            }
                        }
                    }

                    ratio = (double)bodyPixels / (pw * ph);
                }

                bool sizeOk = ratio > 0.24 && ratio < 0.44;
                bool purpleOk = purplePixels > 1000;
                bool purpleBaseOk = basePurplePixels > purplePixels * 0.12;
                bool flatBodyColor = flatPurplePixels > purplePixels * 0.95;
                bool eyesOk = eyePixels > 200;
                bool transparentWindow = AllowsTransparency;
                SolidColorBrush backgroundBrush = Background as SolidColorBrush;
                bool transparentBrush = backgroundBrush != null
                                     && backgroundBrush.Color.A == 0;
                bool transparentOk = transparentWindow && transparentBrush;

                bool ok = deterministic && pokeTransition && spinTransition && interactionRecovery && idleClipVariety && gazeRangeVisible && gazeIgnoresPointer && wanderingRangeVisible && idleExpressionVariety && morphBehindBlink && idleShapeReturnsToBall && idleShapeMorphs && turnProjectionChanges && pitchProjectionChanges && spinHidesFace && capsuleEyes && sphereMeshOk && eyeMeshOk && eyeProjectedShapeOk && eyeSurfaceOk && gazeSurfaceOk && expressionChangesEyes && sizeOk && purpleOk && purpleBaseOk && flatBodyColor && eyesOk && transparentOk
                        && pointerGazeFollows && pointerGazeDirectional
                        && pointerGazeRestores && pointerGazeSmooth
                        && holdFreezesRandomActions && holdStillFollowsPointer && holdReleaseRestores
                        && followReachesAtAnyDistance && extremeLookStaysOnBody
                        && idlePoolFullyPlayed;

                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"ok\": " + (ok ? "true" : "false") + ",");
                sb.AppendLine("  \"frames\": " + _frames + ",");
                sb.AppendLine("  \"elapsed\": " + t.ToString("0.000") + ",");
                sb.AppendLine("  \"dpi\": " + _dpi.ToString("0.###") + ",");
                sb.AppendLine("  \"deterministic\": " + (deterministic ? "true" : "false") + ",");
                sb.AppendLine("  \"idleClipVariety\": " + (idleClipVariety ? "true" : "false") + ",");
                sb.AppendLine("  \"gazeRangeVisible\": " + (gazeRangeVisible ? "true" : "false") + ",");
                sb.AppendLine("  \"gazeIgnoresPointer\": " + (gazeIgnoresPointer ? "true" : "false") + ",");
                sb.AppendLine("  \"pointerGazeFollows\": " + (pointerGazeFollows ? "true" : "false") + ",");
                sb.AppendLine("  \"pointerGazeDirectional\": " + (pointerGazeDirectional ? "true" : "false") + ",");
                sb.AppendLine("  \"holdFreezesRandomActions\": " + (holdFreezesRandomActions ? "true" : "false") + ",");
                sb.AppendLine("  \"holdStillFollowsPointer\": " + (holdStillFollowsPointer ? "true" : "false") + ",");
                sb.AppendLine("  \"holdReleaseRestores\": " + (holdReleaseRestores ? "true" : "false") + ",");
                sb.AppendLine("  \"pointerGazeRestores\": " + (pointerGazeRestores ? "true" : "false") + ",");
                sb.AppendLine("  \"pointerGazeSmooth\": " + (pointerGazeSmooth ? "true" : "false") + ",");
                sb.AppendLine("  \"pointerGazeMaxJump\": " + maxLookJump.ToString("0.0000") + ",");
                sb.AppendLine("  \"pointerGazeMaxRestoreJump\": " + maxRestoreJump.ToString("0.0000") + ",");
                sb.AppendLine("  \"followReachesAtAnyDistance\": " + (followReachesAtAnyDistance ? "true" : "false") + ",");
                sb.AppendLine("  \"extremeLookStaysOnBody\": " + (extremeLookStaysOnBody ? "true" : "false") + ",");
                sb.AppendLine("  \"wanderingRangeVisible\": " + (wanderingRangeVisible ? "true" : "false") + ",");
                sb.AppendLine("  \"idleExpressionVariety\": " + (idleExpressionVariety ? "true" : "false") + ",");
                sb.AppendLine("  \"pokeTransition\": " + (pokeTransition ? "true" : "false") + ",");
                sb.AppendLine("  \"spinTransition\": " + (spinTransition ? "true" : "false") + ",");
                sb.AppendLine("  \"interactionRecovery\": " + (interactionRecovery ? "true" : "false") + ",");
                sb.AppendLine("  \"idleStateCount\": " + idleStates.Count + ",");
                sb.AppendLine("  \"idleExpressionCount\": " + idleExpressions.Count + ",");
                sb.AppendLine("  \"turnProjectionChanges\": " + (turnProjectionChanges ? "true" : "false") + ",");
                sb.AppendLine("  \"spinHidesFace\": " + (spinHidesFace ? "true" : "false") + ",");
                sb.AppendLine("  \"capsuleEyes\": " + (capsuleEyes ? "true" : "false") + ",");
                sb.AppendLine("  \"sphereMesh\": " + (sphereMeshOk ? "true" : "false") + ",");
                sb.AppendLine("  \"eyeMesh\": " + (eyeMeshOk ? "true" : "false") + ",");
                sb.AppendLine("  \"eyeProjectedShape\": " + (eyeProjectedShapeOk ? "true" : "false") + ",");
                sb.AppendLine("  \"eyeProjectedWidth\": " + edgeW.ToString("0.0000") + ",");
                sb.AppendLine("  \"eyeProjectedHeight\": " + edgeH.ToString("0.0000") + ",");
                sb.AppendLine("  \"eyeFrontWidth\": " + faceW.ToString("0.0000") + ",");
                sb.AppendLine("  \"eyeFrontHeight\": " + faceH.ToString("0.0000") + ",");
                sb.AppendLine("  \"eyeForeShortening\": " + (faceW > 0 ? (edgeW / faceW).ToString("0.0000") : "0") + ",");
                sb.AppendLine("  \"eyeSurface\": " + (eyeSurfaceOk ? "true" : "false") + ",");
                sb.AppendLine("  \"gazeSurface\": " + (gazeSurfaceOk ? "true" : "false") + ",");
                sb.AppendLine("  \"pitchProjectionChanges\": " + (pitchProjectionChanges ? "true" : "false") + ",");
                sb.AppendLine("  \"expressionChangesEyes\": " + (expressionChangesEyes ? "true" : "false") + ",");
                sb.AppendLine("  \"morphBehindBlink\": " + (morphBehindBlink ? "true" : "false") + ",");
                sb.AppendLine("  \"idleShapeReturnsToBall\": " + (idleShapeReturnsToBall ? "true" : "false") + ",");
                sb.AppendLine("  \"idleShapeMorphs\": " + (idleShapeMorphs ? "true" : "false") + ",");
                sb.AppendLine("  \"idlePoolFullyPlayed\": " + (idlePoolFullyPlayed ? "true" : "false") + ",");
                sb.AppendLine("  \"idlePoolClips\": " + PetClips.Idle.Length + ",");
                sb.AppendLine("  \"idlePoolSeen\": " + shapeSeen.Count + ",");
                sb.AppendLine("  \"idlePoolCycleSeconds\": " + idleCycleSeconds.ToString("0.0") + ",");
                sb.AppendLine("  \"breathRange\": " + breathRange.ToString("0.0000") + ",");
                sb.AppendLine("  \"bodyRatio\": " + ratio.ToString("0.000") + ",");
                sb.AppendLine("  \"bodyRatioOk\": " + (sizeOk ? "true" : "false") + ",");
                sb.AppendLine("  \"purplePixels\": " + purplePixels + ",");
                sb.AppendLine("  \"purpleOk\": " + (purpleOk ? "true" : "false") + ",");
                sb.AppendLine("  \"basePurplePixels\": " + basePurplePixels + ",");
                sb.AppendLine("  \"purpleBaseOk\": " + (purpleBaseOk ? "true" : "false") + ",");
                sb.AppendLine("  \"flatPurplePixels\": " + flatPurplePixels + ",");
                sb.AppendLine("  \"flatBodyColor\": " + (flatBodyColor ? "true" : "false") + ",");
                sb.AppendLine("  \"eyePixels\": " + eyePixels + ",");
                sb.AppendLine("  \"eyesOk\": " + (eyesOk ? "true" : "false") + ",");
                sb.AppendLine("  \"transparentWindowConfigured\": " + (transparentOk ? "true" : "false") + ",");
                sb.AppendLine("  \"allowsTransparency\": " + (transparentWindow ? "true" : "false") + ",");
                sb.AppendLine("  \"transparentBrush\": " + (transparentBrush ? "true" : "false") + ",");
                sb.AppendLine("  \"px\": { \"magenta\": " + magenta + ", \"body\": " + bodyPixels + ", \"purple\": " + purplePixels + ", \"other\": " + other + " }");
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
