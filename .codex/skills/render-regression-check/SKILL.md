# Skill: render-regression-check

Use when:
- something used to render correctly and now regressed
- there is a known recent diff/commit window
- a visual regression is available

## Goal

Find the smallest changed surface that explains the regression.

## Workflow

1. Define:
   - last known good behavior
   - current broken behavior
   - suspect file set / diff if available
2. Classify affected stage:
   - DOM
   - style/cascade
   - layout
   - paint tree
   - host composition
3. Inspect only changed files first.
4. Look for contract breaks across stage boundaries.
5. Fix the source-stage regression, not the last visible symptom.
6. Verify using the smallest deterministic repro.

## Do not

- scan unrelated files
- convert a regression fix into a broad cleanup
- skip before/after behavior description

## Output contract

1. regressed behavior
2. suspect surface
3. root cause
4. minimal rollback/fix strategy
5. verification

## Token discipline

- stay inside the diff unless evidence forces wider inspection