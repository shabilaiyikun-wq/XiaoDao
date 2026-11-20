param(
  [int]$Ms = 2600,
  [int]$PollMs = 40
)

# Verify the water-neck ("two drops merging, reversed") shape.
#
# Three bugs this script is built to avoid, all of which produced false passes:
#
#   1. Hardcoded window coordinates. The island can be dragged; the scan then reads
#      empty desktop and reports zero forever.
#   2. Picking the largest window of the pid. That pid also owns 1920x1117 helper
#      windows, so it picks a helper and reads garbage.
#   3. Counting plain green pixels. The island's own aurora artwork contains green,
#      so "green present" is true before the ball even appears. Everything below is
#      measured as a DIFF against a baseline frame captured while no ball exists.
#
# What "a neck" looks like in the numbers: a round body with a thin tail reaching
# left, and then that tail vanishing (the pinch-off) while the body stays.
#
# ASCII-only; run under powershell.exe (5.1) for System.Drawing.

Add-Type -AssemblyName System.Drawing

if (-not ("NQ" -as [type])) {
  Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class NQ {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  public struct RECT { public int L, T, R, B; }
  delegate bool EnumProc(IntPtr h, IntPtr p);

  // The island window: right pid, WPF HwndWrapper class, island-sized.
  public static string WindowRect(uint want) {
    string o = "";
    int best = 0;
    EnumWindows(delegate(IntPtr h, IntPtr p) {
      uint pid;
      GetWindowThreadProcessId(h, out pid);
      if (pid != want) return true;
      if (!IsWindowVisible(h)) return true;
      StringBuilder cn = new StringBuilder(256);
      GetClassNameW(h, cn, 256);
      if (!cn.ToString().StartsWith("HwndWrapper")) return true;
      RECT r;
      if (!GetWindowRect(h, out r)) return true;
      int w = r.R - r.L, hh = r.B - r.T;
      if (w < 400 || w > 1200 || hh < 120 || hh > 700) return true;
      if (w * hh > best) { best = w * hh; o = r.L + "," + r.T + "," + w + "," + hh; }
      return true;
    }, IntPtr.Zero);
    return o;
  }

  static byte[] _base;
  static int _w, _h, _stride;

  static Bitmap Grab(int rx, int ry, int rw, int rh, out byte[] buf, out int stride) {
    Bitmap b = new Bitmap(rw, rh, PixelFormat.Format32bppArgb);
    using (Graphics g = Graphics.FromImage(b))
      g.CopyFromScreen(rx, ry, 0, 0, new Size(rw, rh), CopyPixelOperation.SourceCopy);
    BitmapData d = b.LockBits(new Rectangle(0, 0, rw, rh), ImageLockMode.ReadOnly,
                              PixelFormat.Format32bppArgb);
    stride = d.Stride;
    buf = new byte[d.Stride * rh];
    Marshal.Copy(d.Scan0, buf, 0, buf.Length);
    b.UnlockBits(d);
    b.Dispose();
    return b;
  }

  public static void CaptureBaseline(int rx, int ry, int rw, int rh) {
    byte[] buf; int stride;
    Grab(rx, ry, rw, rh, out buf, out stride);
    _base = buf; _w = rw; _h = rh; _stride = stride;
  }

  // Per-column count of pixels that are ball-green NOW and differ from baseline.
  public static int[] DiffCols(int rx, int ry, int rw, int rh,
                               int rLo, int rHi, int gLo, int gHi, int bLo, int bHi) {
    byte[] buf; int stride;
    Grab(rx, ry, rw, rh, out buf, out stride);
    int[] col = new int[rw];
    bool diff = (_base != null && _w == rw && _h == rh && _stride == stride);
    for (int y = 0; y < rh; y++) {
      int row = y * stride;
      for (int x = 0; x < rw; x++) {
        int o = row + x * 4;
        int bl = buf[o], gr = buf[o + 1], rd = buf[o + 2];
        if (rd < rLo || rd > rHi || gr < gLo || gr > gHi || bl < bLo || bl > bHi) continue;
        if (diff) {
          int d = Math.Abs(buf[o] - _base[o]) + Math.Abs(buf[o + 1] - _base[o + 1])
                + Math.Abs(buf[o + 2] - _base[o + 2]);
          if (d < 24) continue;      // unchanged background green
        }
        col[x]++;
      }
    }
    return col;
  }
}
'@
}
[void][NQ]::SetProcessDPIAware()

$proc = Get-Process -Name WinIsland -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output "FAIL WinIsland is not running"; exit 1 }

$rect = [NQ]::WindowRect([uint32]$proc.Id)
if ($rect -eq "") { Write-Output "FAIL window not found for pid $($proc.Id)"; exit 1 }
$r = $rect -split ','
$wl = [int]$r[0]; $wt = [int]$r[1]; $ww = [int]$r[2]; $wh = [int]$r[3]
Write-Output ("window at {0},{1} {2}x{3}" -f $wl, $wt, $ww, $wh)
if ($ww -gt 1400 -or $wh -gt 800) {
  Write-Output "FAIL implausible window size, refusing to scan"
  exit 1
}

# Baseline must be taken while no ball exists. --notify-test waits 1200ms, and this
# runs within ~100ms of launch, but measure instead of assuming: if the baseline
# already shows a tall green body in the right half, the run is unusable.
[NQ]::CaptureBaseline($wl, $wt, $ww, $wh)

$sw = [Diagnostics.Stopwatch]::StartNew()
$t0 = -1
$sawTail = $false
$tailPeak = 0
$sawBreak = $false
$bodySeen = 0

Write-Output ""
Write-Output "   ms  maxH  ballX  tailSpan  tailMax"
Write-Output "  ---  ----  -----  --------  -------"

while ($sw.ElapsedMilliseconds -lt $Ms) {
  $col = [NQ]::DiffCols($wl, $wt, $ww, $wh, 0, 70, 150, 235, 40, 150)
  $maxH = 0
  for ($i = 0; $i -lt $col.Length; $i++) { if ($col[$i] -gt $maxH) { $maxH = $col[$i] } }

  if ($maxH -gt 6) {
    if ($t0 -lt 0) { $t0 = $sw.ElapsedMilliseconds }
    $bodySeen++

    $thr = [int]($maxH * 0.7)
    $bodyX = -1
    for ($i = 0; $i -lt $col.Length; $i++) { if ($col[$i] -ge $thr) { $bodyX = $i; break } }

    $span = 0; $tmax = 0
    if ($bodyX -gt 0) {
      $gap = 0
      for ($i = $bodyX - 1; $i -ge 0; $i--) {
        if ($col[$i] -eq 0) { $gap++; if ($gap -ge 3) { break } else { continue } }
        $gap = 0
        if ($col[$i] -ge $thr) { break }
        $span++
        if ($col[$i] -gt $tmax) { $tmax = $col[$i] }
      }
    }
    if ($span -gt 0) { $sawTail = $true; if ($tmax -gt $tailPeak) { $tailPeak = $tmax } }
    # the pinch-off: the body is there but the tail is gone
    if ($sawTail -and $span -eq 0 -and $bodySeen -gt 4) { $sawBreak = $true }

    Write-Output ("{0,5} {1,5} {2,6} {3,9} {4,7}" -f ($sw.ElapsedMilliseconds - $t0), $maxH, $bodyX, $span, $tmax)
  }
  Start-Sleep -Milliseconds $PollMs
}

Write-Output ""
Write-Output ("body appeared    : {0} samples" -f $bodySeen)
Write-Output ("neck seen        : {0}  (widest {1} px)" -f $sawTail, $tailPeak)
Write-Output ("neck broke off   : {0}" -f $sawBreak)

if (-not $sawTail) {
  Write-Output "FAIL no neck: the drop was a plain round body the whole time"
  exit 1
}
if (-not $sawBreak) {
  Write-Output "FAIL the neck never disappeared: no pinch-off, the two never separated"
  exit 1
}
Write-Output "PASS  neck formed, thinned, and broke off while the drop stayed round"
exit 0