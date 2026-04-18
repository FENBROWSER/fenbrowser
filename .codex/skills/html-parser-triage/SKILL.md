---
name: html-parser-triage
description: Triage HTML parser issues by isolating tokenization/tree-construction failures with minimal scope.
---

# Skill: html-parser-triage

Use when:
- DOM structure is incorrect
- nodes are missing/misplaced
- a site failure suggests tokenizer/tree-builder issues
- raw source and DOM dump do not match

## Goal

Find the first structural divergence and fix the owning parser stage.

## Workflow

1. Compare raw source to parsed DOM/tree output.
2. Find the first divergence, not the noisiest later symptom.
3. Classify the issue:
   - tokenization
   - insertion mode / tree construction
   - attribute handling
   - raw-text/RCDATA/script/style handling
   - parser error recovery
4. Inspect the smallest relevant parser files.
5. Patch the earliest broken rule.
6. Verify with:
   - smallest repro HTML if possible
   - focused parser/tree-builder tests if available
   - then broader slice only if needed

## Do not

- patch layout if DOM is already wrong
- use site-specific string hacks unless explicitly requested
- rewrite parser subsystems for a localized bug

## Output contract

1. first divergence
2. owning subsystem
3. minimal correction
4. verification

## Token discipline

- quote only the tiny source fragment that matters
- keep explanation tied to the first divergence
