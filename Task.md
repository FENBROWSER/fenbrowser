# Browser Engine Implementation Tasks

This task ledger is derived from [browser_engine_csharp_no_compromises_guide.md](browser_engine_csharp_no_compromises_guide.md) and refreshed against the current repo state on 2026-03-08.

Milestone sections below list only remaining work. Implemented baseline work stays in the status snapshot so the milestones remain focused on what is still open.

## Engine Laws

- Multi-process architecture is mandatory.
- Site and origin isolation must be the default.
- No ambient authority. Privileged actions require broker-issued capabilities.
- Spec-first algorithms for URL, Fetch, HTML, DOM, Encoding, MIME sniffing, CSS, and JS behavior.
- No half features. A feature stays off until conformance, fuzzing, security review, and perf gates are met.
- Production grade means correctness, security, and performance at the same time.

## Current Status Snapshot

- Milestone A baseline exists in code: OS-specific sandbox implementations, Renderer/Network/GPU/Utility process-role scaffolding, and an IPC fuzz baseline are present, but production hardening is still incomplete.
- Milestone A remains structurally incomplete because brokered isolation is opt-in and renderer-side network/file I/O paths still exist in the codebase, which breaks the intended no-ambient-authority boundary.
- Milestone B remains far from gate: `Results/test262_results_final.json` reports 529/1000 passed (52.9%).
- Milestone C remains open: `Results/wpt_results_latest.json` reports 31/100 passed (31%) for the current DOM/event pack.
- Latest Google real-page repro no longer corrupts inline `<script>` content into bogus DOM elements; raw-text parsing now keeps Google's font-loader/script bodies inside `SCRIPT` text nodes in `dom_dump.txt`.
- Latest Google real-page repro no longer reproduces the previous fatal interaction timeout during steady-state runtime after the event-dispatch execution-budget reset; focused regression `DispatchEvent_ResetsExpiredExecutionBudget` is green.
- WPT chunk-triage bootstrap regressions have been reduced: global `CSS`, WPT `on_event(...)`, permissions-policy helper parsing, and the bounded `navigator.serial.getPorts()` baseline are now present, and `serial/serial-default-permissions-policy.https.sub.html` is green.
- Milestone D now has dedicated artifacts: `Results/wpt_css_layout_baseline.json` and `Results/wpt_css_layout_results.json` both exist, but the latest bounded pack is still 0/6 passed.
- Milestone E now has dedicated artifacts: `Results/wpt_fetch_cors_baseline.json` and `Results/wpt_fetch_cors_results.json` both exist, but the latest bounded pack is still 0/5 passed.
- Milestone F has partial validation artifacts: `Results/corb_validation.json` passes its current 5-case sample, and `Results/a11y_platform_snapshot.json` shows valid snapshots for three platform mappings, but neither area is production complete.

## Immediate Active Blockers

- [ ] Eliminate long CSS stabilization stalls on real pages. Current evidence: Google run logged CSS parsing partially stalled after 20 seconds before style completion.
- [ ] Continue chunk-driven WPT recovery by removing the next repeated root-cause buckets. Current chunk-1 baseline is 460/1000 passed (46.0%); the first helper/bootstrap blockers (`on_event`, `CSS`, permissions-policy bootstrap, basic `navigator.serial`) are reduced, and the remaining visible clusters are unsupported platform APIs (`CloseWatcher`, clipboard, sensors), popup/window semantics, and runtime/property gaps.

## Cross-Cutting Gates

- [ ] Pin and maintain versioned conformance artifacts for Test262 and WPT packs used as milestone gates.
- [ ] Keep an expected-fail ledger with owner and rationale for every non-green gate.
- [ ] Add or update fuzz cases whenever parser, IPC, shared memory, URL, CSS, or high-risk utility parsing changes.
- [ ] Keep perf budget evidence for hot paths and frame-critical code.
- [ ] Require security review signoff for sandbox, privilege, IPC, unsafe, or native-resource changes.

## Milestone A - Secure Shell

Gate: sandbox verification, IPC fuzz baseline, and no privilege leaks.

- [ ] Remove `NullSandbox` fallback as an acceptable supported-host outcome. Supported hosts must fail closed when required sandbox helpers are unavailable.
- [ ] Make brokered multi-process launch the default production path for Renderer, Network, GPU, and Utility roles.
- [ ] Remove remaining renderer-side direct network and file I/O paths so privileged fetch and file access execute only through brokered host or target-process contracts.
- [ ] Enforce strict capability validation on every privileged IPC path, including site lock, permission state, and user-gesture requirements.
- [ ] Harden shared-memory data-plane handoff with immutable submission rules, strict validation, and endpoint-specific negative tests.
- [ ] Expand IPC fuzzing from baseline coverage to continuous endpoint and frame fuzzing across all process roles.
- [ ] Add explicit privilege-leak regression tests for clipboard, network, storage, and file-scope requests.

## Milestone B - JS Foundation + Bindings Seed

Gate: meaningful Test262 subset and deterministic semantics.

- [ ] Raise Test262 pass rate from the current 52.9% to a milestone-worthy baseline by finishing missing core builtins and semantic edge cases.
- [ ] Complete `Math`, `Date`, `RegExp`, `Map`, `Set`, iterator, descriptor, and prototype behavior needed by current failing tests.
- [ ] Implement generated WebIDL bindings with overload resolution, conversion rules, exception mapping, brand checks, and promise bridging.
- [ ] Replace handwritten DOM API exposure paths with generated IDL-backed bindings where the guide requires them.
- [ ] Establish a pinned Test262 commit, expected-fail ledger, and no-new-failures PR rule.
- [ ] Keep JS tiering limited to baseline VM plus correctness work until conformance and deopt infrastructure are stable enough to justify more JIT work.

## Milestone C - HTML Parsing + Event Loop

Gate: WPT parsing plus DOM/event subsets.

- [ ] Complete tokenizer and tree-builder behavior for hostile streaming input, raw-text elements, foster parenting, and other insertion-mode edge cases.
- [ ] Raise the current DOM/event WPT slice above the present 31/100 baseline and remove the largest failure clusters first.
- [ ] Eliminate `No assertions executed by testharness.` failures from the DOM/event pack before treating any pass-rate increase as meaningful.
- [ ] Fix event API runtime gaps and property descriptor behavior affecting named collections and Web IDL surface semantics.
- [ ] Bring microtask checkpoints, task queues, timers, and parser-event-loop ordering into spec-aligned behavior.
- [ ] Add repeatable regression packs for every historical DOM/event failure cluster and keep result artifacts versioned.

## Milestone D - CSS + Layout + Paint

Gate: WPT CSS/layout subsets and reftests for implemented features.

- [ ] Complete the CSS pipeline from tokenization through computed values with selector invalidation discipline that can scale.
- [ ] Implement arena allocation for style, layout, and display-list high-churn data as recommended by the guide.
- [ ] Finish block and inline layout correctness, dirty-bit invalidation, and incremental layout behavior.
- [ ] Integrate proper text shaping early instead of relying on simplistic text measurement paths.
- [ ] Turn the current CSS/layout pack from 0/6 into a real baseline by fixing harness completion, computed-style defaults, and canonical serialization behavior.
- [ ] Produce stable display-list and compositor output suitable for reftest comparison on implemented features.
- [ ] Add negative tests for layout invalidation, dirty-bit propagation, and render-tree parity regressions.

## Milestone E - Fetch, CORS, and Security Headers

Gate: WPT fetch/cors subsets and origin-boundary tests.

- [ ] Complete `Request`, `Response`, URL parsing, body consumption, and fetch surface behavior needed by the current bounded pack.
- [ ] Implement spec-correct CORS and preflight handling, including cache semantics, response tainting, and blocked-response behavior.
- [ ] Enforce referrer policy, mixed-content decisions, MIME sniffing, and `nosniff` integration through the Network process.
- [ ] Close remaining web-to-local and renderer-local file access gaps so `file:` handling follows explicit origin and privilege policy instead of falling through to direct reads.
- [ ] Turn the current fetch/CORS pack from 0/5 into a real baseline by removing `Request is not defined` failures and no-assertion harness outcomes.
- [ ] Add explicit cross-origin boundary tests that prove renderer isolation does not bypass Network-process policy decisions.
- [ ] Lock fetch-cache keys and partitioning semantics so hostile inputs cannot produce policy confusion.

## Milestone F - Hardening + Expansion

Gate: measured conformance growth and security-audit milestones.

- [ ] Replace OOPIF planning and partial frame-isolation logic with full cross-process frame ownership and navigation handoff semantics.
- [ ] Replace simplified CORB behavior with full MIME and content-analysis policy that stands up to hostile cross-origin responses.
- [ ] Expand accessibility from the current snapshot fixture into production A11y tree generation, invalidation, and platform-target mapping.
- [ ] Implement storage partitioning as a first-class security boundary, not a late additive feature.
- [ ] Keep Service Workers off until lifecycle, storage, fetch interception, and security semantics can be delivered fully.
- [ ] Complete native hardening work such as W^X discipline, poisoning, canaries, and audit evidence for unsafe or native-backed hot paths.

## Definition Of Done For Any Task

- [ ] Spec mapping is documented for the changed behavior.
- [ ] Conformance tests are added or updated and the relevant gates are green or intentionally ledgered.
- [ ] New fuzz cases are added when parser, IPC, URL, CSS, or high-risk parsing code changes.
- [ ] Perf impact is measured for hot paths.
- [ ] Security review is completed when privilege, sandbox, IPC, unsafe, or native-resource behavior changes.
- [ ] Documentation is updated in the same change set whenever architecture or subsystem behavior changes.
