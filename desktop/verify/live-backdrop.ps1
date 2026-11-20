param(
  [string]$Exe = "",
  [string]$Look = "glass",
  [string]$Scene = "media",
  [double]$ExpW = 452, [double]$ExpH = 206,
  [double]$IslW = 452, [double]$IslH = 206,
  # Capture ALLOWED (the menu default) -- the island stays visible to
  # screenshots, and the glass must STILL follow the desktop instead of
  # falling back to the startup snapshot.
  [switch]$AllowCapture
)

# Keep this file ASCII-only: Windows PowerShell reads .ps1 using the ANSI
# codepage, so non-ASCII bytes here break the parser.

# The decisive test for live background mapping.
#
# Timeline:
#   t=0      colour card is RED, island starts -> its startup snapshot therefore
#            contains a RED backdrop
#   t=900    card becomes BLUE
#   t=1500   island freezes its last live frame and drops capture exclusion
#   t=1600   screenshot: the glass on screen is either
#              blue  -> it came from the live buffer   (real-time mapping)
#              red   -> it came from the startup snapshot (the old limitation)
#
# Dropping exclusion is what makes the island visible to the screenshot at all;
# live sampling is stopped at the same moment so the frozen frame cannot contain
# the island itself.
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
if (-not ("Snap" -as [type])) {
  Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition (Get-Content (Join-Path $PSScriptRoot "snap-helper.cs") -Raw -Encoding UTF8)
}
[void][Snap]::SetProcessDPIAware()
Add-Type -Namespace W -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
'@

$ScrW = 2560; $ScrH = 1600; $dpi = 1.5; $stride = $ScrW * 4
$fail = 0
function Check($cond, $ok, $bad) {
  if ($cond) { Write-Output "   ok   $ok" } else { Write-Output "   FAIL $bad"; $script:fail++ }
}
function Fmt($c) { return ("{0},{1},{2}" -f [int]$c[0], [int]$c[1], [int]$c[2]) }

if ($Exe -eq "") { $Exe = Join-Path (Split-Path $PSScriptRoot) "WinIsland.exe" }
Get-Process -Name WinIsland -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }

$card = New-Object System.Windows.Forms.Form
$card.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$card.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$card.Bounds = New-Object System.Drawing.Rectangle(580, 0, 1400, 620)
$card.BackColor = [System.Drawing.Color]::FromArgb(255, 0, 0)   # RED at startup
$card.TopMost = $true
$card.ShowInTaskbar = $false
$card.Show()
[System.Windows.Forms.Application]::DoEvents()
Start-Sleep -Milliseconds 300

$log = Join-Path $env:TEMP "winisland-freeze.log"
Remove-Item $log -ErrorAction SilentlyContinue

$argsList = @("--look=$Look","--scene=$Scene","--state=expanded","--freeze-shot=1500","--log=$log")
if (-not $AllowCapture) { $argsList += "--exclude-capture" }
Write-Output ("   mode: capture {0}" -f $(if ($AllowCapture) { "ALLOWED (island visible to screenshots)" } else { "EXCLUDED (island hidden from screenshots)" }))

$p = Start-Process -FilePath $Exe -ArgumentList $argsList -PassThru

# wait for the window at its final size AND a stable position.
#
# Waiting for the size alone is a race: the window is created at its final size
# but only moved into place a moment later, so a single matching sample can lock
# onto the creation position. That silently misplaces the colour card and every
# measurement after it is meaningless. Requiring two consecutive identical rects
# makes the probe wait for the placement to settle.
$wantW = [int][math]::Round(($ExpW + 48) * $dpi)
$wantH = [int][math]::Round(($ExpH + 16 + 34) * $dpi)
$rect = $null
$prevSized = $null
$sw2 = [Diagnostics.Stopwatch]::StartNew()
while ($sw2.ElapsedMilliseconds -lt 5000) {
  $cur = [Snap]::WindowRectOf($p.Id)
  $sized = ($cur -ne $null) -and
           ([math]::Abs($cur[2]-$wantW) -le 2) -and ([math]::Abs($cur[3]-$wantH) -le 2)
  if ($sized -and $prevSized -and $prevSized[0] -eq $cur[0] -and $prevSized[1] -eq $cur[1]) {
    $rect = $cur; break
  }
  if ($sized) { $prevSized = $cur } else { $prevSized = $null }
  Start-Sleep -Milliseconds 10
}
if (-not $rect) { Write-Output "FAIL window never settled"; $card.Close(); $p.Kill(); exit 1 }

# Place the card from the island's ACTUAL rect. A fixed card position is not good
# enough: the island centres itself inside SystemParameters.WorkArea, and that work
# area changes whenever another topmost window makes Windows hide the taskbar, so
# the island can land well away from where a hardcoded card would be.
$card.Bounds = New-Object System.Drawing.Rectangle(
  ($rect[0] - 100), ($rect[1] - 60), ($rect[2] + 200), ($rect[3] + 160))
$card.Refresh()
[System.Windows.Forms.Application]::DoEvents()

$hw = [Snap]::MainHwnd($p.Id)
[void][W.U]::SetWindowPos($hw, [IntPtr](-1), 0, 0, 0, 0, 0x0013)
Write-Output ("   island at {0},{1} {2}x{3}   card at {4},{5} {6}x{7}" -f $rect[0], $rect[1], $rect[2], $rect[3], $card.Bounds.X, $card.Bounds.Y, $card.Bounds.Width, $card.Bounds.Height)

# card -> blue, well before the freeze
$wait = 900 - $sw2.ElapsedMilliseconds
if ($wait -gt 0) { Start-Sleep -Milliseconds $wait }
$card.BackColor = [System.Drawing.Color]::FromArgb(0, 0, 255)
$card.Refresh()
[System.Windows.Forms.Application]::DoEvents()
Write-Output "   t=900ms  card -> blue"

# wait for the app to say it froze, then screenshot. Driving this off the app's
# own trace instead of a wall-clock guess: the freeze is scheduled relative to
# Loaded, not to process start, and guessing early screenshots an island that is
# still excluded - which silently measures the colour card again.
$frozen = $false
$sw3 = [Diagnostics.Stopwatch]::StartNew()
while ($sw3.ElapsedMilliseconds -lt 8000) {
  if ((Test-Path $log) -and ((Get-Content $log -Raw -Encoding UTF8) -match 'FREEZE')) { $frozen = $true; break }
  Start-Sleep -Milliseconds 20
}
if (-not $frozen) { Write-Output "FAIL app never reached the freeze point"; $p.Kill(); $card.Close(); exit 1 }
Start-Sleep -Milliseconds 150

# The card was placed from the rect read earlier. If the island moved after that,
# the card is no longer behind it and every pixel below measures the wrong thing.
$now = [Snap]::WindowRectOf($p.Id)
if ($now -ne $null -and ($now[0] -ne $rect[0] -or $now[1] -ne $rect[1])) {
  Write-Output ("   FAIL island moved after placement: {0},{1} -> {2},{3}" -f $rect[0], $rect[1], $now[0], $now[1])
  $script:fail++
}

$buf = [Snap]::Grab($ScrW, $ScrH)
Write-Output ("   froze at {0}ms, screenshot taken" -f $sw3.ElapsedMilliseconds)

$ix = $rect[0] + [int][math]::Round(24 * $dpi)
$iy = $rect[1] + [int][math]::Round(16 * $dpi)
$iw = [int][math]::Round($IslW * $dpi)
$gx = $ix + [int]($iw / 2) - 30
$gy = $iy + 8
$glass = [Snap]::RegionRGB($buf, $stride, $gx, $gy, 60, 12)
$margin = [Snap]::RegionRGB($buf, $stride, $rect[0] + 4, $rect[1] + [int]($rect[3] / 2), 8, 8)
Write-Output ("   glass on screen = {0}   (margin {1})" -f (Fmt $glass), (Fmt $margin))

$p.WaitForExit(8000) | Out-Null
if (-not $p.HasExited) { $p.Kill() }
$card.Close(); $card.Dispose()
[System.Windows.Forms.Application]::DoEvents()

$sampled = ""
if (Test-Path $log) {
  Get-Content $log -Encoding UTF8 | ForEach-Object {
    if ($_ -match 'FREEZE sampled=(\S+)') { $sampled = $matches[1] }
  }
}
Write-Output ("   last live sample (inside the app) = {0}" -f $sampled)
Write-Output ""

Check ($sampled -match '^0,0,25') "the live buffer held the BLUE card at freeze time ($sampled)" "live buffer did not hold blue (got '$sampled')"

# Guard: an excluded island is invisible, and the probe would then read the card
# verbatim. The composited glass can never match the raw card colour exactly.
$visible = ([math]::Abs($glass[0] - $margin[0]) -gt 8) -or ([math]::Abs($glass[1] - $margin[1]) -gt 8) -or ([math]::Abs($glass[2] - $margin[2]) -gt 8)
Check ($visible) "the island is actually visible in this screenshot (glass $((Fmt $glass)) vs card $((Fmt $margin)))" "the island is still invisible - probe read the card itself ($((Fmt $glass))), so nothing was measured"

Check ($glass[2] -gt $glass[0] + 8) "rendered glass is BLUE - it came from the live buffer, not the red startup snapshot (B $([int]$glass[2]) vs R $([int]$glass[0]))" "rendered glass is not blue - it is still showing the red startup snapshot (R $([int]$glass[0]) vs B $([int]$glass[2]))"

Write-Output ""
Write-Output ("RESULT fail={0}" -f $fail)
exit $fail