<#
.SYNOPSIS
Runs the bounded daily rendering contract loop with zero-match protection.

.DESCRIPTION
Exercises paint output, animation invalidation, compositor-only updates, image
repaint coalescing, late-frame policy, and brokered scroll/damage rasterization.
Every filter is routed through Invoke-TestFilters.ps1 so removed or renamed test
surfaces fail instead of producing a vacuous green run.
#>
[CmdletBinding()]
param(
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

$filters = @(
    'FullyQualifiedName~FenBrowser.Tests.Core.PaintTreePillRenderingContractTests',
    'FullyQualifiedName~FenBrowser.Tests.Rendering.Phase3AnimationClassificationTests',
    'FullyQualifiedName~FenBrowser.Tests.Rendering.Phase4CompositorOnlyPathTests',
    'FullyQualifiedName~FenBrowser.Tests.Rendering.Phase6ImageRepaintCoalescingTests',
    'FullyQualifiedName~FenBrowser.Tests.Rendering.Phase10WatchdogLateFramePolicyTests',
    'FullyQualifiedName~BrowserIntegration_RenderBrokeredFrame_AppliesCompositorScrollDelta',
    'FullyQualifiedName~Program_ComputeBrokeredFrameRasterHeight_AddsBottomScrollOverdraw',
    'FullyQualifiedName~RendererChildFramePattern_RasterizesScrolledDocumentBand',
    'FullyQualifiedName~ScrollOnlyDamage_RasterizesNewlyExposedDocumentBand'
)

$runner = Join-Path $PSScriptRoot 'Invoke-TestFilters.ps1'
$arguments = @{
    Project = 'FenBrowser.Tests/FenBrowser.Tests.csproj'
    Filters = $filters
    Configuration = 'Release'
}
if ($NoBuild) {
    $arguments.NoBuild = $true
}

& $runner @arguments
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host 'Daily rendering loop passed all paint/compositor/scroll contracts.'
exit 0
