using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace WinIsland
{
    /* 桌宠的 3D 舞台 —— 一个 runtime + 一个 Viewport3D，是「球的全部实现」。

       为什么要有这一层：桌宠现在有两处宿主

         · 独立窗口（--pet / --pet-demo / --pet-selftest，PetWindow）
         · 灵动岛自己（岛内集成，IslandWindow）

       以前每处各持一套 `PetAnimationRuntime + ModelVisual3D + Apply(Key)`
       就等于同一段渲染逻辑写两遍，漂移迟早发生（「岛内的球和出场球各启动一套动画」
       正是这种写法的必然结果）。所以渲染和动画状态都收在这里：
       宿主只负责「把 Shell 放到哪个位置、多大、在哪一层」，一帧只有一次状态推进。

       尺寸与相机

       球是单位球，正交相机 Width=3 时视口里 1 个世界单位 = 视口宽/3。
       参考量纲（PetWindow 那个 220 视口）下球的可见直径是 146.67 DIP，
       而片段里的位移轨道 OffsetX/OffsetY 就是按这个量纲给的（`v*74` ≈ 74 DIP）。

       所以这里不缩放视口，只改**相机宽度**：视口始终是 ShellSize，球要多大就改
       `_camera.Width`。好处是任何尺寸下 3D 都在原生分辨率上光栅化，
       缩到 16.8 DIP 也不会被降采样糊掉（用 RenderTransform 缩视口就会）。

       位移按 `_diameter / RefDiameter` 等比缩放 —— 球小了，它自己的晃动量也跟着小，
       动作比例与参考量纲保持一致。 */
    sealed class PetStage
    {
        /// <summary>参考量纲：220 视口里球的可见直径（DIP）。片段位移轨道按它给。</summary>
        public const double RefDiameter = 220.0 * 2.0 / 3.0;

        /* Shell（视口）是固定尺寸的，比最大球径再留一圈余量：
           变形/挤压/Zoom 会让球瞬时超出标称直径，位移轨道也要在视口内走。 */
        const double ShellSlack = 1.6;

        readonly PetAnimationRuntime _runtime = new PetAnimationRuntime(17);
        readonly double _shellSize;
        readonly Canvas _shell;
        readonly Viewport3D _view;
        readonly ModelVisual3D _visual;
        readonly OrthographicCamera _camera;
        readonly TranslateTransform _shift;

        long _geomKey = long.MinValue;
        double _diameter;
        long _frames;

        public PetStage(double maxDiameter)
        {
            if (maxDiameter < 1) maxDiameter = 1;
            _shellSize = maxDiameter * ShellSlack;

            _shift = new TranslateTransform(0, 0);

            _view = new Viewport3D();
            _view.Width = _shellSize;
            _view.Height = _shellSize;
            _view.ClipToBounds = true;
            _view.IsHitTestVisible = false;
            _view.RenderTransform = _shift;

            _camera = new OrthographicCamera(new Point3D(0, 0, 3), new Vector3D(0, 0, -3),
                                             new Vector3D(0, 1, 0), 3.0);
            _view.Camera = _camera;

            _visual = new ModelVisual3D();
            _view.Children.Add(_visual);

            _shell = new Canvas();
            _shell.Width = _shellSize;
            _shell.Height = _shellSize;
            _shell.IsHitTestVisible = false;
            _shell.Children.Add(_view);

            SetDiameter(maxDiameter);
            Sync();
        }

        /// <summary>放进宿主可视树的那一块。宿主只搬它、缩它、裁它。</summary>
        public Canvas Shell { get { return _shell; } }

        /// <summary>Shell 边长（DIP）。宿主定位时用它的中心对齐。</summary>
        public double ShellSize { get { return _shellSize; } }

        /// <summary>当前设定的可见球径（DIP）。</summary>
        public double Diameter { get { return _diameter; } }

        /// <summary>当前动画状态名：idle/happy/… 或某段参考动画自己的名字。</summary>
        public string State { get { return _runtime.State; } }

        public PetFrame Frame { get { return _runtime.Frame; } }

        public long Frames { get { return _frames; } }

        public double LookWeight { get { return _runtime.LookWeight; } }

        /// <summary>
        /// 设定可见球径。只改相机宽度，不重建几何 —— 逐帧调用也不心疼
        /// （过渡期间每帧都在变，就是靠它做「长到两倍」）。
        /// </summary>
        public void SetDiameter(double diameter)
        {
            if (diameter < 1) diameter = 1;
            _diameter = diameter;
            _camera.Width = 2.0 * _shellSize / diameter;
        }

        public void Advance(double seconds)
        {
            _runtime.Advance(seconds);
            Sync();
        }

        public void Poke() { _runtime.Poke(); }

        public void Spin() { _runtime.Spin(); }

        /// <summary>跳一下（比 Poke 高得多，带蓄力与落地）。</summary>
        public void Jump() { _runtime.Jump(); }

        public void SetState(string state) { _runtime.SetState(state); }

        /// <summary>第三层打开期间置真：底稿钉在静止中立帧，眼睛仍跟鼠标。</summary>
        public void Hold(bool on) { _runtime.Hold(on); }

        public bool Held { get { return _runtime.Held; } }

        public void PlayClip(string clip) { _runtime.PlayClip(clip); }

        public void LookAt(double x, double y) { _runtime.LookAt(x, y); }

        public void LookAt(double x, double y, double amount) { _runtime.LookAt(x, y, amount); }

        public void LookAway() { _runtime.LookAway(); }

        /// <summary>
        /// 把一帧画出来。宿主每帧只会调一次（Advance 里），自检需要看任意帧时才直接调。
        ///
        /// 几何重建按量化 key 去重：呼吸、轻微视线移动不重建，
        /// 眨眼/换表情/变形立刻反映。步长取「看不出差别的最大值」，
        /// 与 PetWindow 里的那份逐位相同（搬过来时没有改数值）。
        /// </summary>
        public void ApplyFrame(PetFrame f)
        {
            long key = Key(f);
            if (key != _geomKey)
            {
                _geomKey = key;
                _visual.Content = Pet3D.Scene(f);
            }

            double k = _diameter / RefDiameter;
            _shift.X = f.OffsetX * k;
            _shift.Y = f.OffsetY * k;
        }

        void Sync()
        {
            ApplyFrame(_runtime.Frame);
            _frames++;
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
            k = Mix(k, Q(f.MorphAmount, 0.004));
            k = Mix(k, Q(f.SquashX, 0.0016));
            k = Mix(k, Q(f.SquashY, 0.0016));
            k = Mix(k, Q(f.Zoom, 0.003));
            k = Mix(k, Q(f.Roll, 0.01));
            k = Mix(k, Q(f.OffsetX, 0.5));
            k = Mix(k, Q(f.OffsetY, 0.5));
            if (f.HasEyePose)
            {
                k = Mix(k, Q(f.LeftEye.Width, 0.003));
                k = Mix(k, Q(f.LeftEye.Height, 0.003));
                k = Mix(k, Q(f.LeftEye.Tilt, 0.005));
                k = Mix(k, Q(f.LeftEye.OffsetY, 0.003));
                k = Mix(k, Q(f.RightEye.Width, 0.003));
                k = Mix(k, Q(f.RightEye.Height, 0.003));
                k = Mix(k, Q(f.RightEye.Tilt, 0.005));
                k = Mix(k, Q(f.RightEye.OffsetY, 0.003));
            }
            return k;
        }
    }
}