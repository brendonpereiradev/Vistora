param([switch]$SkipBrowser)
. "$PSScriptRoot\Common.ps1"
$taskDotnet = Get-VistoraDotnet
& "$PSScriptRoot\Build.ps1"
& $taskDotnet run --project (Join-Path $taskProjectRoot 'tests\Vistora.Tests') -c Release --no-build
Assert-VistoraExit
& $taskDotnet run --project (Join-Path $taskProjectRoot 'tests\Vistora.DesktopTests') -c Release --no-build
Assert-VistoraExit
if (-not $SkipBrowser) {
    & $taskDotnet run --project (Join-Path $taskProjectRoot 'tests\Vistora.BrowserTests') -c Release --no-build
    Assert-VistoraExit
}
