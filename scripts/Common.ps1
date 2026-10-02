$ErrorActionPreference = 'Stop'
$taskProjectRoot = Split-Path -Parent $PSScriptRoot
function Get-VistoraDotnet {
    $taskLocalDotnet = Join-Path $taskProjectRoot '.tools\dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $taskLocalDotnet) { return $taskLocalDotnet }
    $taskInstalled = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($taskInstalled) {
        $taskSdkVersions = & $taskInstalled.Source --list-sdks
        if ($taskSdkVersions | Where-Object { $_ -match '^10\.' }) { return $taskInstalled.Source }
    }
    throw 'Instale o SDK .NET 10 ou execute scripts\Bootstrap.ps1 para instalá-lo somente nesta pasta.'
}
function Assert-VistoraExit {
    if ($LASTEXITCODE -ne 0) { throw "A operação falhou (código $LASTEXITCODE)." }
}
