# AGENTS.md — FenBrowser.Core

Use this file for DOM, parsing, resource management, network/resource policy, shared primitives, and other Core-layer work.

## Goal

Fix the earliest Core-layer cause with minimal downstream impact.

## Core ownership

Typical areas:
- DOM V2
- parser/tokenizer/tree-builder
- resource loading/caching
- network policy surfaces
- shared browser/value types
- platform-agnostic low-level primitives

## Rules

- Fix structure before fixing layout symptoms.
- If DOM/tree shape is wrong, do not patch layout first.
- If resource selection/policy is wrong, do not patch renderer symptoms first.
- Keep Core engine-agnostic where possible.
- Avoid leaking Host assumptions into Core.
- Avoid speculative API growth.

## Typical diagnostic path

1. compare raw source to DOM / produced structures
2. find first divergence
3. inspect the local owning subsystem
4. patch the earliest incorrect stage
5. verify with the narrowest focused build/test

## Output

1. first divergence or root cause
2. owning files
3. minimal fix
4. verification
5. doc target if needed