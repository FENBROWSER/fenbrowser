# Appendix E: Glossary (The Dictionary of Fen)

**State as of:** 2026-02-06
**Codex Version:** 1.0

This glossary defines standard terminology used throughout the FenBrowser codebase. When in doubt, strictly adhere to these definitions.

## A

### Acid Tests

A series of test suites (Acid2, Acid3) designed to verify a browser's compliance with web standards, particularly CSS layout and DOM manipulation.

### Agent Mode

The operating mode of the AI assistant where it autonomously navigates the codebase, runs tools, and updates artifacts.

## B

### Box (LayoutBox)

The fundamental unit of the Layout Tree. A Box represents a rectangular area on the screen.

- **Differs from Element**: A single Element (`<li>`) can generate multiple Boxes (Marker Box + Content Box).
- **Types**: `BlockBox`, `InlineBox`, `AnonymousBox`.

### Bridge

The interoperability layer connecting the Host (Native UI) and the Engine (Logic). Implemented via `BrowserApi.cs` and `BrowserIntegration.cs`.

## C

### CDP (Chrome DevTools Protocol)

The JSON-RPC wire protocol used to debug the browser. FenBrowser implements this protocol in `FenBrowser.DevTools.Core` to allow external tools (like VS Code) to attach.

### ContainerNode

A DOM Node that can have children (e.g., `Element`, `Document`).

- **Contrast**: `Text` nodes are `Node` but NOT `ContainerNodes`.

## D

### DOM (Document Object Model)

The tree structure representing the HTML document.

- **Location**: `FenBrowser.Core.Dom.V2`
- **Root**: `Document`

### Dirty Flag

A boolean state marking a node as needing processing.

- `LayoutDirty`: Needs measurement/arrangement.
- `PaintDirty`: Needs repainting.
- `StyleDirty`: Needs CSS recalculation.

## E

### Element

A specific type of `ContainerNode` representing an HTML tag (e.g., `<div>`, `<span>`).

- **Has**: Attributes, ClassList, TagName.

### Engine Loop

The main logic loop running in `FenEngine`. It processes Tasks, Microtasks, and Rendering updates.

- **Frequency**: Often anchored to `RequestAnimationFrame` logic.

## F

### Fragment (DocumentFragment)

A lightweight DOM container that is not part of the active document tree. Used for efficient DOM manipulation before insertion.

## H

### Host

The native executable (`FenBrowser.Host`). It owns the OS Window, Input, and Clipboard.

## M

### Microtask

A lightweight task (Promise reaction, Observer callback) that must run immediately after the currently executing script and before the next Task.

## N

### Node

The abstract base class for all entities in the DOM tree.

- **Subtypes**: `Element`, `Text`, `Comment`, `Document`.

## P

### Paint Tree

A flat list of drawing commands optimized for the backend renderer (Skia).

- **Source**: Constructed from the Layout Tree.
- **Ordering**: Sorted by Z-Index and Stacking Context rules.

## S

### Sandbox

The security boundary restricting what a script can do.

- **Implementation**: `SandboxPolicy` and `JavaScriptEngine` permission checks.

## W

### WPT (Web Platform Tests)

The shared test suite for all browsers, maintained by the W3C. FenBrowser runs a subset of these to ensure spec compliance.

_End of Appendix E_
