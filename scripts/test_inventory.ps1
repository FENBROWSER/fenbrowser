# Inventory excluded FenBrowser.Tests sources grouped by compile dependency.
# Usage (from repo root):
#   powershell -File scripts\test_inventory.ps1
# Output:
#   Results/test-inventory/excluded_tests.md
#   Results/test-inventory/excluded_tests.json
[CmdletBinding()]
param(
    [string]$TestsProject = "FenBrowser.Tests/FenBrowser.Tests.csproj",
    [string]$OutputDir = "Results/test-inventory"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

if (-not (Test-Path -LiteralPath $TestsProject)) {
    throw "Test project not found: $TestsProject"
}

$projectText = Get-Content -LiteralPath $TestsProject -Raw
$testsRoot = Split-Path -Parent $TestsProject

# Collect explicit Compile Remove patterns.
$removePatterns = [System.Collections.Generic.List[string]]::new()
foreach ($match in [regex]::Matches($projectText, '<Compile\s+Remove="([^"]+)"\s*/>')) {
    $removePatterns.Add($match.Groups[1].Value.Trim())
}

# Collect explicit re-includes so they are not reported as excluded.
$includePatterns = [System.Collections.Generic.List[string]]::new()
foreach ($match in [regex]::Matches($projectText, '<Compile\s+Include="([^"]+)"\s*/>')) {
    $includePatterns.Add($match.Groups[1].Value.Trim())
}

# Supports the three shapes used by FenBrowser.Tests.csproj:
#   Dir\**\*.cs / Dir\**  -> everything under Dir
#   Dir\File.cs           -> exact file
function Convert-PatternToRegex {
    param([string]$Pattern)

    $p = $Pattern.Replace('/', '\')
    if ($p.Contains('**')) {
        $dir = $p.Split('\')[0]
        return '^' + [regex]::Escape($dir) + '(?:\\.*)?$'
    }
    return '^' + [regex]::Escape($p) + '$'
}

$testsRootFull = (Resolve-Path -LiteralPath $testsRoot).ProviderPath.TrimEnd('\', '/')
$allSources = Get-ChildItem -LiteralPath $testsRoot -Recurse -Filter *.cs |
    ForEach-Object { $_.FullName.Substring($testsRootFull.Length + 1).Replace('/', '\') } | Sort-Object

function Test-Excluded {
    param([string]$RelativePath)

    foreach ($pattern in $removePatterns) {
        $regex = Convert-PatternToRegex $pattern
        if ($RelativePath -match $regex) {
            foreach ($include in $includePatterns) {
                if ($include -eq $RelativePath) { return $false }
            }
            return $true
        }
    }
    return $false
}

$excluded = @($allSources | Where-Object { Test-Excluded -RelativePath $_ })
$active = @($allSources | Where-Object { -not (Test-Excluded -RelativePath $_) })

# Group by the first exclusion pattern that matched (the compile-dependency group).
$groups = [System.Collections.Generic.SortedDictionary[string, System.Collections.Generic.List[string]]]::new()
foreach ($file in $excluded) {
    $group = "ungrouped"
    foreach ($pattern in $removePatterns) {
        if ($file -match (Convert-PatternToRegex $pattern)) {
            $group = $pattern
            break
        }
    }
    if (-not $groups.ContainsKey($group)) {
        $groups[$group] = [System.Collections.Generic.List[string]]::new()
    }
    $groups[$group].Add($file)
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$jsonGroups = [ordered]@{}
foreach ($key in $groups.Keys) {
    $jsonGroups[$key] = $groups[$key]
}
$json = [ordered]@{
    schema          = "fenbrowser.test-inventory/1"
    generatedAtUtc  = (Get-Date).ToUniversalTime().ToString("o")
    testsProject    = $TestsProject
    totalSources    = $allSources.Count
    activeCount     = $active.Count
    excludedCount   = $excluded.Count
    groups          = $jsonGroups
}
$jsonPath = Join-Path $OutputDir "excluded_tests.json"
($json | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$md = [System.Text.StringBuilder]::new()
[void]$md.AppendLine("# Excluded FenBrowser.Tests Inventory")
[void]$md.AppendLine("")
[void]$md.AppendLine("Schema: fenbrowser.test-inventory/1")
[void]$md.AppendLine("")
[void]$md.AppendLine("| Group (csproj Compile Remove pattern) | Files |")
[void]$md.AppendLine("|---|---|")
foreach ($key in $groups.Keys) {
    [void]$md.AppendLine("| ``$key`` | $($groups[$key].Count) |")
}
[void]$md.AppendLine("")
[void]$md.AppendLine("Total sources: $($allSources.Count); active: $($active.Count); excluded: $($excluded.Count).")
[void]$md.AppendLine("")
foreach ($key in $groups.Keys) {
    [void]$md.AppendLine("## $key")
    [void]$md.AppendLine("")
    foreach ($file in $groups[$key]) {
        [void]$md.AppendLine("- $file")
    }
    [void]$md.AppendLine("")
}
$mdPath = Join-Path $OutputDir "excluded_tests.md"
$md.ToString() | Set-Content -LiteralPath $mdPath -Encoding UTF8

Write-Host "Excluded $($excluded.Count) of $($allSources.Count) sources across $($groups.Count) groups."
Write-Host "Report: $mdPath"
Write-Host "JSON:   $jsonPath"
