param([switch]$Publish)
$ErrorActionPreference = 'Stop'
$dotnetScript = Join-Path $PSScriptRoot 'scripts\dotnet.ps1'
& $dotnetScript build (Join-Path $PSScriptRoot 'Vhui.sln') --configuration Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$testArguments = @('run', '--project', (Join-Path $PSScriptRoot 'tests\Vhui.Recovery.Tests\Vhui.Recovery.Tests.csproj'), '--configuration', 'Release', '--no-build')
$packedPath = Join-Path (Split-Path -Parent $PSScriptRoot) 'analysis\recovered\vhusbdwinw64.exe'
if (Test-Path -LiteralPath $packedPath) { $testArguments += @('--', '--packed', $packedPath) }
& $dotnetScript @testArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if ($Publish) {
    & $dotnetScript publish (Join-Path $PSScriptRoot 'src\Vhui.Recovery\Vhui.Recovery.csproj') `
        --configuration Release --runtime win-x64 --self-contained true `
        --output (Join-Path $PSScriptRoot 'artifacts\recovery\win-x64')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $dotnetScript publish (Join-Path $PSScriptRoot 'src\Vhui.Server\Vhui.Server.csproj') `
        --configuration Release --runtime win-x64 --self-contained true `
        -p:DebugType=embedded --output (Join-Path $PSScriptRoot 'artifacts\server\single-file\win-x64')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'artifacts\server\single-file\win-x64\Vhui.Server.exe') `
        -Destination (Join-Path $PSScriptRoot 'artifacts\Vhui.Server.exe') -Force
}
