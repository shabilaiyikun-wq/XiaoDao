using System;
using System.Windows.Media.Media3D;

namespace WinIsland
{
    static class AnimationTests
    {
        static int failures;

        static void Check(bool condition, string name)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
            if (!condition) failures++;
        }

        static PetFrame Idle(double time)
        {
            PetInput input = new PetInput();
            input.Time = time;
            input.PokeAge = input.SpinAge = -1;
            return PetAnim.Sample(input);
        }

        static MeshGeometry3D Body(PetFrame frame)
        {
            frame.MorphAmount = 1;
            Model3DGroup scene = Pet3D.Scene(frame);
            for (int i = 0; i < scene.Children.Count; i++)
            {
                GeometryModel3D model = scene.Children[i] as GeometryModel3D;
                if (model != null) return (MeshGeometry3D)model.Geometry;
            }
            throw new InvalidOperationException("body model missing");
        }

        [STAThread]
        static int Main(string[] args)
        {
            /* 缓动曲线必须与上游 constants.ease 逐位相同：
               ease.nr = [0.42, 0, 0.58, 1]（片段插值）、ease.dx = [0.40, 0, 0.20, 1]（桥接）。
               这条断言盯的是「系数有没有被顺手改掉」，不是某个具体采样值。 */
            Check(Math.Abs(PetEase.Smooth.X1 - 0.42) < 1e-12 && Math.Abs(PetEase.Smooth.Y1 - 0) < 1e-12
                  && Math.Abs(PetEase.Smooth.X2 - 0.58) < 1e-12 && Math.Abs(PetEase.Smooth.Y2 - 1) < 1e-12,
                  "smooth easing matches upstream ease.nr");
            Check(Math.Abs(PetEase.Bridge.X1 - 0.40) < 1e-12 && Math.Abs(PetEase.Bridge.Y1 - 0) < 1e-12
                  && Math.Abs(PetEase.Bridge.X2 - 0.20) < 1e-12 && Math.Abs(PetEase.Bridge.Y2 - 1) < 1e-12,
                  "bridge easing matches upstream ease.dx");

            /* Bezier(x) 的含义是「先由 x 反解参数 t，再取该 t 的 y」。
               所以正确的判定不能写死某个 y 值（那会把曲线锁死），而要摊开验证：
               对任意曲线，用 x(t)=X 反解出的 t 代回去，y 必须等于 Bezier(X)；
               并且必须单调、端点精确。若实现直接拿 x 当 t 用，
               y(0.5) 会等于参数 t=0.5 处的 y，和反解结果对不上。 */
            PetEase[] curves = new PetEase[] { PetEase.Smooth, PetEase.Bridge };
            bool solverOk = true;
            for (int c = 0; c < curves.Length; c++)
            {
                PetEase e = curves[c];
                for (int i = 1; i < 20; i++)
                {
                    double x = i / 20.0;
                    double y = AnimationMath.Bezier(x, e);
                    /* 用二分独立反解 t：x(t) = x */
                    double lo = 0, hi = 1;
                    for (int k = 0; k < 60; k++)
                    {
                        double mid = (lo + hi) / 2;
                        double u = 1 - mid;
                        double xt = 3 * u * u * mid * e.X1 + 3 * u * mid * mid * e.X2 + mid * mid * mid;
                        if (xt < x) lo = mid; else hi = mid;
                    }
                    double t = (lo + hi) / 2, v = 1 - t;
                    double yExpected = 3 * v * v * t * e.Y1 + 3 * v * t * t * e.Y2 + t * t * t;
                    if (Math.Abs(y - yExpected) > 1e-4) solverOk = false;
                    if (y < -1e-6 || y > 1 + 1e-6) solverOk = false;
                }
                if (Math.Abs(AnimationMath.Bezier(0, e)) > 1e-6) solverOk = false;
                if (Math.Abs(AnimationMath.Bezier(1, e) - 1) > 1e-6) solverOk = false;
            }
            Check(solverOk, "cubic Bezier solves the x progress before y (curve agnostic)");
            PetInput input = new PetInput();
            input.Time = 3;
            input.GazeX = 0.3; input.GazeY = -0.2; input.HasGaze = true;
            input.PokeAge = input.SpinAge = -1;
            PetFrame before = PetAnim.Sample(input);
            input.PokeAge = 0.00001;
            PetFrame after = PetAnim.Sample(input);
            Check(Math.Abs(before.SquashX - after.SquashX) < 0.02,
                "poke starts continuously instead of teleporting the body");

            MeshGeometry3D shut = Pet3D.EyeMesh(0, 0, 0.195, 0.025);
            Check(shut.Bounds.SizeY < 0.03 && shut.Bounds.SizeX > 0.19,
                "closed capsule becomes a thin horizontal line");

            PetFrame sphere = Idle(0);
            sphere.ShapeIndex = 0;
            PetFrame square = sphere;
            square.ShapeIndex = 1;
            MeshGeometry3D a = Body(sphere), b = Body(square);
            double change = 0;
            for (int i = 0; i < a.Positions.Count; i++)
                change = Math.Max(change, (a.Positions[i] - b.Positions[i]).Length);
            Check(change > 0.05, "morph changes the rendered body mesh");

            PetAnimationRuntime largeStep = new PetAnimationRuntime(17);
            PetAnimationRuntime smallSteps = new PetAnimationRuntime(17);
            largeStep.Advance(10.0 / 120);
            for (int i = 0; i < 10; i++) smallSteps.Advance(1.0 / 120);
            Check(Math.Abs(largeStep.Frame.GazeX - smallSteps.Frame.GazeX) < 1e-9
                && Math.Abs(largeStep.Frame.SquashX - smallSteps.Frame.SquashX) < 1e-9,
                "fixed-step animation is independent of render delta grouping");

            PetAnimationRuntime switched = new PetAnimationRuntime(17);
            switched.Advance(0.25);
            double gazeBeforeSwitch = switched.Frame.GazeX;
            switched.SetState("thinking");
            switched.Advance(1.0 / 120);
            Check(Math.Abs(switched.Frame.GazeX - gazeBeforeSwitch) < 0.03,
                "state changes bridge from the current pose");

            PetAnimationRuntime poked = new PetAnimationRuntime(17);
            PetFrame resting = poked.Frame;
            poked.Poke(); poked.Advance(1.0 / 120);
            Check(poked.Frame.State == "poked" && poked.Frame.SquashY < resting.SquashY,
                "poke impulse compresses the body without an initial jump");

            PetFacePoseChanges();
            ReferenceClipsLoad();
            return failures == 0 ? 0 : 1;
        }

        static void PetFacePoseChanges()
        {
            PetEyePose neutral = PetFaces.Eye(PetExpression.Neutral, -1);
            PetEyePose happy = PetFaces.Eye(PetExpression.Happy, -1);
            PetEyePose winked = PetFaces.Eye(PetExpression.Wink, 1);
            Check(Math.Abs(neutral.Height - happy.Height) > 0.2
                && Math.Abs(winked.Height - neutral.Height) > 0.2,
                "authored expressions change eye descriptors");
        }

        static void ReferenceClipsLoad()
        {
            Check(ReferenceClips.All.Length == 28,
                "all 28 reference clip recipes load");
            Check(ReferenceClips.Find("Working_F") != null
                && ReferenceClips.Find("Streaming_C") != null
                && ReferenceClips.Find("Rotation_B") != null,
                "reference clip lookup includes every animation family");

            int menuEntries = 0;
            foreach (PetClipGroup group in ReferenceClips.Groups) menuEntries += group.Clips.Length;
            bool allFramesRender = menuEntries == ReferenceClips.All.Length;
            foreach (PetClip item in ReferenceClips.All)
            {
                PetFrame frame = item.Sample(item.Duration * 0.5);
                if (double.IsNaN(frame.Pitch) || double.IsInfinity(frame.Pitch)
                    || double.IsNaN(frame.Yaw) || double.IsInfinity(frame.Yaw)
                    || double.IsNaN(frame.Roll) || double.IsInfinity(frame.Roll)
                    || double.IsNaN(frame.Zoom) || double.IsInfinity(frame.Zoom)
                    || Pet3D.Scene(frame).Children.Count < 3)
                    allFramesRender = false;
            }
            Check(allFramesRender, "all reference clips have menu entries and renderable midpoints");

            PetClip clip = ReferenceClips.Find("Done_A");
            PetFrame start = clip.Sample(0);
            PetFrame middle = clip.Sample(0.2);
            Check(Math.Abs(clip.Duration - 3.7) < 0.001 && start.SquashX == 1
                && middle.SquashX > 1.01 && middle.SquashY < 1,
                "reference keyframes preserve timing and squash values");

            PetAnimationRuntime runtime = new PetAnimationRuntime(17);
            runtime.PlayClip("Bodymoves_A");
            runtime.Advance(1.0 / 120);
            Check(runtime.Frame.State == "Bodymoves_A",
                "each reference clip can be selected directly");
        }
    }
}
