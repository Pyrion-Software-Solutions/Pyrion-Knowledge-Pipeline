<#
.SYNOPSIS
Builds a self-contained Windows x64 ZIP and a SHA-256 checksum.
.EXAMPLE
.\scripts\package-release.ps1 -Version 0.1.0-preview.1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$')]
    [string]$Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$releaseRoot = Join-Path $projectRoot "artifacts/releases/$Version"
if (Test-Path -LiteralPath $releaseRoot) {
    throw "Output already exists: $releaseRoot. Choose a new version or move the previous output before retrying."
}
$publishRoot = Join-Path $releaseRoot 'app'
$archiveName = "PyrionKnowledgePipeline-$Version-win-x64.zip"
$archivePath = Join-Path $releaseRoot $archiveName

Push-Location -LiteralPath $projectRoot
try {
    dotnet publish src/PyrionKnowledgePipeline.Web/PyrionKnowledgePipeline.Web.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishTrimmed=false --output $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $publishRoot
    Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishRoot
    Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/release-readme.md') -Destination (Join-Path $publishRoot 'RELEASE-README.md')
    Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts/start-release.ps1') -Destination (Join-Path $publishRoot 'Start-Pyrion.ps1')
    Compress-Archive -Path (Join-Path $publishRoot '*') -DestinationPath $archivePath
    $hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $releaseRoot 'SHA256SUMS.txt'), "$hash  $archiveName`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Release archive: $archivePath"
}
finally { Pop-Location }
