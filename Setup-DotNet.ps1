$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$toolsDirectory = Join-Path $PSScriptRoot '.tools'
New-Item -ItemType Directory -Force -Path $toolsDirectory | Out-Null
$installerPath = Join-Path $toolsDirectory 'dotnet-install.ps1'
Invoke-WebRequest -UseBasicParsing -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $installerPath
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installerPath -Channel 10.0 `
    -InstallDir (Join-Path $toolsDirectory 'dotnet') -NoPath
if ($LASTEXITCODE -ne 0) { throw 'Не удалось установить .NET SDK.' }
