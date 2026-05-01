param(
    [string]$InventoryPath = "C:\Users\udayk\Downloads\html_css_spec_inventory_2026-05-01.md",
    [string]$ElementCoverageTestPath = "FenBrowser.Tests/Engine/HtmlElementInterfaceCoverageTests.cs",
    [string]$AttributeCoverageTestPath = "FenBrowser.Tests/Engine/HtmlAttributeInventoryCoverageTests.cs",
    [string]$EventCoverageTestPath = "FenBrowser.Tests/Engine/HtmlEventHandlerInventoryCoverageTests.cs",
    [string]$CatalogPath = "FenBrowser.Core/Dom/V2/HtmlElementInterfaceCatalog.cs",
    [string]$FenRuntimePath = "FenBrowser.FenEngine/Core/FenRuntime.cs",
    [string]$ElementWrapperPath = "FenBrowser.FenEngine/DOM/ElementWrapper.cs",
    [string]$OutputPath = "Results/html_inventory_coverage_2026-05-01.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-PathExists {
    param([string]$PathToCheck, [string]$Label)
    if (-not (Test-Path -LiteralPath $PathToCheck)) {
        throw "$Label not found: $PathToCheck"
    }
}

function Get-SectionItems {
    param(
        [string[]]$Lines,
        [string]$StartPattern,
        [string]$EndPattern,
        [string]$ItemPattern
    )

    $collect = $false
    $items = New-Object System.Collections.Generic.List[string]

    foreach ($line in $Lines) {
        if (-not $collect -and $line -match $StartPattern) {
            $collect = $true
            continue
        }

        if ($collect -and $line -match $EndPattern) {
            break
        }

        if ($collect -and $line -match $ItemPattern) {
            $items.Add($matches[1].Trim().ToLowerInvariant())
        }
    }

    return $items
}

function Get-CSharpArrayItems {
    param(
        [string]$Path,
        [string]$FieldName
    )

    $lines = Get-Content -LiteralPath $Path
    $collect = $false
    $items = New-Object System.Collections.Generic.List[string]

    foreach ($line in $lines) {
        if (-not $collect -and $line -match [regex]::Escape($FieldName) -and $line -match "=") {
            $collect = $true
            continue
        }

        if (-not $collect) {
            continue
        }

        foreach ($m in [regex]::Matches($line, '"([^"]+)"')) {
            $items.Add($m.Groups[1].Value.Trim().ToLowerInvariant())
        }

        if ($line -match '};') {
            break
        }
    }

    return $items
}

function Get-CSharpMapKeys {
    param([string]$Path)
    $text = Get-Content -LiteralPath $Path -Raw
    $keys = [regex]::Matches($text, '\["([^"]+)"\]\s*=')
    return $keys | ForEach-Object { $_.Groups[1].Value.Trim().ToLowerInvariant() }
}

function Get-CSharpEventListItems {
    param(
        [string]$Path,
        [string]$FieldName
    )

    $lines = Get-Content -LiteralPath $Path
    $collect = $false
    $items = New-Object System.Collections.Generic.List[string]

    foreach ($line in $lines) {
        if (-not $collect -and $line -match [regex]::Escape($FieldName)) {
            $collect = $true
            continue
        }

        if (-not $collect) {
            continue
        }

        foreach ($m in [regex]::Matches($line, '"(on[a-z0-9]+)"')) {
            $items.Add($m.Groups[1].Value.Trim().ToLowerInvariant())
        }

        if ($line -match '};') {
            break
        }
    }

    return $items
}

Assert-PathExists -PathToCheck $InventoryPath -Label "Inventory file"
Assert-PathExists -PathToCheck $ElementCoverageTestPath -Label "Element coverage test"
Assert-PathExists -PathToCheck $AttributeCoverageTestPath -Label "Attribute coverage test"
Assert-PathExists -PathToCheck $EventCoverageTestPath -Label "Event coverage test"
Assert-PathExists -PathToCheck $CatalogPath -Label "Element interface catalog"
Assert-PathExists -PathToCheck $FenRuntimePath -Label "FenRuntime source"
Assert-PathExists -PathToCheck $ElementWrapperPath -Label "ElementWrapper source"

$inventoryLines = Get-Content -LiteralPath $InventoryPath

$inventoryElements = Get-SectionItems -Lines $inventoryLines `
    -StartPattern '^### 113 named HTML elements' `
    -EndPattern '^### Additional element-index rows in WHATWG' `
    -ItemPattern '^- `([^`]+)`'

$inventoryNonEventAttributes = Get-SectionItems -Lines $inventoryLines `
    -StartPattern '^### 144 unique non-event attributes' `
    -EndPattern '^### 89 event handler content attributes' `
    -ItemPattern '^- `([^`]+)`'

$inventoryEventHandlers = Get-SectionItems -Lines $inventoryLines `
    -StartPattern '^### 89 event handler content attributes' `
    -EndPattern '^## CSS implementation guidance' `
    -ItemPattern '^- `([^`]+)`'

$testElements = Get-CSharpArrayItems -Path $ElementCoverageTestPath -FieldName 'InventoryNamedHtmlElements_2026_05_01'
$testNonEventAttributes = Get-CSharpArrayItems -Path $AttributeCoverageTestPath -FieldName 'InventoryNonEventHtmlAttributes_2026_05_01'
$testEventHandlers = Get-CSharpArrayItems -Path $EventCoverageTestPath -FieldName 'InventoryEventHandlerAttributes_2026_05_01'

$catalogTags = Get-CSharpMapKeys -Path $CatalogPath

$runtimeWindowHandlers = Get-CSharpEventListItems -Path $FenRuntimePath -FieldName 's_windowDefaultEventHandlerNames'
$runtimeElementHandlers = Get-CSharpEventListItems -Path $ElementWrapperPath -FieldName 'GlobalEventHandlerProperties'
$runtimeLegacyForwardedHandlers = Get-CSharpEventListItems -Path $ElementWrapperPath -FieldName 'LegacyBodyFrameSetForwardedHandlers'

$inventoryElementsSet = $inventoryElements | Sort-Object -Unique
$inventoryNonEventSet = $inventoryNonEventAttributes | Sort-Object -Unique
$inventoryEventsSet = $inventoryEventHandlers | Sort-Object -Unique

$testElementsSet = $testElements | Sort-Object -Unique
$testNonEventSet = $testNonEventAttributes | Sort-Object -Unique
$testEventsSet = $testEventHandlers | Sort-Object -Unique

$catalogTagSet = $catalogTags | Sort-Object -Unique

$runtimeEventUnion = $runtimeWindowHandlers + $runtimeElementHandlers + $runtimeLegacyForwardedHandlers | Sort-Object -Unique

$result = [ordered]@{
    generated_at_utc = (Get-Date).ToUniversalTime().ToString("o")
    inventory_path = (Resolve-Path -LiteralPath $InventoryPath).Path
    counts = [ordered]@{
        inventory_elements = $inventoryElementsSet.Count
        inventory_non_event_attributes = $inventoryNonEventSet.Count
        inventory_event_handlers = $inventoryEventsSet.Count
        test_elements = $testElementsSet.Count
        test_non_event_attributes = $testNonEventSet.Count
        test_event_handlers = $testEventsSet.Count
        catalog_tags = $catalogTagSet.Count
        runtime_event_union = $runtimeEventUnion.Count
    }
    elements = [ordered]@{
        missing_in_tests = @($inventoryElementsSet | Where-Object { $_ -notin $testElementsSet })
        extra_in_tests = @($testElementsSet | Where-Object { $_ -notin $inventoryElementsSet })
        missing_in_catalog = @($inventoryElementsSet | Where-Object { $_ -notin $catalogTagSet })
    }
    non_event_attributes = [ordered]@{
        missing_in_tests = @($inventoryNonEventSet | Where-Object { $_ -notin $testNonEventSet })
        extra_in_tests = @($testNonEventSet | Where-Object { $_ -notin $inventoryNonEventSet })
    }
    event_handlers = [ordered]@{
        missing_in_tests = @($inventoryEventsSet | Where-Object { $_ -notin $testEventsSet })
        extra_in_tests = @($testEventsSet | Where-Object { $_ -notin $inventoryEventsSet })
        missing_in_runtime_registration = @($inventoryEventsSet | Where-Object { $_ -notin $runtimeEventUnion })
    }
}

$outputDir = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDir) -and -not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Path $outputDir | Out-Null
}

$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$result | ConvertTo-Json -Depth 8
