param(
  [string]$Exe = "E:\xiaodao\desktop\WinIsland.exe",
  [int]$WaitMs = 3000
)

# Assert the notification ball slides out from BEHIND the island.
#
# The design: the ball sits under the island in z-order (_root.Insert before _host),
# so while its offset is negative it is hidden by the island body itself -- no mask,
# no clipping, no shape deformation. Then it translates to its resting spot, and
# only after it is fully out does it turn WeChat-green / QQ-blue.
#
# Checks:
#   1. it starts fully hidden  : dx <= -(NotifyGap + NotifyBall) = -44
#   2. it slides monotonically : dx never goes backwards
#   3. it ends exactly at rest : dx == 0
#   4. no deformation          : sx == sy == 1 throughout (the rejected versions
#                                squashed it into an ellipse / stretched a neck)
#   5. colour comes last       : reveal == 0 while dx < 0, then reveal == 1
#
# Reads the app's own per-frame trace, so it is deterministic.
# ASCII-only.

$log = Join-Path $env:TEMP "winisland-slidecheck.log"
Remove-Item $log -ErrorAction SilentlyContinue

Start-Process -FilePath $Exe -ArgumentList @(
  "--notify-test", "--notify-trace", "--state=expanded", "--log=$log"
) | Out-Null

Start-Sleep -Milliseconds $WaitMs
Get-Process -Name WinIsland -ErrorAction SilentlyContinue | ForEach-Object { $_.Kill() }

$lines = Get-Content $log -Encoding UTF8 | Where-Object { $_ -match 'BALLTRACE' }
if (-not $lines -or $lines.Count -lt 5) {
  Write-Output "FAIL expected a BALLTRACE run, got $($lines.Count) samples"
  exit 1
}

$fail = 0
function Check($cond, $ok, $bad) {
  if ($cond) { Write-Output "   ok   $ok" } else { Write-Output "   FAIL $bad"; $script:fail++ }
}

$t = @(); $dx = @(); $sx = @(); $sy = @(); $reveal = @(); $below = @(); $rawLeft = @(); $clipL = @(); $sc = @(); $bw = @(); $coreIn = @()
foreach ($l in $lines) {
  if ($l -match 't=([\d.]+)')      { $t      += [double]$Matches[1] }
  if ($l -match 'dx=(-?[\d.]+)')   { $dx     += [double]$Matches[1] }
  if ($l -match 'sx=([\d.]+)')     { $sx     += [double]$Matches[1] }
  if ($l -match 'sy=([\d.]+)')     { $sy     += [double]$Matches[1] }
  if ($l -match 'reveal=([\d.]+)') { $reveal += [double]$Matches[1] }
  if ($l -match 'below=(\d)')      { $below  += [int]$Matches[1] }
  if ($l -match 'rawLeft=(-?[\d.]+)') { $rawLeft += [double]$Matches[1] }
  if ($l -match 'clipL=(-?[\d.]+)') { $clipL += [double]$Matches[1] }
  if ($l -match ' sc=([\d.]+)') { $sc += [double]$Matches[1] }
  if ($l -match ' bw=([\d.]+)') { $bw += [double]$Matches[1] }
  if ($l -match ' coreIn=([\d.]+)') { $coreIn += [double]$Matches[1] }
}

Write-Output ("samples: {0}   duration: {1}s" -f $t.Count, [Math]::Round($t[-1], 2))
Write-Output ""

# --- 1. starts fully behind the island
$firstDx = $dx[0]
Check ($firstDx -le -43.0) ("starts fully hidden behind the island (dx {0})" -f $firstDx) ("does not start hidden (dx {0}, need -44)" -f $firstDx)

# --- 2. monotonic slide out
$back = 0
for ($i = 1; $i -lt $dx.Count; $i++) {
  if ($dx[$i] -lt $dx[$i-1] - 0.01) { $back++ }
}
Check ($back -eq 0) "slides out monotonically ($back reversals)" "$back backwards jumps in the slide"

# --- 3. ends at rest
Check ([Math]::Abs($dx[-1]) -lt 0.05) ("ends exactly at rest (dx {0})" -f $dx[-1]) ("does not reach rest (dx {0})" -f $dx[-1])

# --- 4. it grows from small to full size, and stays ROUND while growing
Check ($sc[0] -lt 0.45) ("starts small (scale {0})" -f $sc[0]) ("does not start small (scale {0})" -f $sc[0])
Check ([Math]::Abs($sc[-1] - 1.0) -lt 0.02) ("ends at full size (scale {0})" -f $sc[-1]) ("does not reach full size (scale {0})" -f $sc[-1])

$shrink = 0
for ($i = 1; $i -lt $sc.Count; $i++) { if ($sc[$i] -lt $sc[$i-1] - 0.02) { $shrink++ } }
Check ($shrink -le 1) "grows monotonically (allow one settle-back, saw $shrink)" "scale jumps around ($shrink reversals)"

# an ellipse would show sx != sy; growth alone must not turn it into a lens
$asym = 0.0
for ($i = 0; $i -lt $sx.Count; $i++) {
  $d = [Math]::Abs($sx[$i] - $sy[$i])
  if ($d -gt $asym) { $asym = $d }
}
Check ($asym -lt 0.02) ("stays circular while growing (max sx-sy {0})" -f [Math]::Round($asym, 3)) ("grows into an ellipse (sx-sy {0})" -f [Math]::Round($asym, 3))

# --- 5. colour only after it is out
$leak = 0
for ($i = 0; $i -lt $reveal.Count; $i++) {
  if ($dx[$i] -lt -0.05 -and $reveal[$i] -gt 0.01) { $leak++ }
}
Check ($leak -eq 0) "app colour stays hidden while still behind the island ($leak leaks)" "app colour leaked while still behind the island ($leak samples)"

$firstReveal = -1
for ($i = 0; $i -lt $reveal.Count; $i++) { if ($reveal[$i] -gt 0.01) { $firstReveal = $i; break } }
$lastMoving = -1
for ($i = 0; $i -lt $dx.Count; $i++) { if ($dx[$i] -lt -0.05) { $lastMoving = $i } }
Check ($firstReveal -gt $lastMoving) ("reveal starts after the slide finishes (still moving until {0}, reveal from {1})" -f $lastMoving, $firstReveal) ("reveal starts before the slide ends (moving until {0}, reveal from {1})" -f $lastMoving, $firstReveal)
Check ($reveal[-1] -gt 0.99) ("fully revealed at the end (reveal {0})" -f $reveal[-1]) ("never fully revealed (reveal {0})" -f $reveal[-1])

Write-Output ""
Write-Output "trace:"
$step = [Math]::Max(1, [int]($t.Count / 12))
for ($i = 0; $i -lt $t.Count; $i += $step) {
  Write-Output ("   t={0,-5} dx={1,-7} rawLeft={2,-7} sc={3,-5} reveal={4}" -f $t[$i], $dx[$i], $rawLeft[$i], $sc[$i], $reveal[$i])
}

# --- 6. the ball never paints onto the island
# The island is translucent glass, so z-order alone does not hide the ball -- it is
# visible THROUGH the island, and its white rim sliding across the island face reads
# as "the ball is on top of the island". A fixed clip window whose left edge sits on
# the island's right edge is what actually guarantees it.
# rawLeft going negative proves the ball really does travel past the island's right
# edge (i.e. a clip is genuinely needed). clipL is the clip window's own left edge
# measured against that same edge: it must be exactly 0.
$passed = ($rawLeft | Where-Object { $_ -lt -1 }).Count
Check ($passed -gt 0) "ball does travel behind the island ($passed samples past its right edge)" "ball never actually goes behind the island, nothing to hide"

$badClip = 0
for ($i = 0; $i -lt $clipL.Count; $i++) { if ([Math]::Abs($clipL[$i]) -gt 0.01) { $badClip++ } }
Check ($badClip -eq 0) "clip window edge sits exactly on the island's right edge" ("clip window is not aligned to the island edge in {0} samples" -f $badClip)

$notBelow = ($below | Where-Object { $_ -ne 1 }).Count
Check ($notBelow -eq 0) "ball stays in z-order below the island" "ball is not below the island in $notBelow samples"

# --- no border at all -------------------------------------------------------
# "???????" -- the ball must have zero border thickness. A non-zero value
# means a white rim is being stroked around the drop again.
$border = ($bw | Where-Object { $_ -gt 0.001 }).Count
Check ($border -eq 0) "ball has no border at all" "ball still strokes a border in $border samples"

$inset = ($coreIn | Where-Object { $_ -lt 3.0 }).Count
Check ($inset -eq 0) ("solid core is inset inside the material ring ({0}dip margin)" -f $coreIn[-1]) ("colour sits on the ball edge (margin {0}dip)" -f $coreIn[-1])

Write-Output ""
Write-Output ("RESULT fail={0}" -f $fail)
exit $fail