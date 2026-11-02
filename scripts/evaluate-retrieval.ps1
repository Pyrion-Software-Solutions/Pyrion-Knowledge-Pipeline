param(
    [Parameter(Mandatory = $true)][string]$Model,
    [Parameter(Mandatory = $true)][string]$ModelVersion,
    [string]$DocumentPrefix = '',
    [string]$QueryPrefix = '',
    [string]$ReportPath
)
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $ReportPath) { $ReportPath = Join-Path $repositoryRoot 'docs\retrieval-evaluation-results.json' }
$environmentNames = @('PYRION_RUN_LM_SMOKE','PYRION_EVAL_MODEL','PYRION_EVAL_MODEL_VERSION','PYRION_EVAL_DOCUMENT_PREFIX','PYRION_EVAL_QUERY_PREFIX','PYRION_EVAL_REPORT')
$previousValues = @{}
foreach ($name in $environmentNames) { $previousValues[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
try {
    $env:PYRION_RUN_LM_SMOKE = '1'
    $env:PYRION_EVAL_MODEL = $Model
    $env:PYRION_EVAL_MODEL_VERSION = $ModelVersion
    $env:PYRION_EVAL_DOCUMENT_PREFIX = $DocumentPrefix
    $env:PYRION_EVAL_QUERY_PREFIX = $QueryPrefix
    $env:PYRION_EVAL_REPORT = [IO.Path]::GetFullPath($ReportPath)
    Push-Location -LiteralPath $repositoryRoot
    try {
        dotnet test PyrionKnowledgePipeline.sln --filter 'FullyQualifiedName~LocalRetrievalEvaluationTests' --verbosity minimal
        if ($LASTEXITCODE -ne 0) { throw 'Local embedding evaluation failed. See the test output; no quality report should be accepted as current.' }
    } finally { Pop-Location }
} finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousValues[$name], 'Process') }
}
