param(
    [string]$OutputPath = "",
    [switch]$FailOnHigh
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Invoke-Ripgrep {
    param(
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string[]]$Paths,
        [string[]]$ExtraArgs = @()
    )

    $args = @(
        "-n",
        "--no-heading",
        "--color", "never",
        "--glob", "!**/bin/**",
        "--glob", "!**/obj/**"
    ) + $ExtraArgs + @($Pattern) + $Paths

    $output = & rg @args 2>$null
    if ($LASTEXITCODE -eq 0) {
        return @($output)
    }
    if ($LASTEXITCODE -eq 1) {
        return @()
    }

    throw "ripgrep failed for pattern '$Pattern'."
}

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string]$FilePath)
    $dir = Split-Path -Path $FilePath -Parent
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
}

function To-Evidence {
    param(
        [string[]]$Matches,
        [string]$Fallback
    )

    if ($Matches -and $Matches.Count -gt 0) {
        if ($Matches.Count -eq 1) {
            return $Matches[0]
        }

        return "$($Matches[0]) (+$($Matches.Count - 1) more)"
    }

    return $Fallback
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\\..")).Path
Set-Location $repoRoot

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $OutputPath = Join-Path $repoRoot "Results\\code_cleanup_audit_$stamp.md"
} elseif (-not [System.IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repoRoot $OutputPath
}

Ensure-Directory -FilePath $OutputPath

$checks = @(
    @{
        Name = "Placeholder Assertions (High)"
        Severity = "High"
        Pattern = "Assert\.True\(true\)|Assert\.False\(false\)"
        Paths = @("FenBrowser.Tests")
        ExtraArgs = @()
    },
    @{
        Name = "Silent Catch Blocks (Medium)"
        Severity = "Medium"
        Pattern = "catch\s*\{\s*\}"
        Paths = @("FenBrowser.Core", "FenBrowser.FenEngine", "FenBrowser.Host", "FenBrowser.WebDriver")
        ExtraArgs = @("--glob", "!**/*.g.cs")
    },
    @{
        Name = "Site-Specific Markers In Engine Paths (Medium)"
        Severity = "Medium"
        Pattern = "google\.com|youtube\.com|twitter\.com|x\.com"
        Paths = @("FenBrowser.Core", "FenBrowser.FenEngine", "FenBrowser.Host", "FenBrowser.WebDriver")
        ExtraArgs = @("--glob", "!**/*.g.cs")
    },
    @{
        Name = "Stub / Placeholder Markers (Low)"
        Severity = "Low"
        Pattern = "TODO|HACK|placeholder|stub|not implemented"
        Paths = @("FenBrowser.Core", "FenBrowser.FenEngine", "FenBrowser.Host", "FenBrowser.WebDriver", "FenBrowser.Tests")
        ExtraArgs = @("--glob", "!**/*.g.cs")
    }
)

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add("# FenBrowser Code Cleanup Audit")
$lines.Add("")
$generatedAt = Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"
$lines.Add("Generated: $generatedAt")
$lines.Add("Repository: $repoRoot")
$lines.Add("")

$highFindings = 0

foreach ($check in $checks) {
    $matches = @(Invoke-Ripgrep -Pattern $check.Pattern -Paths $check.Paths -ExtraArgs $check.ExtraArgs)
    $count = $matches.Count

    if ($check.Severity -eq "High") {
        $highFindings += $count
    }

    $lines.Add("## $($check.Name)")
    $lines.Add("")
    $lines.Add("- Severity: $($check.Severity)")
    $lines.Add("- Match count: $count")
    $lines.Add("")

    if ($count -gt 0) {
        $lines.Add('```text')
        foreach ($match in $matches) {
            $lines.Add($match)
        }
        $lines.Add('```')
        $lines.Add("")
    } else {
        $lines.Add("No matches.")
        $lines.Add("")
    }
}

$section4Checks = New-Object System.Collections.Generic.List[object]

$layoutGeometryMatches = @(Invoke-Ripgrep `
    -Pattern "Assert\.(Equal|InRange)\([^)]*(Left|Top|Width|Height|X|Y|Bounds)" `
    -Paths @("FenBrowser.Tests\Layout", "FenBrowser.Tests\Rendering") `
    -ExtraArgs @("--glob", "*.cs"))
$section4Checks.Add([PSCustomObject]@{
    Id = "4.11"
    Name = "Layout box positions/sizes are tested where relevant."
    Severity = "High"
    Passed = ($layoutGeometryMatches.Count -gt 0)
    Evidence = To-Evidence -Matches $layoutGeometryMatches -Fallback "No layout geometry assertions found."
}) | Out-Null

$paintOrderMatches = @(Invoke-Ripgrep `
    -Pattern "paint order|paint tree|display-list|display list" `
    -Paths @("FenBrowser.Tests") `
    -ExtraArgs @("--glob", "*.cs"))
$section4Checks.Add([PSCustomObject]@{
    Id = "4.12"
    Name = "Paint order or display-list output is tested where relevant."
    Severity = "High"
    Passed = ($paintOrderMatches.Count -gt 0)
    Evidence = To-Evidence -Matches $paintOrderMatches -Fallback "No paint-order/display-list assertions found."
}) | Out-Null

$reducedFixtures = @(Get-ChildItem -Path "FenBrowser.Tests\Fixtures\Reduced" -Recurse -File -Include *.html -ErrorAction SilentlyContinue)
$section4Checks.Add([PSCustomObject]@{
    Id = "4.13"
    Name = "At least one reduced HTML fixture exists."
    Severity = "High"
    Passed = ($reducedFixtures.Count -gt 0)
    Evidence = if ($reducedFixtures.Count -gt 0) { (Resolve-Path -Relative $reducedFixtures[0].FullName) } else { "No reduced HTML fixture found under FenBrowser.Tests/Fixtures/Reduced." }
}) | Out-Null

$siteSpecificMatches = @(Invoke-Ripgrep `
    -Pattern "google\.com|youtube\.com|twitter\.com|x\.com" `
    -Paths @("FenBrowser.Core", "FenBrowser.FenEngine", "FenBrowser.Host", "FenBrowser.WebDriver") `
    -ExtraArgs @("--glob", "!**/*.g.cs"))
$siteSpecificAllowlist = @(
    "FenBrowser.Host\Program.cs",
    "FenBrowser.Host\Widgets\SettingsPageWidget.cs",
    "FenBrowser.Core\BrowserSettings.cs",
    "FenBrowser.Core\Network\Handlers\TrackingPreventionHandler.cs",
    "FenBrowser.Core\Network\Handlers\AdBlockHandler.cs",
    "FenBrowser.FenEngine\Rendering\CustomHtmlEngine.cs",
    "FenBrowser.FenEngine\Rendering\BrowserApi.cs",
    "FenBrowser.FenEngine\Scripting\JavaScriptEngine.cs"
)
$unexpectedSiteSpecificMatches = @()
foreach ($match in $siteSpecificMatches) {
    $isAllowlisted = $false
    foreach ($allow in $siteSpecificAllowlist) {
        if ($match.StartsWith($allow + ":", [StringComparison]::OrdinalIgnoreCase)) {
            $isAllowlisted = $true
            break
        }
    }

    if (-not $isAllowlisted) {
        $unexpectedSiteSpecificMatches += $match
    }
}
$section4Checks.Add([PSCustomObject]@{
    Id = "4.14"
    Name = "The change does not rely on undocumented Google/X/YouTube-specific behavior."
    Severity = "Medium"
    Passed = ($unexpectedSiteSpecificMatches.Count -eq 0)
    Evidence = if ($unexpectedSiteSpecificMatches.Count -eq 0) { "All site-specific markers are confined to allowlisted compatibility/config surfaces ($($siteSpecificMatches.Count) match(es))." } else { To-Evidence -Matches $unexpectedSiteSpecificMatches -Fallback "" }
}) | Out-Null

$domMutationMatches = @(Invoke-Ripgrep `
    -Pattern "DomMutation|MutationObserver|appendChild|removeChild|insertBefore|replaceChild" `
    -Paths @("FenBrowser.Tests\DOM", "FenBrowser.Tests\Engine", "FenBrowser.Tests\Interaction") `
    -ExtraArgs @("--glob", "*.cs"))
$section4Checks.Add([PSCustomObject]@{
    Id = "4.15"
    Name = "DOM mutation behavior is tested."
    Severity = "High"
    Passed = ($domMutationMatches.Count -gt 0)
    Evidence = To-Evidence -Matches $domMutationMatches -Fallback "No DOM mutation coverage markers found."
}) | Out-Null

$eventOrderMatches = @(Invoke-Ripgrep `
    -Pattern "Assert\.Equal\(new\[\].*order|event order|dispatch order|mutation-observer|RAF" `
    -Paths @("FenBrowser.Tests\Engine", "FenBrowser.Tests\Interaction") `
    -ExtraArgs @("--glob", "*.cs"))
$section4Checks.Add([PSCustomObject]@{
    Id = "4.16"
    Name = "Event order is tested where events are involved."
    Severity = "High"
    Passed = ($eventOrderMatches.Count -gt 0)
    Evidence = To-Evidence -Matches $eventOrderMatches -Fallback "No explicit event-order assertions found."
}) | Out-Null

$silentCatchMatches = @(Invoke-Ripgrep `
    -Pattern "catch\s*\{\s*\}" `
    -Paths @("FenBrowser.Core", "FenBrowser.FenEngine", "FenBrowser.Host", "FenBrowser.WebDriver") `
    -ExtraArgs @("--glob", "!**/*.g.cs"))
$section4Checks.Add([PSCustomObject]@{
    Id = "4.17"
    Name = "Exceptions are surfaced, not swallowed silently."
    Severity = "High"
    Passed = ($silentCatchMatches.Count -eq 0)
    Evidence = if ($silentCatchMatches.Count -eq 0) { "No empty catch blocks found." } else { To-Evidence -Matches $silentCatchMatches -Fallback "" }
}) | Out-Null

$unsupportedFailureMatches = @(Invoke-Ripgrep `
    -Pattern "Assert\.Throws<\s*(NotSupportedException|PlatformNotSupportedException)\s*>" `
    -Paths @("FenBrowser.Tests") `
    -ExtraArgs @("--glob", "*.cs"))
$section4Checks.Add([PSCustomObject]@{
    Id = "4.18"
    Name = "Unsupported APIs fail honestly."
    Severity = "High"
    Passed = ($unsupportedFailureMatches.Count -gt 0)
    Evidence = To-Evidence -Matches $unsupportedFailureMatches -Fallback "No unsupported-API throw assertions found."
}) | Out-Null

$engineStatusPath = Join-Path $repoRoot "ENGINE_STATUS.md"
$engineStatus = ""
if (Test-Path $engineStatusPath) {
    $engineStatus = Get-Content -Path $engineStatusPath -Raw
}

$section4Checks.Add([PSCustomObject]@{
    Id = "4.19"
    Name = "Claims are marked experimental/partial when appropriate."
    Severity = "Medium"
    Passed = (-not [string]::IsNullOrWhiteSpace($engineStatus) -and $engineStatus -match "\|\s+.*\|\s+(Experimental|Partial|Experimental subset|Usable subset|Mostly complete)\s+\|")
    Evidence = if (-not [string]::IsNullOrWhiteSpace($engineStatus) -and $engineStatus -match "\|\s+.*\|\s+(Experimental|Partial|Experimental subset|Usable subset|Mostly complete)\s+\|") { "ENGINE_STATUS.md status table includes conservative labels." } else { "ENGINE_STATUS.md is missing conservative status labels." }
}) | Out-Null

$section4Checks.Add([PSCustomObject]@{
    Id = "4.20"
    Name = "Tests or current limitations are referenced."
    Severity = "High"
    Passed = (-not [string]::IsNullOrWhiteSpace($engineStatus) -and
        $engineStatus -match "\|\s*Area\s*\|\s*Status\s*\|\s*Evidence\s*\|\s*Known limitations\s*\|" -and
        $engineStatus -match "Known limitations")
    Evidence = if (-not [string]::IsNullOrWhiteSpace($engineStatus) -and
        $engineStatus -match "\|\s*Area\s*\|\s*Status\s*\|\s*Evidence\s*\|\s*Known limitations\s*\|" -and
        $engineStatus -match "Known limitations") { "ENGINE_STATUS.md includes Evidence and Known limitations columns." } else { "ENGINE_STATUS.md is missing evidence/limitations references." }
}) | Out-Null

$marketingMatches = @(Invoke-Ripgrep `
    -Pattern "Fully spec-compliant browser|Production-ready secure browser|Supports modern web standards" `
    -Paths @("docs", "ENGINE_STATUS.md", "CONTRIBUTING_ENGINE.md", "QUIRKS.md", "TESTING.md", "RISK_REGISTER.md") `
    -ExtraArgs @("--glob", "!docs/rules/code_cleanup.md"))
$section4Checks.Add([PSCustomObject]@{
    Id = "4.21"
    Name = "Marketing language is removed or softened."
    Severity = "Medium"
    Passed = ($marketingMatches.Count -eq 0)
    Evidence = if ($marketingMatches.Count -eq 0) { "No banned marketing claims found in canonical docs." } else { To-Evidence -Matches $marketingMatches -Fallback "" }
}) | Out-Null

$lines.Add("## Definition Of Done Progress (Section 4.11-4.21)")
$lines.Add("")
$lines.Add("| Item | Severity | Result | Evidence |")
$lines.Add("|---|---|---|---|")

$section4HighFailures = 0
foreach ($entry in $section4Checks) {
    $result = if ($entry.Passed) { "PASS" } else { "FAIL" }
    $lines.Add("| $($entry.Id) | $($entry.Severity) | $result | $($entry.Evidence -replace '\|', '\\|') |")

    if ($entry.Severity -eq "High" -and -not $entry.Passed) {
        $section4HighFailures++
    }
}
$lines.Add("")

$highFindings += $section4HighFailures

$lines.Add("## Large Files (God File Risk Heuristic)")
$lines.Add("")
$lines.Add("- Heuristic: top 15 non-generated C# files by line count in engine/host/core/webdriver")
$lines.Add("")
$lines.Add("| Lines | File |")
$lines.Add("|---:|---|")

$largeFiles = Get-ChildItem -Path "FenBrowser.Core", "FenBrowser.FenEngine", "FenBrowser.Host", "FenBrowser.WebDriver" -Recurse -File -Include *.cs |
    Where-Object { $_.FullName -notmatch "\\bin\\|\\obj\\" -and $_.Name -notlike "*.g.cs" } |
    ForEach-Object {
        $count = (Get-Content -Path $_.FullName | Measure-Object -Line).Lines
        [PSCustomObject]@{
            Lines = $count
            File = Resolve-Path -Relative $_.FullName
        }
    } |
    Sort-Object -Property Lines -Descending |
    Select-Object -First 15

foreach ($file in $largeFiles) {
    $lines.Add("| $($file.Lines) | $($file.File) |")
}
$lines.Add("")

[System.IO.File]::WriteAllLines($OutputPath, $lines)
Write-Host "[cleanup-audit] Wrote report: $OutputPath"

if ($FailOnHigh -and $highFindings -gt 0) {
    Write-Error "[cleanup-audit] High-severity findings detected: $highFindings"
    exit 1
}

Write-Host "[cleanup-audit] Done."
