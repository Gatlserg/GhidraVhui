param()
$ErrorActionPreference='Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    # Normal Windows elevation through the desktop shell. Start-Process -Verb RunAs
    # did not display a prompt when invoked from the IDE agent on this machine.
    # https://learn.microsoft.com/windows/win32/shell/shell-shellexecute
    $shell=New-Object -ComObject Shell.Application
    try {
        $powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$PSCommandPath+'"'
        $shell.ShellExecute($powershell,$arguments,$PSScriptRoot,'runas',0)
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
    Write-Output 'Elevation requested through Windows Shell. Test status: dotnet/artifacts/republish-client/result.json'
    return
}
$project=Split-Path -Parent $PSScriptRoot
$workspace=Split-Path -Parent $project
$output=Join-Path $project 'artifacts\republish-client'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$clientExe=Join-Path $workspace 'analysis\client\recovered\vhui64.exe'
$recovery=Join-Path $project 'artifacts\recovery\win-x64\Vhui.Recovery.exe'
$sdk=Join-Path $project '.tools\dotnet\dotnet.exe'
$fixture=Join-Path $project 'tests\Vhui.Recovery.Tests\bin\Release\net10.0\Vhui.Recovery.Tests.dll'
$server=$null; $client=$null
$report=[ordered]@{Passed=$false;Phase='Starting';Cycles=@();DriverRestored=$false;Error=$null;CoreSha256=(Get-FileHash (Join-Path (Split-Path -Parent $fixture) 'Vhui.Core.dll')).Hash}
function Save { $report | ConvertTo-Json -Depth 7 | Set-Content -Encoding UTF8 (Join-Path $output 'result.json') }
function Ipc([string]$Text) {
    $path=Join-Path $output 'ipc.txt'
    $worker=Start-Process $clientExe -WindowStyle Hidden -PassThru -ArgumentList @('-t',('"'+$Text+'"'),'-r',('"'+$path+'"'))
    try { $null=$worker.Handle; if (-not $worker.WaitForExit(10000)) { $worker.Kill(); throw 'Native IPC timeout.' } }
    finally { $worker.Dispose() }
    return [IO.File]::ReadAllText($path)
}
function State { [xml]$xml=Ipc 'GET CLIENT STATE'; return $xml }
function Camera($state) { return @($state.state.server.device | Where-Object { $_.idVendor -eq '5215' -and $_.idProduct -eq '743' }) | Select-Object -First 1 }
function Is-Virtual([string]$Id) {
    for ($depth=0;$depth -lt 20 -and $Id;$depth++) {
        $service=(Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Enum\'+$Id) -Name Service -ErrorAction SilentlyContinue).Service
        if ($service -like 'vhusb*') { return $true }
        $Id=(Get-PnpDeviceProperty -InstanceId $Id -KeyName DEVPKEY_Device_Parent -ErrorAction SilentlyContinue).Data
    }
    return $false
}
try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run elevated through normal Windows UAC.' }
    if (Get-Process vhui64,vhusbdwinw64 -ErrorAction SilentlyContinue) { throw 'Existing native processes are not owned by this test.' }
    $before=@(Get-PnpDevice -Class Camera -PresentOnly | Where-Object { $_.InstanceId -like '*VID_145F&PID_02E7*' -and -not (Is-Virtual $_.InstanceId) })
    if (-not $before.Count) { throw 'Test webcam 145F:02E7 is not present.' }
    Save
    Remove-Item -LiteralPath (Join-Path $output 'stop') -Force -ErrorAction SilentlyContinue
    [IO.File]::WriteAllText((Join-Path $output 'shared'),'1')
    $server=Start-Process $sdk -WindowStyle Hidden -PassThru -ArgumentList @(('"'+$fixture+'"'),'--republish-fixture',('"'+$output+'"')) -RedirectStandardError (Join-Path $output 'server.log')
    $null=$server.Handle
    $config=Join-Path $output 'client.ini'
    [IO.File]::WriteAllText($config,"[General]`r`nAutoFind=0`r`nAutoMinimize=1`r`n[Settings]`r`nManualHubs=127.0.0.1:17623`r`n")
    $client=Start-Process $clientExe -WindowStyle Hidden -PassThru -ArgumentList @('-c',('"'+$config+'"'),('-l="'+$output+'\client.log"'))
    for ($cycle=1;$cycle -le 3;$cycle++) {
        $report.Phase='Capture '+$cycle; Save
        $device=$null
        for ($i=0;$i -lt 45;$i++) { Start-Sleep -Milliseconds 300; $state=State; $device=Camera $state; if ($device -and $device.state -eq '1') { break }; if ($server.HasExited) { throw 'Fixture server exited.' } }
        if (-not $device -or $device.state -ne '1') { throw 'Webcam was not republished.' }
        $address=$state.state.server.connection.hostname+'.'+$device.address
        $state.Save((Join-Path $output ('before-'+$cycle+'.xml')))
        $useResult=Ipc ('USE,'+$address)
        [IO.File]::WriteAllText((Join-Path $output ('use-'+$cycle+'.txt')),$useResult)
        # IPC can time out before driver binding completes.
        $captured=$false
        for ($i=0;$i -lt 60;$i++) { Start-Sleep -Milliseconds 300; $state=State; $device=Camera $state; if ($device -and $device.state -eq '3') { $captured=$true; break }; if ($server.HasExited) { throw 'Fixture exited during capture.' } }
        $state.Save((Join-Path $output ('captured-'+$cycle+'.xml')))
        if (-not $captured) { throw ('Capture did not complete: '+$useResult) }
        for ($i=0;$i -lt 45;$i++) {
            Start-Sleep -Milliseconds 300
            $virtual=@(Get-PnpDevice -Class Camera -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like '*VID_145F&PID_02E7*' -and $_.InstanceId -notin @($before.InstanceId) -and $_.Status -eq 'OK' -and (Is-Virtual $_.InstanceId) })
            if ($virtual.Count) { break }
        }
        if (-not $virtual.Count) { throw 'Virtual camera was not ready.' }
        Start-Sleep -Milliseconds 500
        $probe=Start-Process $recovery -WindowStyle Hidden -PassThru -ArgumentList @('camera-probe','Trust') -RedirectStandardOutput (Join-Path $output ('frame-'+$cycle+'.json')) -RedirectStandardError (Join-Path $output ('frame-'+$cycle+'.err'))
        try { $null=$probe.Handle; if (-not $probe.WaitForExit(18000)) { $probe.Kill(); throw 'Frame read timed out.' }; $probe.Refresh(); if ($probe.ExitCode -ne 0) { throw 'Virtual frame read failed.' } }
        finally { $probe.Dispose() }
        $frame=Get-Content -Raw -Encoding UTF8 (Join-Path $output ('frame-'+$cycle+'.json')) | ConvertFrom-Json
        $fromVirtual=@($virtual | Where-Object { $frame.SymbolicLink.IndexOf($_.InstanceId.Replace('\','#'),[StringComparison]::OrdinalIgnoreCase) -ge 0 }).Count -gt 0
        if (-not $fromVirtual) { throw 'Frame was not from the virtual webcam.' }
        $report.Cycles+=@{Cycle=$cycle;Bytes=$frame.Bytes;FrameFromVirtual=$fromVirtual}
        $report.Phase='Unshare '+$cycle; Save
        Remove-Item -LiteralPath (Join-Path $output 'shared') -Force
        for ($i=0;$i -lt 50;$i++) { Start-Sleep -Milliseconds 200; $device=Camera (State); if (-not $device -or $device.state -eq '0') { break } }
        if ($device -and $device.state -ne '0') { throw 'Unshare did not remove the remote device.' }
        for ($i=0;$i -lt 45;$i++) { Start-Sleep -Milliseconds 200; $physical=@(Get-PnpDevice -Class Camera -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -in @($before.InstanceId) -and $_.Status -eq 'OK' }); if ($physical.Count) { break } }
        if (-not $physical.Count) { throw 'Physical driver did not recover after unshare.' }
        # Windows reports Camera=OK before all child-install notifications settle.
        Start-Sleep -Seconds 3
        [IO.File]::WriteAllText((Join-Path $output 'shared'),'1')
    }
    $report.Passed=$report.Cycles.Count -eq 3
} catch { $report.Error=$_.Exception.ToString() }
finally {
    if ($client) { if (-not $client.HasExited) { try { $null=Ipc 'EXIT' } catch { }; if (-not $client.WaitForExit(4000)) { $client.Kill() } }; $client.Dispose() }
    if ($server) { [IO.File]::WriteAllText((Join-Path $output 'stop'),'1'); if (-not $server.WaitForExit(30000)) { $server.Kill(); $report.Error+=' Fixture shutdown timed out.' }; $server.Refresh(); $report.FixtureExitCode=$server.ExitCode; $server.Dispose() }
    if ($before) { $after=@(Get-PnpDevice -Class Camera -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -in @($before.InstanceId) -and $_.Status -eq 'OK' }); $report.DriverRestored=$after.Count -gt 0 }
    $report.Passed=$report.Passed -and $report.DriverRestored
    $report.Phase='Finished'; Save
}
