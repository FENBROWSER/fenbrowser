param()

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$matrixPath = Join-Path $repoRoot "docs/COMPLIANCE_MATRIX.md"
$mapPath = Join-Path $repoRoot "docs/spec_governance_map.json"
$securityContractPath = Join-Path $repoRoot "docs/security_capability_contract.json"

if (-not (Test-Path $matrixPath)) {
    Write-Error "Missing compliance matrix: $matrixPath"
}
if (-not (Test-Path $mapPath)) {
    Write-Error "Missing governance map: $mapPath"
}
if (-not (Test-Path $securityContractPath)) {
    Write-Error "Missing security capability contract: $securityContractPath"
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

function Is-SecuritySensitiveCapabilityId {
    param([string]$CapabilityId)

    if ([string]::IsNullOrWhiteSpace($CapabilityId)) {
        return $false
    }

    if ($CapabilityId.StartsWith("SECURITY-", [StringComparison]::Ordinal)) { return $true }
    if ($CapabilityId.StartsWith("PROCESS-", [StringComparison]::Ordinal)) { return $true }
    if ($CapabilityId -eq "FETCH-CORS-POLICY-01") { return $true }
    return $false
}

$ids = Get-CapabilityIds -Path $matrixPath
$idSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($id in $ids) {
    [void]$idSet.Add($id)
}

$map = Get-Content -Path $mapPath -Raw | ConvertFrom-Json
$files = @($map.governedFiles)
$requiredCapabilityIds = @($map.requiredCapabilityIds)

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

$securityContract = Get-Content -Path $securityContractPath -Raw | ConvertFrom-Json
$securityEntries = @($securityContract.entries)

$contractIdSet = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::Ordinal)
foreach ($entry in $securityEntries) {
    $contractId = [string]$entry.capabilityId
    if ([string]::IsNullOrWhiteSpace($contractId)) {
        $errors.Add("security_capability_contract: entry missing capabilityId")
        continue
    }

    if (-not $contractIdSet.Add($contractId)) {
        $errors.Add("security_capability_contract: duplicate capabilityId '$contractId'")
    }

    if (-not $idSet.Contains($contractId)) {
        $errors.Add("security_capability_contract: capabilityId '$contractId' missing from COMPLIANCE_MATRIX")
    }

    $impact = [string]$entry.securityImpact
    if ([string]::IsNullOrWhiteSpace($impact)) {
        $errors.Add("security_capability_contract: capabilityId '$contractId' missing securityImpact")
    }

    $reasonCodes = @($entry.requiredReasonCodes)
    if ($reasonCodes.Count -eq 0) {
        $errors.Add("security_capability_contract: capabilityId '$contractId' missing requiredReasonCodes")
    }
    else {
        foreach ($code in $reasonCodes) {
            $value = [string]$code
            if ([string]::IsNullOrWhiteSpace($value) -or $value -notmatch '^[A-Z0-9_]+$') {
                $errors.Add("security_capability_contract: capabilityId '$contractId' has invalid reason code '$value'")
            }
        }
    }
}

foreach ($matrixId in $ids) {
    if (-not (Is-SecuritySensitiveCapabilityId -CapabilityId $matrixId)) {
        continue
    }

    if (-not $contractIdSet.Contains($matrixId)) {
        $errors.Add("security_capability_contract: missing entry for security capability '$matrixId'")
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
Write-Host "Security capability contract entries: $($securityEntries.Count)"
