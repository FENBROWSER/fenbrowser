# Contributing to FenBrowser Engine

Last updated: 2026-05-05

This guide defines how to land maintainable browser-engine changes in FenBrowser.

## Working Contract

1. Fix one behavior per change.
2. Identify the broken pipeline stage before coding.
3. Reproduce first, then patch the earliest broken stage.
4. Keep scope local; do not refactor unrelated systems.
5. Add or update focused tests for every behavior change.
6. Do not add site-specific hacks in core execution paths.
7. Document partial support honestly.

## Pipeline Ownership

Assign each bug/feature to one primary stage:

- navigation/loader
- HTML parser
- DOM
- CSS parser/cascade/computed style
- layout tree/layout
- paint/raster
- hit testing/events
- JavaScript/DOM APIs
- host integration

If stage is unclear, investigate before implementing.

## Required Change Shape

For each patch:

1. State the bug in one sentence.
2. Name the affected stage.
3. Add minimal repro test (or update nearest focused test).
4. Apply smallest safe fix.
5. Run smallest verification slice that can fail the change.
6. Update canonical docs when behavior/architecture contracts change.

## Forbidden Patterns

- `Assert.True(true)` or equivalent fake tests
- silent error swallowing that hides failures
- broad rewrites for narrow bugs
- compatibility branches keyed to a specific site inside core logic
- unsupported feature claims without evidence

## Verification Baseline

Minimum per code change:

```powershell
dotnet build <touched-project>.csproj -v minimal
dotnet test FenBrowser.Tests/FenBrowser.Tests.csproj --filter "FullyQualifiedName~<affected-slice>" -v minimal
```

Useful governance/audit checks:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/validate_spec_headers.ps1
powershell -ExecutionPolicy Bypass -File scripts/ci/run-code-cleanup-audit.ps1
```

## Documentation Sync

Update canonical docs when contracts or behavior change:

- Core -> `docs/VOLUME_II_CORE.md`
- FenEngine -> `docs/VOLUME_III_FENENGINE.md`
- Host -> `docs/VOLUME_IV_HOST.md`
- DevTools -> `docs/VOLUME_V_DEVTOOLS.md`
- WebDriver/tests/tooling -> `docs/VOLUME_VI_EXTENSIONS_VERIFICATION.md`

Also update, when relevant:

- `docs/COMPLIANCE.md`
- `docs/DEFINITION_OF_DONE.md`
- `docs/GLOSSARY.md`
- `docs/THIRD_PARTY_DEPENDENCIES.md`

## Commit and PR Discipline

1. Keep commits scoped and reviewable.
2. Stage only files tied to the accepted task.
3. Include root cause + verification in PR/commit summary.
4. Do not merge unresolved blocking test failures.
