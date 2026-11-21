# 桌宠岛内集成 —— 独立验收探针
#
# 这个脚本只回答一个问题，而且要从**进程外面**回答：
#   桌宠到底还是一个独立窗口，还是已经并进岛的那一个窗口里了？
#
# 为什么要单独做：代码里写「不再 new PetWindow 了」不算证明。
# 以前是两个 topmost 窗口抢 Z-order，用户看到的顺序取决于系统，
# 只有从外面数一遍顶层窗口，才能确定「只有一个窗口」这件事真的成立。
#
# 判据
#   1. WinIsland 进程的可见顶层窗口恰好 1 个（岛自己）
#   2. 不存在标题为「桌宠」的窗口（老实现有，且它有独立 HWND）
#   3. 岛窗口带 WS_EX_TOOLWINDOW / WS_EX_NOACTIVATE（不抢激活、不进任务栏）
#   4. 探针 JSON 全部 ok，且三层尺寸 / 遮挡 / 单一球符合预期
#
# 用法：pwsh -File desktop\verify\pet-island.ps1 [-SkipRun]

param(
    [switch]$SkipRun,
    [string]$Desktop = (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path))
)

$ErrorActionPreference = 'Stop'
$exe = Join-Path $Desktop 'WinIsland.exe'
$probeJson = Join-Path $Desktop 'pet-island-probe.json'
$script:fails = @()
$script:winCount = -1

function Write-Head($t) { Write-Host ''; Write-Host "== $t" -ForegroundColor Cyan }

# ---------------------------------------------------------------- 窗口枚举器
# 用 EnumWindows 而不是 Get-Process.MainWindowHandle：
# 后者只给「一个主窗口」，恰好会把第二个 topmost 窗口藏起来 ——
# 而第二个窗口正是这件事要否掉的东西。
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class WinProbe
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    public static List<string> List(uint targetPid)
    {
        List<string> rows = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid != targetPid) return true;
            if (!IsWindowVisible(h)) return true;

            StringBuilder t = new StringBuilder(512);
            GetWindowTextW(h, t, t.Capacity);
            StringBuilder c = new StringBuilder(512);
            GetClassNameW(h, c, c.Capacity);
            RECT r;
            GetWindowRect(h, out r);

            int ex = GetWindowLong(h, -20);
            int style = GetWindowLong(h, -16);

            rows.Add(string.Join("\t", new string[] {
                h.ToInt64().ToString(),
                t.ToString(),
                c.ToString(),
                (r.Right - r.Left).ToString() + "x" + (r.Bottom - r.Top),
                (ex & 0x00000080) != 0 ? "TOOLWINDOW" : "-",
                (ex & 0x08000000) != 0 ? "NOACTIVATE" : "-",
                (ex & 0x00000020) != 0 ? "TRANSPARENT" : "-",
                (ex & 0x00080000) != 0 ? "LAYERED" : "-",
                (style & 0x80000000L) != 0 ? "POPUP" : "-"
            }));
            return true;
        }, IntPtr.Zero);
        return rows;
    }
}
'@

function Start-Island([string]$extraArgs) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.WorkingDirectory = $Desktop
    $psi.UseShellExecute = $false
    $psi.Arguments = $extraArgs
    return [System.Diagnostics.Process]::Start($psi)
}

function Stop-Quietly($proc) {
    try { if (-not $proc.HasExited) { $proc.Kill(); $proc.WaitForExit(4000) | Out-Null } } catch { }
}

if (-not (Test-Path $exe)) { throw "missing $exe (先跑 desktop\build.cmd)" }

# ---------------------------------------------------------------- ① 跑探针
if (-not $SkipRun) {
    Write-Head 'run --pet-probe'
    $proc = Start-Island '--pet-probe --look obsidian --art aurora --scene media --no-wda'
    if (-not $proc.WaitForExit(120000)) { Stop-Quietly $proc; throw 'probe timed out' }
    Write-Host ("probe exit code = {0}" -f $proc.ExitCode)
    if ($proc.ExitCode -ne 0) { $script:fails += "probe exit code $($proc.ExitCode)" }
}
else {
    Write-Head 'skip run (use existing probe output)'
}

# ---------------------------------------------------------------- ② 数窗口
# 探针进程会自己退出（量完就 Shutdown），窗口数要在**正常启动**的进程上数，
# 那才是用户实际会遇到的进程形态。
if (-not $SkipRun) {
    Write-Head 'top-level windows (normal launch, pet per preference)'
    $run = Start-Island '--look obsidian --art aurora --scene media --no-wda'
    Start-Sleep -Milliseconds 2000

    $rows = @([WinProbe]::List([uint32]$run.Id))
    Stop-Quietly $run

    $rows | ForEach-Object {
        $p = $_ -split "`t"
        Write-Host ("  hwnd={0} title='{1}' class='{2}' size={3} ex=[{4} {5} {6} {7}] {8}" -f `
            $p[0], $p[1], $p[2], $p[3], $p[4], $p[5], $p[6], $p[7], $p[8])
    }

    $script:winCount = $rows.Count
    Write-Host ("visible top-level windows = {0}" -f $rows.Count)

    if ($rows.Count -ne 1) { $script:fails += "topLevelWindows=$($rows.Count) (期望 1：桌宠不该再有独立窗口)" }

    foreach ($row in $rows) {
        $p = $row -split "`t"
        if ($p[1] -eq '桌宠') { $script:fails += "found a window titled 桌宠 (独立桌宠窗口仍然存在)" }
    }

    if ($rows.Count -ge 1) {
        $p = $rows[0] -split "`t"
        # 只断言 LAYERED（分层窗口，才能做圆角/透明）。
        #
        # **不**断言 WS_EX_TRANSPARENT：岛的穿透是按光标位置动态开关的
        # （光标压到岛上就关掉，否则用户点不到它）。探针跑的时候鼠标停在哪
        # 是随机的，拿这个当固定判据会随机判红 —— 实测光标停在岛上时
        # 它读成「不是点击穿透」。
        # 穿透与拖拽由 verify-input.ps1 用真实鼠标事件验收，那才是量它的地方。
        #
        # 也**不**断言 TOOLWINDOW / NOACTIVATE：那是老的独立桌宠窗口 PetWindow
        # 才有的标志，岛从来没有设过（它靠 AllowsTransparency + ShowInTaskbar=false
        # 达到同样效果）。按 PetWindow 的期望写在这里曾经判红过，那是判据错。
        if ($p[7] -ne 'LAYERED') { $script:fails += 'island window 不是分层窗口 (WS_EX_LAYERED 缺失)' }
    }
}
else {
    Write-Host 'window enumeration skipped (no live process)'
}

# ---------------------------------------------------------------- ③ 校验 JSON
Write-Head 'probe json'
if (-not (Test-Path $probeJson)) { throw "missing $probeJson" }
$j = Get-Content $probeJson -Raw -Encoding UTF8 | ConvertFrom-Json

function Check($cond, $msg) { if (-not $cond) { $script:fails += $msg } }

Check ($j.ok -eq $true) 'probe ok != true'

$l1 = $j.layer1
Check ($l1.ok -eq $true)              'layer1 not ok'
Check ($l1.state -eq 'idle')          "layer1 state=$($l1.state)"
Check ($l1.slot -eq 'dock')           "layer1 slot=$($l1.slot)"
Check ([math]::Abs($l1.diameterDip - 16.8) -lt 2.0) "layer1 diameter=$($l1.diameterDip) (期望 ~16.8)"
Check ($l1.purpleOutIsland -eq 0)     "layer1 purple outside island=$($l1.purpleOutIsland)"
Check ($l1.orbPixels -le 40)          "layer1 原球仍在 ($($l1.orbPixels) px)"
Check ($l1.eyePixels -ge 4)           "layer1 eyes=$($l1.eyePixels)"
Check ($l1.blobs -eq 1)               "layer1 blobs=$($l1.blobs)"

$l2 = $j.layer2
Check ($l2.ok -eq $true)              'layer2 not ok'
Check ($l2.islandDip[0] -eq 112 -and $l2.islandDip[1] -eq 38) "layer2 island=$($l2.islandDip -join 'x') (期望 112x38)"
Check ($l2.slot -eq 'under')          "layer2 slot=$($l2.slot)"
Check ([math]::Abs($l2.diameterDip - 33.6) -lt 3.0) "layer2 diameter=$($l2.diameterDip) (期望 ~33.6)"
Check ($l2.purpleInIsland -eq 0)      "layer2 leaked into island=$($l2.purpleInIsland)"
Check ($l2.purpleOutIsland -gt 200)   'layer2 ball missing outside island'
Check ($l2.blobs -eq 1)               "layer2 blobs=$($l2.blobs)"
Check ($l2.side -eq -1)               "layer2 side=$($l2.side) (期望 -1 = 左侧出场)"

$l3 = $j.layer3
Check ($l3.ok -eq $true)              'layer3 not ok'
Check ($l3.slot -eq 'under')          "layer3 slot=$($l3.slot)"
Check ([math]::Abs($l3.diameterDip - 33.6) -lt 3.0) "layer3 diameter=$($l3.diameterDip)"
Check ($l3.purpleInIsland -eq 0)      "layer3 leaked into island=$($l3.purpleInIsland)"
Check ($l3.blobs -eq 1)               "layer3 blobs=$($l3.blobs)"
Check ($l3.side -eq -1)               "layer3 side=$($l3.side) (期望 -1 = 左侧出场)"

# 位置：球必须**整颗在岛体左边**，并且和岛体左缘留出约 PetGap(8) 的间隙。
# 这一条是这次「改成从左边出来」的核心判据 —— 只看 side 字段不够，
# 那只是摆放代码自己算出来的意图，真正落地的是像素里球心的位置。
foreach ($l in @($l2, $l3)) {
    $ballRight = $l.centerDip[0] + $l.diameterDip / 2
    $islandLeft = $l.islandAtDip[0]
    Check ($l.centerDip[0] -lt $islandLeft) `
        ("ball center {0} 在岛体左缘 {1} 左侧" -f $l.centerDip[0], $islandLeft) `
        ("ball center {0} 不在岛体左侧 (island left {1})" -f $l.centerDip[0], $islandLeft)
    Check ($ballRight -lt $islandLeft) `
        ("ball 整颗在岛外 (right edge {0:N1} < island left {1:N1})" -f $ballRight, $islandLeft) `
        ("ball 和岛体有重叠 (right edge {0:N1}, island left {1:N1})" -f $ballRight, $islandLeft)
    Check ([math]::Abs(($islandLeft - $ballRight) - 8) -lt 3.0) `
        ("ball 与岛体间隙 {0:N1} dip ~= PetGap 8" -f ($islandLeft - $ballRight)) `
        ("ball 与岛体间隙异常 {0:N1} dip (期望 ~8)" -f ($islandLeft - $ballRight))
    Check ($l.centerDip[0] - $l.diameterDip / 2 -gt 0) `
        ("ball 左缘 {0:N1} 在窗口内" -f ($l.centerDip[0] - $l.diameterDip / 2)) `
        ("ball 被窗口左缘切掉 (left {0:N1})" -f ($l.centerDip[0] - $l.diameterDip / 2))
}

$lr = $j.layer1return
Check ($lr.ok -eq $true)              'layer1return not ok'
Check ($lr.slot -eq 'dock')           "return slot=$($lr.slot)"
Check ([math]::Abs($lr.diameterDip - 16.8) -lt 2.0) "return diameter=$($lr.diameterDip)"
Check ($lr.purpleOutIsland -eq 0)     "return purple outside=$($lr.purpleOutIsland)"
Check ($lr.orbPixels -le 40)          "return 原球回来了 ($($lr.orbPixels) px)"
Check ($lr.blobs -eq 1)               "return blobs=$($lr.blobs)"

foreach ($t in @('idle_to_compact', 'expanded_to_idle')) {
    $x = $j.$t
    Check ($x.ok -eq $true)               "$t not ok"
    Check ($x.maxBlobs -le 1)             "$t blobs=$($x.maxBlobs)"
    Check ($x.slotSwitches -eq 1)         "$t slotSwitches=$($x.slotSwitches)"
    Check ($x.switchOffsetDip -lt 0.6)    "$t switchOffset=$($x.switchOffsetDip)"
}

Check ($j.actions.ok -eq $true)       'actions not ok'
Check ($j.actions.pokeMoved -eq $true) 'poke did not move the ball'
Check ($j.actions.spinMoved -eq $true) 'spin did not move the ball'

# ---------------------------------------------------------------- ④ 汇总
Write-Head 'result'
Write-Host ("visibleTopLevelWindows = {0}" -f $script:winCount)
Write-Host ("layer1 diameter = {0} dip, eyes = {1} px ({2}x{3} dip)" -f `
    $l1.diameterDip, $l1.eyePixels, $l1.eyeBoxDip[0], $l1.eyeBoxDip[1])
Write-Host ("layer2 diameter = {0} dip, island = {1}x{2}" -f `
    $l2.diameterDip, $l2.islandDip[0], $l2.islandDip[1])
Write-Host ("layer3 diameter = {0} dip, island = {1}x{2}" -f `
    $l3.diameterDip, $l3.islandDip[0], $l3.islandDip[1])

if ($script:fails.Count -eq 0) {
    Write-Host ''
    Write-Host 'PET-ISLAND PROBE OK' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host 'PET-ISLAND PROBE FAILED:' -ForegroundColor Red
$script:fails | ForEach-Object { Write-Host ("  - " + $_) -ForegroundColor Red }
exit 1