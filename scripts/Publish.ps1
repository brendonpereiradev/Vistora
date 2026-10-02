. "$PSScriptRoot\Common.ps1"
$taskDotnet = Get-VistoraDotnet
$taskReleaseDir = Join-Path $taskProjectRoot 'output\release'
$taskAppDir = Join-Path $taskReleaseDir 'Vistora'
New-Item -ItemType Directory -Force -Path $taskAppDir | Out-Null
& $taskDotnet publish (Join-Path $taskProjectRoot 'src\Vistora.Desktop\Vistora.Desktop.csproj') -c Release -r win-x64 --self-contained true -o $taskAppDir
Assert-VistoraExit
if (-not (Test-Path -LiteralPath (Join-Path $taskAppDir '.playwright\node\win32_x64\node.exe'))) {
    throw 'O pacote não contém o componente de automação do navegador.'
}
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'README.md') -Destination (Join-Path $taskAppDir 'COMO-USAR.md')
$taskZipPath = Join-Path $taskReleaseDir 'Vistora-Windows-x64.zip'
if (Test-Path -LiteralPath $taskZipPath) { Remove-Item -LiteralPath $taskZipPath }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($taskAppDir, $taskZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Output "Aplicativo: $(Join-Path $taskAppDir 'Vistora.exe')"
Write-Output "Pacote: $taskZipPath"
