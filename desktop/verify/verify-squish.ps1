# End-to-end acceptance for the press-squish feedback.
#
# What it proves that the probe cannot: the feature is wired into the REAL app --
# a physical mouse press through SendInput reaches Layout.OnTap's plate, the
# counter-scaled squash happens, the spring returns, and the click action still fires.
#
# Geometry: the window is the island card grown by 48 DIP horizontally and
# 16 DIP top / 34 DIP bottom (see verify-input.ps1), all times DPI 1.5. The button
# offsets inside the card are NOT guessed: `probe-squish centers <scene> <w> <h>`
# runs the same Layout.Build the app runs, at the island size the app itself
# reports in its heartbeat.
#
# NOTE: another session in this repo rebuilds WinIsland.exe and kills running
# instances while doing so, so every geometry read here is null-safe and the
# script reports "the app went away" rather than crashing.
#
# ASCII-only on purpose: this shell reads .ps1 files with the local ANSI codepage.
param(
  [string]$Exe = "E:\xiaodao\desktop\WinIsland.exe",
  [string]$Scene = "timer",
  [string]$State = "expanded",
  [string]$Look = "glass"
)

Add-Type -AssemblyName System.Drawing
if (-not ("Snap" -as [type])) {
  Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition (Get-Content "E:\xiaodao\desktop\verify\snap-helper.cs" -Raw -Encoding UTF8)
}
[void][Snap]::SetProcessDPIAware()

$dpi = 1.5
$fail = 0
function Check($cond, $msgOk, $msgBad) {
  if ($cond) { Write-Output "   ok   $msgOk" } else { Write-Output "   FAIL $msgBad"; $script:fail++ }
}

function Trim($p) { if ($p -and (Test-Path $p)) { [IO.File]::ReadAllText($p) } else { "" } }

# WindowRectOf returns $null when the window is gone. Another session in this repo
# kills WinIsland.exe while it builds, so every geometry read must be null-safe.
function Get-Rect($procId) {
  $r = [Snap]::WindowRectOf($procId)
  if (-not $r) { return $null }
  if ($r.Length -lt 4) { return $null }
  return $r
}

function Start-Island($extra, $logPath) {
  $a = @("--look=$Look", "--scene=$Scene", "--state=$State", "--heartbeat", "--log=$logPath") + $extra
  return Start-Process -FilePath $Exe -ArgumentList $a -PassThru
}

# Island size the app reports about itself, in DIP. Authoritative -- never derived.
function Get-IslandSize($logPath) {
  $txt = Trim $logPath
  $ms = [regex]::Matches($txt, 'heartbeat island=([\d.]+)x([\d.]+)')
  if ($ms.Count -eq 0) { return $null }
  $last = $ms[$ms.Count - 1]
  return @([double]$last.Groups[1].Value, [double]$last.Groups[2].Value)
}

function Get-Centres($scene, $w, $h) {
  $out = & cmd /c "E:\xiaodao\notes\probes\build-probe-squish.cmd centers $scene $w $h" 2>&1
  $map = @{}
  foreach ($line in $out) {
    $m = [regex]::Match($line, '^center (\S+) ([-\d.]+) ([-\d.]+)')
    if ($m.Success) {
      $map[$m.Groups[1].Value] = @([double]$m.Groups[2].Value, [double]$m.Groups[3].Value)
    }
  }
  return $map
}

# Move the cursor onto the target and converge: hovering can MOVE the window
# (the island keeps its anchor as it grows), and moving the window changes where
# the button is -- so aim, re-read, re-aim, until the rect stops changing.
# Two rounds is normally enough; the cap keeps a pathological case from spinning.
function Aim-At($procId, $c, $w, $h) {
  $last = $null
  for ($round = 0; $round -lt 4; $round++) {
    $r = Get-Rect $procId
    if (-not $r) { return $null }
    $x = $r[0] + [int][math]::Round(24 * $dpi) + [int][math]::Round($c[0] * $dpi)
    $y = $r[1] + [int][math]::Round(16 * $dpi) + [int][math]::Round($c[1] * $dpi)
    [Snap]::MoveCursor($x, $y)
    for ($i = 0; $i -lt 20; $i++) {
      Start-Sleep -Milliseconds 60
      if (-not (Get-Rect $procId)) { return $null }
      if (-not [Snap]::IsClickThrough($procId)) { break }
    }
    Start-Sleep -Milliseconds 200
    $r2 = Get-Rect $procId
    if (-not $r2) { return $null }
    $x2 = $r2[0] + [int][math]::Round(24 * $dpi) + [int][math]::Round($c[0] * $dpi)
    $y2 = $r2[1] + [int][math]::Round(16 * $dpi) + [int][math]::Round($c[1] * $dpi)
    $last = @{ X = $x2; Y = $y2; Rect = $r2 }
    if ($x2 -eq $x -and $y2 -eq $y) { break }   # converged
  }

  # Final placement, then confirm the cursor really is where we put it and that the
  # point lies inside the island card. Another process on this machine drives the
  # cursor too, so "we called MoveCursor" is not evidence that it took effect.
  [Snap]::MoveCursor($last.X, $last.Y)
  Start-Sleep -Milliseconds 160
  $cur = [Snap]::CursorPos()
  $last = @{ X = $last.X; Y = $last.Y; Rect = $last.Rect
             Cursor = $cur
             OnTarget = ([math]::Abs($cur[0] - $last.X) -le 2 -and [math]::Abs($cur[1] - $last.Y) -le 2) }
  return $last
}

Write-Output "=== e2e press-squish / scene=$Scene state=$State ==="

# ---------------------------------------------------------------- baseline run
$log1 = "E:\xiaodao\work\e2e-squish.log"
Remove-Item $log1 -ErrorAction SilentlyContinue
$p1 = Start-Island @() $log1
Start-Sleep -Milliseconds 2400

$isz = Get-IslandSize $log1
if (-not $isz) {
  Write-Output "   FAIL the app never reported an island size (log: $log1)"
  if (-not $p1.HasExited) { $p1.Kill() }
  exit 1
}
Write-Output ("   island {0:0.0}x{1:0.0} DIP (from the app's heartbeat)" -f $isz[0], $isz[1])

$r1 = Get-Rect $p1.Id
if (-not $r1) { Write-Output "   FAIL window not found"; if (-not $p1.HasExited) { $p1.Kill() }; exit 1 }
Write-Output ("   window {0}x{1} at {2},{3}" -f $r1[2], $r1[3], $r1[0], $r1[1])

$centres = Get-Centres $Scene $isz[0] $isz[1]
if ($centres.Count -eq 0) {
  Write-Output "   FAIL the probe reported no clickable controls"
  if (-not $p1.HasExited) { $p1.Kill() }
  exit 1
}
Write-Output ("   probe centres: " + (($centres.Keys | Sort-Object) -join ", "))

$target = $null
foreach ($name in @("timer-plus", "timer-toggle", "timer-reset")) {
  if ($centres.ContainsKey($name)) { $target = $name; break }
}
if (-not $target) { Write-Output "   FAIL no suitable target"; if (-not $p1.HasExited) { $p1.Kill() }; exit 1 }

$c = $centres[$target]
Write-Output ("   target '{0}' at island {1:0.0},{2:0.0} DIP" -f $target, $c[0], $c[1])

$aim = Aim-At $p1.Id $c $isz[0] $isz[1]
if (-not $aim) {
  Write-Output "   FAIL the app went away before the click (another session rebuilding?)"
  if (-not $p1.HasExited) { $p1.Kill() }
  exit 1
}
Write-Output ("   cursor aimed at {0},{1}" -f $aim.X, $aim.Y)
Write-Output ("   cursor actually at {0},{1}" -f $aim.Cursor[0], $aim.Cursor[1])
Check ($aim.OnTarget) "the cursor is where we put it" `
  ("cursor drifted to {0},{1} -- another process is driving the mouse" -f $aim.Cursor[0], $aim.Cursor[1])
Check (-not [Snap]::IsClickThrough($p1.Id)) "cursor over the island clears click-through" "island stayed click-through"

$before = (Trim $log1)
[Snap]::LeftDown()
Start-Sleep -Milliseconds 120
[Snap]::LeftUp()
Start-Sleep -Milliseconds 900
$after1 = (Trim $log1)
if ($after1.Length -le $before.Length) {
  Write-Output "   FAIL the log did not grow -- app died or press never arrived"
  if (-not $p1.HasExited) { $p1.Kill() }
  exit 1
}
$new1 = $after1.Substring($before.Length)

Check ($new1 -match 'squish press') "a real mouse press logs 'squish press'" "no squish press in the log"
Check ($new1 -match 'squish release') "a real mouse release logs 'squish release'" "no squish release in the log"

$mPress = [regex]::Match($new1, 'squish press (\S+) amp=([\d.]+)')
if ($mPress.Success) {
  $amp = [double]$mPress.Groups[2].Value
  Check ($amp -gt 0 -and $amp -le 0.05) `
    ("amplitude is restrained (amp={0:0.0000}, target '{1}')" -f $amp, $mPress.Groups[1].Value) `
    ("amplitude out of range: {0}" -f $amp)
} else {
  Check $false "the press line carries an amplitude" "press line has no amp="
}

Check ($new1 -match 'tap ') "the click action still fires (tap logged)" "no tap line -- the plate swallowed the click"

if (-not $p1.HasExited) { $p1.Kill() }
Start-Sleep -Milliseconds 400

# ------------------------------------------------------------ reduced motion run
# XIAODAO_SQUISH_MOTION=off forces the reduce-motion path deterministically, so this
# does not depend on how the test machine has Windows configured.
$log2 = "E:\xiaodao\work\e2e-squish-reduced.log"
Remove-Item $log2 -ErrorAction SilentlyContinue

$env:XIAODAO_SQUISH_MOTION = "off"
$p2 = Start-Island @() $log2
Start-Sleep -Milliseconds 2400

$isz2 = Get-IslandSize $log2
$aim2 = $null
if ($isz2) { $aim2 = Aim-At $p2.Id $c $isz2[0] $isz2[1] }

if (-not $isz2 -or -not $aim2) {
  # NOTE: this must go through Check so the exit code tells the truth. Printing
  # "FAIL" without counting it would report RESULT fail=0 on a run that never
  # verified the reduce-motion path at all.
  Check $false "the reduce-motion run could be measured" `
    "the app went away before the reduced-motion click"
  if (-not $p2.HasExited) { $p2.Kill() }
  $env:XIAODAO_SQUISH_MOTION = $null
} else {
  $before2 = (Trim $log2)
  [Snap]::LeftDown()
  Start-Sleep -Milliseconds 120
  [Snap]::LeftUp()
  Start-Sleep -Milliseconds 700
  $after2 = (Trim $log2)
  $new2 = ""
  if ($after2.Length -gt $before2.Length) { $new2 = $after2.Substring($before2.Length) }

  Check ($new2 -match 'squish press .* suppressed \(reduced motion\)') `
    "with reduce-motion forced, the press is suppressed" `
    "reduce-motion path did not report suppression"
  Check ($new2 -match 'tap ') "the click still works with reduce-motion on" "click died under reduce-motion"

  $env:XIAODAO_SQUISH_MOTION = $null
  if (-not $p2.HasExited) { $p2.Kill() }
}

Write-Output ""
Write-Output ("RESULT fail={0}" -f $fail)
exit $fail