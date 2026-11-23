param([int]$StreamSeconds=120,[switch]$SkipAudioOutput)
$ErrorActionPreference='Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Start one elevated PowerShell through normal Windows UAC, then run this script.'
}
$project=Split-Path -Parent $PSScriptRoot
& (Join-Path $project 'Build.ps1') -Publish
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
& (Join-Path $PSScriptRoot 'Test-ProtocolCompatibility.ps1')
& (Join-Path $PSScriptRoot 'Test-NativeTls.ps1')
& (Join-Path $PSScriptRoot 'Test-UsbClient.ps1') -HoldSeconds 3 -StreamSeconds $StreamSeconds -AudioProbe -ServerSeconds ($StreamSeconds+60)
if (-not $SkipAudioOutput) { & (Join-Path $PSScriptRoot 'Test-AudioOutputClient.ps1') }
& (Join-Path $PSScriptRoot 'Test-CrashRecovery.ps1')
& (Join-Path $PSScriptRoot 'Test-ServiceDiscovery.ps1')
$directories=@('protocol-compatibility','native-tls','usb-client-test','crash-recovery','service-discovery')
if (-not $SkipAudioOutput) { $directories+='audio-output-test' }
$failed=@()
foreach ($directory in $directories) {
    $result=Get-Content -Raw -Encoding UTF8 (Join-Path $project ('artifacts\'+$directory+'\result.json')) | ConvertFrom-Json
    if (-not $result.Passed) { $failed+=$directory }
}
if ($failed.Count) { throw ('Compatibility checks failed: '+($failed -join ', ')) }
Write-Output 'All requested compatibility checks passed.'
