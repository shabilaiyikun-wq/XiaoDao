# notify-activate.ps1
#
# Regression test for "clicking the notification ball sometimes freezes the island".
#
# Three proven defects, one assertion each:
#   1. the old window picker could return a HIDDEN window (SetForegroundWindow on a
#      hidden window always fails), so the picker must never do that;
#   2. the failed focus fell through to Process.Start(exe), and that hwnd->exe lookup
#      only works for an app that is ALREADY running -- so the "launch" branch could
#      only ever spawn a second instance, which then fights over the data dir and
#      hangs. The branch must be gone;
#   3. the whole sequence ran on the WPF UI thread, so any stall froze the island.
#      It must be dispatched off-thread.
#
# Defect 1 is tested against the real desktop; 2 and 3 are source assertions because
# they are structural properties, not runtime values.
#
# ASCII only.

param(
  [string]$Source = "E:\xiaodao\desktop\Notify.cs"
)

$fail = 0
function Check($cond, $ok, $bad) {
  if ($cond) { Write-Output "   ok   $ok" } else { Write-Output "   FAIL $bad"; $script:fail++ }
}

# ---------------------------------------------------------------- source checks
$raw = Get-Content $Source -Raw -Encoding UTF8

# strip comments first: the doc comment legitimately *mentions* Process.Start while
# explaining why it was removed, and a naive match would flag its own explanation.
$src = [regex]::Replace($raw, '/\*[\s\S]*?\*/', '')
$src = [regex]::Replace($src, '(?m)//.*$', '')

Check (-not ($src -match 'Process\.Start\(')) `
  "no Process.Start left in the activation path (cannot spawn a duplicate)" `
  "Process.Start is still reachable -- a second instance can be launched"

Check ($src -match 'QueueUserWorkItem') `
  "activation is dispatched off the UI thread" `
  "activation still runs synchronously on the UI thread"

Check ($src -match 'finally[\s\S]{0,400}AttachThreadInput\(GetCurrentThreadId\(\), fgThread, false\)') `
  "AttachThreadInput is always detached (try/finally)" `
  "input-queue attach can leak -- that wedges the thread"

Check ($src -match 'IsWindowVisible\(h\)') `
  "window picker filters on visibility" `
  "window picker has no visibility filter"

# ---------------------------------------------------------------- runtime check
# Replay the picker against the real windows of every WeChat/QQ process on this
# machine and require that whatever it returns is actually activatable.
$code = @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public class PICK
{
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassNameW(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);

    public struct RECT { public int L, T, R, B; }

    static string[] Noise = new string[] {
        "Base_PowerMessageWindow", "Chrome_SystemMessageWindow", "DisplayICC_SystemMessageWindow",
        "Qt51514WxTrayIconMessageWindowClass", "IME", "MSCTFIME UI", "GDI+ Hook Window Class",
        "TXGuiFoundation" };

    static bool IsNoise(string c)
    {
        for (int i = 0; i < Noise.Length; i++) if (Noise[i] == c) return true;
        return false;
    }

    // The OLD rule, kept only as a control: title match, else largest area with NO
    // visibility / noise filtering. This is what shipped before the fix.
    public static string PickOld(string pidsCsv, string title)
    {
        string[] parts = pidsCsv.Split(',');
        uint[] pids = new uint[parts.Length];
        for (int i = 0; i < parts.Length; i++) pids[i] = Convert.ToUInt32(parts[i]);

        IntPtr byTitle = IntPtr.Zero, byArea = IntPtr.Zero;
        long best = 0;
        string areaInfo = "";

        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            bool mine = false;
            for (int i = 0; i < pids.Length; i++) if (pids[i] == pid) mine = true;
            if (!mine) return true;

            StringBuilder tb = new StringBuilder(512);
            GetWindowTextW(h, tb, 512);
            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            int w = r.R - r.L, hh = r.B - r.T;

            if (tb.ToString() == title && byTitle == IntPtr.Zero) { byTitle = h; return true; }

            long area = (long)w * (long)hh;
            if (area > best) {
                best = area;
                byArea = h;
                StringBuilder cb = new StringBuilder(256);
                GetClassNameW(h, cb, 256);
                areaInfo = "cls=" + cb.ToString() + "|vis=" + (IsWindowVisible(h) ? 1 : 0)
                         + "|x=" + r.L + "|y=" + r.T;
            }
            return true;
        }, IntPtr.Zero);

        if (byTitle != IntPtr.Zero) return "byTitle";
        if (byArea != IntPtr.Zero) return "byArea|" + areaInfo;
        return "none";
    }
    // Same rule as the shipped FindMainWindow. Returns "title=<t>|cls=<c>|vis=<v>|x=<x>|y=<y>"
    public static string Pick(string pidsCsv, string title)
    {
        string[] parts = pidsCsv.Split(',');
        uint[] pids = new uint[parts.Length];
        for (int i = 0; i < parts.Length; i++) pids[i] = Convert.ToUInt32(parts[i]);

        IntPtr byTitle = IntPtr.Zero, byArea = IntPtr.Zero;
        long best = 0;
        string areaInfo = "", titleInfo = "";

        EnumWindows(delegate(IntPtr h, IntPtr p)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            bool mine = false;
            for (int i = 0; i < pids.Length; i++) if (pids[i] == pid) mine = true;
            if (!mine) return true;

            StringBuilder cb = new StringBuilder(256);
            GetClassNameW(h, cb, 256);
            if (IsNoise(cb.ToString())) return true;

            StringBuilder tb = new StringBuilder(512);
            GetWindowTextW(h, tb, 512);

            RECT r;
            if (!GetWindowRect(h, out r)) return true;
            int w = r.R - r.L, hh = r.B - r.T;
            if (w <= 0 || hh <= 0) return true;

            string info = "cls=" + cb.ToString() + "|vis=" + (IsWindowVisible(h) ? 1 : 0)
                        + "|x=" + r.L + "|y=" + r.T + "|w=" + w + "|h=" + hh;

            if (tb.ToString() == title && byTitle == IntPtr.Zero) { byTitle = h; titleInfo = info; return true; }
            if (!IsWindowVisible(h)) return true;
            if (r.L <= -30000 || r.T <= -30000) return true;

            long area = (long)w * (long)hh;
            if (area > best) { best = area; byArea = h; areaInfo = info; }
            return true;
        }, IntPtr.Zero);

        if (byTitle != IntPtr.Zero) return "byTitle|" + titleInfo;
        if (byArea != IntPtr.Zero) return "byArea|" + areaInfo;
        return "none";
    }
}
'@

Add-Type -TypeDefinition $code -Language CSharp

function Test-App($name, $pattern, $title) {
  $procs = Get-Process | Where-Object { $_.ProcessName -match $pattern }
  if (-not $procs) { Write-Output ("   --   {0} not running, picker not exercised" -f $name); return }
  $csv = ($procs | ForEach-Object { $_.Id }) -join ','
  $res = [PICK]::Pick($csv, $title)
  Write-Output ("   --   {0} -> {1}" -f $name, $res)
  if ($res -eq 'none') {
    # No activatable window. That is the correct answer, and it is exactly the case
    # the old rule got wrong: it would have handed back a hidden helper window.
    $oldRes = [PICK]::PickOld($csv, $title)
    Write-Output ("   --   {0} (old rule would pick) -> {1}" -f $name, $oldRes)
    if ($oldRes -ne 'none') {
      Check ($oldRes -match 'vis=0') ("{0}: none is right -- the old rule would have picked a hidden window" -f $name) ("{0}: picker found nothing but the old rule had a visible candidate" -f $name)
    }
    return
  }
  $vis = ($res -split '\|') | Where-Object { $_ -like 'vis=*' }
  $x = (($res -split '\|') | Where-Object { $_ -like 'x=*' }) -replace 'x=', ''
  Check ($vis -eq 'vis=1') ("{0}: picked window is visible" -f $name) ("{0}: picked a HIDDEN window -- SetForegroundWindow will fail" -f $name)
  Check ([int]$x -gt -30000) ("{0}: picked window is not a minimized placeholder" -f $name) ("{0}: picked a -32000 placeholder" -f $name)
}

Write-Output ""
Write-Output "picker against real windows:"
Test-App "WeChat" '^(Weixin|WeChat|WeChatAppEx)$' ([char]0x5FAE + [char]0x4FE1)
Test-App "QQ" '^QQ$' "QQ"

Write-Output ""
Write-Output ("RESULT fail={0}" -f $fail)
exit $fail