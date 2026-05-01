param(
    [string]$InventoryPath = "C:\Users\udayk\Downloads\html_css_spec_inventory_2026-05-01.md",
    [string]$CssLoaderPath = "FenBrowser.FenEngine/Rendering/Css/CssLoader.cs",
    [string]$OutputPath = "Results/css_inventory_coverage_2026-05-01.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $InventoryPath)) {
    throw "Inventory file not found: $InventoryPath"
}

if (-not (Test-Path -LiteralPath $CssLoaderPath)) {
    throw "CssLoader file not found: $CssLoaderPath"
}

$inventoryLines = Get-Content -LiteralPath $InventoryPath
$loaderText = Get-Content -LiteralPath $CssLoaderPath -Raw

$collect = $false
$rawEntries = New-Object System.Collections.Generic.List[string]
foreach ($line in $inventoryLines) {
    if ($line -match '^##\s+CSS parent-to-child inherited property checklist') {
        $collect = $true
        continue
    }

    if ($line -match '^##\s+Browser-engine implementation notes') {
        $collect = $false
    }

    if (-not $collect) {
        continue
    }

    if ($line -match '^- `([^`]+)`') {
        $rawEntries.Add($matches[1].Trim().ToLowerInvariant())
    }
}

if ($rawEntries.Count -eq 0) {
    throw "No CSS inventory entries were extracted from $InventoryPath"
}

$normalized = New-Object System.Collections.Generic.HashSet[string] ([System.StringComparer]::OrdinalIgnoreCase)
$ignoredPseudoEntries = New-Object System.Collections.Generic.List[string]
$transforms = New-Object System.Collections.Generic.List[string]

function Add-NormalizedProperty {
    param([string]$Token)

    $token = $Token.Trim().ToLowerInvariant()
    if ([string]::IsNullOrWhiteSpace($token)) {
        return
    }

    if ($token -eq "--* (custom properties)") {
        $transforms.Add("--* (custom properties) -> --inventory-probe")
        $normalized.Add("--inventory-probe") | Out-Null
        return
    }

    if ($token.EndsWith(" alias", [System.StringComparison]::Ordinal)) {
        $base = $token.Substring(0, $token.Length - " alias".Length).Trim()
        $transforms.Add("$token -> $base")
        $token = $base
    }

    # Exclude prose-only labels that are not CSS property identifiers.
    if ($token -match '\s') {
        $ignoredPseudoEntries.Add($token)
        return
    }

    if ($token -match '^[a-z][a-z0-9-]*$' -or $token -match '^--[a-z0-9-]+$') {
        $normalized.Add($token) | Out-Null
    } else {
        $ignoredPseudoEntries.Add($token)
    }
}

foreach ($entry in $rawEntries) {
    $parts = $entry -split '/'
    foreach ($part in $parts) {
        Add-NormalizedProperty -Token $part
    }
}

$missing = New-Object System.Collections.Generic.List[string]
$syntheticProbeChecks = New-Object System.Collections.Generic.List[string]
foreach ($property in ($normalized | Sort-Object)) {
    if ($property -eq "--inventory-probe") {
        if ($loaderText -match 'StartsWith\("--"\)') {
            $syntheticProbeChecks.Add("--inventory-probe validated via IsSupportedProperty custom-property gate")
            continue
        }

        $missing.Add($property)
        continue
    }

    $quoted = '"' + [regex]::Escape($property) + '"'
    if ($loaderText -notmatch $quoted) {
        $missing.Add($property)
    }
}

$outputDir = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDir) -and -not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

$result = [ordered]@{
    generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    inventory_path = (Resolve-Path -LiteralPath $InventoryPath).Path
    css_loader_path = (Resolve-Path -LiteralPath $CssLoaderPath).Path
    raw_entry_count = $rawEntries.Count
    normalized_property_count = $normalized.Count
    ignored_pseudo_entries = ($ignoredPseudoEntries | Sort-Object -Unique)
    transforms_applied = ($transforms | Sort-Object -Unique)
    synthetic_probe_checks = ($syntheticProbeChecks | Sort-Object -Unique)
    missing_property_count = $missing.Count
    missing_properties = $missing
}

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$result | ConvertTo-Json -Depth 4
