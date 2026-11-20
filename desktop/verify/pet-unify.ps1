# ============================================================================
#  桌宠 / 岛 同进程共存 —— 真实启动验收
#
#  探针（notes\probes\probe-pet-unify.cs）验的是代码路径；这里验的是
#  「双击 WinIsland.exe 会发生什么」：真的是两个窗口、真的在同一个进程、
#  桌宠真的画在桌面上、偏好真的决定开局、诊断模式真的不弹桌宠。
#
#  每一个用例都从干净的 ui.txt 出发，跑完把 ui.txt 还原，
#  所以反复跑不会污染用户自己的偏好。
#
#  用法： powershell -ExecutionPolicy Bypass -File desktop\verify\pet-unify.ps1
# ============================================================================
$ErrorActionPreference = 'Stop'

$root    = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)   # E:\xiaodao
$exe     = Join-Path $root 'desktop\WinIsland.exe'
$appData = Join-Path $env:APPDATA 'WinIsland'
$uiPath  = Join-Path $appData 'ui.txt'
$outDir  = Join-Path $root 'work\pet-unify-evidence'

if (-not (Test-Path $exe)) { throw "missing $exe (run desktop\build.cmd first)" }
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$results = New-Object System.Collections.Generic.List[object]

function Check([string]$name, [bool]$ok, [string]$detail) {
    $script:results.Add([pscustomobject]@{ Name = $name; Ok = $ok; Detail = $detail })
    $tag = if ($ok) { 'PASS' } else { 'FAIL' }
    Write-Host ("{0} {1}{2}" -f $tag, $name, $(if ($detail) { "  ($detail)" } else { '' }))
}

# ---------------------------------------------------------------- 窗口枚举
if (-not ('W32Win' -as [type])) {
    Add-Type @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public class W32Win
{
    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder s, int n);

    public static List<string> TopLevel(uint pid)
    {
        List<string> found = new List<string>();
        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            uint owner;
            GetWindowThreadProcessId(h, out owner);
            if (owner != pid) return true;
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            StringBuilder t = new StringBuilder(len + 2);
            GetWindowText(h, t, t.Capacity);
            StringBuilder c = new StringBuilder(256);
            GetClassName(h, c, c.Capacity);
            found.Add(t.ToString() + "|" + c.ToString());
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
'@
}

function Get-Windows([int]$processId) { [W32Win]::TopLevel([uint32]$processId) }

function Stop-Island {
    Get-Process -Name WinIsland -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 700
}

# 写一份受控的 ui.txt。
# $petValue = '__absent__' 表示「文件里干脆没有 pet 这一行」，也就是从没设置过
# 偏好的新用户。这里不能用 $null 传「缺席」——[string] 参数会把 $null 转成空串，
# 结果写成 "pet="（应用侧会当成没设置，但文件内容就不是「没有这一行」了）。
function Set-Prefs([string]$petValue) {
    New-Item -ItemType Directory -Force -Path $appData | Out-Null
    $lines = @('scene=media', 'look=glass', 'art=aurora', 'capture=1', 'transferOut=0')
    if ($petValue -ne '__absent__') { $lines += "pet=$petValue" }
    Set-Content -Path $uiPath -Value $lines -Encoding UTF8
}

function Get-Pref([string]$key) {
    if (-not (Test-Path $uiPath)) { return $null }
    foreach ($line in Get-Content $uiPath) {
        $eq = $line.IndexOf('=')
        if ($eq -le 0) { continue }
        if ($line.Substring(0, $eq).Trim() -eq $key) { return $line.Substring($eq + 1).Trim() }
    }
    return $null
}

function Save-Screen([string]$name) {
    Add-Type -AssemblyName System.Drawing
    $bounds = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $bmp = New-Object System.Drawing.Bitmap $bounds.Width, $bounds.Height
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, $bmp.Size)
    $g.Dispose()
    $path = Join-Path $outDir $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return $path
}

Add-Type -AssemblyName System.Windows.Forms

$backup = $null
if (Test-Path $uiPath) { $backup = Get-Content $uiPath -Raw }
# 位置文件也会被这轮启动改写（岛落点 / 桌宠落点），一并备份还原
$posWinTxt = Join-Path $appData 'window.txt'
$posPetTxt = Join-Path $appData 'pet.txt'
$backupWin = if (Test-Path $posWinTxt) { Get-Content $posWinTxt -Raw } else { $null }
$backupPet = if (Test-Path $posPetTxt) { Get-Content $posPetTxt -Raw } else { $null }

Write-Host ''
Write-Host '=== A. 首次正常启动（ui.txt 里没有 pet 这一行）==='
Stop-Island
Set-Prefs '__absent__'
$logA = Join-Path $outDir 'A-normal-start.log'
Remove-Item $logA -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--log', $logA -PassThru
Start-Sleep -Seconds 4
$w = Get-Windows $p.Id
$p.Refresh()
$alive = -not $p.HasExited
Check 'process is running' $alive ("pid=" + $p.Id)
Check 'two top-level windows in ONE process' ($w.Count -eq 2) ($w.Count.ToString() + ': ' + ($w -join ' ; '))
$petWin = @($w | Where-Object { $_ -like '桌宠|*' })
$islandWin = @($w | Where-Object { $_ -like '|*' })
Check 'one window is the pet (title 桌宠)' ($petWin.Count -eq 1) ($petWin -join ',')
Check 'one window is the island' ($islandWin.Count -eq 1) ($islandWin -join ',')
$petPrefAfterA = Get-Pref 'pet'
Check 'first run defaults to pet ON (no pet key in ui.txt, nothing written back)' ($null -eq $petPrefAfterA) ("ui.txt pet=" + $petPrefAfterA)
$shotA = Save-Screen 'A-normal-start.png'
Check 'screenshot captured' (Test-Path $shotA) $shotA
$logTextA = if (Test-Path $logA) { Get-Content $logA -Raw } else { '' }
Check 'pet trace appears in the log' ($logTextA.Contains('[pet] pet enabled')) ''
Stop-Island

Write-Host ''
Write-Host '=== B. 偏好 pet=0（用户上次关掉了）==='
Set-Prefs '0'
$logB = Join-Path $outDir 'B-pref-off.log'
Remove-Item $logB -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--log', $logB -PassThru
Start-Sleep -Seconds 4
$w = Get-Windows $p.Id
Check 'only the island window (no pet)' ($w.Count -eq 1) ($w.Count.ToString() + ': ' + ($w -join ' ; '))
Check 'no pet window title present' (@($w | Where-Object { $_ -like '桌宠|*' }).Count -eq 0) ($w -join ',')
Check 'preference untouched by launch' ((Get-Pref 'pet') -eq '0') ("pet=" + (Get-Pref 'pet'))
# 同一屏、同样光照下截一张「没有桌宠」的对照图：下面 pet-pixels 拿它和
# A/C 两张比紫色像素数，差值就是桌宠真的画在桌面上的证据。
$shotB = Save-Screen 'B-pref-off.png'
Check 'control screenshot captured (pet off)' (Test-Path $shotB) $shotB
Stop-Island

Write-Host ''
Write-Host '=== C. 偏好 pet=1 ==='
Set-Prefs '1'
$logC = Join-Path $outDir 'C-pref-on.log'
Remove-Item $logC -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--log', $logC -PassThru
Start-Sleep -Seconds 4
$w = Get-Windows $p.Id
Check 'pet is back with the same preference' ($w.Count -eq 2) ($w.Count.ToString() + ': ' + ($w -join ' ; '))
Check 'pet window present' (@($w | Where-Object { $_ -like '桌宠|*' }).Count -eq 1) ($w -join ',')
$shotC = Save-Screen 'C-pref-on.png'
Check 'screenshot captured' (Test-Path $shotC) $shotC
# 位置分开存：两个文件都要在，且内容不同（岛 window.txt / 桌宠 pet.txt）
$winTxt = Join-Path $appData 'window.txt'
$petTxt = Join-Path $appData 'pet.txt'
Check 'island position file exists' (Test-Path $winTxt) $winTxt
Check 'pet position file exists' (Test-Path $petTxt) $petTxt
if ((Test-Path $winTxt) -and (Test-Path $petTxt)) {
    $iw = (Get-Content $winTxt -Raw).Trim()
    $pw = (Get-Content $petTxt -Raw).Trim()
    Check 'position files hold different values' ($iw -ne $pw) ("window.txt='" + $iw + "' pet.txt='" + $pw + "'")
}
Stop-Island

Write-Host ''
Write-Host '=== D. --selftest 不自动弹桌宠、不覆盖偏好 ==='
Set-Prefs '1'                       # 用户明确想要桌宠
$logD = Join-Path $outDir 'D-selftest.log'
Remove-Item $logD -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--selftest', '--log', $logD -PassThru

# 更强的证据：自检跑着的时候一直数「标题叫桌宠的窗口」。
# 不能数窗口总数 —— 自检自己会打开右键菜单和子菜单 Popup，那也都是 top-level
# HWND（实测会到 3 个）。桌宠的特征是 Title="桌宠"（PetWindow 里写死的），
# 所以只按标题认它。
$petSeenD = 0
$islandSeenD = 0
for ($i = 0; $i -lt 24; $i++) {
    if ($p.HasExited) { break }
    $ww = @(Get-Windows $p.Id)
    if (@($ww | Where-Object { $_ -like '桌宠|*' }).Count -gt $petSeenD) { $petSeenD = @($ww | Where-Object { $_ -like '桌宠|*' }).Count }
    if (@($ww | Where-Object { $_ -like '|*' }).Count -gt 0) { $islandSeenD++ }
    Start-Sleep -Milliseconds 500
}
$p.WaitForExit()
$logTextD = if (Test-Path $logD) { Get-Content $logD -Raw } else { '' }
Check 'selftest finished (exit 0)' ($p.ExitCode -eq 0) ("exit=" + $p.ExitCode)
Check 'selftest ran to completion' ($logTextD.Contains('SELFTEST OK')) ''
Check 'selftest never enabled the pet' (-not $logTextD.Contains('[pet] pet enabled')) ''
Check 'selftest showed no pet window at any sample' (($petSeenD -eq 0) -and ($islandSeenD -gt 0)) ("petWindows=" + $petSeenD + " islandSamples=" + $islandSeenD)
Check 'selftest did not overwrite the pet preference' ((Get-Pref 'pet') -eq '1') ("pet=" + (Get-Pref 'pet'))

Write-Host ''
Write-Host '=== E. --pet 独立诊断模式（只桌宠、不建岛）==='
Stop-Island
$p = Start-Process -FilePath $exe -ArgumentList '--pet' -PassThru
Start-Sleep -Seconds 3
$w = Get-Windows $p.Id
Check 'standalone --pet shows exactly one window' ($w.Count -eq 1) ($w.Count.ToString() + ': ' + ($w -join ' ; '))
Check 'that window is the pet, not the island' (@($w | Where-Object { $_ -like '桌宠|*' }).Count -eq 1) ($w -join ',')
Stop-Island

Write-Host ''
Write-Host '=== F. --pet-selftest 独立诊断模式（退出码）==='
$p = Start-Process -FilePath $exe -ArgumentList '--pet-selftest' -PassThru -Wait
Check '--pet-selftest exit code 0' ($p.ExitCode -eq 0) ("exit=" + $p.ExitCode)
$st = Join-Path $root 'desktop\pet-selftest.json'
Check 'pet self-test report written' (Test-Path $st) $st
if (Test-Path $st) {
    $json = Get-Content $st -Raw
    Check 'pet self-test reports ok' ($json -match '"ok"\s*:\s*true') ''
}

Write-Host ''
Write-Host '=== G. --no-pet：本次不要桌宠，且不改偏好 ==='
Set-Prefs '1'                       # 用户偏好是「想要桌宠」
$logG = Join-Path $outDir 'G-no-pet.log'
Remove-Item $logG -ErrorAction SilentlyContinue
$p = Start-Process -FilePath $exe -ArgumentList '--no-pet', '--log', $logG -PassThru
Start-Sleep -Seconds 4
$w = Get-Windows $p.Id
Check '--no-pet shows only the island' ($w.Count -eq 1) ($w.Count.ToString() + ': ' + ($w -join ' ; '))
Check '--no-pet shows no pet window' (@($w | Where-Object { $_ -like '桌宠|*' }).Count -eq 0) ($w -join ',')
Check '--no-pet left the preference at pet=1' ((Get-Pref 'pet') -eq '1') ("pet=" + (Get-Pref 'pet'))
Stop-Island

Write-Host ''
Write-Host '=== H. 退出后不留进程 ==='
Stop-Island
$left = @(Get-Process -Name WinIsland -ErrorAction SilentlyContinue)
Check 'no WinIsland process left behind' ($left.Count -eq 0) ($left.Count.ToString() + ' left')

# ---------------------------------------------------------------- 还原
if ($null -ne $backup) { Set-Content -Path $uiPath -Value $backup -NoNewline -Encoding UTF8 }
if ($null -ne $backupWin) { Set-Content -Path $posWinTxt -Value $backupWin -NoNewline -Encoding UTF8 }
if ($null -ne $backupPet) { Set-Content -Path $posPetTxt -Value $backupPet -NoNewline -Encoding UTF8 }
Write-Host ''
Write-Host ("ui.txt / window.txt / pet.txt restored from backup" )

$failed = @($results | Where-Object { -not $_.Ok })
$results | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $outDir 'pet-unify-results.json') -Encoding UTF8
Write-Host ''
if ($failed.Count -eq 0) {
    Write-Host ("ALL PASS ({0} checks)" -f $results.Count)
    exit 0
}
Write-Host ("FAILURES: {0} / {1}" -f $failed.Count, $results.Count)
$failed | ForEach-Object { Write-Host ("  FAIL " + $_.Name + "  " + $_.Detail) }
exit 1