param([string]$OutputDirectory)
. "$PSScriptRoot\Common.ps1"
$taskDotnet = Get-VistoraDotnet
$taskDesktopProject = Join-Path $taskProjectRoot 'src\Vistora.Desktop\Vistora.Desktop.csproj'
[xml]$taskProject = Get-Content -LiteralPath $taskDesktopProject -Raw
$taskVersion = [string]$taskProject.Project.PropertyGroup.Version
if ($taskVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw 'O projeto precisa declarar uma versão válida para a publicação.'
}
$taskReleaseDir = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $taskProjectRoot "output\release\$taskVersion" }
$taskAppDir = Join-Path $taskReleaseDir 'Vistora'
$taskZipPath = Join-Path $taskReleaseDir "Vistora-$taskVersion-Windows-x64.zip"
if (Test-Path -LiteralPath $taskAppDir) {
    if (-not (Get-Item -LiteralPath $taskAppDir).PSIsContainer -or @(Get-ChildItem -LiteralPath $taskAppDir -Force).Count -gt 0) {
        throw 'A pasta do aplicativo já contém arquivos. Informe uma pasta nova em -OutputDirectory.'
    }
}
if (Test-Path -LiteralPath $taskZipPath) {
    throw 'O ZIP desta publicação já existe. Informe uma pasta nova em -OutputDirectory.'
}
New-Item -ItemType Directory -Force -Path $taskAppDir | Out-Null
& $taskDotnet publish $taskDesktopProject -c Release -r win-x64 --self-contained true -o $taskAppDir
Assert-VistoraExit
Copy-Item -LiteralPath (Join-Path $taskProjectRoot 'README.md') -Destination (Join-Path $taskAppDir 'COMO-USAR.md')
foreach ($taskRequiredFile in @('Vistora.exe', 'Vistora.dll', 'Vistora.runtimeconfig.json', 'assets/resolucoes.txt', 'COMO-USAR.md', '.playwright/node/win32_x64/node.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $taskAppDir $taskRequiredFile) -PathType Leaf)) {
        throw "O pacote não contém um arquivo necessário: $taskRequiredFile"
    }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($taskAppDir, $taskZipPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)
Write-Output "Aplicativo: $(Join-Path $taskAppDir 'Vistora.exe')"
Write-Output "Pacote: $taskZipPath"
