# Пример из dotnet: .\Start.ps1 inspect ..\analysis\recovered\vhusbdwinw64.exe
# Пути к данным разрешаются относительно каталога, из которого вызван скрипт.
$runArguments = @($args)
if (($runArguments.Count -eq 2 -and $runArguments[0] -eq 'inspect') -or
    ($runArguments.Count -eq 3 -and $runArguments[0] -eq 'extract-payload')) {
    for ($argumentIndex = 1; $argumentIndex -lt $runArguments.Count; $argumentIndex++) {
        $runArguments[$argumentIndex] = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($runArguments[$argumentIndex])
    }
}
& (Join-Path $PSScriptRoot 'scripts\dotnet.ps1') run `
    --project (Join-Path $PSScriptRoot 'src\Vhui.Recovery\Vhui.Recovery.csproj') -- @runArguments
exit $LASTEXITCODE
