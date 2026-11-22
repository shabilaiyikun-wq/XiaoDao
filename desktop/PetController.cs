using System;
using System.Windows;

namespace WinIsland
{
    /* 桌宠控制器。

       之前桌宠只有一个入口：desktop\Program.cs 里 `--pet` 那一支，它 **return** ——
       起了桌宠就不建岛，两者是互斥的进程形态。现在要的是同一个进程里两个窗口：
       岛照旧是那条 52px 的带子，桌宠是自己独立的 220×220 透明窗，各拖各的。

       于是这里收口所有「桌宠」的操作。为什么不在 IslandWindow 里直接 new PetWindow：

         1) 生命周期只有一份。岛菜单能开能关，用户还会反复开反复关；
            如果每个入口各自 new / Close，很容易出现两个桌宠同时在跑 ——
            两个窗口两套 CompositionTarget.Rendering，动画互相追尾。
            这里用单例 + 一个 _pet 字段做闸门，Enable() 第二次调用直接返回 false。
         2) 退出清理只有一份。岛有六条 Shutdown 路径（菜单退出、自检结束、
            抓帧结束、WDA 探针、采样探针、以及窗口被系统关掉），
            逐条去加 Release() 一定会漏。这里在第一次 Enable 时挂一次
            Application.Exit，所有路径都会走到同一个 Release。
         3) 诊断模式（--pet / --pet-demo / --pet-selftest）仍然直接 new PetWindow，
            不经过这里 —— 那三个模式没有 Application 级的 IslandWindow，
            也不该被这里的偏好/退出钩子牵连。

       所有权：桌宠窗口 **不设 Owner**。设了 Owner 之后它会被岛的最小化/关闭带走，
       而且 WPF 会对 owned window 做 z-order 联动 —— 岛是 WS_EX_NOACTIVATE 的
       topmost 小条，被它带着动不是我们要的「桌宠在桌面上自己待着」。 */

    static class PetController
    {
        /* 当前活着的那一个。null = 没开。 */
        static PetWindow _pet;

        /* 桌宠自己的日志落点。和岛共用 --log 那个文件，行首标 P 区分。 */
        static string _logPath = "";

        static bool _exitHooked;

        public static bool IsOn { get { return _pet != null; } }

        public static PetWindow Window { get { return _pet; } }

        /// <summary>已经画了多少帧。给验收用：>0 说明窗口真的在渲染。</summary>
        public static long Frames { get { return _pet == null ? 0 : _pet.Frames; } }

        /// <summary>当前动画状态名（idle/happy/… 或某段参考动画的名字）。没开返回空串。</summary>
        public static string State { get { return _pet == null ? "" : _pet.State; } }

        public static void Init(string logPath)
        {
            _logPath = logPath == null ? "" : logPath;
        }

        /// <summary>
        /// 拉起桌宠。已经在跑就什么都不做 —— 返回值告诉调用方「这次真的建了吗」，
        /// 菜单勾选态要用它，不要拿「调用成功」当「已经打开」。
        /// </summary>
        public static bool Enable()
        {
            if (_pet != null) return false;

            PetWindow pet = new PetWindow(_logPath, false);
            /* 外部把它关掉时（目前只有 Close() 一条路，但别赌）把闸门放开，
               否则 _pet 会指着一个已经关掉的窗口，IsOn 之后永远说 true。 */
            pet.Closed += OnPetClosed;
            _pet = pet;

            HookExit();
            /* Show() 会走 SourceInitialized / Loaded / 第一次 3D 场景构建，
               任何一步抛出来（显卡驱动、D3D 设备丢失都见过）都不能让 _pet 停在
               一个永远不会显示的窗口上：那样 IsOn 恒真、菜单勾着「启用」、
               所有桌宠动作静默无效，而用户眼前什么都没有。失败就回滚闸门。 */
            try
            {
                pet.Show();
            }
            catch (Exception ex)
            {
                Trace("pet show failed: " + ex.GetType().Name + ": " + ex.Message);
                _pet = null;
                pet.Closed -= OnPetClosed;
                try { pet.Close(); } catch { }
                return false;
            }
            Trace("pet enabled frames=0");
            return true;
        }

        /// <summary>关掉桌宠。岛不受影响 —— ShutdownMode 是 OnExplicitShutdown。</summary>
        public static bool Disable()
        {
            PetWindow pet = _pet;
            if (pet == null) return false;

            /* 先摘闸门再关窗：Close() 会同步触发 Closed，那时 _pet 必须已经是 null，
               否则 OnPetClosed 里会把它当成「外部关闭」再走一遍。 */
            _pet = null;
            pet.Closed -= OnPetClosed;
            try { pet.Close(); }
            catch (Exception ex) { Trace("pet close failed: " + ex.Message); }
            Trace("pet disabled");
            return true;
        }

        public static bool Toggle()
        {
            if (IsOn) { Disable(); return false; }
            Enable();
            return true;
        }

        /// <summary>
        /// 关掉桌宠但保留日志落点，供「关了以后还能再开」的菜单路径用。
        /// 名字故意不叫 Reset：它就是 Disable 的语义，只是把偏好也一起清掉由调用方决定。
        /// </summary>
        public static void Release()
        {
            Disable();
            _logPath = "";
        }

        /* ------------------------------------------------------------ 动作 */

        public static bool Poke() { return Apply("poke", delegate(PetWindow p) { p.Poke(); }); }

        public static bool Spin() { return Apply("spin", delegate(PetWindow p) { p.Spin(); }); }

        public static bool SetState(string state)
        {
            return Apply("state " + state, delegate(PetWindow p) { p.SetState(state); });
        }

        /// <summary>播一段参考动画。名字不合法时 PetAnimationRuntime 会抛，这里吞掉并记日志。</summary>
        public static bool PlayClip(string clip)
        {
            return Apply("clip " + clip, delegate(PetWindow p) { p.PlayClip(clip); });
        }

        /// <summary>
        /// 统一的「对活着的桌宠做一件事」入口。没开就返回 false，不偷偷建窗 ——
        /// 建不建窗是菜单「启用」那一项的事，别的动作不该有副作用。
        /// </summary>
        static bool Apply(string what, Action<PetWindow> work)
        {
            PetWindow pet = _pet;
            if (pet == null) return false;
            try
            {
                work(pet);
                Trace(what + " ok");
                return true;
            }
            catch (Exception ex)
            {
                Trace(what + " FAILED: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        /* ------------------------------------------------------------ 清理 */

        static void OnPetClosed(object sender, EventArgs e)
        {
            PetWindow pet = sender as PetWindow;
            if (pet == null || !ReferenceEquals(pet, _pet)) return;
            _pet = null;
            pet.Closed -= OnPetClosed;
            Trace("pet closed externally");
        }

        /* 岛有六条 Shutdown 路径，逐条加 Release 一定漏一个。
           挂 Application.Exit 是唯一能全覆盖的钩子：任何一条路径最终都要经过它。
           只挂一次 —— 反复开关桌宠不该叠出六个 Exit 委托。 */
        static void HookExit()
        {
            if (_exitHooked) return;
            if (Application.Current == null) return;
            _exitHooked = true;
            Application.Current.Exit += delegate
            {
                bool was = _pet != null;
                Disable();
                if (was) Trace("pet released on application exit");
            };
        }

        /* 日志和岛共用同一个文件。桌宠自己那份 Trace 只有构造时给了 logPath 才写，
           这里补的是「控制器视角」的事件（开/关/动作/异常），行首 P 便于 grep。 */
        static void Trace(string s)
        {
            if (string.IsNullOrEmpty(_logPath)) return;
            try
            {
                System.IO.File.AppendAllText(_logPath, "[pet] " + s + "\r\n");
            }
            catch { }
        }
    }
}