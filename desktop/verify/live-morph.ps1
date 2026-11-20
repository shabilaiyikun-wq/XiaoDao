param(
  [string]$Exe = "E:\xiaodao\desktop\WinIsland.exe",
  [string]$Look = "frost",
  [string]$Scene = "media",
  [double]$ExpW = 416, [double]$ExpH = 156,
  [double]$IslW = 416, [double]$IslH = 156,
  [double]$CompW = 248, [double]$CompH = 38
)

# Proves the intro morph actually runs on the shipped program's normal path
# (not the --selftest path): start it and sample the rendered island width over
# time. Compact -> something in between -> settled expanded should all show up.
#
# Beware: PowerShell variable names are case-insensitive, so never name a result
# variable $w while a screen size lives in $W - the assignment silently rewrites
# the screen size and every later capture gets a nonsense rectangle.
Add-Type -AssemblyName System.Drawing
if (-not ("Snap" -as [type])) {
  Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition (Get-Content (Join-Path $PSScriptRoot "snap-helper.cs") -Raw -Encoding UTF8)
}
[void][Snap]::SetProcessDPIAware()

$ScrW = 2560; $ScrH = 1600; $dpi = 1.5
$fail = 0
function Check($cond, $ok, $bad) {
  if ($cond) { Write-Output "   ok   $ok" } else { Write-Output "   FAIL $bad"; $script:fail++ }
}

$sw = [Diagnostics.Stopwatch]::StartNew()
$proc = Start-Process -FilePath $Exe -ArgumentList @("--look=$Look","--scene=$Scene","--no-wda") -PassThru

# Wait for the window to reach its final size, not merely to exist. WPF reports a
# window as soon as it is created, before the app has set Width/Height and moved
# it into place, so an early GetWindowRect returns a plausible-looking but wrong
# rectangle - and every probe derived from it then samples the wallpaper.
$wantW = [int][math]::Round(($ExpW + 48) * $dpi)
$wantH = [int][math]::Round(($ExpH + 16 + 34) * $dpi)
$rect = $null
$tShown = -1
while ($sw.ElapsedMilliseconds -lt 6000) {
  $rect = [Snap]::WindowRectOf($proc.Id)
  if ($rect -and [math]::Abs($rect[2] - $wantW) -le 2 -and [math]::Abs($rect[3] - $wantH) -le 2) {
    $tShown = $sw.ElapsedMilliseconds
    break
  }
  Start-Sleep -Milliseconds 10
}
if ($tShown -lt 0) {
  Write-Output "FAIL window never reached ${wantW}x${wantH} (last rect: $(if($rect){$rect -join ','}else{'<none>'}))"
  if ($proc -and -not $proc.HasExited) { $proc.Kill() }
  exit 1
}

$leftExp  = $rect[0] + [int][math]::Round(($ExpW + 48 - $IslW) / 2 * $dpi)
$topIsl   = $rect[1] + [int][math]::Round(16 * $dpi)
$widthExp = [int][math]::Round($IslW * $dpi)
$widthComp = [int][math]::Round($CompW * $dpi)
$centreX  = $leftExp + [int]($widthExp / 2)

# Both the compact capsule and the expanded card are horizontally centred in the
# window, so the middle of the island box is inside the island in every form.
# Sampling the fill there (rather than at the expanded left edge) keeps the
# reference valid while the shape is still compact.
$fillX = $centreX - 20
$fillY = $topIsl + 8
$midY   = $topIsl + [int]([math]::Round($CompH * $dpi) / 2)

function Get-IslandWidth {
  $buf = [Snap]::Grab($ScrW, $ScrH)
  $fillL = [Snap]::ModeLuma($buf, $ScrW*4, $fillX, $fillY, 40, 16)
  $deskL = [Snap]::ModeLuma($buf, $ScrW*4, $rect[0]+3, $rect[1]+3, 12, 12)
  if ([math]::Abs($fillL - $deskL) -lt 40) { return -1 }
  $mid = ($fillL + $deskL) / 2
  if ($fillL -gt $deskL) { $lo = $mid; $hi = 255.0 } else { $lo = 0.0; $hi = $mid }
  $x0 = [math]::Max(0, $leftExp - 60)
  $x1 = [math]::Min($ScrW - 1, $leftExp + $widthExp + 60)
  $span = [Snap]::FillSpan($buf, $ScrW*4, $midY, $x0, $x1, $lo, $hi)
  return $span[2]
}

Write-Output "look=$Look  window appeared at ${tShown}ms"
Write-Output ("   expect compact {0}px -> expanded {1}px" -f $widthComp, $widthExp)

$plan = @(0, 120, 300, 500, 700, 900, 1100, 1400, 1800)
$samples = @()
foreach ($target in $plan) {
  $waitMs = $tShown + $target - $sw.ElapsedMilliseconds
  if ($waitMs -gt 0) { Start-Sleep -Milliseconds $waitMs }
  $measured = Get-IslandWidth
  $samples += [pscustomobject]@{ T = [int]($sw.ElapsedMilliseconds - $tShown); Width = [int]$measured }
}

foreach ($s in $samples) {
  $bar = if ($s.Width -lt 0) { "(not decidable)" } else { "$($s.Width)px" }
  Write-Output ("   t+{0,4}ms  {1}" -f $s.T, $bar)
}

$widths = @($samples | Where-Object { $_.Width -gt 0 } | ForEach-Object { $_.Width })
$first = if ($widths.Count -gt 0) { $widths[0] } else { -1 }
$last  = if ($widths.Count -gt 0) { $widths[$widths.Count-1] } else { -1 }
$between = @($widths | Where-Object { $_ -gt ($widthComp + 12) -and $_ -lt ($widthExp - 12) })

Write-Output ""
Check ($last -gt 0 -and [math]::Abs($last - $widthExp) -le 8) `
  "settled at expanded width $last px" "did not settle at expanded width (last=$last)"
Check ($first -gt 0 -and $first -le ($widthComp + 20)) `
  "started near compact width $first px" "did not start near compact width (first=$first)"
Check ($between.Count -ge 1) `
  "observed $($between.Count) intermediate width(s) mid-morph: $($between -join ', ')" `
  "no intermediate width observed - the morph may be snapping instead of animating"

if (-not $proc.HasExited) { $proc.Kill() }
Write-Output ""
exit $fail