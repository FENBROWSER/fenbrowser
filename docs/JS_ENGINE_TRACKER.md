# FenBrowser JavaScript Engine Tracker

Status: TESTED for the recorded local Test262 baseline; browser-host integration remains INTEGRATED. Snapshot date: 2026-07-15.

## Evidence baseline

- Local Test262 source of truth: `docs/test262_results.md`, generated 2026-07-05.
- Result: 49,070 passed of 53,198 tests, 92.24%.
- Category result: 1,490 of 1,767 categories at 100%; 277 categories below 100%.
- Real-site evidence: Google discovered 14 script elements and recorded 18 completed executions with no direct script or callback failure in `logs/real-site/www.google.com/20260715T085915Z/`.

The differing script discovery/execution populations are a diagnostic-semantics issue until source records prove otherwise. They are not evidence of a language failure.

## Capability status

| Area | Status | Current evidence | Next falsifiable check |
| --- | --- | --- | --- |
| Parser/compiler/interpreter | TESTED | Broad Test262 baseline and real-site execution | Work only from a current identical failure cluster or attributed site exception |
| Objects/prototypes/functions/classes | TESTED | Test262 coverage | Category rerun at 2-second per-test timeout when selected |
| Promise jobs/microtasks | TESTED | Google recorded 9 checkpoints; typed rejection diagnostics agree at zero in the current bundle | Add browser-host ordering and navigation-teardown reductions |
| Async/await/generators | TESTED | Included in Test262 aggregate | Do not prioritize without a current blocker cluster |
| Modules/dynamic import | IMPLEMENTED | Code paths exist | Fresh module-script WPT/local boot matrix |
| Realms/global host hooks | INTEGRATED | Browser scripts execute through FenEngine runtime | Window/document/global/teardown tests |
| Error and rejection reporting | TESTED | Timer/event/Promise records share typed source/task/receiver/stack data and drained bundle counts | Add microtask/rAF/navigation-invalidation bounds and export-failure reductions |
| JS/DOM wrapper identity and lifetime | BLOCKED_NEEDS_HUMAN_DECISION | Strong host table and identity cache observed | Accepted memory model and stress tests |

## Triage rule

A real-site exception enters this tracker only after source attribution proves the failing behavior is ECMAScript semantics. Missing host properties, wrong WebIDL conversion, DOM state, script ordering, task timing, network policy, and layout failures remain in their owning trackers.

Every Test262 run uses the local `C:\Users\udayk\Videos\test262` checkout, a 2-second per-test timeout, and a 30-second stall watchdog. Full-suite reruns are not the normal work loop.

## Next work

1. Preserve all browser callback exceptions with realm, script/source, task, and stack fields.
2. Add host-integration reductions for Promise jobs, timers, DCL/load ordering, and navigation teardown.
3. Select any language fix only from `docs/test262_results.md` plus the corresponding local batched failure details.
