# Skill: browser-diff-review

Use when:
- reviewing a patch or local diff
- checking regression risk before commit
- validating browser-engine safety

## Goal

Review for correctness, regression risk, architecture drift, resource/threading issues, and doc impact.

## Review checklist

- right layer touched?
- earliest broken stage fixed?
- hidden global state introduced?
- UI-thread / render-thread ownership preserved?
- native resource lifetime safe?
- platform-specific leakage into Core/FenEngine?
- logging hot-path blowup?
- excessive scope growth?
- docs update required?
- smallest meaningful verification run?

## Severity buckets

High:
- likely correctness regression
- thread/resource ownership violation
- wrong-layer patch
- contract break

Medium:
- unnecessary scope
- noisy logging / perf cost
- weak verification
- missing docs when needed

Low:
- style cleanup opportunities
- naming polish
- minor simplifications

## Output contract

1. high-risk findings
2. medium-risk findings
3. safe parts
4. required follow-up checks

## Token discipline

- review the diff, not the whole repo
- keep findings short and ranked