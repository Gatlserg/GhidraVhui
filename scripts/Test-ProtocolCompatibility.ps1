param([switch]$ReuseNative)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$workspace = Split-Path -Parent $project
$output = Join-Path $project 'artifacts\protocol-compatibility'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $project 'artifacts\recovery\win-x64\Vhui.Recovery.exe'
$nativeExe = Join-Path $workspace 'analysis\recovered\vhusbdwinw64.exe'
$report = [ordered]@{ Phase='Starting'; Passed=$false; Error=$null; Native=$false; Managed=$false; Comparisons=@(); CameraRestored=$false }
$report.ManagedCoreSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path (Split-Path -Parent $exe) 'Vhui.Core.dll')).Hash
$report.NativeSha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $nativeExe).Hash
function Save { $report | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 (Join-Path $output 'result.json') }
function Wait-Port([int]$Port, $Process) {
    for ($attempt=0; $attempt -lt 100; $attempt++) {
        if ($Process.HasExited) { throw 'Server exited before listening.' }
        $socket = New-Object Net.Sockets.TcpClient
        try { $socket.Connect('127.0.0.1',$Port); return } catch { Start-Sleep -Milliseconds 100 } finally { $socket.Dispose() }
    }
    throw 'Server did not listen.'
}
function Probe([int]$Port, [string]$Name) {
    $worker=Start-Process $exe -WindowStyle Hidden -PassThru -ArgumentList @('usb-check','127.0.0.1',"$Port",'145F:02E7') `
        -RedirectStandardOutput (Join-Path $output ($Name+'.json')) -RedirectStandardError (Join-Path $output ($Name+'.stderr.txt'))
    try {
        $null=$worker.Handle
        if (-not $worker.WaitForExit(120000)) { $worker.Kill(); throw 'USB probe timed out.' }
        $worker.Refresh()
        if ($worker.ExitCode -ne 0) { throw ('USB probe failed: '+(Get-Content -Raw -Encoding UTF8 (Join-Path $output ($Name+'.stderr.txt')))) }
    } finally { $worker.Dispose() }
}
$native=$null; $managed=$null
try {
    if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run elevated through normal UAC.' }
    if (Get-Process vhui64,vhusbdwinw64,Vhui.Recovery -ErrorAction SilentlyContinue) { throw 'Existing server/client instances must finish first.' }
    Save
    $config=Join-Path $output 'native.ini'
    [IO.File]::WriteAllText($config,"ServerName=Compatibility baseline`r`nUseAVAHI=0`r`nEasyFind=0`r`n")
    if (-not $ReuseNative) {
    $native=Start-Process $nativeExe -WindowStyle Hidden -PassThru -WorkingDirectory $output `
        -ArgumentList @('-n','-c',('"'+$config+'"'),'-r',('"'+(Join-Path $output 'native.log')+'"'))
    Wait-Port 7575 $native
    $report.Phase='Native'; Save; Probe 7575 'native'; $report.Native=$true
    if (-not $native.HasExited) { $native.CloseMainWindow() | Out-Null; if (-not $native.WaitForExit(3000)) { $native.Kill(); $native.WaitForExit() } }
    } else {
        $baseline=Get-Content -Raw -Encoding UTF8 (Join-Path $output 'native.json') | ConvertFrom-Json
        if (-not $baseline.Captured -or -not $baseline.Released) { throw 'Saved native baseline is incomplete.' }
        $report.Native=$true
    }
    Start-Sleep -Seconds 2
    $managed=Start-Process $exe -WindowStyle Hidden -PassThru -WorkingDirectory $output `
        -ArgumentList @('serve-usb','145F:02E7','17570','40') -RedirectStandardError (Join-Path $output 'managed.log')
    Wait-Port 17570 $managed
    $report.Phase='Managed'; Save; Probe 17570 'managed'; $report.Managed=$true
    $baseline=Get-Content -Raw -Encoding UTF8 (Join-Path $output 'native.json') | ConvertFrom-Json
    $clone=Get-Content -Raw -Encoding UTF8 (Join-Path $output 'managed.json') | ConvertFrom-Json
    foreach ($sample in $baseline.Exchanges) {
        if ($sample.Name -in @('server','capture')) { continue }
        $other=@($clone.Exchanges | Where-Object { $_.Name -eq $sample.Name }) | Select-Object -First 1
        $report.Comparisons += [ordered]@{ Name=$sample.Name; Same=($other -and $sample.ResponseType -eq $other.ResponseType -and $sample.PayloadBase64 -eq $other.PayloadBase64); Native=$sample.PayloadBase64; Managed=$other.PayloadBase64 }
    }
    if (-not $managed.WaitForExit(55000)) { throw 'Managed server did not stop by its duration.' }
    $report.CameraRestored=@(Get-PnpDevice -Class Camera -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.InstanceId -like '*VID_145F&PID_02E7*' -and $_.Status -eq 'OK' }).Count -gt 0
    $report.Passed=$baseline.Captured -and $baseline.Released -and $clone.Captured -and $clone.Released -and $report.CameraRestored `
        -and @($report.Comparisons | Where-Object { -not $_.Same }).Count -eq 0
} catch { $report.Error=$_.Exception.ToString() }
finally {
    foreach ($owned in @($managed,$native)) { if ($owned) { if (-not $owned.HasExited) { $owned.Kill(); $owned.WaitForExit() }; $owned.Dispose() } }
    $report.Phase='Finished'; Save
}
