param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $projectRoot
$output = Join-Path $projectRoot 'artifacts\native-capture'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$clientPath = Join-Path $workspaceRoot 'analysis\client\recovered\vhui64.exe'
$serverPath = Join-Path $workspaceRoot 'analysis\recovered\vhusbdwinw64.exe'
$recovery = Join-Path $projectRoot 'artifacts\recovery\win-x64\Vhui.Recovery.exe'
$server = $null; $client = $null; $proxy = $null; $deviceAddress = $null
$report = [ordered]@{ Phase='Starting'; CaptureRequested=$false; CaptureState=$null; Released=$false; Error=$null }
function Save-Report { $report | ConvertTo-Json -Depth 5 | Set-Content -Encoding UTF8 (Join-Path $output 'result.json') }
function Ipc([string]$Command, [string]$Name) {
    $resultPath = Join-Path $output $Name
    $worker = Start-Process $clientPath -ArgumentList @('-t', ('"'+$Command+'"'), '-r', ('"'+$resultPath+'"')) -WindowStyle Hidden -PassThru
    try { if (-not $worker.WaitForExit(10000)) { $worker.Kill(); throw 'IPC timeout' }; return $resultPath }
    finally { $worker.Dispose() }
}
try {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run through UAC.' }
    if (Get-Process vhui64,vhusbdwinw64 -ErrorAction SilentlyContinue) { throw 'Close existing native client/server first.' }
    $config = Join-Path $output 'server.ini'
    # Default TCP port/interface are required by the native trial; do not alter its license.
    [IO.File]::WriteAllText($config, "ServerName=Vhui native capture baseline`r`n")
    $server = Start-Process $serverPath -ArgumentList @('-n','-c',('"'+$config+'"'),'-r',('"'+(Join-Path $output 'server.log')+'"')) -WorkingDirectory $output -WindowStyle Hidden -PassThru
    $proxy = Start-Process $recovery -ArgumentList @('trace-proxy','17570','127.0.0.1','7575',('"'+(Join-Path $output 'trace')+'"'),'120') -WorkingDirectory $output -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $output 'proxy.log')
    $clientConfig = Join-Path $output 'client.ini'
    [IO.File]::WriteAllText($clientConfig, "[General]`r`nAutoFind=0`r`nAutoMinimize=1`r`n[Settings]`r`nManualHubs=127.0.0.1:17570`r`n")
    $client = Start-Process $clientPath -ArgumentList @('-c',('"'+$clientConfig+'"'),('-l="'+(Join-Path $output 'client.log')+'"')) -WorkingDirectory $output -WindowStyle Hidden -PassThru
    $report.Phase='WaitingForCamera'; Save-Report
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 500
        [xml]$state = Get-Content -Raw -Encoding UTF8 (Ipc 'GET CLIENT STATE' 'before.xml')
        $device = @($state.state.server.device) | Where-Object { $_.idVendor -eq '5215' -and $_.idProduct -eq '743' } | Select-Object -First 1
        if ($device) { break }
        if ($server.HasExited -or $client.HasExited -or $proxy.HasExited) { throw 'A test process exited.' }
    }
    if (-not $device) { throw 'Camera was not enumerated by the native server.' }
    $connection = @($state.state.server.connection) | Where-Object { $_.connectionId -eq $device.connectionId } | Select-Object -First 1
    $deviceAddress = $connection.hostname + '.' + $device.address
    $report.Phase='Capturing'; $report.CaptureRequested=$true; Save-Report
    Ipc ('USE,'+$deviceAddress) 'use.txt' | Out-Null
    for ($attempt=0; $attempt -lt 30; $attempt++) {
        Start-Sleep -Milliseconds 500
        [xml]$captured = Get-Content -Raw -Encoding UTF8 (Ipc 'GET CLIENT STATE' 'captured.xml')
        $actual = @($captured.state.server.device) | Where-Object { $_.idVendor -eq '5215' -and $_.idProduct -eq '743' } | Select-Object -First 1
        $report.CaptureState = $actual.state
        Save-Report
        if ($actual.state -eq '3') { break }
    }
    $report.Phase='Releasing'; Save-Report
}
catch { $report.Error=$_.Exception.Message }
finally {
    if ($client -and -not $client.HasExited) {
        if ($deviceAddress) {
            try {
                Ipc ('STOP USING,'+$deviceAddress) 'stop.txt' | Out-Null
                Start-Sleep -Milliseconds 1500
                Ipc 'GET CLIENT STATE' 'after.xml' | Out-Null
                $report.Released=$true
            } catch { $report.Error = "$($report.Error) Release: $($_.Exception.Message)" }
        }
        try { Ipc 'EXIT' 'exit.txt' | Out-Null } catch { }
        if (-not $client.WaitForExit(3000)) { $client.Kill() }
    }
    if ($proxy -and -not $proxy.HasExited) { $proxy.Kill() }
    if ($server -and -not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    $report.Phase='Finished'; Save-Report
}
