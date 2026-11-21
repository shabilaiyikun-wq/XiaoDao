# probe-todo-unpin.ps1 -- regression probe for "island never collapses after a todo reminder"
#
# Symptom: when a todo item comes due, the island expands to the todo card and then
#          stays in the expanded layer forever -- hovering out never collapses it.
#
# Root cause under test: TickTodoDue() sets _pinnedState = true, and nothing in the
#          project ever clears it, so Reconcile() returns at its first guard forever.
#
# Phase A: with a due todo present, do NOT touch the cursor at all.
#          Expect: an "auto expanded -> ..." line appears on its own (reminder hold expires).
# Phase B: hover the island, then move away.
#          Expect: it expands, then an "auto expanded -> ..." line appears again,
#                  and the drawn island size (the "island=WxH" field) is small again.
#
# ASCII only on purpose: this file is read as ANSI/GBK on this host.

$ErrorActionPreference = 'Stop'

$exe    = 'E:\xiaodao\desktop\WinIsland.exe'
$log    = Join-Path $env:TEMP 'probe-todo-unpin.log'
$todos  = Join-Path $env:APPDATA 'WinIsland\todos.txt'
$backup = Join-Path $env:TEMP 'probe-todo-unpin.todos.bak'

Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public struct RECT { public int L, T, R, B; }
public class Win {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

    // The island window is the visible top-level window of the process that is NOT the pet
    // (the pet is a 220x220 square; the island is wide and short).
    public static int FindIsland(int pid, out int x, out int y, out int w, out int h) {
        int found = 0; int bx = 0, by = 0, bw = 0, bh = 0;
        EnumWindows(delegate(IntPtr win, IntPtr p) {
            uint q; GetWindowThreadProcessId(win, out q);
            if ((int)q != pid || !IsWindowVisible(win)) return true;
            RECT r; if (!GetWindowRect(win, out r)) return true;
            int ww = r.R - r.L, hh = r.B - r.T;
            if (ww > hh * 1.5) { bx = r.L; by = r.T; bw = ww; bh = hh; found = 1; }
            return true;
        }, IntPtr.Zero);
        x = bx; y = by; w = bw; h = bh;
        return found;
    }
}
"@

function Count-Match($path, $pattern) {
    if (-not (Test-Path $path)) { return 0 }
    return @(Select-String -Path $path -Pattern $pattern -AllMatches).Count
}

function Wait-Log($path, $pattern, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if (Count-Match $path $pattern) { return $true }
        Start-Sleep -Milliseconds 150
    }
    return (Count-Match $path $pattern) -gt 0
}

# wait until MORE lines match than before -- needed when the pattern already matched earlier
function Wait-More($path, $pattern, $baseline, $seconds) {
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Count-Match $path $pattern) -gt $baseline) { return $true }
        Start-Sleep -Milliseconds 100
    }
    return ((Count-Match $path $pattern) -gt $baseline)
}

$fail = 0
function Check($ok, $name, $detail) {
    if ($ok) { Write-Host ("PASS " + $name) }
    else     { Write-Host ("FAIL " + $name + "  " + $detail); $script:fail++ }
}

# ---------------------------------------------------------------- setup
Copy-Item $todos $backup -Force
Get-Process WinIsland -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Milliseconds 900
Remove-Item $log -Force -ErrorAction SilentlyContinue

# a due item, deliberately FIRST in the file so TakeDue() picks it deterministically
$text  = 'probe due item'
$b64   = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($text))
$ticks = ([DateTime]::Now.AddMinutes(-1)).Ticks
$old   = @(Get-Content $todos)
Set-Content -Path $todos -Value (@(("0|" + $ticks + "|" + $b64)) + $old) -Encoding ASCII

Start-Process -FilePath $exe -ArgumentList ("--log=" + $log)
$fired = Wait-Log $log 'todo due:' 12
Check $fired 'todo reminder fired on startup' 'no "todo due:" line in the log'

# ---------------------------------------------------------------- phase A: no cursor at all
Write-Host '--- phase A: wait for the reminder hold to expire (cursor untouched) ---'
$autoA = Wait-Log $log 'auto expanded -> ' 14
Check $autoA 'expanded collapses on its own after the reminder hold' 'no "auto expanded ->" line: still pinned in the third layer'

# ---------------------------------------------------------------- phase B: hover round trip
Write-Host '--- phase B: hover then leave ---'
$pid2 = (Get-Process WinIsland).Id
$x = 0; $y = 0; $w = 0; $h = 0
if (-not [Win]::FindIsland($pid2, [ref]$x, [ref]$y, [ref]$w, [ref]$h)) {
    Write-Host 'FAIL could not locate the island window'
    $fail++
} else {
    Write-Host ("island window (virtual units): " + $x + "," + $y + " " + $w + "x" + $h)

    # park far away first, then sweep a grid inside the window rect until hover fires
    [void][Win]::SetCursorPos(40, 40)
    Start-Sleep -Milliseconds 600
    $before = Count-Match $log 'hover -> True'
    $swept = $false
    for ($yy = $y + 4; $yy -lt ($y + $h - 4) -and -not $swept; $yy += 22) {
        for ($xx = $x + 6; $xx -lt ($x + $w - 6); $xx += 26) {
            [void][Win]::SetCursorPos($xx, $yy)
            Start-Sleep -Milliseconds 60
            if ((Count-Match $log 'hover -> True') -gt $before) { $swept = $true; break }
        }
    }
    Check $swept 'island is hoverable' 'cursor never entered the island body'

    if ($swept) {
        # the hover line that just fired carries the pre-hover drawn size
        $line = (Select-String -Path $log -Pattern 'hover -> True' | Select-Object -Last 1).Line
        if ($line -match 'island=([0-9.]+)x([0-9.]+)') {
            $iw = [double]$Matches[1]
            # idle is 52 wide, compact is ~248, expanded is 452/468 -- 300 separates them
            Check ($iw -lt 300) 'remaining in the third layer before hover' ('drawn width was ' + $iw + ' -- already expanded')
        } else {
            Check $false 'island= field present in the hover line' $line
        }

        $autoB = Count-Match $log 'auto expanded -> '
        [void][Win]::SetCursorPos(40, 40)
        $collapsed = Wait-More $log 'auto expanded -> ' $autoB 8
        Check $collapsed 'hover out collapses the expanded layer' 'no new "auto expanded ->" line after leaving'
    }
}

# ---------------------------------------------------------------- teardown
Get-Process WinIsland -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id $_.Id -Force }
Start-Sleep -Milliseconds 600
Copy-Item $backup (Join-Path $env:APPDATA 'WinIsland\todos.txt') -Force
[void][Win]::SetCursorPos(1200, 700)
Start-Process -FilePath $exe

Write-Host ''
Write-Host ('=== auto lines seen ===')
if (Test-Path $log) { Select-String -Path $log -Pattern 'todo due:|auto |hover ->' | ForEach-Object { $_.Line } }

if ($fail -eq 0) { Write-Host 'PROBE PASS'; exit 0 }
Write-Host ('PROBE FAIL (' + $fail + ' checks)'); exit 1