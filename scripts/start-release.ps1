<#
.SYNOPSIS
Starts the extracted Windows release from its own folder.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$appExecutable = Join-Path $PSScriptRoot 'PyrionKnowledgePipeline.Web.exe'
if (-not (Test-Path -LiteralPath $appExecutable -PathType Leaf)) {
    throw 'Extract the complete Windows release ZIP before running Start-Pyrion.ps1.'
}

Push-Location -LiteralPath $PSScriptRoot
try {
    Write-Host 'Open http://localhost:5187 in your browser after the app starts.'
    Write-Host 'Keep this window open. Press Ctrl+C to stop. Start LM Studio separately for AI analysis.'
    & $appExecutable --urls http://localhost:5187
    if ($LASTEXITCODE -ne 0) { throw "Pyrion exited with code $LASTEXITCODE. Check whether another app is using port 5187." }
}
finally { Pop-Location }
