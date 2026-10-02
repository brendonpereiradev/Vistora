. "$PSScriptRoot\Common.ps1"
$taskDotnet = Get-VistoraDotnet
& $taskDotnet build (Join-Path $taskProjectRoot 'Vistora.sln') -c Release
Assert-VistoraExit
