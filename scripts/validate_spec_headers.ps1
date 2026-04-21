param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$matrixPath = Join-Path $repoRoot "docs/COMPLIANCE_MATRIX.md"

if (-not (Test-Path $matrixPath)) {
    Write-Error "Missing compliance matrix: $matrixPath"
}

function Get-CapabilityIds {
    param([string]$Path)

    $ids = New-Object System.Collections.Generic.List[string]
    foreach ($line in Get-Content -Path $Path) {
        $trimmed = $line.Trim()
        if (-not $trimmed.StartsWith("|")) { continue }
        if ($trimmed.StartsWith("| ---")) { continue }

        $parts = $trimmed.Split("|") | ForEach-Object { $_.Trim() }
        if ($parts.Count -lt 3) { continue }

        $candidate = $parts[1]
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if ($candidate -ieq "Capability ID") { continue }
        if ($candidate -match '^[A-Z0-9\-]+$') {
            $ids.Add($candidate)
        }
    }
    return $ids
}

$ids = Get-CapabilityIds -Path $matrixPath
$idSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($id in $ids) {
    [void]$idSet.Add($id)
}

$files = @(
    "FenBrowser.FenEngine/Core/EventLoop/EventLoopCoordinator.cs",
    "FenBrowser.FenEngine/Core/EventLoop/MicrotaskQueue.cs",
    "FenBrowser.FenEngine/DOM/NodeListWrapper.cs",
    "FenBrowser.FenEngine/DOM/DomMutationQueue.cs",
    "FenBrowser.FenEngine/Rendering/Css/CssSyntaxParser.cs",
    "FenBrowser.FenEngine/Rendering/Css/CascadeEngine.cs",
    "FenBrowser.FenEngine/Rendering/Css/SelectorMatcher.cs",
    "FenBrowser.FenEngine/Layout/MinimalLayoutComputer.cs",
    "FenBrowser.FenEngine/Rendering/Css/CssFlexLayout.cs",
    "FenBrowser.FenEngine/Layout/GridLayoutComputer.cs",
    "FenBrowser.FenEngine/Rendering/PaintTree/NewPaintTreeBuilder.cs",
    "FenBrowser.FenEngine/Core/FenRuntime.cs",
    "FenBrowser.FenEngine/Scripting/JavaScriptEngine.cs",
    "FenBrowser.Core/WebIDL/WebIdlParser.cs",
    "FenBrowser.Core/Network/Handlers/HttpHandler.cs",
    "FenBrowser.Core/Security/CspPolicy.cs",
    "FenBrowser.Core/Storage/BrowserCookieJar.cs",
    "FenBrowser.Host/ProcessIsolation/RendererIpc.cs",
    "FenBrowser.Host/ProcessIsolation/BrokeredProcessIsolationCoordinator.cs",
    "FenBrowser.Core/Accessibility/AccessibilityTreeBuilder.cs",
    "FenBrowser.DevTools/Domains/RuntimeDomain.cs",
    "FenBrowser.FenEngine/Testing/WPTTestRunner.cs",
    "FenBrowser.Test262/Program.cs"
)

$requiredCapabilityIds = @(
    "EVENTLOOP-MACROTASK-01",
    "EVENTLOOP-MICROTASK-01",
    "DOM-NODELIST-LIVE-01",
    "DOM-MUTATION-OBSERVER-01",
    "CSS-PARSER-RECOVERY-01",
    "CSS-CASCADE-ORDER-01",
    "CSS-SELECTOR-MATCH-01",
    "LAYOUT-FORMATTING-CONTEXT-01",
    "LAYOUT-FLEX-ALGO-01",
    "LAYOUT-GRID-TRACKS-01",
    "PAINT-STACKING-ORDER-01",
    "JS-BUILTINS-ES2024-01",
    "JS-RUNTIME-EXCEPTION-01",
    "WEBIDL-GENERATION-ORDER-01",
    "FETCH-CORS-POLICY-01",
    "SECURITY-CSP-ENFORCEMENT-01",
    "SECURITY-COOKIE-MODEL-01",
    "PROCESS-IPC-HANDSHAKE-01",
    "PROCESS-SANDBOX-FAILCLOSED-01",
    "A11Y-TREE-ROLE-MAP-01",
    "DEVTOOLS-RUNTIME-SIGNAL-01",
    "VERIFY-WPT-TRUTH-01",
    "VERIFY-TEST262-TRUTH-01"
)

$allowedDeterminism = @("strict", "best-effort")
$allowedFallback = @("clean-unsupported", "spec-defined")
$errors = New-Object System.Collections.Generic.List[string]

foreach ($relative in $files) {
    $fullPath = Join-Path $repoRoot ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path $fullPath)) {
        $errors.Add("${relative}: file missing")
        continue
    }

    $lines = Get-Content -Path $fullPath -TotalCount 30
    $head = $lines -join "`n"

    $specRef = [regex]::Match($head, '(?m)^//\s*SpecRef:\s*(.+?)\s*$').Groups[1].Value.Trim()
    $capId = [regex]::Match($head, '(?m)^//\s*CapabilityId:\s*(.+?)\s*$').Groups[1].Value.Trim()
    $det = [regex]::Match($head, '(?m)^//\s*Determinism:\s*(.+?)\s*$').Groups[1].Value.Trim()
    $fallback = [regex]::Match($head, '(?m)^//\s*FallbackPolicy:\s*(.+?)\s*$').Groups[1].Value.Trim()

    if ([string]::IsNullOrWhiteSpace($specRef)) { $errors.Add("${relative}: missing SpecRef") }
    if ([string]::IsNullOrWhiteSpace($capId)) {
        $errors.Add("${relative}: missing CapabilityId")
    } elseif (-not $idSet.Contains($capId)) {
        $errors.Add("${relative}: CapabilityId '$capId' missing from COMPLIANCE_MATRIX")
    }
    if ($allowedDeterminism -notcontains $det) { $errors.Add("${relative}: invalid Determinism '$det'") }
    if ($allowedFallback -notcontains $fallback) { $errors.Add("${relative}: invalid FallbackPolicy '$fallback'") }
}

$mappedIdSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($relative in $files) {
    $fullPath = Join-Path $repoRoot ($relative -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path $fullPath)) { continue }
    $head = (Get-Content -Path $fullPath -TotalCount 30) -join "`n"
    $capId = [regex]::Match($head, '(?m)^//\s*CapabilityId:\s*(.+?)\s*$').Groups[1].Value.Trim()
    if (-not [string]::IsNullOrWhiteSpace($capId)) {
        [void]$mappedIdSet.Add($capId)
    }
}

foreach ($requiredId in $requiredCapabilityIds) {
    if (-not $mappedIdSet.Contains($requiredId)) {
        $errors.Add("Required capability ID '$requiredId' is not mapped by governed source headers")
    }
}

if ($errors.Count -gt 0) {
    Write-Host "Spec governance validation failed:" -ForegroundColor Red
    $errors | ForEach-Object { Write-Host " - $_" -ForegroundColor Red }
    exit 1
}

Write-Host "Spec governance validation passed." -ForegroundColor Green
Write-Host "Checked files: $($files.Count)"
Write-Host "Capability IDs in matrix: $($idSet.Count)"
