param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$workspace=Split-Path -Parent $project
$output=Join-Path $project 'artifacts\service-discovery'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe=Join-Path $project 'artifacts\recovery\win-x64\Vhui.Recovery.exe'
$clientExe=Join-Path $workspace 'analysis\client\recovered\vhui64.exe'
$report=[ordered]@{Phase='Starting';Passed=$false;ServiceStarted=$false;ServiceProbed=$false;ServiceStopped=$false;ServiceRemoved=$false;MdnsRegistered=$false;NativeAutoFind=$false;Error=$null}
$report.CoreSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path (Split-Path -Parent $exe) 'Vhui.Core.dll')).Hash
$created=$false; $client=$null
function Save { $report | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $output 'result.json') }
function Stop-OwnedService {
    try { Stop-Service VhuiRecovery }
    catch {
        $state=Get-Service VhuiRecovery
        if ($state.Status -notin @([ServiceProcess.ServiceControllerStatus]::Stopped,[ServiceProcess.ServiceControllerStatus]::StopPending)) { throw }
    }
    (Get-Service VhuiRecovery).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Stopped,[TimeSpan]::FromSeconds(20))
}
function Command([string[]]$Arguments,[string]$Name) {
    & $exe @Arguments *> (Join-Path $output $Name)
    if ($LASTEXITCODE -ne 0) { throw ('Service command failed: '+(Get-Content -Raw -Encoding UTF8 (Join-Path $output $Name))) }
}
function Ipc([string]$Text,[string]$Name) {
    $path=Join-Path $output $Name
    $worker=Start-Process $clientExe -WindowStyle Hidden -PassThru -ArgumentList @('-t',('"'+$Text+'"'),'-r',('"'+$path+'"'))
    try { $null=$worker.Handle; if (-not $worker.WaitForExit(10000)) { $worker.Kill(); throw 'IPC timeout' } }
    finally { $worker.Dispose() }
    return $path
}
try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run elevated.' }
    if (Get-Service VhuiRecovery -ErrorAction SilentlyContinue) { throw 'An existing VhuiRecovery service is not owned by this test.' }
    if (Get-Process vhui64,vhusbdwinw64 -ErrorAction SilentlyContinue) { throw 'Native client/server already running.' }
    Save
    $config=Join-Path $output 'service.ini'
    $log=Join-Path $output 'service.log'
    [IO.File]::WriteAllText($config,"NetworkInterface=127.0.0.1`r`nTCPPort=17581`r`nUseAVAHI=0`r`nAllowedDevices=145f/02e7`r`nLogFile=$log`r`n")
    Command @('service-install',$config) 'install.txt'; $created=$true
    $report.Phase='Service'; Save
    Start-Service VhuiRecovery
    (Get-Service VhuiRecovery).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(15))
    $report.ServiceStarted=$true
    Start-Sleep -Seconds 1
    Command @('probe-server','127.0.0.1','17581') 'probe.json'
    $probe=Get-Content -Raw -Encoding UTF8 (Join-Path $output 'probe.json') | ConvertFrom-Json
    $report.ServiceProbed=$probe.Identity.ProtocolVersion -eq 8 -and @($probe.Messages | Where-Object { $_.Type -eq 16 }).Count -gt 0
    Stop-OwnedService
    $report.ServiceStopped=$true
    # Exercise DNS-SD using the same service process; no persistent firewall changes.
    [IO.File]::WriteAllText($config,"NetworkInterface=0.0.0.0`r`nTCPPort=7575`r`nUseAVAHI=1`r`nDNSFriendlyName=Vhui compatibility discovery`r`nAllowedDevices=145f/02e7`r`nLogFile=$log`r`n")
    Start-Service VhuiRecovery
    (Get-Service VhuiRecovery).WaitForStatus([ServiceProcess.ServiceControllerStatus]::Running,[TimeSpan]::FromSeconds(15))
    $report.Phase='Discovery'; Save
    $clientConfig=Join-Path $output 'client.ini'
    [IO.File]::WriteAllText($clientConfig,"[General]`r`nAutoFind=1`r`nAutoMinimize=1`r`n")
    $client=Start-Process $clientExe -WindowStyle Hidden -PassThru -WorkingDirectory $output `
        -ArgumentList @('-c',('"'+$clientConfig+'"'),('-l="'+(Join-Path $output 'client.log')+'"'))
    for ($attempt=0;$attempt -lt 30;$attempt++) {
        Start-Sleep -Milliseconds 500
        if ($client.HasExited) { throw 'Native client exited.' }
        [xml]$state=Get-Content -Raw -Encoding UTF8 (Ipc 'GET CLIENT STATE' 'state.xml')
        foreach ($server in @($state.state.server)) {
            if (@($server.device | Where-Object { $_.idVendor -eq '5215' -and $_.idProduct -eq '743' }).Count -gt 0) { $report.NativeAutoFind=$true; break }
        }
        if ($report.NativeAutoFind) { break }
    }
    $report.MdnsRegistered=Select-String -LiteralPath $log -Pattern '"Event":"MdnsCompleted","Status":0' -Quiet
    $report.Passed=$report.ServiceStarted -and $report.ServiceProbed -and $report.ServiceStopped -and $report.MdnsRegistered -and $report.NativeAutoFind
} catch { $report.Error=$_.Exception.ToString() }
finally {
    if ($client) {
        if (-not $client.HasExited) { try { Ipc 'EXIT' 'exit.txt' | Out-Null } catch { }; if (-not $client.WaitForExit(3000)) { $client.Kill() } }
        $client.Dispose()
    }
    if ($created) {
        try { Stop-OwnedService } catch { }
        try { Command @('service-uninstall') 'uninstall.txt'; $report.ServiceRemoved=$true } catch { $report.Error="$($report.Error) Cleanup: $_" }
    }
    $report.Passed=$report.Passed -and $report.ServiceRemoved
    $report.Phase='Finished'; Save
}
