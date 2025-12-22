# FenBrowser Engine Contracts

> **Authority**: This document is the canonical reference for module boundaries, phase restrictions, and invariant contracts. All refactors and code reviews must verify compliance.

---

## 1. Engine Phases

| Phase     | Description                            | Duration             |
| --------- | -------------------------------------- | -------------------- |
| `Idle`    | No rendering active                    | Between frames       |
| `Style`   | CSS cascade, computed style resolution | Per-node             |
| `Measure` | Text measurement, intrinsic sizing     | Per-node             |
| `Layout`  | Box model calculation, positioning     | Per-tree, multi-pass |
| `Paint`   | Drawing to canvas                      | Per-frame            |

### Phase Transition Rules

```
Idle → Style → Measure → Layout → Paint → Idle
```

- Transitions MUST be linear (no skipping)
- Backward transitions trigger invariant failure
- `Layout` may run multiple passes (convergence loop)

---

## 2. Module Contracts

### FenBrowser.Core

**Purpose**: Shared types, DOM model, CSS computed styles

| Permission            | Details                          |
| --------------------- | -------------------------------- |
| **MAY READ**          | Nothing external                 |
| **MAY WRITE**         | Own types only                   |
| **MUST NEVER**        | Reference FenEngine, Host, or UI |
| **PHASE RESTRICTION** | None (data layer)                |

### FenBrowser.FenEngine

**Purpose**: Rendering engine - CSS parsing, layout, paint

| Permission            | Details                           |
| --------------------- | --------------------------------- |
| **MAY READ**          | Core types, DOM, CssComputed      |
| **MAY WRITE**         | Layout boxes, render commands     |
| **MUST NEVER**        | Mutate DOM during Layout/Paint    |
| **MUST NEVER**        | Reference Host or UI directly     |
| **MUST NEVER**        | Call async during Paint phase     |
| **PHASE RESTRICTION** | All mutations in Style phase only |

### FenBrowser.Host

**Purpose**: Platform abstraction, resource management, networking

| Permission            | Details                            |
| --------------------- | ---------------------------------- |
| **MAY READ**          | Core types                         |
| **MAY WRITE**         | DOM (parse results), network state |
| **MUST NEVER**        | Call Engine layout/paint directly  |
| **MUST NEVER**        | Reference UI                       |
| **PHASE RESTRICTION** | Operates in Idle phase only        |

### FenBrowser.UI

**Purpose**: User interface, input handling, browser chrome

| Permission            | Details                       |
| --------------------- | ----------------------------- |
| **MAY READ**          | Core types, Host state        |
| **MAY WRITE**         | UI state, navigation requests |
| **MUST NEVER**        | Mutate DOM directly           |
| **MUST NEVER**        | Call Engine internals         |
| **MUST NEVER**        | Bypass Host for network       |
| **PHASE RESTRICTION** | Triggers Engine via Host only |

---

## 3. Invariant Contracts

### Core Immutability

```
DURING (Measure | Layout | Paint):
  DOM.Children = READONLY
  DOM.Attributes = READONLY
  CssComputed.* = READONLY (except cache fields)
```

**Violation**: Throw `EngineInvariantException`

### Layout Side-Effect Free

```
AFTER Layout(node):
  node.Style = UNCHANGED
  node.DOM = UNCHANGED
  GlobalState = UNCHANGED
```

**Violation**: Fail-fast with stack trace

### Phase Linearity

```
TRANSITION(from, to):
  ASSERT order(from) < order(to) OR to == Idle
```

**Violation**: Throw with phase names

### Convergence Limit

```
LAYOUT_PASSES ≤ 10
```

**Violation**: Throw `LayoutConvergenceException`

### No Host Mutation by Engine

```
DURING (Style | Measure | Layout | Paint):
  Host.* = READONLY
  Network.* = NO_CALLS
  FileSystem.* = NO_CALLS
```

**Violation**: Debug assertion

### No UI → Engine Mutation

```
UI.* → Engine.Layout() = FORBIDDEN
UI.* → Engine.Style() = FORBIDDEN
UI.* → DOM.Mutate() = FORBIDDEN
```

**Allowed path**: `UI → Host.Navigate() → Host → Engine`

---

## 4. Diagnostic Format

All engine logs MUST use this format:

```
[{Phase}][Pass={N}][Node={Tag}#{ID}] {Message}
```

### Examples

```
[Layout][Pass=1][Node=DIV#48291] Computing width: available=1920, result=600
[Paint][Pass=0][Node=A#12847] DrawText: "Learn more" at (150, 340)
[Style][Pass=0][Node=BODY#1001] Resolved font-family: system-ui → Segoe UI
```

### Required Fields

- `Phase`: Current engine phase
- `Pass`: Iteration index (0 for non-looping phases)
- `Node`: Tag name + unique ID (hash code)

---

## 5. Enforcement

| Guard Type               | Usage                    |
| ------------------------ | ------------------------ |
| `[Conditional("DEBUG")]` | All invariant assertions |
| `#if DEBUG`              | Verbose phase logging    |
| `ThrowHelper.FailFast()` | Unrecoverable violations |

### Debug Mode Behavior

- Invariant violations = **immediate crash with stack trace**
- Phase violations = **throw with context**
- Convergence exceeded = **throw with pass count**

### Release Mode Behavior

- Assertions compiled out
- Silent fallback for recoverable issues
- Logging level reduced

---

## 6. Review Checklist

Before merging any PR, verify:

- [ ] No new Core references in UI
- [ ] No new DOM mutations during Layout/Paint
- [ ] All logs include phase/pass/node
- [ ] Phase transitions are explicit
- [ ] No async calls in Paint phase
- [ ] Convergence loops have bounds
