param([string]$Executable)
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
$exe=if ($Executable) { (Resolve-Path -LiteralPath $Executable).Path } else { Join-Path $project 'artifacts\Vhui.Server.exe' }
$output=Join-Path $project 'artifacts\single-file-test'
$app=Join-Path $output ('Single file app '+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $app -Force | Out-Null
$copy=Join-Path $app 'Vhui.Server.exe'
Copy-Item -LiteralPath $exe -Destination $copy
$report=[ordered]@{Passed=$false;SingleExecutableOnly=$false;SdkRootDisabled=$true;ExecutableSha256=(Get-FileHash -LiteralPath $copy).Hash;Bytes=(Get-Item -LiteralPath $copy).Length;Languages=@();Error=$null}
try {
    foreach ($language in @('ru','en')) {
        $results=Join-Path $output $language
        & (Join-Path $PSScriptRoot 'Test-Gui.ps1') -Executable $copy -Language $language -ResultDirectory $results -NoSdkEnvironment
        $result=Get-Content -LiteralPath (Join-Path $results 'result.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        if (-not $result.Passed -or -not $result.EmbeddedGuideLoaded) { throw ('Standalone GUI failed: '+$language) }
        $report.Languages+=@{Language=$language;Passed=$true;EmbeddedGuideLoaded=$result.EmbeddedGuideLoaded;AliasSavedAndBroadcast=$result.AliasSavedAndBroadcast}
    }
    $files=@(Get-ChildItem -LiteralPath $app -Force)
    $report.SingleExecutableOnly=$files.Count -eq 1 -and $files[0].Name -eq 'Vhui.Server.exe'
    $report.Passed=$report.SingleExecutableOnly -and $report.Languages.Count -eq 2
} catch { $report.Error=$_.Exception.ToString() }
finally { $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'result.json') -Encoding UTF8 }
if (-not $report.Passed) { throw ($report | ConvertTo-Json -Depth 5) }
$report | ConvertTo-Json -Depth 5
