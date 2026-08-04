[CmdletBinding()]
param(
    [ValidateRange(1, 1024)]
    [int]$CorpusMiB = 8,

    [ValidateRange(1, 100)]
    [int]$Iterations = 9,

    [ValidateRange(0, 100)]
    [int]$WarmupIterations = 2
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Get-Location).Path
$csharpProject = Join-Path $repoRoot 'tools\html-tokenizer-bench\csharp\FenBrowser.HtmlTokenizer.Bench.csproj'
$rustManifest = Join-Path $repoRoot 'tools\html-tokenizer-bench\rust\Cargo.toml'
$cppSource = Join-Path $repoRoot 'tools\html-tokenizer-bench\cpp\main.cpp'
$seedPath = Join-Path $repoRoot 'tools\html-tokenizer-bench\corpus\benchmark-seed.html'

foreach ($requiredPath in @($csharpProject, $rustManifest, $cppSource, $seedPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Run this script from the FenBrowser repository root. Missing: $requiredPath"
    }
}

$resultRoot = Join-Path $repoRoot 'Results\html-tokenizer-bench'
$csharpOutput = Join-Path $resultRoot 'build\csharp'
$rustTarget = Join-Path $resultRoot 'build\rust'
$cppOutput = Join-Path $resultRoot 'build\cpp'
$corpusPath = Join-Path $resultRoot 'benchmark-corpus.html'
$summaryPath = Join-Path $resultRoot 'summary.json'

foreach ($directory in @($resultRoot, $csharpOutput, $rustTarget, $cppOutput)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

$seedBytes = [IO.File]::ReadAllBytes($seedPath)
if ($seedBytes.Length -eq 0) {
    throw 'Benchmark seed must not be empty.'
}
if ($seedBytes | Where-Object { $_ -gt 0x7f } | Select-Object -First 1) {
    throw 'Benchmark seed must remain ASCII.'
}

$targetBytes = $CorpusMiB * 1MB
$corpusStream = [IO.File]::Open(
    $corpusPath,
    [IO.FileMode]::Create,
    [IO.FileAccess]::Write,
    [IO.FileShare]::None)
try {
    while ($corpusStream.Length -lt $targetBytes) {
        $corpusStream.Write($seedBytes, 0, $seedBytes.Length)
    }
}
finally {
    $corpusStream.Dispose()
}

dotnet build-server shutdown | Out-Null
dotnet publish $csharpProject `
    --configuration Release `
    --output $csharpOutput `
    -p:UseAppHost=true `
    /nodeReuse:false `
    --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    throw "C# benchmark publish failed with exit code $LASTEXITCODE."
}

cargo build `
    --release `
    --locked `
    --manifest-path $rustManifest `
    --target-dir $rustTarget
if ($LASTEXITCODE -ne 0) {
    throw "Rust benchmark build failed with exit code $LASTEXITCODE."
}

$vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswherePath)) {
    throw 'Visual Studio Installer vswhere.exe was not found.'
}

$visualStudioPath = & $vswherePath `
    -latest `
    -products * `
    -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 `
    -property installationPath
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($visualStudioPath)) {
    throw 'A Visual Studio installation with the x64 C++ toolchain was not found.'
}

$developerCommand = Join-Path $visualStudioPath 'Common7\Tools\VsDevCmd.bat'
if (-not (Test-Path -LiteralPath $developerCommand)) {
    throw "Visual Studio developer command was not found: $developerCommand"
}

$environmentCommand = "`"$developerCommand`" -no_logo -arch=x64 -host_arch=x64 >nul && set"
$environmentLines = & $env:ComSpec /d /s /c $environmentCommand
if ($LASTEXITCODE -ne 0) {
    throw 'Visual Studio developer environment initialization failed.'
}
foreach ($line in $environmentLines) {
    if ($line -match '^([^=]+)=(.*)$') {
        Set-Item -Path "Env:$($matches[1])" -Value $matches[2]
    }
}

$compiler = Get-Command cl.exe -ErrorAction Stop
$cppExecutable = Join-Path $cppOutput 'fenbrowser-html-tokenizer-bench.exe'
$cppObject = Join-Path $cppOutput 'html-tokenizer-bench.obj'
$cppPdb = Join-Path $cppOutput 'html-tokenizer-bench.pdb'
& $compiler.Source `
    /nologo `
    /std:c++20 `
    /O2 `
    /GL `
    /EHsc `
    /DNDEBUG `
    /W4 `
    $cppSource `
    "/Fo$cppObject" `
    "/Fd$cppPdb" `
    "/Fe$cppExecutable" `
    /link `
    /LTCG
if ($LASTEXITCODE -ne 0) {
    throw "C++ benchmark build failed with exit code $LASTEXITCODE."
}

$csharpExecutable = Join-Path $csharpOutput 'FenBrowser.HtmlTokenizer.Bench.exe'
$rustExecutable = Join-Path $rustTarget 'release\fenbrowser-html-tokenizer-bench.exe'

function Invoke-TokenizerBenchmark {
    param(
        [Parameter(Mandatory)]
        [string]$Executable,

        [string[]]$ExtraArguments = @()
    )

    if (-not (Test-Path -LiteralPath $Executable)) {
        throw "Benchmark executable was not found: $Executable"
    }

    $rawOutput = & $Executable `
        --input $corpusPath `
        --iterations $Iterations `
        --warmup $WarmupIterations `
        @ExtraArguments
    if ($LASTEXITCODE -ne 0) {
        throw "Benchmark failed with exit code ${LASTEXITCODE}: $Executable"
    }

    try {
        return $rawOutput | ConvertFrom-Json
    }
    catch {
        throw "Benchmark returned invalid JSON: $rawOutput"
    }
}

$reports = @(
    Invoke-TokenizerBenchmark `
        -Executable $csharpExecutable `
        -ExtraArguments @('--implementation', 'production')
    Invoke-TokenizerBenchmark `
        -Executable $csharpExecutable `
        -ExtraArguments @('--implementation', 'common')
    Invoke-TokenizerBenchmark -Executable $rustExecutable
    Invoke-TokenizerBenchmark -Executable $cppExecutable
)

$checksums = @($reports | Select-Object -ExpandProperty checksum -Unique)
$tokenCounts = @($reports | Select-Object -ExpandProperty token_count -Unique)
$inputLengths = @($reports | Select-Object -ExpandProperty input_bytes -Unique)
if ($checksums.Count -ne 1 -or $tokenCounts.Count -ne 1 -or $inputLengths.Count -ne 1) {
    $mismatch = $reports | ConvertTo-Json -Depth 5
    throw "Cross-language output mismatch. Timing results are invalid.`n$mismatch"
}

$csharpProductionReport = $reports |
    Where-Object benchmark_id -eq 'csharp-production'
$csharpCommonReport = $reports |
    Where-Object benchmark_id -eq 'csharp-common'
$speedupsVsProduction = [ordered]@{}
$speedupsVsCommon = [ordered]@{}
foreach ($report in $reports) {
    $speedupsVsProduction[$report.benchmark_id] = [Math]::Round(
        [double]$csharpProductionReport.median_ms / [double]$report.median_ms,
        3)
    $speedupsVsCommon[$report.benchmark_id] = [Math]::Round(
        [double]$csharpCommonReport.median_ms / [double]$report.median_ms,
        3)
}

$corpusHash = (Get-FileHash -LiteralPath $corpusPath -Algorithm SHA256).Hash.ToLowerInvariant()
$compilerVersion = (Get-Item -LiteralPath $compiler.Source).VersionInfo.ProductVersion
$summary = [ordered]@{
    schema_version = 1
    generated_at_utc = [DateTime]::UtcNow.ToString('O')
    git_commit = (git rev-parse HEAD).Trim()
    environment = [ordered]@{
        os = [Environment]::OSVersion.VersionString
        architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        processor = $env:PROCESSOR_IDENTIFIER
        dotnet = (dotnet --version).Trim()
        rustc = (rustc --version).Trim()
        cargo = (cargo --version).Trim()
        cpp_compiler = "MSVC $compilerVersion"
        visual_studio = $visualStudioPath
    }
    corpus = [ordered]@{
        path = 'Results/html-tokenizer-bench/benchmark-corpus.html'
        bytes = [IO.FileInfo]::new($corpusPath).Length
        sha256 = $corpusHash
        seed = 'tools/html-tokenizer-bench/corpus/benchmark-seed.html'
    }
    validation = [ordered]@{
        status = 'passed'
        token_count = [long]$tokenCounts[0]
        checksum = [string]$checksums[0]
    }
    speedup_vs_csharp_production = $speedupsVsProduction
    speedup_vs_csharp_common = $speedupsVsCommon
    results = $reports
}

$summaryJson = $summary | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText(
    $summaryPath,
    $summaryJson,
    [Text.UTF8Encoding]::new($false))

$displayReports = foreach ($report in $reports) {
    [pscustomobject]@{
        benchmark_id = $report.benchmark_id
        language = $report.language
        token_count = $report.token_count
        checksum = $report.checksum
        median_ms = $report.median_ms
        throughput_mib_per_second = $report.throughput_mib_per_second
        speedup_vs_production = $speedupsVsProduction[$report.benchmark_id]
        speedup_vs_common_csharp = $speedupsVsCommon[$report.benchmark_id]
        allocated_bytes_per_iteration = $report.allocated_bytes_per_iteration
    }
}

$displayReports |
    Format-Table -AutoSize

"Cross-language validation: passed"
"Summary: $summaryPath"
