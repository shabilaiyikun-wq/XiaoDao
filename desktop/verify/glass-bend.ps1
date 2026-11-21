param(
  [string]$Exe = "",
  [string]$Scene = "clip",
  [double]$ExpW = 468, [double]$ExpH = 268,
  [switch]$KeepArtifacts
)

# Keep this file ASCII-only: Windows PowerShell reads .ps1 using the ANSI
# codepage, so non-ASCII bytes here break the parser.
#
# Glass rim refraction: end-to-end acceptance on the real composited window.
#
# Division of labour with the unit probe (notes/probes/probe-glass-math.cs):
#   probe  - compiles desktop/Glass.cs and measures the math directly:
#            displacement direction and magnitude, R-vs-B channel separation,
#            cost, and the same numbers at 100/125/150/200% scaling.
#   this   - proves what a user can actually see, on one real window:
#            0. every run suppresses the desktop pet (a separate topmost window
#               that sits on the island rim and would cover the measured band)
#            1. run-to-run noise floor is understood before anything is trusted
#            2. only the rim band changed - the island interior is pixel-exact
#               with and without --no-bend
#            3. the rim really changed (measurable, and it is the only place)
#            4. nothing got brighter than the backdrop behind it
#            5. live desktop sampling still reaches the glass
#            6. other looks are untouched and never build the layer
#
# Fixture: a screen-anchored grey sawtooth card behind the island (see the Paint
# bright 200 at +25 DIP from the island's left border, plus a static list scene.
# The grey ramp is the "no added light" reference: refraction may only resample
# pixels that already exist, so nothing on screen may exceed 200.
#
# Note on measuring dispersion here: the island draws its own coloured UI over
# most of the rim band, so a differential R-B reading over the band carries
# content residue of the same order as the 2.6 DIP channel split. The in-band
# R-B numbers are printed for the record, but dispersion is *asserted* by the
# probe (which has no UI in the way), not here.
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
if (-not ("Snap" -as [type])) {
  Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition (Get-Content (Join-Path $PSScriptRoot "snap-helper.cs") -Raw -Encoding UTF8)
}
[void][Snap]::SetProcessDPIAware()
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
'@

# Robust region comparison. max-delta is the wrong statistic: the list page has a
# blinking caret and a relative timestamp, so a couple of dozen pixels out of
# 280k can differ by ~9 levels in any run, while the refraction moves whole rows.
# A 99.9th percentile plus the mean separates "a caret blinked" from "the glass
# changed".
if (-not ("Robust" -as [type])) {
  $robustSrc = @'
using System;
public static class Robust {
    // { meanAbs, p999, countOver4, total }
    public static double[] Diff(byte[] a, byte[] b, int stride, int x, int y, int w, int h) {
        int n = w * h;
        if (n <= 0) return new double[] { 0, 0, 0, 0 };
        byte[] d = new byte[n];
        double sum = 0; int k = 0;
        for (int j = 0; j < h; j++) {
            int row = (y + j) * stride;
            for (int i = 0; i < w; i++) {
                int o = row + (x + i) * 4;
                double la = 0.2126 * a[o + 2] + 0.7152 * a[o + 1] + 0.0722 * a[o];
                double lb = 0.2126 * b[o + 2] + 0.7152 * b[o + 1] + 0.0722 * b[o];
                double dv = Math.Abs(la - lb);
                sum += dv;
                d[k++] = (byte)(dv > 255 ? 255 : (int)dv);
            }
        }
        Array.Sort(d);
        int idx = (int)(n * 0.999); if (idx >= n) idx = n - 1;
        int over = 0;
        for (int i = 0; i < n; i++) if (d[i] > 4) over++;
        return new double[] { sum / n, d[idx], over, n };
    }
}
'@
  Add-Type -TypeDefinition $robustSrc
}

$scr = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$ScrW = $scr.Width; $ScrH = $scr.Height; $stride = $ScrW * 4
$fail = 0
$unstable = 0
$tmp = Join-Path $env:TEMP "winisland-glass-bend"
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

function Check($cond, $ok, $bad) {
  if ($cond) { Write-Output "   ok   $ok" } else { Write-Output "   FAIL $bad"; $script:fail++ }
}
function Fmt2($v) { return ([double]$v).ToString("0.00") }
if ($Exe -eq "") { $Exe = Join-Path (Split-Path $PSScriptRoot) "WinIsland.exe" }
if (-not (Test-Path $Exe)) { Write-Output "FAIL exe not found: $Exe"; exit 1 }
Write-Output ("   exe: {0}" -f $Exe)
Write-Output ("   screen: {0}x{1}   scene: {2}" -f $ScrW, $ScrH, $Scene)

<#
  One launch + one screenshot.
    -Mode ramp  : grey ramp card (the fixture for geometry / no-added-light)
    -Mode solid : solid card that starts RED and turns BLUE (live sampling)
#>
function Shot {
  param([string]$label, [string[]]$extra, [string]$Mode = "ramp")

  Get-Process -Name WinIsland -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }
  Start-Sleep -Milliseconds 120

  $card = New-Object System.Windows.Forms.Form
  $card.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
  $card.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
  # The card must cover the island plus the margin the refraction samples from,
  # from before the app's first masked sample onwards. Smaller than that leaves
  # live desktop pixels inside the measured band whenever something else on the
  # machine repaints - this box is only a placeholder, the real placement comes
  # from the island rect below.
  $card.Bounds = New-Object System.Drawing.Rectangle(0, 0, $ScrW, $ScrH)
  $card.BackColor = [System.Drawing.Color]::FromArgb(40, 40, 40)
  # NOT topmost on purpose. The island is a topmost window, so a normal card is
  # always behind it and the two can never fight over z-order. When the card was
  # topmost as well, the island sometimes ended up underneath it and a run would
  # screenshot the bare ramp instead of the island - which showed up as a
  # run-vs-run delta of 149 out of 255 with identical arguments.
  $card.TopMost = $false
  $card.ShowInTaskbar = $false
  $script:edgeA = 0
  $script:edgeB = 0
  if ($Mode -eq "ramp" -and $script:knownEdge -gt 0) {
    $d = $script:knownDpi
    $script:edgeA = [int]($script:knownEdge - [math]::Round(25 * $d))
    $script:edgeB = [int]($script:knownEdge + [math]::Round(25 * $d))
  }
  if ($Mode -eq "solid") { $card.BackColor = [System.Drawing.Color]::FromArgb(255, 0, 0) }


  if ($Mode -eq "ramp") {
    # A screen-fixed grey ramp, dark 40 at (edge - 25dpi) rising to 200 at
    # (edge + 25dpi), flat outside. "edge" is the island's left border, learned
    # from the probe launch below; the card covers the whole screen and never
    # moves, so this gradient lands in the same screen pixels on every launch.
    # That is what makes the fixture reproducible: the frozen frame no longer
    # depends on which of the app's masked samples happened to be last.
    $script:cardPaint = {
      param($sender, $ev)
      $g = $ev.Graphics
      $w = $sender.ClientSize.Width
      $h = $sender.ClientSize.Height
      $dark = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(40,40,40))
      $bright = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(200,200,200))
      $g.FillRectangle($dark, 0, 0, $w, $h)
      $a = [int]$script:edgeA
      $b = [int]$script:edgeB
      if ($b -gt $a) {
        $lg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
          (New-Object System.Drawing.Point($a, 0)),
          (New-Object System.Drawing.Point($b, 0)),
          [System.Drawing.Color]::FromArgb(40,40,40),
          [System.Drawing.Color]::FromArgb(200,200,200))
        $g.FillRectangle($lg, $a, 0, $b - $a, $h)
        $lg.Dispose()
        if ($b -lt $w) { $g.FillRectangle($bright, $b, 0, $w - $b, $h) }
      }
      $dark.Dispose(); $bright.Dispose()
    }
    $card.Add_Paint($script:cardPaint)
  }
  $card.Show()
  [System.Windows.Forms.Application]::DoEvents()

  $log = Join-Path $tmp ("bend-" + $label + ".log")
  Remove-Item $log -ErrorAction SilentlyContinue

  $argsList = @("--state=expanded", "--scene=$Scene", "--freeze-shot=3500", "--no-pet", "--log=$log")
  $argsList += $extra
  $p = Start-Process -FilePath $Exe -ArgumentList $argsList -PassThru

  # wait for the island to reach its final size at a stable position
  $rect = $null; $prevSized = $null; $dpi = 1.0
  $sw = [Diagnostics.Stopwatch]::StartNew()
  while ($sw.ElapsedMilliseconds -lt 6000) {
    $cur = [Snap]::WindowRectOf($p.Id)
    if ($cur -ne $null) {
      $h = [Snap]::MainHwnd($p.Id)
      if ($h -ne [IntPtr]::Zero) {
        $d = [W.U]::GetDpiForWindow($h)
        if ($d -gt 0) { $dpi = $d / 96.0 }
      }
      $wantW = [int][math]::Round(($ExpW + 48) * $dpi)
      $wantH = [int][math]::Round(($ExpH + 16 + 34) * $dpi)
      $sized = ([math]::Abs($cur[2]-$wantW) -le 2) -and ([math]::Abs($cur[3]-$wantH) -le 2)
      if ($sized -and $prevSized -and $prevSized[0] -eq $cur[0] -and $prevSized[1] -eq $cur[1]) { $rect = $cur; break }
      if ($sized) { $prevSized = $cur } else { $prevSized = $null }
    }
    Start-Sleep -Milliseconds 10
  }
  if (-not $rect) { Write-Output "FAIL[$label] window never settled"; $card.Close(); $p.Kill(); return $null }

  $ix = $rect[0] + [int][math]::Round(24 * $dpi)
  $iy = $rect[1] + [int][math]::Round(16 * $dpi)
  $iw = [int][math]::Round($ExpW * $dpi)
  $ih = [int][math]::Round($ExpH * $dpi)

  # The card is NOT moved from here on: it already covers the screen and its
  # pattern is anchored to screen coordinates, so the fixture is identical no
  # matter when the app takes its samples.

  $hw = [Snap]::MainHwnd($p.Id)
  if ($hw -ne [IntPtr]::Zero) { [void][W.U]::SetWindowPos($hw, [IntPtr](-1), 0, 0, 0, 0, 0x0013) }

  if ($Mode -eq "solid") {
    $wait = 900 - $sw.ElapsedMilliseconds
    if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
    $card.BackColor = [System.Drawing.Color]::FromArgb(0, 0, 255)
    $card.Refresh(); [System.Windows.Forms.Application]::DoEvents()
  }

  # screenshot off the app's own FREEZE line, never a wall clock
  $frozen = $false
  $sw2 = [Diagnostics.Stopwatch]::StartNew()
  while ($sw2.ElapsedMilliseconds -lt 12000) {
    if ((Test-Path $log) -and ((Get-Content $log -Raw -Encoding UTF8) -match 'FREEZE')) { $frozen = $true; break }
    Start-Sleep -Milliseconds 20
  }
  if (-not $frozen) { Write-Output "FAIL[$label] no FREEZE"; $p.Kill(); $card.Close(); return $null }
  Start-Sleep -Milliseconds 150

  $now = [Snap]::WindowRectOf($p.Id)
  if ($now -ne $null -and ($now[0] -ne $rect[0] -or $now[1] -ne $rect[1])) {
    Write-Output ("   FAIL[$label] island moved after card placement: {0},{1} -> {2},{3}" -f $rect[0],$rect[1],$now[0],$now[1])
    $script:fail++
  }

  $buf = [Snap]::Grab($ScrW, $ScrH)
  $p.WaitForExit(8000) | Out-Null
  if (-not $p.HasExited) { $p.Kill() }
  $card.Close(); $card.Dispose()
  [System.Windows.Forms.Application]::DoEvents()

  $logText = ""
  if (Test-Path $log) { $logText = Get-Content $log -Raw -Encoding UTF8 }
  if ($KeepArtifacts) {
    [Snap]::Save($buf, $ScrW, $ScrH, ($ix - [int](8*$dpi)), ($iy - [int](8*$dpi)),
                 ($iw + [int](16*$dpi)), ($ih + [int](16*$dpi)), (Join-Path $tmp ("bend-" + $label + ".png")))
  }
  return @{ buf=$buf; ix=$ix; iy=$iy; iw=$iw; ih=$ih; dpi=$dpi; log=$logText; label=$label }
}

function RegionDelta {
  param($a, $b, [int]$x, [int]$y, [int]$w, [int]$h)
  return [Robust]::Diff($a.buf, $b.buf, $stride, $x, $y, $w, $h)
}

<#
  Differential channel split between two runs, printed for the record only.
  Returns @(meanSigned, maxAbs, meanAbs).
#>
function RbDelta {
  param($a, $b, [int]$x, [int]$y, [int]$w, [int]$h)
  [long]$sum = 0; $max = 0; $absSum = 0; $n = 0
  for ($j = $y; $j -lt $y + $h; $j++) {
    $row = $j * $stride
    for ($i = $x; $i -lt $x + $w; $i++) {
      $o = $row + $i * 4
      $da = [int]$a.buf[$o + 2] - [int]$a.buf[$o]
      $db = [int]$b.buf[$o + 2] - [int]$b.buf[$o]
      $d = $da - $db
      $ad = [math]::Abs($d)
      $sum += $d; $absSum += $ad
      if ($ad -gt $max) { $max = $ad }
      $n++
    }
  }
  if ($n -eq 0) { return @(0, 0, 0) }
  return @(($sum / $n), $max, ($absSum / $n))
}

Write-Output ""
Write-Output "=== runs ==="
$script:knownEdge = 0
$script:knownDpi = 1.0
$probe = Shot -label "probe" -extra @("--look=glass")
if ($probe -eq $null) { Write-Output "FAIL the geometry probe launch produced no screenshot"; exit 1 }
$script:knownEdge = $probe.ix
$script:knownDpi = $probe.dpi
Write-Output ("   geometry probe: island left border at x={0}, dpi {1} (the fixture ramp is pinned here from now on)" -f $probe.ix, $probe.dpi)
# Interleaved A/B: on, off, on, off. Two same-argument launches give a control for
# each arm, so the script can tell "the effect is wrong" apart from "something else
# on this machine repainted while I was measuring". Any pair of identical launches
# is by definition free of the effect, so a non-zero control is fixture noise - the
# verdict then becomes inconclusive instead of blaming the code.
$on1  = Shot -label "on1"  -extra @("--look=glass")
$off1 = Shot -label "off1" -extra @("--look=glass","--no-bend")
$on2  = Shot -label "on2"  -extra @("--look=glass")
$off2 = Shot -label "off2" -extra @("--look=glass","--no-bend")
foreach ($s in @($on1,$off1,$on2,$off2)) {
  if ($s -eq $null) { Write-Output "FAIL a glass run produced no screenshot"; exit 1 }
}
$on = $on1

$posOk = $true
foreach ($s in @($off1,$on2,$off2)) {
  if (([math]::Abs($s.ix - $on.ix) -gt 1) -or ([math]::Abs($s.iy - $on.iy) -gt 1)) { $posOk = $false }
}
Write-Output ("   island: on1 {0},{1}  off1 {2},{3}  on2 {4},{5}  off2 {6},{7}   size {8}x{9} dpi {10}" -f `
  $on1.ix,$on1.iy,$off1.ix,$off1.iy,$on2.ix,$on2.iy,$off2.ix,$off2.iy,$on.iw,$on.ih,$on.dpi)
Write-Output ("   log: {0}" -f $(if ($on.log -match 'GLASS bend .*cost=([0-9.]+)ms avg=([0-9.]+)') { "cost=" + $matches[1] + "ms avg=" + $matches[2] + "ms" } else { "no GLASS bend" }))
Check ($posOk) "the island landed in the same place for every run" "the island moved between runs - all comparisons below are void"
if (-not $posOk) { Write-Output ("RESULT fail={0} unstable=0" -f $fail); exit $fail }

Check ($on.log -match 'GLASS bend ') "the refraction layer rendered (GLASS bend trace present)" "no GLASS bend trace - the layer never turned on"
Check (-not ($on.log -match 'GLASS bend OFF')) "the effect did not auto-disable on cost" "GLASS bend OFF was traced - the layer gave up"
$onBend = [regex]::Match($on.log, 'band=([0-9.]+) kick=([0-9.]+) split=([0-9.]+)')
if ($onBend.Success) { Write-Output ("   look: band={0} kick={1} split={2}" -f $onBend.Groups[1].Value, $onBend.Groups[2].Value, $onBend.Groups[3].Value) }

$dpi = $on.dpi
$rimX = $on.ix
$rimY = $on.iy + [int][math]::Round(45 * $dpi)
$rimW = [int][math]::Round(24 * $dpi)
$rimH = $on.ih - [int][math]::Round(90 * $dpi)
$inX = $on.ix + [int][math]::Round(60 * $dpi)
$inY = $on.iy + [int][math]::Round(55 * $dpi)
$inW = $on.iw - [int][math]::Round(120 * $dpi)
$inH = $on.ih - [int][math]::Round(110 * $dpi)

Write-Output ""
Write-Output "--- 1. fixture controls (identical arguments, effect-free by definition) ---"
$cOn  = RegionDelta $on1  $on2  $on.ix $on.iy $on.iw $on.ih
$cOff = RegionDelta $off1 $off2 $on.ix $on.iy $on.iw $on.ih
Write-Output ("   on-vs-on   mean={0} p999={1}" -f (Fmt2 $cOn[0]),  (Fmt2 $cOn[1]))
Write-Output ("   off-vs-off mean={0} p999={1}" -f (Fmt2 $cOff[0]), (Fmt2 $cOff[1]))
$control = [math]::Max($cOn[0], $cOff[0])
$stable = ($control -le 0.25)
if (-not $stable) {
  Write-Output ("   NOTE the fixture itself moved between identical launches (control mean {0});" -f (Fmt2 $control))
  Write-Output "        pixel assertions below cannot separate that from the effect."
}

Write-Output ""
Write-Output "--- 2. the change is confined to the rim band ---"
$p1in  = RegionDelta $off1 $on1 $inX $inY $inW $inH
$p2in  = RegionDelta $off2 $on2 $inX $inY $inW $inH
$p1rim = RegionDelta $off1 $on1 $rimX $rimY $rimW $rimH
$p2rim = RegionDelta $off2 $on2 $rimX $rimY $rimW $rimH
Write-Output ("   interior  pair1 mean={0} p999={1}   pair2 mean={2} p999={3}   (control {4})" -f `
  (Fmt2 $p1in[0]), (Fmt2 $p1in[1]), (Fmt2 $p2in[0]), (Fmt2 $p2in[1]), (Fmt2 $control))
Write-Output ("   rim band  pair1 mean={0} p999={1}   pair2 mean={2} p999={3}" -f `
  (Fmt2 $p1rim[0]), (Fmt2 $p1rim[1]), (Fmt2 $p2rim[0]), (Fmt2 $p2rim[1]))

# Both pairs must show it, and the interior must not move more than the control.
$inOk  = (($p1in[0]  -le ($control + 0.2)) -and ($p2in[0]  -le ($control + 0.2)))
$rimOk = (($p1rim[0] -gt 2.0) -and ($p2rim[0] -gt 2.0))
$concOk = (($p1rim[0] -gt $p1in[0] * 5) -and ($p2rim[0] -gt $p2in[0] * 5))

if (-not $stable) {
  $script:unstable++
  Write-Output "   SKIP pixel verdict: the fixture was not stable, so these numbers prove nothing either way"
} else {
  Check ($inOk) "the island interior is untouched by the effect ($((Fmt2 $p1in[0])) / $((Fmt2 $p2in[0])) vs control $((Fmt2 $control)))" "the interior moved ($((Fmt2 $p1in[0])) / $((Fmt2 $p2in[0])) vs control $((Fmt2 $control))) - the effect leaks past the rim band"
  Check ($rimOk) "the rim band changed in both pairs ($((Fmt2 $p1rim[0])) / $((Fmt2 $p2rim[0])) levels mean)" "the rim band did not change in both pairs ($((Fmt2 $p1rim[0])) / $((Fmt2 $p2rim[0]))) - the layer is not visible"
  Check ($concOk) "the change is concentrated in the band, not the interior" "the band barely differs from the interior"
}

Write-Output ""
Write-Output "--- 3. colour dispersion (recorded; the probe asserts the numbers) ---"
$dRbRim = RbDelta $on1 $off1 $rimX $rimY $rimW $rimH
$dRbIn  = RbDelta $on1 $off1 $inX  $inY  $inW  $inH
$dRbNil = RbDelta $on1 $on2  $rimX $rimY $rimW $rimH
Write-Output ("   rim  bent-vs-flat: signed={0} maxAbs={1} meanAbs={2}" -f (Fmt2 $dRbRim[0]), (Fmt2 $dRbRim[1]), (Fmt2 $dRbRim[2]))
Write-Output ("   rim  run-vs-run : signed={0} maxAbs={1} meanAbs={2}   (control)" -f (Fmt2 $dRbNil[0]), (Fmt2 $dRbNil[1]), (Fmt2 $dRbNil[2]))
Write-Output ("   interior bent-vs-flat: signed={0} maxAbs={1} meanAbs={2}" -f (Fmt2 $dRbIn[0]), (Fmt2 $dRbIn[1]), (Fmt2 $dRbIn[2]))
Write-Output "   (the island's own UI covers most of the band, so this stays a diagnostic;"
Write-Output "    probe-glass-math.cs asserts the channel separation: 2.0-3.0 DIP, R above B)"

Write-Output ""
Write-Output "--- 4. the effect must not add light ---"
$lOff  = [Snap]::RegionLum($off1.buf, $stride, $rimX, $rimY, $rimW, $rimH)
$lOn   = [Snap]::RegionLum($on1.buf,  $stride, $rimX, $rimY, $rimW, $rimH)
$iOff  = [Snap]::RegionLum($off1.buf, $stride, $on.ix, $on.iy, $on.iw, $on.ih)
$iOn   = [Snap]::RegionLum($on1.buf,  $stride, $on.ix, $on.iy, $on.iw, $on.ih)
Write-Output ("   band   no-bend max={0}   bent max={1}" -f (Fmt2 $lOff[2]), (Fmt2 $lOn[2]))
Write-Output ("   island no-bend mean={0}   bent mean={1}" -f (Fmt2 $iOff[0]), (Fmt2 $iOn[0]))
if ($stable) {
  Check ($lOn[2] -le $lOff[2] + 1) "no pixel in the band got brighter than with the effect off ($([int]$lOff[2]) -> $([int]$lOn[2]))" "the band got brighter: $([int]$lOff[2]) -> $([int]$lOn[2])"
} else {
  Write-Output "   SKIP: same reason as above"
}

Write-Output ""
Write-Output "--- 5. the layer is live, not a static bake ---"
# Only that: the layer is fed and drawn while the app runs. Whether the glass
# follows a changing desktop is covered by verify/live-backdrop.ps1, which is the
# existing test for exactly that.
$live = Shot -label "live" -extra @("--look=glass","--exclude-capture")
if ($live -eq $null) { Write-Output "   FAIL live run produced no screenshot"; $fail++ }
else {
  $frames = ""
  if ($live.log -match 'FREEZE sampled=\S+ frames=(\d+)') { $frames = $matches[1] }
  Write-Output ("   frames={0}" -f $frames)
  Check ([int]$frames -gt 8) "live sampling kept running at the fast rate ($frames frames)" "live sampling barely ran ($frames frames)"
  Check ($live.log -match 'GLASS bend ') "the refraction layer rendered in the live run too" "the refraction layer never rendered in the live run"
}

Write-Output ""
Write-Output "--- 6. other looks must be untouched ---"
# Structural assertion first (the layer is never built for those looks - that is
# logged and deterministic), then the pixel check gated on that look's own control.
foreach ($look in @("obsidian","frost","graphite")) {
  $a = Shot -label ($look + "-a") -extra @("--look=$look")
  $b = Shot -label ($look + "-b") -extra @("--look=$look")
  $c = Shot -label ($look + "-c") -extra @("--look=$look","--no-bend")
  if ($a -eq $null -or $b -eq $null -or $c -eq $null) { Write-Output "   FAIL[$look] a run produced no screenshot"; $script:fail++; continue }
  if (([math]::Abs($a.ix - $c.ix) -gt 2) -or ([math]::Abs($a.iy - $c.iy) -gt 2) -or
      ([math]::Abs($a.ix - $b.ix) -gt 2) -or ([math]::Abs($a.iy - $b.iy) -gt 2)) {
    Write-Output "   FAIL[$look] island moved between runs, cannot compare"; $script:fail++; continue
  }
  $n = RegionDelta $a $b $a.ix $a.iy $a.iw $a.ih
  $f = RegionDelta $a $c $a.ix $a.iy $a.iw $a.ih
  Write-Output ("   {0,-9} control mean={1}   with --no-bend mean={2} p999={3}" -f `
    $look, (Fmt2 $n[0]), (Fmt2 $f[0]), (Fmt2 $f[1]))
  Check (-not ($a.log -match 'GLASS bend ')) "  $look never builds the refraction layer" "  $look built the refraction layer"
  if ($n[0] -gt 0.25) {
    $script:unstable++
    Write-Output "   SKIP pixel verdict for $look : identical launches already differ by $((Fmt2 $n[0]))"
  } else {
    Check ($f[0] -le 0.25) "  $look is pixel-identical with the effect disabled ($((Fmt2 $f[0])))" "  $look changed ($((Fmt2 $f[0]))) with the effect disabled"
  }
}

Write-Output ""
if ($KeepArtifacts) { Write-Output ("   crops saved under {0}" -f $tmp) }
if ($unstable -gt 0) {
  Write-Output ("RESULT fail={0} unstable={1}  INCONCLUSIVE - rerun when the machine is idle" -f $fail, $unstable)
} else {
  Write-Output ("RESULT fail={0} unstable=0" -f $fail)
}
if ($fail -gt 0) { exit 1 }
if ($unstable -gt 0) { exit 2 }
exit 0
