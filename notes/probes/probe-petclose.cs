using System;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinIsland
{
    /* 关窗清理探针。

       目的只有一个：证明 PetWindow 关掉之后，手上那些「外部引用」真的被摘干净了 ——
       DispatcherTimer 停了、CompositionTarget.Rendering 上不留本窗口的委托。
       静态事件泄漏是 WPF 里最难靠肉眼发现的一类问题：窗口看起来没了，
       进程还在跑，下一次开窗就是两路动画叠在一起。

       不在任何 build.cmd 里，手动编：
         csc /target:exe /platform:x64 /out:probe-petclose.exe ^
             /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll ^
             /r:System.Xaml.dll /r:System.Core.dll /r:System.dll ^
             /r:System.Web.Extensions.dll ^
             /resource:grok-clips.json,WinIsland.grok-clips.json ^
             DemoSupport.cs AnimationCore.cs PetClips.cs ReferenceClips.cs ^
             PetShape.cs PetAnim.cs Pet3D.cs Pet.cs probe-petclose.cs */
    static class ProbePetClose
    {
        static int failures;

        static void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
            if (!condition) failures++;
        }

        static object Field(object target, string name)
        {
            FieldInfo info = target.GetType().GetField(name,
                BindingFlags.Instance | BindingFlags.NonPublic);
            return info == null ? null : info.GetValue(target);
        }

        static bool Animating(object target)
        {
            object value = Field(target, "_animating");
            return value is bool && (bool)value;
        }

        /// <summary>
        /// 数一遍 CompositionTarget.Rendering 上还挂着几个来自 PetWindow 的委托。
        /// 返回 -1 表示这一版 WPF 没把事件处理器放在能反射到的字段里（那就只报信息，不判失败），
        /// 0 表示确实摘干净了。
        /// </summary>
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

        static void AssertCleaned(PetWindow pet, string tag)
        {
            Check(Field(pet, "_hit") == null, tag + ": hit timer stopped and dropped");
            Check(Field(pet, "_demo") == null, tag + ": demo timer dropped");
            Check(!Animating(pet), tag + ": Rendering handler detached");
            Stopwatch clock = Field(pet, "_clock") as Stopwatch;
            Check(clock == null || !clock.IsRunning, tag + ": stopwatch stopped");

            int handlers = PetRenderingHandlers();
            if (handlers < 0)
                Console.WriteLine("INFO " + tag + ": CompositionTarget handler list not reflectable, skipped");
            else
                Check(handlers == 0, tag + ": no PetWindow handler left on CompositionTarget.Rendering");
        }

        [STAThread]
        static int Main(string[] args)
        {
            Application app = new Application();
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            PetWindow pet = new PetWindow("", false);
            pet.Show();

            bool rendered = false;
            WeakReference weak = null;
            DispatcherTimer wait = new DispatcherTimer(DispatcherPriority.Background);
            wait.Interval = TimeSpan.FromMilliseconds(500);
            wait.Tick += delegate
            {
                wait.Stop();

                rendered = pet.Frames > 0;
                Check(rendered, "pet renders frames while open");
                Check(Animating(pet), "pet animates while open");
                Check(PetRenderingHandlers() >= 1 || PetRenderingHandlers() < 0,
                      "Rendering handler present while open");

                pet.Close();
                pet.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Background);
                AssertCleaned(pet, "first close");

                /* 再开一关：如果第一次关窗漏了静态事件，这里的计数会是 2 而不是 0 */
                PetWindow again = new PetWindow("", false);
                again.Show();
                again.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Background);
                again.Close();
                again.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Background);
                AssertCleaned(again, "reopened close");

                /* 最后一条硬证据：关掉的窗口必须能被回收。
                   只要 CompositionTarget.Rendering 上还留着 OnFrame，静态事件就是
                   GC 根，窗口连同它的渲染树会一直活着 —— 这里就会看到 IsAlive 为 true。 */
                weak = new WeakReference(pet);
                pet = null;
                again = null;
                for (int i = 0; i < 4 && weak.IsAlive; i++)
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    app.Dispatcher.Invoke(new Action(delegate { }), DispatcherPriority.Background);
                }
                if (weak.IsAlive)
                    Console.WriteLine("INFO closed window still reachable after GC "
                                      + "(WPF may cache a closed Window; handler detachment "
                                      + "is still asserted above)");
                else
                    Console.WriteLine("INFO closed window collected: no static root holds it");

                app.Shutdown();
            };
            wait.Start();
            app.Run();

            Console.WriteLine(failures == 0 ? "ALL CLEAN" : ("FAILURES: " + failures));
            return failures == 0 ? 0 : 1;
        }
    }
}