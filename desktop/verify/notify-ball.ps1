param(
  [int]$Left = 0, [int]$Top = 0, [int]$W = 0, [int]$H = 0,
  [double]$Dpi = 1.5,
  [double]$ExpW = 452, [double]$MX = 24, [double]$MT = 16, [double]$MB = 34,
  [string]$Log = ""
)

# Verify the notification ball is actually painted, by pixel, not by eyeball.
#
# The ball sits OUTSIDE the squircle, in the reserved right margin of the window,
# so it is invisible to every check that measures the island body. This scans the
# whole window rect for WeChat green (#07C160) and reports the blob.
#
# ASCII-only: Windows PowerShell reads .ps1 with the ANSI codepage.
# Must run under powershell.exe (5.1): System.Drawing.Imaging needs .NET Framework.

Add-Type -AssemblyName System.Drawing
if (-not ("BS" -as [type])) {
  Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

public static class BS {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
  [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
  [DllImport("gdi32.dll")] static extern uint GetPixel(IntPtr dc, int x, int y);

  // "r,g,b" of one screen pixel
  public static string Sample(int x, int y) {
    IntPtr dc = GetDC(IntPtr.Zero);
    uint c = GetPixel(dc, x, y);
    ReleaseDC(IntPtr.Zero, dc);
    if (c == 0xFFFFFFFF) return "-1,-1,-1";
    return (c & 0xFF) + "," + ((c >> 8) & 0xFF) + "," + ((c >> 16) & 0xFF);
  }

  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  public struct RECT { public int L, T, R, B; }
  delegate bool EnumProc(IntPtr h, IntPtr p);

  // ????????????????????????????????
  // ????????????????????????? pid ??? 1920x1117 ?
  // helper???????(HwndWrapper)??????
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

  // Returns "count,x0,y0,x1,y1" relative to the region, for pixels matching the
  // requested colour window. One CopyFromScreen + LockBits: GetPixel per sample
  // is far too slow for a ~250k pixel region.
  public static string Scan(int rx, int ry, int rw, int rh,
                            int rLo, int rHi, int gLo, int gHi, int bLo, int bHi) {
    int count = 0, x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
    using (Bitmap b = new Bitmap(rw, rh, PixelFormat.Format32bppArgb)) {
      using (Graphics g = Graphics.FromImage(b))
        g.CopyFromScreen(rx, ry, 0, 0, new Size(rw, rh), CopyPixelOperation.SourceCopy);
      BitmapData d = b.LockBits(new Rectangle(0, 0, rw, rh), ImageLockMode.ReadOnly,
                                PixelFormat.Format32bppArgb);
      byte[] buf = new byte[d.Stride * rh];
      Marshal.Copy(d.Scan0, buf, 0, buf.Length);
      b.UnlockBits(d);
      for (int y = 0; y < rh; y++) {
        int row = y * d.Stride;
        for (int x = 0; x < rw; x++) {
          int o = row + x * 4;
          int bl = buf[o], gr = buf[o + 1], rd = buf[o + 2];
          if (rd >= rLo && rd <= rHi && gr >= gLo && gr <= gHi && bl >= bLo && bl <= bHi) {
            count++;
            if (x < x0) x0 = x;
            if (y < y0) y0 = y;
            if (x > x1) x1 = x;
            if (y > y1) y1 = y;
          }
        }
      }
    }
    if (count == 0) return "0,-1,-1,-1,-1";
    return count + "," + x0 + "," + y0 + "," + x1 + "," + y1;
  }
}
'@
}
[void][BS]::SetProcessDPIAware()

$proc = Get-Process -Name WinIsland -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Output "FAIL WinIsland is not running"; exit 1 }

$rect = [BS]::WindowRect([uint32]$proc.Id)
if ($rect -eq "") { Write-Output "FAIL window not found for pid $($proc.Id)"; exit 1 }
$r = $rect -split ","
$Left = [int]$r[0]; $Top = [int]$r[1]
if ($W -le 0) { $W = [int]$r[2] }
if ($H -le 0) { $H = [int]$r[3] }

Write-Output ("region: {0},{1} {2}x{3}" -f $Left, $Top, $W, $H)

$fail = 0
function Check($cond, $ok, $bad) {
  if ($cond) { Write-Output "   ok   $ok" } else { Write-Output "   FAIL $bad"; $script:fail++ }
}

# WeChat green 07C160 = R7 G193 B96. Generous window: the 1.5px white border and
# the rounded-corner antialiasing pull edge pixels toward white.
$green = [BS]::Scan($Left, $Top, $W, $H, 0, 70, 150, 235, 40, 150)
$f = $green -split ','
Write-Output ("   green blob = {0} px  bbox ({1},{2})-({3},{4})" -f $f[0], $f[1], $f[2], $f[3], $f[4])

# QQ blue 12B7F5 = R18 G183 B245
$blue = [BS]::Scan($Left, $Top, $W, $H, 0, 70, 150, 215, 210, 255)
$fb = $blue -split ','
Write-Output ("   blue  blob = {0} px  bbox ({1},{2})-({3},{4})" -f $fb[0], $fb[1], $fb[2], $fb[3], $fb[4])

Check ([int]$f[0] -gt 200) "ball is painted (green px $($f[0]))" "no WeChat-green blob found ($($f[0]) px)"

if ([int]$f[0] -gt 200) {
  $bw = [int]$f[3] - [int]$f[1] + 1
  $bh = [int]$f[4] - [int]$f[2] + 1
  Write-Output ("   ball at screen {0},{1}  ~{2}x{3}" -f ($Left + [int]$f[1]), ($Top + [int]$f[2]), $bw, $bh)
  # The ball no longer has a border, and the colour is an inset ring, so the green
# blob is NOT the ball's diameter any more -- measuring it against 34dip would be
# measuring the wrong object. What is checkable: the ring band is a fraction of
# the ball, neither a hairline nor a filled disc.
$ballW = 34 * $Dpi
$frac = [Math]::Round($bw / $ballW, 2)
Check ($frac -ge 0.38 -and $frac -le 0.62) ("green disc is small: {0} of the ball width ({1}px of {2}px)" -f $frac, $bw, [int]$ballW) ("green disc is the wrong size: {0}px = {1} of the ball" -f $bw, $frac)
  # the island body right edge is at MX + ExpW; the ball must start beyond it
  $bodyRightRel = [int](($MX + $ExpW) * $Dpi)
  Check ([int]$f[1] -ge $bodyRightRel) "ball sits OUTSIDE the island body (ball x $($f[1]) >= body right $bodyRightRel)" "ball overlaps the island body (ball x $($f[1]) < body right $bodyRightRel)"
}

# --- donut check -----------------------------------------------------------
# Counting green pixels cannot tell a ring from a filled disc: both pass. What
# separates them is the CENTRE. A donut leaves the middle showing the island
# material, so the middle must NOT be the app colour while the ring at ~0.7R must
# be. This is the assertion that would have caught shipping a solid ball.
if ([int]$f[0] -gt 200) {
  $cx = $Left + [int]$f[1] + [int](($f[3] - $f[1]) / 2)
  $cy = $Top + [int]$f[2] + [int](($f[4] - $f[2]) / 2)
  $r = ([int]$f[3] - [int]$f[1]) / 2
  # The core is a SOLID disc, margin 9dip -> diameter 16dip = 24px inside a
  # 51px ball. Sampling at 0.5R of the detected blob lands inside the disc.
  $rx = [int]($cx + $r * 0.50)

  $mid = [BS]::Sample($cx, $cy)          # centre of the ball
  $rim = [BS]::Sample($rx, $cy)          # on the ring
  Write-Output ("   centre rgb = {0}   ring rgb = {1}" -f $mid, $rim)

  $midParts = $mid -split ','
  $rimParts = $rim -split ','
  $midGreen = ([int]$midParts[1] -gt 130 -and [int]$midParts[0] -lt 90)
  $rimGreen = ([int]$rimParts[1] -gt 130 -and [int]$rimParts[0] -lt 90)

  Check ($rimGreen) "core shows the app colour" ("core is not app-coloured ({0})" -f $rim)
  Check ($midGreen) "centre is SOLID app colour, not hollow (centre {0})" ("centre is hollow ({0}) -- the disc must be filled" -f $mid)
}

# --- the colour must be an INNER ring, and there must be no border -----------
# The ball is 34dip = 51px across, so its outer radius is 25.5px. The coloured
# band peaks at 9.4dip = 14.1px. If green reaches beyond ~85% of the ball radius
# then the ring is sitting on the outer edge again (that is the look that got
# rejected: "???????").
if ([int]$f[0] -gt 200) {
  $cx = $Left + [int]$f[1] + [int](($f[3] - $f[1]) / 2)
  $cy = $Top + [int]$f[2] + [int](($f[4] - $f[2]) / 2)
  $ballR = 34 * $Dpi / 2
  $far = 0.0
  for ($yy = [int]$f[2]; $yy -le [int]$f[4]; $yy += 2) {
    for ($xx = [int]$f[1]; $xx -le [int]$f[3]; $xx += 2) {
      $s = [BS]::Sample(($Left + $xx), ($Top + $yy)) -split ','
      if ([int]$s[1] -gt 130 -and [int]$s[0] -lt 90) {
        $d = [Math]::Sqrt([Math]::Pow(($xx - [int](($f[3] - $f[1]) / 2) - $f[1]), 2) + [Math]::Pow(($yy - [int](($f[4] - $f[2]) / 2) - $f[2]), 2))
        if ($d -gt $far) { $far = $d }
      }
    }
  }
  $ratio = [Math]::Round($far / $ballR, 2)
  Write-Output ("   colour reaches {0}% of the ball radius" -f [Math]::Round($ratio * 100))
  Check ($ratio -lt 0.75) ("solid core sits inside a material ring ({0} of radius)" -f $ratio) ("colour reaches the outer edge ({0} of radius) -- no material ring left" -f $ratio)
}

Write-Output ""
Write-Output ("RESULT fail={0}" -f $fail)
exit $fail