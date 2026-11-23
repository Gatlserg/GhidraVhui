param([string]$Executable,[ValidateSet('ru','en')][string]$Language='ru',[string]$ResultDirectory,[switch]$NoSdkEnvironment)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$env:DOTNET_ROOT=Join-Path $project $(if ($NoSdkEnvironment) { 'artifacts\absent-dotnet-runtime' } else { '.tools\dotnet' })
if ($NoSdkEnvironment) { $env:DOTNET_ROOT_X64=$env:DOTNET_ROOT; $env:DOTNET_MULTILEVEL_LOOKUP='0' }
$exe=if ($Executable) { (Resolve-Path -LiteralPath $Executable).Path } else { Join-Path $project 'artifacts\Vhui.Server.exe' }
if (-not (Test-Path -LiteralPath $exe)) { throw 'Publish first: powershell -File dotnet/Build.ps1 -Publish' }
$output=if ($ResultDirectory) { [IO.Path]::GetFullPath($ResultDirectory) } else { Join-Path $project $(if ($Language -eq 'en') { 'artifacts\gui-smoke-en' } else { 'artifacts\gui-smoke' }) }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$config=Join-Path $output 'server.ini'
[IO.File]::WriteAllText($config,"ServerName=Vhui USB Server`r`nNetworkInterface=127.0.0.1`r`nTCPPort=17621`r`nUseAVAHI=0`r`nDesktopLanguage=$Language`r`n")
$resultPath=Join-Path $output 'result.json'
[IO.File]::WriteAllText($resultPath,'{"Passed":false,"Phase":"Starting"}')
$process=Start-Process $exe -PassThru -ArgumentList @('--config',('"'+$config+'"'),'--smoke-output',('"'+$output+'"'))
try {
    $null=$process.Handle
    if (-not $process.WaitForExit(30000)) {
        $process.Kill()
        $phase=Get-Content -LiteralPath (Join-Path $output 'phase.txt') -Raw -ErrorAction SilentlyContinue
        @{Passed=$false;Error='GUI test timed out.';Phase=$phase} | ConvertTo-Json | Set-Content -LiteralPath $resultPath -Encoding UTF8
        throw ('GUI test timed out: '+$phase)
    }
    $result=Get-Content -Raw -Encoding UTF8 (Join-Path $output 'result.json') | ConvertFrom-Json
    if (-not $result.Passed) { throw ($result | ConvertTo-Json) }
    $result | ConvertTo-Json
}
finally { $process.Dispose() }
