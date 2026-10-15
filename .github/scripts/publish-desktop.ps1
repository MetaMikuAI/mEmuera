param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64')][string]$Rid,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
$publishArgs = @(
    'publish', 'src/mEmuera.App.Tui', '-c', 'Release', '-r', $Rid,
    '--self-contained', 'true', '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false',
    '-p:DebugType=None', '-p:DebugSymbols=false', '-o', "artifacts/publish/$Rid"
)
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw '单文件发布失败。' }
$extension = if ($Rid.StartsWith('win-')) { '.exe' } else { '' }
New-Item -ItemType Directory -Path artifacts/dist -Force | Out-Null
$target = "artifacts/dist/mEmuera-$Version-$Rid$extension"
Copy-Item -LiteralPath "artifacts/publish/$Rid/mEmuera.App.Tui$extension" -Destination $target
