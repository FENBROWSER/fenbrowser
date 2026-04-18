# AGENTS.md — FenBrowser Root Guide

This file defines the default operating rules for agents working in FenBrowser.

## Mission

Work on FenBrowser with:
- minimal scope
- root-cause fixes
- production-grade behavior
- deterministic verification
- low-regression changes
- compact communication

The browser may be referred to as `fen` or `fenbrowser`.

## Repository shape

Primary projects:
- `FenBrowser.Core` — DOM, parsing, resource/network primitives, shared browser types
- `FenBrowser.FenEngine` — CSS, layout, rendering, scripting, runtime pipeline
- `FenBrowser.Host` — windowing, input, host integration, OS boundary
- `FenBrowser.DevTools` — native devtools UI and remote debugging protocol

Other important surfaces:
- `FenBrowser.WebDriver`
- `FenBrowser.Tests`
- `FenBrowser.Test262`
- tooling and scripts under repo root / `scripts/`

Default behavior:
- inspect the smallest relevant project first
- do not scan all projects unless the task is cross-cutting
- do not start in tests/conformance projects unless verification or test work is the task

## Non-negotiable rules

1. Reproduce before fixing.
2. Prefer root cause over symptom patch.
3. Keep changes minimal and local.
4. Do not refactor unrelated code.
5. Do not widen scope without evidence.
6. Build or verify the smallest affected surface first.
7. If behavior or architecture meaningfully changes, update the relevant canonical docs in the same change.
8. Do not use platform-specific APIs outside host/platform layers unless the task is explicitly platform work.
9. Treat native resource ownership as critical. Dispose correctly.
10. Keep replies compact unless a deep dive is requested.

## Canonical documentation set

Use only these docs as the authoritative documentation map:

- `docs/VOLUME_I_SYSTEM_MANIFEST.md`
- `docs/VOLUME_II_CORE.md`
- `docs/VOLUME_III_FENENGINE.md`
- `docs/VOLUME_IV_HOST.md`
- `docs/VOLUME_V_DEVTOOLS.md`
- `docs/VOLUME_VI_EXTENSIONS_VERIFICATION.md`
- `docs/COMPLIANCE.md`
- `docs/DEFINITION_OF_DONE.md`
- `docs/GLOSSARY.md`
- `docs/INDEX.md`
- `docs/THIRD_PARTY_DEPENDENCIES.md`

Do not assume legacy `docs/TechnicalReference/`, `Part_*`, or `Volume_*_Tree.md` structures are the active documentation contract.

## Documentation synchronization rule

Documentation is required when a change affects:
- architecture or subsystem boundaries
- observable runtime behavior
- diagnostics or operator workflow
- verification/test workflow
- compliance/spec-status understanding
- terminology
- third-party dependency inventory or rationale

Small localized fixes do not require doc churn unless they change how the subsystem should be understood.

Mapping:
- Core changes -> `VOLUME_II_CORE.md`
- FenEngine changes -> `VOLUME_III_FENENGINE.md`
- Host changes -> `VOLUME_IV_HOST.md`
- DevTools changes -> `VOLUME_V_DEVTOOLS.md`
- WebDriver / tests / verification / tooling -> `VOLUME_VI_EXTENSIONS_VERIFICATION.md`

Additional appendices only when relevant:
- compliance/spec planning -> `COMPLIANCE.md`
- merge/review gates -> `DEFINITION_OF_DONE.md`
- terminology -> `GLOSSARY.md`
- dependency surface -> `THIRD_PARTY_DEPENDENCIES.md`
- docs navigation/indexing -> `INDEX.md`

## Working style

Default sequence:
1. Restate the bug/task in one sentence internally.
2. Identify the owning subsystem.
3. Inspect only likely files first.
4. Reproduce or verify the failure.
5. Patch the earliest broken stage.
6. Run the smallest meaningful verification.
7. Update docs only if required.
8. Summarize:
   - root cause
   - files changed
   - verification
   - docs touched

Avoid:
- broad repo tours
- speculative rewrites
- architecture changes without evidence
- repeating the user prompt
- long educational explanations unless requested

## Debug and artifact workflow

For runtime/rendering issues, prefer this diagnostic order:
1. `logs/debug_screenshot.png`
2. `logs/raw_source_*.html`
3. `logs/dom_dump.txt`
4. `logs/fenbrowser_*.log`

Default clean-state workflow:
1. kill existing FenBrowser-related processes
2. clear relevant logs/artifacts
3. build/run cleanly
4. allow startup/navigation enough time to settle
5. inspect screenshot + DOM/log artifacts before patching

Diagnostics path policy (strict):
- Runtime artifacts must be written under workspace-root `logs/` only.
- Do not write diagnostics to repository root.
- Do not write diagnostics to `docs/`.
- Applies to generated `.txt`, `.png`, `.png.meta`, `.js`, `.html` artifacts.

Result/report policy:
- Use workspace-root `Results/` for generated reports and result bundles.
- Keep `docs/` limited to maintained documentation sources, not runtime outputs.

## Threading boundaries

UI thread only:
- widgets
- window management
- UI invalidation
- host/UI event handling

Render thread / engine-owned execution only:
- box/layout computation
- paint preparation
- rendering pipeline work

Queued/microtask/event-loop work:
- use the established coordinator/event-loop surfaces
- do not block hot paths with ad hoc synchronous work

## Resource and safety discipline

- Use `using` / deterministic disposal for native Skia and similar native-backed objects.
- Avoid unnecessary allocations in hot paths.
- Prefer pooling/reuse only when it simplifies hot-path cost without obscuring correctness.
- Never let a drawing library define layout semantics.
- Keep state flow explicit; avoid hidden globals.
- Keep Core/FenEngine/WebDriver logic platform-agnostic unless the task is explicitly platform work.

## Terminology contract

Use project terms consistently:
- Box / LayoutBox
- Box Tree
- Paint Tree
- Bridge
- Host
- DOM
- Node / Element / ContainerNode
- Microtask

Avoid inventing alternate names for established subsystem concepts.

## Verification policy

Choose the smallest verification that can falsify the change.

Typical order:
- focused build of touched project
- focused test slice for the changed path
- broader slice only if the change crosses subsystem boundaries
- full solution build/test only when necessary

Do not run large suites by default when a smaller proof is sufficient.

## Output contract for agent responses

Default response format:
1. Root cause
2. Files inspected/changed
3. Minimal patch plan or applied fix
4. Verification command/result
5. Docs updated or not needed

Keep the response brief and technical.
Do not restate obvious context.
Do not add background unless it materially helps the next action.
