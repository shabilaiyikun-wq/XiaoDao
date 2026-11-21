using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace WinIsland
{
    static class Pet3D
    {
        /* 眼睛的几何约定（沿用原实现的三个量，一个都没改）：

             MaxEyeRadius    眼睛（含边界）在正面圆盘内的上限 —— 防穿模的那道闸
             EyeSurfaceRadius 眼睛贴片所在球面半径（比身体 1.0 多一点点，压在表面上）
             EyeFrontZ       正面剔除线，与 PetShape.EyePair 的 z<0.12 同一门槛 */

        const double MaxEyeRadius = 0.97;
        const double EyeSurfaceRadius = 1.015;
        const double EyeFrontZ = 0.12;

        /* 脸的静止位置：两眼在 x = ±0.29，整体上移 0.04（原实现同值）。 */
        const double EyeRestX = 0.29;
        const double EyeRestY = 0.04;

        /* 注视量 → 球面点。0.62 是原实现的幅度，保持不变 —— 也就是说
           片段里 authored 的 Gaze 轨道（-0.76..0.76）折算出来的球面位移
           与改动前是同一个量级，变的只是「怎么读出来」（球面转向，
           不是两只眼睛一起平移）。纵向再乘 0.8：脸的上下留白比左右少。 */
        const double GazeAmount = 0.62;
        const double GazeUpAmount = 0.8;

        static readonly MeshGeometry3D UnitSphere = SphereMesh(40, 28);

        /// <summary>
        /// 注视量（-1..1）→ 球面点（-0.62..0.62）。下游一律用
        /// PetGaze.Turn 把这个点读成 yaw / pitch。
        /// </summary>
        public static double GazeShift(double value)
        {
            return PetGaze.Clamp(value, -1.0, 1.0) * GazeAmount;
        }

        public static MeshGeometry3D SphereMesh(int longitudeSegments, int latitudeSegments)
        {
            MeshGeometry3D mesh = new MeshGeometry3D();
            for (int latitude = 0; latitude <= latitudeSegments; latitude++)
            {
                double theta = Math.PI * latitude / latitudeSegments;
                double y = Math.Cos(theta);
                double ring = Math.Sin(theta);
                for (int longitude = 0; longitude <= longitudeSegments; longitude++)
                {
                    double phi = Math.PI * 2.0 * longitude / longitudeSegments;
                    Vector3D normal = new Vector3D(ring * Math.Cos(phi), y,
                                                    ring * Math.Sin(phi));
                    normal.Normalize();
                    mesh.Positions.Add(new Point3D(normal.X, normal.Y, normal.Z));
                    mesh.Normals.Add(normal);
                }
            }

            for (int latitude = 0; latitude < latitudeSegments; latitude++)
            {
                for (int longitude = 0; longitude < longitudeSegments; longitude++)
                {
                    int a = latitude * (longitudeSegments + 1) + longitude;
                    int b = a + longitudeSegments + 1;
                    mesh.TriangleIndices.Add(a);
                    mesh.TriangleIndices.Add(b);
                    mesh.TriangleIndices.Add(a + 1);
                    mesh.TriangleIndices.Add(a + 1);
                    mesh.TriangleIndices.Add(b);
                    mesh.TriangleIndices.Add(b + 1);
                }
            }

            mesh.Freeze();
            return mesh;
        }

        /* ============================================================ 眼睛

           改动前后最本质的一处差别：眼睛不再是「两个矩形在屏幕平面里平移」，
           而是「画在球面上的一张贴片，球转多少它跟着转多少」。

           为什么「平移」读起来是贴纸：

             · 平移两个等大的矩形，两个矩形永远一样宽、永远一样深
               —— 眼睛离你远近从来没变过，只是位置变了；
             · 真实球面上，往右看时右眼转到侧壁、左眼几乎还在正面：
               近轮廓那只在正交投影下会被压窄、同时被球体自己挡掉一部分；
             · 所以关键不是「往哪移到」，而是「贴片的朝向」。

           实现：锚点 = 两只眼睛各自的球面点绕 yaw / pitch 转过去；
           贴片 = 以锚点法线为 z 的切平面上那块圆角矩形，再每个顶点
           投回 1.015 球面（保持「贴片始终压在球面上」这条不变量）。
           于是压窄与遮挡全部由 WPF 的正交投影 + z-buffer 完成 ——
           上游 SVG 里那套 `k` / `sx` 模拟缩放一概不叠（叠加就是同一件事
           做两遍，边缘会窄过头，而且眨眼时会出现缝）。

           两只眼睛共用同一组 yaw / pitch（PetGaze.Turn 从同一注视量算出来），
           所以它们不可能各指一个方向。 */

        public static MeshGeometry3D EyeMesh(double centerX, double centerY,
                                              double width, double height)
        {
            return EyeMesh(centerX, centerY, width, height, 0.0);
        }

        /// <summary>
        /// 眼睛贴片。centerX / centerY 是眼睛中心在**正面**上的位置（球面点的
        /// x / y，z 由球面推出），width / height / tilt 是贴片自己的尺寸与倾角。
        ///
        /// 注意投影宽度已经包含压窄：x = 0.85 处 right 向量的 x 分量只剩
        /// 约 0.53，同样的 0.20 宽在那里只投影出 0.105。这是几何的结果，
        /// 不是又乘了一次缩放系数。
        /// </summary>
        public static MeshGeometry3D EyeMesh(double centerX, double centerY,
                                      double width, double height, double tilt,
                                      double bend = 0.0)
        {
            /* bend 默认 0 = 胶囊：这个方法和它的调用方（探针/自检）量的是
               「贴片在球面上的位置与大小」，和眼形无关，保持原样。 */
            return EyePatch(AnchorOf(centerX, centerY), width, height, tilt, 0.0, bend);
        }

        /// <summary>正面 (x, y) → 单位球面上的点（z 取正，正面）。</summary>
        static Point3D AnchorOf(double faceX, double faceY)
        {
            double limit = MaxEyeRadius * MaxEyeRadius;
            double d2 = faceX * faceX + faceY * faceY;
            if (d2 > limit)
            {
                double k = MaxEyeRadius / Math.Sqrt(d2);
                faceX *= k; faceY *= k; d2 = limit;
            }
            return new Point3D(faceX, faceY, Math.Sqrt(Math.Max(0.0, 1.0 - d2)));
        }

        /// <summary>
        /// 一只眼睛的锚点：静止位置绕 yaw（y 轴）与 pitch（x 轴）转过去。
        /// 旋转保长，所以结果仍在单位球面上 —— 这就是「眼睛附着在球面上」。
        /// </summary>
        static Point3D EyeAnchor(int side, double yaw, double pitch)
        {
            double x0 = side * EyeRestX;
            double y0 = EyeRestY;
            double z0 = Math.Sqrt(Math.Max(1e-6, 1.0 - x0 * x0 - y0 * y0));
            double x, y, z;
            PetGaze.RotatePoint(x0, y0, z0, yaw, pitch, out x, out y, out z);
            double length = Math.Sqrt(x * x + y * y + z * z);
            if (length < 1e-9) return new Point3D(0, 0, 1);
            return new Point3D(x / length, y / length, z / length);
        }

        /// <summary>
        /// 锚点处的切平面基。right 与屏幕上「右」对齐（叉乘世界 up），
        /// up 再由 right × n 定出 —— 两只眼睛因此永远不自转、也不会互相扭转。
        ///
        /// 上游给整对眼睛额外加了一个 `tilt = TILT * t.x * t.y` 的斜靠。
        /// 这里不加：项目里的倾角已经由表情（PetFaces.Eye 的 Tilt）占着，
        /// 再叠一个就会把「生气时眼角上挑」这类语义糊掉。
        /// </summary>
        static void EyeFrame(Point3D unitAnchor, out Vector3D right, out Vector3D up)
        {
            Vector3D n = new Vector3D(unitAnchor.X, unitAnchor.Y, unitAnchor.Z);
            n.Normalize();

            right = Vector3D.CrossProduct(new Vector3D(0, 1, 0), n);
            if (right.LengthSquared < 1e-9) right = new Vector3D(1, 0, 0);
            right.Normalize();

            up = Vector3D.CrossProduct(n, right);
            up.Normalize();
        }

        /// <summary>
        /// 切平面局部坐标 → 球面上的点。最后除一次长度，把顶点压回
        /// EyeSurfaceRadius 球面：这条不变量是「贴片不会从球面里浮出来、
        /// 也不会陷进去」的保证（改动前每个顶点半径恒为 1.015，现在也一样）。
        /// </summary>
        static Point3D SurfacePoint(Point3D unitAnchor, Vector3D right, Vector3D up,
                                    double localX, double localY)
        {
            double ax = unitAnchor.X * EyeSurfaceRadius + right.X * localX + up.X * localY;
            double ay = unitAnchor.Y * EyeSurfaceRadius + right.Y * localX + up.Y * localY;
            double az = unitAnchor.Z * EyeSurfaceRadius + right.Z * localX + up.Z * localY;
            double length = Math.Sqrt(ax * ax + ay * ay + az * az);
            if (length < 1e-9) return new Point3D(0, 0, EyeSurfaceRadius);
            double k = EyeSurfaceRadius / length;
            return new Point3D(ax * k, ay * k, az * k);
        }

        static MeshGeometry3D EyePatch(Point3D unitAnchor, double width, double height,
                                        double tilt, double offsetY, double bend)
        {
            Vector3D right, up;
            EyeFrame(unitAnchor, out right, out up);

            MeshGeometry3D mesh = new MeshGeometry3D();
            List<Point> boundary = EyeBoundary(0, 0, width, height, tilt, bend);
            /* OffsetY 是表情自带的纵向微移（Curious 两眼不对称那种）。
               它作用在贴片的局部 up 方向 —— 也就是「眼睛在球面上挪一点」，
               而不是「整块贴片飘离球面」。 */
            Point center = new Point(0, offsetY);
            const int radialSubdivisions = 6;
            for (int i = 0; i < boundary.Count; i++)
            {
                Point edgeA = new Point(boundary[i].X, boundary[i].Y + offsetY);
                Point edgeB = new Point(boundary[(i + 1) % boundary.Count].X,
                                        boundary[(i + 1) % boundary.Count].Y + offsetY);
                for (int step = 0; step < radialSubdivisions; step++)
                {
                    double r0 = (double)step / radialSubdivisions;
                    double r1 = (double)(step + 1) / radialSubdivisions;
                    Point innerA = Lerp(center, edgeA, r0);
                    Point innerB = Lerp(center, edgeB, r0);
                    Point outerA = Lerp(center, edgeA, r1);
                    Point outerB = Lerp(center, edgeB, r1);
                    AddTriangle(mesh, unitAnchor, right, up, innerA, outerA, outerB);
                    AddTriangle(mesh, unitAnchor, right, up, innerA, outerB, innerB);
                }
            }
            mesh.Freeze();
            return mesh;
        }

        /* 眼睛轮廓。默认是胶囊（宽的就是横向胶囊，两段半圆 + 直边）。

           拱桥形（∩）由 bend 决定：bend 的单位是「拱起幅度 / **眼宽**」，
           正值向上拱、负值向下凹。只有 |bend| ≥ ArchBend 才换成拱形带，
           以下一律走原来的胶囊 —— 这样 Sleepy(-.02) / Sad(-.025) /
           Calm(±.04) / Wink(.025) 这些历史值一个像素都不会变，
           拱形是「显式声明」出来的，不是顺带被改出来的。
           另外要求 width > height×1.2：拱是横向的形，竖长的眼睛套上去只会变成怪东西。

           public 是为了让 AnimationTests 能直接量轮廓：拱形这件事必须靠数字验，
           靠肉眼看截图证明不了（而且这个模型的截图里根本没有 3D 内容）。 */
        public const double ArchBend = 0.25;

        public static List<Point> EyeBoundary(double centerX, double centerY,
                                              double width, double height, double tilt, double bend)
        {
            if (Math.Abs(bend) >= ArchBend && width > height * 1.2)
                return EyeArch(centerX, centerY, width, height, tilt, bend);

            List<Point> boundary = new List<Point>();
            bool horizontal = height < width;
            double radius = Math.Min(width, height) / 2.0;
            double capCenter = Math.Max(0.0, (horizontal ? width : height) / 2.0 - radius);
            int arcSegments = 12;
            if (horizontal)
            {
                for (int i = 0; i <= arcSegments; i++)
                {
                    double a = -Math.PI / 2 + Math.PI * i / arcSegments;
                    boundary.Add(RotateEyePoint(centerX + capCenter + radius * Math.Cos(a),
                                                centerY + radius * Math.Sin(a), centerX, centerY, tilt));
                }
                for (int i = 0; i <= arcSegments; i++)
                {
                    double a = Math.PI / 2 + Math.PI * i / arcSegments;
                    boundary.Add(RotateEyePoint(centerX - capCenter + radius * Math.Cos(a),
                                                centerY + radius * Math.Sin(a), centerX, centerY, tilt));
                }
            }
            else
            {
                for (int i = 0; i <= arcSegments; i++)
                {
                    double a = Math.PI * i / arcSegments;
                    boundary.Add(RotateEyePoint(centerX + radius * Math.Cos(a),
                                                centerY + capCenter + radius * Math.Sin(a), centerX, centerY, tilt));
                }
                for (int i = 0; i <= arcSegments; i++)
                {
                    double a = Math.PI + Math.PI * i / arcSegments;
                    boundary.Add(RotateEyePoint(centerX + radius * Math.Cos(a),
                                                centerY - capCenter + radius * Math.Sin(a), centerX, centerY, tilt));
                }
            }

            return boundary;
        }

        /* 拱桥形眼睛（∩）：一条向上鼓起的带子。

           为什么是「带子」而不是一条线：这里的眼睛是**实心贴片**（从中心点扇形三角化
           填满整个轮廓），所以拱必须是个有厚度的闭合区域，不能是一条笔画。

           rise 按**眼宽**算，不按眼高。这条是踩过坑改的：
           Happy 的 Height 只有 0.066（单位球坐标，球半径=1），而 Width 是 0.29 ——
           按眼高算出来的拱高在屏幕上只有 1 DIP，眼睛宽 4.9 DIP，比例 1:5，
           看起来仍然是一根平线（「还是眯眼」就是这么来的）。
           拱的跨度本来由宽度决定，所以幅度也该以宽度为尺。
           顺带解决另一个隐患：Scene 里 Height 会先乘 (1-Lid)，
           按眼高算的拱高会在眼睑压下来时一起塌掉；按宽度算就不会。

           thickness 仍然按眼高算，这样眨眼（Lid→1）时拱会变细、最后收成一条线，
           而不是在闭眼时还悬着一个不变的粗拱。

           厚度往两端收窄到 30%：等厚读起来像「一根弯了的香肠」，收窄了才像一笔。
           收到 0 会让轮廓在两端自相交，扇形三角化会在那里翻出破面。

           baseY 取 -rise/2：让拱的竖直中心落在原来胶囊的位置上 ——
           换成拱形，眼睛不会整体往上飘。 */
        static List<Point> EyeArch(double centerX, double centerY,
                                   double width, double height, double tilt, double bend)
        {
            double half = width / 2.0;
            double rise = bend * width;
            double baseY = -rise * 0.5;
            double thickness = height * 0.80;
            const int segments = 14;

            List<Point> arch = new List<Point>(segments * 2 + 2);
            for (int i = 0; i <= segments; i++)
            {
                double u = -1.0 + 2.0 * i / segments;
                double y = baseY + rise * (1.0 - u * u);
                arch.Add(RotateEyePoint(centerX + half * u, centerY + y, centerX, centerY, tilt));
            }
            for (int i = segments; i >= 0; i--)
            {
                double u = -1.0 + 2.0 * i / segments;
                double y = baseY + rise * (1.0 - u * u);
                double t = thickness * (1.0 - 0.70 * u * u);
                arch.Add(RotateEyePoint(centerX + half * u, centerY + y - t, centerX, centerY, tilt));
            }
            return arch;
        }

        /* 防穿模。改动前是「把转幅整体乘一个系数，直到两只眼睛的边界都落在
           正面圆盘里」；现在边界的位置也跟着球面转向走了，所以判据改成
           逐点检查贴片顶点本身：投影半径 ≤ MaxEyeRadius 且 z ≥ EyeFrontZ。

           为什么还要二分：球面转向是 asin 的，不是线性的；但「转幅 × 系数」
           仍然单调 —— 系数越小越靠近脸的正中，最安全。二分 24 次取可行解的
           下界，效果是「眼睛能转到哪就转到哪，剩下的转幅被摊掉」。 */

        static double FitGaze(double gazeX, double gazeY, PetEyePose left, PetEyePose right)
        {
            if (EyePairFits(gazeX, gazeY, left, right)) return 1.0;
            double low = 0.0, high = 1.0;
            for (int i = 0; i < 24; i++)
            {
                double mid = (low + high) * 0.5;
                if (EyePairFits(gazeX * mid, gazeY * mid, left, right)) low = mid;
                else high = mid;
            }
            return low;
        }

        static bool EyePairFits(double gazeX, double gazeY, PetEyePose left, PetEyePose right)
        {
            double yaw, pitch;
            PetGaze.Turn(gazeX, gazeY, out yaw, out pitch);
            for (int side = -1; side <= 1; side += 2)
            {
                PetEyePose pose = side < 0 ? left : right;
                Point3D anchor = EyeAnchor(side, yaw, pitch);
                if (anchor.Z < EyeFrontZ) return false;

                Vector3D rightAxis, upAxis;
                EyeFrame(anchor, out rightAxis, out upAxis);
                List<Point> boundary = EyeBoundary(0, 0, pose.Width, pose.Height, pose.Tilt, pose.Bend);
                double limit = MaxEyeRadius * MaxEyeRadius;
                for (int i = 0; i < boundary.Count; i++)
                {
                    Point3D point = SurfacePoint(anchor, rightAxis, upAxis,
                                                 boundary[i].X,
                                                 boundary[i].Y + pose.OffsetY);
                    if (point.Z < EyeFrontZ) return false;
                    if (point.X * point.X + point.Y * point.Y > limit) return false;
                }
            }
            return true;
        }

        public static Model3DGroup Scene(PetFrame frame)
        {
            Model3DGroup scene = new Model3DGroup();
            scene.Children.Add(new AmbientLight(Colors.White));

            Material body = BodyMaterial();
            GeometryModel3D sphere = new GeometryModel3D(DeformedSphere(frame), body);
            sphere.BackMaterial = body;
            scene.Children.Add(sphere);

            Material eyes = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0xF4, 0xF3, 0xFF)));
            double gazeX = GazeShift(frame.GazeX);
            double gazeY = GazeShift(frame.GazeY) * GazeUpAmount;
            PetEyePose leftPose = frame.HasEyePose ? frame.LeftEye : PetFaces.Eye(frame.Expression, -1);
            PetEyePose rightPose = frame.HasEyePose ? frame.RightEye : PetFaces.Eye(frame.Expression, 1);
            leftPose.Height *= Math.Max(0.055, 1 - frame.Lid);
            rightPose.Height *= Math.Max(0.055, 1 - frame.Lid);

            /* 转向走球面：两只眼睛共用 yaw / pitch，一次算好。 */
            double fit = FitGaze(gazeX, gazeY, leftPose, rightPose);
            double yaw, pitch;
            PetGaze.Turn(gazeX * fit, gazeY * fit, out yaw, out pitch);

            for (int side = -1; side <= 1; side += 2)
            {
                PetEyePose pose = side < 0 ? leftPose : rightPose;
                Point3D anchor = EyeAnchor(side, yaw, pitch);
                MeshGeometry3D eyeMesh = EyePatch(anchor, pose.Width, pose.Height,
                                                  pose.Tilt, pose.OffsetY, pose.Bend);
                GeometryModel3D eye = new GeometryModel3D(eyeMesh, eyes);
                eye.BackMaterial = eyes;
                scene.Children.Add(eye);
            }

            Transform3DGroup transform = new Transform3DGroup();
            transform.Children.Add(new ScaleTransform3D(frame.SquashX, frame.SquashY, 1.0));
            double zoom = frame.Zoom <= 0 ? 1.0 : frame.Zoom;
            transform.Children.Add(new ScaleTransform3D(zoom, zoom, zoom));
            transform.Children.Add(new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(1, 0, 0), frame.Pitch * 180.0 / Math.PI)));
            transform.Children.Add(new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(0, 1, 0), frame.Yaw * 180.0 / Math.PI)));
            transform.Children.Add(new RotateTransform3D(
                new AxisAngleRotation3D(new Vector3D(0, 0, 1), frame.Roll * 180.0 / Math.PI)));
            scene.Transform = transform;
            return scene;
        }

        static MeshGeometry3D DeformedSphere(PetFrame frame)
        {
            if (frame.MorphAmount <= 0.001 || frame.ShapeIndex <= 0) return UnitSphere;
            double[] profile = PetShape.Profile(frame.ShapeIndex);
            MeshGeometry3D mesh = new MeshGeometry3D();
            for (int i = 0; i < UnitSphere.Positions.Count; i++)
            {
                Point3D p = UnitSphere.Positions[i];
                double angle = Math.Atan2(p.Y, p.X);
                if (angle < 0) angle += Math.PI * 2;
                double fi = angle / (Math.PI * 2) * profile.Length;
                int index = (int)Math.Floor(fi) % profile.Length;
                double radius = AnimationMath.Lerp(profile[index], profile[(index + 1) % profile.Length], fi - Math.Floor(fi));
                double factor = AnimationMath.Lerp(1, radius, frame.MorphAmount);
                mesh.Positions.Add(new Point3D(p.X * factor, p.Y * factor, p.Z));
                Vector3D normal = new Vector3D(p.X * factor, p.Y * factor, p.Z);
                normal.Normalize(); mesh.Normals.Add(normal);
            }
            for (int i = 0; i < UnitSphere.TriangleIndices.Count; i++) mesh.TriangleIndices.Add(UnitSphere.TriangleIndices[i]);
            mesh.Freeze(); return mesh;
        }

        static Material BodyMaterial()
        {
            return new DiffuseMaterial(
                new SolidColorBrush(Color.FromRgb(0x5A, 0x4A, 0xC8)));
        }

        static Point RotateEyePoint(double x, double y,
                                    double centerX, double centerY, double tilt)
        {
            double dx = x - centerX;
            double dy = y - centerY;
            double c = Math.Cos(tilt);
            double s = Math.Sin(tilt);
            return new Point(centerX + dx * c - dy * s,
                             centerY + dx * s + dy * c);
        }

        static Point Lerp(Point a, Point b, double t)
        {
            return new Point(a.X + (b.X - a.X) * t,
                             a.Y + (b.Y - a.Y) * t);
        }

        static void AddTriangle(MeshGeometry3D mesh, Point3D unitAnchor,
                                Vector3D right, Vector3D up, Point a, Point b, Point c)
        {
            int index = mesh.Positions.Count;
            AddSurfacePoint(mesh, unitAnchor, right, up, a.X, a.Y);
            AddSurfacePoint(mesh, unitAnchor, right, up, b.X, b.Y);
            AddSurfacePoint(mesh, unitAnchor, right, up, c.X, c.Y);
            mesh.TriangleIndices.Add(index);
            mesh.TriangleIndices.Add(index + 1);
            mesh.TriangleIndices.Add(index + 2);
        }

        static void AddSurfacePoint(MeshGeometry3D mesh, Point3D unitAnchor,
                                    Vector3D right, Vector3D up, double localX, double localY)
        {
            Point3D point = SurfacePoint(unitAnchor, right, up, localX, localY);
            /* 法线取锚点法线就够：整个场景只有 AmbientLight，
               明暗不随法线变（改动前后一致）。 */
            Vector3D normal = new Vector3D(unitAnchor.X, unitAnchor.Y, unitAnchor.Z);
            normal.Normalize();
            mesh.Positions.Add(point);
            mesh.Normals.Add(normal);
        }
    }
}