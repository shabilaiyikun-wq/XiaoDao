using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinIsland
{
    /* 桌宠与岛同进程共存 —— 验收探针。

       为什么需要它：这一版改的是「一个进程里两个窗口」这件事本身，
       而肉眼只能看到「有个球和一个岛」，看不到
         * 关掉桌宠之后岛还在不在，
         * 反复开关是不是每次只多建了一个窗口（还是两个、三个），
         * 关掉的窗口有没有把每帧回调从静态事件上摘掉，
         * 退出岛的时候桌宠的 Timer / 秒表 / Rendering 委托有没有跟着走，
         * 菜单里 28 段动画点下去究竟是播了那段，还是只是关掉了菜单。

       它走的是**真实的生产路径**：反射调 IslandWindow.OpenMenu() 拿到那一次的
       ContextMenu，再对具体的 MenuItem 发 Click。没有在探针里重写一份接线 ——
       重写一份就等于只测了探针自己。

       编译（不进任何 build.cmd，手动编手动跑，和 probe-petclose.cs 一个套路）：
         见 notes\probes\build-probe-pet-unify.cmd
    */
    static class ProbePetUnify
    {
        static int checks;
        static int failures;

        static IslandWindow island;
        static WeakReference weakPet;
        static PetWindow petRef;
        static string prefsPath;
        static string logPath;

        /* 探针会真的写盘（ui.txt 的 pet 键、window.txt / pet.txt 的位置）。
           跑完必须还原，否则跑一次验收就改掉了用户自己的偏好和岛的位置。 */
        static string backupPrefs;
        static string backupWindowTxt;
        static string backupPetTxt;

        /* ------------------------------------------------------------ 断言 */

        static void Check(bool condition, string name)
        {
            checks++;
            if (!condition) failures++;
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        }

        static void Info(string text)
        {
            Console.WriteLine("INFO " + text);
        }

        /* ------------------------------------------------------------ 反射 */

        static object Field(object target, string name)
        {
            FieldInfo info = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            return info == null ? null : info.GetValue(target);
        }

        static object Call(object target, string name, params object[] args)
        {
            MethodInfo info = target.GetType().GetMethod(name,
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (info == null) throw new MissingMethodException(target.GetType().Name, name);
            return info.Invoke(target, args);
        }

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

        /* 数一棵树里还有几个 PetWindow 挂在 CompositionTarget.Rendering 上。
           -1 = 这一版 WPF 没把处理器放在能反射到的字段里。 */
        static int PetRenderingHandlers()
        {
            try
            {
                bool seen = false;
                int count = 0;
                FieldInfo[] fields = typeof(CompositionTarget).GetFields(
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                for (int i = 0; i < fields.Length; i++)
                {
                    Delegate handler = fields[i].GetValue(null) as Delegate;
                    if (handler == null) continue;
                    seen = true;
                    Delegate[] list = handler.GetInvocationList();
                    for (int j = 0; j < list.Length; j++)
                        if (list[j].Target is PetWindow) count++;
                }
                return seen ? count : -1;
            }
            catch { return -1; }
        }

        /* ------------------------------------------------------------ 菜单 */

        static ContextMenu OpenMenuAndReturn()
        {
            /* _menuOpen 是「菜单已经开着」的闸门；上一轮没关掉会让这次直接返回，
               于是拿到的是旧的 _lastMenu。先确保关掉。 */
            ContextMenu previous = Field(island, "_lastMenu") as ContextMenu;
            if (previous != null && previous.IsOpen)
            {
                previous.IsOpen = false;
                Pump(200);
            }

            Call(island, "OpenMenu");
            Pump(350);
            ContextMenu menu = Field(island, "_lastMenu") as ContextMenu;
            if (menu == null) throw new InvalidOperationException("OpenMenu did not store a ContextMenu");
            return menu;
        }

        static MenuItem FindSubmenu(ContextMenu menu, string header)
        {
            foreach (object o in menu.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi == null) continue;
                if ((mi.Header as string) == header) return mi;
            }
            return null;
        }

        static MenuItem FindItem(MenuItem parent, string header)
        {
            if (parent == null) return null;
            foreach (object o in parent.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi == null) continue;
                if ((mi.Header as string) == header) return mi;
            }
            return null;
        }

        static void Click(MenuItem item)
        {
            if (item == null) throw new InvalidOperationException("menu item not found");
            item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Pump(120);
        }

        /* ------------------------------------------------------------ 偏好 */

        static string ReadPref(string key)
        {
            try
            {
                if (!System.IO.File.Exists(prefsPath)) return null;
                string[] lines = System.IO.File.ReadAllLines(prefsPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    int eq = lines[i].IndexOf('=');
                    if (eq <= 0) continue;
                    if (lines[i].Substring(0, eq).Trim() == key)
                        return lines[i].Substring(eq + 1).Trim();
                }
            }
            catch { }
            return null;
        }

        static string ReadAllTextOrNull(string path)
        {
            try { return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null; }
            catch { return null; }
        }

        /* 把跑之前的内容放回去。null = 这个文件原本不存在，那就删掉，
           恢复成「用户从没设置过」的状态。 */
        static void RestoreFile(string path, string content)
        {
            try
            {
                if (content == null) { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
                else System.IO.File.WriteAllText(path, content);
            }
            catch { }
        }

        static void WritePrefs(string petValue)
        {
            string dir = System.IO.Path.GetDirectoryName(prefsPath);
            if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
            List<string> lines = new List<string>();
            lines.Add("scene=media");
            lines.Add("look=glass");
            lines.Add("art=aurora");
            lines.Add("capture=1");
            lines.Add("transferOut=0");
            if (petValue != null) lines.Add("pet=" + petValue);
            System.IO.File.WriteAllLines(prefsPath, lines.ToArray());
        }

        /* ------------------------------------------------------------ 主流程 */

        [STAThread]
        static int Main(string[] args)
        {
            Native.MakeDpiAware();

            prefsPath = System.IO.Path.Combine(AppDir.Root, "ui.txt");
            backupPrefs = ReadAllTextOrNull(prefsPath);
            backupWindowTxt = ReadAllTextOrNull(AppDir.FilePath("window.txt"));
            backupPetTxt = ReadAllTextOrNull(AppDir.FilePath("pet.txt"));

            /* 起点：桌宠偏好设成「开」，模拟一个从没关过桌宠的正常用户 */
            WritePrefs("1");
            Prefs.Pet = true;

            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            string log = System.IO.Path.GetFullPath("probe-pet-unify.log");
            try { System.IO.File.Delete(log); } catch { }
            logPath = log;
            PetController.Init(log);

            app.Startup += delegate
            {
                try { RunOpenPhase(app); }
                catch (Exception ex)
                {
                    Check(false, "unhandled: " + ex.GetType().Name + ": " + ex.Message);
                    Info(ex.StackTrace);
                    app.Shutdown();
                }
            };

            app.Run();

            /* app.Run() 已经返回 = Dispatcher 关掉了。从这里开始**不能**再 Pump：
               PushFrame 在已关闭的 Dispatcher 上会抛，收尾断言全被吞掉、
               连结论都打不出来。只用线程 Sleep 等 GC。 */
            try { RunPostExitPhase(); }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine("FAIL post-exit phase threw: " + ex.GetType().Name
                                  + ": " + ex.Message);
                Console.WriteLine("     " + ex.StackTrace);
            }

            Console.WriteLine(failures == 0
                ? ("ALL PASS (" + checks + " checks)")
                : ("FAILURES: " + failures + " / " + checks));

            /* 硬退出。Main 返回之后 WPF 那边偶尔还留着一个前台线程（本地化 / 输入法 /
               渲染线程都可能），进程就挂在那儿不退，下一次构建会撞上
               CS0016「输出文件正由另一个进程使用」。探针的结论已经打完了，
               Environment.Exit 保证它一定走。 */
            Console.Out.Flush();
            Environment.Exit(failures == 0 ? 0 : 1);
            return failures == 0 ? 0 : 1;
        }

        static void RunOpenPhase(Application app)
        {
            /* ---- 1. 正常启动：岛 + 桌宠同进程共存 ---- */

            island = new IslandWindow("glass", "media", "aurora", "compact", false, logPath,
                                      false, true, false, false, 0, false, false,
                                      false, false, false,
                                      true);            /* petAuto：模拟「正常启动」 */
            island.Show();
            Pump(1400);                                 /* 建窗、入场、桌宠 Background 建窗 */

            Check(PetController.IsOn, "normal start auto-enables the pet");
            Check(island.IsLoaded && island.IsVisible, "island is loaded and visible");
            Check(Application.Current.Windows.Count >= 2,
                  "two windows in one process (actual " + Application.Current.Windows.Count + ")");

            petRef = PetController.Window;
            Check(petRef != null, "controller exposes the pet window");
            Check(petRef.Owner == null,
                  "pet has no Owner (island cannot drag it around on close)");
            Check(ReferenceEquals(petRef.Owner, island) == false, "pet owner is not the island");

            Pump(900);
            Check(PetController.Frames > 0, "pet renders frames while coexisting ("
                  + PetController.Frames + ")");
            Check(PetController.State.Length > 0, "pet reports an animation state ("
                  + PetController.State + ")");

            /* 岛自己还在跑：命中计时器还在，窗口位置没有被桌宠改动 */
            Check(Field(island, "_hitTimer") != null, "island hit timer still alive");
            double islandLeft = island.Left;
            double islandTop = island.Top;
            Pump(400);
            Check(Math.Abs(island.Left - islandLeft) < 0.001
                  && Math.Abs(island.Top - islandTop) < 0.001,
                  "island position untouched by the pet");

            /* ---- 2. 菜单结构：桌宠子菜单 + 28 段动画 ---- */

            ContextMenu menu = OpenMenuAndReturn();
            MenuItem petMenu = FindSubmenu(menu, "桌宠");
            Check(petMenu != null, "island context menu has a 桌宠 submenu");

            MenuItem toggle = FindItem(petMenu, "启用桌宠");
            Check(toggle != null && toggle.IsChecked, "启用桌宠 is checked while the pet runs");
            Check(FindItem(petMenu, "戳击") != null, "menu has 戳击");
            Check(FindItem(petMenu, "转圈") != null, "menu has 转圈");
            Check(FindItem(petMenu, "恢复随机待机") != null, "menu has 恢复随机待机");

            MenuItem moods = FindItem(petMenu, "表情模式");
            Check(moods != null && moods.Items.Count == 6,
                  "表情模式 lists 6 states (actual " + (moods == null ? -1 : moods.Items.Count) + ")");

            MenuItem all = FindItem(petMenu, "全部动画 (28)");
            Check(all != null, "全部动画 header carries the 28-clip count ("
                  + (all == null ? "missing" : all.Header + "") + ")");
            Check(all != null && all.Items.Count == ReferenceClips.Groups.Length,
                  "全部动画 lists " + ReferenceClips.Groups.Length + " groups");

            int clips = 0;
            List<string> clipNames = new List<string>();
            if (all != null)
            {
                foreach (object o in all.Items)
                {
                    MenuItem group = o as MenuItem;
                    if (group == null) continue;
                    foreach (object c in group.Items)
                    {
                        MenuItem clip = c as MenuItem;
                        if (clip == null) continue;
                        clips++;
                        clipNames.Add(clip.Header as string);
                    }
                }
            }
            Check(clips == 28, "all 28 reference clips are reachable from the menu (actual "
                  + clips + ")");

            /* ---- 3. 菜单选播：每一段都要真的播出去 ---- */

            menu.IsOpen = false;
            Pump(200);

            int played = 0;
            int mismatched = 0;
            string mismatchDetail = "";
            if (all != null)
            {
                foreach (object o in all.Items)
                {
                    MenuItem group = o as MenuItem;
                    if (group == null) continue;
                    foreach (object c in group.Items)
                    {
                        MenuItem clip = c as MenuItem;
                        if (clip == null) continue;
                        string name = clip.Header as string;
                        string expectedState = name.Replace("__", "_");
                        ContextMenu fresh = OpenMenuAndReturn();      /* 菜单每次重建 */
                        MenuItem petMenu2 = FindSubmenu(fresh, "桌宠");
                        MenuItem all2 = FindItem(petMenu2, "全部动画 (28)");
                        MenuItem group2 = FindItem(all2, group.Header as string);
                        MenuItem clip2 = FindItem(group2, name);
                        if (clip2 == null)
                        {
                            mismatched++;
                            mismatchDetail += name + "(not-found) ";
                            continue;
                        }
                        fresh.IsOpen = false;
                        Pump(60);
                        Click(clip2);
                        Pump(160);
                        played++;
                        if (PetController.State != expectedState)
                        {
                            mismatched++;
                            mismatchDetail += expectedState + "->" + PetController.State + " ";
                        }
                        if (!PetController.IsOn) break;
                    }
                }
            }
            Check(played == 28, "clicked every clip menu item (actual " + played + ")");
            Check(mismatched == 0, "every clip click put the runtime in that clip"
                  + (mismatched == 0 ? "" : ": " + mismatchDetail));

            /* ---- 4. 戳击 / 转圈 / 表情模式 ---- */

            ContextMenu m4 = OpenMenuAndReturn();
            MenuItem pet4 = FindSubmenu(m4, "桌宠");
            Click(FindItem(pet4, "戳击"));
            double pokeAt = (double)Field(petRef, "_pokeAt");
            Check(pokeAt >= 0, "戳击 reached the pet runtime (pokeAt=" + pokeAt.ToString("0.00") + ")");

            ContextMenu m5 = OpenMenuAndReturn();
            MenuItem pet5 = FindSubmenu(m5, "桌宠");
            Click(FindItem(pet5, "转圈"));
            double spinAt = (double)Field(petRef, "_spinAt");
            Check(spinAt >= 0, "转圈 reached the pet runtime (spinAt=" + spinAt.ToString("0.00") + ")");

            ContextMenu m6 = OpenMenuAndReturn();
            MenuItem pet6 = FindSubmenu(m6, "桌宠");
            Click(FindItem(pet6, "表情模式"));                 /* 子菜单本身不触发动作 */
            MenuItem moods6 = FindItem(pet6, "表情模式");
            MenuItem happy = FindItem(moods6, "开心 happy");
            Check(happy != null, "表情模式 has 开心 happy");
            Click(happy);
            Check(PetController.State == "happy", "表情模式 -> happy (actual '"
                  + PetController.State + "')");

            ContextMenu m7 = OpenMenuAndReturn();
            MenuItem pet7 = FindSubmenu(m7, "桌宠");
            Check(FindItem(pet7, "启用桌宠").IsChecked, "启用桌宠 still checked after state change");
            Click(FindItem(pet7, "恢复随机待机"));
            Check(PetController.State == "idle", "恢复随机待机 -> idle (actual '"
                  + PetController.State + "')");

            /* ---- 5. 反复开关：每次只多一个窗口，关掉不留每帧回调 ---- */

            int cycles = 5;
            bool everyCycleClean = true;
            bool windowCountStable = true;
            string cycleDetail = "";
            int baselineWindows = Application.Current.Windows.Count;
            for (int i = 0; i < cycles; i++)
            {
                PetWindow closing = PetController.Window;

                ContextMenu cm = OpenMenuAndReturn();
                MenuItem pm = FindSubmenu(cm, "桌宠");
                Click(FindItem(pm, "启用桌宠"));               /* 关 */
                Pump(320);

                bool offOk = !PetController.IsOn && PetController.Window == null;

                /* 关掉的那个实例必须自证清干净了：_hit / _demo 置空、_animating 归 false
                   （后者和 CompositionTarget.Rendering -= OnFrame 是同一个赋值点，
                   所以它就是「每帧回调已摘除」能拿到的最直接证据 —— 本机 WPF 的
                   CompositionTarget 没有可反射的处理器字段，只能从这里看）。 */
                bool closedClean = closing != null
                                   && Field(closing, "_hit") == null
                                   && Field(closing, "_demo") == null
                                   && !(Field(closing, "_animating") is bool
                                        && (bool)Field(closing, "_animating"))
                                   && !closing.IsVisible;

                ContextMenu cm2 = OpenMenuAndReturn();
                MenuItem pm2 = FindSubmenu(cm2, "桌宠");
                MenuItem tg = FindItem(pm2, "启用桌宠");
                bool uncheckedWhileOff = tg != null && !tg.IsChecked;
                Click(tg);                                     /* 开 */
                Pump(520);

                bool onOk = PetController.IsOn
                            && PetController.Window != null
                            && PetController.Window != closing;    /* 每次都是新窗口 */
                if (PetController.Window != null) petRef = PetController.Window;

                int windowsNow = Application.Current.Windows.Count;
                bool stable = windowsNow <= baselineWindows;    /* 关掉的那些不该还活着 */

                Pump(240);
                bool rendering = PetController.Frames > 0;

                if (!(offOk && closedClean && uncheckedWhileOff && onOk && stable && rendering))
                {
                    everyCycleClean = false;
                    cycleDetail += "[cycle " + i + " off=" + offOk + " clean=" + closedClean
                                   + " unchk=" + uncheckedWhileOff + " on=" + onOk
                                   + " win=" + windowsNow + "/" + baselineWindows
                                   + " frames=" + PetController.Frames + "] ";
                }
                if (!stable) windowCountStable = false;
            }
            Check(everyCycleClean, "5 on/off cycles all clean: pet closed, resources dropped, "
                  + "preference unchecked, reopened as a NEW window"
                  + (everyCycleClean ? "" : ": " + cycleDetail));
            Check(windowCountStable, "window count returns to baseline after every close ("
                  + baselineWindows + ")");

            /* 同一时刻只能有一路每帧回调。这个 WPF 版本没把处理器放进可反射的字段，
               -1 就只能报信息 —— 上面每轮 closedClean 已经覆盖了「摘除了」这件事。 */
            int liveHandlers = PetRenderingHandlers();
            if (liveHandlers < 0)
                Info("CompositionTarget handlers not reflectable on this WPF; "
                     + "detachment covered by per-cycle _animating check");
            else
                Check(liveHandlers == 1, "exactly one pet frame handler while running (actual "
                      + liveHandlers + ")");

            /* ---- 6. 关闭桌宠不退出岛 ---- */

            ContextMenu m8 = OpenMenuAndReturn();
            Click(FindItem(FindSubmenu(m8, "桌宠"), "启用桌宠"));   /* 关 */
            Pump(600);
            Check(!PetController.IsOn, "pet is off after the toggle");
            Check(island.IsVisible, "island survives closing the pet");
            Check(Field(island, "_hitTimer") != null, "island hit timer survives closing the pet");
            Check(Application.Current.Windows.Count >= 1, "application still has windows");

            /* ---- 7. 偏好：开关写盘、位置分开存 ---- */

            Check(ReadPref("pet") == "0", "closing the pet persisted pet=0 (ui.txt: "
                  + ReadPref("pet") + ")");

            ContextMenu m9 = OpenMenuAndReturn();
            Click(FindItem(FindSubmenu(m9, "桌宠"), "启用桌宠"));   /* 开 */
            Pump(520);
            Check(PetController.IsOn, "pet is on again");
            Check(ReadPref("pet") == "1", "enabling the pet persisted pet=1 (ui.txt: "
                  + ReadPref("pet") + ")");

            Prefs.Pet = true;
            Prefs.Load();
            Check(Prefs.Pet, "Prefs.Load restores pet=true from disk");

            /* 位置各存一份：岛 window.txt / 桌宠 pet.txt，互不覆盖 */
            Call(island, "SavePosition");
            Pump(160);
            string islandPos = AppDir.FilePath("window.txt");
            string petPos = AppDir.FilePath("pet.txt");
            Check(System.IO.File.Exists(islandPos), "island position in window.txt");
            Check(System.IO.File.Exists(petPos), "pet position in pet.txt");
            Check(islandPos != petPos, "positions are stored separately");

            /* ---- 8. 退出岛要释放桌宠 ---- */

            petRef = PetController.Window;
            weakPet = new WeakReference(petRef);
            Info("pet frames before quit: " + PetController.Frames);

            ContextMenu m10 = OpenMenuAndReturn();
            MenuItem quit = null;
            foreach (object o in m10.Items)
            {
                MenuItem mi = o as MenuItem;
                if (mi == null) continue;
                string h = mi.Header as string;
                if (h != null && h.IndexOf("退出") >= 0) { quit = mi; break; }
            }
            Check(quit != null, "menu has 退出 WinIsland");
            m10.IsOpen = false;
            Pump(80);
            Click(quit);                                   /* -> BeginShutdown -> Shutdown */
        }

        /* app.Run() 返回之后才跑：这时候进程还活着，但岛已经走了。
           能在这里看到静态状态，正好用来证明「退出时真的释放了」。 */
        static void RunPostExitPhase()
        {
            Check(!PetController.IsOn, "pet released on island quit (IsOn=false)");
            Check(PetController.Window == null, "controller dropped the pet reference");

            int handlers = PetRenderingHandlers();
            if (handlers < 0) Info("CompositionTarget handlers not reflectable, skipped");
            else Check(handlers == 0, "no pet frame handler left after quit (actual "
                       + handlers + ")");

            if (petRef != null)
            {
                Check(Field(petRef, "_hit") == null, "pet hit timer stopped and dropped");
                Check(Field(petRef, "_demo") == null, "pet demo timer dropped");
                object animating = Field(petRef, "_animating");
                Check(!(animating is bool && (bool)animating), "pet Rendering handler detached");
                System.Diagnostics.Stopwatch clock =
                    Field(petRef, "_clock") as System.Diagnostics.Stopwatch;
                Check(clock == null || !clock.IsRunning, "pet stopwatch stopped");
                Check(!petRef.IsVisible, "pet window is no longer visible");
            }

            /* 硬证据：关掉的窗口要能被回收。Rendering 上留着委托的话，
               静态事件就是 GC 根，窗口连同渲染树一直活着。 */
            petRef = null;
            island = null;
            if (weakPet != null)
            {
                for (int i = 0; i < 6 && weakPet.IsAlive; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    System.Threading.Thread.Sleep(80);   /* 不能 Pump：Dispatcher 已关闭 */
                }
                if (weakPet.IsAlive)
                    Info("closed pet still reachable after GC (WPF may cache a closed Window; "
                         + "handler detachment asserted above)");
                else
                    Info("closed pet collected: no static root holds it");
            }

            /* ui.txt 里 pet=1（退出不该把用户的「想开桌宠」改掉） */
            Check(ReadPref("pet") == "1", "quit kept the user's pet=1 preference (ui.txt: "
                  + ReadPref("pet") + ")");

            /* 还原：探针写过的三个文件全部放回跑之前的样子。
               不还原的话，跑一次验收就把用户自己的偏好和岛的落点改掉了。 */
            RestoreFile(prefsPath, backupPrefs);
            RestoreFile(AppDir.FilePath("window.txt"), backupWindowTxt);
            RestoreFile(AppDir.FilePath("pet.txt"), backupPetTxt);
            Info("ui.txt / window.txt / pet.txt restored to their pre-probe contents");
        }
    }
}
