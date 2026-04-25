# WebDriver Acceleration Plan (Bucket-First, Invariant-Driven)

This file defines how to speed up WebDriver parity work without test-by-test patching.
Use this as the default execution loop for every hardening cycle.

## Goal

Fix root causes that fan out across many WPT subtests.
Do not optimize for individual test-file greens.

## Core Strategy

1. Build a failure taxonomy from the latest WPT report.
2. Map each taxonomy bucket to one owning code surface.
3. Define strict invariants for that bucket.
4. Add contract tests for invariants first.
5. Implement one bucket at a time until its focused gate is green.
6. Run broad 100-test slice only after a bucket passes.

## Taxonomy Workflow

1. Extract unexpected results from latest WPT report.
2. Group by root-cause signature, not file path.
3. Count fanout (`#subtests`) per bucket.
4. Work highest-fanout bucket first.

Example bucket labels:
- script-promise-timeout-mapping
- prompt-lifecycle-and-leakage
- element-interactability
- frame-shadow-reference-ownership
- cookie-origin-and-session-isolation

## Ownership Map

- `FenBrowser.FenEngine/Rendering/BrowserApi.cs`
  script execution lifecycle, async completion, prompt interaction paths
- `FenBrowser.WebDriver/Commands/ScriptCommands.cs`
  sync/async script protocol behavior, arg/result marshalling contracts
- `FenBrowser.WebDriver/Commands/CommandHandler.cs`
  deterministic error mapping to W3C WebDriver error classes
- `FenBrowser.FenEngine/Core/FenRuntime.cs` and DOM wrappers
  window/frame/shadow/node identity behavior
- `FenBrowser.WebDriver/Commands/ElementCommands.cs`
  interactability, stale checks, command preconditions

## Invariant Rules

For each bucket, write explicit invariants before coding.

Examples:
- Script timeout must return `script timeout` with correct wire status.
- Promise rejection in script execution must return `javascript error`.
- No stale prompt state may affect unrelated commands.
- Frame/shadow references must be stable across serialize/deserialize path.
- Stale or cross-context references must fail deterministically.

## Verification Loop (Required)

1. Focused build of touched project(s).
2. Focused WPT include list for the active bucket (20-40 tests max).
3. Require `0 unexpected` for that bucket.
4. Then run 100-test milestone slice.

Do not skip focused gates.
Do not use broad suite as first feedback loop.

## Regression Prevention

For each root-cause fix:
- Add/extend `FenBrowser.Tests/WebDriver` contract test.
- Keep test name tied to invariant.
- Confirm focused WPT bucket remains green after refactor.

Every fix should protect against re-breaks.

## Execution Order (Default)

1. script/promise/prompt lifecycle
2. element interactability and input semantics
3. frame/shadow traversal and stale ownership
4. cookie/origin/session isolation
5. fullscreen + user-prompt edges

## Working Agreement

- No one-off hacks for single tests.
- No silent fallbacks.
- Deterministic protocol errors only.
- Root-cause patch at earliest broken stage.
- Keep scope local to WebDriver and directly related runtime surfaces.

## Session Startup Checklist

At the start of each hardening session:
1. Open this file.
2. Identify active top-fanout bucket from latest run.
3. Write bucket invariant list in notes.
4. Run focused gate, patch, re-run until green.
5. Run 100-test milestone check.
