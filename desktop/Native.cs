using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace WinIsland
{
    /// <summary>Win32 互操作：穿透、无焦点、光标、桌面抓取。</summary>
    static class Native
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_LAYERED   = 0x00080000;
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_NOACTIVATE = 0x08000000;

        public const uint WDA_NONE = 0x0;
        public const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

        const uint SRCCOPY = 0x00CC0020;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        static extern IntPtr GetWindowLongPtrW(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        /* 「真的要退出」用的两把锤子。

           为什么需要：Environment.Exit 并不是立刻死 —— 它先跑 ProcessExit 处理器，
           再走 ExitProcess 的 DLL detach。这个程序挂着 OLE 拖放、COM 媒体会话
           （GlobalSystemMediaTransportControls）、UIA 通知这些第三方组件，
           任何一个在 detach 里卡住，进程就会永远停在退出路上。
           实测到过：调用 Environment.Exit 之后进程还活着 6 秒以上。

           TerminateProcess(自己, code) 是内核级终止，不会执行任何清理，
           因此不会卡。它是最后一道，只在优雅退出已经失败时才用。 */
        [DllImport("kernel32.dll")]
        static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")]
        static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        /// <summary>立刻终止本进程（不跑清理、不可能被卡住）。失败返回 false。</summary>
        public static bool TerminateSelf(int exitCode)
        {
            try { return TerminateProcess(GetCurrentProcess(), (uint)exitCode); }
            catch { return false; }
        }

        public static IntPtr GetExStyle(IntPtr hwnd)
        {
            return GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
        }

        public static void SetExStyle(IntPtr hwnd, IntPtr style)
        {
            SetWindowLongPtrW(hwnd, GWL_EXSTYLE, style);
        }

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT p);

        public static bool TryGetCursor(out Point p)
        {
            POINT raw;
            if (!GetCursorPos(out raw)) { p = new Point(); return false; }
            p = new Point(raw.X, raw.Y);
            return true;
        }

        /* 左键此刻是不是真的按着。拖动靠它兜底：鼠标在窗口外松开、或者按住时
           弹了 UAC/切换了桌面，我们收不到 MouseUp，岛就会一直粘着光标。
           每 16ms 拿一次真实按键状态，能自己发现「其实早松了」。 */
        [DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int vKey);

        public static bool LeftButtonDown()
        {
            return (GetAsyncKeyState(0x01) & 0x8000) != 0;
        }

        [DllImport("user32.dll")]
        static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
                                        int x, int y, int w, int h, uint flags);

        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        /// <summary>
        /// 把窗口重新压回 topmost 队列顶部，但不移动、不改尺寸、不抢激活。
        /// 桌宠自检时会垫一张探针窗在下面，探针窗一 Show 就有机会盖住桌宠；
        /// 抓屏前调一次这个，确保拍到的还是桌宠自己。
        /// </summary>
        public static void KeepTopmost(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            try
            {
                SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                             SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
            }
            catch { }
        }

        /// <summary>让本窗口不出现在屏幕截图里，抓桌面时才不会拍到自己。</summary>
        public static bool ExcludeFromCapture(IntPtr hwnd)
        {
            try { return SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE); }
            catch { return false; }
        }

        /// <summary>取消抓屏排除。抓屏式验收要靠这个才能重新看见岛。</summary>
        public static bool ClearCaptureExclusion(IntPtr hwnd)
        {
            try { return SetWindowDisplayAffinity(hwnd, WDA_NONE); }
            catch { return false; }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmFlush();

        /// <summary>
        /// 等一帧 DWM 合成结束。
        ///
        /// 改了 SetWindowDisplayAffinity 之后，新的亲和性要等 DWM 合成下一帧才
        /// 真正生效；紧接着 BitBlt 拿到的可能还是上一帧（那一帧里窗口还在）。
        /// 抓屏前调一次，才能确保拍到的不是自己。DWM 没跑时它立刻返回。
        /// </summary>
        public static void FlushComposition()
        {
            try { DwmFlush(); }
            catch { }
        }

        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        public static void MakeDpiAware()
        {
            try { SetProcessDPIAware(); } catch { }
        }

        const int SM_CXSCREEN = 0;
        const int SM_CYSCREEN = 1;

        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        /// <summary>主屏物理像素尺寸。</summary>
        public static int ScreenWidth { get { return GetSystemMetrics(SM_CXSCREEN); } }
        public static int ScreenHeight { get { return GetSystemMetrics(SM_CYSCREEN); } }

        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")]
        static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

        /// <summary>
        /// 抓屏幕一块矩形。返回冻结的 BitmapSource（BGRA32），失败返回 null。
        /// 用 GDI BitBlt 而不是 DWM/DXGI：窗口像素由 DWM 合成，BitBlt 拿到的是合成后的画面。
        /// </summary>
        public static BitmapSource Capture(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0) return null;
            IntPtr screenDc = IntPtr.Zero;
            IntPtr memDc = IntPtr.Zero;
            IntPtr bmp = IntPtr.Zero;
            IntPtr old = IntPtr.Zero;
            try
            {
                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return null;
                memDc = CreateCompatibleDC(screenDc);
                if (memDc == IntPtr.Zero) return null;
                bmp = CreateCompatibleBitmap(screenDc, w, h);
                if (bmp == IntPtr.Zero) return null;
                old = SelectObject(memDc, bmp);
                if (!BitBlt(memDc, 0, 0, w, h, screenDc, x, y, SRCCOPY)) return null;

                BitmapSource src = Imaging.CreateBitmapSourceFromHBitmap(
                    bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { return null; }
            finally
            {
                if (memDc != IntPtr.Zero && old != IntPtr.Zero) SelectObject(memDc, old);
                if (bmp != IntPtr.Zero) DeleteObject(bmp);
                if (memDc != IntPtr.Zero) DeleteDC(memDc);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        /* ------------------------------------------------------- 实时抓取 */
        [StructLayout(LayoutKind.Sequential)]
        struct BmiHeader
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Bmi
        {
            public BmiHeader header;
            public int colors;
        }

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateDIBSection(IntPtr hdc, ref Bmi bmi, uint usage,
                                              out IntPtr bits, IntPtr section, uint offset);

        /// <summary>
        /// 固定尺寸的复用抓取器：一块 DIB 反复 BitBlt，像素指针直接交给
        /// WriteableBitmap.WritePixels。
        ///
        /// 为什么不用 Native.Capture：那个每帧都会 new 一个 860KB 的 byte[] 和一张
        /// 新的 BitmapSource。860KB 超过 85KB 的大对象阈值，直接进 LOH，10Hz 就是
        /// 每秒 8.6MB 的 LOH 分配，会持续触发 gen2 回收。复用 DIB 之后每帧零分配。
        /// </summary>
        public sealed class Grabber : IDisposable
        {
            IntPtr _memDc, _bmp, _old, _bits;
            int _w, _h;

            public IntPtr Bits { get { return _bits; } }
            public int Stride { get { return _w * 4; } }
            public int Width { get { return _w; } }
            public int Height { get { return _h; } }
            public bool Ok { get { return _bits != IntPtr.Zero && _memDc != IntPtr.Zero; } }

            public Grabber(int w, int h)
            {
                _w = w; _h = h;
                IntPtr screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return;
                try
                {
                    _memDc = CreateCompatibleDC(screenDc);
                    if (_memDc == IntPtr.Zero) return;
                    Bmi bmi = new Bmi();
                    bmi.header.biSize = Marshal.SizeOf(typeof(BmiHeader));
                    bmi.header.biWidth = w;
                    bmi.header.biHeight = -h;   // 负值 = 自上而下，和 WPF 位图的行序一致
                    bmi.header.biPlanes = 1;
                    bmi.header.biBitCount = 32;
                    bmi.header.biCompression = 0;
                    _bmp = CreateDIBSection(screenDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
                    if (_bmp != IntPtr.Zero) _old = SelectObject(_memDc, _bmp);
                }
                finally { ReleaseDC(IntPtr.Zero, screenDc); }
            }

            public bool Grab(int x, int y)
            {
                if (!Ok) return false;
                IntPtr screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) return false;
                try { return BitBlt(_memDc, 0, 0, _w, _h, screenDc, x, y, SRCCOPY); }
                finally { ReleaseDC(IntPtr.Zero, screenDc); }
            }

            public void Dispose()
            {
                if (_memDc != IntPtr.Zero && _old != IntPtr.Zero) SelectObject(_memDc, _old);
                if (_bmp != IntPtr.Zero) DeleteObject(_bmp);
                if (_memDc != IntPtr.Zero) DeleteDC(_memDc);
                _bmp = IntPtr.Zero; _memDc = IntPtr.Zero; _bits = IntPtr.Zero; _old = IntPtr.Zero;
            }
        }
    }
}