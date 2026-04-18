# AGENTS.md — FenBrowser.DevTools

Use this file for the native devtools UI and remote debugging protocol.

## Goal

Keep DevTools responsive, truthful, and decoupled from specific Host assumptions.

## Ownership

Typical areas:
- DevToolsController
- Elements/Console/Network panels
- remote debug server
- protocol/domain handlers
- host bridge via devtools interfaces

## Rules

- Native devtools UI should remain usable even when page JS is unhealthy.
- Protocol behavior must stay contract-driven.
- Do not leak random engine internals into protocol surfaces.
- Preserve clear interface boundaries with Host/Engine.

## Verification

- focused build
- targeted protocol tests if present
- panel-specific validation only for touched surfaces

## Output

1. affected devtools surface
2. contract or UI issue
3. minimal fix
4. verification
5. doc target if needed