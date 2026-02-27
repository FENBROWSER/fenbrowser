param(
    [string]$RepoRoot = ".",
    [string]$DocsPath = "docs",
    [string]$WriteReportPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

$projectPath = "test_parser/test_parser.csproj"
if (-not (Test-Path $projectPath)) {
    Fail "Missing parser project: $projectPath"
}

$argsList = @(
    "run",
    "--project", $projectPath,
    "--",
    "--repo", $RepoRoot,
    "--docs", $DocsPath
)

if (-not [string]::IsNullOrWhiteSpace($WriteReportPath)) {
    $argsList += @("--write", $WriteReportPath)
}

& dotnet @argsList
if ($LASTEXITCODE -ne 0) {
    Fail "Volume doc reference validation failed."
}

Write-Host "[verify-volume-doc-references] Passed."
