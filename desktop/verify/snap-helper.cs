using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

/// <summary>
/// 原生窗口验收探针。单帧绝对色值判据 + Win32 状态查询，
/// 不依赖「前后两帧相减」，因为桌面本身一直在变。
/// </summary>
public static class Snap
{
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindowLongPtrW(IntPtr h, int i);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extra);
    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extra);

    const uint ME_LEFTDOWN = 0x0002;
    const uint ME_LEFTUP = 0x0004;
    const uint ME_RIGHTDOWN = 0x0008;
    const uint ME_RIGHTUP = 0x0010;

    /// <summary>合成一次左键按下（配合 LeftUp 使用）。</summary>
    public static void LeftDown() { mouse_event(ME_LEFTDOWN, 0, 0, 0, IntPtr.Zero); }

    /// <summary>合成一次左键松开。测试脚本必须在 finally 里调用，否则会把系统鼠标按死。</summary>
    public static void LeftUp() { mouse_event(ME_LEFTUP, 0, 0, 0, IntPtr.Zero); }

    public static void RightDown() { mouse_event(ME_RIGHTDOWN, 0, 0, 0, IntPtr.Zero); }
    public static void RightUp() { mouse_event(ME_RIGHTUP, 0, 0, 0, IntPtr.Zero); }

    /// <summary>按一次键（按下+抬起）。</summary>
    public static void KeyTap(int vk)
    {
        keybd_event((byte)vk, 0, 0, IntPtr.Zero);
        keybd_event((byte)vk, 0, 0x0002, IntPtr.Zero);
    }

    /// <summary>把光标从 (x0,y0) 平滑移到 (x1,y1)，让目标程序收到鼠标移动消息。</summary>
    public static void DragTo(int x0, int y0, int x1, int y1, int ms)
    {
        DateTime t0 = DateTime.UtcNow;
        while (true)
        {
            double p = ms <= 0 ? 1.0 : (DateTime.UtcNow - t0).TotalMilliseconds / ms;
            if (p >= 1.0) break;
            int x = (int)(x0 + (x1 - x0) * p);
            int y = (int)(y0 + (y1 - y0) * p);
            SetCursorPos(x, y);
            System.Threading.Thread.Sleep(12);
        }
        SetCursorPos(x1, y1);
        System.Threading.Thread.Sleep(20);
    }

    delegate bool EnumProc(IntPtr h, IntPtr p);

    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public static readonly IntPtr HWND_TOP = IntPtr.Zero;

    /* ---------------------------------------------------------------- 窗口 */

    static IntPtr FindMain(int pid)
    {
        IntPtr best = IntPtr.Zero;
        int bestArea = 0;
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            uint wpid;
            GetWindowThreadProcessId(h, out wpid);
            if (wpid != (uint)pid) return true;
            if (!IsWindowVisible(h)) return true;
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            int w = r.Right - r.Left, hh = r.Bottom - r.Top;
            if (w <= 0 || hh <= 0) return true;
            if (w * hh > bestArea) { bestArea = w * hh; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    public static int[] WindowRectOf(int pid)
    {
        IntPtr h = FindMain(pid);
        if (h == IntPtr.Zero) return null;
        RECT r;
        if (!GetWindowRect(h, out r)) return null;
        return new int[] { r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top };
    }

    /// <summary>该进程主窗口的 HWND。
    /// 注意不能用 Process.MainWindowHandle：ShowInTaskbar=false + WS_EX_TOOLWINDOW
    /// 的窗口它多半返回 0，拿着 0 去 SetWindowPos 是空操作，然后在测试脚本里
    /// 静默地量错窗口（踩过）。</summary>
    public static IntPtr MainHwnd(int pid)
    {
        return FindMain(pid);
    }

    /// <summary>窗口扩展样式（含 WS_EX_TRANSPARENT = 0x20）。</summary>
    public static long ExStyleOf(int pid)
    {
        IntPtr h = FindMain(pid);
        if (h == IntPtr.Zero) return 0;
        return GetWindowLongPtrW(h, -20).ToInt64();
    }

    public static bool IsClickThrough(int pid)
    {
        return (ExStyleOf(pid) & 0x20L) != 0;
    }

    public static void MoveCursor(int x, int y) { SetCursorPos(x, y); }

    public static int[] CursorPos()
    {
        POINT p;
        GetCursorPos(out p);
        return new int[] { p.X, p.Y };
    }

    /* ---------------------------------------------------------------- 取图 */

    public static byte[] Grab(int w, int h)
    {
        using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            using (Graphics g = Graphics.FromImage(b))
            {
                g.CopyFromScreen(0, 0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }
            BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] buf = new byte[d.Stride * h];
            Marshal.Copy(d.Scan0, buf, 0, buf.Length);
            b.UnlockBits(d);
            return buf;
        }
    }

    public static void Save(byte[] buf, int w, int h, int x, int y, int cw, int ch, string path)
    {
        using (Bitmap src = new Bitmap(w, h, PixelFormat.Format32bppArgb))
        {
            BitmapData d = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(buf, 0, d.Scan0, d.Stride * h);
            src.UnlockBits(d);
            using (Bitmap crop = new Bitmap(cw, ch))
            {
                using (Graphics g = Graphics.FromImage(crop))
                {
                    g.DrawImage(src, new Rectangle(0, 0, cw, ch), new Rectangle(x, y, cw, ch), GraphicsUnit.Pixel);
                }
                crop.Save(path, ImageFormat.Png);
            }
        }
    }

    /* ---------------------------------------------------------------- 统计 */

    static double Lum(byte[] b, int i)
    {
        return 0.2126 * b[i + 2] + 0.7152 * b[i + 1] + 0.0722 * b[i];
    }

    /// <summary>[均值, 最小, 最大]</summary>
    public static double[] RegionLum(byte[] a, int stride, int x, int y, int w, int h)
    {
        double sum = 0, max = 0, min = 255; int n = 0;
        for (int j = 0; j < h; j++)
        {
            int row = (y + j) * stride;
            for (int i = 0; i < w; i++)
            {
                double l = Lum(a, row + (x + i) * 4);
                sum += l; if (l > max) max = l; if (l < min) min = l; n++;
            }
        }
        return new double[] { n > 0 ? sum / n : 0, min, max };
    }

    /// <summary>块内落在 [lo,hi] 亮度区间的像素占比。</summary>
    public static double InRangeRatio(byte[] a, int stride, int x, int y, int w, int h, double lo, double hi)
    {
        int hit = 0, n = 0;
        for (int j = 0; j < h; j++)
        {
            int row = (y + j) * stride;
            for (int i = 0; i < w; i++)
            {
                double l = Lum(a, row + (x + i) * 4);
                if (l >= lo && l <= hi) hit++;
                n++;
            }
        }
        return n > 0 ? (double)hit / n : 0;
    }

    /// <summary>
    /// 块内出现最多的亮度（4 级分桶后取桶中心）。
    /// 用众数而不是均值取「填充色」：岛内文字/图标只占少数像素，
    /// 均值会被它们拉偏，众数稳定落在填充上。
    /// </summary>
    /// <summary>
    /// 从中心向两侧走，量出「连续属于岛体」的那一段宽度。
    ///
    /// 为什么不用整行 min/max：桌面只要有一点亮区（比如频谱可视化那种亮点），
    /// 它也会被判成岛体侧，宽度立刻虚胖几十像素。岛是以中心为起点的一块连通区域，
    /// 所以从中心往外走才对。
    ///
    /// 允许 gap 个连续非岛体像素：卡片里的文字、封面会在岛内造成零星暗点，
    /// 一碰到就停会量短；而真正的边界是一整片外部区域，gap 一到就停。
    /// </summary>
    public static int[] FillRun(byte[] a, int stride, int y, int cx, int x0, int x1, double lo, double hi, int gap)
    {
        int row = y * stride;
        int left = cx, right = cx;
        int miss = 0;
        for (int x = cx; x >= x0; x--)
        {
            double l = Lum(a, row + x * 4);
            if (l >= lo && l <= hi) { left = x; miss = 0; }
            else { miss++; if (miss > gap) break; }
        }
        miss = 0;
        for (int x = cx; x <= x1; x++)
        {
            double l = Lum(a, row + x * 4);
            if (l >= lo && l <= hi) { right = x; miss = 0; }
            else { miss++; if (miss > gap) break; }
        }
        return new int[] { left, right, right - left + 1 };
    }

    public static double ModeLuma(byte[] a, int stride, int x, int y, int w, int h)
    {
        int[] hist = new int[64];
        for (int j = 0; j < h; j++)
        {
            int row = (y + j) * stride;
            for (int i = 0; i < w; i++)
            {
                int b = (int)(Lum(a, row + (x + i) * 4) / 4.0);
                if (b < 0) b = 0; if (b > 63) b = 63;
                hist[b]++;
            }
        }
        int best = 0, bi = 0;
        for (int i = 0; i < 64; i++) { if (hist[i] > best) { best = hist[i]; bi = i; } }
        return bi * 4 + 2;
    }

    /// <summary>块内平均 RGB（[r,g,b]）。用来判断玻璃到底透出了什么颜色。</summary>
    public static double[] RegionRGB(byte[] a, int stride, int x, int y, int w, int h)
    {
        double r = 0, g = 0, b = 0;
        int n = 0;
        for (int j = y; j < y + h; j++)
        {
            int row = j * stride;
            for (int i = x; i < x + w; i++)
            {
                int o = row + i * 4;
                b += a[o]; g += a[o + 1]; r += a[o + 2];
                n++;
            }
        }
        if (n == 0) return new double[] { 0, 0, 0 };
        return new double[] { r / n, g / n, b / n };
    }

    /// <summary>块内亮度 &lt; t 的像素个数。</summary>
    public static int CountBelow(byte[] a, int stride, int x, int y, int w, int h, double t)
    {
        int hit = 0;
        for (int j = 0; j < h; j++)
        {
            int row = (y + j) * stride;
            for (int i = 0; i < w; i++)
            {
                if (Lum(a, row + (x + i) * 4) < t) hit++;
            }
        }
        return hit;
    }

    /// <summary>块内亮度 &gt; t 的像素个数。</summary>
    public static int CountAbove(byte[] a, int stride, int x, int y, int w, int h, double t)
    {
        int hit = 0;
        for (int j = 0; j < h; j++)
        {
            int row = (y + j) * stride;
            for (int i = 0; i < w; i++)
            {
                if (Lum(a, row + (x + i) * 4) > t) hit++;
            }
        }
        return hit;
    }

    /// <summary>
    /// 沿一行扫描亮度落在 [lo,hi] 的像素，返回最左与最右的 x。
    /// 用来实测「岛真的画了这么宽」。返回 [left, right, width]。
    /// </summary>
    public static int[] FillSpan(byte[] a, int stride, int y, int x0, int x1, double lo, double hi)
    {
        int left = -1, right = -1;
        int row = y * stride;
        for (int x = x0; x < x1; x++)
        {
            double l = Lum(a, row + x * 4);
            if (l >= lo && l <= hi) { if (left < 0) left = x; right = x; }
        }
        if (left < 0) return new int[] { -1, -1, 0 };
        return new int[] { left, right, right - left + 1 };
    }

    /// <summary>两帧同一区域的平均差（用来量化桌面噪声，不作为主判据）。</summary>
    public static double[] RegionDiff(byte[] a, byte[] b, int stride, int x, int y, int w, int h)
    {
        double sum = 0, max = 0; int n = 0;
        for (int j = 0; j < h; j++)
        {
            int row = (y + j) * stride;
            for (int i = 0; i < w; i++)
            {
                int k = row + (x + i) * 4;
                double d = Math.Abs(Lum(a, k) - Lum(b, k));
                sum += d; if (d > max) max = d; n++;
            }
        }
        return new double[] { n > 0 ? sum / n : 0, max };
    }
}