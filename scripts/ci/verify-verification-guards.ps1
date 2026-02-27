Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Fail([string]$Message) {
    Write-Error $Message
    exit 1
}

function Parse-IntValue([string]$Content, [string]$Pattern, [string]$Label) {
    $m = [regex]::Match($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $m.Success) { Fail "Missing numeric value for '$Label'." }
    return [int]($m.Groups[1].Value -replace ",", "")
}

function Parse-DoubleValue([string]$Content, [string]$Pattern, [string]$Label) {
    $m = [regex]::Match($Content, $Pattern, [System.Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $m.Success) { Fail "Missing decimal value for '$Label'." }
    return [double]::Parse($m.Groups[1].Value, [System.Globalization.CultureInfo]::InvariantCulture)
}

Write-Host "[verify] Checking for placeholder assertions..."
$testFiles = Get-ChildItem FenBrowser.Tests -Recurse -File -Include *.cs
$placeholder = Select-String -Path $testFiles.FullName -Pattern 'Assert\.True\(true\)|Assert\.False\(false\)' -CaseSensitive
if ($placeholder) {
    $lines = $placeholder | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    Fail ("Placeholder assertions found:`n" + ($lines -join "`n"))
}

Write-Host "[verify] Checking stale WPT runner filename references..."
$docFiles = Get-ChildItem docs -Recurse -File -Include *.md
$staleWptName = Select-String -Path $docFiles.FullName -Pattern 'WptRunner\.cs' -CaseSensitive
if ($staleWptName) {
    $lines = $staleWptName | ForEach-Object { "$($_.Path):$($_.LineNumber): $($_.Line.Trim())" }
    Fail ("Stale WPT runner filename reference detected (expected WPTTestRunner.cs):`n" + ($lines -join "`n"))
}

Write-Host "[verify] Checking Test262 baseline drift..."
$sourcePath = "test262_results.md"
$baselinePath = "docs/VERIFICATION_BASELINES.md"
if (-not (Test-Path $sourcePath)) { Fail "Missing source benchmark file: $sourcePath" }
if (-not (Test-Path $baselinePath)) { Fail "Missing baseline file: $baselinePath" }

$source = Get-Content $sourcePath -Raw
$baseline = Get-Content $baselinePath -Raw

$srcTotal = Parse-IntValue $source '\*\*Total Tests:\*\*\s*([0-9,]+)' "source total"
$srcPassed = Parse-IntValue $source '\*\*Total Passed:\*\*\s*([0-9,]+)' "source passed"
$srcFailed = Parse-IntValue $source '\*\*Total Failed:\*\*\s*([0-9,]+)' "source failed"
$srcRate = Parse-DoubleValue $source '\*\*Overall Pass Rate:\*\*\s*([0-9]+(?:\.[0-9]+)?)%' "source pass rate"

$baseTotal = Parse-IntValue $baseline 'Test262Full\.TotalTests:\s*([0-9,]+)' "baseline total"
$basePassed = Parse-IntValue $baseline 'Test262Full\.Passed:\s*([0-9,]+)' "baseline passed"
$baseFailed = Parse-IntValue $baseline 'Test262Full\.Failed:\s*([0-9,]+)' "baseline failed"
$baseRate = Parse-DoubleValue $baseline 'Test262Full\.PassRatePercent:\s*([0-9]+(?:\.[0-9]+)?)' "baseline pass rate"

if (($srcPassed + $srcFailed) -ne $srcTotal) {
    Fail "Source arithmetic mismatch: passed + failed != total in $sourcePath"
}
if (($basePassed + $baseFailed) -ne $baseTotal) {
    Fail "Baseline arithmetic mismatch: passed + failed != total in $baselinePath"
}
if ($srcTotal -ne $baseTotal -or $srcPassed -ne $basePassed -or $srcFailed -ne $baseFailed) {
    Fail "Baseline drift detected: $baselinePath does not match $sourcePath totals."
}
if ([math]::Abs($srcRate - $baseRate) -gt 0.01) {
    Fail "Baseline drift detected: pass rate mismatch (source=$srcRate, baseline=$baseRate)."
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
