param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $projectRoot
$output = Join-Path $projectRoot 'artifacts\native-elevated-test'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$serverPath = Join-Path $workspaceRoot 'analysis\recovered\vhusbdwinw64.exe'
$recovery = Join-Path $projectRoot 'artifacts\recovery\win-x64\Vhui.Recovery.exe'
$config = Join-Path $output 'server.ini'
$log = Join-Path $output 'server.log'
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
$report = [ordered]@{
    Elevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    StartedUtc = [DateTime]::UtcNow.ToString('o')
    NativeServerPid = $null
    ProbePassed = $false
    Error = $null
    Stopped = $false
}
$server = $null
try {
    if (-not $report.Elevated) { throw 'This test requires an elevated Windows process.' }
    # Bind only to loopback; do not issue capture/USE commands or install a service.
    [IO.File]::WriteAllText($config, "NetworkInterface=127.0.0.1`r`nTCPPort=17575`r`nServerName=Vhui native protocol baseline`r`n")
    $server = Start-Process -FilePath $serverPath -ArgumentList @('-n', '-c', ('"' + $config + '"'), '-r', ('"' + $log + '"')) `
        -WorkingDirectory $output -WindowStyle Hidden -PassThru
    $report.NativeServerPid = $server.Id
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($server.HasExited) { throw ('Native server exited with code ' + $server.ExitCode) }
        $socket = New-Object Net.Sockets.TcpClient
        try {
            $connect = $socket.ConnectAsync('127.0.0.1', 17575)
            if ($connect.Wait(200) -and $socket.Connected) { $ready = $true }
        }
        catch { }
        finally { $socket.Dispose() }
        if ($ready) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'Native server did not start listening.' }
    & $recovery probe-server 127.0.0.1 17575 2> (Join-Path $output 'probe.stderr.txt') |
        Set-Content -Encoding UTF8 -LiteralPath (Join-Path $output 'probe.json')
    if ($LASTEXITCODE -ne 0) { throw ('Protocol probe failed with code ' + $LASTEXITCODE) }
    $report.ProbePassed = $true
}
catch { $report.Error = $_.Exception.Message }
finally {
    if ($server) {
        if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
        $report.Stopped = $true
        $server.Dispose()
    }
    $report | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $output 'result.json')
}
if ($report.ProbePassed) { exit 0 }
exit 1
