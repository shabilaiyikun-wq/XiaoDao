param(
  [string]$Exe = "E:\xiaodao\desktop\WinIsland.exe",
  [string]$Look = "frost",
  [string]$Scene = "media",
  [double]$ExpW = 452, [double]$ExpH = 206,
  [double]$IslW = 452, [double]$IslH = 206,
  [int]$DragX = -170, [int]$DragY = 130
)

# ASCII-only on purpose: this shell reads .ps1 files with the local ANSI codepage,
# so any non-ASCII literal breaks parsing once the BOM is lost.
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

# Window geometry the app is expected to announce for this scene.
$wantW = [int][math]::Round(($ExpW + 48) * $dpi)
$wantH = [int][math]::Round(($ExpH + 16 + 34) * $dpi)
# NOTE: PowerShell variable names are case-insensitive. Never name these $islW /
# $islH -- that would overwrite the parameters with physical pixels and break the
# subtraction inside IslandCentre.
$physIslW = [int][math]::Round($IslW * $dpi)
$physIslH = [int][math]::Round($IslH * $dpi)

function Start-Island($extra) {
  $a = @("--look=$Look", "--scene=$Scene") + $extra
  return Start-Process -FilePath $Exe -ArgumentList $a -PassThru
}

function IslandCentre($procId) {
  $rect = [Snap]::WindowRectOf($procId)
  if (-not $rect) { return $null }
  $ix = $rect[0] + [int][math]::Round(($ExpW + 48 - $IslW) / 2 * $dpi)
  $iy = $rect[1] + [int][math]::Round(16 * $dpi)
  return @{ X = $ix + [int][math]::Round($IslW * $dpi / 2); Y = $iy + [int][math]::Round($IslH * $dpi / 2) }
}

function PositionFile() { return "$env:APPDATA\WinIsland\window.txt" }

Write-Output "=== interaction acceptance / $Scene ${ExpW}x${ExpH} -> window ${wantW}x${wantH} ==="

# ---------------------------------------------------------------------------
# 1. Drag: the island must follow the cursor, and the window must not be
#    resized by the gesture. Drag from the centre of the *left edge band*,
#    which is background in every scene, so the gesture is never swallowed by
#    a clickable row (rows are Border+Tag=act and deliberately do not drag).
# ---------------------------------------------------------------------------
$pf = PositionFile
Remove-Item $pf -ErrorAction SilentlyContinue

$dragLog = "E:\xiaodao\work\drag-trace.log"
Remove-Item $dragLog -ErrorAction SilentlyContinue
$s1 = Start-Island @("--state=expanded", "--log=$dragLog")
Start-Sleep -Milliseconds 1900

$r0 = [Snap]::WindowRectOf($s1.Id)
if (-not $r0) { Write-Output "   FAIL window not found"; if (-not $s1.HasExited) { $s1.Kill() }; exit 1 }

Check ([math]::Abs($r0[2] - $wantW) -le 6 -and [math]::Abs($r0[3] - $wantH) -le 6) `
  "window is ${wantW}x${wantH} for the ${ExpW}x${ExpH} island" `
  "window size off ($($r0[2])x$($r0[3]), expected ${wantW}x${wantH})"

# A point 10 DIP inside the island's left edge, vertical centre: always empty.
$grabX = $r0[0] + [int][math]::Round((($ExpW + 48 - $IslW) / 2 + 10) * $dpi)
$grabY = $r0[1] + [int][math]::Round((16 + $IslH / 2) * $dpi)

[Snap]::MoveCursor($grabX, $grabY)
Start-Sleep -Milliseconds 260
# DragTo only moves the cursor; the button state is the caller's job.
[Snap]::LeftDown()
Start-Sleep -Milliseconds 60
[Snap]::DragTo($grabX, $grabY, $grabX + $DragX, $grabY + $DragY, 420)
Start-Sleep -Milliseconds 80
[Snap]::LeftUp()
Start-Sleep -Milliseconds 420

$r1 = [Snap]::WindowRectOf($s1.Id)
$dx = $r1[0] - $r0[0]
$dy = $r1[1] - $r0[1]
Write-Output "   after drag rect=$($r1[0]),$($r1[1])  delta=$dx,$dy (expect $DragX,$DragY)"

Check ([math]::Abs($dx - $DragX) -le 3 -and [math]::Abs($dy - $DragY) -le 3) `
  "the window followed the cursor ($dx,$dy vs expected $DragX,$DragY)" `
  "window moved $dx,$dy but the cursor moved $DragX,$DragY"

Check ([math]::Abs($dx) -ge 20 -or [math]::Abs($dy) -ge 20) `
  "the drag gesture actually travelled the intended $DragX,$DragY" `
  "the drag did not move the window at all -- the hit test swallowed the gesture"

Check ($r1[2] -eq $r0[2] -and $r1[3] -eq $r0[3]) `
  "window size unchanged by the drag ($($r1[2])x$($r1[3]))" `
  "the drag resized the window ($($r0[2])x$($r0[3]) -> $($r1[2])x$($r1[3]))"

# 2. The drop position must be persisted, and honoured on the next start.
Check (Test-Path $pf) "drop position was written to window.txt" "no position file after drag"

$saved = ""
if (Test-Path $pf) { $saved = (Get-Content $pf -Raw).Trim() }
# window.txt 存的是锚点（岛心 X、岛顶 Y）加版本标记 v2，不是窗口左上角。
# 从实测窗口矩形换算过来对一下，格式回归会直接看出来。
$expAx = [int][math]::Round($r1[0]/$dpi + ($r1[2]/$dpi)/2)
$expAt = [int][math]::Round($r1[1]/$dpi + 16)
Write-Output "   window.txt = '$saved' (expect '$expAx,$expAt,v2' = island centre x / island top)"

if (-not $s1.HasExited) { $s1.Kill() }
Start-Sleep -Milliseconds 400

$s2 = Start-Island @("--state=expanded")
Start-Sleep -Milliseconds 1900
$r2 = [Snap]::WindowRectOf($s2.Id)
$restored = ([math]::Abs($r2[0] - $r1[0]) -le 4 -and [math]::Abs($r2[1] - $r1[1]) -le 4)
Check ($restored) `
  "restart restores the dragged position ($($r2[0]),$($r2[1]))" `
  "position not restored after restart ($($r2[0]),$($r2[1]) vs $($r1[0]),$($r1[1]))"

# ---------------------------------------------------------------------------
# 3. Right-click menu. The app is the only trustworthy observer here: the main
#    window is WDA_EXCLUDEFROMCAPTURE, so the island is invisible in screenshots
#    and the desktop behind it keeps changing.
# ---------------------------------------------------------------------------
$menuLog = "E:\xiaodao\work\menu-trace.log"
Remove-Item $menuLog -ErrorAction SilentlyContinue
if (-not $s2.HasExited) { $s2.Kill() }
Start-Sleep -Milliseconds 400

$s3 = Start-Island @("--state=expanded", "--heartbeat", "--log=$menuLog")
Start-Sleep -Milliseconds 2000
$r3 = [Snap]::WindowRectOf($s3.Id)
$c3 = IslandCentre $s3.Id

[Snap]::MoveCursor($c3.X, $c3.Y)
Start-Sleep -Milliseconds 240
[Snap]::RightDown()
Start-Sleep -Milliseconds 60
[Snap]::RightUp()
Start-Sleep -Milliseconds 700

$menuText = ""
if (Test-Path $menuLog) { $menuText = [IO.File]::ReadAllText($menuLog) }
$opened = ($menuText -match 'menu opened')

# Collect the island size the app reports while the menu is open.
# Heartbeat order is: "heartbeat island=WxH win=L,T menu=True|False ...", so
# island= comes BEFORE menu= -- match them separately, never as one pattern.
$hbAll = @()
if (Test-Path $menuLog) {
  $hbAll = Get-Content $menuLog -Encoding UTF8 | Where-Object { $_ -match 'heartbeat' }
}
$hbMenuOpen = @($hbAll | Where-Object { $_ -match 'menu=True' })
$hbSizes = @()
foreach ($m in $hbMenuOpen) {
  $mm = [regex]::Match($m, 'island=(\d+(?:\.\d+)?)x(\d+(?:\.\d+)?)')
  if ($mm.Success) {
    $hbSizes += "$([int][double]$mm.Groups[1].Value)x$([int][double]$mm.Groups[2].Value)"
  }
}
Write-Output ("   heartbeats with menu open: {0} -> sizes {1}" -f $hbMenuOpen.Count, ($hbSizes -join ' '))

Check ($opened) "right-click opens the menu (app logged it)" "right-click produced no menu"
Check ($hbMenuOpen.Count -ge 1) `
  "the app reports the menu open in its heartbeat ($($hbMenuOpen.Count) ticks)" `
  "no heartbeat reported the menu open"

$wantSize = "$([int]$ExpW)x$([int]$ExpH)"
Check ($hbSizes.Count -gt 0 -and ($hbSizes | Where-Object { $_ -eq $wantSize }).Count -eq $hbSizes.Count) `
  "the island stayed expanded ($wantSize) while the menu was open" `
  "the island changed size while the menu was open: $($hbSizes -join ' ')"

$r4 = [Snap]::WindowRectOf($s3.Id)
Check ([math]::Abs($r4[0] - $r3[0]) -le 2 -and [math]::Abs($r4[1] - $r3[1]) -le 2) `
  "opening the menu does not move the window (stayed at $($r4[0]),$($r4[1]))" `
  "the menu moved the window ($($r3[0]),$($r3[1]) -> $($r4[0]),$($r4[1]))"

# 4. Escape closes it again.
[Snap]::KeyTap(0x1B)
Start-Sleep -Milliseconds 700
$menuText2 = ""
if (Test-Path $menuLog) { $menuText2 = [IO.File]::ReadAllText($menuLog) }
Check ($menuText2 -match 'menu closed') "Escape closes the menu" "Escape did not close the menu"

$hbClosed = @()
if (Test-Path $menuLog) {
  $hbClosed = Get-Content $menuLog -Encoding UTF8 | Where-Object { $_ -match 'heartbeat.*menu=False' }
}
Check ($hbClosed.Count -ge 1) `
  "the heartbeat reports the menu closed after Escape ($($hbClosed.Count) ticks)" `
  "no heartbeat reported the menu closed after Escape"

if (-not $s3.HasExited) { $s3.Kill() }
Start-Sleep -Milliseconds 200

Write-Output ""
Write-Output ("RESULT fail={0}" -f $fail)
exit $fail