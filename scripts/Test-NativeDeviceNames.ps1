param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$workspace=Split-Path -Parent $project
$output=Join-Path $project 'artifacts\native-device-names'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe=Join-Path $project 'artifacts\recovery\win-x64\Vhui.Recovery.exe'
$clientExe=Join-Path $workspace 'analysis\client\recovered\vhui64.exe'
$report=[ordered]@{Passed=$false;FirstSession=$false;AfterRestart=$false;Error=$null;CoreSha256=(Get-FileHash (Join-Path (Split-Path -Parent $exe) 'Vhui.Core.dll')).Hash}
$server=$null; $client=$null
function Save { $report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8 }
function Ipc([string]$Text) {
    $path=Join-Path $output 'ipc.txt'
    $worker=Start-Process $clientExe -WindowStyle Hidden -PassThru -ArgumentList @('-t',('"'+$Text+'"'),'-r',('"'+$path+'"'))
    try { $null=$worker.Handle; if (-not $worker.WaitForExit(10000)) { $worker.Kill(); throw 'Native IPC timeout.' } }
    finally { $worker.Dispose() }
    return [IO.File]::ReadAllText($path)
}
function Stop-Owned {
    if ($script:client) {
        if (-not $script:client.HasExited) { try { $null=Ipc 'EXIT' } catch { }; if (-not $script:client.WaitForExit(3000)) { $script:client.Kill() } }
        $script:client.Dispose(); $script:client=$null
    }
    if ($script:server) {
        if (-not $script:server.HasExited) { $script:server.Kill(); $script:server.WaitForExit() }
        $script:server.Dispose(); $script:server=$null
    }
}
try {
    if (Get-Process vhui64,vhusbdwinw64 -ErrorAction SilentlyContinue) { throw 'Existing native processes are not owned by this test.' }
    Save
    $inventory=& $exe usb-inspect 145F:02E7 | ConvertFrom-Json
    $device=@($inventory.Devices) | Select-Object -First 1
    if (-not $device) { throw 'Test webcam is not present.' }
    $alias='Studio Camera '+[char]0x2014+' '+[char]0x041a+[char]0x0430+[char]0x043c+[char]0x0435+[char]0x0440+[char]0x0430
    $aliases=@(@{Vendor=0x145f;Product=0x02e7;Serial=$device.SerialNumber;Location=($device.HubPath+'#'+$device.Port);Name=$alias}) | ConvertTo-Json -Compress
    if (-not $aliases.StartsWith('[')) { $aliases='['+$aliases+']' }
    $serverConfig=Join-Path $output 'server.ini'
    [IO.File]::WriteAllText($serverConfig,"NetworkInterface=127.0.0.1`r`nTCPPort=17625`r`nUseAVAHI=0`r`nAllowedDevices=145f/02e7`r`nDeviceAliases=$aliases`r`n")
    foreach ($session in @('FirstSession','AfterRestart')) {
        $server=Start-Process $exe -WindowStyle Hidden -PassThru -ArgumentList @('serve',('"'+$serverConfig+'"')) -RedirectStandardError (Join-Path $output ($session+'-server.log'))
        $config=Join-Path $output ($session+'-client.ini')
        [IO.File]::WriteAllText($config,"[General]`r`nAutoFind=0`r`nAutoMinimize=1`r`n[Settings]`r`nManualHubs=127.0.0.1:17625`r`n")
        $client=Start-Process $clientExe -WindowStyle Hidden -PassThru -ArgumentList @('-c',('"'+$config+'"'),('-l="'+$output+'\'+$session+'-client.log"'))
        for ($i=0;$i -lt 30;$i++) {
            Start-Sleep -Milliseconds 400
            if ($client.HasExited -or $server.HasExited) { throw 'A metadata test process exited.' }
            [xml]$state=Ipc 'GET CLIENT STATE'
            $remote=@($state.state.server.device | Where-Object { $_.idVendor -eq '5215' -and $_.idProduct -eq '743' }) | Select-Object -First 1
            if ($remote -and $remote.product -eq $alias) { $report[$session]=$true; $state.Save((Join-Path $output ($session+'.xml'))); break }
        }
        if (-not $report[$session]) { throw ('Native client did not display the alias in '+$session) }
        Stop-Owned; Save
    }
    $report.Passed=$report.FirstSession -and $report.AfterRestart
} catch { $report.Error=$_.Exception.ToString() }
finally { Stop-Owned; Save }
if (-not $report.Passed) { exit 1 }
