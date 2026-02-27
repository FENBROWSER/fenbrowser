Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "[fuzz] Running parser/renderer hostile corpus regressions..."
& dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj -c Debug --filter "FullyQualifiedName~ParserFuzzRegressionTests"
if ($LASTEXITCODE -ne 0) {
    Write-Error "Parser fuzz regression suite failed."
    exit 1
}

Write-Host "[fuzz] Parser/renderer hostile corpus regressions passed."
