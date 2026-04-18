# Skill: css-layout-debug

Use when:
- visual layout differs from expected browser behavior
- spacing/alignment/block/inline/flex/grid behavior is wrong
- screenshot mismatch is reported

## Goal

Determine the earliest broken stage among:
- DOM/tree shape
- style/cascade/computed values
- formatting-context choice
- layout math
- paint-only output

## Workflow

1. Start from the visible symptom.
2. Check:
   - `debug_screenshot.png`
   - `dom_dump.txt`
   - relevant `logs/fenbrowser_*.log`
3. Decide whether the structure is already wrong before paint.
4. Inspect only the owning subsystem:
   - cascade/style
   - formatting context
   - layout engine
   - paint tree
5. Fix the earliest incorrect stage.
6. Verify with the narrowest build/test/screenshot path.

## Bias

- prefer root-cause fixes over paint hacks
- prefer generic engine correctness over site-specific patches
- only use a site-specific containment when the task explicitly requires it

## Do not

- scan the whole repo
- jump straight to paint code when layout is wrong
- treat text-only rendering issues as always paint issues

## Output contract

1. symptom
2. earliest broken stage
3. root cause
4. files to change
5. minimal patch
6. verification

## Token discipline

- do not narrate the full pipeline
- do not repeat user context
- answer with stage -> cause -> fix -> verify