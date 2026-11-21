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
            GazeTests();
            EyeGeometryTests();
            HoldTests();
            ReferenceClipsLoad();
            return failures == 0 ? 0 : 1;
        }

        /* ================================================================ 注视

           移植自 Bencho eye tracker 的四件事，逐条钉住：
           方向归一化 / tanh 距离饱和 / 双轴弹簧 / 双眼共享 yaw-pitch。
           全部走生产用的 PetGaze / PetGazeTrack，不另写一份逻辑。 */
        static void GazeTests()
        {
            /* 岛内停靠那颗球的半径（DIP）：16.8 直径 —— 小尺寸就是这个量级。 */
            const double BallR = 8.4;

            /* 1) 方向归一化。上游的 far 乘的是**单位向量**，所以远处给的
                  是同一个方向、幅值到顶。逐轴钳制会把 3:4 掰成 1:1，
                  这正是要避免的（上游注释里第一件事）。 */
            double gx1, gy1, gx2, gy2, gx3, gy3;
            PetGaze.Follow(300, -400, BallR, out gx1, out gy1);
            PetGaze.Follow(600, -800, BallR, out gx2, out gy2);
            PetGaze.Follow(1200, -1600, BallR, out gx3, out gy3);
            Check(Math.Abs(gx1 - gx2) < 1e-9 && Math.Abs(gy1 - gy2) < 1e-9
                  && Math.Abs(gx1 - gx3) < 1e-9 && Math.Abs(gy1 - gy3) < 1e-9,
                  "gaze reaches one fixed direction at any distance");
            Check(Math.Abs(gy1 / gx1 - (-400.0 / 300.0)) < 1e-9
                  && Math.Abs(Math.Sqrt(gx1 * gx1 + gy1 * gy1) - PetGaze.MaxAmount) < 1e-9,
                  "gaze keeps the true direction instead of clamping each axis");

            /* 2) tanh 距离饱和：单调、连续、没有硬截断，近处按比例、远处收住。 */
            bool monotone = true;
            double previous = -1;
            for (int i = 0; i <= 40; i++)
            {
                double a, b;
                PetGaze.Follow(i * 5.0, 0, BallR, out a, out b);
                if (Math.Abs(b) > 1e-12) monotone = false;
                if (i > 0 && a < previous - 1e-12) monotone = false;
                previous = a;
            }
            Check(monotone, "gaze grows monotonically with distance and stays on axis");

            double near, unused;
            PetGaze.Follow(BallR * 0.5, 0, BallR, out near, out unused);
            Check(near > 0.15 && near < 0.75,
                  "gaze is proportional near the ball instead of snapping to the cap");

            double onEdge;
            PetGaze.Follow(BallR, 0, BallR, out onEdge, out unused);
            Check(onEdge > 0.60 && onEdge < PetGaze.MaxAmount,
                  "a file on the pet's edge already turns the eyes most of the way");

            /* 3) reach 跟着球走：同样的「相对距离」必须给同样的注视量。
                  否则停靠时那 16.8 DIP 的小球会「几乎不转」（reach 写死 60px 的后果）。 */
            double s1x, s1y, s2x, s2y;
            PetGaze.Follow(30, 20, BallR, out s1x, out s1y);
            PetGaze.Follow(60, 40, BallR * 2, out s2x, out s2y);
            Check(Math.Abs(s1x - s2x) < 1e-9 && Math.Abs(s1y - s2y) < 1e-9,
                  "gaze saturation scales with the ball, so pet size does not change the feel");

            /* 4) 屏幕 y 向下，脸的 y 向上：这一处翻转只允许存在于 PetGaze.FollowScreen。 */
            double upX, upY;
            PetGaze.FollowScreen(0, -20, BallR, PetGaze.DefaultFollow, out upX, out upY);
            Check(Math.Abs(upX) < 1e-9 && upY > 0.2,
                  "a cursor above the pet looks up (screen y is inverted once, here)");

            /* 5) 双轴弹簧：不能跳、必须到位、过冲有限。 */
            PetGazeTrack track = new PetGazeTrack();
            track.Snap(0, 0);
            track.Aim(1.0, 0);
            double worstStep = 0, peak = -9;
            for (int i = 0; i < 240; i++)
            {
                double before = track.X;
                track.Advance(1.0 / 120);
                double step = Math.Abs(track.X - before);
                if (step > worstStep) worstStep = step;
                if (track.X > peak) peak = track.X;
            }
            Check(worstStep < 0.07,
                  "spring moves at most " + (worstStep * 100).ToString("0.0")
                  + "% of the gaze range per 1/120 s frame (a sweep, not a jump)");
            Check(Math.Abs(track.X - 1.0) < 0.001, "spring settles exactly on its target");
            Check(peak < 1.08, "spring overshoot stays under 8%, so the eye never rebounds visibly");

            /* 6) 目标瞬间跨到另一侧（快速跨越目标）：不许一帧跨过去。 */
            PetGazeTrack swing = new PetGazeTrack();
            swing.Snap(1.0, 0);
            swing.Aim(-1.0, 0);
            double worstSwing = 0;
            int framesToArrive = -1;
            for (int i = 0; i < 240; i++)
            {
                double before = swing.X;
                swing.Advance(1.0 / 120);
                double step = Math.Abs(swing.X - before);
                if (step > worstSwing) worstSwing = step;
                if (framesToArrive < 0 && swing.X < -0.98) framesToArrive = i + 1;
            }
            Check(worstSwing < 0.16,
                  "a target that crosses sides is swept, not jumped (worst frame "
                  + (worstSwing * 100).ToString("0.0") + "% of the 2.0 swing)");
            Check(framesToArrive > 6 && framesToArrive < 90,
                  "the crossing takes a visible moment, then arrives ("
                  + framesToArrive + " frames at 120 Hz)");

            /* 7) 静止时不能自己漂。 */
            PetGazeTrack idle = new PetGazeTrack();
            for (int i = 0; i < 240; i++) idle.Advance(1.0 / 120);
            Check(idle.X == 0 && idle.Y == 0 && !idle.Moving,
                  "a spring that was never aimed stays exactly still");

            /* 8) 共用一组 yaw / pitch：转向之外没有第二个自由度。
                  同一注视量下，两只眼睛的球面点必须是各自静止位置绕同一个
                  yaw / pitch 转过去的结果（PetGaze.RotatePoint 是那份旋转）。 */
            double yaw, pitch;
            PetGaze.Turn(Pet3D.GazeShift(1.0), 0, out yaw, out pitch);
            Check(Math.Abs(yaw - Math.Asin(Pet3D.GazeShift(1.0))) < 1e-9
                  && Math.Abs(pitch) < 1e-12,
                  "yaw follows asin(gaze) and pitch stays level on a pure sideways gaze");
            double limitYaw, limitPitch;
            PetGaze.Turn(5.0, -5.0, out limitYaw, out limitPitch);
            Check(Math.Abs(limitYaw - Math.Asin(PetGaze.AsinLimit)) < 1e-9
                  && Math.Abs(limitPitch - Math.Asin(PetGaze.AsinLimit)) < 1e-9,
                  "over-range gaze is clamped short of asin's singularity");
            double rx, ry, rz;
            PetGaze.RotatePoint(0, 0, 1, 0, 0, out rx, out ry, out rz);
            Check(Math.Abs(rx) < 1e-12 && Math.Abs(ry) < 1e-12 && Math.Abs(rz - 1) < 1e-12,
                  "the sphere rotation is the identity at rest");
            PetGaze.RotatePoint(0, 0, 1, Math.PI / 2, 0, out rx, out ry, out rz);
            Check(Math.Abs(rx - 1) < 1e-9 && rz < 1e-9,
                  "a quarter yaw turns the front onto +x");
        }

        /* ============================================================ 眼睛几何

           眼睛是「画在球面上的贴片」：锚点绕共用 yaw / pitch 转，
           贴片是锚点切平面上的圆角矩形，每个顶点再压回 1.015 球面。
           所以压窄与遮挡全部由正交投影 + z-buffer 完成 —— 不叠模拟缩放。 */
        static void EyeGeometryTests()
        {
            PetFrame face = new PetFrame();
            face.Expression = PetExpression.Neutral;
            face.SquashX = 1; face.SquashY = 1; face.Zoom = 1;

            MeshGeometry3D left, right;

            /* 1) 正视：两只眼睛一样宽、一样深、对称。 */
            face.GazeX = 0; face.GazeY = 0;
            Eyes(face, out left, out right);
            double frontLeftW = left.Bounds.SizeX, frontRightW = right.Bounds.SizeX;
            double frontRightH = right.Bounds.SizeY;
            double frontRightY = Centroid(right).Y;
            Check(Math.Abs(frontLeftW - frontRightW) < 1e-9
                  && Math.Abs(Centroid(left).X + Centroid(right).X) < 1e-9,
                  "a straight-ahead face is symmetric and both eyes are the same width");

            /* 2) 侧看：两只眼睛都朝同一侧走，但近轮廓那只**更窄更深** ——
                  这就是「贴在球面上转」而不是「两个矩形平移」的判据。 */
            face.GazeX = 1.0; face.GazeY = 0;
            Eyes(face, out left, out right);
            Point3D lc = Centroid(left), rc = Centroid(right);
            Check(rc.X > lc.X && lc.X > 0,
                  "both eyes swing toward the same side (shared yaw), the near one further");
            Check(rc.Z < lc.Z,
                  "the eye heading for the rim sits deeper on the ball");
            Check(right.Bounds.SizeX < left.Bounds.SizeX * 0.80,
                  "the rim eye is projected narrower by real geometry ("
                  + (right.Bounds.SizeX / left.Bounds.SizeX * 100).ToString("0.0") + "% of the far eye)");
            Check(right.Bounds.SizeX < frontRightW * 0.85,
                  "the same eye is narrower when turned than when facing front");
            Check(Math.Abs(right.Bounds.SizeY - frontRightH) < 0.06,
                  "turning narrows the eye along the turn only: height "
                  + frontRightH.ToString("0.000") + " -> " + right.Bounds.SizeY.ToString("0.000"));

            /* 2b) 纵向符号：片段里 +GazeY 是「往上看」，几何必须同向。 */
            face.GazeX = 0; face.GazeY = 1.0;
            Eyes(face, out left, out right);
            double upY2 = Centroid(right).Y;
            Check(upY2 > frontRightY + 0.15,
                  "a positive GazeY lifts the eyes on screen (" + frontRightY.ToString("0.000")
                  + " -> " + upY2.ToString("0.000") + ")");

            /* 3) 贴片不变量：每个顶点都在 1.015 球面上、都在正面圆盘里。
                  这条就是「不裂缝、不悬浮、不穿模」的几何保证。 */
            face.GazeX = 1.0; face.GazeY = 1.0;
            Eyes(face, out left, out right);
            Check(OnSurface(left) && OnSurface(right),
                  "every eye vertex stays exactly on the 1.015 surface");
            Check(InsideDisc(left) && InsideDisc(right),
                  "no eye vertex leaves the 0.97 front disc at full gaze");

            /* 4) 四个对角极限 + 每一个表情：同一组不变量都要成立。 */
            bool allExtremesSafe = true;
            bool allExpressionsDiffer = true;
            double previousWidth = -1;
            Array expressions = Enum.GetValues(typeof(PetExpression));
            foreach (PetExpression expression in expressions)
            {
                face.Expression = expression;
                double width = 0;
                for (int sx = -1; sx <= 1; sx += 2)
                {
                    for (int sy = -1; sy <= 1; sy += 2)
                    {
                        face.GazeX = sx; face.GazeY = sy;
                        Eyes(face, out left, out right);
                        if (!OnSurface(left) || !OnSurface(right)
                            || !InsideDisc(left) || !InsideDisc(right)) allExtremesSafe = false;
                    }
                }
                face.GazeX = 0; face.GazeY = 0;
                Eyes(face, out left, out right);
                width = left.Bounds.SizeX;
                if (previousWidth > 0 && Math.Abs(width - previousWidth) > 1e-6)
                    allExpressionsDiffer = true;
                previousWidth = width;
            }
            Check(allExtremesSafe,
                  "every expression survives all four diagonal extremes without clipping");
            Check(allExpressionsDiffer, "expressions still change the eye geometry");

            /* 5) 眨眼压到最扁时几何仍然成立（高度极小但不能退化成缝）。 */
            face.Expression = PetExpression.Neutral;
            face.GazeX = 0; face.GazeY = 0;
            face.Lid = 0.99;
            Eyes(face, out left, out right);
            Check(left.Positions.Count > 20 && OnSurface(left) && InsideDisc(left),
                  "a closed lid keeps a full, valid patch instead of a degenerate slit");
            Check(left.Bounds.SizeY < left.Bounds.SizeX,
                  "a closed lid reads as a thin horizontal capsule");
            face.Lid = 0;

            /* 6) 压窄是几何给的，不是乘出来的：同样的尺寸在正面与在轮廓处
                  投影宽度必须不同，且高度基本不变。 */
            MeshGeometry3D centre = Pet3D.EyeMesh(0, 0, 0.20, 0.25);
            MeshGeometry3D rim = Pet3D.EyeMesh(0.85, 0.04, 0.20, 0.25);
            double ratio = rim.Bounds.SizeX / centre.Bounds.SizeX;
            Check(ratio > 0.45 && ratio < 0.60,
                  "the rim eye projects at " + (ratio * 100).ToString("0.0")
                  + "% of the centred eye (real foreshortening, not simulated)");
            Check(Math.Abs(rim.Bounds.SizeY - centre.Bounds.SizeY) < 0.02,
                  "foreshortening narrows the eye along the turn only, not in height");
            Check(OnSurface(rim), "the rim eye is still a surface patch, not a floating quad");
        }

        static void Eyes(PetFrame frame, out MeshGeometry3D left, out MeshGeometry3D right)
        {
            Model3DGroup scene = Pet3D.Scene(frame);
            left = null; right = null;
            int geometry = 0;
            for (int i = 0; i < scene.Children.Count; i++)
            {
                GeometryModel3D model = scene.Children[i] as GeometryModel3D;
                if (model == null) continue;
                geometry++;
                MeshGeometry3D mesh = model.Geometry as MeshGeometry3D;
                if (geometry == 2) left = mesh;
                else if (geometry == 3) right = mesh;
            }
            if (left == null) throw new InvalidOperationException("left eye mesh missing");
            if (right == null) throw new InvalidOperationException("right eye mesh missing");
        }

        static Point3D Centroid(MeshGeometry3D mesh)
        {
            double x = 0, y = 0, z = 0;
            for (int i = 0; i < mesh.Positions.Count; i++)
            {
                Point3D p = mesh.Positions[i];
                x += p.X; y += p.Y; z += p.Z;
            }
            double n = Math.Max(1, mesh.Positions.Count);
            return new Point3D(x / n, y / n, z / n);
        }

        static bool OnSurface(MeshGeometry3D mesh)
        {
            for (int i = 0; i < mesh.Positions.Count; i++)
            {
                Point3D p = mesh.Positions[i];
                double radius = Math.Sqrt(p.X * p.X + p.Y * p.Y + p.Z * p.Z);
                if (Math.Abs(radius - 1.015) > 1e-4) return false;
                if (double.IsNaN(radius)) return false;
            }
            return true;
        }

        static bool InsideDisc(MeshGeometry3D mesh)
        {
            for (int i = 0; i < mesh.Positions.Count; i++)
            {
                Point3D p = mesh.Positions[i];
                if (p.X * p.X + p.Y * p.Y > 0.97 * 0.97 + 1e-9) return false;
                if (p.Z < 0.12 - 1e-9) return false;
            }
            return true;
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

        /* ============================================================ 保持态

           第三层打开期间：底稿钉在静止中立帧、随机池停摆，眼睛仍然跟鼠标；
           退出第三层后随机池必须自己回来。这三条分别钉住。 */
        static void HoldTests()
        {
            const double Step = PetAnimationRuntime.Step;
            /* 桥接时长 0.35s，取 0.5s 保证已经换完姿态。 */
            const int SettleSteps = 60;

            /* 1) 保持后身体真的不动：把动作通道全查一遍，而不是只看其中一项。
                  再推进 3s 必须逐字段相同 —— 有随机片段被抽中的话这里立刻露。 */
            PetAnimationRuntime held = new PetAnimationRuntime(17);
            held.Advance(1.4);                       /* 先让它随便跑一段，进保持时不在初始帧 */
            held.Hold(true);
            for (int i = 0; i < SettleSteps; i++) held.Advance(Step);
            PetFrame a = held.Frame;
            for (int i = 0; i < 360; i++) held.Advance(Step);
            PetFrame b = held.Frame;

            bool bodyStill = Math.Abs(a.SquashX - 1) < 1e-6 && Math.Abs(a.SquashY - 1) < 1e-6
                          && Math.Abs(a.Zoom - 1) < 1e-6
                          && Math.Abs(a.Yaw) < 1e-6 && Math.Abs(a.Pitch) < 1e-6
                          && Math.Abs(a.Roll) < 1e-6
                          && Math.Abs(a.OffsetX) < 1e-6 && Math.Abs(a.OffsetY) < 1e-6
                          && Math.Abs(a.MorphAmount) < 1e-6
                          && a.Expression == PetExpression.Neutral && a.State == "hold";
            bool noRandomDrift = Math.Abs(a.SquashX - b.SquashX) < 1e-9
                              && Math.Abs(a.SquashY - b.SquashY) < 1e-9
                              && Math.Abs(a.Yaw - b.Yaw) < 1e-9
                              && Math.Abs(a.Pitch - b.Pitch) < 1e-9
                              && Math.Abs(a.Roll - b.Roll) < 1e-9
                              && Math.Abs(a.Zoom - b.Zoom) < 1e-9
                              && Math.Abs(a.Lid - b.Lid) < 1e-9
                              && b.State == "hold";
            Check(bodyStill, "hold parks the body on the neutral pose");
            Check(noRandomDrift, "hold stops the random clip pool (3 s, frame unchanged)");

            /* 2) 保持期间眼睛照样跟鼠标：身体是静的，视线必须到位。 */
            PetAnimationRuntime watching = new PetAnimationRuntime(17);
            watching.Hold(true);
            watching.LookAt(0.85, -0.60);
            for (int i = 0; i < SettleSteps; i++) watching.Advance(Step);
            PetFrame w = watching.Frame;
            Check(Math.Abs(w.GazeX - 0.85) < 0.02 && Math.Abs(w.GazeY + 0.60) < 0.02
                  && watching.LookWeight > 0.999 && Math.Abs(w.SquashY - 1) < 1e-6,
                  "hold still lets the eyes follow the pointer");

            /* 3) 退出保持：随机池回来（状态离开 hold、画面开始变），
                  而且换姿态是滑的不是跳。

                  量的是「所有会动的通道里最大的单帧步进」而不是只看 squash：
                  静止帧与片段之间的差可能只在某个通道上（比如只有 yaw 和
                  offset 在动），只盯 squash 会让这条断言在多数片段上是空的。 */
            PetAnimationRuntime left = new PetAnimationRuntime(17);
            left.Hold(true);
            for (int i = 0; i < SettleSteps; i++) left.Advance(Step);
            PetFrame atHold = left.Frame;
            left.Hold(false);

            bool poolBack = false;
            bool poolMoves = false;
            double maxBridgeJump = 0.0;
            PetFrame prev = left.Frame;
            for (int i = 0; i < 240; i++)
            {
                left.Advance(Step);
                PetFrame now = left.Frame;
                double jump = Math.Abs(now.SquashY - prev.SquashY)
                            + Math.Abs(now.Zoom - prev.Zoom)
                            + Math.Abs(now.OffsetY - prev.OffsetY)
                            + Math.Abs(now.Yaw - prev.Yaw);
                if (jump > maxBridgeJump) maxBridgeJump = jump;
                double drift = Math.Abs(now.SquashY - atHold.SquashY)
                             + Math.Abs(now.Zoom - atHold.Zoom)
                             + Math.Abs(now.OffsetY - atHold.OffsetY)
                             + Math.Abs(now.Yaw - atHold.Yaw);
                if (drift > 0.05) poolMoves = true;
                prev = now;
                if (now.State != "hold") poolBack = true;
            }
            Check(poolBack, "leaving hold resumes the random clip pool");
            Check(poolMoves, "leaving hold the body actually starts moving again");
            Check(maxBridgeJump < 0.25,
                "leaving hold bridges back instead of jumping (max per-frame step "
                + maxBridgeJump.ToString("0.0000") + " over squash+zoom+offset+yaw)");

            /* 4) 幂等：重复 Hold(true/false) 不该再起一次桥接、也不该换片段。 */
            PetAnimationRuntime twice = new PetAnimationRuntime(17);
            twice.Hold(true);
            for (int i = 0; i < SettleSteps; i++) twice.Advance(Step);
            PetFrame before = twice.Frame;
            twice.Hold(true);
            for (int i = 0; i < 30; i++) twice.Advance(Step);
            Check(Math.Abs(twice.Frame.SquashY - before.SquashY) < 1e-9
                  && twice.Frame.State == "hold",
                  "holding twice is idempotent");
        }
    }
}
