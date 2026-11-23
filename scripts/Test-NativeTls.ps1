param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$workspace=Split-Path -Parent $project
$output=Join-Path $project 'artifacts\native-tls'
$pemDirectory=$output.Replace('\','/')
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe=Join-Path $project 'artifacts\recovery\win-x64\Vhui.Recovery.exe'
$clientExe=Join-Path $workspace 'analysis\client\recovered\vhui64.exe'
$sdk=Join-Path $project '.tools\dotnet\dotnet.exe'
$report=[ordered]@{Passed=$false;DirectTls=$false;MutualTls=$false;ReverseTls=$false;ReverseTcp=$false;CoreSha256=(Get-FileHash (Join-Path (Split-Path -Parent $exe) 'Vhui.Core.dll')).Hash;Error=$null}
$server=$null; $client=$null
function Save { $report | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $output 'result.json') }
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
    if (Get-Process vhui64,vhusbdwinw64 -ErrorAction SilentlyContinue) { throw 'A native client/server is already running; this test only closes its own processes.' }
    Save
    & $sdk (Join-Path $project 'tests\Vhui.Recovery.Tests\bin\Release\net10.0\Vhui.Recovery.Tests.dll') --write-tls-fixture $output
    if ($LASTEXITCODE -ne 0) { throw 'Certificate fixture generation failed.' }
    foreach ($mode in @('DirectTls','MutualTls','ReverseTls','ReverseTcp')) {
        $serverConfig=Join-Path $output ($mode+'-server.ini')
        $clientConfig=Join-Path $output ($mode+'-client.ini')
        $nativeText="[General]`r`nAutoFind=0`r`nAutoMinimize=1`r`nSSLCAFile=$pemDirectory/ca.pem`r`n"
        $serverText="NetworkInterface=127.0.0.1`r`nSSLPort=7574`r`nUseAVAHI=0`r`nSSLCert=$pemDirectory/server.pem`r`nAllowedDevices=145f/02e7`r`nLogFile=$output\$mode-server.log`r`n"
        if ($mode -in @('MutualTls','ReverseTls')) { $nativeText+="SSLClientCert=$pemDirectory/client.pem`r`n"; $serverText+="SSLUseClientCerts=1`r`nSSLCAFile=$pemDirectory/ca.pem`r`n" }
        if ($mode -eq 'ReverseTcp') {
            $nativeText+="ReverseLookup=0`r`n"
            $serverText="NetworkInterface=127.0.0.1`r`nTCPPort=17575`r`nUseAVAHI=0`r`nAllowedDevices=145f/02e7`r`nReverseClients=127.0.0.1:7573`r`nLogFile=$output\$mode-server.log`r`n"
        }
        elseif ($mode -eq 'ReverseTls') { $serverText+="SSLReverseClients=127.0.0.1:7572`r`nReverseClientsRetryPeriod=1`r`n" }
        else { $nativeText+="[Settings]`r`nManualHubs=127.0.0.1:7574`r`n" }
        [IO.File]::WriteAllText($clientConfig,$nativeText); [IO.File]::WriteAllText($serverConfig,$serverText)
        $client=Start-Process $clientExe -WorkingDirectory $output -WindowStyle Hidden -PassThru -ArgumentList @('-c',('"'+$clientConfig+'"'),('-l="'+$output+'\'+$mode+'-client.log"'))
        Start-Sleep -Seconds 1
        if ($mode -eq 'ReverseTls') { $null=Ipc 'SSLREVERSE' }
        if ($mode -eq 'ReverseTcp') { $null=Ipc 'REVERSE' }
        $server=Start-Process $exe -WorkingDirectory $output -WindowStyle Hidden -PassThru -ArgumentList @('serve',('"'+$serverConfig+'"'))
        for ($attempt=0;$attempt -lt 30;$attempt++) {
            Start-Sleep -Milliseconds 500
            if ($client.HasExited -or $server.HasExited) { throw ('A process exited in '+$mode) }
            [xml]$state=Ipc 'GET CLIENT STATE'
            if (@($state.state.server.device | Where-Object { $_.idVendor -eq '5215' -and $_.idProduct -eq '743' }).Count) {
                $report[$mode]=$true
                $state.Save((Join-Path $output ($mode+'-state.xml')))
                break
            }
        }
        if (-not $report[$mode]) { throw ('No device advertised over '+$mode) }
        Stop-Owned; Save
    }
    $report.Passed=$report.DirectTls -and $report.MutualTls -and $report.ReverseTls -and $report.ReverseTcp
} catch { $report.Error=$_.Exception.ToString() }
finally { Stop-Owned; Save }
