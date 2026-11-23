param([int]$HoldSeconds = 12, [switch]$VideoProbe, [int]$StreamSeconds=0, [switch]$AudioProbe, [int]$ServerSeconds=45)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path -Parent $PSScriptRoot
$workspaceRoot=Split-Path -Parent $projectRoot
$output=Join-Path $projectRoot 'artifacts\usb-client-test'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$recovery=Join-Path $projectRoot 'artifacts\recovery\win-x64\Vhui.Recovery.exe'
$clientPath=Join-Path $workspaceRoot 'analysis\client\recovered\vhui64.exe'
$server=$null; $client=$null; $address=$null
$report=[ordered]@{
    Phase='Starting'; Passed=$false; Captured=$false; Released=$false
    TimestampUtc=[DateTime]::UtcNow.ToString('o')
    NativeClientVersion=(Get-Item -LiteralPath $clientPath).VersionInfo.ProductVersion
    CoreSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path (Split-Path -Parent $recovery) 'Vhui.Core.dll')).Hash
    PhysicalCameraBefore=$null; VirtualCamera=$null; PhysicalCameraAfter=$null
    VideoFrameRead=$false; FrameFromVirtualDevice=$false; VideoFrame=$null
    PhysicalDriverRestored=$false; ControlRequests=0; PipeRequests=0; IsochronousRequests=0; Error=$null
}
function Save { $report | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $output 'result.json') }
function Ipc([string]$Command,[string]$Name) {
    $path=Join-Path $output $Name
    $process=Start-Process $clientPath -WindowStyle Hidden -PassThru -ArgumentList @('-t',('"'+$Command+'"'),'-r',('"'+$path+'"'))
    try { if (-not $process.WaitForExit(10000)) { $process.Kill(); throw 'IPC timeout' }; return $path }
    finally { $process.Dispose() }
}
try {
    $principal=New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this test through UAC.' }
    if (Get-Process vhui64,vhusbdwinw64 -ErrorAction SilentlyContinue) { throw 'Close other native instances before testing.' }
    $report.PhysicalCameraBefore=@(Get-PnpDevice -Class Camera -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like '*VID_145F&PID_02E7*' } | Select-Object Status,FriendlyName,InstanceId)
    $server=Start-Process $recovery -WorkingDirectory $output -WindowStyle Hidden -PassThru -ArgumentList @('serve-usb','145F:02E7','17570',"$ServerSeconds") -RedirectStandardError (Join-Path $output 'server.log')
    for ($attempt=0;$attempt -lt 50;$attempt++) {
        if ($server.HasExited) { throw 'C# USB server exited.' }
        if ((Test-Path (Join-Path $output 'server.log')) -and (Select-String -LiteralPath (Join-Path $output 'server.log') -SimpleMatch 'Listening' -Quiet)) { break }
        Start-Sleep -Milliseconds 100
    }
    $config=Join-Path $output 'client.ini'
    [IO.File]::WriteAllText($config,"[General]`r`nAutoFind=0`r`nAutoMinimize=1`r`n[Settings]`r`nManualHubs=127.0.0.1:17570`r`n")
    $client=Start-Process $clientPath -WorkingDirectory $output -WindowStyle Hidden -PassThru -ArgumentList @('-c',('"'+$config+'"'),('-l="'+(Join-Path $output 'client.log')+'"'))
    for ($attempt=0;$attempt -lt 20;$attempt++) {
        Start-Sleep -Milliseconds 400
        [xml]$state=Get-Content -Raw -Encoding UTF8 (Ipc 'GET CLIENT STATE' 'before.xml')
        $device=@($state.state.server.device) | Where-Object { $_.idVendor -eq '5215' -and $_.idProduct -eq '743' } | Select-Object -First 1
        if ($device) { break }
    }
    if (-not $device) { throw 'Client did not discover the C# camera export.' }
    $address=$state.state.server.connection.hostname+'.'+$device.address
    $report.Phase='Capturing'; Save
    Ipc ('USE,'+$address) 'use.txt' | Out-Null
    if ((Get-Content -Raw -Encoding UTF8 (Join-Path $output 'use.txt')) -match 'FAILED|ERROR') { throw 'Native client rejected USE; inspect use.txt and server.log.' }
    for ($attempt=0;$attempt -lt 30;$attempt++) {
        Start-Sleep -Milliseconds 300
        [xml]$state=Get-Content -Raw -Encoding UTF8 (Ipc 'GET CLIENT STATE' 'captured.xml')
        $device=@($state.state.server.device) | Where-Object { $_.idVendor -eq '5215' -and $_.idProduct -eq '743' } | Select-Object -First 1
        if ($device.state -eq '3') { $report.Captured=$true; break }
        if ($server.HasExited) { throw 'C# server exited during capture.' }
    }
    $report.Phase='Holding'; Save
    if (-not $report.Captured) { throw 'Client did not enter the in-use state.' }
    Start-Sleep -Seconds $HoldSeconds
    # Protocol state 3 precedes Windows camera-interface registration. Wait for PnP, not a fixed sleep.
    for ($ready=0;$ready -lt 30;$ready++) {
        $virtualReady=@(Get-PnpDevice -Class Camera -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like '*VID_145F&PID_02E7*' -and $_.Status -eq 'OK' -and $_.InstanceId -notin @($report.PhysicalCameraBefore.InstanceId) })
        if ($virtualReady.Count -gt 0) { break }
        Start-Sleep -Milliseconds 300
    }
    if (-not $virtualReady.Count) { throw 'Virtual camera did not reach the PnP ready state.' }
    Start-Sleep -Milliseconds 500
    $report.VirtualCamera=@(Get-PnpDevice -Class Camera -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like '*VID_145F&PID_02E7*' } | Select-Object Status,FriendlyName,InstanceId)
    if ($VideoProbe -or $StreamSeconds -gt 0) {
        $report.Phase='ReadingVideo'; Save
        $videoArguments=@('camera-probe','Trust')
        if ($StreamSeconds -gt 0) { $videoArguments=@('camera-stream','Trust',"$StreamSeconds") }
        $video=Start-Process $recovery -WorkingDirectory $output -WindowStyle Hidden -PassThru -ArgumentList $videoArguments `
            -RedirectStandardOutput (Join-Path $output 'virtual-frame.json') -RedirectStandardError (Join-Path $output 'virtual-frame.stderr.txt')
        try {
            $null=$video.Handle
            if (-not $video.WaitForExit([Math]::Max(18000,($StreamSeconds+25)*1000))) { $video.Kill(); throw 'Video frame read timed out.' }
            $video.Refresh()
            if ($video.ExitCode -ne 0) { throw ('Camera probe failed, exit '+$video.ExitCode+': '+(Get-Content -Raw -Encoding UTF8 (Join-Path $output 'virtual-frame.stderr.txt'))) }
            $report.VideoFrame=Get-Content -Raw -Encoding UTF8 (Join-Path $output 'virtual-frame.json') | ConvertFrom-Json
            $report.VideoFrameRead=$report.VideoFrame.Bytes -gt 0
            foreach ($virtual in $report.VirtualCamera) {
                if ($report.VideoFrame.SymbolicLink.IndexOf($virtual.InstanceId.Replace('\','#'),[StringComparison]::OrdinalIgnoreCase) -ge 0) { $report.FrameFromVirtualDevice=$true }
            }
        }
        finally { $video.Dispose() }
    }
    if ($AudioProbe) {
        $report.Phase='ReadingAudio'; Save
        $audio=Start-Process $recovery -WorkingDirectory $output -WindowStyle Hidden -PassThru -ArgumentList @('audio-probe','Trust','10') `
            -RedirectStandardOutput (Join-Path $output 'virtual-audio.json') -RedirectStandardError (Join-Path $output 'virtual-audio.stderr.txt')
        try {
            $null=$audio.Handle
            if (-not $audio.WaitForExit(20000)) { $audio.Kill(); throw 'Audio probe timeout.' }
            $audio.Refresh()
            if ($audio.ExitCode -ne 0) { throw ('Audio probe failed: '+(Get-Content -Raw -Encoding UTF8 (Join-Path $output 'virtual-audio.stderr.txt'))) }
            $report.Audio=Get-Content -Raw -Encoding UTF8 (Join-Path $output 'virtual-audio.json') | ConvertFrom-Json
            $report.AudioRead=$report.Audio.Bytes -gt 0
        } finally { $audio.Dispose() }
    }
    Ipc 'GET CLIENT STATE' 'held.xml' | Out-Null
}
catch { $report.Error=$_.Exception.ToString() }
finally {
    if ($client -and -not $client.HasExited) {
        if ($address) {
            try {
                Ipc ('STOP USING,'+$address) 'stop.txt' | Out-Null
                Start-Sleep -Seconds 3
                [xml]$after=Get-Content -Raw -Encoding UTF8 (Ipc 'GET CLIENT STATE' 'after.xml')
                $releasedDevice=@($after.state.server.device) | Where-Object { $_.idVendor -eq '5215' -and $_.idProduct -eq '743' } | Select-Object -First 1
                $report.Released=$releasedDevice.state -eq '1'
            }
            catch { $report.Error="$($report.Error) Release: $_" }
        }
        try { Ipc 'EXIT' 'exit.txt' | Out-Null } catch { }
        if (-not $client.WaitForExit(3000)) { $client.Kill() }
    }
    # Let the managed server finish its finally blocks and restore Windows' driver.
    if ($server -and -not $server.HasExited) { $server.WaitForExit(($ServerSeconds+10)*1000) | Out-Null }
    $report.PhysicalCameraAfter=@(Get-PnpDevice -Class Camera -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like '*VID_145F&PID_02E7*' } | Select-Object Status,FriendlyName,InstanceId)
    $journals=@(Get-ChildItem -LiteralPath (Join-Path (Split-Path -Parent $recovery) 'usb-leases') -Filter '*.json' -ErrorAction SilentlyContinue | ForEach-Object { Get-Content -Raw -Encoding UTF8 $_.FullName | ConvertFrom-Json })
    $report.PhysicalDriverRestored=($report.PhysicalCameraAfter.Count -gt 0 -and @($report.PhysicalCameraAfter | Where-Object { $_.Status -ne 'OK' }).Count -eq 0 `
        -and @($journals | Where-Object { $_.Phase -ne 'Restored' }).Count -eq 0 `
        -and @($report.PhysicalCameraAfter | Where-Object { $_.InstanceId -in @($report.PhysicalCameraBefore.InstanceId) }).Count -gt 0)
    foreach ($line in Get-Content -Encoding UTF8 (Join-Path $output 'server.log')) {
        try { $event=$line | ConvertFrom-Json } catch { continue }
        if ($event.Event -ne 'Received') { continue }
        if ($event.Type -eq 13) { $report.ControlRequests++ }
        if ($event.Type -in @(18,74)) { $report.PipeRequests++ }
        if ($event.Type -in @(36,77)) { $report.IsochronousRequests++ }
    }
    $report.Passed=($report.Captured -and $report.Released -and $report.PhysicalDriverRestored -and -not $report.Error `
        -and (-not ($VideoProbe -or $StreamSeconds -gt 0) -or ($report.VideoFrameRead -and $report.FrameFromVirtualDevice)) `
        -and (-not $AudioProbe -or $report.AudioRead))
    $report.Phase='Finished'; Save
}
