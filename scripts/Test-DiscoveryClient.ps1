param(
    [string]$NativeClientPath,
    [string]$UsbId = '145F:02E7',
    [int]$Port = 17575,
    [string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent $projectRoot
if (-not $NativeClientPath) { $NativeClientPath = Join-Path $workspaceRoot 'analysis\client\recovered\vhui64.exe' }
$NativeClientPath = (Resolve-Path -LiteralPath $NativeClientPath).Path
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts\discovery-test' }
if (Get-Process -Name vhui64 -ErrorAction SilentlyContinue) {
    throw 'Close the running VirtualHere client before this test: its IPC pipe is shared between instances.'
}
if ($Port -lt 1 -or $Port -gt 65535) { throw 'Invalid TCP port.' }
$recovery = Join-Path $projectRoot 'artifacts\recovery\win-x64\Vhui.Recovery.exe'
if (-not (Test-Path -LiteralPath $recovery)) { throw 'Run Build.ps1 -Publish first.' }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
$hardwarePath = Join-Path $OutputDirectory 'hardware.json'
& $recovery usb-inspect $UsbId | Set-Content -Encoding UTF8 -LiteralPath $hardwarePath
if ($LASTEXITCODE -ne 0) { throw 'USB inventory failed.' }
$hardware = Get-Content -Encoding UTF8 -Raw -LiteralPath $hardwarePath | ConvertFrom-Json
if ($hardware.Devices.Count -ne 1 -or $hardware.Errors.Count -ne 0) { throw 'Expected exactly one matching USB device and no query errors.' }
$device = $hardware.Devices[0]
$server = $null
$client = $null

function Invoke-ClientIpc([string]$Command, [string]$OutputName) {
    $resultPath = Join-Path $OutputDirectory $OutputName
    $arguments = @('-t', ('"' + $Command + '"'), '-r', ('"' + $resultPath + '"'))
    $ipc = Start-Process -FilePath $NativeClientPath -ArgumentList $arguments -WorkingDirectory $OutputDirectory -WindowStyle Hidden -PassThru
    try {
        if (-not $ipc.WaitForExit(8000)) { $ipc.Kill(); throw 'VirtualHere IPC timeout.' }
        if ($ipc.ExitCode -ne 0) { throw ('IPC failed: ' + $Command + ', exit ' + $ipc.ExitCode) }
    }
    finally { $ipc.Dispose() }
    return $resultPath
}

try {
    $serverLog = Join-Path $OutputDirectory 'server.jsonl'
    $server = Start-Process -FilePath $recovery -ArgumentList @('serve-discovery', $UsbId, "$Port", '60') `
        -WorkingDirectory $OutputDirectory -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $OutputDirectory 'server.stdout.txt') -RedirectStandardError $serverLog
    $ready = $false
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($server.HasExited) { throw ('Discovery server exited: ' + (Get-Content -Raw -LiteralPath $serverLog)) }
        if ((Test-Path -LiteralPath $serverLog) -and (Select-String -LiteralPath $serverLog -SimpleMatch 'Listening' -Quiet)) { $ready = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) { throw 'Discovery server startup timeout.' }
    # A bad connection must not terminate the listener used by the real client.
    $malformed = New-Object Net.Sockets.TcpClient
    try {
        $malformed.Connect('127.0.0.1', $Port)
        $malformed.GetStream().Write((New-Object byte[] 8), 0, 8)
    }
    finally { $malformed.Dispose() }
    $rejected = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        if (Select-String -LiteralPath $serverLog -SimpleMatch 'ConnectionEnded' -Quiet) { $rejected = $true; break }
        Start-Sleep -Milliseconds 20
    }
    if (-not $rejected -or $server.HasExited) { throw 'Malformed frame was not isolated to its connection.' }
    $config = Join-Path $OutputDirectory 'client.ini'
    [IO.File]::WriteAllText($config, "[General]`r`nAutoFind=0`r`nAutoMinimize=1`r`n[Settings]`r`nManualHubs=127.0.0.1:$Port`r`n")
    $client = Start-Process -FilePath $NativeClientPath `
        -ArgumentList @('-c', ('"' + $config + '"'), ('-l="' + (Join-Path $OutputDirectory 'client.log') + '"')) `
        -WorkingDirectory $OutputDirectory -WindowStyle Hidden -PassThru
    for ($attempt = 0; $attempt -lt 50; $attempt++) {
        if ($client.HasExited) { throw 'Native client exited before the protocol test.' }
        if (Select-String -LiteralPath $serverLog -SimpleMatch '"Type":16' -Quiet) { break }
        Start-Sleep -Milliseconds 100
    }
    $keepAlive = $false
    for ($attempt = 0; $attempt -lt 70; $attempt++) {
        if (Select-String -LiteralPath $serverLog -SimpleMatch '"Type":15' -Quiet) { $keepAlive = $true; break }
        Start-Sleep -Milliseconds 100
    }
    if (-not $keepAlive -or $client.HasExited -or $server.HasExited) { throw 'Native client did not acknowledge the server keep-alive.' }
    $statePath = Invoke-ClientIpc 'GET CLIENT STATE' 'client-state.xml'
    $listPath = Invoke-ClientIpc 'LIST' 'client-list.txt'
    [xml]$state = Get-Content -Encoding UTF8 -Raw -LiteralPath $statePath
    $reported = @($state.state.server.device) | Where-Object { $_.idVendor -eq "$($device.VendorId)" -and $_.idProduct -eq "$($device.ProductId)" }
    if (-not $reported -or $reported.vendor -ne $device.Manufacturer -or $reported.product -ne $device.Product `
        -or $reported.deviceSerial -ne $device.SerialNumber) { throw 'Native client device identity differs from the physical USB descriptors.' }
    $report = [ordered]@{
        Passed = $true
        TimestampUtc = [DateTime]::UtcNow.ToString('o')
        NativeClientVersion = (Get-Item -LiteralPath $NativeClientPath).VersionInfo.ProductVersion
        NativeClientSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $NativeClientPath).Hash
        CSharpExeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $recovery).Hash
        RecoveryDllSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path (Split-Path -Parent $recovery) 'Vhui.Recovery.dll')).Hash
        CoreDllSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path (Split-Path -Parent $recovery) 'Vhui.Core.dll')).Hash
        UsbId = $UsbId
        Product = $reported.product
        Manufacturer = $reported.vendor
        SerialNumber = $reported.deviceSerial
        FirstInterfaceClass = [int]$reported.firstInterfaceClass
        InterfaceCount = [int]$reported.numInterfacesInFirstConfiguration
        Mode = 'DiscoveryOnly'
        MalformedFrameRejected = $rejected
        KeepAliveConfirmed = $keepAlive
        VideoTransferTested = $false
    }
    $report | ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $OutputDirectory 'result.json')
    $report | ConvertTo-Json
    Get-Content -Encoding UTF8 -LiteralPath $listPath
}
finally {
    if ($client) {
        if (-not $client.HasExited) {
            try { Invoke-ClientIpc 'EXIT' 'client-exit.txt' | Out-Null } catch { Write-Warning $_ }
            if (-not $client.WaitForExit(3000)) { $client.Kill() }
        }
        $client.Dispose()
    }
    if ($server) {
        if (-not $server.HasExited) { $server.Kill(); $server.WaitForExit(3000) | Out-Null }
        $server.Dispose()
    }
}
