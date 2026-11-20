param(
  [string]$Exe = "E:\xiaodao\desktop\WinIsland.exe",
  [string]$Look = "frost",
  [string]$Scene = "media",
  [string]$State = "expanded",
  [double]$ExpW = 416,
  [double]$ExpH = 156,
  [double]$IslW = 416,
  [double]$IslH = 156,
  [switch]$ClickThrough,
  [int]$MinContent = 200,
  [switch]$Geometry,
  [switch]$NoWda,
  [string]$AppLog = "",
  [string]$ShotPath = ""
)

# ASCII-only on purpose: this shell reads .ps1 files with the local ANSI codepage,
# so any non-ASCII literal breaks parsing once the BOM is lost.
Add-Type -AssemblyName System.Drawing
if (-not ("Snap" -as [type])) {
  Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition (Get-Content "E:\xiaodao\desktop\verify\snap-helper.cs" -Raw -Encoding UTF8)
}
[void][Snap]::SetProcessDPIAware()

$W = 2560; $H = 1600; $dpi = 1.5; $stride = $W * 4
$fail = 0; $skip = 0
function Check($cond, $msgOk, $msgBad) {
  if ($cond) { Write-Output "   ok   $msgOk" } else { Write-Output "   FAIL $msgBad"; $script:fail++ }
}
function Inconclusive($msg) { Write-Output "   skip $msg"; $script:skip++ }

if ($AppLog -ne "") { Remove-Item $AppLog -ErrorAction SilentlyContinue }
$argList = @("--look=$Look","--scene=$Scene","--state=$State")
if ($AppLog -ne "") { $argList += "--log=$AppLog" }
# 默认程序会把自己从抓屏里排除，岛在任何截图里都看不见，像素验收反而没法量。
# 所以所有抓屏式判据都要带 --no-wda 跑。
Write-Output ("   DEBUG-NOWDA NoWda=" + [string]$NoWda + " args=" + ($argList -join " "))
if ($NoWda) { $argList += "--no-wda" }

$p = Start-Process -FilePath $Exe -ArgumentList $argList -PassThru
Start-Sleep -Milliseconds 1900
$wr0 = [Snap]::WindowRectOf($p.Id)
if (-not $wr0) {
  Write-Output "FAIL window not found"
  if ($p -and -not $p.HasExited) { $p.Kill() }
  exit 1
}
$buf = [Snap]::Grab($W, $H)

$isExp = ($State -eq "expanded")
$winW = [int][math]::Round(($ExpW + 48) * $dpi)
$winH = [int][math]::Round(($ExpH + 16 + 34) * $dpi)
$ix = $wr0[0] + [int][math]::Round(($ExpW + 48 - $IslW) / 2 * $dpi)
$iy = $wr0[1] + [int][math]::Round(16 * $dpi)
$iw = [int][math]::Round($IslW * $dpi)
$ih = [int][math]::Round($IslH * $dpi)
$rPhys = [math]::Min(30, $IslH / 2) * $dpi

Write-Output "$Look / $Scene / $State"
Write-Output ("   window {0}x{1} at {2},{3}   island {4}x{5} at {6},{7}   corner r={8}px" -f `
  $wr0[2], $wr0[3], $wr0[0], $wr0[1], $iw, $ih, $ix, $iy, [math]::Round($rPhys,1))

Check ([math]::Abs($wr0[2]-$winW) -le 6 -and [math]::Abs($wr0[3]-$winH) -le 6) `
  "window size ${winW}x${winH} = island + padding, fixed across states" `
  "window size mismatch ($($wr0[2])x$($wr0[3]), expected ${winW}x${winH})"

# ---------------------------------------------------------------------------
# Geometry.
#   deskL = mode luma of the window margin, which is proven transparent, so it
#           is whatever is on the desktop behind the island
#   fillL = mode luma of the island interior, i.e. the panel fill. Mode, not
#           mean: ink and icons are a minority of pixels and would skew a mean.
# Every probe is then a binary classification at the midpoint between the two.
# That is immune to the specular highlight, edge highlights, antialiasing and
# content, all of which stay on the fill's side of the midpoint; and to the drop
# shadow, which only makes the backdrop darker and so stays on its own side.
# If the two references are not far enough apart the probes are skipped rather
# than forced into a verdict.
# ---------------------------------------------------------------------------
if ($Geometry) {
  $deskL = [Snap]::ModeLuma($buf, $stride, $wr0[0]+3, $wr0[1]+3, 12, 12)
  $fillL = [Snap]::ModeLuma($buf, $stride, $ix+4, $iy+4, $iw-8, $ih-8)
  $sep = [math]::Abs($fillL - $deskL)
  $mid = ($fillL + $deskL) / 2
  if ($fillL -gt $deskL) { $lo = $mid; $hi = 255.0 } else { $lo = 0.0; $hi = $mid }

  Write-Output ("   refs: backdrop={0}  fill={1}  separation={2}  island-side luma band=[{3},{4}]" -f `
    [math]::Round($deskL,1), [math]::Round($fillL,1), [math]::Round($sep,1),
    [math]::Round($lo,1), [math]::Round($hi,1))

  if ($sep -lt 40) {
    Inconclusive "fill and backdrop too similar (separation $([math]::Round($sep,1)) < 40); geometry not decidable on this backdrop"
  } else {
    # G1 rendered width: extreme island-side pixels on the vertical centre line.
    # The centre row is the only full-width row; interior content does not move
    # the min/max because the panel edges are the outermost island-side pixels.
    $midY = $iy + [int]($ih / 2)
    $cx = $ix + [int]($iw / 2)
    $span = [Snap]::FillRun($buf, $stride, $midY, $cx,
                            [math]::Max(0,$ix-60), [math]::Min($W-1,$ix+$iw+60), $lo, $hi,
                            [int][math]::Round(4 * $dpi))
    Write-Output ("   centre row: left={0} right={1} width={2}px (expect {3}, left edge {4})" -f `
      $span[0], $span[1], $span[2], $iw, $ix)
    Check ($span[2] -gt 0 -and [math]::Abs($span[2] - $iw) -le 8 -and [math]::Abs($span[0] - $ix) -le 8) `
      "measured island width $($span[2])px matches ${IslW} DIP" `
      "measured width off ($($span[2])px, expected $iw, left $($span[0]) vs $ix)"

    # G2 expanded only: both ends of the top band are island-side, so the painted
    # edge sits exactly on the declared box, and nothing is painted just outside.
    if ($isExp) {
      $rowY = $iy + 10
      $endW = 24
      $inL = [Snap]::InRangeRatio($buf, $stride, $ix+4, $rowY, $endW, 6, $lo, $hi)
      $inR = [Snap]::InRangeRatio($buf, $stride, $ix+$iw-4-$endW, $rowY, $endW, 6, $lo, $hi)
      $outL = [Snap]::InRangeRatio($buf, $stride, $ix-16, $rowY, 11, 6, $lo, $hi)
      $outR = [Snap]::InRangeRatio($buf, $stride, $ix+$iw+5, $rowY, 11, 6, $lo, $hi)
      Check ([math]::Min($inL,$inR) -gt 0.95) `
        "top band reaches both box edges (L $([math]::Round($inL*100,1))% / R $([math]::Round($inR*100,1))%)" `
        "top band stops short of the box edge (L $([math]::Round($inL*100,1))% / R $([math]::Round($inR*100,1))%)"
      Check ([math]::Max($outL,$outR) -lt 0.15) `
        "nothing painted outside the island box (L $([math]::Round($outL*100,1))% / R $([math]::Round($outR*100,1))%)" `
        "paint overflows the island box (L $([math]::Round($outL*100,1))% / R $([math]::Round($outR*100,1))%)"
    }

    # G3 corners must classify as backdrop. An unclipped rectangle would classify
    # as island here, which is the whole point of the test.
    $k = [int][math]::Round(2 * $dpi)
    $msgs = @(); $worst = 999.0; $worstN = ""
    $corners = @(
      @{ N="TL"; X=$ix;        Y=$iy },
      @{ N="TR"; X=$ix+$iw-$k; Y=$iy },
      @{ N="BL"; X=$ix;        Y=$iy+$ih-$k },
      @{ N="BR"; X=$ix+$iw-$k; Y=$iy+$ih-$k }
    )
    foreach ($c in $corners) {
      $l = [Snap]::RegionLum($buf, $stride, $c.X, $c.Y, $k, $k)[0]
      $dToFill = [math]::Abs($l - $fillL)
      if ($dToFill -lt $worst) { $worst = $dToFill; $worstN = $c.N }
      $msgs += ("{0}={1}" -f $c.N, [math]::Round($l,1))
    }
    Check ($worst -gt ($mid - [math]::Min($fillL,$deskL)) * 0.55) `
      ("all four corners fall on the backdrop side (" + ($msgs -join " ") + ")") `
      ("corner $worstN reads as fill: luma " + [math]::Round(($fillL - $worst),1) + " (fill " + [math]::Round($fillL,1) + ", backdrop " + [math]::Round($deskL,1) + ")")

    # G4 continuous corner. On the diagonal at 0.23r a superellipse is already
    # filled while a plain circular arc is still outside, so this distinguishes
    # a continuous corner from a round one. Expanded only: compact and idle put
    # their cover thumbnail inside the corner, which would poison the probe.
    if ($isExp) {
      $dd = [int][math]::Round($rPhys * 0.23)
      $pl = [Snap]::RegionLum($buf, $stride, $ix+$dd, $iy+$dd, 5, 5)[0]
      $onIsland = ($pl -ge $lo -and $pl -le $hi)
      Check ($onIsland) `
        "corner is fuller than a circular arc: island-side fill at diagonal d=$dd px (0.23r), luma $([math]::Round($pl,1))" `
        "corner too round: no fill at diagonal d=$dd px (luma $([math]::Round($pl,1)), fill $([math]::Round($fillL,1)), backdrop $([math]::Round($deskL,1)))"
    }
  }
}

# ---- Content: frost draws dark ink on white; the dark looks draw light ink.
if ($Look -eq "frost") {
  $cnt = [Snap]::CountBelow($buf, $stride, $ix+4, $iy+4, $iw-8, $ih-8, 150.0)
  $what = "dark px"
} else {
  $cnt = [Snap]::CountAbove($buf, $stride, $ix+4, $iy+4, $iw-8, $ih-8, 150.0)
  $what = "light px"
}
Check ($cnt -gt $MinContent) "island has real content ($cnt $what)" "island looks empty ($cnt $what, need > $MinContent)"

# ---- Click-through: move the real cursor, then poll WS_EX_TRANSPARENT with a
# bound, reporting convergence latency instead of trusting a fixed instant.
if ($ClickThrough) {
  function WaitThrough($procId, $expect, $timeoutMs) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $timeoutMs) {
      if ([Snap]::IsClickThrough($procId) -eq $expect) { return [int]$sw.ElapsedMilliseconds }
      Start-Sleep -Milliseconds 12
    }
    return -1
  }

  $orig = [Snap]::CursorPos()
  $cx = $ix + [int]($iw/2)
  $cy = $iy + [int]($ih/2)

  [Snap]::MoveCursor(60, 1500)
  $msAway = WaitThrough $p.Id $true 1200

  [Snap]::MoveCursor($cx, $cy)
  $msOver = WaitThrough $p.Id $false 1200
  $wr1 = [Snap]::WindowRectOf($p.Id)

  [Snap]::MoveCursor(60, 1500)
  $msBack = WaitThrough $p.Id $true 1200
  $wr2 = [Snap]::WindowRectOf($p.Id)

  [Snap]::MoveCursor($orig[0], $orig[1])

  Write-Output "   click-through convergence: away=${msAway}ms over=${msOver}ms back=${msBack}ms (-1 = never)"
  Check ($msAway -ge 0) "click-through while cursor is away (${msAway}ms)" "never became click-through while cursor away"
  Check ($msOver -ge 0) "interactive over the island (${msOver}ms)" "never became interactive over the island"
  Check ($msBack -ge 0) "click-through restored after leaving (${msBack}ms)" "never restored click-through after leaving"
  Check ($wr1[2] -eq $wr2[2] -and $wr1[3] -eq $wr2[3]) `
    "window size stable across the hover cycle ($($wr2[2])x$($wr2[3]))" "window was resized during the hover cycle"
}

if ($ShotPath -ne "") {
  $pad = 30
  $sx = [math]::Max(0, $wr0[0] - $pad)
  $sy = [math]::Max(0, $wr0[1] - $pad)
  $sw = [math]::Min($W - $sx, $wr0[2] + $pad*2)
  $sh = [math]::Min($H - $sy, $wr0[3] + $pad*2)
  [Snap]::Save($buf, $W, $H, $sx, $sy, $sw, $sh, $ShotPath)
  Write-Output "   shot $([IO.Path]::GetFileName($ShotPath)) (${sw}x${sh})"
}

if (-not $p.HasExited) { $p.Kill() }
Start-Sleep -Milliseconds 150

if ($AppLog -ne "" -and (Test-Path $AppLog)) {
  Write-Output "   --- app trace ---"
  Get-Content $AppLog -Encoding UTF8 | ForEach-Object { "   $_" }
  Write-Output "   -----------------"
}

Write-Output ""
Write-Output ("RESULT fail={0} skip={1}" -f $fail, $skip)
exit $fail