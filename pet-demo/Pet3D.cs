using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace WinIsland
{
    static class Pet3D
    {
        const double MaxEyeRadius = 0.97;
        const double EyeSurfaceRadius = 1.015;
        static readonly MeshGeometry3D UnitSphere = SphereMesh(40, 28);

        public static double GazeShift(double value)
        {
            if (value < -1.0) value = -1.0;
            if (value > 1.0) value = 1.0;
            return value * 0.62;
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

        public static MeshGeometry3D EyeMesh(double centerX, double centerY,
                                              double width, double height)
        {
            return EyeMesh(centerX, centerY, width, height, 0.0);
        }

        static MeshGeometry3D EyeMesh(double centerX, double centerY,
                                      double width, double height, double tilt)
        {
            MeshGeometry3D mesh = new MeshGeometry3D();
            List<Point> boundary = EyeBoundary(centerX, centerY, width, height, tilt);
            Point center = new Point(centerX, centerY);
            const int radialSubdivisions = 6;
            for (int i = 0; i < boundary.Count; i++)
            {
                Point edgeA = boundary[i];
                Point edgeB = boundary[(i + 1) % boundary.Count];
                for (int step = 0; step < radialSubdivisions; step++)
                {
                    double r0 = (double)step / radialSubdivisions;
                    double r1 = (double)(step + 1) / radialSubdivisions;
                    Point innerA = Lerp(center, edgeA, r0);
                    Point innerB = Lerp(center, edgeB, r0);
                    Point outerA = Lerp(center, edgeA, r1);
                    Point outerB = Lerp(center, edgeB, r1);
                    AddTriangle(mesh, innerA, outerA, outerB);
                    AddTriangle(mesh, innerA, outerB, innerB);
                }
            }
            mesh.Freeze();
            return mesh;
        }

        static List<Point> EyeBoundary(double centerX, double centerY,
                                       double width, double height, double tilt)
        {
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

        static double FitEyeGaze(double gazeX, double gazeY, double width,
                                 double height, double tilt, PetExpression expression)
        {
            if (EyePairFits(gazeX, gazeY, width, height, tilt, expression)) return 1.0;
            double low = 0.0, high = 1.0;
            for (int i = 0; i < 24; i++)
            {
                double mid = (low + high) * 0.5;
                if (EyePairFits(gazeX * mid, gazeY * mid, width, height, tilt, expression))
                    low = mid;
                else
                    high = mid;
            }
            return low;
        }

        static bool EyePairFits(double gazeX, double gazeY, double width,
                                double height, double tilt, PetExpression expression)
        {
            double limitSquared = MaxEyeRadius * MaxEyeRadius;
            for (int side = -1; side <= 1; side += 2)
            {
                double sideHeight = height;
                if (expression == PetExpression.Wink && side > 0) sideHeight *= 0.06;
                double centerX = side * 0.29 + gazeX;
                double centerY = 0.04 + gazeY;
                if (centerX * centerX + centerY * centerY > limitSquared) return false;
                List<Point> boundary = EyeBoundary(centerX, centerY, width, sideHeight,
                                                   side < 0 ? -tilt : tilt);
                for (int i = 0; i < boundary.Count; i++)
                {
                    Point point = boundary[i];
                    if (point.X * point.X + point.Y * point.Y > limitSquared) return false;
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
            double gazeY = GazeShift(frame.GazeY) * 0.8;
            PetEyePose leftPose = frame.HasEyePose ? frame.LeftEye : PetFaces.Eye(frame.Expression, -1);
            PetEyePose rightPose = frame.HasEyePose ? frame.RightEye : PetFaces.Eye(frame.Expression, 1);
            leftPose.Height *= Math.Max(0.055, 1 - frame.Lid);
            rightPose.Height *= Math.Max(0.055, 1 - frame.Lid);
            double eyeWidth = Math.Max(leftPose.Width, rightPose.Width);
            double eyeHeight = Math.Max(leftPose.Height, rightPose.Height);
            double eyeTilt = Math.Max(Math.Abs(leftPose.Tilt), Math.Abs(rightPose.Tilt));
            double gazeScale = FitEyeGaze(gazeX, gazeY, eyeWidth, eyeHeight, eyeTilt, frame.Expression);
            gazeX *= gazeScale;
            gazeY *= gazeScale;
            for (int side = -1; side <= 1; side += 2)
            {
                double x = side * 0.29 + gazeX;
                PetEyePose pose = side < 0 ? leftPose : rightPose;
                double y = 0.04 + gazeY + pose.OffsetY;
                MeshGeometry3D eyeMesh = EyeMesh(x, y, pose.Width, pose.Height, pose.Tilt);
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

        static void AddSurfacePoint(MeshGeometry3D mesh, double x, double y)
        {
            double d = x * x + y * y;
            if (d > MaxEyeRadius * MaxEyeRadius)
            { double scale = MaxEyeRadius / Math.Sqrt(d); x *= scale; y *= scale; d = x * x + y * y; }
            double bodyZ = Math.Sqrt(Math.Max(0.0, 1.0 - d));
            double z = Math.Sqrt(Math.Max(0.0, EyeSurfaceRadius * EyeSurfaceRadius - d));
            Vector3D normal = new Vector3D(x, y, bodyZ);
            normal.Normalize();
            Point3D point = new Point3D(x, y, z);
            mesh.Positions.Add(point);
            mesh.Normals.Add(normal);
        }

        static Point Lerp(Point a, Point b, double t)
        {
            return new Point(a.X + (b.X - a.X) * t,
                             a.Y + (b.Y - a.Y) * t);
        }

        static void AddTriangle(MeshGeometry3D mesh, Point a, Point b, Point c)
        {
            int index = mesh.Positions.Count;
            AddSurfacePoint(mesh, a.X, a.Y);
            AddSurfacePoint(mesh, b.X, b.Y);
            AddSurfacePoint(mesh, c.X, c.Y);
            mesh.TriangleIndices.Add(index);
            mesh.TriangleIndices.Add(index + 1);
            mesh.TriangleIndices.Add(index + 2);
        }

    }
}
