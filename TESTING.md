# FenBrowser Testing Guide

Last updated: 2026-05-05

This runbook focuses on deterministic, minimal-cost verification.

## Principles

1. Verify the smallest affected surface first.
2. Prefer focused tests over whole-suite runs.
3. Use large suites only when a change is cross-cutting.
4. Keep runtime artifacts in `logs/`.
5. Keep result bundles/reports in `Results/`.

## Prerequisites

- .NET SDK from `global.json`
- PowerShell (Windows workflow)
- Clean enough workspace for scoped staging/verification

## Fast Verification Workflow

1. Build touched project.
2. Run nearest focused test slice.
3. Run wider slice only if impacted boundaries crossed.

Example:

```powershell
dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -v minimal
dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~Layout|FullyQualifiedName~Cascade" -v minimal
```

## Common Commands

Focused builds:

```powershell
dotnet build FenBrowser.Core/FenBrowser.Core.csproj -v minimal
dotnet build FenBrowser.FenEngine/FenBrowser.FenEngine.csproj -v minimal
dotnet build FenBrowser.Host/FenBrowser.Host.csproj -v minimal
dotnet build FenBrowser.WebDriver/FenBrowser.WebDriver.csproj -v minimal
dotnet build FenBrowser.Tests/FenBrowser.Tests.csproj -v minimal
```

Focused test slice:

```powershell
dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~<TestClassOrArea>" -v minimal
```

Cleanup/governance checks:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/ci/run-code-cleanup-audit.ps1
powershell -ExecutionPolicy Bypass -File scripts/validate_spec_headers.ps1
powershell -ExecutionPolicy Bypass -File scripts/ci/verify-verification-guards.ps1
```

Staged baseline bundle:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/run_stage_recovery_baseline.ps1
```

## Runtime Debug Artifacts

For render/runtime investigation, inspect in this order:

1. `logs/debug_screenshot.png`
2. `logs/raw_source_*.html`
3. `logs/dom_dump.txt`
4. `logs/fenbrowser_*.log`

Default clean-state cycle:

1. Stop running FenBrowser processes.
2. Clear stale logs/artifacts for the repro.
3. Build/run cleanly.
4. Let navigation settle.
5. Inspect artifacts before patching.

## When to Run Larger Suites

Use broader runs only if needed:

- change spans multiple pipeline stages
- shared infra touched (event loop, scheduler, runtime boot, IPC)
- focused tests are insufficient to falsify regressions

For full-scope or scheduled verification, use CI/manual workflows and collect result bundles under `Results/`.
