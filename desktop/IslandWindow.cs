using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace WinIsland
{
    partial class IslandWindow : Window
    {
        /* 窗口留白：给落影和阴影描边用 */
        const double MX = 24, MT = 16, MB = 34;

        /* 弹簧：与网页版同参 */
        const double K = 250, C = 24, KCOLLAPSE = 1.75, CCOLLAPSE = 1.38;

        string _scene;
        string _state;
        string _lookKey;
        string _artKey;
        bool _playing = true;

        Spring _w, _h;
        readonly Stopwatch _clock = new Stopwatch();
        double _lastT, _morphStartMs, _lastAboveMs, _lastMorphMs;
        bool _morphing, _animating;

        Canvas _root;
        Canvas _host;
        Canvas _content;
        Canvas _shadowLayer;
        Rectangle _refract, _wash, _tint, _spec, _glow, _grain;
        Path _edge;
        Path _shA, _shB, _shC, _shD;
        ImageBrush _deskBrush;
        StreamGeometry _shape;

        DispatcherTimer _hitTimer;
        bool _hover;
        bool _pinnedState;

        /* 待办到点提醒的「临时压层」。

           以前这里直接写 _pinnedState = true，但 _pinnedState 的语义是
           「用户/命令行明确指定了层，不要自动改」，全项目没有任何地方会把它
           清回 false —— 于是提醒过一次之后三层状态机就永久死掉，
           鼠标移开也不再收层（现象：只卡在第三层收不起来）。
           提醒只是「压一会儿」，用带截止时间的临时压层表达，过点后把场景还回去，
           状态机自然接管。 */
        double _remindUntilMs;
        string _remindScene;
        const double RemindHold = 6.0;   /* 秒：至少让用户看清是哪条 */

        /* 拖动改位置：按岛体拖动整个窗口，松手落盘，下次启动还在原位 */
        bool _dragging, _dragMoved, _dragSawMove, _placed, _sized;
        Point _dragCursorStart;

        /* 位置只记「锚点」，不记窗口左上角。

           窗口是按展开态预留尺寸的（ExpW + 2MX 宽、ExpH + MT + MB 高），
           拿整个窗口去卡屏幕边界，小球就永远到不了边上：左右各差半个窗口宽
           （media 场景约 226px），下边差一整个展开高度（约 210px）。
           所以位置改成记锚点 —— 横向记岛心、纵向记岛顶。
           贴边时由 SyncPlacement 把「岛体」而不是窗口压回屏幕内，
           窗口可以有一部分在屏幕外：窗口在岛外是透明且点击穿透的，
           看不见也吃不到鼠标。 */
        double _anchorX = double.NaN, _anchorTop = double.NaN;
        double _dragAnchorX, _dragAnchorTop;

        /* 拖动过程中「第三层是否已经顶到屏幕边」。

           判据用的是**展开态**尺寸，不是当前动画尺寸：问题本身就是「卡片贴墙了吗」，
           拿动画中间值会出现「光标停住的瞬间卡片还在长，等它长到贴墙时已经没有
           新的 MoveMouse 事件了」这种漏判。松手时按这四个标志吸附（见 EndDrag）。 */
        bool _hitL, _hitR, _hitT, _hitB;

        /* 岛在窗口内的偏移。中段等于 MX / MT（和以前逐像素一致），
           贴边时被压到 0 或 2MX / MT+MB。 */
        double _ix = MX, _iy = MT;

        /* 右键菜单开着的时候冻结悬停状态机 —— 否则光标一移到菜单上，
           岛就判断「鼠标离开了」自己收起，菜单会跟卡片脱开 */
        bool _menuOpen;
        // 初值必须是 false：OnSourceInitialized 里第一次 ApplyClickThrough(true)
        // 才算「状态发生变化」并真正写一次样式。若初值为 true，那次调用会被
        // 去重逻辑挡掉，窗口在启动时就一直拦截鼠标，直到光标进出一次才恢复。
        bool _ignoring;
        IntPtr _hwnd;
        double _dpi = 1.0;

        BitmapSource _desktopShot;
        double _mx = 0.5, _my = 0.0;

        /* 实时映射：抓屏区域 = 窗口矩形，_shotRect 记录这张图覆盖的屏幕物理矩形。
           DeskViewbox 把岛的绝对位置换算进 _shotRect 的归一化空间，所以换成
           「只抓窗口」之后，采样窗口的计算一个字都不用改。 */
        Native.Grabber _grabber;
        WriteableBitmap _live;
        DispatcherTimer _liveTimer;
        Rect _shotRect;
        int _liveFrames;

        /* 允许被捕捉时玻璃的刷新间隔。

           这条路上每次采样都要「临时排除自己 → 等一帧合成 → 抓 → 恢复」，
           被排除的那十几毫秒里别的录屏软件拍不到岛。间隔越短，背景越跟手，
           但录屏里缺帧越明显。700ms 是个折中：背景仍跟着桌面变，只是慢半拍。 */
        const int GlassMaskedMs = 700;

        double _curIx, _curIy, _curW, _curH;
        bool _excluded;

        /* 能否被截图 / 录屏拍到。true = WDA_NONE（拍得到），false = 排除捕获。
           这个值和 _excluded 是一枚硬币的两面，但含义不同：
           _capture 是「用户的意愿」，_excluded 是「系统里当前实际生效的状态」。 */
        bool _capture;

        /* 中转站打样：外部拖放的三个状态。

           _armOn  左键按下 + 光标在岛附近 → 主动关掉穿透。必须这么做：
                   OLE 找投放目标用 WindowFromPoint，而它会跳过
                   WS_EX_TRANSPARENT 的窗口，不关就永远收不到 DragEnter。
           _dragIn 已经收到 DragEnter（确认是文件），此时钉住展开 + 中转站页。
           _dropOk 最近一次 Drop 收到了几个文件，只用于日志。 */
        bool _armOn;
        bool _dragIn;
        int _dropOk;
        /* 正在把中转站的文件往外拖（DoDragDrop 的嵌套循环里） */
        bool _dragOut;

        bool _selftest;
        bool _wdaProbe;
        bool _liveProbe;
        bool _heartbeat;
        bool _todoOpen;
        bool _notify;
        bool _notifyTest;
        int _freezeShot;
        string _lastSample = "";
        string _logPath;

        /* 桌宠：这一次启动允不允许自动拉起（Program.cs 按「是不是诊断模式」算），
           以及它现在到底开着还是关着。两个分开存 —— 前者管「这次要不要自动开」，
           后者管菜单勾选和退出释放。桌宠本体在 PetController 里，这里只持有一份镜像，
           每次动作后跟 PetController.IsOn 对齐，避免两边各记一套状态。 */
        bool _petAuto;
        bool _petOn;
        /* --pet-probe：岛内集成版的取证跑法。不靠抓屏猜，直接把可视树渲成位图，
           按 DIP 量球径、眼睛、遮挡；跑完写 JSON + PNG + ASCII 并退出。 */
        bool _petProbe;

        /* 已经在走退出流程。BeginShutdown 会先立这个旗，
           免得 Closed 事件里的 OnIslandClosed 再走一遍释放 + Shutdown。 */
        bool _closing;

        /* 上一次建出来的右键菜单。只给探针读，运行时逻辑一概不看它。 */
        ContextMenu _lastMenu;

        /* --------------------------------------------------------- 消息小球

           岛右边挂一颗圆球表示「某个应用来消息了」，点它把那个应用调出来。

           几何：窗口宽 = ExpW + 2MX（左右各 24 的透明留白），球挂在右侧留白
           之外，所以窗宽再加上 NotifyGap + NotifyBall。因为 Left 是从
           `_anchorX - def.ExpW/2 - MX` 推出来的（只认岛体宽度），加宽**不会把
           岛推走**，多出来的宽度全在右边。

           球挂在 _root 上而不是 _host 上：_host 被 Clip 成胶囊形状，挂在里面
           画到外面就被裁掉了。 */
        const double NotifyGap = 10;
        const double NotifyBall = 34;
        const double NotifyReserve = NotifyGap + NotifyBall;

        /* 球自己消失的时间。留着不消失会变成第二个状态栏图标；太短又容易错过。
           用户点一下也立刻消失（点它就是为了去处理消息）。 */
        const int NotifyBallMs = 25000;

        Canvas _ballDock;
        Border _ball;
        Border _ballWash;
        Border _ballColor;
        Brush _ballBrush;
        Brush _ballMat;
        ImageBrush _ballRefract;
        Ellipse _ballCore;
        bool _ballOn;
        Notify.App _ballApp;
        DateTime _ballUntil = DateTime.MinValue;

        /* 弹出动画：细胞分裂。

           进度不用 Spring 驱动器 —— Spring 的 Settled 阈值是给像素量级（0.35/12）
           设计的，拿它跑 0→1 的进度会在 65% 就判"到位"，球直接僵在半路。所以按
           时间算，末端用 easeOutBack 过冲一下。

           < 0 表示没有动画在跑，OnFrame 的收尾条件要看它，否则球飞到一半
           帧回调就被停掉了（岛体 spring 早就 settled 了）。 */
        const int BallAnimMs = 640;
        /* 起始缩放：0.30 = 一开始只是颗小珠子，长大到 1.0 */
        const double BallFromScale = 0.30;
        double _ballStartMs = -1;
        ScaleTransform _ballScale;
        TranslateTransform _ballMove;
        bool _notifyTrace;
        int _ballTraceTicks;

        public IslandWindow(string look, string scene, string art, string state,
                            bool selftest, string logPath, bool wdaProbe, bool noWda,
                            bool excludeCapture,
                            bool liveProbe, int freezeShot, bool heartbeat, bool todoOpen,
                            bool notify, bool notifyTest, bool notifyTrace,
                            bool petAuto, bool petProbe)
        {
            _heartbeat = heartbeat;
            _notify = notify;
            _notifyTest = notifyTest;
            _notifyTrace = notifyTrace;
            _todoOpen = todoOpen;
            _wdaProbe = wdaProbe;
            _liveProbe = liveProbe;
            _freezeShot = freezeShot;
            _petAuto = petAuto;
            _petProbe = petProbe;
            _petOn = PetController.IsOn;
            _lookKey = look;
            _scene = scene;
            _artKey = art;

            /* 没显式指定就恢复到上次会话的选择。
               场景切换现在是高频操作，切完重启回到 media 等于白切。 */
            Prefs.Load();
            if (_scene == null || _scene.Length == 0) _scene = Prefs.Scene.Length > 0 ? Prefs.Scene : "media";
            if (_lookKey == null || _lookKey.Length == 0) _lookKey = Prefs.Look.Length > 0 ? Prefs.Look : "glass";
            if (_artKey == null || _artKey.Length == 0) _artKey = Prefs.Art.Length > 0 ? Prefs.Art : "aurora";

            /* 抓屏亲和性：用户偏好决定默认值。
               --no-wda 强制「拍得到」，--exclude-capture 强制「拍不到」——
               后者是给实时采样那条验收用的：默认允许捕捉之后实时采样会停，
               要验它就得先把排除打开。
               两边都必须在 OnSourceInit 之前定下来，那边会立刻写进系统。 */
            _capture = !excludeCapture && (noWda || Prefs.Capture);

            /* 自检会遍历所有场景和外观，那不是在替用户做选择 */
            if (selftest) Prefs.Enabled = false;
            /* 不传 --state 时走三层自动状态机（idle/compact/expanded 由「有没有音乐」和悬停驱动）。
               显式传了就固定住——验收脚本靠这个拿到可重复的几何。 */
            _pinnedState = state != null && state != "auto";
            _state = _pinnedState ? state : "idle";

            /* 探针自己驱动层状态。两个要点：
               · 不钉层 —— 钉住的 compact 是 define 里的 200×38，而规格要验的是
                 「自动播放」那一档 112×38，所以 _pinnedState 必须为 false；
               · 但悬停/数据轮询一律不许改层：鼠标恰好停在岛上就会把摆好的
                 那一层顶掉，量出来的就不是要验的那一层。锁在构造里就上好，
                 因为 16ms 命中计时器的第一拍可能抢在 BeginInvoke 之前跑。 */
            if (_petProbe)
            {
                _pinnedState = false;
                _state = "idle";
                _petProbeLock = true;
            }
            _selftest = selftest;
            _logPath = logPath;

            /* 把 Layout 的埋点出口接到本窗口的日志上 */
            Layout.Diag = delegate(string s) { Trace(s); };
            Transfer.Diag = delegate(string s) { Trace(s); };
            Lyric.Diag = delegate(string s) { Trace(s); };
            Notify.Diag = delegate(string s) { Trace(s); };

            /* 桌面程序没有控制台，未处理异常会静默退出 —— 什么线索都不留。
               这里把它写进日志，并且不让一次绘制失败带走整个程序。 */
            if (Application.Current != null)
            {
                Application.Current.DispatcherUnhandledException +=
                    delegate(object s0, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e0)
                    {
                        Trace("UNHANDLED(dispatcher) " + e0.Exception.GetType().Name + ": " + e0.Exception.Message);
                        Trace("  at " + e0.Exception.StackTrace);
                        e0.Handled = true;
                    };

                AppDomain.CurrentDomain.UnhandledException +=
                    delegate(object s0, UnhandledExceptionEventArgs e0)
                    {
                        Exception ex0 = e0.ExceptionObject as Exception;
                        Trace("UNHANDLED(appdomain) terminating=" + e0.IsTerminating + " "
                              + (ex0 == null ? e0.ExceptionObject.ToString()
                                             : ex0.GetType().Name + ": " + ex0.Message));
                        if (ex0 != null) Trace("  at " + ex0.StackTrace);
                    };

                Application.Current.Exit += delegate
                {
                    Trace("application exit");
                };

                Application.Current.Dispatcher.ShutdownStarted += delegate
                {
                    Trace("dispatcher shutdown started");
                };
            }

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            SnapsToDevicePixels = false;
            UseLayoutRounding = false;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

            SceneDef def = Scenes.Get(_scene);
            _w = new Spring(def.ExpW);
            _h = new Spring(def.ExpH);
            if (_state == "compact") { _w.Snap(); _h.Snap(); }
            _w.Value = def.CompW; _w.Target = def.CompW;
            _h.Value = def.CompH; _h.Target = def.CompH;

            BuildVisualTree();
            CaptureDesktop();

            Loaded += OnLoaded;
            SourceInitialized += OnSourceInit;
            /* 岛窗口被外部关掉（任务栏「结束任务」发的 WM_CLOSE、系统关机、
               或者别的窗口管理器）时，ShutdownMode 是 OnExplicitShutdown ——
               进程不会自己退，桌宠会留在桌面上、岛没了。这里把这条缝堵上：
               岛没了就把桌宠一起收掉并退出。正常「退出 WinIsland」走的还是
               BeginShutdown，那条路会先把 _closing 立起来，不会重入。 */
            Closed += OnIslandClosed;
        }

        void OnIslandClosed(object sender, EventArgs e)
        {
            if (_closing) return;
            _closing = true;
            Trace("island window closed externally -> releasing pet and shutting down");
            /* 集成版的球就在这棵树上：摘掉它、停掉计时器和每帧回调，
               顺带把还给独立窗口诊断模式的 PetController 也收掉。 */
            ReleasePet();
            PetController.Release();
            StopTimersForShutdown();
            try { if (Application.Current != null) Application.Current.Shutdown(); }
            catch (Exception ex) { Trace("shutdown after external close failed: " + ex.Message); }
        }

        /// <summary>
        /// 把挂在 Dispatcher 上的计时器全停掉。
        ///
        /// 不做这一步，「关窗」之后就还剩三个活着的 timer 在叫：数据轮询、
        /// 命中轮询、实时桌面采样。它们各自持有 this，Dispatcher 又把 timer 只当
        /// 普通对象引用 —— 进程看起来退出了，实际要等下一次 tick 才发现没人要它。
        /// </summary>
        void StopTimersForShutdown()
        {
            /* _dataTimer 的 Tick 是匿名委托，摘不掉 —— Stop 就够：
               停止的 DispatcherTimer 不再是 Dispatcher 的工作项，
               它持有的闭包也就没有下一拍可跑。 */
            if (_dataTimer != null) { _dataTimer.Stop(); _dataTimer = null; }
            if (_hitTimer != null) { _hitTimer.Stop(); _hitTimer.Tick -= OnHitTick; _hitTimer = null; }
            StopLiveBackdrop();
            StopAnimating();
        }

        /* ------------------------------------------------------------ 视觉树 */
        void BuildVisualTree()
        {
            _root = new Canvas();
            _root.Background = Brushes.Transparent;
            Content = _root;

            /* 外部文件拖放（中转站）。

               AllowDrop 交给 WPF 去注册 OLE 的 IDropTarget —— 自己写
               IDropTarget 要多背一份 COM 接口，没有额外收益。
               _root 已经有 Transparent 背景（见上），所以可视树命中不会落空：
               这一点很关键，WPF 的拖放事件是按命中结果投递的，
               背景为 null 的元素不参与命中，事件可能整个不来。 */
            AllowDrop = true;
            DragEnter += OnDragEnterFiles;
            DragOver += OnDragOverFiles;
            DragLeave += OnDragLeaveFiles;
            Drop += OnDropFiles;

            /* 落影：三层同心描边 + 一层填充，跟随几何，不做模糊（每帧模糊太贵） */
            _shadowLayer = new Canvas();
            _root.Children.Add(_shadowLayer);

            Look look = Look.ByKey(_lookKey);
            _shA = ShadowPath(3, look, 0.16);
            _shB = ShadowPath(10, look, 0.085);
            _shC = ShadowPath(22, look, 0.040);
            _shD = new Path();
            _shD.Fill = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
            _shD.Fill.Freeze();
            _shadowLayer.Children.Add(_shD);
            _shadowLayer.Children.Add(_shC);
            _shadowLayer.Children.Add(_shB);
            _shadowLayer.Children.Add(_shA);

            _host = new Canvas();
            _root.Children.Add(_host);

            _refract = MakeRect();
            _deskBrush = new ImageBrush();
            _deskBrush.Stretch = Stretch.Fill;
            _refract.Fill = _deskBrush;
            _refract.Effect = new System.Windows.Media.Effects.BlurEffect
            {
                Radius = 16,
                KernelType = System.Windows.Media.Effects.KernelType.Gaussian
            };
            _host.Children.Add(_refract);

            _wash = MakeRect();
            _host.Children.Add(_wash);

            _tint = MakeRect();
            _host.Children.Add(_tint);

            _spec = MakeRect();
            _host.Children.Add(_spec);

            _glow = MakeRect();
            _host.Children.Add(_glow);

            _grain = MakeRect();
            _grain.Fill = NoiseBrush(128, 0.5);
            _host.Children.Add(_grain);

            _edge = new Path();
            _edge.StrokeThickness = 1.2;
            _edge.Fill = null;
            _host.Children.Add(_edge);

            _content = new Canvas();
            _host.Children.Add(_content);

            /* 消息小球最后加，保证画在最上层（_root 的子元素按添加顺序叠）。
               命中不靠 WPF（窗口默认是穿透的），由 OnHitTick 和 OnLeftDown
               自己算矩形，所以这里 IsHitTestVisible=false 只是省掉无谓的遍历。 */
            /* 小球是三层：

               _ball        外层 = 岛体材质（折射图 + wash + 同款白边）
               _ballColor   中层 = **甜甜圈**：应用色的圆环，圆心是空的，
                            所以中间露出的是岛体材质，不是一块实心色饼
               _ballCore     应用色**实心**圆盘，比球小一圈，外面那圈材质就是甜甜圈的圈

           为什么中间要空：实心绿球在岛旁边太沉、太突兀。环状把视觉重量压下来，
           而颜色本身已经足够区分微信和 QQ —— 所以不放文字。 */
            _ball = new Border();
            _ball.Width = NotifyBall;
            _ball.Height = NotifyBall;
            _ball.CornerRadius = new CornerRadius(NotifyBall / 2);
            /* 不要白边。一圈亮白描边贴在岛上太扎眼，球的边界交给材质自己表现
               （折射 + wash 的明暗差）。 */
            _ball.BorderThickness = new Thickness(0);
            _ball.Visibility = Visibility.Collapsed;
            _ball.IsHitTestVisible = false;

            /* 颜色层只有一环，Background 留空：圆心和最外圈都透出岛体材质。
               环往里收（Margin 5.6），所以**最外圈露出来的是材质，不是绿色** ——
               绿环贴在外缘会显得像个硬边色圈。 */
            _ballColor = new Border();
            _ballColor.CornerRadius = new CornerRadius(NotifyBall / 2);
            _ballColor.Opacity = 0;
            _ballColor.IsHitTestVisible = false;
            _ballColor.Background = null;

            _ballCore = new Ellipse();
            _ballCore.Stretch = Stretch.Fill;
            /* 实心：Fill 上色、不描边。之前做成环（Stroke + Fill=Transparent），
               圆心是空的，看起来是个甜甜圈的洞 —— 要的是实心绿。
               Margin 9 = 核心直径 16dip；球是 34dip，所以绿球只占一半，
               外面那圈材质才是"甜甜圈的圈"。 */
            _ballCore.StrokeThickness = 0;
            _ballCore.Margin = new Thickness(9);
            _ballCore.Fill = Brushes.Transparent;

            _ballColor.Child = _ballCore;

            /* wash 层：压在折射之上，跟岛体那层 RefractWash + 竖向 tint 一个意思。
               玻璃 look 的 tint 几乎是透明的（白 16%/4%、黑 14%），单靠它球会看不见
               —— 岛能这么淡是因为底下有真实桌面折射，球也得有。 */
            _ballWash = new Border();
            _ballWash.CornerRadius = new CornerRadius(NotifyBall / 2 - 1.2);
            _ballWash.IsHitTestVisible = false;

            _ball.Child = _ballWash;
            _ballWash.Child = _ballColor;

            /* 动画走 RenderTransform，不动 Canvas.Left/Top：
               PlaceBall() 负责静止位置，变形叠在它上面，两边不会互相打架。 */
            _ballScale = new ScaleTransform(1, 1);
            _ballMove = new TranslateTransform(0, 0);
            TransformGroup xf = new TransformGroup();
            xf.Children.Add(_ballScale);
            xf.Children.Add(_ballMove);
            _ball.RenderTransformOrigin = new Point(0.5, 0.5);
            _ball.RenderTransform = xf;

            /* 真正确保「球永远画不到岛上」的是裁剪，不是层级。

               一开始只把球插到 _host 之前（= 画在岛下面），以为岛会挡住它。
               不行 —— 岛体是半透明玻璃（glass 的 tint 只有白 16%/4%、黑 14%），
               球从底下滑过会被透视出来，那圈白边划过岛面尤其显眼，看起来就是
               "球跑到岛上面去了"。

               所以给球套一个固定裁剪窗口，左边缘精确压在岛体右边缘上。
               不管岛什么材质、多透明，球都画不进岛的范围内；层级仍作为额外保险，
               让岛自己的边缘高光压在球之上。 */
            _ballDock = new Canvas();
            _ballDock.Clip = new RectangleGeometry(
                new Rect(-NotifyGap, -4, NotifyBall + 200, NotifyBall + 8));
            _ballDock.IsHitTestVisible = false;
            Canvas.SetLeft(_ball, 0);
            Canvas.SetTop(_ball, 0);
            _ballDock.Children.Add(_ball);

            _root.Children.Insert(_root.Children.IndexOf(_host), _ballDock);

            /* 桌宠的两个槽位（岛内 / 岛下）。放在最后建：它要往 _root 和
               _host 里各插一层，前面几层的顺序才算定了 —— 见 PetIsland.cs。 */
            BuildPetLayers();
        }

        static Rectangle MakeRect()
        {
            Rectangle r = new Rectangle();
            r.IsHitTestVisible = false;
            Canvas.SetLeft(r, 0);
            Canvas.SetTop(r, 0);
            return r;
        }

        static Path ShadowPath(double thickness, Look look, double alpha)
        {
            Path p = new Path();
            p.Stroke = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(255 * alpha * (look.ShadowAlpha / 0.62)), 0, 0, 0));
            p.Stroke.Freeze();
            p.StrokeThickness = thickness;
            p.Fill = null;
            p.IsHitTestVisible = false;
            return p;
        }

        static ImageBrush NoiseBrush(int size, double alpha)
        {
            int stride = size * 4;
            byte[] px = new byte[size * size * 4];
            Random rnd = new Random(7);
            for (int i = 0; i < size * size; i++)
            {
                byte v = (byte)rnd.Next(0, 256);
                px[i * 4 + 0] = v;
                px[i * 4 + 1] = v;
                px[i * 4 + 2] = v;
                px[i * 4 + 3] = (byte)(alpha * 255);
            }
            BitmapSource bmp = BitmapSource.Create(size, size, 96, 96,
                PixelFormats.Bgra32, null, px, stride);
            bmp.Freeze();
            ImageBrush ib = new ImageBrush(bmp);
            ib.TileMode = TileMode.Tile;
            ib.ViewportUnits = BrushMappingMode.Absolute;
            ib.Viewport = new Rect(0, 0, size, size);
            ib.Stretch = Stretch.Fill;
            return ib;
        }

        /* ------------------------------------------------------------ 生命周期 */
        void OnSourceInit(object sender, EventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (_dpi <= 0) _dpi = 1.0;

            /* 截图热键 Alt+A。挂在岛的 HWND 上收 WM_HOTKEY ——
               岛是 WS_EX_NOACTIVATE，装不了键盘钩子，但 RegisterHotKey 是
               系统级的，不需要焦点。 */
            Shot.Attach(this, _hwnd, _dpi, delegate(string s) { Trace(s); });

            /* 抓屏亲和性：默认允许被截图/录屏拍到（见 Prefs.Capture）。

               为什么以前一直排除：实时桌面采样抓的就是岛自己那块矩形，
               不排除就会拍到自己，玻璃里映出玻璃。
               现在反过来 —— 优先保证「用户的截图里应该有岛」，把回授问题
               交给「开启捕捉时停掉实时采样」来解决（ApplyCaptureAffinity 里做）。

               --no-wda 仍然强制为「拍得到」，验收脚本要用：排除生效后
               岛在任何抓屏里都看不见，像素验收反而没法量它。 */
            ApplyCaptureAffinity();

            ApplyClickThrough(true);
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            _clock.Start();
            SyncWindowSize();
            Rebuild(false);
            Apply();
            StartData();

            /* 调试开关：直接开输入窗，验收脚本不必猜按钮坐标 */
            if (_todoOpen)
            {
                Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (Actions().AddTodo != null) Actions().AddTodo();
                }), DispatcherPriority.Background);
            }

            _hitTimer = new DispatcherTimer(DispatcherPriority.Input);
            _hitTimer.Interval = TimeSpan.FromMilliseconds(16);
            _hitTimer.Tick += OnHitTick;
            _hitTimer.Start();

            /* 消息提醒：只在 --notify 下开。 */
            if (_notify)
            {
                Notify.Arrived += delegate(Notify.App app) { ShowBall(app); };
                Notify.Start();

                /* 验收用：不等真消息，直接亮一颗，方便肉眼确认位置和点击。
                   延迟 1200ms 再弹：启动瞬间岛体自己还在从收起态展开，
                   两个动画叠在一起会让轨迹没法判读。 */
                if (_notifyTest)
                {
                    DispatcherTimer bt = new DispatcherTimer();
                    bt.Interval = TimeSpan.FromMilliseconds(1200);
                    bt.Tick += delegate
                    {
                        bt.Stop();
                        ShowBall(Notify.App.WeChat);
                    };
                    bt.Start();
                }
            }

            /* 心跳：只在 --heartbeat 下开。用来判定「UI 线程卡住」和「真的没事发生」
               是两件不同的事 —— 日志长时间没新行时，前者会让程序看起来已死。 */
            if (_heartbeat)
            {
                DispatcherTimer hb = new DispatcherTimer(DispatcherPriority.Background);
                hb.Interval = TimeSpan.FromMilliseconds(1000);
                hb.Tick += delegate
                {
                    Trace("heartbeat island=" + Math.Round(_w.Value, 1) + "x" + Math.Round(_h.Value, 1)
                          + " win=" + Math.Round(Left, 1) + "," + Math.Round(Top, 1)
                          + " menu=" + _menuOpen + " drag=" + _dragging
                          + " scene=" + _scene + "/" + _state
                          + " media=" + (_snap.MediaHas
                              ? _snap.Media.PrettyAppName + ":" + _snap.Media.Status
                                + " '" + _snap.Media.Title + "'"
                                + " art=v" + Media.ArtVersion()
                                + "(" + (Media.CachedArt() == null
                                    ? "none" : Media.CachedArt().Length + "B") + ")"
                                + " lyr=v" + Media.LyricVersion()
                                + "(" + Media.LyricLines() + "行)"
                                + " '" + Media.LyricLine(_snap.Media.Position) + "'"
                              : "none")
                          + " timer=" + _snap.TimerText + (_snap.TimerRunning ? ">" : "|")
                          + " todo=" + Todos.CountText()
                          + " " + HitInfo());
                };
                hb.Start();
            }

            /* 实时采样必须一直开着。

               原来这里写的是 `if (!_capture) StartLiveBackdrop();` —— 允许被捕捉
               时干脆不采样，折射层退回启动那一刻的整屏快照。结果是：背景再也不动
               （glass 外观下最明显，就是一张开机时的桌面照片）。

               现在改成允许被捕捉时也采样，只是每帧抓之前临时把自己排除掉、
               抓完立刻恢复（见 OnLiveTick）。所以这条守门去掉。 */
            StartLiveBackdrop();

            if (_freezeShot > 0) ScheduleFreezeShot();

            /* 挂 PreviewMouseDown / PreviewMouseUp（隧道版本），再按 ChangedButton 分派。
               两个都踩过：
               1) 挂在 _host 的 MouseLeftButtonDown 上，按下点落在卡片里的 Path 上时
                  事件到不了 _host，拖动静默失效；
               2) 挂在窗口的 PreviewMouseLeftButtonDown 上更糟 —— 那是直接事件，
                  实测根本不触发。只有隧道的 PreviewMouseDown 稳定到达窗口。 */
            PreviewMouseDown += OnPreviewDown;
            PreviewMouseUp += OnPreviewUp;
            PreviewMouseMove += OnMouseMove;
            PreviewMouseWheel += OnWheel;

            /* 桌宠：正常启动时按偏好拉起来。
               三个条件缺一不可 ——
                 _petAuto     这一次不是诊断/抓帧模式（Program.cs 算的）；
                 Prefs.Pet    用户上次的开关（默认开，见 Store.cs）；
                 !IsOn        还没开着（正常启动不会，但别留第二条建窗的路）。

               放在 OnLoaded 之后半拍、用 Background 优先级：岛自己的 Rebuild/Apply/
               弹簧入场都在 Normal 优先级上，桌宠建窗（要走 3D 场景初始化）挤在一起
               会让第一帧明显迟滞。它也不写 ui.txt —— 这里只是「按偏好执行」，
               不是「改偏好」。 */
            if (_petAuto && Prefs.Pet && !PetController.IsOn)
            {
                Dispatcher.BeginInvoke(new Action(EnablePetByPreference), DispatcherPriority.Background);
            }

            if (_wdaProbe)
            {
                Dispatcher.BeginInvoke(new Action(RunWdaProbe));
            }
            else if (_liveProbe)
            {
                Dispatcher.BeginInvoke(new Action(RunSampleProbe));
            }
            else if (_selftest)
            {
                Dispatcher.BeginInvoke(new Action(async () => await RunSelfTest()));
            }
            else if (_petProbe)
            {
                Dispatcher.BeginInvoke(new Action(async () => await RunPetProbe()));
            }
            else
            {
                /* 按启动参数决定入场方式：要展开就先落紧凑再弹开，让第一眼就是动的 */
                if (_state == "expanded")
                {
                    SetState("compact", true);
                    DispatcherTimer t = new DispatcherTimer();
                    t.Interval = TimeSpan.FromMilliseconds(700);
                    t.Tick += (s2, e2) => { t.Stop(); SetState("expanded", false); };
                    t.Start();
                }
                else
                {
                    SetState(_state, true);
                }
            }
        }

        /* -------------------------------------------------------------- 尺寸 */
        double IslandW { get { return _w.Value; } }
        double IslandH { get { return _h.Value; } }

        /* 岛在窗口内的偏移由 SyncPlacement 算：中段是 (Width-IslandW)/2 和 MT，
           贴边时被压向窗口边缘 —— 展开态在窗口里居中，贴边时岛体顶住屏幕边。 */
        double IslandLeft { get { return _ix; } }
        double IslandTop { get { return _iy; } }

        /* 岛在屏幕上的矩形（DIP）。输入窗要贴着它下面弹出来。 */
        Rect IslandScreenRect()
        {
            return new Rect(Left + _ix, Top + _iy, IslandW, IslandH);
        }

        /* 待办到点：自动展开到待办卡，让用户看见是哪一条。
           提醒只做一次（Todos.TakeDue 里标记 Alerted）。 */
        void TickTodoDue()
        {
            /* 探针要量的是「层 ↔ 球」的对应关系。用户真实的待办到点了会把层
               压成 expanded（见下），正好落在探针摆好第一层之后 —— 量出来的
               就不是第一层了。探针期间不提醒，也不动用户的待办数据。 */
            if (_petProbe) return;

            TodoItem due = Todos.TakeDue();
            if (due == null) return;

            Trace("todo due: '" + due.Text + "'");

            /* 不要动 _pinnedState：那是「用户钉住的层」，没有复位路径。
               这里只压一个带截止时间的提醒，压完自动还场景并允许收层。 */
            if (_scene != "todo") _remindScene = _scene;
            _scene = "todo";
            _remindUntilMs = _clock.Elapsed.TotalMilliseconds + RemindHold * 1000.0;

            /* 必须走 SetState：它用 _w.Aim() 设目标并挂上 CompositionTarget.Rendering
               去跑弹簧动画。直接写 _w.Target 不会启动动画循环，岛会卡在旧尺寸。 */
            SetState("expanded", false);
            RefreshNow();
        }

        /* 位置落盘：%APPDATA%\WinIsland\window.txt
           新格式 "x,top,v2" —— 记的是锚点（岛心 X、岛顶 Y，DIP 整数）。
           老格式 "left,top" 记的是窗口左上角，LoadPosition 里换算，升级不跳位。 */
        static string PositionPath()
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinIsland");
            return System.IO.Path.Combine(dir, "window.txt");
        }

        void SavePosition()
        {
            try
            {
                string p = PositionPath();
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p));
                System.IO.File.WriteAllText(p,
                    ((int)Math.Round(_anchorX)).ToString() + ","
                    + ((int)Math.Round(_anchorTop)).ToString() + ",v2");
                Trace("position saved anchor " + (int)Math.Round(_anchorX)
                      + "," + (int)Math.Round(_anchorTop));
            }
            catch (Exception ex) { Trace("position save failed: " + ex.Message); }
        }

        bool LoadPosition()
        {
            try
            {
                string p = PositionPath();
                if (!System.IO.File.Exists(p)) return false;
                string[] parts = System.IO.File.ReadAllText(p).Split(',');
                if (parts.Length < 2) return false;
                int l, t;
                if (!int.TryParse(parts[0].Trim(), out l)) return false;
                if (!int.TryParse(parts[1].Trim(), out t)) return false;

                if (parts.Length >= 3 && parts[2].Trim() == "v2")
                {
                    _anchorX = l;
                    _anchorTop = t;
                }
                else
                {
                    /* 老格式：窗口左上角。窗口横向是以岛心为中心的，
                       纵向岛顶固定落在窗口顶 + MT，据此还原成锚点。 */
                    _anchorX = l + Width / 2;
                    _anchorTop = t + MT;
                }

                /* 换过显示器或改过分辨率时，旧坐标可能已经在桌面外，当作过期 */
                double vl = SystemParameters.VirtualScreenLeft;
                double vt = SystemParameters.VirtualScreenTop;
                double vr = vl + SystemParameters.VirtualScreenWidth;
                double vb = vt + SystemParameters.VirtualScreenHeight;
                if (_anchorX < vl - Scenes.IdleW) return false;
                if (_anchorTop < vt - Scenes.IdleH) return false;
                if (_anchorX > vr + Scenes.IdleW) return false;
                if (_anchorTop > vb + Scenes.IdleH) return false;

                Trace("position load ok anchor " + l + "," + t
                      + " dpi=" + _dpi.ToString("0.###") + " w=" + Math.Round(Width, 1));
                return true;
            }
            catch { return false; }
        }

        /* 默认锚点：工作区顶部居中。
           岛顶落在 WorkArea.Top + 8 + MT —— 和以前「窗口顶 = WorkArea.Top + 8、
           岛顶 = 窗口顶 + MT」逐像素一致，默认观感不变。 */
        void ResetAnchorToDefault()
        {
            Rect wa = SystemParameters.WorkArea;
            _anchorX = wa.Left + wa.Width / 2;
            _anchorTop = wa.Top + 8 + MT;
            SyncPlacement();
        }

        void SyncWindowSize()
        {
            SceneDef def = Scenes.Get(_scene);
            /* 开了消息提醒才预留右侧那颗球的位置：默认用户看不到任何区别。
               桌宠的停靠位同理 —— **只在实际开着的时候**才预留。恒留会让
               窗口比原来宽 47.6 DIP，岛在窗口里的偏移跟着变，既有验收脚本
               按「窗口 = ExpW + 48」量出来的像素位置就整体偏了；
               桌宠关着的时候（也是验收脚本跑的默认形态）必须和改之前逐像素一致。 */
            double nw = def.ExpW + MX * 2 + (_notify ? NotifyReserve : 0)
                        + (_petOn ? PetReserve : 0);
            double nh = def.ExpH + MT + MB;
            bool first = !_sized;

            Width = nw;
            Height = nh;
            _sized = true;

            if (first)
            {
                /* 第一次：用户上次拖到的位置优先（自检时忽略，保持可复现），否则顶部居中 */
                if (!_selftest && LoadPosition()) { _placed = true; SyncPlacement(); return; }
                ResetAnchorToDefault();
                return;
            }

            /* 之后：换场景只改窗口尺寸。拖过的保持岛心不动，没拖过的继续顶部居中 */
            if (!_placed) ResetAnchorToDefault();
            else SyncPlacement();

            Trace("size sync " + nw + "x" + nh + " at " + Math.Round(Left, 1) + "," + Math.Round(Top, 1)
                  + " anchor=" + Math.Round(_anchorX, 1) + "," + Math.Round(_anchorTop, 1));
        }

        /* ---------------------------------------------------------- 落点计算 */
        static double ClampD(double v, double lo, double hi)
        {
            if (hi < lo) hi = lo;
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        /* 锚点 → 岛体落点 → 窗口位置。

           两块尺寸必须分开算，这是「小岛能贴到屏幕边」的关键：
           · 岛体用它「当前」的动画尺寸去贴边。不贴边时 bl/bt 就是纯锚点
             （横向绕岛心对称展开、纵向岛顶不动），和以前完全一样；
             一旦出界就把岛体压回屏幕内 —— 于是左右贴边的卡片各自顶住屏幕边、
             底部的卡片改成往上长，而不是把整个窗口顶出去。
           · 窗口位置按「展开态」算，形变过程中不变，只有岛在窗口内部滑动
             （_ix/_iy）。所以贴边时也不会出现每帧 SetWindowPos 的抖动。 */
        void SyncPlacement()
        {
            if (double.IsNaN(_anchorX)) { ResetAnchorToDefault(); return; }

            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth;
            double vb = vt + SystemParameters.VirtualScreenHeight;

            SceneDef def = Scenes.Get(_scene);
            double w = Math.Max(2, IslandW), h = Math.Max(2, IslandH);

            /* 岛体：当前尺寸，出界就压回屏幕 */
            double bl = ClampD(_anchorX - w / 2, vl, vr - w);
            double bt = ClampD(_anchorTop, vt, vb - h);

            /* 窗口：按展开态预留，形变中不动。
               展开时 ebl/ebt 就是岛体落点，所以岛在窗口内正好回到 lead / MT。

               lead 里的 PetReserve 是**桌宠那一侧的预留加在左边**：
               窗口向左长 PetReserve，_ix 同步右移同样的量（因为 _ix 是
               「岛体左边 - 窗口左边」），于是岛在屏幕上的位置一点不动，
               只是它左边多出 47.6 DIP 的空位给球站。
               球径 33.6 + 间隙 8 需要 41.6，而展开态原本左侧只有 MX=24 —— 
               不加这块预留，球在第三层就没地方站，只能被挤到右边去。 */
            double lead = MX + (_petOn ? PetReserve : 0);
            double wl = ClampD(ClampD(_anchorX - def.ExpW / 2, vl, vr - def.ExpW) - lead, vl, vr - Width);
            double wt = ClampD(ClampD(_anchorTop, vt, vb - def.ExpH) - MT, vt, vb - Height);

            double oix = _ix, oiy = _iy;
            _ix = bl - wl;
            _iy = bt - wt;

            if (Math.Abs(Left - wl) > 0.01) Left = wl;
            if (Math.Abs(Top - wt) > 0.01) Top = wt;

            /* 位置一变就得把岛搬过去，不能只指望 Apply()。

               拖动期间没有形变动画：OnFrame 不跑（没人订阅 Rendering），
               RefreshNow 只刷内容不搬岛，所以 Apply() 根本不会被调用。
               贴左边时 _ix 要从 MX 变成 0 —— 没人搬，卡片就停在离屏幕左边界
               一个 MX（24px）的地方，看起来就是「第三层碰不到左边」。
               贴底同理，_iy 要从 MT 变成 MT+MB，差 34px。
               放在这里而不是 MoveAnchorTo 里：这是唯一算 _ix/_iy 的地方，
               以后再多几条改位置的路径也不会漏。 */
            if (Math.Abs(_ix - oix) > 0.01 || Math.Abs(_iy - oiy) > 0.01) RepositionLayers();
        }

        /* 只搬位置，不重建几何。

           拖动和吸附走这条路：每次鼠标移动都重算一遍圆角路径没有必要，
           贴边时变的是偏移、不是尺寸。 */
        void RepositionLayers()
        {
            if (_host == null) return;
            Canvas.SetLeft(_host, _ix); Canvas.SetTop(_host, _iy);
            Canvas.SetLeft(_shA, _ix); Canvas.SetTop(_shA, _iy + 2);
            Canvas.SetLeft(_shB, _ix); Canvas.SetTop(_shB, _iy + 5);
            Canvas.SetLeft(_shC, _ix); Canvas.SetTop(_shC, _iy + 9);
            Canvas.SetLeft(_shD, _ix); Canvas.SetTop(_shD, _iy + 6);
            _curIx = _ix; _curIy = _iy;
            UpdateRefract();
            PlacePet();
        }

        /* 拖动改的是锚点，不是窗口。

           贴边时岛体自己会压回屏幕内（见 SyncPlacement），所以这里只保证
           「静息小球」不出桌面 —— 拖到边上就停，不会拖出去找不回来。
           屏幕中段锚点跟光标 1:1，手感不变。 */
        void MoveAnchorTo(double ax, double at)
        {
            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth;
            double vb = vt + SystemParameters.VirtualScreenHeight;

            _anchorX = ClampD(ax, vl + Scenes.IdleW / 2, vr - Scenes.IdleW / 2);
            _anchorTop = ClampD(at, vt, vb - Scenes.IdleH);
            UpdateEdgeHit();
            SyncPlacement();
        }

        /* 第三层（展开卡片）此刻是不是已经顶在屏幕边上了。

           用展开态尺寸判，不看当前动画尺寸 —— 否则「光标停了但卡片还在长」那一下
           会被漏判成没贴墙。 */
        void UpdateEdgeHit()
        {
            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth;
            double vb = vt + SystemParameters.VirtualScreenHeight;

            SceneDef def = Scenes.Get(_scene);
            double left = _anchorX - def.ExpW / 2;

            _hitL = left < vl - 0.001;
            _hitR = left > vr - def.ExpW + 0.001;
            _hitT = _anchorTop < vt - 0.001;
            _hitB = _anchorTop > vb - def.ExpH + 0.001;
        }

        /* 松手吸附：第三层已经贴墙的，把锚点挪到「第一层贴墙」的位置。

           为什么必须有这一步：卡片绕岛心对称展开，卡片贴墙发生在岛心离墙
           ExpW/2 的地方，而小球要到岛心离墙 IdleW/2 才算贴墙 —— 中间这条
           约 200px 的带子里，卡片怎么拖都不动，松手后收起就会往中间「缩」一段，
           看着像没贴上去。吸附之后收起时小球直接落在边上。

           关键：吸附时卡片一格都不会动。因为贴墙正是 clamp 生效的状态，
           改锚点只改了「将来收起时落在哪」，所以没有跳变。 */
        void SnapAnchorToEdge()
        {
            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth;
            double vb = vt + SystemParameters.VirtualScreenHeight;

            double ox = _anchorX, oy = _anchorTop;
            string where = "";

            if (_hitL) { _anchorX = vl + Scenes.IdleW / 2; where += "L"; }
            else if (_hitR) { _anchorX = vr - Scenes.IdleW / 2; where += "R"; }

            if (_hitT) { _anchorTop = vt; where += "T"; }
            else if (_hitB) { _anchorTop = vb - Scenes.IdleH; where += "B"; }

            if (where.Length == 0) return;

            SyncPlacement();
            Trace("snap " + where + " anchor " + Math.Round(ox, 1) + "," + Math.Round(oy, 1)
                  + " -> " + Math.Round(_anchorX, 1) + "," + Math.Round(_anchorTop, 1)
                  + " win=" + Math.Round(Left, 1) + "," + Math.Round(Top, 1)
                  + " ix=" + Math.Round(_ix, 1) + " iy=" + Math.Round(_iy, 1));
        }

        /* ------------------------------------------------------------ 状态机 */
        /* 三层各自的尺寸。自动模式下第二层固定是「封面+律动」那个小尺寸，
   与当前选中的场景无关——因为那一层只表达「在放歌」。 */
        void SizeOf(string state, out double w, out double h)
        {
            SceneDef def = Scenes.Get(_scene);
            if (state == "expanded") { w = def.ExpW; h = def.ExpH; return; }
            if (state == "compact")
            {
                if (_pinnedState) { w = def.CompW; h = def.CompH; }
                else { w = Scenes.PlayW; h = Scenes.PlayH; }
                return;
            }
            w = Scenes.IdleW;
            h = Scenes.IdleH;
        }

        /* 三层自动状态机：悬停 > 在放歌 > 空闲。
           只在「想要的层」和「当前层」不同时才动，避免每 400ms 反复触发弹簧。 */
        void Reconcile()
        {
            if (_pinnedState) return;
            /* 探针期间层状态由探针脚本驱动，数据轮询不许插手 */
            if (_petProbeLock) return;

            /* 待办提醒压层：截止前不收回（悬停也照旧撑着，那是下面的 hover 规则）。
               截止后先把场景还原，再往下走正常的层级判定 —— 不能只清标记，
               否则岛会以 todo 场景的展开尺寸塌成小球。 */
            if (_remindUntilMs > 0)
            {
                if (_hover || _clock.Elapsed.TotalMilliseconds < _remindUntilMs) return;
                _remindUntilMs = 0;
                if (_scene == "todo")
                {
                    string back = (_remindScene != null && _remindScene.Length > 0)
                                  ? _remindScene
                                  : Prefs.Scene;
                    if (back == null || back.Length == 0) back = "media";
                    _remindScene = null;
                    GoScene(back, false);
                }
            }
            /* 输入窗开着时鼠标必然离开了岛（去打字了），这时候不能自动收回，
               否则用户一边写一边看着岛塌成小球 */
            if (TodoInput.Current != null && TodoInput.Current.IsOpen) return;

            /* 拖动期间钉住展开：光标一旦离开岛体，_hover 就是 false，
               卡片会缩回去 —— 投放区在用户眼皮底下跑掉，DragLeave 立刻跟着来。
               低一层也不许退：退回紧凑/收起会让目标变得比光标还小。 */
            if (_dragIn || _armOn)
            {
                ReconcileDrag();
                return;
            }

            /* 第二层「封面+律动」表达的是**在放歌**，不是在放歌的应用存在。

               所以判据必须是 MediaHas && Playing，不能只看 MediaHas。
               只看 MediaHas 的话，只要开着 QQ音乐/网易云（哪怕暂停着、或者只是
               停在某个没播的歌单上）岛就一直是第二层，用户看到的就是
               "只要有播音程序存在就会展开"。 */
            string want = _hover ? "expanded"
                        : (_snap.MediaHas && _snap.Playing ? "compact" : "idle");
            if (want == _state) return;
            Trace("auto " + _state + " -> " + want + "  media=" + _snap.MediaHas
                  + " playing=" + _snap.Playing + " hover=" + _hover);
            SetState(want, false);
        }

        /* 拖动期间要维持的形态：展开 + 停在「中转站」。

           为什么不在这里跳页：跳页在 DragEnter 里做（那时才确认是文件）。
           武装阶段（_armOn）只知道「有人按着左键在岛附近」，可能是拖文本、
           可能是在桌面上框选，这时候擅自跳到中转站会打断用户。 */
        void ReconcileDrag()
        {
            if (_state != "expanded") SetState("expanded", false);
        }

        /* ------------------------------------------------- 外部文件拖放（打样）

           目标只有一个：把「资源管理器拖过来的文件」接住并显示出来。

           整条链路的关卡不在 WPF，而在窗口样式：岛靠 WS_EX_TRANSPARENT 做点击穿透，
           而 OLE 找投放目标用 WindowFromPoint，**它会跳过穿透窗口**。
           所以顺序必须是「轮询发现有人拖着东西靠近 → 关掉穿透 → OLE 下一次
           鼠标移动命中我们 → DragEnter」。OnHitTick 里那段 _armOn 就是干这个的。 */

        void OnDragEnterFiles(object sender, DragEventArgs e)
        {
            bool files = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
            Trace("drag enter files=" + files + " state=" + _state + " scene=" + _scene);

            if (!files)
            {
                /* 拖的是文本之类：不接受，并且立刻解除武装，让 OLE 下一次移动
                   就看不见我们（穿透会重新打开），把拖放还给下面的窗口。 */
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                _armOn = false;
                return;
            }

            _dragIn = true;
            Transfer.Hover = true;

            /* 自动跳到中转站，但不落盘：这是被拖动触发的，不是用户选页。
               落盘的话下次启动会直接停在中转站。 */
            GoScene("transfer", false);
            if (_state != "expanded") SetState("expanded", false);

            e.Effects = DragDropEffects.Copy;
            e.Handled = true;

            Rebuild(false);
            Apply();
        }

        void OnDragOverFiles(object sender, DragEventArgs e)
        {
            bool files = e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects = files ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        void OnDragLeaveFiles(object sender, DragEventArgs e)
        {
            Trace("drag leave");
            _dropEnd();
        }

        void OnDropFiles(object sender, DragEventArgs e)
        {
            string[] paths = null;
            try { paths = e.Data.GetData(DataFormats.FileDrop) as string[]; }
            catch (Exception ex) { Trace("drop data failed: " + ex.Message); }

            int added = Transfer.Add(paths);
            _dropOk = added;

            Trace("DROP items=" + (paths == null ? 0 : paths.Length)
                  + " added=" + added + " total=" + Transfer.Count());
            if (paths != null)
            {
                for (int i = 0; i < paths.Length; i++) Trace("DROP path[" + i + "]=" + paths[i]);
            }

            e.Effects = added > 0 ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            _dropEnd();
        }

        /* 一次拖放交互结束后的收尾。DragLeave / Drop / 异常取消都走这里，
           三处分别写会漏掉一处，然后岛就永远卡在「武装」状态里不穿透了。 */
        void _dropEnd()
        {
            _dragIn = false;
            _armOn = false;
            Transfer.Hover = false;
            if (_state == "expanded" && !_hover) Reconcile();
            Rebuild(false);
            Apply();
        }

        /* ---------------------------------------------------- 中转站：打开 / 拖出 / 菜单 */

        void OpenTransferItem(string path)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo psi =
                    new System.Diagnostics.ProcessStartInfo(path);
                psi.UseShellExecute = true;
                System.Diagnostics.Process.Start(psi);
                Trace("transfer open " + path);
            }
            catch (Exception ex)
            {
                /* 没有关联程序、权限不足、文件已没了 —— 一律不抛到上层，
                   岛是个常驻程序，为了一个打不开的文件崩掉不值得。 */
                Trace("transfer open failed: " + ex.Message);
            }
        }

        void RevealTransferItem(string path)
        {
            try
            {
                /* /select 要求路径带引号，否则带空格的路径会被拆开 */
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                Trace("transfer reveal " + path);
            }
            catch (Exception ex) { Trace("transfer reveal failed: " + ex.Message); }
        }

        void CopyTransferPath(string path)
        {
            try
            {
                Clipboard.SetText(path);
                Trace("transfer copy path " + path);
            }
            catch (Exception ex) { Trace("transfer copy path failed: " + ex.Message); }
        }

        /* 从岛上把文件拖出去。

           传真的 FileDrop，接受方（资源管理器、聊天窗口、网页上传框）拿到的
           就是真实路径 —— 因为中转站只存引用，这里不需要准备任何文件本体。

           两个必须守的地方：
           1. DoDragDrop 是嵌套消息循环，我们的 16ms 定时器还在跑。不设 _dragOut
              OnHitTick 会中途把穿透打开，拖到自己窗口上就断了。
           2. 一律 Copy：Move 会在拖进资源管理器时把文件搬走/删掉，
              而中转站的语义是「暂存引用」，不该改变源文件。 */
        void BeginTransferDrag(string path)
        {
            if (_dragOut) { Trace("drag-out ignored: already dragging"); return; }
            if (_dragIn) { Trace("drag-out ignored: dropping in"); return; }

            _dragOut = true;
            Trace("drag-out begin " + path);
            try
            {
                DataObject data = new DataObject();
                data.SetData(DataFormats.FileDrop, new string[] { path });
                DragDropEffects res = DragDrop.DoDragDrop(_host, data, DragDropEffects.Copy);
                Trace("drag-out end " + res);
                if (res != DragDropEffects.None && Prefs.TransferOut)
                {
                    Transfer.Remove(path);
                    Trace("drag-out auto-remove -> " + Transfer.Count());
                    RefreshNow();
                }
            }
            catch (Exception ex) { Trace("drag-out failed: " + ex.Message); }
            finally { _dragOut = false; }
        }

        /* 行级右键菜单：在资源管理器中显示 / 复制路径 / 移除。
           走和主菜单同一套模板，否则会冒出一个系统默认样式的白菜单。 */
        void OpenTransferMenu(string path)
        {
            if (_menuOpen) { Trace("row menu ignored: menu open"); return; }
            try
            {
                MenuActions a = new MenuActions();
                a.Reveal = delegate { RevealTransferItem(path); };
                a.CopyPath = delegate { CopyTransferPath(path); };
                a.RemoveRow = delegate
                {
                    Transfer.Remove(path);
                    Trace("act transfer remove(menu) -> " + Transfer.Count());
                    RefreshNow();
                };

                ContextMenu m = Menu.BuildRow(Look.ByKey(_lookKey), Art.ByKey(_artKey), a);
                m.PlacementTarget = _host;
                m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

                _menuOpen = true;
                ApplyClickThrough(false);
                m.Closed += delegate
                {
                    _menuOpen = false;
                    ApplyClickThrough(!_hover);
                    Trace("row menu closed");
                };
                m.IsOpen = true;
                Trace("row menu opened " + path);
            }
            catch (Exception ex) { Trace("row menu failed: " + ex.Message); _menuOpen = false; }
        }

        void SetState(string next, bool immediate)
        {
            if (next == _state && !immediate) return;
            /* 层变化是这一版最容易被间接路径改掉的东西（悬停、数据轮询、待办提醒、
               贴合动画）。留一行日志，出问题时能直接看出是谁改的，不用猜。 */
            Trace("state " + _state + " -> " + next + (immediate ? " (immediate)" : "")
                  + (_petProbeLock ? " [probe-locked]" : ""));
            _state = next;

            double tw, th;
            SizeOf(next, out tw, out th);

            _w.Aim(tw);
            _h.Aim(th);
            _morphing = true;
            _morphStartMs = _clock.Elapsed.TotalMilliseconds;
            _lastAboveMs = _morphStartMs;

            if (immediate) { _w.Snap(); _h.Snap(); }
            else StartAnimating();

            Rebuild(immediate);
            Apply();

            /* 切层就是「球该在哪儿」这件事变了：第一层回岛内，其余两层出场。
               紧跟着一起推进，避免出现「岛已经是第二层、球却还在岛内」的一帧。
               immediate 的那几处（启动、拖动结束）直接落位，不做过渡。 */
            if (_petOn) PetLinkTo(PetTargetLink, immediate);
        }

        void StartAnimating()
        {
            if (_animating) return;
            _animating = true;
            _lastT = _clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnFrame;
        }

        void StopAnimating()
        {
            if (!_animating) return;
            _animating = false;
            CompositionTarget.Rendering -= OnFrame;
        }

        bool InBand(Spring s)
        {
            double d = Math.Abs(s.Target - s.From);
            if (d < 0.5) return true;
            return Math.Abs(s.Value - s.Target) <= Math.Max(0.5, d * 0.01);
        }

        void OnFrame(object sender, EventArgs e)
        {
            double now = _clock.Elapsed.TotalSeconds;
            double dt = now - _lastT;
            _lastT = now;
            if (dt < 0) dt = 0;
            if (dt > 0.05) dt = 0.05;

            bool expanding = _state != "compact";
            double k = expanding ? K : K * KCOLLAPSE;
            double c = expanding ? C : C * CCOLLAPSE;

            if (!_w.Settled) _w.Step(dt, k, c);
            if (!_h.Settled) _h.Step(dt, k * 1.06, c * 1.02);

            double nowMs = _clock.Elapsed.TotalMilliseconds;
            if (!InBand(_w) || !InBand(_h)) _lastAboveMs = nowMs;

            if (_w.Settled) { _w.Snap(); }
            if (_h.Settled) { _h.Snap(); }

            UpdateSpecularFromCursor();
            Apply();

            /* 球的动画独立于岛体 spring：岛早就 settled 了，球可能还在飞。
               集成版这里还带上了桌宠 —— 岛和球共用这一次 tick（一套更新循环）。 */
            if (_ballStartMs >= 0) ApplyBallAnim();
            AdvancePet(now);

            if (_w.Settled && _h.Settled && _ballStartMs < 0 && !PetWantsFrames
                && !_petTween)
            {
                if (_morphing)
                {
                    _lastMorphMs = _lastAboveMs - _morphStartMs;
                    if (_lastMorphMs < 1) _lastMorphMs = nowMs - _morphStartMs;
                    _morphing = false;
                }
                StopAnimating();
            }
        }

        /* -------------------------------------------------------------- 绘制 */
        void Apply()
        {
            /* 先定落点：形变过程中岛在窗口里滑，窗口本身不动 */
            SyncPlacement();

            Look look = Look.ByKey(_lookKey);
            Art art = Art.ByKey(_artKey);

            double w = Math.Max(2, IslandW);
            double h = Math.Max(2, IslandH);
            double r = Math.Min(30, h / 2);
            double ix = _ix;
            double iy = _iy;

            _shape = Squircle.Build(w, h, r);

            _host.Width = w;
            _host.Height = h;
            _host.Clip = _shape;
            Canvas.SetLeft(_host, ix);
            Canvas.SetTop(_host, iy);

            _refract.Width = w; _refract.Height = h;
            _wash.Width = w; _wash.Height = h;
            _tint.Width = w; _tint.Height = h;
            _spec.Width = w; _spec.Height = h;
            _glow.Width = w; _glow.Height = h;
            _grain.Width = w; _grain.Height = h;

            _content.Width = w;
            _content.Height = h;

            /* 折射底 */
            _curIx = ix; _curIy = iy; _curW = w; _curH = h;
            UpdateRefract();
            PlaceBall();

            _wash.Fill = Ui.Br(look.RefractWash);
            _wash.Visibility = look.RefractWash.A == 0 ? Visibility.Collapsed : Visibility.Visible;

            _tint.Fill = TintBrush(look);

            RadialGradientBrush sb = new RadialGradientBrush();
            sb.Center = new Point(_mx, _my);
            sb.GradientOrigin = new Point(_mx, _my);
            sb.RadiusX = 0.78;
            sb.RadiusY = 0.78;
            sb.GradientStops.Add(new GradientStop(Look.A("#FFFFFF", 0.20 * look.Spec), 0.0));
            sb.GradientStops.Add(new GradientStop(Look.A("#FFFFFF", 0.0), 0.72));
            _spec.Fill = sb;
            _spec.Visibility = look.Spec <= 0.01 ? Visibility.Collapsed : Visibility.Visible;

            RadialGradientBrush gb = new RadialGradientBrush();
            gb.Center = new Point(0.06, 1.06);
            gb.GradientOrigin = new Point(0.06, 1.06);
            gb.RadiusX = 0.95;
            gb.RadiusY = 0.95;
            gb.GradientStops.Add(new GradientStop(Look.A(HexOf(art.Acc), 0.26), 0.0));
            gb.GradientStops.Add(new GradientStop(Look.A(HexOf(art.Acc), 0.0), 0.66));
            _glow.Fill = gb;

            _grain.Opacity = look.Grain;

            LinearGradientBrush eb = new LinearGradientBrush();
            eb.StartPoint = new Point(0, 0);
            eb.EndPoint = new Point(0.06, 1);
            eb.GradientStops.Add(new GradientStop(Look.A("#FFFFFF", 0.55 * look.Edge), 0.0));
            eb.GradientStops.Add(new GradientStop(Look.A("#FFFFFF", 0.06 * look.Edge), 0.32));
            eb.GradientStops.Add(new GradientStop(Look.A("#000000", 0.0), 0.55));
            eb.GradientStops.Add(new GradientStop(Look.A("#000000", 0.30 * look.Edge), 1.0));
            _edge.Stroke = eb;
            _edge.Data = _shape;

            _shA.Data = _shape; _shB.Data = _shape; _shC.Data = _shape; _shD.Data = _shape;
            Canvas.SetLeft(_shA, ix); Canvas.SetTop(_shA, iy + 2);
            Canvas.SetLeft(_shB, ix); Canvas.SetTop(_shB, iy + 5);
            Canvas.SetLeft(_shC, ix); Canvas.SetTop(_shC, iy + 9);
            Canvas.SetLeft(_shD, ix); Canvas.SetTop(_shD, iy + 6);

            /* 岛体几何变了，桌宠的落点和裁剪也要跟着走 —— 这一句不能少，
               否则贴边/形变时球会停在旧位置上（球在窗口里，窗口本身没动）。 */
            PlacePet();
        }

        /* 压在折射之上的那一层，对应岛体的 RefractWash + 竖向 tint：
           顶上一道高光、中间 wash、底下一层暗边，靠这个把"玻璃珠"的球面感做出来。 */
        Brush WashBrush(Look look)
        {
            LinearGradientBrush b = new LinearGradientBrush();
            b.StartPoint = new Point(0, 0);
            b.EndPoint = new Point(0.14, 1);
            b.GradientStops.Add(new GradientStop(Look.A("#FFFFFF", 0.18 * look.Edge), 0.0));
            b.GradientStops.Add(new GradientStop(
                look.RefractWash.A > 10 ? look.RefractWash : Look.A("#14161D", 0.40), 0.45));
            b.GradientStops.Add(new GradientStop(Look.A("#000000", 0.22), 1.0));
            return b;
        }

        Brush TintBrush(Look look)
        {
            LinearGradientBrush b = new LinearGradientBrush();
            b.StartPoint = new Point(0, 0);
            b.EndPoint = new Point(0.14, 1);
            b.GradientStops.Add(new GradientStop(look.TintTop, 0.0));
            b.GradientStops.Add(new GradientStop(look.TintMid, 0.48));
            b.GradientStops.Add(new GradientStop(look.TintBottom, 1.0));
            return b;
        }

        static string HexOf(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        /* ------------------------------------------------------- 桌面采样 */

        /// <summary>
        /// 第一帧用整屏快照。Show() 之前调用，此时窗口还没上屏，抓到的天然就是
        /// 干净的桌面。实时抓取接手后它不再刷新，只作为启动瞬间的兜底。
        /// </summary>
        void CaptureDesktop()
        {
            int sw = Native.ScreenWidth;
            int sh = Native.ScreenHeight;
            if (sw <= 0 || sh <= 0) return;
            _desktopShot = Native.Capture(0, 0, sw, sh);
            _shotRect = new Rect(0, 0, sw, sh);
        }

        /* --------------------------------------------------- 自遮挡实验
           实时映射的死结是：岛盖住的正是要采的那块像素。WDA_EXCLUDEFROMCAPTURE
           声称能让窗口不出现在抓屏里，但它只能由窗口所属进程设置，所以必须在这里
           实测三种可能：仍是岛、变成桌面、还是拍成黑色。 */
        void RunWdaProbe()
        {
            int x = (int)Math.Round(Left * _dpi);
            int y = (int)Math.Round(Top * _dpi);
            int cw = (int)Math.Round(Width * _dpi);
            int chh = (int)Math.Round(Height * _dpi);
            int ix = (int)Math.Round(_ix * _dpi);
            int iy = (int)Math.Round(_iy * _dpi);
            int iw = (int)Math.Round(_w.Value * _dpi);
            int ih = (int)Math.Round(_h.Value * _dpi);
            int px = ix + iw / 2 - 10;
            int py = iy + ih / 2 - 10;

            double before = PatchLuma(Native.Capture(x, y, cw, chh), cw, chh, px, py, 20, 20);
            double beforeMargin = PatchLuma(Native.Capture(x, y, cw, chh), cw, chh, 2, 2, 10, 10);
            bool ok = Native.ExcludeFromCapture(_hwnd);
            Trace("WDA call returned " + ok);

            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(600);
            t.Tick += delegate
            {
                t.Stop();
                BitmapSource shot = Native.Capture(x, y, cw, chh);
                double after = PatchLuma(shot, cw, chh, px, py, 20, 20);
                double afterMargin = PatchLuma(shot, cw, chh, 2, 2, 10, 10);
                Trace("WDA island centre luma: before=" + Math.Round(before, 1)
                      + " after=" + Math.Round(after, 1)
                      + " | window margin: before=" + Math.Round(beforeMargin, 1)
                      + " after=" + Math.Round(afterMargin, 1));
                Trace("WDA verdict: " + Verdict(before, after, beforeMargin));
                Application.Current.Shutdown();
            };
            t.Start();
        }

        static string Verdict(double before, double after, double margin)
        {
            if (before < 0 || after < 0) return "capture-failed";
            double d = Math.Abs(after - before);
            if (d < 6) return "no-effect (island still captured)";
            if (Math.Abs(after - margin) < 12) return "reveals-what-is-behind";
            if (after < 8) return "captured-as-black";
            return "changed-but-unclear";
        }

        /// <summary>
        /// 冻一帧给抓屏验收看。
        ///
        /// 自排除开着的时候岛不在任何截图里，所以「实时缓冲真的被画到屏上」这最后一跳
        /// 没法从外面量。这里在指定时刻停掉采样、解除自排除，屏幕上留下的是**最后一帧
        /// 实时采样**驱动的画面，于是截图拍到的那张玻璃就是从实时缓冲来的那张。
        /// 停掉采样是为了解除排除的瞬间不会拍到自己形成回授。
        /// </summary>
        void ScheduleFreezeShot()
        {
            DispatcherTimer ft = new DispatcherTimer();
            ft.Interval = TimeSpan.FromMilliseconds(_freezeShot);
            ft.Tick += delegate
            {
                ft.Stop();
                if (_liveTimer != null) _liveTimer.Stop();
                Native.ClearCaptureExclusion(_hwnd);
                Trace("FREEZE sampled=" + _lastSample + " frames=" + _liveFrames);

                DispatcherTimer et = new DispatcherTimer();
                et.Interval = TimeSpan.FromMilliseconds(900);
                et.Tick += delegate
                {
                    et.Stop();
                    Application.Current.Shutdown();
                };
                et.Start();
            };
            ft.Start();
        }

        /// <summary>程序内采样探针：反复读取抓屏缓冲区里「玻璃会采到的那块像素」的平均 RGB。
        ///
        /// 为什么要绕这一道：自排除生效后岛不在任何屏幕截图里，验证脚本没法再量它。
        /// 但这个探针读的是抓屏缓冲区本身，也就是玻璃真正拿去当底的那块桌面，
        /// 与屏幕截图无关，所以它能在开着自排除的情况下直接证明采样是实时的。
        /// </summary>
        void RunSampleProbe()
        {
            Native.Grabber g = null;
            int n = 0;
            DispatcherTimer t = new DispatcherTimer();
            t.Interval = TimeSpan.FromMilliseconds(200);
            t.Tick += delegate
            {
                n++;
                int w = (int)Math.Round(Width * _dpi);
                int hh = (int)Math.Round(Height * _dpi);
                int x = (int)Math.Round(Left * _dpi);
                int y = (int)Math.Round(Top * _dpi);
                if (g == null) g = new Native.Grabber(w, hh);
                if (g.Ok && g.Grab(x, y))
                {
                    int bx = (int)Math.Round((_curIx + _curW / 2 - 20) * _dpi);
                    int by = (int)Math.Round((_curIy + 8) * _dpi);
                    Trace("SAMPLE " + n + " rgb=" + MeanRgb(g, bx, by, 40, 12));
                }
                if (n >= 16)
                {
                    t.Stop();
                    if (g != null) g.Dispose();
                    Application.Current.Shutdown();
                }
            };
            t.Start();
        }

        /// <summary>抓屏缓冲区里一块的平均 RGB（缓冲区自上而下、32 位）。</summary>
        string MeanRgb(Native.Grabber g, int x, int y, int pw, int ph)
        {
            int stride = g.Stride;
            int w = g.Width;
            int h = g.Height;
            byte[] buf = new byte[stride * h];
            System.Runtime.InteropServices.Marshal.Copy(g.Bits, buf, 0, buf.Length);
            double r = 0, gg = 0, b = 0;
            int cnt = 0;
            for (int j = y; j < y + ph; j++)
            {
                if (j < 0 || j >= h) continue;
                for (int i = x; i < x + pw; i++)
                {
                    if (i < 0 || i >= w) continue;
                    int o = j * stride + i * 4;
                    b += buf[o]; gg += buf[o + 1]; r += buf[o + 2];
                    cnt++;
                }
            }
            if (cnt == 0) return "none";
            return Math.Round(r / cnt, 1) + "," + Math.Round(gg / cnt, 1) + "," + Math.Round(b / cnt, 1);
        }

        /// <summary>取一块的平均亮度。位深的字节数按 BitmapSource 自己的格式算，
        /// CreateBitmapSourceFromHBitmap 给的不一定是 Bgra32。</summary>
        static double PatchLuma(BitmapSource src, int w, int h, int px, int py, int pw, int ph)
        {
            if (src == null) return -1;
            int bpp = (src.Format.BitsPerPixel + 7) / 8;
            int stride = w * bpp;
            byte[] buf = new byte[stride * h];
            src.CopyPixels(buf, stride, 0);
            double sum = 0;
            int n = 0;
            for (int j = py; j < py + ph; j++)
            {
                if (j < 0 || j >= h) continue;
                for (int i = px; i < px + pw; i++)
                {
                    if (i < 0 || i >= w) continue;
                    int o = j * stride + i * bpp;
                    sum += 0.114 * buf[o] + 0.587 * buf[o + 1] + 0.299 * buf[o + 2];
                    n++;
                }
            }
            return n == 0 ? -1 : sum / n;
        }

        /// <summary>
        /// 实时映射。只抓窗口矩形，不抓全屏——岛永远在这个矩形里，多抓的部分毫无用处。
        /// 12Hz 对一层 16px 模糊的玻璃绰绰有余，再快只是把 BitBlt 换成白烧 CPU。
        /// </summary>
        void StartLiveBackdrop()
        {
            if (_liveTimer != null) return;

            _liveTimer = new DispatcherTimer(DispatcherPriority.Render);
            /* 两种抓法，间隔不一样：

               禁止捕捉 → 窗口一直排除在外，连续采样，12Hz 对 16px 模糊绰绰有余；
               允许捕捉 → 每次抓之前都要「排除 → 等一帧 → 抓 → 恢复」，
                          12Hz 那样切会把 DWM 合成打得太频繁，录屏里也会明显闪烁。
                          降到 700ms：背景仍然跟着桌面变，只是慢半拍。 */
            _liveTimer.Interval = TimeSpan.FromMilliseconds(_capture ? GlassMaskedMs : 83);
            _liveTimer.Tick += OnLiveTick;
            _liveTimer.Start();
        }

        /* 抓屏亲和性的唯一写入口。

           允许捕捉 → WDA_NONE（岛进得了截图），采样走「每帧临时排除」的慢速档；
           禁止捕捉 → WDA_EXCLUDEFROMCAPTURE（岛消失于所有抓屏），连续采样。

           两种状态都要重开定时器：间隔和抓法不同，不能沿用上一次的。 */
        void ApplyCaptureAffinity()
        {
            if (_hwnd == IntPtr.Zero) return;

            StopLiveBackdrop();

            if (_capture)
            {
                bool ok = Native.ClearCaptureExclusion(_hwnd);
                _excluded = false;
                /* 启动那一刻（OnSourceInit）还没上屏，采样交给 OnLoaded 去开 */
                if (IsLoaded) StartLiveBackdrop();
                Trace("capture allow=true wda=" + ok + " live=masked@" + GlassMaskedMs + "ms");
            }
            else
            {
                _excluded = Native.ExcludeFromCapture(_hwnd);
                if (IsLoaded) StartLiveBackdrop();
                Trace("capture allow=false excluded=" + _excluded + " live=continuous");
            }
        }

        /* 右键菜单那一项。切换后立刻生效，不用重启。
           落盘走 Prefs.Save：它整体重写 ui.txt，所以必须把选项全带上。

           注意 Prefs.Capture 只在这里改，不在 ApplyCaptureAffinity 里改 ——
           否则 --no-wda / --exclude-capture 这类验收参数会把「用户意愿」
           一起改掉，跑一次验收脚本就永久改掉了用户的设置。 */
        void ToggleCapture()
        {
            _capture = !_capture;
            ApplyCaptureAffinity();
            Prefs.Capture = _capture;
            Prefs.Save(_scene, _lookKey, _artKey);
            Trace("toggle capture -> " + _capture);
        }

        void ToggleTransferOut()
        {
            Prefs.TransferOut = !Prefs.TransferOut;
            Prefs.Save(_scene, _lookKey, _artKey);
            Trace("toggle transfer-out -> " + Prefs.TransferOut);
        }

        /* ------------------------------------------------------------ 桌宠

           桌宠现在是岛内集成的：球和岛体在同一棵可视树里，只有一个窗口
           （见 PetIsland.cs）。下面这些动作全部转给那套实现 ——
           PetController / PetWindow 只留给 --pet* 那几个独立窗口的诊断模式用。 */

        /* 兜底入口：资源管理器里 Ctrl+C 复制文件之后，从菜单直接暂存。

           为什么留着它：拖入依赖「穿透窗口 + OLE 命中判定」这条不太常规的路，
           换台机器、换个安全软件都可能不灵。这条路走的是剪贴板，和拖放无关，
           等于给这个功能留了个不会一起坏的入口。 */
        void AddFromClipboard()
        {
            System.Collections.Specialized.StringCollection list = null;
            try
            {
                if (Clipboard.ContainsFileDropList()) list = Clipboard.GetFileDropList();
            }
            catch (Exception ex)
            {
                Trace("clipboard files failed: " + ex.Message);
                return;
            }

            if (list == null || list.Count == 0)
            {
                Trace("clipboard has no files");
                return;
            }

            string[] paths = new string[list.Count];
            list.CopyTo(paths, 0);
            int added = Transfer.Add(paths);
            Trace("clipboard -> transfer added=" + added);
            if (added > 0)
            {
                GoScene("transfer");
                if (_state != "expanded") SetState("expanded", false);
                Rebuild(false);
                Apply();
            }
        }

        /* 改扩展样式会重建窗口合成属性，抓屏亲和性有可能被清掉。
           这里按当前意愿补一次 —— 以前只补「排除」，现在两个方向都要补，
           否则「允许捕捉」会在第一次点击穿透状态变化后悄悄失效。 */
        void ReassertCaptureAffinity()
        {
            if (_hwnd == IntPtr.Zero) return;
            if (_capture) Native.ClearCaptureExclusion(_hwnd);
            else Native.ExcludeFromCapture(_hwnd);
        }

        void OnLiveTick(object sender, EventArgs e)
        {
            Look look = Look.ByKey(_lookKey);
            if (!look.Refract) return;              // 只有玻璃外观才采样背后
            if (!IsVisible) return;

            int w = (int)Math.Round(Width * _dpi);
            int h = (int)Math.Round(Height * _dpi);
            if (w <= 0 || h <= 0) return;
            int x = (int)Math.Round(Left * _dpi);
            int y = (int)Math.Round(Top * _dpi);

            if (_live == null || _live.PixelWidth != w || _live.PixelHeight != h)
            {
                if (_grabber != null) { _grabber.Dispose(); _grabber = null; }
                _grabber = new Native.Grabber(w, h);
                if (!_grabber.Ok)
                {
                    _grabber.Dispose(); _grabber = null;
                    _liveTimer.Stop();
                    Trace("LIVE disabled: grabber alloc failed");
                    return;
                }
                /* Bgr32 而不是 Bgra32：GDI BitBlt 不写 alpha 字节，
                   用带 alpha 的格式整张图会变成全透明。 */
                _live = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgr32, null);
                _shotRect = new Rect(0, 0, w, h);
                _deskBrush.ImageSource = _live;
                _deskBrush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
                Trace("LIVE on " + w + "x" + h);
            }

            /* 允许被捕捉时：抓之前临时把自己排除掉，抓完立刻恢复。

               不这么做，采回来的就是岛自己 —— 玻璃映玻璃，逐帧递归越叠越糊。
               而「一直排除」又会让岛从所有截图/录屏里消失，那是另一个开关的事。

               排除生效到真正反映进合成结果有延迟，所以先 FlushComposition 等一帧；
               否则 BitBlt 拿到的还是上一帧（那一帧里窗口还在）。

               代价说清楚：被排除的这十几毫秒里，别的录屏软件拍不到岛。
               700ms 一次、每次约一帧，录屏里是极轻微的顿挫。 */
            bool masked = _capture && _hwnd != IntPtr.Zero;
            if (masked)
            {
                Native.ExcludeFromCapture(_hwnd);
                Native.FlushComposition();
            }

            try
            {
                if (!_grabber.Grab(x, y)) return;
                _live.WritePixels(new Int32Rect(0, 0, w, h),
                                  _grabber.Bits, _grabber.Stride * h, _grabber.Stride);
                _liveFrames++;

                if (_freezeShot > 0)
                {
                    int bx = (int)Math.Round((_curIx + _curW / 2 - 20) * _dpi);
                    int by = (int)Math.Round((_curIy + 8) * _dpi);
                    _lastSample = MeanRgb(_grabber, bx, by, 40, 12);
                }
            }
            finally
            {
                /* 必须恢复：漏了这一步，岛就永久进不了截图，正好和这个开关相反。
                   上面有 return，所以只能用 finally。 */
                if (masked) Native.ClearCaptureExclusion(_hwnd);
            }

            /* 窗口一动、岛一变形，采样窗口就得跟着换。每帧一次 Rect 赋值，
               不做脏检查——比判断「要不要更新」更快。 */
            UpdateRefract();
        }

        void UpdateRefract()
        {
            Look look = Look.ByKey(_lookKey);
            if (look.Refract && _deskBrush.ImageSource != null)
            {
                _refract.Visibility = Visibility.Visible;
                _deskBrush.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
                _deskBrush.Viewbox = DeskViewbox(_curIx, _curIy, _curW, _curH);
            }
            else
            {
                _refract.Visibility = Visibility.Collapsed;
            }
        }

        void StopLiveBackdrop()
        {
            if (_liveTimer != null) { _liveTimer.Stop(); _liveTimer = null; }
            if (_grabber != null) { _grabber.Dispose(); _grabber = null; }
            _live = null;
        }

        /// <summary>
        /// 把岛在屏幕上的绝对物理矩形，换算成抓屏图里的归一化 Viewbox。
        /// 图可能是整屏（启动快照），也可能只是窗口矩形（实时），靠 _shotRect 区分。
        /// </summary>
        Rect DeskViewbox(double ix, double iy, double w, double h)
        {
            double sw = Math.Max(1, _shotRect.Width);
            double sh = Math.Max(1, _shotRect.Height);
            double x = (Left + ix) * _dpi - _shotRect.X;
            double y = (Top + iy) * _dpi - _shotRect.Y;
            double ww = w * _dpi;
            double hh = h * _dpi;

            double rx = Clamp01(x / sw);
            double ry = Clamp01(y / sh);
            double rw = Clamp01(ww / sw);
            double rh = Clamp01(hh / sh);
            if (rx + rw > 1) rw = 1 - rx;
            if (ry + rh > 1) rh = 1 - ry;
            return new Rect(rx, ry, Math.Max(0.001, rw), Math.Max(0.001, rh));
        }

        static double Clamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        /* -------------------------------------------------------------- 内容 */
        void Rebuild(bool immediate)
        {
            Look look = Look.ByKey(_lookKey);
            Art art = Art.ByKey(_artKey);

            SceneDef def = Scenes.Get(_scene);
            double tw, th;
            SizeOf(_state, out tw, out th);

            _content.Children.Clear();
            /* petDocked：第一层那颗原球不画，位置让给桌宠。
               只在「桌宠开着 + 正好是 idle」时留白 —— 其余情况原球照旧，
               这样关掉桌宠、或者还停在第二/三层时，界面和以前逐像素一致。 */
            bool petDocked = _petOn && _state == "idle";
            Built built = Layout.Build(_scene, _state, tw, th, look, art, _snap, Actions(), !_pinnedState, petDocked);
            _content.Children.Add(built.Root);
            _built = built;

            if (immediate)
            {
                _content.Opacity = 1;
            }
            else
            {
                _content.Opacity = 0;
                DoubleAnimation fade = new DoubleAnimation(1,
                    new Duration(TimeSpan.FromMilliseconds(240)));
                fade.BeginTime = TimeSpan.FromMilliseconds(55);
                fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                _content.BeginAnimation(UIElement.OpacityProperty, fade);

                TranslateTransform tt = new TranslateTransform(0, 6);
                _content.RenderTransform = tt;
                DoubleAnimation rise = new DoubleAnimation(0,
                    new Duration(TimeSpan.FromMilliseconds(300)));
                rise.BeginTime = TimeSpan.FromMilliseconds(55);
                rise.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
                tt.BeginAnimation(TranslateTransform.YProperty, rise);
            }
        }

        /* ---------------------------------------------------- 穿透与悬停 */
        void Trace(string s)
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            try
            {
                File.AppendAllText(_logPath,
                    "[" + _clock.ElapsedMilliseconds.ToString() + "ms] " + s + "\r\n");
            }
            catch { }
        }

        /* 命中判定的全部输入项，用来把「点不到」定位到具体哪一项不对 */
        string HitInfo()
        {
            Point screen;
            if (!Native.TryGetCursor(out screen)) return "hit=nocursor";
            double lx = screen.X / _dpi - (Left + IslandLeft);
            double ly = screen.Y / _dpi - (Top + IslandTop);
            double w = IslandW, h = IslandH;
            bool inside = lx >= -2 && ly >= -2 && lx <= w + 2 && ly <= h + 2
                          && Squircle.Contains(new Point(lx, ly), w + 4, h + 4, Math.Min(30, h / 2));
            return "cur=" + screen.X + "," + screen.Y
                   + " local=" + Math.Round(lx, 1) + "," + Math.Round(ly, 1)
                   + " origin=" + Math.Round(Left + IslandLeft, 1) + "," + Math.Round(Top + IslandTop, 1)
                   + " isl=" + Math.Round(w, 1) + "x" + Math.Round(h, 1)
                   + " hover=" + _hover + " ign=" + _ignoring + " inside=" + inside;
        }

        /* ------------------------------------------------------------ 消息小球 */

        void PlaceBall()
        {
            if (_ball == null) return;
            /* 容器钉在球的静止位置；球在里面用 RenderTransform 滑出。
               -NotifyGap 那条裁剪边正好落在岛体右边缘上。 */
            Canvas.SetLeft(_ballDock, _curIx + _curW + NotifyGap);
            Canvas.SetTop(_ballDock, _curIy + (_curH - NotifyBall) / 2);
        }

        void ShowBall(Notify.App app)
        {
            if (_ball == null) return;
            _ballApp = app;
            _ballOn = true;
            _ballUntil = DateTime.UtcNow.AddMilliseconds(NotifyBallMs);

            /* 微信绿 / QQ 蓝。球上写字而不是画图标：外挂图标要额外的文件，
               而这里只要用户一眼分清是哪个应用。 */
            /* 分裂阶段用岛体自己的材质，这样它出来的时候跟岛是一个东西，
               不会有"贴了块色块"的割裂感。

               关键：玻璃 look 的 tint 几乎是透明的，岛之所以看着像玻璃是因为底下
               有**真实桌面折射**（_refract 采样 _deskBrush）。球要是只套 tint，
               在玻璃外观下基本看不见。所以这里给球也接同一张桌面图，只是 viewbox
               取到球所在的那一小块 —— 「材质跟场景同步」就是字面意思。 */
            Look lk = Look.ByKey(_lookKey);
            _ballRefract = null;
            if (lk.Refract && _deskBrush != null && _deskBrush.ImageSource != null)
            {
                _ballRefract = new ImageBrush(_deskBrush.ImageSource);
                _ballRefract.ViewboxUnits = BrushMappingMode.RelativeToBoundingBox;
                _ballRefract.Stretch = Stretch.Fill;
                _ballMat = _ballRefract;
            }
            else
            {
                /* 兜底必须可见：玻璃 look 的 TintMid 是白色 4% alpha，直接拿来当背景
                   等于没有背景，分裂阶段会凭空看不见球。 */
                Color fb = lk.TintMid;
                if (lk.Refract || fb.A < 120) fb = Color.FromArgb(210, 0x16, 0x18, 0x20);
                _ballMat = new SolidColorBrush(fb);
            }
            _ball.Background = _ballMat;
            _ballWash.Background = WashBrush(lk);


            /* 颜色层先藏起来，等完全断开之后再揭示 */
            Color c = app == Notify.App.WeChat
                ? Color.FromRgb(0x07, 0xC1, 0x60)
                : Color.FromRgb(0x12, 0xB7, 0xF5);
            _ballBrush = new SolidColorBrush(c);
            /* 只描环，不填底：底色一填就变回实心球了（甜甜圈的圆心必须是空的）。 */
            _ballColor.Background = null;
            _ballColor.Opacity = 0;
            _ballCore.Fill = _ballBrush;

            PlaceBall();
            _ball.Visibility = Visibility.Visible;

            /* 细胞分裂：从岛体边缘鼓出来 → 拉长变扁 → 飞出去 → 回弹成圆。
               起点由 ApplyBallAnim 自己算，这里只记开始时刻。 */
            _ballStartMs = _clock.Elapsed.TotalMilliseconds;
            ApplyBallAnim();
            StartAnimating();

            Trace("ball show " + app + " until " + _ballUntil.ToString("HH:mm:ss"));
        }

        /* 从岛体后面位移出来。

           球在 _root 里排在 _host **之前**（见构造里那个 Insert），所以它还没滑出来
           的时候整颗被岛体挡住，看上去就是从岛后面钻出来 —— 不需要裁剪、不需要遮罩、
           也不需要任何形状变形。上一版做"水滴分裂/脖颈"被否掉了，理由是突兀；
           这一版只做一件事：平移。

           分段：
             0.00–0.62 滑出：translateX 从 -(NotifyGap + NotifyBall) 走到 0。
                        那个起点正好让球的右边缘贴着岛体右边缘，即完全藏住。
             0.62–0.86 揭示：颜色层淡入（滑出阶段是岛体的折射材质）
             0.86–1.00 收尾：满色 */

        /* 裁剪窗口左边缘相对岛体右边缘的偏移。dock 左上角钉在
           "岛体右边缘 + NotifyGap"，裁剪从 dock 局部的 -NotifyGap 开始，
           两者相加应当正好是 0 —— 也就是裁剪边压在岛体右边缘上。
           返回 999 表示裁剪根本没设（球就会画到岛上）。 */
        double ClipEdgeOffset()
        {
            if (_ballDock == null || _ballDock.Clip == null) return 999;
            RectangleGeometry rg = _ballDock.Clip as RectangleGeometry;
            if (rg == null) return 998;
            return rg.Rect.Left + NotifyGap;
        }
        void ApplyBallAnim()
        {
            if (!_ballOn || _ball == null || _ballStartMs < 0) return;

            double t = (_clock.Elapsed.TotalMilliseconds - _ballStartMs) / BallAnimMs;
            if (t < 0) t = 0;
            bool done = t >= 1;
            if (done) t = 1;

            /* 完全藏在岛后的偏移：朝左挪一个自己宽 + 那道缝隙 */
            double hidden = -(NotifyGap + NotifyBall);

            double travel;
            if (t < 0.62)
            {
                double a = t / 0.62;
                /* easeOutCubic：起步就动、收尾轻，像被推出来，不是弹出来 */
                double u = 1 - a;
                travel = 1 - u * u * u;
            }
            else travel = 1;

            _ballMove.X = hidden * (1 - travel);
            _ballMove.Y = 0;

            /* 由小到大 + 从岛后滑出。

               缩放绕球心（RenderTransformOrigin 0.5,0.5），所以小球是"从一点长大"，
               不是从一个角撑开。裁剪窗口在 dock 上、不跟着缩放走，所以球再小也不会
               露出岛的边界 —— 反过来球长大后左缘越过岛边时也会被裁掉。

               缓动：easeOutCubic 撑起主要生长，再叠一个 sin 的轻微过冲，让它在
               快到尺寸时"胖一下"再收回来，比匀速长大有生气。 */
            double growth;
            if (t < 0.62)
            {
                double a = t / 0.62;
                double u = 1 - a;
                growth = 1 - u * u * u;
            }
            else growth = 1;

            double scale = BallFromScale + (1 - BallFromScale) * growth
                           + 0.05 * Math.Sin(Math.PI * Math.Min(1, t / 0.62)) * growth;

            _ballScale.ScaleX = scale;
            _ballScale.ScaleY = scale;
            _ball.Opacity = 1;

            /* 折射跟着球走：每帧把 viewbox 挪到球**实际覆盖**的那块桌面。
               球缩小的时候可视区域也小，尺寸必须乘 scale，否则会采样一大块、
               缩下去之后看起来像贴图没对齐。 */
            if (_ballRefract != null)
            {
                double R = NotifyBall / 2;
                double vis = NotifyBall * scale;
                _ballRefract.Viewbox = DeskViewbox(
                    Canvas.GetLeft(_ballDock) + _ballMove.X + R - vis / 2,
                    Canvas.GetTop(_ballDock) + R - vis / 2,
                    vis, vis);
            }

            /* 揭示：滑出完成之后再上色。既是上一轮"完全出来才显示是哪个应用"的要求，
               也避免球还在岛体后面时就开始变色（那会从岛边缘漏出一丝绿）。 */
            double reveal;
            if (t < 0.62) reveal = 0;
            else if (t < 0.86)
            {
                double a = (t - 0.62) / 0.24;
                reveal = a * a * (3 - 2 * a);
            }
            else reveal = 1;

            _ballColor.Opacity = reveal;

            /* 没有任何边框要跟着亮 —— 球的边界由材质自己表现。 */

            /* 逐帧留痕，给 verify\notify-anim.ps1 断言用：
               dx 是相对静止位置的偏移（负数 = 还藏在岛后），reveal 是颜色层透明度。 */
            /* below：球在 _root 里的层级是否真的低于岛体（1 = 在下面）
               visLeft：球可见区域的左边缘相对岛体右边缘的偏移 —— 必须 >= 0，
                        负数就意味着球能画到岛上，用户会看到"球跑到岛上面"。 */
            if (_notifyTrace && (_ballTraceTicks++ % 4) == 0)
            {
                Trace("BALLTRACE t=" + t.ToString("0.###")
                      + " dx=" + _ballMove.X.ToString("0.##")
                      + " sx=" + _ballScale.ScaleX.ToString("0.###")
                      + " sy=" + _ballScale.ScaleY.ToString("0.###") + " sc=" + _ballScale.ScaleX.ToString("0.###")
                      + " reveal=" + _ballColor.Opacity.ToString("0.###")
                      + " below=" + (_root.Children.IndexOf(_ballDock) < _root.Children.IndexOf(_host) ? 1 : 0)
                      + " rawLeft=" + (_ballMove.X + NotifyGap).ToString("0.##")
                      + " clipL=" + ClipEdgeOffset().ToString("0.##")
                      + " bw=" + _ball.BorderThickness.Left.ToString("0.##")
                      + " coreIn=" + _ballCore.Margin.Left.ToString("0.##"));
            }

            if (done) _ballStartMs = -1;
        }
        void HideBall(string why)
        {
            if (!_ballOn) return;
            _ballOn = false;
            _ballStartMs = -1;
            if (_ballColor != null) _ballColor.Opacity = 0;
            if (_ball != null)
            {
                _ball.Visibility = Visibility.Collapsed;
                _ball.Opacity = 1;
                if (_ballScale != null) { _ballScale.ScaleX = 1; _ballScale.ScaleY = 1; }
                if (_ballMove != null) { _ballMove.X = 0; _ballMove.Y = 0; }
            }
            Trace("ball hide (" + why + ")");
        }

        /* 光标是否落在球上。窗口是穿透的，WPF 的命中测试不生效，只能自己算。 */
        bool InBall(double dipX, double dipY)
        {
            if (!_ballOn || _ball == null) return false;
            double bx = Left + IslandLeft + IslandW + NotifyGap;
            double by = Top + IslandTop + (IslandH - NotifyBall) / 2;
            return dipX >= bx && dipX <= bx + NotifyBall
                && dipY >= by && dipY <= by + NotifyBall;
        }

        void OnHitTick(object sender, EventArgs e)
        {
            /* 先兜底：物理左键已经松开但我们没收到 MouseUp（在窗外松手、
               弹了 UAC、切了桌面都会这样）。不检查的话岛会一直粘着光标。 */
            if (_dragging && !Native.LeftButtonDown())
            {
                EndDrag("drag release missed (button already up)");
            }

            /* 菜单开着或正在拖动时不要让悬停改变形态 */
            if (_menuOpen || _dragging || _dragOut) return;

            /* 截图框选期间整屏都是遮罩窗口，这时候的鼠标位置毫无意义，
               动形态只会白白重建卡片（还会把岛从遮罩底下顶出来）。 */
            if (Shot.Busy) return;

            Point screen;
            if (!Native.TryGetCursor(out screen))
            {
                Trace("poll: GetCursorPos failed");
                return;
            }

            double dipX = screen.X / _dpi;
            double dipY = screen.Y / _dpi;

            double w = IslandW, h = IslandH;
            double r = Math.Min(30, h / 2);
            double localX = dipX - (Left + IslandLeft);
            double localY = dipY - (Top + IslandTop);

            /* 放宽 2px，避免边缘抖动导致反复开关。
               第一层只有 52×30，对鼠标来说太小，额外给 6px 的透明容差——
               视觉不变，但不用像素级瞄准也能悬停上去。 */
            double slack = w < 90 ? 6 : 2;
            bool inside = localX >= -slack && localY >= -slack
                          && localX <= w + slack && localY <= h + slack
                          && Squircle.Contains(new Point(localX, localY),
                                               w + slack * 2, h + slack * 2, r);

            /* 外部拖放武装。

               判据只用三条：左键按下 + 光标在岛体附近 + 不是自己在拖。
               为什么不用 GetCapture 判断「别的窗口抓着鼠标」——它是**线程局部**的，
               拿不到其他进程的 capture，跨进程那个信号根本不存在。

               为什么判定区是「岛体 + 14px」而不是窗口矩形：窗口恒等于展开尺寸
               （Width = ExpW + 2MX），收起态时那是一个 516×318 的大区域，
               拿它当判定区会把所有路过的拖放全吞掉。

               为什么现在不担心误伤：别人拖动时对方窗口持有 capture，
               鼠标消息本来就不走命中判定，所以我们关掉穿透对它毫无影响。 */
            bool foreignDrag = Native.LeftButtonDown() && !_dragging && !_dragIn;
            bool nearBody = localX >= -14 && localY >= -14
                            && localX <= w + 14 && localY <= h + 14;
            _armOn = foreignDrag && nearBody;

            if (inside != _hover)
            {
                _hover = inside;
                Trace("hover -> " + inside + "  cursor=" + screen.X + "," + screen.Y
                      + " local=" + Math.Round(localX, 1) + "," + Math.Round(localY, 1)
                      + " island=" + Math.Round(w, 1) + "x" + Math.Round(h, 1)
                      + " win=" + Math.Round(Left, 1) + "," + Math.Round(Top, 1)
                      + " dpi=" + _dpi.ToString("0.###"));
                /* 探针期间层状态由探针自己走：鼠标恰好停在岛上时，悬停会把好不容易
                   摆好的第二层又展开掉，量出来的就不是探针要的那一层了。
                   这里只挡「悬停改层」，穿透/命中/投放一律照旧。 */
                if (_petProbeLock) return;

                if (inside) { if (_state == "compact" || _state == "idle") SetState("expanded", false); }
                else { if (_state == "expanded") Reconcile(); }
            }

            /* 穿透统一在这里收口：悬停、武装、正在投放 —— 任一成立就不能穿透。

               以前这行写在上面那个 if 里面，只有悬停变化才重算；武装是个新的
               触发源，必须每 tick 都算一遍，否则放下鼠标那一刻会漏。

               但只在**值真的变了**的时候才调下去：ApplyClickThrough 内部对
               「值没变」会打一行 dedup 日志，每 tick 调一次就是每秒 60 行日志，
               把有用的信息全冲掉。 */
            /* 球到点自己消失 */
            if (_ballOn && DateTime.UtcNow > _ballUntil) HideBall("timeout");

            /* 球在胶囊外面，所以它不在 inside 里。不单独算一笔的话，
               穿透会让它永远点不到（这是加宽窗口之后必须跟着改的一处）。 */
            bool onBall = InBall(dipX, dipY);

            bool wantIgnore = !(inside || _armOn || _dragIn || onBall);
            if (wantIgnore != _ignoring) ApplyClickThrough(wantIgnore);
        }

        void ApplyClickThrough(bool ignore)
        {
            if (_hwnd == IntPtr.Zero) { Trace("style skipped: hwnd null"); return; }
            if (ignore == _ignoring) { Trace("style dedup-skipped ignore=" + ignore); return; }
            _ignoring = ignore;

            IntPtr style = Native.GetExStyle(_hwnd);
            long v = style.ToInt64();
            if (ignore) v |= Native.WS_EX_TRANSPARENT;
            else v &= ~(long)Native.WS_EX_TRANSPARENT;
            Native.SetExStyle(_hwnd, new IntPtr(v));

            /* 改扩展样式会重建窗口的合成属性，抓屏亲和性有可能被清掉。
               这里补一次，只在样式真的切换时发生，不是每帧。 */
            ReassertCaptureAffinity();

            long after = Native.GetExStyle(_hwnd).ToInt64();
            Trace("style write ignore=" + ignore + " -> 0x" + after.ToString("X"));
        }

        /* 不看去重缓存，按 _ignoring 当前值强行写一遍。

           换场景会改窗口尺寸。分层窗口（AllowsTransparency）改尺寸时合成属性会被
           重建，之前写进去的 WS_EX_TRANSPARENT 有可能被系统清掉；而 _ignoring 是
           去重缓存，它还以为样式是原来的样子，于是再也不会重写 —— 岛就会卡在
           「看着正常、点不进去」的状态。换完尺寸强制重写一次最稳。 */
        void ForceClickThroughStyle()
        {
            if (_hwnd == IntPtr.Zero) return;

            IntPtr style = Native.GetExStyle(_hwnd);
            long v = style.ToInt64();
            if (_ignoring) v |= Native.WS_EX_TRANSPARENT;
            else v &= ~(long)Native.WS_EX_TRANSPARENT;
            Native.SetExStyle(_hwnd, new IntPtr(v));
            ReassertCaptureAffinity();

            Trace("style forced ignore=" + _ignoring + " -> 0x"
                  + Native.GetExStyle(_hwnd).ToInt64().ToString("X"));
        }

        /* ------------------------------------------------------------ 交互 */

        /* 左键按住岛体拖动窗口。双击仍然是换外观。
           用真实光标坐标算位移，而不是 e.GetPosition —— 窗口自己在动，
           相对坐标会跟着抖，只有屏幕坐标是稳的。 */
        void OnPreviewDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            {
                /* 点卡片里的按钮时不要起拖拽 */
                if (IsActionHit(e.OriginalSource))
                {
                    Trace("press on action");
                    return;
                }
                Trace("press not action -> drag  src="
                      + (e.OriginalSource == null ? "null" : e.OriginalSource.GetType().Name));
                OnLeftDown(sender, e);
            }
            else if (e.ChangedButton == System.Windows.Input.MouseButton.Right) OnRightDown(sender, e);
        }

        void OnPreviewUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ChangedButton == System.Windows.Input.MouseButton.Left) OnLeftUp(sender, e);
        }

        void OnLeftDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            /* 双击曾经是「换外观」的隐藏手势，现在删掉了。
               原因：卡片变大以后，按钮下面全是岛体，双击按钮时稍偏一点就落到岛体上，
               整张卡当场换色，看着像「切换了场景」。外观现在只走右键菜单 → 外观，
               不再有会在误触时改变界面的隐藏手势。 */
            Point scr;
            if (!Native.TryGetCursor(out scr)) return;

            /* 点球：唤起对应应用，然后球自己消失。
               必须在拖拽逻辑之前拦下来 —— 球在胶囊外面，落到下面会变成
               「拖着岛走」，一按球整个岛就跟着鼠标跑。 */
            if (InBall(scr.X / _dpi, scr.Y / _dpi))
            {
                Notify.App app = _ballApp;
                HideBall("clicked");
                Notify.Activate(app);
                e.Handled = true;
                return;
            }

            _dragging = true;
            _dragMoved = false;
            _dragSawMove = false;
            _dragCursorStart = new Point(scr.X / _dpi, scr.Y / _dpi);
            /* 拖的是锚点，不是窗口左上角：贴边时窗口会被压在屏幕内不动，
               拿窗口起点算位移会让岛在边上「粘住」。 */
            _dragAnchorX = _anchorX;
            _dragAnchorTop = _anchorTop;
            _host.CaptureMouse();
            Trace("left down click=" + e.ClickCount + " cursor=" + scr.X + "," + scr.Y
                  + " at=" + Math.Round(Left, 1) + "," + Math.Round(Top, 1)
                  + " captured=" + IsMouseCaptured);
            e.Handled = true;
        }

        void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!_dragging) return;

            Point scr;
            if (!Native.TryGetCursor(out scr)) return;

            double dx = scr.X / _dpi - _dragCursorStart.X;
            double dy = scr.Y / _dpi - _dragCursorStart.Y;

            if (!_dragSawMove)
            {
                _dragSawMove = true;
                Trace("first move d=" + Math.Round(dx, 1) + "," + Math.Round(dy, 1));
            }

            /* 4px 死区：手抖不算拖动，也不影响单击 */
            if (!_dragMoved)
            {
                if (Math.Abs(dx) < 4 && Math.Abs(dy) < 4) return;
                _dragMoved = true;
                Trace("drag start");
            }

            MoveAnchorTo(_dragAnchorX + dx, _dragAnchorTop + dy);
            e.Handled = true;
        }

        void OnLeftUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            Point up;
            Native.TryGetCursor(out up);
            EndDrag("left up cursor=" + (int)up.X + "," + (int)up.Y);
            e.Handled = true;
        }

        /* 拖动的唯一收尾出口。正常松手和「发现其实早松了」都走这里。 */
        void EndDrag(string why)
        {
            if (!_dragging) return;
            _dragging = false;
            /* 捕获是 _host 抓的，就必须让 _host 放。
               原来这里写的是 ReleaseMouseCapture()（Window 自己的方法），
               调用方不是捕获方，等于什么都没做。 */
            if (_host != null) _host.ReleaseMouseCapture();
            Trace(why + " moved=" + _dragMoved);

            if (_dragMoved)
            {
                /* 第三层拖到贴墙就吸附：松手之后收起，第一层直接落在边上 */
                SnapAnchorToEdge();

                _placed = true;
                SavePosition();
                Trace("drag end " + (int)Math.Round(Left) + "," + (int)Math.Round(Top)
                      + " anchor=" + Math.Round(_anchorX, 1) + "," + Math.Round(_anchorTop, 1));
            }
        }

        void OnRightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            /* 中转站的行有自己的右键菜单（资源管理器/复制路径/移除）。
               这里是窗口的 Preview（隧道）处理，比行的冒泡处理先跑 ——
               不在这里让路，表现就是「行菜单永远弹不出来，只会弹主菜单」。
               所以让开并不设 Handled，把事件留给行自己去处理。 */
            if (InTransferRow(e.OriginalSource))
            {
                Trace("right down on transfer row -> row menu");
                return;
            }

            e.Handled = true;
            Trace("right down -> open menu");
            OpenMenu();
        }

        /* 往祖先里找中转站行的标记（Layout 给行设了 Uid="trow"） */
        static bool InTransferRow(object src)
        {
            DependencyObject d = src as DependencyObject;
            for (int i = 0; d != null && i < 8; i++)
            {
                FrameworkElement fe = d as FrameworkElement;
                if (fe != null && fe.Uid == "trow") return true;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        void OpenMenu()
        {
            if (_menuOpen) { Trace("menu already open"); return; }

            try
            {
                MenuActions a = new MenuActions();
                a.Scene = GoScene;
                a.Look = GoLook;
                a.Art = GoArt;
                a.State = GoState;
                a.ResetPosition = ResetPosition;
                a.Shot = delegate { Shot.Begin(); };
                a.ToggleCapture = ToggleCapture;
                a.ToggleTransferOut = ToggleTransferOut;
                a.AddFromClipboard = AddFromClipboard;
                a.Quit = BeginShutdown;
                a.TogglePet = TogglePet;
                a.PetPoke = PetPoke;
                a.PetSpin = PetSpin;
                a.PetRandomIdle = PetRandomIdle;
                a.PetState = PetSetState;
                a.PetClip = PetPlayClip;

                /* 勾选态和动作必须用同一时刻的状态。PetStateKey() 会把 _petOn
                   跟 PetController.IsOn 对齐（它是有副作用的），所以必须在
                   Menu.Build 之前调用一次、把结果存下来 —— 直接写成
                   Build(..., _petOn, PetStateKey()) 的话，C# 从左到右求值，
                   _petOn 是**刷新前**的旧值：桌宠已经不在而 _petOn 还是 true 时，
                   菜单会勾着「启用桌宠」，点下去却走「关掉」。 */
                string petState = PetStateKey();

                ContextMenu m = Menu.Build(Look.ByKey(_lookKey), Art.ByKey(_artKey), a,
                                           _scene, _lookKey, _artKey, _state, _capture,
                                           Prefs.TransferOut, _petOn, petState);
                m.PlacementTarget = _host;
                m.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

                _menuOpen = true;
                ApplyClickThrough(false);
                m.Closed += delegate
                {
                    _menuOpen = false;
                    Trace("menu closed");
                };
                m.IsOpen = true;
                /* 留一份引用给验收用（notes\probes\probe-pet-unify.cs 走这条路
                   点到真实菜单项，而不是反射去调私有方法 —— 测的必须是
                   「用户点下去会发生什么」，不是「那个方法自己能跑」）。
                   只为可验证性存在，不参与任何运行时逻辑。 */
                _lastMenu = m;
                Trace("menu opened scene=" + _scene + " look=" + _lookKey + " state=" + _state
                      + " items=" + m.Items.Count + " pet=" + _petOn);
            }
            catch (Exception ex)
            {
                _menuOpen = false;
                Trace("MENU FAILED: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        void ResetPosition()
        {
            try { System.IO.File.Delete(PositionPath()); } catch { }
            _placed = false;
            _sized = false;
            SyncWindowSize();
            Trace("position reset");
        }

        /* 滚轮 = 切场景。

           这是岛上最快的那个手势，以前它绑在「换配色」上 —— 一个一辈子换两次的
           装饰操作，占着最高频的入口，而真正高频的场景切换却埋在右键菜单里要走四步。
           现在把这两件事换过来，配色走右键菜单。

           两个必须处理的细节：
           1) 触控板一次滑动会连发几十个小 delta，不累加会一下跳过两三个场景；
           2) 翻页之后要短暂锁一下，否则惯性滑行会继续把后面的场景全翻过去。 */
        void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            e.Handled = true;

            int now = Environment.TickCount;
            if (now < _wheelLockMs) return;

            _wheelAcc += e.Delta;
            if (Math.Abs(_wheelAcc) < 120) return;

            /* 向上 = 上一个，向下 = 下一个 */
            int dir = _wheelAcc > 0 ? -1 : 1;
            _wheelAcc = 0;
            _wheelLockMs = now + 220;
            StepScene(dir);
        }

        /* 在场景表里走一格，首尾循环。4 个场景循环比在边界卡住顺手。 */
        void StepScene(int dir)
        {
            int n = Scenes.All.Length;
            if (n <= 0) return;
            int i = Scenes.IndexOf(_scene);
            if (i < 0) i = 0;
            int j = ((i + dir) % n + n) % n;      /* C# 的 % 会给负数，补一遍 */
            GoScene(Scenes.All[j].Key);
        }

        /* ---------------------------------------------------- 真实数据 */

        Snap _snap = new Snap();
        Built _built;
        CardActions _acts;
        DispatcherTimer _dataTimer;
        int _lastTimerMs = -1;

        /* 滚轮切场景：累计 delta + 翻页后短暂锁定，避免触控板惯性连翻 */
        int _wheelAcc;
        int _wheelLockMs;

        CardActions Actions()
        {
            if (_acts != null) return _acts;

            _acts = new CardActions();
            _acts.PlayPause = delegate
            {
                Media.RequestPlayPause();
                Trace("act play-pause queued");
                /* 乐观更新：等下一轮采样回来才变图标会有 400ms 迟滞，手感很差 */
                NowPlaying cur = Media.Snapshot();
                if (cur != null && cur.Has)
                {
                    NowPlaying flip = CloneWithStatus(cur, cur.Playing ? "Paused" : "Playing");
                    _snap.Media = flip;
                    _playing = flip.Playing;
                }
                RefreshNow();
            };
            _acts.Next = delegate
            {
                Media.RequestNext();
                Trace("act next queued");
            };
            _acts.Prev = delegate
            {
                Media.RequestPrev();
                Trace("act prev queued");
            };
            /* 底部指示点用：直接跳到某个场景 */
            _acts.Scene = delegate(string key) { GoScene(key); };

            _acts.TimerToggle = delegate
            {
                _snap.TimerRunning = !_snap.TimerRunning;
                Trace("act timer " + (_snap.TimerRunning ? "run" : "pause")
                      + " left=" + Math.Round(_snap.TimerLeft));
                RefreshNow();
            };
            _acts.TimerReset = delegate
            {
                _snap.TimerLeft = _snap.TimerTotal;
                _snap.TimerRunning = false;
                Trace("act timer reset");
                RefreshNow();
            };
            _acts.TimerPlus = delegate
            {
                _snap.TimerLeft += 60;
                if (_snap.TimerLeft > _snap.TimerTotal) _snap.TimerTotal = _snap.TimerLeft;
                Trace("act timer +60 left=" + Math.Round(_snap.TimerLeft));
                RefreshNow();
            };
            _acts.CopyClip = delegate(int i)
            {
                Clips.CopyAt(i);
                Trace("act copy clip " + i);
            };
            _acts.ClearClips = delegate
            {
                Clips.Clear();
                Trace("act clip clear");
                RefreshNow();
            };
            _acts.ClearTransfer = delegate
            {
                Transfer.Clear();
                Trace("act transfer clear -> " + Transfer.Count());
                RefreshNow();
            };
            _acts.ClearTransferMissing = delegate
            {
                int n = Transfer.ClearMissing();
                Trace("act transfer clear-missing -> " + n + " left=" + Transfer.Count());
                RefreshNow();
            };
            _acts.TransferRemove = delegate(string path)
            {
                Transfer.Remove(path);
                Trace("act transfer remove " + path + " -> " + Transfer.Count());
                RefreshNow();
            };
            _acts.TransferOpen = delegate(string path) { OpenTransferItem(path); };
            _acts.TransferDragOut = delegate(string path) { BeginTransferDrag(path); };
            _acts.TransferMenu = delegate(string path) { OpenTransferMenu(path); };
            _acts.ToggleTodo = delegate(int i)
            {
                Todos.Toggle(i);
                Trace("act todo toggle " + i + " -> " + Todos.CountText());
                RefreshNow();
            };
            _acts.AddTodo = delegate
            {
                /* 岛自己没有键盘焦点，所以要开一个能聚焦的小窗。
                   位置贴在岛的正下方，视觉上像是岛长出来的一块。 */
                Rect screen = IslandScreenRect();
                TodoInput.Open(this, screen, Look.ByKey(_lookKey), Art.ByKey(_artKey),
                    delegate(string text, int dueMinutes)
                    {
                        Todos.Add(text, dueMinutes);
                        Trace("act todo add " + text.Length + " chars due=" + dueMinutes
                              + "min -> " + Todos.CountText());
                        RefreshNow();
                    });
                Trace("act todo input opened");
            };
            _acts.ClearDoneTodos = delegate
            {
                Todos.ClearDone();
                Trace("act todo clear done -> " + Todos.CountText());
                RefreshNow();
            };
            return _acts;
        }

        /* 让 Dispatcher 跑一会儿：Popup 打开、项目生成都是队列里的活儿，
           不同步泵一下看到的是旧状态。 */
        static void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end)
            {
                DispatcherFrame frame = new DispatcherFrame();
                Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,
                    new Action(delegate { frame.Continue = false; }));
                Dispatcher.PushFrame(frame);
                System.Threading.Thread.Sleep(10);
            }
        }

        /* 数一棵树里有多少个 MenuItem（子菜单到底生成了几项） */
        static int CountItems(DependencyObject node)
        {
            if (node == null) return 0;
            int n = node is MenuItem ? 1 : 0;
            int kids = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < kids; i++) n += CountItems(VisualTreeHelper.GetChild(node, i));
            return n;
        }

        /* 把一棵可视树里所有非空文字收出来（自检用，验证界面上真的画了什么） */
        static void CollectTexts(DependencyObject node, List<string> into)
        {
            if (node == null) return;
            TextBlock tb = node as TextBlock;
            if (tb != null && tb.Text != null && tb.Text.Trim().Length > 0
                && tb.Visibility == Visibility.Visible)
                into.Add(tb.Text);

            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
                CollectTexts(VisualTreeHelper.GetChild(node, i), into);
        }

        /* 找一个指定边长的方形按钮，返回它里面那个图标字形。
           用来断言「播放/播放中」的图标真的换过来了 —— 字形是私有区码点，
           直接 print 出来是空白，所以下面按码点打。 */
        static TextBlock FindSquareGlyph(DependencyObject node, double size)
        {
            if (node == null) return null;
            Border b = node as Border;
            if (b != null && Math.Abs(b.Width - size) < 0.6 && Math.Abs(b.Height - size) < 0.6)
            {
                TextBlock t = b.Child as TextBlock;
                if (t != null) return t;
            }
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                TextBlock hit = FindSquareGlyph(VisualTreeHelper.GetChild(node, i), size);
                if (hit != null) return hit;
            }
            return null;
        }

        static string Code(TextBlock tb)
        {
            if (tb == null || tb.Text == null || tb.Text.Length == 0) return "none";
            return ((int)tb.Text[0]).ToString("X4");
        }

        /* 只改播放状态的一份副本，用于按下去的即时反馈（不动其它字段） */
        static NowPlaying CloneWithStatus(NowPlaying src, string status)
        {
            NowPlaying np = new NowPlaying();
            np.Has = src.Has;
            np.App = src.App;
            np.Title = src.Title;
            np.Artist = src.Artist;
            np.Album = src.Album;
            np.Status = status;
            np.Position = src.Position;
            np.Duration = src.Duration;
            np.CanNext = src.CanNext;
            np.CanPrev = src.CanPrev;
            np.CanSeek = src.CanSeek;
            np.CanPlayPause = src.CanPlayPause;
            return np;
        }

        /* 工作线程记下的命令结果，搬进日志。
           命令失败以前是静默的，用户只会看到「点了没反应」。 */
        void DrainMediaTrace()
        {
            string[] lines = Media.DrainTrace();
            if (lines == null) return;
            for (int i = 0; i < lines.Length; i++) Trace(lines[i]);
        }

        void PollMedia()
        {
            /* 只读工作线程发布好的快照 —— 这里一个 COM 调用都不能有，
               否则又回到「在 UI 线程上等 WinRT」那个死结（见 Media.cs 顶部注释）。 */
            try
            {
                NowPlaying np = Media.Snapshot();
                if (np != null)
                {
                    _snap.Media = np;
                    _playing = np.Playing;
                }
            }
            catch { }
        }

        /* 倒计时按真实经过时间扣，手机睡过去再醒来也不会走偏 */
        void TickTimer()
        {
            int now = Environment.TickCount;
            if (_lastTimerMs < 0) { _lastTimerMs = now; return; }
            double dt = (now - _lastTimerMs) / 1000.0;
            _lastTimerMs = now;
            if (dt <= 0 || dt > 5) return;

            if (_snap.TimerRunning && _snap.TimerLeft > 0)
            {
                _snap.TimerLeft -= dt;
                if (_snap.TimerLeft <= 0)
                {
                    _snap.TimerLeft = 0;
                    _snap.TimerRunning = false;
                    Trace("timer finished");
                }
            }
        }

        void RefreshNow()
        {
            if (_built == null) return;
            _built.Refresh(_snap);
        }

        void StartData()
        {
            /* 媒体工作线程先起来，它自己会跑 MTA 上的轮询 */
            Media.Start();

            /* 本地数据：读盘 + 起剪贴板监听线程 */
            Clips.Start();
            Todos.Start();
            /* 中转站：读盘 + 起后台校验线程（源文件可能已经被删/移走） */
            Transfer.Start();
            PollMedia();
            _lastTimerMs = Environment.TickCount;

            _dataTimer = new DispatcherTimer(DispatcherPriority.Background);
            _dataTimer.Interval = TimeSpan.FromMilliseconds(400);
            _dataTimer.Tick += delegate
            {
                PollMedia();
                DrainMediaTrace();
                TickTimer();
                TickTodoDue();
                RefreshNow();
                /* 音乐出现/消失要能自动换层，否则「有歌就变成第二层」这条就不成立 */
                Reconcile();
            };
            _dataTimer.Start();
        }

        /* 点在按钮上就别拖窗口：往上找 Tag="act" */
        static bool IsActionHit(object src)
        {
            DependencyObject d = src as DependencyObject;
            for (int i = 0; d != null && i < 8; i++)
            {
                FrameworkElement fe = d as FrameworkElement;
                if (fe != null && (fe.Tag as string) == "act") return true;
                d = System.Windows.Media.VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        static readonly string[] Looks = new string[] { "glass", "obsidian", "graphite", "frost" };
        static readonly string[] Arts = new string[] { "aurora", "ember", "mint", "rose", "steel" };

        /* 内容里的墨色也是按 look 取的，换外观必须重建内容。
           只调 Apply() 会留下旧墨色（深色字压在深色面板上）——原来的双击换外观就有这个毛病。 */
        void GoLook(string key)
        {
            if (key == _lookKey) return;
            _lookKey = key;
            Trace("look -> " + key);
            Rebuild(false);
            Apply();
            Prefs.Save(_scene, _lookKey, _artKey);
        }

        void GoArt(string key)
        {
            if (key == _artKey) return;
            _artKey = key;
            Rebuild(false);
            Apply();
            Prefs.Save(_scene, _lookKey, _artKey);
        }

        void GoState(string key)
        {
            if (key == _state) return;
            SetState(key, false);
        }

        void GoScene(string key)
        {
            GoScene(key, true);
        }

        /* persist=false 用于「被事件带着跳过去」的场合（拖动自动跳中转站）：
           那不是用户选页，写进 ui.txt 会让下次启动停在这一页。 */
        void GoScene(string key, bool persist)
        {
            if (key == _scene) return;
            _scene = key;

            SyncWindowSize();

            double nw, nh;
            SizeOf(_state, out nw, out nh);
            _w.Aim(nw);
            _h.Aim(nh);
            _w.Snap(); _h.Snap();

            Rebuild(false);
            Apply();

            /* 尺寸变了，扩展样式必须重写（见 ForceClickThroughStyle 的注释） */
            ForceClickThroughStyle();

            Trace("scene -> " + key
                  + " island=" + Math.Round(IslandW) + "x" + Math.Round(IslandH)
                  + " win=" + Math.Round(Width) + "x" + Math.Round(Height)
                  + " islandLeft=" + Math.Round(IslandLeft)
                  + " hover=" + _hover + " state=" + _state);

            if (persist) Prefs.Save(_scene, _lookKey, _artKey);
        }

        void CycleLook()
        {
            int i = Array.IndexOf(Looks, _lookKey);
            GoLook(Looks[(i + 1) % Looks.Length]);
        }

        /* ------------------------------------------------------------ 主视觉 */
        void UpdateSpecularFromCursor()
        {
            Point screen;
            if (!Native.TryGetCursor(out screen)) return;
            double dipX = screen.X / _dpi;
            double dipY = screen.Y / _dpi;
            double w = Math.Max(1, IslandW), h = Math.Max(1, IslandH);
            _mx = Clamp01((dipX - (Left + IslandLeft)) / w);
            _my = Clamp01((dipY - (Top + IslandTop)) / h);
        }

        /* -------------------------------------------------------------- 自检 */
        async Task RunSelfTest()
        {
            TextWriter log = null;
            try
            {
                if (!string.IsNullOrEmpty(_logPath))
                {
                    log = new StreamWriter(_logPath, false, System.Text.Encoding.UTF8);
                }
            }
            catch { log = null; }

            Action<string> W = delegate(string s)
            {
                if (log != null) { log.WriteLine(s); log.Flush(); }
                Console.WriteLine(s);
            };

            W("SELFTEST begin dpi=" + _dpi.ToString("0.###"));
            W("SCREEN " + Native.ScreenWidth + "x" + Native.ScreenHeight);

            /* 源码是不带 BOM 的 UTF-8。若编译器按系统 ANSI 代码页读源码，
               所有中文字面量都会变成乱码——而像素计数看不出来。
               这里直接把一个已知字符串的码位打出来对照。 */
            string probe = "夜航西飞";
            W("TEXTCHECK chars=" + probe.Length
              + " codes=" + ((int)probe[0]).ToString("X4")
              + "," + ((int)probe[1]).ToString("X4")
              + "," + ((int)probe[2]).ToString("X4")
              + "," + ((int)probe[3]).ToString("X4")
              + " expect chars=4 codes=591C,822A,897F,98DE");

            SetState("compact", true);
            await Task.Delay(120);
            W("STATE compact " + Round(IslandW) + "x" + Round(IslandH)
              + " r" + Round(Math.Min(30, IslandH / 2)));

            SetState("expanded", false);
            for (int i = 0; i < 80; i++)
            {
                await Task.Delay(20);
                if (_w.Settled && _h.Settled) break;
            }
            await Task.Delay(60);
            W("STATE expanded " + Round(IslandW) + "x" + Round(IslandH)
              + " r" + Round(Math.Min(30, IslandH / 2)));
            W("MORPH expand " + Round(_lastMorphMs) + "ms");

            SetState("compact", false);
            for (int i = 0; i < 80; i++)
            {
                await Task.Delay(20);
                if (_w.Settled && _h.Settled) break;
            }
            await Task.Delay(60);
            W("MORPH collapse " + Round(_lastMorphMs) + "ms");

            SetState("idle", false);
            for (int i = 0; i < 80; i++)
            {
                await Task.Delay(20);
                if (_w.Settled && _h.Settled) break;
            }
            W("STATE idle " + Round(IslandW) + "x" + Round(IslandH));

            foreach (SceneDef d in Scenes.All)
            {
                _scene = d.Key;
                SyncWindowSize();
                SetState("expanded", true);
                await Task.Delay(90);
                W("SCENE " + d.Key + " " + Round(IslandW) + "x" + Round(IslandH)
                  + " win=" + Round(Width) + "x" + Round(Height));
            }

            foreach (string lk in Looks)
            {
                _lookKey = lk;
                Rebuild(true);
                Apply();
                await Task.Delay(60);
                W("LOOK " + lk);
            }

            foreach (string ak in Arts)
            {
                _artKey = ak;
                Rebuild(true);
                Apply();
                await Task.Delay(60);
                W("ART " + ak);
            }

            W("DESKTOPCAPTURE " + (_desktopShot != null
                ? _desktopShot.PixelWidth + "x" + _desktopShot.PixelHeight : "none"));

            /* 真实数据源自检。桌面程序在别人机器上跑起来「什么都不显示」最难查，
               这里把每条数据源的原始读数打出来，一看就知道是没数据还是代码坏了。 */
            try
            {
                NowPlaying np = Media.WaitFirst(1500);
                W("DATA media has=" + np.Has + " app=" + Media.PrettyApp(np.App)
                  + " status=" + np.Status + " title='" + np.Title + "'"
                  + " artist='" + np.Artist + "'"
                  + " pos=" + Round(np.Position) + "/" + Round(np.Duration)
                  + " canNext=" + np.CanNext + " canSeek=" + np.CanSeek);
                byte[] art = Media.CachedArt();
                W("DATA media art=" + (art == null ? "none" : art.Length + "B"));
                W("DATA lyric lines=" + Media.LyricLines() + " source="
                  + Media.LyricSource() + " v=" + Media.LyricVersion()
                  + " now='" + Media.LyricLine(np.Position) + "'");

                /* 结构验证：真把展开媒体卡建出来，把里面所有文字抖出来。
                   「数据层有歌词」和「界面上真画了歌词」是两件事，
                   只盯 DATA 行会漏掉后者。 */
                try
                {
                    Built b = Layout.Build("media", "expanded", 416, 156, Look.ByKey(_lookKey),
                                           Art.ByKey(_artKey), _snap, Actions(), false);
                    List<string> texts = new List<string>();
                    b.Refresh(_snap);          /* Live 更新器要跑一遍才有文字 */
                    CollectTexts(b.Root, texts);
                    for (int i = 0; i < texts.Count; i++)
                        W("DATA card text[" + i + "]='" + texts[i] + "'");
                }
                catch (Exception ex) { W("DATA card ERR " + ex.GetType().Name + ": " + ex.Message); }

                /* 剪贴板 / 待办两张列表卡同样要证明「内容真画到界面上了」，
                   而不只是数据文件里有几行。 */
                try
                {
                    string[] probes = new string[] { "clip", "todo" };
                    for (int k = 0; k < probes.Length; k++)
                    {
                        SceneDef sd = Scenes.Get(probes[k]);
                        Built bc = Layout.Build(probes[k], "expanded", sd.ExpW, sd.ExpH,
                                                Look.ByKey(_lookKey), Art.ByKey(_artKey),
                                                _snap, Actions(), false);
                        bc.Refresh(_snap);
                        List<string> t2 = new List<string>();
                        CollectTexts(bc.Root, t2);
                        for (int i = 0; i < t2.Count; i++)
                            W("DATA " + probes[k] + " text[" + i + "]='" + t2[i] + "'");
                    }
                }
                catch (Exception ex) { W("DATA list ERR " + ex.GetType().Name + ": " + ex.Message); }

                /* 动作接线验证：不点鼠标，直接调 Actions() 里的委托，
                   确认「动作 -> 数据 -> 列表重建」这条链是通的。 */
                try
                {
                    CardActions ca = Actions();
                    W("DATA stores clips=" + Clips.Count() + " todos=" + Todos.CountText());
                    /* 自检不弹输入窗（那会抢焦点），直接走数据层：
                       验的是「写入 -> 持久化 -> 列表重建」这条链 */
                    Todos.Add("自检：15 分钟后到点", 15);
                    W("DATA after addTodo todos=" + Todos.CountText());
                    if (Todos.Items().Length > 0 && ca.ToggleTodo != null) ca.ToggleTodo(0);
                    W("DATA after toggle[0] todos=" + Todos.CountText());

                    SceneDef sd2 = Scenes.Get("todo");
                    Built bt = Layout.Build("todo", "expanded", sd2.ExpW, sd2.ExpH,
                                            Look.ByKey(_lookKey), Art.ByKey(_artKey),
                                            _snap, ca, false);
                    bt.Refresh(_snap);
                    List<string> t3 = new List<string>();
                    CollectTexts(bt.Root, t3);
                    for (int i = 0; i < t3.Count; i++)
                        W("DATA todo-after text[" + i + "]='" + t3[i] + "'");
                }
                catch (Exception ex) { W("DATA act ERR " + ex.GetType().Name + ": " + ex.Message); }

                /* 番茄钟的开始按钮：点下去之后图标必须从 ▶(E768) 变成 ⏸(E769)。
                   以前这个图标只在构建时取一次，RefreshNow() 不重建树，
                   所以倒计时在走、按钮还画着 ▶ —— 用户以为「点不了」。 */
                try
                {
                    CardActions ca3 = Actions();
                    SceneDef st = Scenes.Get("timer");
                    _snap.TimerRunning = false;

                    Built btWin = Layout.Build("timer", "expanded", st.ExpW, st.ExpH,
                                               Look.ByKey(_lookKey), Art.ByKey(_artKey),
                                               _snap, ca3, false);
                    btWin.Refresh(_snap);
                    TextBlock g0 = FindSquareGlyph(btWin.Root, 34);
                    W("DATA timer glyph before=" + Code(g0) + " (期望 E768 播放)");

                    if (ca3.TimerToggle != null) ca3.TimerToggle();
                    btWin.Refresh(_snap);
                    W("DATA timer glyph after=" + Code(g0) + " (期望 E769 暂停)"
                      + "  running=" + _snap.TimerRunning);

                    if (ca3.TimerToggle != null) ca3.TimerToggle();
                    btWin.Refresh(_snap);
                    W("DATA timer glyph back=" + Code(g0) + " (期望 E768 播放)"
                      + "  running=" + _snap.TimerRunning);
                }
                catch (Exception ex) { W("DATA timer ERR " + ex.GetType().Name + ": " + ex.Message); }

                /* 收起态（第二层）刻意不放任何文字：只有封面和律动。
                   这里断言它确实一条文字都没有，免得以后又被顺手加回去。 */
                try
                {
                    Built bp = Layout.Build("media", "compact", Scenes.PlayW, Scenes.PlayH,
                                            Look.ByKey(_lookKey), Art.ByKey(_artKey),
                                            _snap, Actions(), true);
                    bp.Refresh(_snap);
                    List<string> tp = new List<string>();
                    CollectTexts(bp.Root, tp);
                    W("DATA play texts=" + tp.Count + " (期望 0，第二层不显示文字)"
                      + (tp.Count > 0 ? " 意外='" + tp[0] + "'" : ""));
                }
                catch (Exception ex) { W("DATA play ERR " + ex.GetType().Name + ": " + ex.Message); }

                /* 菜单结构验证：子菜单能不能展开，取决于 MenuItem 模板里有没有
                   Popup 并把它绑到 IsSubmenuOpen。这件事没法靠眼睛确认，
                   直接在进程里开一次子菜单、数生成出来的子项。 */
                try
                {
                    MenuActions ma = new MenuActions();
                    ma.Scene = GoScene;
                    ma.Look = GoLook;
                    ma.Art = GoArt;
                    ma.State = GoState;
                    ma.ResetPosition = ResetPosition;
                    ma.Shot = delegate { Shot.Begin(); };
                    ma.ToggleCapture = ToggleCapture;
                    ma.ToggleTransferOut = ToggleTransferOut;
                    ma.AddFromClipboard = AddFromClipboard;
                    ma.Quit = BeginShutdown;

                    ContextMenu cm = Menu.Build(Look.ByKey(_lookKey), Art.ByKey(_artKey), ma,
                                                _scene, _lookKey, _artKey, _state, _capture,
                                                Prefs.TransferOut, PetController.IsOn, PetStateKey());
                    W("DATA menu items=" + cm.Items.Count);
                    W("DATA menu hostLoaded=" + _host.IsLoaded);

                    /* 必须真把菜单打开：WPF 对 IsSubmenuOpen 有强制校验，
                       没挂在已加载可视树上的 MenuItem 一律不许展开。 */
                    cm.PlacementTarget = _host;
                    cm.Placement = PlacementMode.Center;
                    cm.IsOpen = true;
                    Pump(450);

                    MenuItem first = cm.Items[0] as MenuItem;
                    if (first == null) W("DATA menu first=NOT-MENUITEM");
                    else
                    {
                        first.ApplyTemplate();
                        Popup pp = first.Template.FindName("PART_Popup", first) as Popup;
                        W("DATA menu first='" + first.Header + "' hasItems=" + first.HasItems
                          + " role=" + first.Role + " popup=" + (pp == null ? "MISSING" : "ok"));
                        if (pp != null)
                        {
                            first.IsSubmenuOpen = true;
                            Pump(400);
                            W("DATA menu submenu open=" + pp.IsOpen
                              + " wanted=" + first.IsSubmenuOpen
                              + " childVisible=" + (pp.Child != null && pp.Child.IsVisible)
                              + " subItems=" + CountItems(pp.Child));
                            first.IsSubmenuOpen = false;
                            Pump(150);
                        }
                    }

                    /* 四个子菜单逐个开一遍：Popup 里那个 items host 要是没接上，
                       只会有一点零散的项跑出来，数量对不上就算失败。 */
                    for (int i = 0; i < cm.Items.Count; i++)
                    {
                        MenuItem mi = cm.Items[i] as MenuItem;
                        if (mi == null || !mi.HasItems) continue;
                        mi.IsSubmenuOpen = true;
                        Pump(260);
                        Popup p2 = mi.Template.FindName("PART_Popup", mi) as Popup;
                        W("DATA submenu[" + i + "] '" + mi.Header + "' wanted=" + mi.Items.Count
                          + " open=" + (p2 != null && p2.IsOpen)
                          + " got=" + (p2 == null ? -1 : CountItems(p2.Child)));
                        mi.IsSubmenuOpen = false;
                        Pump(80);
                    }

                    cm.IsOpen = false;
                    Pump(120);

                    /* 子项点下去要真的生效，否则「能展开」也只是个空壳。
                       直接触发 Click 事件，看外观有没有切过去。 */
                    try
                    {
                        string before = _lookKey;
                        MenuItem lookMenu = cm.Items[1] as MenuItem;
                        MenuItem target = null;
                        foreach (object o in lookMenu.Items)
                        {
                            MenuItem mi2 = o as MenuItem;
                            if (mi2 == null) continue;
                            string h = mi2.Header as string;
                            if (h != null && h.IndexOf("曜黑") >= 0) target = mi2;
                        }
                        if (target == null) W("DATA menu click=item-not-found");
                        else
                        {
                            target.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                            Pump(250);
                            W("DATA menu click '曜黑' look " + before + " -> " + _lookKey);
                        }
                    }
                    catch (Exception ex) { W("DATA menu click ERR " + ex.GetType().Name + ": " + ex.Message); }
                }
                catch (Exception ex) { W("DATA menu ERR " + ex.GetType().Name + ": " + ex.Message); }
                string err = Media.LastError();
                if (err.Length > 0) W("DATA media lastError=" + err);
            }
            catch (Exception ex) { W("DATA media ERR " + ex.GetType().Name + ": " + ex.Message); }

            W("SELFTEST OK");

            if (log != null) { log.Flush(); log.Close(); }
            BeginShutdown();
        }

        static string Round(double v)
        {
            return Math.Round(v, 1).ToString();
        }

        void BeginShutdown()
        {
            if (_closing) return;
            _closing = true;
            ReleasePet();
            StopTimersForShutdown();
            /* 退出岛必须把桌宠一起收干净：Shell 从可视树摘下来、runtime 不再推进、
               每帧委托和三个 DispatcherTimer 都停掉。漏一样的后果是进程退不干净
               （Rendering 是静态事件，会当 GC 根把窗口钉住）。
               PetController 自己也挂了 Application.Exit 兜底 —— 那条覆盖
               自检/抓帧/WDA 探针那几条直接调 Shutdown 的路；这里显式释放是为了
               「用户点退出」这条主路径不依赖兜底。重复调用是幂等的。 */
            PetController.Release();
            Application.Current.Shutdown();
        }
    }
}












