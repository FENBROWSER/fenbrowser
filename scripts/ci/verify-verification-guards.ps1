Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

Write-Host "[verify] Checking for placeholder assertions..."
$testFiles = Get-ChildItem FenBrowser.Tests -Recurse -File -Include *.cs
$placeholder = Select-String -Path $testFiles.FullName -Pattern 'Assert\.True\(true\)|Assert\.False\(false\)' -CaseSensitive
if ($placeholder) {
    $lines = $placeholder | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    Fail ("Placeholder assertions found:`n" + ($lines -join "`n"))
}

Write-Host "[verify] Checking volume documentation file/line references..."
$volumeParserProject = "test_parser/test_parser.csproj"
if (-not (Test-Path $volumeParserProject)) {
    Fail "Missing volume reference parser project: $volumeParserProject"
}

& dotnet run --project $volumeParserProject -- --repo . --docs docs
if ($LASTEXITCODE -ne 0) {
    Fail "Volume reference validation failed."
}

Write-Host "[verify] Running parser hostile corpus fuzz regressions..."
$fuzzScript = "scripts/ci/run-parser-fuzz-regressions.ps1"
if (-not (Test-Path $fuzzScript)) {
    Fail "Missing parser fuzz regression runner: $fuzzScript"
}

& powershell -ExecutionPolicy Bypass -File $fuzzScript
if ($LASTEXITCODE -ne 0) {
    Fail "Parser fuzz regression verification failed."
}

Write-Host "[verify] Verification guards passed."
