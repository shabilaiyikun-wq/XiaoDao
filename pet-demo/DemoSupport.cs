using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinIsland
{
    static class Ui
    {
        public static SolidColorBrush Br(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    static class AppDir
    {
        static string Root
        {
            get
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "XiaodaoPetDemo");
                Directory.CreateDirectory(path);
                return path;
            }
        }

        public static string[] ReadLines(string name)
        {
            try
            {
                string path = Path.Combine(Root, name);
                return File.Exists(path) ? File.ReadAllLines(path) : new string[0];
            }
            catch { return new string[0]; }
        }

        public static void WriteLines(string name, System.Collections.Generic.List<string> lines)
        {
            try { File.WriteAllLines(Path.Combine(Root, name), lines.ToArray()); }
            catch { }
        }
    }

    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter,
                                        int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")]
        static extern bool BitBlt(IntPtr dst, int x, int y, int width, int height,
                                 IntPtr src, int sx, int sy, uint operation);

        public static IntPtr GetExStyle(IntPtr hwnd) { return GetWindowLongPtr(hwnd, GWL_EXSTYLE); }
        public static void SetExStyle(IntPtr hwnd, IntPtr style) { SetWindowLongPtr(hwnd, GWL_EXSTYLE, style); }
        public static bool TryGetCursor(out Point point)
        {
            POINT raw;
            if (!GetCursorPos(out raw)) { point = new Point(); return false; }
            point = new Point(raw.X, raw.Y);
            return true;
        }
        public static bool LeftButtonDown() { return (GetAsyncKeyState(1) & 0x8000) != 0; }
        public static int ScreenWidth { get { return GetSystemMetrics(0); } }
        public static int ScreenHeight { get { return GetSystemMetrics(1); } }

        public static void KeepTopmost(IntPtr hwnd)
        {
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);
        }

        public static void MakeDpiAware()
        {
            try { SetProcessDPIAware(); } catch { }
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        public static BitmapSource Capture(int x, int y, int width, int height)
        {
            IntPtr screen = IntPtr.Zero, memory = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                screen = GetDC(IntPtr.Zero);
                if (screen == IntPtr.Zero) return null;
                memory = CreateCompatibleDC(screen);
                bitmap = CreateCompatibleBitmap(screen, width, height);
                if (memory == IntPtr.Zero || bitmap == IntPtr.Zero) return null;
                old = SelectObject(memory, bitmap);
                if (!BitBlt(memory, 0, 0, width, height, screen, x, y, 0x00CC0020)) return null;
                BitmapSource result = Imaging.CreateBitmapSourceFromHBitmap(
                    bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                result.Freeze();
                return result;
            }
            catch { return null; }
            finally
            {
                if (memory != IntPtr.Zero && old != IntPtr.Zero) SelectObject(memory, old);
                if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
                if (memory != IntPtr.Zero) DeleteDC(memory);
                if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
