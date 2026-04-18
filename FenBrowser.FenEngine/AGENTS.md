# AGENTS.md — FenBrowser.FenEngine

Use this file for CSS, cascade, layout, rendering, scripting, runtime orchestration, and engine-side interaction.

## Goal

Fix the earliest broken engine stage without papering over the issue in a later stage.

## Engine ownership

Typical areas:
- CSS parsing/cascade/computed style
- formatting context resolution
- layout / geometry
- paint-tree construction
- renderer preparation
- scripting/runtime behavior
- interaction and engine-side state changes

## Stage order

Always reason in this order:
1. source / DOM correctness
2. style/cascade/computed values
3. formatting context / layout
4. paint-tree generation
5. raster/render-only behavior

Patch the earliest broken stage.

## Rules

- Do not fix layout bugs in paint unless it is truly paint-only.
- Do not fix cascade bugs with site-specific hacks unless explicitly requested and clearly isolated.
- Do not widen host/platform coupling from engine code.
- Be careful with hot-path logging and allocations.
- Respect deterministic pipeline boundaries.

## Typical verification

- screenshot
- `dom_dump.txt`
- focused logs
- smallest targeted test slice
- only then broader regression slices if needed

## Output

1. symptom
2. earliest broken stage
3. root cause
4. minimal fix
5. verification
6. doc target if needed