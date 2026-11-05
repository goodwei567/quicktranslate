[CmdletBinding()]
param(
    [string]$Version = "1.2.0"
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = Join-Path $projectRoot "artifacts"
$publishDirectory = Join-Path $artifactsRoot "publish\win-x64"
$archivePath = Join-Path $artifactsRoot "QuickTranslate-v$Version-win-x64.zip"
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue)?.Source
if (-not $dotnet) {
    $dotnet = "C:\Program Files\dotnet\dotnet.exe"
}

if (-not (Test-Path -LiteralPath $dotnet)) {
    throw ".NET SDK was not found. Install the .NET 8 SDK and retry."
}

$resolvedRoot = [System.IO.Path]::GetFullPath($projectRoot)
$resolvedArtifacts = [System.IO.Path]::GetFullPath($artifactsRoot)
if (-not $resolvedArtifacts.StartsWith($resolvedRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to clean an artifacts directory outside the repository."
}

if (Test-Path -LiteralPath $artifactsRoot) {
    Remove-Item -LiteralPath $artifactsRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

& $dotnet restore (Join-Path $projectRoot "QuickTranslate.csproj") -r win-x64
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore failed."
}

& $dotnet publish (Join-Path $projectRoot "QuickTranslate.csproj") `
    -c Release `
    -p:Platform=x64 `
    -p:RuntimeIdentifier=win-x64 `
    -p:SelfContained=true `
    -p:PublishTrimmed=false `
    -p:Version=$Version `
    --no-restore `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed."
}

Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archivePath -CompressionLevel Optimal
Write-Host "Published: $publishDirectory"
Write-Host "Archive:   $archivePath"
