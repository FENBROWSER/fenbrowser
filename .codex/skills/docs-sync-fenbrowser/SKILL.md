---
name: docs-sync-fenbrowser
description: Update only affected canonical FenBrowser docs when behavior, architecture, or workflow changes.
---

# Skill: docs-sync-fenbrowser

Use when:
- code behavior changed meaningfully
- architecture changed
- diagnostics/verification workflow changed
- terminology/compliance/dependency docs may need updates

## Goal

Update only the canonical docs that are actually affected.

## Canonical targets

- `VOLUME_I_SYSTEM_MANIFEST.md`
- `VOLUME_II_CORE.md`
- `VOLUME_III_FENENGINE.md`
- `VOLUME_IV_HOST.md`
- `VOLUME_V_DEVTOOLS.md`
- `VOLUME_VI_EXTENSIONS_VERIFICATION.md`
- `COMPLIANCE.md`
- `DEFINITION_OF_DONE.md`
- `GLOSSARY.md`
- `INDEX.md`
- `THIRD_PARTY_DEPENDENCIES.md`

## Workflow

1. Decide whether docs are actually required.
2. Map the change to the owning volume.
3. Update only the affected section.
4. Keep entries factual:
   - scope
   - code/files
   - why it mattered
   - verification
5. Do not fan out into unrelated docs.

## Do not

- update docs for tiny local fixes that do not change understanding
- duplicate the same note across multiple volumes without need
- invent status claims not supported by code/verification

## Output contract

1. docs required or not
2. exact file(s) to update
3. exact section(s) to add/change
4. concise wording proposal

## Token discipline

- one owning volume first
- appendices only if directly impacted
