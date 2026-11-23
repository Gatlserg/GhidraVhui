param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot; $workspace=Split-Path -Parent $project
$output=Join-Path $project 'artifacts\audio-output-test'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe=Join-Path $project 'artifacts\recovery\win-x64\Vhui.Recovery.exe'
$clientExe=Join-Path $workspace 'analysis\client\recovered\vhui64.exe'
$server=$null; $client=$null; $address=$null
$report=[ordered]@{Phase='Starting';Passed=$false;Captured=$false;Released=$false;OutputCompleted=$false;IsoOutRequests=0;DriverRestored=$false;Error=$null}
$report.CoreSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path (Split-Path -Parent $exe) 'Vhui.Core.dll')).Hash
function Save { $report | ConvertTo-Json -Depth 6 | Set-Content -Encoding UTF8 (Join-Path $output 'result.json') }
function Ipc([string]$Text,[string]$Name) {
    $path=Join-Path $output $Name
    $p=Start-Process $clientExe -WindowStyle Hidden -PassThru -ArgumentList @('-t',('"'+$Text+'"'),'-r',('"'+$path+'"'))
    try { $null=$p.Handle; if (-not $p.WaitForExit(10000)) { $p.Kill(); throw 'IPC timeout' } } finally { $p.Dispose() }
    return $path
}
try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run elevated.' }
    if (Get-Process vhui64,vhusbdwinw64,Vhui.Recovery -ErrorAction SilentlyContinue) { throw 'Existing native/managed processes are not owned by this test.' }
    $before=@(Get-PnpDevice -Class MEDIA -PresentOnly | Where-Object { $_.InstanceId -like '*VID_4C4A&PID_4155*' })
    if (-not $before.Count) { throw 'The selected UACDemo USB audio device is not present.' }
    Save
    $server=Start-Process $exe -WindowStyle Hidden -PassThru -WorkingDirectory $output -ArgumentList @('serve-usb','4C4A:4155','17570','55') -RedirectStandardError (Join-Path $output 'server.log')
    for ($i=0;$i -lt 100;$i++) {
        if ($server.HasExited) { throw 'Server did not start.' }
        if ((Test-Path (Join-Path $output 'server.log')) -and (Select-String -LiteralPath (Join-Path $output 'server.log') -SimpleMatch 'Listening' -Quiet)) { break }
        Start-Sleep -Milliseconds 100
    }
    $config=Join-Path $output 'client.ini'
    [IO.File]::WriteAllText($config,"[General]`r`nAutoFind=0`r`nAutoMinimize=1`r`n[Settings]`r`nManualHubs=127.0.0.1:17570`r`n")
    $client=Start-Process $clientExe -WindowStyle Hidden -PassThru -WorkingDirectory $output -ArgumentList @('-c',('"'+$config+'"'))
    for ($i=0;$i -lt 30;$i++) {
        Start-Sleep -Milliseconds 300
        [xml]$state=Get-Content -Raw -Encoding UTF8 (Ipc 'GET CLIENT STATE' 'state.xml')
        $device=@($state.state.server.device | Where-Object { $_.idVendor -eq '19530' -and $_.idProduct -eq '16725' }) | Select-Object -First 1
        if ($device) { break }
    }
    if (-not $device) { throw 'Native client did not discover the USB audio device.' }
    $address=$state.state.server.connection.hostname+'.'+$device.address
    Ipc ('USE,'+$address) 'use.txt' | Out-Null
    for ($i=0;$i -lt 30;$i++) {
        Start-Sleep -Milliseconds 300
        $virtual=@(Get-PnpDevice -Class MEDIA -PresentOnly | Where-Object { $_.InstanceId -like '*VID_4C4A&PID_4155*' -and $_.Status -eq 'OK' -and $_.InstanceId -notin @($before.InstanceId) })
        if ($virtual.Count) { $report.Captured=$true; break }
    }
    if (-not $report.Captured) { throw 'Virtual audio device did not become ready.' }
    $report.Phase='WritingSilence'; Save
    $probe=Start-Process $exe -WindowStyle Hidden -PassThru -ArgumentList @('audio-output-probe','UACDemo') `
        -RedirectStandardOutput (Join-Path $output 'silence.json') -RedirectStandardError (Join-Path $output 'silence.stderr.txt')
    try {
        $null=$probe.Handle; if (-not $probe.WaitForExit(15000)) { $probe.Kill(); throw 'Audio output probe timed out.' }; $probe.Refresh()
        if ($probe.ExitCode -ne 0) { throw (Get-Content -Raw -Encoding UTF8 (Join-Path $output 'silence.stderr.txt')) }
        $report.Output=Get-Content -Raw -Encoding UTF8 (Join-Path $output 'silence.json') | ConvertFrom-Json
        $report.OutputCompleted=$report.Output.Completed
    } finally { $probe.Dispose() }
} catch { $report.Error=$_.Exception.ToString() }
finally {
    if ($client) {
        if (-not $client.HasExited) {
            if ($address) { try { Ipc ('STOP USING,'+$address) 'stop.txt' | Out-Null; $report.Released=$true } catch { } }
            try { Ipc 'EXIT' 'exit.txt' | Out-Null } catch { }; if (-not $client.WaitForExit(3000)) { $client.Kill() }
        }
        $client.Dispose()
    }
    if ($server) { if (-not $server.HasExited) { $server.WaitForExit(65000) | Out-Null }; if (-not $server.HasExited) { $server.Kill() }; $server.Dispose() }
    foreach ($line in Get-Content -Encoding UTF8 (Join-Path $output 'server.log') -ErrorAction SilentlyContinue) {
        try { $message=$line | ConvertFrom-Json } catch { continue }
        if ($message.Event -eq 'Received' -and $message.Type -in @(36,77) -and $null -ne $message.Endpoint -and $message.Endpoint -lt 128) { $report.IsoOutRequests++ }
    }
    $after=@(Get-PnpDevice -Class MEDIA -PresentOnly | Where-Object { $_.InstanceId -in @($before.InstanceId) -and $_.Status -eq 'OK' })
    $report.DriverRestored=$after.Count -gt 0
    $report.Passed=$report.Captured -and $report.Released -and $report.OutputCompleted -and $report.IsoOutRequests -gt 0 -and $report.DriverRestored -and -not $report.Error
    $report.Phase='Finished'; Save
}
