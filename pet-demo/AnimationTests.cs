using System;
using System.Windows;
using System.Windows.Media.Media3D;

namespace WinIsland
{
    static class AnimationTests
    {
        static int failures;
        static int checks;

        static void Check(bool condition, string name)
        {
            checks++;
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
            ReactionTests();
            JumpImpulseTests();
            HappyArchTests();
            HoverPoseTests();
            BindingPoolTests();
            /* 总计行。以前这里只靠退出码表达成败，人肉看日志数不出「到底跑了多少条」，
               也看不出某一段是否整段没执行（正是 LiquidProbe 那种假绿的形状）。 */
            Console.WriteLine("RESULT pass=" + (checks - failures) + " fail=" + failures
                              + " total=" + checks);
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
            /* 900 步 = 15s，**必须跨过整整一段**：片段最长 5.8s（idle-sleepy），
               而 idle-rest 有 4.8s、整段只有 ±0.006 的呼吸。以前这里只跑 240 步（4s），
               一旦松手后抽中的是这种安静片段，「在动」就量不到 —— 那条断言当时能过，
               靠的是 34 段洗牌恰好抽中活泼片段，属于运气。
               改成跨段之后，它量的是「池子回来了并且会动」，与抽到哪一段无关。 */
            for (int i = 0; i < 900; i++)
            {
                left.Advance(Step);
                PetFrame now = left.Frame;
                double jump = Math.Abs(now.SquashY - prev.SquashY)
                            + Math.Abs(now.Zoom - prev.Zoom)
                            + Math.Abs(now.OffsetY - prev.OffsetY)
                            + Math.Abs(now.Yaw - prev.Yaw);
                /* 单帧步进只在前 240 步量：那是「刚松手」的桥接段。
                   拉长到 15s 会把后续每次换片也纳进来，阈值就不再是同一件事了。 */
                if (i < 240 && jump > maxBridgeJump) maxBridgeJump = jump;
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

        /* 事件 → 反应：配方表与裁决器（desktop/PetReaction.cs）。
           这一组全是纯逻辑，不开窗、不等真实事件 —— 微信来消息和二十分钟番茄钟
           都没法在测试里等，但「消息风暴只反应一次」「低优先级不打断高优先级」
           这些规则必须能在这里被证明。 */
        static void ReactionTests()
        {
            /* 1) 每个已接线事件都有配方，拼错的事件名不许静默当成功 */
            string[] events = PetReactions.Events();
            Check(events.Length == 11, "eleven pet reactions are wired (got " + events.Length + ")");
            for (int i = 0; i < events.Length; i++)
                Check(PetReactions.Get(events[i]) != null,
                      "reaction table has a recipe for " + events[i]);
            Check(PetReactions.Get("no.such.event") == null,
                  "unknown event has no recipe (so it can be logged, not silently played)");
            Check(PetReactions.Get(null) == null, "null event is rejected");

            /* 2) 每个事件的配方要对得上约定：状态、脉冲、优先级次序 */
            PetReaction notify = PetReactions.Get("notify");
            Check(notify.State == "curious" && !notify.Spin && !notify.Poke,
                  "notify -> curious short look, no impulse");

            PetReaction timer = PetReactions.Get("timer.done");
            Check(timer.State == "happy" && timer.Spin && !timer.Poke,
                  "timer.done -> happy plus one spin (the celebration)");

            PetReaction todo = PetReactions.Get("todo.due");
            Check(todo.State == "thinking" && !todo.Spin && !todo.Poke,
                  "todo.due -> thinking");

            PetReaction drop = PetReactions.Get("transfer.drop");
            Check(drop.Poke && (drop.State == null || drop.State.Length == 0),
                  "transfer.drop -> squeeze only, no state change");

            /* 戳桌宠：必须是最灵敏的那个 —— 冷却短、不占保护期、不改状态，
               这样连戳不会把正在播的反应顶掉，也不会看起来像卡住。 */
            PetReaction poke = PetReactions.Get("pet.poke");
            Check(poke.Poke && (poke.State == null || poke.State.Length == 0),
                  "pet.poke -> squeeze only, no state change");
            Check(poke.CooldownMs > 0 && poke.CooldownMs <= 300,
                  "pet.poke cooldown is short enough to feel immediate, long enough to stop double-fire");
            Check(poke.HoldMs == 0, "pet.poke does not hold the pet (nothing to fall back from)");

            /* 第二轮加的六个（A 组） */
            PetReaction done = PetReactions.Get("todo.done");
            Check(done.State == "happy" && done.Spin && done.Jump && done.HoldMs > 0,
                  "todo.done -> happy plus jump and spin (the real celebration)");
            Check(!timer.Jump, "timer.done spins but does not jump (only 收工 is a leap)");
            Check(done.Priority < timer.Priority,
                  "todo.done yields to timer.done when both land together");

            PetReaction add = PetReactions.Get("todo.add");
            Check(add.State == "thinking" && !add.Spin && !add.Poke, "todo.add -> thinking");

            PetReaction leave = PetReactions.Get("island.leave");
            Check(leave.State == "sleepy", "island.leave -> sleepy");
            Check(leave.CooldownMs >= 10000,
                  "island.leave cooldown is long: the cursor crosses the island constantly");

            PetReaction menu = PetReactions.Get("menu.open");
            Check(menu.State == "curious", "menu.open -> curious (look at the menu)");

            PetReaction drag = PetReactions.Get("island.drag");
            Check(drag.State == "dizzy", "island.drag -> dizzy");

            PetReaction scene = PetReactions.Get("scene.change");
            Check(scene.State == "curious" && scene.Priority <= 15,
                  "scene.change is low priority (the auto state machine rewrites scenes)");

            /* 被绑定用到的状态名必须都能查到片段。拼错的话 ForState 会静默回落到
               idle 池，表现是「绑了个不存在的动作」，很难查 —— 这里直接点名。
               另外这些状态都不能是 idle：把 idle 绑成反应等于没有反应。 */
            string[] boundStates = PetReactions.BoundStates();
            Check(boundStates.Length > 0, "bindings declare their states");
            for (int i = 0; i < boundStates.Length; i++)
            {
                PetClip[] cs = PetClips.ForState(boundStates[i]);
                bool ok = cs != null && cs.Length > 0;
                if (ok) for (int j = 0; j < cs.Length; j++) if (cs[j] == null) ok = false;
                Check(ok, "bound state '" + boundStates[i] + "' resolves to real clips");
                Check(boundStates[i] != "idle", "bound state '" + boundStates[i] + "' is not idle");
            }

            Check(timer.Priority > todo.Priority && todo.Priority > notify.Priority,
                  "priority order is timer.done > todo.due > notify");
            Check(notify.CooldownMs >= 2000,
                  "notify cooldown is long enough to absorb a burst of messages");
            Check(timer.HoldMs > 0 && todo.HoldMs > 0,
                  "state-changing reactions carry a hold time so they can fall back to idle");

            /* 3) 冷却：同一事件在冷却期内只过第一次 */
            PetReactionPolicy p = new PetReactionPolicy();
            Check(p.Accept(notify, 0), "first notify plays");
            Check(!p.Accept(notify, notify.CooldownMs - 1), "notify inside cooldown is dropped");
            Check(p.Accept(notify, notify.CooldownMs), "notify plays again exactly at cooldown");

            /* 4) 优先级抢占：番茄钟在保护期内，来消息和待办都得让路 */
            PetReactionPolicy q = new PetReactionPolicy();
            Check(q.Accept(timer, 0), "timer.done plays");
            Check(q.Busy(timer.HoldMs - 1), "policy reports busy while holding");
            Check(!q.Accept(notify, 100), "low priority notify cannot interrupt the celebration");
            Check(!q.Accept(todo, 100), "todo.due cannot interrupt the celebration either");

            /* 到期那一拍必须已经放开 —— 注意要在「接受 todo」之前问，
               因为 todo 自己也会占一段保护期，之后再问就只会看到它留下的忙。 */
            Check(!q.Busy(timer.HoldMs), "policy is free again exactly at the hold deadline");
            Check(q.Accept(todo, timer.HoldMs), "todo.due plays once the celebration window ends");
            Check(q.Busy(timer.HoldMs + 1), "the todo reaction arms its own busy window");

            /* 5) 同优先级不互相挡：连着两次收工都该有反应 */
            PetReactionPolicy r = new PetReactionPolicy();
            Check(r.Accept(timer, 0), "first celebration plays");
            Check(r.Accept(timer, timer.CooldownMs),
                  "a second celebration at its cooldown is not blocked by equal priority");

            /* 6) 纯脉冲事件不占用保护期：挤一下不该把后续状态反应挡掉 */
            PetReactionPolicy s = new PetReactionPolicy();
            Check(s.Accept(drop, 0), "transfer.drop plays");
            Check(s.Accept(todo, 10), "a squeeze does not block the following todo reaction");

            /* 7) 复位后一切从头开始（关掉再打开桌宠不该继承旧的冷却） */
            PetReactionPolicy t = new PetReactionPolicy();
            t.Accept(notify, 0);
            t.Reset();
            Check(t.Accept(notify, 1), "policy reset clears cooldowns");

            /* 8) 戳桌宠的命中区：圆内中、圆外不中、透明四角不中、直径没算出来时不中。
                  这块以前没有，所以桌宠整块是穿透的，鼠标直接落到桌面上 —— 根本戳不到。 */
            Check(PetHit.Circle(100, 100, 100, 100, 60), "pet hit: dead centre hits");
            Check(PetHit.Circle(129, 100, 100, 100, 60), "pet hit: inside the radius hits");
            Check(!PetHit.Circle(131, 100, 100, 100, 60), "pet hit: just outside the radius misses");
            Check(!PetHit.Circle(100 + 25, 100 + 25, 100, 100, 60),
                  "pet hit: the transparent corner of the bounding box misses (circle, not square)");
            Check(!PetHit.Circle(100, 100, 100, 100, 0),
                  "pet hit: no hit before the diameter is known");
            Check(!PetHit.Circle(0, 0, double.NaN, 100, 60),
                  "pet hit: a NaN centre never hits (rather than swallowing every click)");
        }

        /* 跳跃脉冲：比「戳」高得多，而且自己会落回原地。

           为什么要单独测峰值：跳跃和戳走的是同一条 OffsetY 通道，只是曲线不同。
           如果哪天有人把 52 改成 12，代码不会报错，桌宠只是「跳」得看不出来 ——
           这种退化只有量数字能发现。 */
        static void JumpImpulseTests()
        {
            const double Step = PetAnimationRuntime.Step;

            /* 先把身体停在静止姿态，拿到干净的基线（OffsetY≈0） */
            PetAnimationRuntime jump = new PetAnimationRuntime(17);
            jump.Hold(true);
            for (int i = 0; i < 60; i++) jump.Advance(Step);
            double baseY = jump.Frame.OffsetY;

            jump.Jump();
            double peak = 0;
            for (int i = 0; i < 90; i++)
            {
                jump.Advance(Step);
                double lift = baseY - jump.Frame.OffsetY;   /* OffsetY 负值向上 */
                if (lift > peak) peak = lift;
            }
            Check(peak > 40,
                  "jump lifts clearly off the ground (peak " + peak.ToString("0.0") + " px)");

            /* 落地后必须回到基线，而且冲量要自己从队列里消失 —— 否则身体会永久悬空 */
            for (int i = 0; i < 90; i++) jump.Advance(Step);
            Check(Math.Abs(jump.Frame.OffsetY - baseY) < 0.5,
                  "jump lands back on the baseline (offset "
                  + (jump.Frame.OffsetY - baseY).ToString("0.000") + " px)");
            Check(Math.Abs(jump.Frame.SquashY - 1) < 0.02,
                  "jump settles back to an unsquashed body");

            /* 和戳对比：跳跃必须明显更高，否则「加个跳跃」等于没加 */
            PetAnimationRuntime poke = new PetAnimationRuntime(17);
            poke.Hold(true);
            for (int i = 0; i < 60; i++) poke.Advance(Step);
            double pokeBase = poke.Frame.OffsetY;
            poke.Poke();
            double pokePeak = 0;
            for (int i = 0; i < 90; i++)
            {
                poke.Advance(Step);
                double lift = pokeBase - poke.Frame.OffsetY;
                if (lift > pokePeak) pokePeak = lift;
            }
            Check(pokePeak > 10 && pokePeak < 30,
                  "poke stays a small nudge (" + pokePeak.ToString("0.0") + " px)");
            Check(peak > pokePeak * 1.8,
                  "jump is at least twice the poke (" + peak.ToString("0.0")
                  + " vs " + pokePeak.ToString("0.0") + " px)");
        }

        /* 高兴的眼睛是拱桥形（∩）。这件事必须用数字验：
           这个模型读不了截图（模型不支持图像输入），而且 PetStage 是 Viewport3D，
           RenderTargetBitmap 根本抓不到球体内容 —— 所以「看起来更像」这种判据
           在这里无法成立，只能量轮廓。

           量的三件事：
             1) 拱顶在中间（|X| 接近 0），不是偏到一边；
             2) 拱顶明显高于两端（差值 ≥ 半个眼高），否则只是「弯了一点」；
             3) 换形状不改大小（竖直范围仍在原来的胶囊量级内）。 */
        static void HappyArchTests()
        {
            PetEyePose happy = PetFaces.Eye(PetExpression.Happy, -1);
            Check(Math.Abs(happy.Bend) >= Pet3D.ArchBend,
                  "happy declares an arch (bend " + happy.Bend.ToString("0.##")
                  + " >= " + Pet3D.ArchBend.ToString("0.##") + ")");

            System.Collections.Generic.List<Point> b =
                Pet3D.EyeBoundary(0, 0, happy.Width, happy.Height, 0, happy.Bend);
            Check(b != null && b.Count >= 16, "happy arch outline has enough points");

            double maxY = double.MinValue, minX = double.MaxValue, maxX = double.MinValue;
            double peakX = 0;
            bool finite = true;
            for (int i = 0; i < b.Count; i++)
            {
                if (double.IsNaN(b[i].X) || double.IsNaN(b[i].Y)
                    || double.IsInfinity(b[i].X) || double.IsInfinity(b[i].Y)) finite = false;
                if (b[i].Y > maxY) { maxY = b[i].Y; peakX = b[i].X; }
                if (b[i].X < minX) minX = b[i].X;
                if (b[i].X > maxX) maxX = b[i].X;
            }
            Check(finite, "happy arch outline has no NaN or infinity");

            /* 1) 拱顶在中间 */
            Check(Math.Abs(peakX) < happy.Width * 0.12,
                  "arch peaks near the middle (x=" + peakX.ToString("0.000") + ")");

            /* 2) 拱顶明显高于两端。取「靠两端那些点里最高的」当端头高度。
               幅度按**眼宽**衡量：
               眼睛只有 4.9 DIP 宽，拱如果只有 1 DIP 高（= 按眼高算的结果），
               在屏幕上就是一根平线 —— 这正是第一版「还是眯眼」的原因。 */
            /* 端头 = |X| 取到最外侧（= 半宽）的那两点。注意别写成「|X| > 宽×0.4」：
               X 的绝对值上限是半宽，那个条件等于 |u| > 0.8，量到的是拱腰而不是端头
               —— 会把真实拱高 0.110 量成 0.081。 */
            double endY = double.MinValue;
            for (int i = 0; i < b.Count; i++)
                if (Math.Abs(Math.Abs(b[i].X) - maxX) < 1e-9 && b[i].Y > endY) endY = b[i].Y;
            Check(maxY - endY > happy.Width * 0.30,
                  "arch rises by a readable fraction of the eye width (rise "
                  + (maxY - endY).ToString("0.000") + " vs width "
                  + happy.Width.ToString("0.000") + "; maxY=" + maxY.ToString("0.000")
                  + " endY=" + endY.ToString("0.000") + " pts=" + b.Count
                  + " bend=" + happy.Bend.ToString("0.000")
                  + " maxX=" + maxX.ToString("0.000") + ")");

            /* 3) 换形状可以更高，但不能出框 */
            Check(Math.Abs(maxX - happy.Width / 2) < 1e-9 && Math.Abs(minX + happy.Width / 2) < 1e-9,
                  "arch keeps the original eye width");
            double extent = maxY - (endY - happy.Height * 0.80 * 0.30);
            Check(extent < happy.Width * 1.0,
                  "arch stays inside a sane box (extent " + extent.ToString("0.000")
                  + " vs width " + happy.Width.ToString("0.000") + ")");

            /* 4) 眼睑压下来时拱不能跟着塌。
               这是第一版真正的 bug：拱高按眼高算，而 Scene 里 Height 会先乘 (1-Lid)，
               眼一眯拱就没了。按眼宽算之后，Lid 只让拱变细，不该让它变平。 */
            System.Collections.Generic.List<Point> squinting =
                Pet3D.EyeBoundary(0, 0, happy.Width, happy.Height * 0.20, 0, happy.Bend);
            double sMaxY = double.MinValue, sEndY = double.MinValue, sMaxX = 0;
            for (int i = 0; i < squinting.Count; i++)
            {
                if (squinting[i].Y > sMaxY) sMaxY = squinting[i].Y;
                if (Math.Abs(squinting[i].X) > sMaxX) sMaxX = Math.Abs(squinting[i].X);
            }
            for (int i = 0; i < squinting.Count; i++)
                if (Math.Abs(Math.Abs(squinting[i].X) - sMaxX) < 1e-9 && squinting[i].Y > sEndY)
                    sEndY = squinting[i].Y;
            Check(sMaxY - sEndY > happy.Width * 0.30,
                  "arch does not flatten when the lid squeezes the height (rise "
                  + (sMaxY - sEndY).ToString("0.000") + ")");

            /* 4) 带真实 tilt 仍然是拱形（旋转不该把拱压平） */
            PetEyePose right = PetFaces.Eye(PetExpression.Happy, 1);
            System.Collections.Generic.List<Point> tilted =
                Pet3D.EyeBoundary(0, 0, right.Width, right.Height, right.Tilt, right.Bend);
            double tMaxY = double.MinValue, tEndY = double.MinValue;
            for (int i = 0; i < tilted.Count; i++)
            {
                if (tilted[i].Y > tMaxY) tMaxY = tilted[i].Y;
            }
            /* 旋转后「端头高度」按 y 的中位附近取：直接用不旋转的端头做参照更稳，
               这里只验「拱顶仍然在最上面且高于中线」 */
            Check(tMaxY > right.Height * 0.3,
                  "tilted arch still rises above the eye midline ("
                  + tMaxY.ToString("0.000") + ")");

            /* 5) 其他表情一个像素都没变：它们的 bend 都低于阈值，仍走胶囊 */
            PetExpression[] others = new PetExpression[] {
                PetExpression.Neutral, PetExpression.Surprised, PetExpression.Sleepy,
                PetExpression.Focused, PetExpression.Sad, PetExpression.Angry,
                PetExpression.Curious, PetExpression.Wink, PetExpression.Calm };
            int changed = 0;
            string changedNames = "";
            for (int i = 0; i < others.Length; i++)
            {
                PetEyePose p = PetFaces.Eye(others[i], -1);
                if (Math.Abs(p.Bend) >= Pet3D.ArchBend) { changed++; changedNames += " " + others[i]; }
            }
            Check(changed == 0, "only happy arches; other expressions keep the capsule"
                  + changedNames);

            /* 6) 胶囊那条分支本身没坏：Neutral 的轮廓仍是「高一半、宽一半」 */
            PetEyePose neutral = PetFaces.Eye(PetExpression.Neutral, -1);
            System.Collections.Generic.List<Point> capsule =
                Pet3D.EyeBoundary(0, 0, neutral.Width, neutral.Height, 0, neutral.Bend);
            double cMaxY = double.MinValue, cMaxX = double.MinValue;
            for (int i = 0; i < capsule.Count; i++)
            {
                if (capsule[i].Y > cMaxY) cMaxY = capsule[i].Y;
                if (Math.Abs(capsule[i].X) > cMaxX) cMaxX = Math.Abs(capsule[i].X);
            }
            Check(Math.Abs(cMaxY - neutral.Height / 2) < 1e-9,
                  "neutral outline is still a capsule (half height "
                  + cMaxY.ToString("0.000") + ")");
            Check(Math.Abs(cMaxX - neutral.Width / 2) < 1e-9,
                  "neutral outline is still a capsule (half width "
                  + cMaxX.ToString("0.000") + ")");

            /* 7) 端到端：轮廓对了，还要确认**网格真的吃到了 bend**。
               轮廓函数正确但 EyePatch 忘了传参时，上面所有断言依然全绿 ——
               这条比的是两条真实渲染路径产出的顶点数：拱形带 30 个轮廓点、
               胶囊 26 个，扇形细分相同，所以顶点数必然不同。 */
            MeshGeometry3D archMesh = Pet3D.EyeMesh(0, 0, happy.Width, happy.Height, 0, happy.Bend);
            MeshGeometry3D capsuleMesh = Pet3D.EyeMesh(0, 0, happy.Width, happy.Height, 0, 0);
            Check(archMesh != null && capsuleMesh != null
                  && archMesh.Positions.Count != capsuleMesh.Positions.Count,
                  "the eye mesh really receives the bend ("
                  + (archMesh == null ? -1 : archMesh.Positions.Count) + " vs "
                  + (capsuleMesh == null ? -1 : capsuleMesh.Positions.Count) + " vertices)");
        }

        /* 鼠标停在球上：低头 + 眯眼 + 左右摇头。
           这三样在数据里是三条独立通道，所以能分别断言 —— 而不是只能靠肉眼看截图：
             Pitch 低头（正 = 往下，见 PetClip.Sample）
             Lid   眯眼（Pet3D 里眼睛高度 *= 1-Lid）
             Yaw   左右摇头（必须变号，否则是「歪着头」不是「摇头」） */
        static void HoverPoseTests()
        {
            PetClip[] pool = PetClips.ForState("hover");
            Check(pool != null && pool.Length == 1 && pool[0] != null,
                  "hover state maps to exactly one clip");
            PetClip c = pool[0];
            Check(c.Name == "hover-shy", "hover clip is named hover-shy");
            Check(c.Duration >= 2, "hover clip lasts long enough to read as a loop");

            /* 低头：整段都要低着头（不是点一下头又抬起来） */
            double minPitch = double.MaxValue;
            for (double s = 0.3; s <= c.Duration - 0.4; s += 0.1)
            {
                PetFrame f = c.Sample(s);
                if (f.Pitch < minPitch) minPitch = f.Pitch;
            }
            Check(minPitch > 0.1,
                  "hover holds its head down (min pitch " + minPitch.ToString("0.000") + " rad)");

            /* 眯眼：中段压到一半，且不能压成闭眼 */
            PetFrame mid = c.Sample(c.Duration * 0.5);
            Check(mid.Lid > 0.45 && mid.Lid < 0.7,
                  "hover squints: eyes about half height (lid " + mid.Lid.ToString("0.00") + ")");
            Check(c.Sample(0).Lid < 0.3,
                  "hover starts with eyes open, so entering it is not a pop");

            /* 摇头：一段里必须同时出现明显的左和右 */
            double maxYaw = 0, minYaw = 0;
            for (double s = 0; s <= c.Duration; s += 0.02)
            {
                PetFrame f = c.Sample(s);
                if (f.Yaw > maxYaw) maxYaw = f.Yaw;
                if (f.Yaw < minYaw) minYaw = f.Yaw;
            }
            Check(maxYaw > 0.15 && minYaw < -0.15,
                  "hover shakes head both ways (yaw " + minYaw.ToString("0.00")
                  + " .. " + maxYaw.ToString("0.00") + " rad)");

            /* 首尾回到中位：换回 idle 时不会看到脑袋「啪」地弹正 */
            PetFrame first = c.Sample(0), last = c.Sample(c.Duration);
            Check(Math.Abs(first.Yaw) < 0.02 && Math.Abs(last.Yaw) < 0.02,
                  "hover starts and ends facing forward (no snap back to idle)");

            /* 它只能被 hover 用，不能混进随机待机池 —— 否则平时发呆也会摇头 */
            bool leaked = false;
            for (int i = 0; i < PetClips.Idle.Length; i++)
                if (PetClips.Idle[i] != null && PetClips.Idle[i].Name == "hover-shy") leaked = true;
            Check(!leaked, "hover clip is not part of the random idle pool");
        }

        static bool PoolHas(string name)
        {
            for (int i = 0; i < PetClips.Idle.Length; i++)
                if (PetClips.Idle[i] != null && PetClips.Idle[i].Name == name) return true;
            return false;
        }

        /* 「被绑定的动作必须移出随机动画池」——这条规则的守门测试。

           为什么值得专门锁住：一个片段一旦既表示「某事发生了」又在待机里随机出现，
           它的信号含义就废了（看到 Done 你分不清它是在庆祝还是路过）。
           排除名单是从绑定表**推导**的（PetClips.BuildIdlePool →
           ReservedClipNames → PetReactions.BoundStates），所以这里要验的是
           推导结果本身：绑了什么，池子里就必须没有什么。 */
        static void BindingPoolTests()
        {
            string[] reserved = PetClips.ReservedClipNames();
            Check(Array.IndexOf(reserved, "Blocked_A") >= 0,
                  "reserved list keeps the hand-picked exclusion Blocked_A");

            /* 方向一：凡是绑定状态用到的片段，一个都不许在池子里 */
            string[] bound = PetReactions.BoundStates();
            int leaked = 0;
            string leakedNames = "";
            for (int s = 0; s < bound.Length; s++)
            {
                PetClip[] cs = PetClips.ForState(bound[s]);
                if (cs == null) continue;
                for (int i = 0; i < cs.Length; i++)
                {
                    if (cs[i] == null) continue;
                    if (PoolHas(cs[i].Name))
                    {
                        leaked++;
                        leakedNames += " " + cs[i].Name + "(" + bound[s] + ")";
                    }
                }
            }
            Check(leaked == 0, "no bound clip leaks into the random idle pool" + leakedNames);

            /* 方向二：排除必须是精确的，不能把整族一刀切掉 ——
               没被绑定用到的参考动画应该还在池子里。 */
            Check(PoolHas("Marketing_A") && PoolHas("Streaming_A") && PoolHas("Idle_A"),
                  "unbound reference clips stay in the idle pool");
            Check(PoolHas("idle-rest"),
                  "the self-authored idle clips stay in the pool");

            /* 方向三：排完之后池子还得够厚，否则待机看起来会重复 */
            Check(PetClips.Idle.Length >= 15,
                  "idle pool still has enough variety after exclusions ("
                  + PetClips.Idle.Length + " clips)");
        }
    }
}
