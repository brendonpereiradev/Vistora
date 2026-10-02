$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskTools = Join-Path $taskRoot '.tools'
New-Item -ItemType Directory -Force -Path $taskTools | Out-Null
$taskInstaller = Join-Path $taskTools 'dotnet-install.ps1'
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $taskInstaller -UseBasicParsing
& $taskInstaller -Version '10.0.401' -InstallDir (Join-Path $taskTools 'dotnet') -NoPath
if (-not (Test-Path -LiteralPath (Join-Path $taskTools 'dotnet\dotnet.exe'))) { throw 'O SDK não foi instalado.' }
Write-Output 'SDK disponível na pasta .tools. O ambiente global do Windows foi mantido.'
