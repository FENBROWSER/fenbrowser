# AGENTS.md — FenBrowser.Host

Use this file for windowing, input, UI widgets, BrowserIntegration, OS/platform boundaries, and process/bootstrap work.

## Goal

Keep Host thin, deterministic, and thread-correct.

## Host ownership

Typical areas:
- window/input loop
- BrowserIntegration / BrowserHost bridge
- widgets/chrome
- UI invalidation
- startup/bootstrap
- host-side process isolation and IPC wiring
- platform-specific implementation details

## Rules

- Platform-specific APIs stay in Host or platform-owned layers.
- UI thread ownership must remain explicit.
- Normalize/validate data at Host contract boundaries.
- Do not move engine policy into Host unless the contract truly belongs there.
- Prefer thin contracts and explicit transformations.

## Typical verification

- focused host build
- artifact generation / startup check
- UI-thread / repaint correctness check
- targeted host tests when available

## Output

1. host-side contract being fixed
2. files changed
3. threading/ownership impact
4. verification
5. doc target if needed