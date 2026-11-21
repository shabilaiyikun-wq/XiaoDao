using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinIsland
{
    /* ============================================================================
       桌宠 · 岛内集成版

       和以前最大的不同：桌宠不再是第二个窗口。

       以前是 PetWindow（独立透明置顶窗）+ 岛自己一个窗，两个置顶窗都画球，
       谁在上全看 Z-order 怎么排 —— 球要「从岛后面出来」，两个窗就必然互相争，
       而且争的结果还随激活状态、DPI、切桌面而变。这里换成一套：

         · 只有岛这一个窗；
         · 球和岛体在**同一棵可视树**里，前后关系由子元素顺序决定（不用抢）；
         · 「从岛后面出来」用两层 + 一条裁剪实现：
             _petUnder  在 _host 之前 → 岛体画在它上面，字面意义上的遮挡
             _petDock   在 _host 里、_content 之下 → 只在岛内那块地盘上出现
           同一个 Shell 在两个槽位之间搬，任何时刻只有一个槽位有它，
           所以不可能「两个球」，也不可能有「残影」。

       球的实现（runtime / 3D / 几何去重）在 PetStage 里，和独立窗口共用一份 ——
       岛内那颗球和出场那颗球是同一个对象，只有位置和尺寸在变，
       动画状态永远只有一套（这就是「不要各起一套动画」）。

       遮挡的边界

       _petUnder 的裁剪 = 窗口矩形 − 岛体轮廓（略微外扩 PetClipInset）。
       于是即便岛体半透明、折射桌面，也不可能露出球体像素 ——
       「岛挡住了球」是裁剪 + 绘制顺序两重保证，不是靠 alpha 碰运气。
       ---------------------------------------------------------------------- */

    partial class IslandWindow
    {
        /* 出场停靠点与岛体边缘的间隙 */
        const double PetGap = 8;

        /* 出场后是岛内尺寸的两倍 */
        const double PetGrow = 2.0;

        /* 裁剪边界外扩量（DIP）。岛体边缘有 1.2px 描边 + 抗锯齿，
           贴着边缘切会留一条半透明的紫边，往外让一点最干净。 */
        const double PetClipInset = 0.8;

        /* 过渡时间线，两段：先在岛内沉下去，再从岛后面滑出来长大。
           「沉下去」用淡出而不是直接消失 —— 否则第一层那颗球会在
           新一层的封面已经铺上来之后还盖在上面闪一下。 */
        const double PetFadeSec = 0.10;
        const double PetRunSec = 0.34;
        const double PetLinkTotal = PetFadeSec + PetRunSec;

        /* 窗口右侧恒留的宽度：8 间隙 + 两倍球径 + 一点余量。
           恒留而不是按开关动态加减 —— 窗口尺寸变了要重排落点、重写命中样式，
           而这块区域本来就是全透明的，用户看不见，换一次风险不值。 */
        static readonly double PetReserve =
            PetGap + Layout.IdleOrbSize(Scenes.IdleW, Scenes.IdleH) * PetGrow + 6;

        PetStage _petStage;
        Canvas _petDock;          /* 第一层：岛内（_host 里，_content 之下） */
        Canvas _petUnder;         /* 出场：岛下（_root 里，_host 之前） */
        bool _petInUnder;         /* Shell 当前挂在 _petUnder 里 */
        bool _petMounted;         /* Shell 已经挂到树上（否则别去摘） */

        /* _petT：0 = 在岛内原球的位置，PetLinkTotal = 停在岛旁。
           用「时间线上的位置」而不是一个 0..1 的进度，是因为两段时长不一样，
           写成一个数之后每一帧都要反推，反而绕。 */
        double _petT;
        double _petTFrom;
        double _petTTo;
        bool _petTween;
        double _petLastT = -1;
        double _petSide = -1;     /* -1 停左边（默认），+1 停右边（岛贴左缘时才用） */
        long _petClipKey = long.MinValue;

        /* 探针模式：钉住层状态，不让悬停/数据轮询改层，量出来的东西才可复现 */
        bool _petProbeLock;

        /* 过渡逐帧轨迹。软件渲染一张全尺寸窗口（_refract 上还挂着模糊）
           要 80~300ms，靠抓像素根本解析不了 0.44s 的行程 —— 实测一拍就差 0.27s。
           所以平滑度改成**记录动画自己的输出**：每帧 60 次推进时把
           (时间线, 直径, 球心, 不透明度, 槽位) 记下来，一次行程就有二十多帧的真值，
           而像素只用来确认「画面上确实只有一个球」。 */
        readonly List<double[]> _petTrace = new List<double[]>();
        bool _petTraceOn;

        /* ------------------------------------------------------------ 建层 */
        void BuildPetLayers()
        {
            if (_root == null || _host == null || _content == null) return;

            double d1 = IslandPetDockDiameter;
            _petStage = new PetStage(d1 * PetGrow);

            /* 出场层：插在落影之后、岛体之前。
               注意 _ballDock（消息小球）也插在 _host 之前，两者互不影响 ——
               小球在岛右侧那条窄带里，桌宠停靠点比它更靠外。 */
            _petUnder = new Canvas();
            _petUnder.IsHitTestVisible = false;
            _petUnder.Visibility = Visibility.Collapsed;
            _root.Children.Insert(1, _petUnder);

            /* 岛内层：放进 _host，压在所有材质之上、内容之下。
               在 _host 里就自动受岛体轮廓裁剪，不需要额外几何；
               在内容之下则意味着第二层的封面/波形画上来时天然盖住它。 */
            _petDock = new Canvas();
            _petDock.IsHitTestVisible = false;
            _petDock.Visibility = Visibility.Collapsed;
            _host.Children.Insert(_host.Children.IndexOf(_content), _petDock);

            _petT = PetLinkTotal;
            _petTFrom = _petT;
            _petTTo = _petT;
            PlacePet();
        }

        /* 第一层那颗球的直径。和 Layout.Build 里画原球用的是同一个来源，
           所以「桌宠占据原球的位置」是尺寸意义上的占据，不是看起来差不多。 */
        double IslandPetDockDiameter
        {
            get { return Layout.IdleOrbSize(Scenes.IdleW, Scenes.IdleH); }
        }

        /* ------------------------------------------------------------ 每帧 */
        /// <summary>桌宠还想不想要下一帧。岛体已经 settled 时靠它决定要不要继续订阅 Rendering。</summary>
        bool PetWantsFrames
        {
            get { return _petStage != null && _petOn; }
        }

        /// <summary>
        /// 把桌宠推进一帧。只在岛自己那条 Rendering 回调里调用 ——
        /// 这就是「一套更新循环」：岛和球共用同一次 tick，
        /// 不额外订阅 CompositionTarget.Rendering，也不额外起 DispatcherTimer。
        /// </summary>
        void AdvancePet(double now)
        {
            if (_petStage == null || !_petOn) return;

            double dt;
            if (_petLastT < 0) dt = 1.0 / 60;
            else dt = now - _petLastT;
            if (dt < 0) dt = 0;

            /* 60fps 上限。屏幕可能是 120/144Hz，球不需要那么多帧；
               过渡的推进也用同一个 dt，两者不会跑偏。 */
            if (dt < 0.0166) return;
            if (dt > 0.05) dt = 0.05;
            _petLastT = now;

            if (_petTween)
            {
                if (_petTTo > _petTFrom)
                {
                    _petT += dt;
                    if (_petT >= _petTTo) { _petT = _petTTo; _petTween = false; }
                }
                else
                {
                    _petT -= dt;
                    if (_petT <= _petTTo) { _petT = _petTTo; _petTween = false; }
                }
            }

            _petStage.Advance(dt);
            PlacePet();
        }

        /// <summary>把过渡目标设成 target（0 = 岛内，PetLinkTotal = 岛旁）。</summary>
        void PetLinkTo(double target, bool immediate)
        {
            if (_petStage == null) return;
            if (target < 0) target = 0;
            if (target > PetLinkTotal) target = PetLinkTotal;

            if (immediate)
            {
                _petT = target;
                _petTFrom = target;
                _petTTo = target;
                _petTween = false;
                PlacePet();
                return;
            }

            if (Math.Abs(_petT - target) < 0.0005) return;
            _petTFrom = _petT;
            _petTTo = target;
            _petTween = true;
            StartAnimating();
        }

        /// <summary>按当前层状态该在的位置：第一层在岛内，其余两层在岛旁。</summary>
        double PetTargetLink
        {
            get { return _state == "idle" ? 0.0 : PetLinkTotal; }
        }

        static double PetEase(double u)
        {
            /* 起步慢、收尾更慢：球是「长出来」的，不是弹出来的。
               smoothstep 两端一阶导为 0，接上淡出那一段不会打勾。 */
            if (u <= 0) return 0;
            if (u >= 1) return 1;
            return u * u * (3 - 2 * u);
        }

        static double PetClamp01(double v)
        {
            if (v < 0) return 0;
            if (v > 1) return 1;
            return v;
        }

        /* ------------------------------------------------------------ 摆位 */
        /// <summary>
        /// 把球摆到当前时间线对应的位置。只做「位置 / 尺寸 / 槽位 / 透明度」四件事，
        /// 不碰动画状态 —— 那个在 PetStage 里，和宿主无关。
        /// </summary>
        void PlacePet()
        {
            if (_petStage == null || _petUnder == null || _petDock == null) return;

            if (!_petOn)
            {
                if (_petMounted) UnmountPet();
                _petUnder.Visibility = Visibility.Collapsed;
                _petDock.Visibility = Visibility.Collapsed;
                return;
            }

            double w = Math.Max(2, IslandW);
            double h = Math.Max(2, IslandH);
            double ix = _ix;
            double iy = _iy;

            double d1 = IslandPetDockDiameter;
            double d2 = d1 * PetGrow;
            double dockCx = ix + w / 2;
            double dockCy = iy + h / 2;

            /* 停靠点：岛旁，**优先左边**。
               
               球从岛体左侧出来，往左移出去。左侧放不下时（岛贴近屏幕左缘，
               窗口的左边距被压没了）才退到右边，别让球被窗口边缘切掉。
               两侧都不够时取宽的一侧 —— 窗口预留本应保证走不到这一步。 */
            double r2 = d2 / 2;
            double need = PetGap + d2;
            double roomRight = Width - (ix + w);
            double roomLeft = ix;
            if (roomLeft >= need) _petSide = -1;
            else if (roomRight >= need) _petSide = 1;
            else _petSide = roomRight >= roomLeft ? 1 : -1;

            double restCx = _petSide > 0 ? ix + w + PetGap + r2 : ix - PetGap - r2;
            double restCy = iy + h / 2;

            /* 时间线拆两段：
                 0 → PetFadeSec            还在岛内那一格，淡出、缩一点
                 PetFadeSec → PetLinkTotal 在岛下那一层，从岛心移到岛旁、长到两倍 */
            double fade = PetClamp01(_petT / PetFadeSec);
            double run = PetClamp01((_petT - PetFadeSec) / PetRunSec);

            bool inDock = _petT < PetFadeSec;
            double dia, cx, cy, opacity;

            if (inDock)
            {
                cx = dockCx;
                cy = dockCy;
                /* 这里**不能**再缩小。曾经写的是 dia = d1*(1-0.22*fade)，
                   于是岛内那颗淡出到 0 时只有 78%（13.1dip），而岛下那颗一接手
                   就是整尺寸 16.8dip —— 直径在换层那一帧硬跳 3.7dip。
                   因为那一刻两边都不可见（一颗透明度 0、一颗整颗埋在岛体轮廓里），
                   肉眼看不出来，但这是一条真实的不连续：只要以后有人把接手点
                   挪出岛体轮廓，它立刻会变成一个可见的跳动。改成只淡出不缩，
                   直径就从头到尾连续（16.8 → 33.6）。 */
                dia = d1;
                opacity = 1.0 - fade;
            }
            else
            {
                double e = PetEase(run);
                cx = dockCx + (restCx - dockCx) * e;
                cy = dockCy + (restCy - dockCy) * e;
                dia = d1 + (d2 - d1) * e;
                opacity = 1.0;
            }

            SetPetSlot(!inDock);

            /* 两个槽位的坐标原点不一样：
                 _petDock  是 _host 的子元素，原点 = 岛体左上角
                 _petUnder 是 _root 的子元素，原点 = 窗口左上角 */
            double localCx = inDock ? cx - ix : cx;
            double localCy = inDock ? cy - iy : cy;

            _petStage.SetDiameter(dia);
            Canvas shell = _petStage.Shell;
            double half = _petStage.ShellSize / 2;
            Canvas.SetLeft(shell, localCx - half);
            Canvas.SetTop(shell, localCy - half);
            shell.Opacity = opacity;

            _petDock.Width = w;
            _petDock.Height = h;
            _petUnder.Width = Math.Max(2, Width);
            _petUnder.Height = Math.Max(2, Height);

            _petDock.Visibility = inDock ? Visibility.Visible : Visibility.Collapsed;
            _petUnder.Visibility = inDock ? Visibility.Collapsed : Visibility.Visible;

            if (!inDock) UpdatePetUnderClip(w, h, ix, iy);

            /* 轨迹：t / 直径 / 球心x / 球心y / 不透明度 / 槽位 / 岛内停靠点x / 岛宽 */
            if (_petTraceOn && _petTrace.Count < 4096)
            {
                _petTrace.Add(new double[] {
                    _petT, dia, cx, cy, opacity, inDock ? 0 : 1, dockCx, w });
            }
        }

        /// <summary>把 Shell 在「岛内 / 岛下」两个槽位之间搬。搬的是同一个对象，不是复制。</summary>
        void SetPetSlot(bool under)
        {
            Canvas target = under ? _petUnder : _petDock;
            if (_petMounted && _petInUnder == under && ReferenceEquals(_petStage.Shell.Parent, target))
                return;

            Canvas shell = _petStage.Shell;
            Panel parent = shell.Parent as Panel;
            if (parent != null) parent.Children.Remove(shell);
            target.Children.Add(shell);

            _petMounted = true;
            _petInUnder = under;
        }

        void UnmountPet()
        {
            if (_petStage == null || !_petMounted) return;
            Panel parent = _petStage.Shell.Parent as Panel;
            if (parent != null) parent.Children.Remove(_petStage.Shell);
            _petMounted = false;
        }

        /// <summary>
        /// 岛下那层的裁剪：窗口矩形减去岛体轮廓。
        ///
        /// 这是「岛把球挡住」的硬保证 —— 不是依赖绘制顺序碰巧正确，也不是
        /// 依赖岛体不透明。球的一部分永远画不进岛体的地盘里，所以哪怕
        /// 换成 glass 那种半透明折射外观，也看不见球在岛底下。
        /// </summary>
        void UpdatePetUnderClip(double w, double h, double ix, double iy)
        {
            long key = (long)Math.Round(w * 4) ^ ((long)Math.Round(h * 4) << 16)
                     ^ ((long)Math.Round(ix * 4) << 32) ^ ((long)Math.Round(iy * 4) << 48)
                     ^ ((long)Math.Round(Width * 4) << 20) ^ ((long)Math.Round(Height * 4) << 44);
            if (key == _petClipKey && _petUnder.Clip != null) return;
            _petClipKey = key;

            double r = Math.Min(30, h / 2);

            /* Squircle.Build 返回**冻结**几何，不能设 Transform。用
               Combine(x, Geometry.Empty, Union, transform) 把岛体轮廓搬到 (ix,iy) ——
               这是给冻结几何换位置的唯一办法。注意不能直接把变换传给下面那次
               Exclude：Geometry.Combine 的变换同时作用于两个几何，
               矩形也会被搬走，抠出来的就不是岛体那块了。 */
            TranslateTransform shift = new TranslateTransform(ix - PetClipInset, iy - PetClipInset);
            Geometry inflated = Squircle.Build(w + PetClipInset * 2, h + PetClipInset * 2, r + PetClipInset);
            Geometry placed = Geometry.Combine(inflated, Geometry.Empty, GeometryCombineMode.Union, shift);

            RectangleGeometry all = new RectangleGeometry(
                new Rect(0, 0, Math.Max(2, Width), Math.Max(2, Height)));
            all.Freeze();

            Geometry clipGeo = Geometry.Combine(all, placed, GeometryCombineMode.Exclude, null);
            clipGeo.Freeze();
            _petUnder.Clip = clipGeo;
        }

        /* ------------------------------------------------------------ 开关 */

        /// <summary>
        /// 按「桌宠现在到底开没开」重算窗口要不要那块预留宽度。
        ///
        /// 预留宽度（PetGap + 两倍球径 + 余量 ≈ 47.6 DIP）只在实际开着时才加：
        /// 关着的时候窗口必须和引入桌宠之前**逐像素一致** —— 验收脚本
        /// 是按「窗口 = ExpW + 48」量岛体位置的，恒留会让岛在窗口里整体右移
        /// 23.8 DIP，把一堆既有断言判红。
        ///
        /// 改了尺寸要接着重排：SyncWindowSize 会重算 _ix/_iy 与 Left/Top，
        /// RepositionLayers 把每层按新几何摆回去；扩展样式也要重写一次
        /// ——改尺寸会重建合成属性，点击穿透有可能被系统清掉。
        /// 返回 true 表示尺寸真的变了。
        /// </summary>
        bool ApplyPetReserve()
        {
            double want = Scenes.Get(_scene).ExpW + MX * 2
                          + (_notify ? NotifyReserve : 0)
                          + (_petOn ? PetReserve : 0);
            if (Math.Abs(Width - want) < 0.01) return false;
            Trace("pet reserve: window " + Width.ToString("0.0") + " -> " + want.ToString("0.0")
                  + " (pet " + (_petOn ? "on" : "off") + ")");
            SyncWindowSize();
            ForceClickThroughStyle();
            return true;
        }

        void EnablePet()
        {
            _petOn = true;
            Prefs.Pet = true;
            Prefs.Save(_scene, _lookKey, _artKey);
            ApplyPetReserve();
            MountPetForState(true);
            Trace("pet enabled (integrated, pref saved)");
        }

        /* 启动时按偏好拉起 —— 和 EnablePet 的区别只有一个：不写 ui.txt。
           这一路是「照用户上次的意愿执行」，不是「用户改了意愿」。 */
        void EnablePetByPreference()
        {
            _petOn = true;
            ApplyPetReserve();
            MountPetForState(true);
            Trace("pet enabled by preference (integrated, ui.txt untouched)");
        }

        void DisablePet()
        {
            bool was = _petOn;
            _petOn = false;
            PetLinkTo(0, true);
            UnmountPet();
            PlacePet();
            ApplyPetReserve();
            Rebuild(false);
            Apply();
            if (!was) { Trace("pet already off (pref untouched)"); return; }
            Prefs.Pet = false;
            Prefs.Save(_scene, _lookKey, _artKey);
            Trace("pet disabled (integrated, pref saved)");
        }

        void TogglePet()
        {
            if (_petOn) DisablePet();
            else EnablePet();
        }

        /// <summary>动作前保证桌宠在。返回 false 表示这次确实没能拉起来。</summary>
        bool EnsurePet()
        {
            if (_petOn) return true;
            _petOn = true;
            Prefs.Pet = true;
            Prefs.Save(_scene, _lookKey, _artKey);
            ApplyPetReserve();
            MountPetForState(true);
            Trace("pet enabled by action (integrated, pref saved)");
            return true;
        }

        /// <summary>开起来之后先落到「当前层该在的位置」，别从岛内闪一下再飞出去。</summary>
        void MountPetForState(bool immediate)
        {
            _petT = PetTargetLink;
            _petTFrom = _petT;
            _petTTo = _petT;
            _petTween = false;
            _petLastT = -1;
            Rebuild(immediate);
            Apply();
            PlacePet();
            StartAnimating();
        }

        /* ------------------------------------------------------------ 动作 */
        void PetPoke()
        {
            if (!EnsurePet()) { Trace("pet poke skipped: not running"); return; }
            _petStage.Poke();
            Trace("pet poke");
        }

        void PetSpin()
        {
            if (!EnsurePet()) { Trace("pet spin skipped: not running"); return; }
            _petStage.Spin();
            Trace("pet spin");
        }

        void PetSetState(string state)
        {
            if (!EnsurePet()) { Trace("pet state skipped: not running"); return; }
            _petStage.SetState(state);
            PlacePet();
            Trace("pet state -> " + state);
        }

        void PetPlayClip(string clip)
        {
            if (!EnsurePet()) { Trace("pet clip skipped: not running"); return; }
            try { _petStage.PlayClip(clip); }
            catch (Exception ex) { Trace("pet clip failed: " + clip + " " + ex.Message); return; }
            PlacePet();
            Trace("pet clip -> " + clip);
        }

        void PetRandomIdle()
        {
            if (!EnsurePet()) { Trace("pet idle skipped: not running"); return; }
            /* 恢复随机 = 回到 idle 状态机，片段池由 runtime 自己洗牌 */
            _petStage.SetState("idle");
            PlacePet();
            Trace("pet idle (random pool)");
        }

        /* 菜单勾选用的当前态。桌宠没开时返回空串，于是「表情模式」和
           「全部动画」里的勾选全灭 —— 这比留着上一次的勾更诚实。 */
        string PetStateKey()
        {
            if (!_petOn || _petStage == null) return "";
            return _petStage.State;
        }

        /* ------------------------------------------------------------ 释放 */
        /// <summary>
        /// 关岛时把桌宠这条链上的东西全摘干净。
        ///
        /// 集成版没有第二个窗口了，所以「关掉岛就该没有球在跑」这件事
        /// 完全取决于这里：Shell 从可视树摘下、runtime 不再被推进、
        /// 每帧回调和计时器停掉（后两者在 BeginShutdown 里一律收）。
        /// 漏一步，进程就会因为还在订阅 Rendering 或还有 timer 醒着而退不干净。
        /// </summary>
        void ReleasePet()
        {
            _petOn = false;
            _petTween = false;
            _petT = 0;
            _petLastT = -1;
            UnmountPet();
            _petStage = null;
        }

        /* ============================================================================
           验收探针（--pet-probe）

           为什么不用抓屏：抓屏里混着桌面、别的窗口、DPI 缩放后的模糊边缘，
           量「球是不是 16.8 DIP」「眼睛还剩几个像素」这种问题会一路靠猜。
           这里直接把 **自己的可视树** 渲成位图（RenderTargetBitmap 会连
           Viewport3D 一起渲），于是每个像素都对应确定的 DIP 坐标。

           另外还会抓一张真窗口的屏，只为证明「这块东西真的合成到屏幕上了」，
           不参与几何断言。
           ---------------------------------------------------------------------- */

        /* 桌宠那套紫。和 Art.AccOn(#5342C2) 差 7/8/6 ——
           所以判定带只有 ±4：球的平面材质整片命中，岛第一层那颗原球是渐变，
           只有绕过该色的一小圈能碰上，靠它分辨「球」和「原球」。 */
        static bool IsPetPurple(int b, int g, int r)
        {
            return Math.Abs(r - 0x5A) <= 4 && Math.Abs(g - 0x4A) <= 4 && Math.Abs(b - 0xC8) <= 4;
        }

        static bool IsPetEye(int b, int g, int r)
        {
            return r > 200 && g > 200 && b > 200;
        }

        async Task RunPetProbe()
        {
            StringBuilder log = new StringBuilder();
            StringBuilder json = new StringBuilder();
            List<string> steps = new List<string>();

            try
            {
                /* 探针自己开集成版，不走 EnablePet —— 那条会写 ui.txt */
                _petOn = true;
                ApplyPetReserve();
                PetLinkTo(0, true);
                Rebuild(true);
                Apply();

                /* 等一次真实合成，让 3D 内容真正上屏 */
                await Task.Delay(420);

                /* ① 第一层：球占原球的位置 */
                SetState("idle", true);
                await SettlePet();
                PetShot("layer1", log, json, steps);

                /* ② 第一层 → 第二层：过渡途中看「有没有两个球 / 残影 / 跳变」 */
                SetState("compact", false);
                await SampleTransition("idle_to_compact", log, json, steps);
                await SettlePet();
                PetShot("layer2", log, json, steps);

                /* ③ 第三层：出场后仍是两倍尺寸，只是岛变大了 */
                SetState("expanded", false);
                await SettlePet();
                PetShot("layer3", log, json, steps);

                /* ④ 回到第一层：球平滑回岛内、恢复第一层尺寸、原球仍隐藏 */
                SetState("idle", false);
                await SampleTransition("expanded_to_idle", log, json, steps);
                await SettlePet();
                PetShot("layer1return", log, json, steps);

                /* 真窗口抓屏：证明这套东西真的被合成到屏幕上 */
                if (ScreenShotForProbe() != null) json.AppendLine("  \"screenShot\": true,");

                /* ⑥ 菜单动作整条链路：这一版把「戳击 / 转圈 / 表情 / 动画 / 恢复随机」
                   从 PetController 改成了直接驱 PetStage，所以必须证明菜单点下去
                   真的动的是**岛内这颗球**（而不是某个已经没人用的老对象）。 */
                RunPetActionChecks(log, json, steps);

                bool ok = true;
                for (int i = 0; i < steps.Count; i++)
                    if (steps[i] != "ok") ok = false;

                /* 拼一份**合法**的 JSON：每段自己以逗号结尾，拼完之后
                   把末尾那个逗号去掉再补右大括号。带尾逗号的写法人手读没问题，
                   但 ConvertFrom-Json 会直接判不合法（实测）。 */
                string body = json.ToString().TrimEnd();
                while (body.EndsWith(",")) body = body.Substring(0, body.Length - 1).TrimEnd();
                string doc = "{ \"ok\": " + (ok ? "true" : "false") + ",\n" + body + "\n}\n";
                WriteProbeFile("pet-island-probe.json", doc);
                WriteProbeFile("pet-island-probe.txt", log.ToString());

                Trace("pet probe done ok=" + ok);
                Environment.ExitCode = ok ? 0 : 1;
            }
            catch (Exception ex)
            {
                Trace("pet probe failed: " + ex.GetType().Name + ": " + ex.Message);
                WriteProbeFile("pet-island-probe.txt", "failed: " + ex + "\n" + log);
                Environment.ExitCode = 2;
            }

            ReleasePet();
            if (Application.Current != null) Application.Current.Shutdown();
        }

        /// <summary>
        /// 菜单动作链路检查：每个动作都调**菜单真正调的那个方法**（PetPoke 等），
        /// 然后看球本身有没有反应 —— 看 State 和帧，而不是看返回值。
        ///
        /// 这一版的改动正是「动作从 PetController 转到 PetStage」，
        /// 所以这条检查是这次重构最该有的一条：方法名没变、菜单没变，
        /// 但底下那个对象换了，接错线只会表现为「点了没反应」。
        /// </summary>
        void RunPetActionChecks(StringBuilder log, StringBuilder json, List<string> steps)
        {
            List<string> fails = new List<string>();

            /* 表情模式：状态名必须当场变 */
            PetSetState("sleepy");
            if (_petStage.State != "sleepy") fails.Add("setState=" + _petStage.State);

            /* 参考动画：PlayClip 会把状态名换成片段自己的名字 */
            PetPlayClip("Bodymoves_A");
            if (_petStage.State != "Bodymoves_A") fails.Add("clip=" + _petStage.State);

            /* 恢复随机：回到 idle 状态机 */
            PetRandomIdle();
            if (_petStage.State != "idle") fails.Add("randomIdle=" + _petStage.State);

            /* 戳击 / 转圈不换状态名，只看帧有没有在动。
               先把当前帧记下来，动一下，再等几帧后比较 —— 冲量是逐帧积分的。 */
            double before = _petStage.Frame.SquashY;
            PetPoke();
            bool pokeMoved = false;
            for (int i = 0; i < 12; i++)
            {
                _petStage.Advance(PetAnimationRuntime.Step);
                if (Math.Abs(_petStage.Frame.SquashY - before) > 1e-6) pokeMoved = true;
            }
            if (!pokeMoved) fails.Add("pokeNoFrameChange");

            double yawBefore = _petStage.Frame.Yaw;
            PetSpin();
            bool spinMoved = false;
            for (int i = 0; i < 12; i++)
            {
                _petStage.Advance(PetAnimationRuntime.Step);
                if (Math.Abs(_petStage.Frame.Yaw - yawBefore) > 1e-6) spinMoved = true;
            }
            if (!spinMoved) fails.Add("spinNoFrameChange");

            bool ok = fails.Count == 0;
            steps.Add(ok ? "ok" : "actions: " + string.Join(",", fails.ToArray()));

            log.AppendLine("actions  " + (ok ? "ok" : "FAIL")
                           + "  state=" + _petStage.State
                           + " frames=" + _petStage.Frames
                           + " poke=" + (pokeMoved ? "moved" : "still")
                           + " spin=" + (spinMoved ? "moved" : "still")
                           + (ok ? "" : "  FAILS: " + string.Join(" | ", fails.ToArray())));
            json.AppendLine("  \"actions\": { \"ok\": " + (ok ? "true" : "false")
                            + ", \"state\": \"" + _petStage.State + "\""
                            + ", \"stageFrames\": " + _petStage.Frames
                            + ", \"pokeMoved\": " + (pokeMoved ? "true" : "false")
                            + ", \"spinMoved\": " + (spinMoved ? "true" : "false")
                            + ", \"fails\": \"" + string.Join(",", fails.ToArray()) + "\" },");
        }

        void WriteProbeFile(string name, string text)
        {
            try
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, name), text);
            }
            catch (Exception ex) { Trace("probe write failed: " + ex.Message); }
        }

        /// <summary>等过渡走完再截：时间线 0.44s，给到 0.9s 留余量。</summary>
        async Task SettlePet()
        {
            await Task.Delay(900);
        }

        /* 过渡验收 = 逐帧轨迹 + 稀疏像素确认。

           逐帧轨迹回答「动画有没有跳」：
             · 时间线单调、每帧推进 ≤ 0.05s（AdvancePet 的 dt 上限）；
             · 直径按帧连续变化，硬切会一步走完整段 16.8 DIP；
             · 球心按帧连续变化，硬切会一步跳过整段行程；
             · 不透明度只在岛内那一小段里变化，且连续；
             · 槽位只切换一次，且切换瞬间球心仍在岛心 —— 也就是说球是
               **整个埋进岛体轮廓里**才换层的，所以换层那一刻画面上什么都不会变。
           像素确认回答「画面上确实只有一个球」—— 这一步不能省：轨迹只证明
           我们「要求」画什么，像素才证明真的这么画了。 */
        async Task SampleTransition(string tag, StringBuilder log, StringBuilder json,
                                    List<string> steps)
        {
            _petTrace.Clear();
            _petTraceOn = true;

            int maxBlobs = 0;
            int samples = 0;
            /* 行程 0.44s，采 6 次、每次间隔 220ms 覆盖整段并留出收敛时间 */
            for (int i = 0; i < 6; i++)
            {
                await Task.Delay(220);
                PetFrameShot s = RenderShot(false);
                if (s == null || s.PetPixels == 0) continue;
                samples++;
                if (s.Blobs > maxBlobs) maxBlobs = s.Blobs;
            }
            _petTraceOn = false;

            /* ---- 轨迹分析 ---- */
            List<string> fails = new List<string>();
            int frames = _petTrace.Count;
            double maxDt = 0, maxDiaStep = 0, maxCenterStep = 0, maxOpacityStep = 0;
            int switches = 0;
            double switchOffset = -1;      /* 换层瞬间球心距岛心的距离 */
            bool monotone = true;
            double lastT = _petTrace.Count > 0 ? _petTrace[0][0] : 0;
            int dir = _petTrace.Count > 1
                      ? (_petTrace[_petTrace.Count - 1][0] >= _petTrace[0][0] ? 1 : -1) : 1;

            for (int i = 1; i < frames; i++)
            {
                double[] a = _petTrace[i - 1];
                double[] c = _petTrace[i];
                double dt = Math.Abs(c[0] - a[0]);
                if (dt > maxDt) maxDt = dt;
                if ((c[0] - a[0]) * dir < -0.0001) monotone = false;
                double dd = Math.Abs(c[1] - a[1]);
                if (dd > maxDiaStep) maxDiaStep = dd;
                double dc = Math.Abs(c[2] - a[2]);
                if (dc > maxCenterStep) maxCenterStep = dc;
                /* 不透明度只在**同一槽位内**比较：换层那一刻是刻意的接手
                   （岛内那颗已淡到 0，岛下那颗一上来就是 1，且整颗埋在岛体里），
                   跨槽位比会得到「跳了 0.93」这种假红。 */
                if (Math.Abs(c[5] - a[5]) < 0.5)
                {
                    double dop = Math.Abs(c[4] - a[4]);
                    if (dop > maxOpacityStep) maxOpacityStep = dop;
                }
                if (Math.Abs(c[5] - a[5]) > 0.5)
                {
                    switches++;
                    switchOffset = Math.Abs(c[2] - c[6]);
                }
                lastT = c[0];
            }

            /* 门槛要跟「真实速率 × 本帧 dt」比，不能拍一个固定数：
               直径最大斜率 = (33.6-16.8)×1.5/0.34 ≈ 74 dip/s，dt 上限 0.05 → 3.7dip；
               球心最大斜率 = 行程 × 1.5/0.34，行程是把球从岛心送到停靠点的距离
               （compact 下约 80dip → 352dip/s → 一帧 14dip，实测 13.88 就是这么来的）。
               硬切会一步跨完整段（3.7 → 16.8，14 → 80），仍然分得开。 */
            double travel = 0;
            if (frames > 1) travel = Math.Abs(_petTrace[frames - 1][2] - _petTrace[0][2]);
            double diaLimit = Math.Max(2.5, 80.0 * maxDt + 0.5);
            double centerLimit = Math.Max(8.0, travel * 0.3);
            double opacityLimit = Math.Max(0.55, maxDt / PetFadeSec + 0.08);

            if (frames < 8) fails.Add("frames=" + frames);
            if (maxBlobs > 1) fails.Add("blobs=" + maxBlobs);
            if (!monotone) fails.Add("nonMonotone");
            if (maxDt > 0.05) fails.Add("dt=" + maxDt.ToString("0.000"));
            if (maxDiaStep > diaLimit) fails.Add("diaStep=" + maxDiaStep.ToString("0.00"));
            if (maxCenterStep > centerLimit) fails.Add("centerStep=" + maxCenterStep.ToString("0.00"));
            if (maxOpacityStep > opacityLimit) fails.Add("opacityStep=" + maxOpacityStep.ToString("0.00"));
            if (switches != 1) fails.Add("slotSwitch=" + switches);
            /* 换层瞬间球心必须还在岛心：球完全埋进岛体轮廓里 → 画面上无变化 */
            if (switchOffset > 0.6) fails.Add("switchAtOffset=" + switchOffset.ToString("0.0"));

            bool ok = fails.Count == 0;
            steps.Add(ok ? "ok" : tag + ": " + string.Join(",", fails.ToArray()));

            log.AppendLine("transition " + tag + "  " + (ok ? "ok" : "FAIL")
                           + (ok ? "" : "  FAILS: " + string.Join(" | ", fails.ToArray())));
            log.AppendLine("  frames=" + frames + " pixelSamples=" + samples
                           + " maxBlobs=" + maxBlobs
                           + " t: " + _petTrace[0][0].ToString("0.000") + "->"
                           + lastT.ToString("0.000") + " maxDt=" + maxDt.ToString("0.000") + "s");
            log.AppendLine("  per-frame max: diaStep=" + maxDiaStep.ToString("0.00")
                           + "dip centerStep=" + maxCenterStep.ToString("0.00")
                           + "dip opacityStep=" + maxOpacityStep.ToString("0.00"));
            log.AppendLine("  slot switches=" + switches + " at island-center offset="
                           + (switchOffset < 0 ? "-" : switchOffset.ToString("0.000") + "dip")
                           + "  dia " + (_petTrace.Count > 0 ? _petTrace[0][1].ToString("0.00") : "-")
                           + "->" + (_petTrace.Count > 0
                                     ? _petTrace[_petTrace.Count - 1][1].ToString("0.00") : "-"));

            json.AppendLine("  \"" + tag + "\": { \"ok\": " + (ok ? "true" : "false")
                            + ", \"frames\": " + frames
                            + ", \"pixelSamples\": " + samples
                            + ", \"maxBlobs\": " + maxBlobs
                            + ", \"maxDtSec\": " + maxDt.ToString("0.0000")
                            + ", \"maxDiaStepDip\": " + maxDiaStep.ToString("0.000")
                            + ", \"maxCenterStepDip\": " + maxCenterStep.ToString("0.000")
                            + ", \"maxOpacityStep\": " + maxOpacityStep.ToString("0.000")
                            + ", \"slotSwitches\": " + switches
                            + ", \"switchOffsetDip\": " + (switchOffset < 0 ? -1 : switchOffset).ToString("0.000")
                            + ", \"diaFrom\": " + (_petTrace.Count > 0
                                                   ? _petTrace[0][1].ToString("0.000") : "0")
                            + ", \"diaTo\": " + (_petTrace.Count > 0
                                                 ? _petTrace[_petTrace.Count - 1][1].ToString("0.000") : "0")
                            + ", \"fails\": \"" + string.Join(",", fails.ToArray()) + "\" },");
        }

        /* ------------------------------------------------------------ 度量 */
        class PetFrameShot
        {
            public int W, H;
            public double Dpi;
            public int PetPixels;
            public int Blobs;
            public int EyePixels;
            public int PurpleInIsland;      /* 落在岛体轮廓里的球体像素 */
            public int PurpleOutIsland;
            public double MinX = double.MaxValue, MaxX = double.MinValue;
            public double MinY = double.MaxValue, MaxY = double.MinValue;
            public double EyeMinX = double.MaxValue, EyeMaxX = double.MinValue;
            public double EyeMinY = double.MaxValue, EyeMaxY = double.MinValue;
            public double DiaDip;
            public double CxDip, CyDip;
            public double EyeWDip, EyeHDip;
            public int IslandPixels;        /* 岛体轮廓内的不透明像素（判「岛在不在」） */
            public int OrbPixels;           /* 岛内的非平色紫像素：idle 下就是原球残留 */
        }

        /// <summary>
        /// 把 _root 渲成位图再逐像素量。纯 DIP，不含桌面。
        ///
        /// needIsland=false 是**快速路径**：不判「这个像素在不在岛体轮廓里」。
        /// 那个判断要对每个像素调一次 Squircle.Contains（几何包含测试），
        /// 一次整窗要三十多万次 —— 过渡采样只要「几个球 / 直径 / 球心」，
        /// 根本用不着岛体归属，砍掉之后采样间隔从 300ms 降到能解析 0.44s 的行程。
        /// </summary>
        PetFrameShot RenderShot(bool needIsland)
        {
            double dpi = _dpi > 0.1 ? _dpi : 1.0;
            int pw = (int)Math.Round(Math.Max(2, Width) * dpi);
            int ph = (int)Math.Round(Math.Max(2, Height) * dpi);
            if (pw <= 0 || ph <= 0) return null;

            RenderTargetBitmap rtb = new RenderTargetBitmap(pw, ph, 96 * dpi, 96 * dpi,
                                                            PixelFormats.Pbgra32);
            rtb.Render(_root);

            int stride = pw * 4;
            byte[] buf = new byte[stride * ph];
            rtb.CopyPixels(buf, stride, 0);

            PetFrameShot s = new PetFrameShot();
            s.W = pw; s.H = ph; s.Dpi = dpi;

            double iw = Math.Max(2, IslandW), ih = Math.Max(2, IslandH);
            double ix = _ix, iy = _iy;
            double r = Math.Min(30, ih / 2);
            bool[] hit = new bool[pw * ph];

            /* 第一遍：球体像素、包围盒、岛内/岛外归属、岛内非平色紫像素 */
            for (int y = 0; y < ph; y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    int o = y * stride + x * 4;
                    int b = buf[o], g = buf[o + 1], rr = buf[o + 2], a = buf[o + 3];
                    if (a <= 8) continue;

                    double dx = (x + 0.5) / dpi;
                    double dy = (y + 0.5) / dpi;
                    bool inIsland = needIsland && Squircle.Contains(
                        new Point(dx - ix, dy - iy), iw, ih, r);
                    if (inIsland) s.IslandPixels++;

                    if (IsPetPurple(b, g, rr))
                    {
                        s.PetPixels++;
                        hit[y * pw + x] = true;
                        if (dx < s.MinX) s.MinX = dx;
                        if (dx > s.MaxX) s.MaxX = dx;
                        if (dy < s.MinY) s.MinY = dy;
                        if (dy > s.MaxY) s.MaxY = dy;
                        if (inIsland) s.PurpleInIsland++; else s.PurpleOutIsland++;
                    }
                    else if (inIsland && b > rr + 24 && rr > g + 2 && b > 120)
                    {
                        /* 紫系但不是平色 #5A4AC8：渐变/高光 → 原球或卡片装饰 */
                        s.OrbPixels++;
                    }
                }
            }

            if (s.PetPixels > 0)
            {
                s.DiaDip = (s.MaxX - s.MinX) + 1.0 / dpi;
                s.CxDip = (s.MinX + s.MaxX) / 2;
                s.CyDip = (s.MinY + s.MaxY) / 2;
            }

            /* 第二遍：眼睛只在**球的包围盒**里数。
               早先的写法是在岛体轮廓里数近白像素 —— 第二/三层岛上有白字和白条，
               会被当成眼睛（实测 compact 报了 145 个、还有 44dip 的「眼距」）。
               锁在球框里之后，「眼睛」就只可能是球上的眼睛。 */
            if (s.PetPixels > 4)
            {
                int x0 = (int)Math.Floor(s.MinX * dpi) - 2;
                int y0 = (int)Math.Floor(s.MinY * dpi) - 2;
                int x1 = (int)Math.Ceiling(s.MaxX * dpi) + 2;
                int y1 = (int)Math.Ceiling(s.MaxY * dpi) + 2;
                if (x0 < 0) x0 = 0;
                if (y0 < 0) y0 = 0;
                if (x1 > pw - 1) x1 = pw - 1;
                if (y1 > ph - 1) y1 = ph - 1;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int o = y * stride + x * 4;
                        int b = buf[o], g = buf[o + 1], rr = buf[o + 2], a = buf[o + 3];
                        if (a <= 8 || !IsPetEye(b, g, rr)) continue;
                        s.EyePixels++;
                        double dx = (x + 0.5) / dpi, dy = (y + 0.5) / dpi;
                        if (dx < s.EyeMinX) s.EyeMinX = dx;
                        if (dx > s.EyeMaxX) s.EyeMaxX = dx;
                        if (dy < s.EyeMinY) s.EyeMinY = dy;
                        if (dy > s.EyeMaxY) s.EyeMaxY = dy;
                    }
                }
            }

            if (s.EyePixels > 0)
            {
                s.EyeWDip = (s.EyeMaxX - s.EyeMinX) + 1.0 / dpi;
                s.EyeHDip = (s.EyeMaxY - s.EyeMinY) + 1.0 / dpi;
            }

            s.Blobs = CountBlobs(hit, pw, ph, (int)Math.Max(4, 20 * s.Dpi * s.Dpi));
            return s;
        }

        /* 连通域计数（4 邻域，迭代栈，不递归）。
           面积下限过滤抗锯齿碎点：一颗球在 1.5 dpi 下约 500 像素，
           阈值取 20dpi² ≈ 45，把零星毛边挡在外面。 */
        static int CountBlobs(bool[] hit, int w, int h, int minArea)
        {
            int count = 0;
            bool[] seen = new bool[hit.Length];
            Stack<int> stack = new Stack<int>();
            for (int i = 0; i < hit.Length; i++)
            {
                if (!hit[i] || seen[i]) continue;
                int area = 0;
                stack.Clear();
                stack.Push(i);
                seen[i] = true;
                while (stack.Count > 0)
                {
                    int p = stack.Pop();
                    area++;
                    int px = p % w, py = p / w;
                    if (px > 0) Push(hit, seen, stack, p - 1);
                    if (px < w - 1) Push(hit, seen, stack, p + 1);
                    if (py > 0) Push(hit, seen, stack, p - w);
                    if (py < h - 1) Push(hit, seen, stack, p + w);
                }
                if (area >= minArea) count++;
            }
            return count;
        }

        static void Push(bool[] hit, bool[] seen, Stack<int> stack, int i)
        {
            if (!hit[i] || seen[i]) return;
            seen[i] = true;
            stack.Push(i);
        }

        /* ------------------------------------------------------------ 取证 */
        void PetShot(string tag, StringBuilder log, StringBuilder json, List<string> steps)
        {
            PetFrameShot s = RenderShot(true);
            if (s == null) { steps.Add(tag + ": render null"); return; }

            bool idle = _state == "idle";
            double d1 = IslandPetDockDiameter;
            double d2 = d1 * PetGrow;
            double iw = Math.Max(2, IslandW), ih = Math.Max(2, IslandH);
            double islandCx = _ix + iw / 2, islandCy = _iy + ih / 2;

            List<string> fails = new List<string>();
            if (s.Blobs != 1) fails.Add("blobs=" + s.Blobs);

            if (idle)
            {
                /* 第一层：直径 = 原球直径，球心 = 岛心，原球必须已经不画了 */
                if (Math.Abs(s.DiaDip - d1) > 2.0) fails.Add("dia=" + s.DiaDip.ToString("0.00"));
                if (Math.Abs(s.CxDip - islandCx) > 2.0) fails.Add("cx=" + s.CxDip.ToString("0.00"));
                if (Math.Abs(s.CyDip - islandCy) > 2.0) fails.Add("cy=" + s.CyDip.ToString("0.00"));
                if (s.PurpleOutIsland > 0) fails.Add("leak=" + s.PurpleOutIsland);
                /* 原球隐藏：渐变特征像素应当只剩抗锯齿级别的零星几个 */
                if (s.OrbPixels > 40) fails.Add("orb=" + s.OrbPixels);
                /* 眼睛一个像素都没有 = 球没画出来 */
                if (s.EyePixels < 4) fails.Add("eyes=" + s.EyePixels);
            }
            else
            {
                /* 第二/三层：两倍直径，停在岛旁，岛体轮廓里一个球体像素都不许有 */
                if (Math.Abs(s.DiaDip - d2) > 3.0) fails.Add("dia=" + s.DiaDip.ToString("0.00"));
                if (s.PurpleInIsland > 0) fails.Add("inIsland=" + s.PurpleInIsland);
                if (s.PurpleOutIsland < 200) fails.Add("outside=" + s.PurpleOutIsland);
                if (s.CxDip > _ix - 1 && s.CxDip < _ix + iw + 1
                    && s.CyDip > _iy - 1 && s.CyDip < _iy + ih + 1)
                    fails.Add("centerInsideIsland");
            }

            bool ok = fails.Count == 0;
            steps.Add(ok ? "ok" : tag + ": " + string.Join(",", fails.ToArray()));

            log.AppendLine("── " + tag + (ok ? "  ok" : "  FAIL") + " ──");
            log.AppendLine("  state=" + _state + " petT=" + _petT.ToString("0.000") + "/"
                           + PetLinkTotal.ToString("0.000") + " side=" + _petSide
                           + " slot=" + (_petInUnder ? "under" : "dock")
                           + " opacity=" + _petStage.Shell.Opacity.ToString("0.00"));
            log.AppendLine("  island " + iw.ToString("0.0") + "x" + ih.ToString("0.0")
                           + " at " + _ix.ToString("0.0") + "," + _iy.ToString("0.0")
                           + "  window " + Width.ToString("0.0") + "x" + Height.ToString("0.0"));
            log.AppendLine("  ball dia=" + s.DiaDip.ToString("0.00") + " dip  center="
                           + s.CxDip.ToString("0.00") + "," + s.CyDip.ToString("0.00")
                           + "  blobs=" + s.Blobs);
            log.AppendLine("  pixels pet=" + s.PetPixels + " inIsland=" + s.PurpleInIsland
                           + " outIsland=" + s.PurpleOutIsland
                           + " eyes=" + s.EyePixels + " orbInIsland=" + s.OrbPixels);
            if (s.EyePixels > 0)
                log.AppendLine("  eyes box=" + s.EyeWDip.ToString("0.00") + "x"
                               + s.EyeHDip.ToString("0.00") + " dip  (两个眼睛一起的外框)");
            if (!ok) log.AppendLine("  FAILS: " + string.Join(" | ", fails.ToArray()));

            log.AppendLine(AsciiOf(s));

            json.AppendLine("  \"" + tag + "\": { \"ok\": " + (ok ? "true" : "false")
                            + ", \"state\": \"" + _state + "\""
                            + ", \"petT\": " + _petT.ToString("0.000")
                            + ", \"slot\": \"" + (_petInUnder ? "under" : "dock") + "\""
                            + ", \"side\": " + _petSide.ToString("0")
                            + ", \"islandDip\": [" + iw.ToString("0.0") + "," + ih.ToString("0.0") + "]"
                            + ", \"islandAtDip\": [" + _ix.ToString("0.000") + ","
                            + _iy.ToString("0.000") + "]"
                            + ", \"diameterDip\": " + s.DiaDip.ToString("0.000")
                            + ", \"centerDip\": [" + s.CxDip.ToString("0.000") + ","
                            + s.CyDip.ToString("0.000") + "]"
                            + ", \"petPixels\": " + s.PetPixels
                            + ", \"purpleInIsland\": " + s.PurpleInIsland
                            + ", \"purpleOutIsland\": " + s.PurpleOutIsland
                            + ", \"eyePixels\": " + s.EyePixels
                            + ", \"eyeBoxDip\": [" + s.EyeWDip.ToString("0.000") + ","
                            + s.EyeHDip.ToString("0.000") + "]"
                            + ", \"orbPixels\": " + s.OrbPixels
                            + ", \"blobs\": " + s.Blobs
                            + ", \"fails\": \"" + string.Join(",", fails.ToArray()) + "\" },");

            SavePng(tag);
        }

        /* 逐像素 ASCII：一个字符 = 一个物理像素。
           '#' 球体紫  'o' 眼睛  '+' 其它（岛体/边缘）  '.' 透明
           这台机器没有读图能力，眼睛到底清不清楚只能靠这个和数字说话。 */
        string AsciiOf(PetFrameShot s)
        {
            double dpi = _dpi > 0.1 ? _dpi : 1.0;
            int pw = (int)Math.Round(Math.Max(2, Width) * dpi);
            int ph = (int)Math.Round(Math.Max(2, Height) * dpi);
            RenderTargetBitmap rtb = new RenderTargetBitmap(pw, ph, 96 * dpi, 96 * dpi,
                                                            PixelFormats.Pbgra32);
            rtb.Render(_root);
            int stride = pw * 4;
            byte[] px = new byte[stride * ph];
            rtb.CopyPixels(px, stride, 0);

            int x0 = (int)Math.Floor(s.MinX * dpi) - 6;
            int y0 = (int)Math.Floor(s.MinY * dpi) - 6;
            int x1 = (int)Math.Ceiling(s.MaxX * dpi) + 6;
            int y1 = (int)Math.Ceiling(s.MaxY * dpi) + 6;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > pw - 1) x1 = pw - 1;
            if (y1 > ph - 1) y1 = ph - 1;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("  ascii " + (x1 - x0 + 1) + "x" + (y1 - y0 + 1)
                          + " px  ('#'=ball 'o'=eye '+'=other '.'=empty)");
            for (int y = y0; y <= y1; y++)
            {
                sb.Append("  ");
                for (int x = x0; x <= x1; x++)
                {
                    int o = y * stride + x * 4;
                    int b = px[o], g = px[o + 1], r = px[o + 2], a = px[o + 3];
                    if (a < 8) sb.Append('.');
                    else if (IsPetEye(b, g, r)) sb.Append('o');
                    else if (IsPetPurple(b, g, r)) sb.Append('#');
                    else sb.Append('+');
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        void SavePng(string tag)
        {
            try
            {
                double dpi = _dpi > 0.1 ? _dpi : 1.0;
                int pw = (int)Math.Round(Math.Max(2, Width) * dpi);
                int ph = (int)Math.Round(Math.Max(2, Height) * dpi);
                RenderTargetBitmap rtb = new RenderTargetBitmap(pw, ph, 96 * dpi, 96 * dpi,
                                                                PixelFormats.Pbgra32);
                rtb.Render(_root);
                PngBitmapEncoder enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(rtb));
                using (FileStream fs = File.Create(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "pet-island-" + tag + ".png")))
                    enc.Save(fs);
            }
            catch (Exception ex) { Trace("probe png failed: " + ex.Message); }
        }

        /* 真窗口抓屏：只证明「合成到屏幕上了」，不断言几何。 */
        string ScreenShotForProbe()
        {
            try
            {
                int px = (int)Math.Round(Left * _dpi);
                int py = (int)Math.Round(Top * _dpi);
                int pw = (int)Math.Round(Width * _dpi);
                int ph = (int)Math.Round(Height * _dpi);
                BitmapSource shot = Native.Capture(px, py, pw, ph);
                if (shot == null) return null;
                PngBitmapEncoder enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(shot));
                using (FileStream fs = File.Create(Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory, "pet-island-screen.png")))
                    enc.Save(fs);
                return "screen";
            }
            catch (Exception ex)
            {
                Trace("probe screen shot failed: " + ex.Message);
                return null;
            }
        }
    }
}