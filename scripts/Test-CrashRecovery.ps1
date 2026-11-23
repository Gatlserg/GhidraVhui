param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$workspace=Split-Path -Parent $project
$output=Join-Path $project 'artifacts\crash-recovery'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe=Join-Path $project 'artifacts\recovery\win-x64\Vhui.Recovery.exe'
$clientExe=Join-Path $workspace 'analysis\client\recovered\vhui64.exe'
$server=$null; $client=$null; $journal=$null
$report=[ordered]@{Phase='Starting';Passed=$false;Captured=$false;KilledOwnedServer=$false;JournalRecovered=$false;PhysicalCameraRecovered=$false;PhysicalFrameRead=$false;Error=$null}
$report.CoreSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path (Split-Path -Parent $exe) 'Vhui.Core.dll')).Hash
function Save { $report | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $output 'result.json') }
function Ipc([string]$Text,[string]$Name) {
    $path=Join-Path $output $Name
    $p=Start-Process $clientExe -WindowStyle Hidden -PassThru -ArgumentList @('-t',('"'+$Text+'"'),'-r',('"'+$path+'"'))
    try { $null=$p.Handle; if (-not $p.WaitForExit(10000)) { $p.Kill(); throw 'IPC timeout.' } } finally { $p.Dispose() }
    return $path
}
function Start-OwnedServer([int]$Seconds,[string]$Name) {
    $path=Join-Path $output $Name
    $p=Start-Process $exe -WindowStyle Hidden -PassThru -WorkingDirectory $output -ArgumentList @('serve-usb','145F:02E7','17570',"$Seconds") -RedirectStandardError $path
    for ($attempt=0;$attempt -lt 100;$attempt++) {
        if ($p.HasExited) { throw ('Server startup failed: '+(Get-Content -Raw -Encoding UTF8 $path)) }
        if ((Test-Path $path) -and (Select-String -LiteralPath $path -SimpleMatch 'Listening' -Quiet)) { return $p }
        Start-Sleep -Milliseconds 200
    }
    $p.Kill(); throw 'Server did not start.'
}
try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run elevated.' }
    if (Get-Process vhui64,vhusbdwinw64,Vhui.Recovery -ErrorAction SilentlyContinue) { throw 'Existing client/server processes are not owned by this test.' }
    Save
    $before=@(Get-PnpDevice -Class Camera -PresentOnly | Where-Object { $_.InstanceId -like '*VID_145F&PID_02E7*' })
    $server=Start-OwnedServer 120 'before-crash.log'
    $config=Join-Path $output 'client.ini'
    [IO.File]::WriteAllText($config,"[General]`r`nAutoFind=0`r`nAutoMinimize=1`r`n[Settings]`r`nManualHubs=127.0.0.1:17570`r`n")
    $client=Start-Process $clientExe -WindowStyle Hidden -PassThru -WorkingDirectory $output -ArgumentList @('-c',('"'+$config+'"'))
    for ($attempt=0;$attempt -lt 30;$attempt++) {
        Start-Sleep -Milliseconds 300
        [xml]$state=Get-Content -Raw -Encoding UTF8 (Ipc 'GET CLIENT STATE' 'state.xml')
        $device=@($state.state.server.device | Where-Object { $_.idVendor -eq '5215' -and $_.idProduct -eq '743' }) | Select-Object -First 1
        if ($device) { break }
    }
    if (-not $device) { throw 'Device was not discovered.' }
    $address=$state.state.server.connection.hostname+'.'+$device.address
    Ipc ('USE,'+$address) 'use.txt' | Out-Null
    for ($attempt=0;$attempt -lt 100;$attempt++) {
        foreach ($file in Get-ChildItem -LiteralPath (Join-Path (Split-Path -Parent $exe) 'usb-leases') -Filter '*.json') {
            $record=Get-Content -Raw -Encoding UTF8 $file.FullName | ConvertFrom-Json
            if ($record.Phase -eq 'Captured' -and $record.OwnerProcessId -eq $server.Id) { $journal=$file.FullName; break }
        }
        if ($journal) { break }; Start-Sleep -Milliseconds 100
    }
    if (-not $journal) { throw 'Capture journal was not committed.' }
    $report.Captured=$true; $report.Phase='Crashing'; Save
    $server.Kill(); $server.WaitForExit(); $server.Dispose(); $server=$null; $report.KilledOwnedServer=$true
    Ipc 'EXIT' 'exit.txt' | Out-Null
    if (-not $client.WaitForExit(3000)) { $client.Kill() }; $client.Dispose(); $client=$null
    Start-Sleep -Seconds 2
    $report.Phase='Restarting'; Save
    $server=Start-OwnedServer 10 'after-restart.log'
    $record=Get-Content -Raw -Encoding UTF8 $journal | ConvertFrom-Json
    $report.JournalRecovered=$record.Phase -eq 'Restored'
    $after=@(Get-PnpDevice -Class Camera -PresentOnly | Where-Object { $_.InstanceId -like '*VID_145F&PID_02E7*' -and $_.Status -eq 'OK' })
    $report.PhysicalCameraRecovered=@($after | Where-Object { $_.InstanceId -in @($before.InstanceId) }).Count -gt 0
    & $exe camera-probe Trust *> (Join-Path $output 'physical-frame.json')
    if ($LASTEXITCODE -eq 0) {
        $frame=Get-Content -Raw -Encoding UTF8 (Join-Path $output 'physical-frame.json') | ConvertFrom-Json
        $report.PhysicalFrameRead=$frame.Bytes -gt 0
    }
    $server.WaitForExit(20000) | Out-Null
    $report.Passed=$report.Captured -and $report.KilledOwnedServer -and $report.JournalRecovered -and $report.PhysicalCameraRecovered -and $report.PhysicalFrameRead
} catch { $report.Error=$_.Exception.ToString() }
finally {
    foreach ($p in @($client,$server)) { if ($p) { if (-not $p.HasExited) { $p.Kill(); $p.WaitForExit() }; $p.Dispose() } }
    if ($journal) { try { & $exe restore-usb $journal *> (Join-Path $output 'cleanup.txt') } catch { $report.Error="$($report.Error) Recovery cleanup: $_" } }
    $report.Phase='Finished'; Save
}
