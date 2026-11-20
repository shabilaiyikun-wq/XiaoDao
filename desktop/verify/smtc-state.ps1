# smtc-state.ps1 -- read the real system media transport controls state.
# Independent evidence for "is a media app actually PLAYING right now?", which is
# exactly what the island's compact-layer gate should be testing.
# ASCII only.

Add-Type -AssemblyName System.Runtime.WindowsRuntime

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and
    $_.GetParameters().Count -eq 1 -and
    $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]

function Await($op, $type) {
    $m = $asTaskGeneric.MakeGenericMethod($type)
    $task = $m.Invoke($null, @($op))
    [void]$task.Wait(-1)
    $task.Result
}

$mgrType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]

try {
    $mgr = Await ($mgrType::RequestAsync()) $mgrType
    $sessions = $mgr.GetSessions()
    if (-not $sessions -or $sessions.Count -eq 0) {
        Write-Output "no media sessions"
        exit 0
    }
    foreach ($s in $sessions) {
        $info = $s.GetPlaybackInfo()
        $props = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
        Write-Output ("app=" + $s.SourceAppUserModelId)
        Write-Output ("  status = " + $info.PlaybackStatus)
        if ($props) { Write-Output ("  title  = " + $props.Title) }
    }
} catch {
    Write-Output ("ERR " + $_.Exception.Message)
    exit 1
}