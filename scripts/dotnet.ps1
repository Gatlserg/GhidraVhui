# Выбирает SDK проекта, чтобы системный .NET SDK 2.1 не мешал сборке.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$localDotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (Test-Path -LiteralPath $localDotnet) {
    $dotnetExecutable = $localDotnet
} else {
    $command = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (-not $command) {
        throw 'Установите .NET SDK 10 или выполните .\Setup-DotNet.ps1.'
    }
    $dotnetExecutable = $command.Source
}
$sdkList = & $dotnetExecutable --list-sdks
if (-not ($sdkList -match '^10\.0\.')) {
    throw 'Нужен .NET SDK 10. Выполните .\Setup-DotNet.ps1.'
}
$env:DOTNET_ROOT = Split-Path -Parent $dotnetExecutable
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
Push-Location -LiteralPath $projectRoot
try {
    & $dotnetExecutable @args
    $resultCode = $LASTEXITCODE
} finally {
    Pop-Location
}
exit $resultCode
