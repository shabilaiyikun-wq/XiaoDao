# ============================================================================
#  截图里的桌宠像素统计 —— 客观证据，不靠肉眼
#
#  桌宠本体是纯色 #5a4ac8（DiffuseMaterial + AmbientLight，无高光，见
#  notes\ANIMATION-PORT.md 第 5 节）。所以在整屏截图里数「接近这个颜色」的像素：
#    * 关掉桌宠的那张（对照）应当几乎没有；
#    * 打开桌宠的那张应当有一大块。
#  两张之间的差值，就是「桌宠真的画在桌面上」的直接证据。
#
#  用法： powershell -ExecutionPolicy Bypass -File desktop\verify\pet-pixels.ps1
# ============================================================================
$ErrorActionPreference = 'Stop'

$root   = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$outDir = Join-Path $root 'work\pet-unify-evidence'

Add-Type -AssemblyName System.Drawing

# 颜色容差：桌宠是纯色 + 均匀环境光，实际像素会因合成/缩放差几个数值；
# 壁纸里的紫蓝渐变离 (90,74,200) 还有明显距离，±26 足够分开。
$tol = 26
$targetR = 0x5a
$targetG = 0x4a
$targetB = 0xc8

function Count-PetPixels([string]$path) {
    $bmp = [System.Drawing.Bitmap]::FromFile($path)
    $w = $bmp.Width; $h = $bmp.Height
    $count = 0
    $minX = $w; $maxX = -1; $minY = $h; $maxY = -1
    try {
        # LockBits + 直接读字节比 GetPixel 快两个数量级（1080p 是 200 万像素）
        $rect = New-Object System.Drawing.Rectangle 0, 0, $w, $h
        $data = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly,
                              [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        try {
            $stride = $data.Stride
            $bytes = New-Object byte[] ($stride * $h)
            [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $bytes, 0, $bytes.Length)
            for ($y = 0; $y -lt $h; $y++) {
                $row = $y * $stride
                for ($x = 0; $x -lt $w; $x++) {
                    $i = $row + ($x * 4)
                    $b = $bytes[$i]; $g = $bytes[$i + 1]; $r = $bytes[$i + 2]
                    if ([Math]::Abs($b - $targetB) -le $tol -and
                        [Math]::Abs($g - $targetG) -le $tol -and
                        [Math]::Abs($r - $targetR) -le $tol) {
                        $count++
                        if ($x -lt $minX) { $minX = $x }
                        if ($x -gt $maxX) { $maxX = $x }
                        if ($y -lt $minY) { $minY = $y }
                        if ($y -gt $maxY) { $maxY = $y }
                    }
                }
            }
        } finally { $bmp.UnlockBits($data) }
    } finally { $bmp.Dispose() }

    [pscustomobject]@{
        File    = [System.IO.Path]::GetFileName($path)
        Pixels  = $count
        Box     = if ($count -gt 0) { "$minX,$minY .. $maxX,$maxY" } else { '-' }
        BoxSize = if ($count -gt 0) { "$($maxX-$minX+1)x$($maxY-$minY+1)" } else { '-' }
    }
}

$shots = @('B-pref-off.png', 'A-normal-start.png', 'C-pref-on.png')
$rows = @()
foreach ($s in $shots) {
    $p = Join-Path $outDir $s
    if (-not (Test-Path $p)) { Write-Host ("[skip] missing " + $s); continue }
    $rows += Count-PetPixels $p
}

Write-Host ''
Write-Host ('pet-colour (#5a4ac8 +/-' + $tol + ') pixels per screenshot')
Write-Host ('-' * 78)
$rows | Format-Table -AutoSize | Out-String -Width 120 | Write-Host

$off  = ($rows | Where-Object { $_.File -eq 'B-pref-off.png' }).Pixels
$on   = @($rows | Where-Object { $_.File -ne 'B-pref-off.png' })
$ok   = $true
foreach ($r in $on) {
    $delta = $r.Pixels - $off
    $pass = $delta -gt 2000          # 桌宠球体在任何 DPI 下都远大于这个数
    if (-not $pass) { $ok = $false }
    Write-Host ("{0} {1}: {2} px vs control {3} px -> delta {4}" -f
                $(if ($pass) { 'PASS' } else { 'FAIL' }), $r.File, $r.Pixels, $off, $delta)
}

$rows | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $outDir 'pet-pixels.json') -Encoding UTF8
Write-Host ''
if ($ok) { Write-Host 'ALL PASS (pet is visibly painted on the desktop)'; exit 0 }
Write-Host 'FAILURES: pet pixels not found where expected'; exit 1