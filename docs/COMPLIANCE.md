# FenEngine Spec-Compliant Rendering Pipeline Roadmap

> **Target Outcome**: A deterministic, spec-traceable HTML/CSS engine capable of passing Acid2, rendering modern layouts, and supporting controlled interactivity — without architectural collapse.

---

## Executive Summary

This document defines a **phased engineering roadmap** for transforming FenEngine into a robust, spec-compliant rendering engine. Each phase has strict exit criteria, and advancement is blocked until all criteria are met.

**Key Principle**: Spec sections map directly to code modules. Every subsystem references specific W3C/WHATWG specifications.

---

## Global Non-Negotiable Rules

> [!CAUTION]
> These are **laws**, not guidelines. Violating them guarantees architectural collapse.

| Rule                           | Description                                                                                            |
| ------------------------------ | ------------------------------------------------------------------------------------------------------ |
| **Single Authority**           | Parser parses, DOM owns structure, Style computes values, Layout computes geometry, Paint draws pixels |
| **Spec ↔ Code Mapping**        | Every module MUST reference a specific spec section in its header comment                              |
| **Determinism > Completeness** | Same input → same output → forever. Non-deterministic behavior is a critical bug                       |
| **Unsupported ≠ Partial**      | Features are either fully supported or fail cleanly. No partial implementations that "seem to work"    |

---

## Current State Analysis (Audit Jan 2026)

The engine has undergone a massive **ES6+ Upgrade (Phases 1-10)** and architectural hardening. Below is the updated compliance score (1-10 scale).

| Component         | Score | Status      | Spec Compliance Analysis                                                                                                                                                         |
| ----------------- | ----- | ----------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Scripting**     | 9/10  | ✅ Mature   | ES6+ complete (Proxies, Promises). Lifecycle events (DOMContentLoaded/load) hardened. ReadyState spec-compliant.                                                                 |
| **Layout**        | 10/10 | ✅ Complete | Grid Phase 3 complete (spec sizing). Flexbox full spec compliance with order property. Multi-column hardened. Intrinsic sizing implemented engine-wide.                          |
| **CSS / Cascade** | 10/10 | ✅ Complete | Tokenizer/Syntax parser implemented. Media Query evaluation hardened. Cascade sorting (Specificity) is spec-compliant. `inherit`/`initial`/`unset` keywords supported.           |
| **DOM / Events**  | 10/10 | ✅ Complete | Event system matches DOM Level 3. ReadyState transitions and readystatechange event hardened. Full `Attr`, `NamedNodeMap`, `TextContent`, and `CompareDocumentPosition` support. |
| **Web APIs**      | 8/10  | Mixed       | `Fetch` is hardened. `IndexedDB` now provides versioned in-memory CRUD/transaction semantics, and Cache Storage preserves response/header interop more accurately. `Web Audio` and `WebRTC` runtime surfaces are intentionally not exposed until real media subsystems exist.                                                                 |
| **Architecture**  | 4/10  | ❌ Fragile  | Pipeline stages defined and enforced. Event Loop matches WHATWG shape but macro/micro task draining needs strict isolation.                                                      |

---

## Technical Debt: "Half-Baked" Feature Analysis

The following features functionally "work" for common websites but deviate from spec in ways that cause edge-case breakage.

### 1. Networking: `FetchApi.cs` (Audit Score: 10/10)

- **Status**: Fully Hardened. `JsRequest`, `JsHeaders`, and `JsResponse` are complete.
- **Improved**: Full support for `fetch(Request)` and initialization options.
- **Complete**: JSON and Text parsing are spec-compliant.

- **Status**: Spec-Compliant Sizing.
- **Improved**: Full implementation of [CSS Grid §11. sizing algorithm](https://www.w3.org/TR/css-grid-1/#algo-overview) including multi-track distribution and measurement callbacks.
- **Complete**: Intrinsic sizing (`min-content`/`max-content`) accurately resolved for tracks and container.

### 3. Scripting: `JavaScriptEngine.cs` (Audit Score: 9/10)

- **Status**: ES6+ Complete.
- **Hardened**: Removed "legacy immediate-fire" hack for `load`/`DOMContentLoaded`.
- **Improved**: `document.readyState` transitions and event timing are now spec-compliant.

---

## Phase 0 — Engine Stabilization & Contracts (Mandatory)

> [!IMPORTANT] > **You do not advance until this phase is complete.** This phase establishes the architectural foundation that prevents future rewrites.

### Goal

Make FenEngine refactor-safe and spec-navigable.

---

### Module 0.1: Canonical Pipeline Contract

**Objective**: Lock the rendering pipeline in code and documentation.

```
InputBytes
  → ParsedTokens
  → DOMTree
  → StyleTree (ComputedValues)
  → LayoutTree (Geometry)
  → DisplayList
  → RasterOutput
```

#### Tasks

| #     | Task                                    | Files                                                | Notes                                                                   |
| ----- | --------------------------------------- | ---------------------------------------------------- | ----------------------------------------------------------------------- |
| 0.1.1 | Create `PipelineStage` enum             | [NEW] `FenBrowser.FenEngine/Core/PipelineStage.cs`   | `Idle`, `Tokenizing`, `Parsing`, `Styling`, `Layout`, `Paint`, `Raster` |
| 0.1.2 | Create `PipelineContext` class          | [NEW] `FenBrowser.FenEngine/Core/PipelineContext.cs` | Holds current stage, dirty flags, immutable snapshots                   |
| 0.1.3 | Implement snapshot immutability         | Update `LayoutResult.cs`                             | Mark outputs as `readonly` structs                                      |
| 0.1.4 | Add forward-only dirty flag propagation | Update `EngineLoop.cs`                               | `StyleDirty → LayoutDirty → PaintDirty` cascade                         |
| 0.1.5 | Add phase violation detection           | [NEW] `FenBrowser.FenEngine/Core/PhaseGuard.cs`      | Debug assertions for reading "future" state                             |

#### Acceptance Criteria

- [ ] All pipeline stages defined in enum
- [ ] Dirty flags propagate forward only
- [ ] Reading future stage data throws in DEBUG builds
- [ ] Pipeline context serializable for debugging

---

### Module 0.2: Formal Event Loop Skeleton

**Objective**: Implement spec-compliant processing model shell.

**Reference**: [WHATWG HTML Event Loop](https://html.spec.whatwg.org/multipage/webappapis.html#event-loops)

```csharp
public void RunFrame()
{
    // 1. Handle raw input (mouse, keyboard)
    HandleInput();

    // 2. Dispatch queued events
    DispatchEvents();

    // 3. Style computation (if dirty)
    if (_styleDirty) ComputeStyles();

    // 4. Layout (if dirty)
    if (_layoutDirty) ComputeLayout();

    // 5. Build display list (if dirty)
    if (_paintDirty) BuildDisplayList();

    // 6. Rasterize to GPU
    Rasterize();

    // 7. Present to screen
    Present();

    // 8. Run requestAnimationFrame callbacks
    RunAnimationFrameCallbacks();
}
```

#### Tasks

| #     | Task                                | Files                                             | Notes                                     |
| ----- | ----------------------------------- | ------------------------------------------------- | ----------------------------------------- |
| 0.2.1 | Refactor `EngineLoop.RunFrame()`    | [MODIFY] `Core/EngineLoop.cs`                     | Implement full sequence above             |
| 0.2.2 | Add input queue                     | [NEW] `Core/InputQueue.cs`                        | Buffer keyboard/mouse events              |
| 0.2.3 | Add event dispatch phase            | [MODIFY] `Core/EngineLoop.cs`                     | Process macro-task queue, then microtasks |
| 0.2.4 | Add animation frame registry        | [NEW] `Core/AnimationFrameScheduler.cs`           | `requestAnimationFrame` hook              |
| 0.2.5 | Integrate with EventLoopCoordinator | [MODIFY] `Core/EventLoop/EventLoopCoordinator.cs` | Ensure spec order                         |

#### Acceptance Criteria

- [ ] Event loop matches WHATWG spec order
- [ ] Input events queued, not processed immediately
- [ ] Animation frames run after paint
- [ ] Microtasks drain between macro-tasks

---

### Module 0.3: Spec Index & Ownership Map

**Objective**: Create living documentation mapping subsystems to specifications.

#### Tasks

| #     | Task                                | Files                             | Notes                           |
| ----- | ----------------------------------- | --------------------------------- | ------------------------------- |
| 0.3.1 | Create Specs.md                     | [NEW] `docs/SPECS.md`             | Central specification reference |
| 0.3.2 | Add spec references to source files | All core files                    | Header comments with spec links |
| 0.3.3 | Create compliance tracking system   | [NEW] `docs/COMPLIANCE_MATRIX.md` | Feature-by-feature status       |

#### Specs.md Template

```markdown
| Subsystem    | Spec                | Version         | Status     | Owner File               |
| ------------ | ------------------- | --------------- | ---------- | ------------------------ |
| HTML Parsing | WHATWG HTML §13     | Living Standard | ❌ N/A     | Parser.cs                |
| DOM Core     | DOM Living Standard | 2024-12         | ⚠️ Partial | Core/Dom/Element.cs      |
| CSS Cascade  | CSS Cascade 4       | CR              | ⚠️ Partial | CssLoader.cs             |
| CSS 2.1 VFM  | CSS 2.1 §9-10       | REC             | ⚠️ Partial | MinimalLayoutComputer.cs |
| CSS Flexbox  | CSS Flexbox 1       | CR              | ⚠️ Partial | MinimalLayoutComputer.cs |
| Stacking     | CSS 2.1 Appendix E  | REC             | ⚠️ Partial | StackingContext.cs       |
```

#### Acceptance Criteria

- [ ] Specs.md exists with all subsystems
- [ ] Each core source file has spec reference header
- [ ] Compliance matrix tracks feature status

---

### Phase 0 Exit Criteria

> [!WARNING]
> Do not proceed to Phase 1 until ALL items below are checked.

- [ ] Pipeline frozen with typed stages
- [ ] Event loop matches WHATWG processing model shape
- [ ] Every subsystem has a spec owner documented
- [ ] Phase violation guard implemented
- [ ] All existing tests still pass

---

## Phase 1 — "Acid2 Foundation" (CSS 2.1 Layout & Painting)

> **Goal**: Achieve Acid2 test passage. This establishes credibility and validates core layout/paint architecture.

**Reference**: [Acid2 Test](https://www.webstandards.org/files/acid2/test.html)

---

### Module 1.1: Stacking Contexts (CSS 2.1 ONLY)

**Spec**: [CSS 2.1 Appendix E - Elaborate Description of Stacking Contexts](https://www.w3.org/TR/CSS21/zindex.html)

#### Paint Order (CSS 2.1 Canonical)

```
1. Background and borders of the element forming stacking context
2. Child stacking contexts with negative z-index (in z-index order)
3. In-flow, non-positioned block descendants
4. Floated descendants
5. In-flow, non-positioned inline descendants
6. Positioned descendants with z-index: auto or 0
7. Child stacking contexts with positive z-index (in z-index order)
```

#### Tasks

| #     | Task                              | Files                                                | Notes                               |
| ----- | --------------------------------- | ---------------------------------------------------- | ----------------------------------- |
| 1.1.1 | Refactor `StackingContextBuilder` | [MODIFY] `Rendering/StackingContext.cs`              | Implement 7-phase paint order       |
| 1.1.2 | Add float layer tracking          | [MODIFY] `StackingContext.cs`                        | Separate floats from in-flow        |
| 1.1.3 | Add inline layer tracking         | [MODIFY] `StackingContext.cs`                        | Inline content painted after floats |
| 1.1.4 | Create `StackingContextPainter`   | [NEW] `Rendering/Painting/StackingContextPainter.cs` | Paints layers in correct order      |
| 1.1.5 | Add tests for paint order         | [NEW] `FenBrowser.Tests/StackingContextTests.cs`     | Reference test cases                |

> [!IMPORTANT] > **Explicitly EXCLUDE** (not CSS 2.1): `opacity`, `transform`, `filter`, `will-change`. These create stacking contexts in CSS3+, but we implement them later.

#### Acceptance Criteria

- [ ] Root stacking context created
- [ ] Positioned + z-index creates stacking context
- [ ] Paint order matches spec exactly
- [ ] Negative z-index paints before normal flow
- [ ] Positive z-index paints last

---

### Module 1.2: Full Margin Collapsing

**Spec**: [CSS 2.1 §8.3.1 - Collapsing Margins](https://www.w3.org/TR/CSS21/box.html#collapsing-margins)

#### Rules to Implement

```
1. Adjacent sibling margins collapse
2. Parent/first-child margins collapse (if no border/padding/inline)
3. Parent/last-child margins collapse (if no border/padding/inline/height)
4. Empty block margins collapse through
5. Collapsed margin = max(margins), or algebraic sum if negative
6. Clearance prevents collapse
```

#### Tasks

| #     | Task                                   | Files                                           | Notes                                |
| ----- | -------------------------------------- | ----------------------------------------------- | ------------------------------------ |
| 1.2.1 | Create `MarginCollapseComputer`        | [NEW] `Layout/MarginCollapseComputer.cs`        | Isolated margin logic                |
| 1.2.2 | Add collapse detection                 | `MarginCollapseComputer.cs`                     | Detect when collapse applies         |
| 1.2.3 | Add clearance handling                 | `MarginCollapseComputer.cs`                     | `clear: left/right/both` interaction |
| 1.2.4 | Integrate with `MinimalLayoutComputer` | [MODIFY] `Layout/MinimalLayoutComputer.cs`      | Replace current margin handling      |
| 1.2.5 | Add reference tests                    | [NEW] `FenBrowser.Tests/MarginCollapseTests.cs` | CSS 2.1 test cases                   |

#### Test Case: Nested Divs

```html
<div style="margin-bottom: 20px">
  <div style="margin-bottom: 30px">Content</div>
</div>
<div style="margin-top: 25px">Below</div>
```

**Expected**: Gap between = `35px` (max of collapsed 30px and 25px), not 75px.

#### Acceptance Criteria

- [ ] Sibling margin collapse works
- [ ] Parent/child collapse works (when applicable)
- [ ] Empty block collapse-through works
- [ ] Clearance prevents collapse
- [ ] Nested `<div>` test passes

---

### Module 1.3: Absolute Positioning Resolution

**Spec**: [CSS 2.1 §10.1–10.6 - Visual Formatting Model](https://www.w3.org/TR/CSS21/visudet.html)

#### The "Equation of 7 Variables"

For width:

```
'left' + 'margin-left' + 'border-left' + 'padding-left' +
'width' +
'padding-right' + 'border-right' + 'margin-right' + 'right'
= containing block width
```

When values are `auto`, solve according to spec precedence.

#### Tasks

| #     | Task                                 | Files                                             | Notes                                   |
| ----- | ------------------------------------ | ------------------------------------------------- | --------------------------------------- |
| 1.3.1 | Implement containing block finder    | [NEW] `Layout/ContainingBlockResolver.cs`         | Find nearest positioned ancestor or ICB |
| 1.3.2 | Implement 7-variable equation solver | [NEW] `Layout/AbsolutePositionSolver.cs`          | Handles all auto combinations           |
| 1.3.3 | Add min/max constraint interaction   | `AbsolutePositionSolver.cs`                       | `min-width`, `max-width` trump computed |
| 1.3.4 | Integrate with layout engine         | [MODIFY] `MinimalLayoutComputer.cs`               | Use solver for `position: absolute`     |
| 1.3.5 | Add reference tests                  | [NEW] `FenBrowser.Tests/AbsolutePositionTests.cs` | Spec examples                           |

#### Acceptance Criteria

- [ ] Containing block correctly identified
- [ ] `left: 0; right: 0;` stretches element
- [ ] `left: 0; width: auto; right: 0;` stretches with margin for auto
- [ ] `min-width` respected when computed width is smaller
- [ ] Absolutely positioned children don't affect parent size

---

### Phase 1 Exit Criteria

> [!CAUTION]
> Acid2 MUST pass before proceeding. This is non-negotiable.

- [ ] **Acid2 test passes completely** (face renders correctly)
- [ ] No z-index visual glitches
- [ ] Margin behavior matches CSS 2.1 spec diagrams
- [ ] Absolute positioning solves equation correctly
- [ ] All Phase 1 tests pass

---

## Phase 1.5 — HTML Parser Transplant (Moved Earlier)

> [!IMPORTANT]
> This is moved earlier because layout debugging becomes hellish without proper DOM structure. Bad DOM → bad layout → false bug reports.

**Spec**: [WHATWG HTML §13 - Parsing](https://html.spec.whatwg.org/multipage/parsing.html)

---

### Module 1.5.1: HTML5 Tokenizer (Spec-Compliant)

**Options**:

1. **Custom tokenizer** (recommended) - Built for FenEngine data structures
2. Custom state machine - More control, more effort

#### Required Tokenizer States

| State                 | Transitions                    |
| --------------------- | ------------------------------ |
| Data                  | Default state                  |
| Tag Open              | Saw `<`                        |
| End Tag Open          | Saw `</`                       |
| Tag Name              | Reading element name           |
| Before Attribute Name | After tag name                 |
| Attribute Name        | Reading attribute name         |
| Attribute Value       | Various quote states           |
| Script Data           | Inside `<script>`              |
| RCDATA                | Inside `<title>`, `<textarea>` |
| RAWTEXT               | Inside `<style>`, `<xmp>`      |
| CDATA                 | Inside `<![CDATA[`             |

#### Tasks

| #       | Task                          | Files                                      | Notes                                              |
| ------- | ----------------------------- | ------------------------------------------ | -------------------------------------------------- |
| 1.5.1.1 | Evaluate Custom tokenizer     | Research                                   | Check integration feasibility                      |
| 1.5.1.2 | Create tokenizer interface    | [NEW] `Core/Parsing/IHtmlTokenizer.cs`     | Abstract over implementation                       |
| 1.5.1.3 | Implement tokenizer adapter   | [NEW] `Core/Parsing/Html5Tokenizer.cs`     | Wrap chosen implementation                         |
| 1.5.1.4 | Add token types               | [NEW] `Core/Parsing/HtmlToken.cs`          | DOCTYPE, StartTag, EndTag, Comment, Character, EOF |
| 1.5.1.5 | Implement state machine tests | [NEW] `FenBrowser.Tests/TokenizerTests.cs` | WHATWG test cases                                  |

#### Acceptance Criteria

- [ ] All token types produced correctly
- [ ] Script data state handles `</script>` edge cases
- [ ] RCDATA handles `</title>` correctly
- [ ] Entity references decoded
- [ ] ERROR tokens for malformed input

---

### Module 1.5.2: Tree Construction Algorithms

**Spec**: [WHATWG HTML §13.2.6 - Tree Construction](https://html.spec.whatwg.org/multipage/parsing.html#tree-construction)

#### Mandatory Algorithms

| Algorithm        | Description                     | Spec Section |
| ---------------- | ------------------------------- | ------------ |
| Insertion Modes  | State machine for tree building | §13.2.6.4    |
| Implied End Tags | Auto-close `<p>`, `<li>`, etc.  | §13.2.6.3    |
| Adoption Agency  | Fix `<b><i></b></i>` misnesting | §13.2.6.4.7  |
| Foster Parenting | Handle content before `<tbody>` | §13.2.6.4.8  |

#### Tasks

| #       | Task                       | Files                                 | Notes                                |
| ------- | -------------------------- | ------------------------------------- | ------------------------------------ |
| 1.5.2.1 | Create insertion mode enum | [NEW] `Core/Parsing/InsertionMode.cs` | Initial, BeforeHTML, AfterHead, etc. |
| 1.5.2.2 | Create tree builder        | [NEW] `Core/Parsing/TreeBuilder.cs`   | Main tree construction               |
| 1.5.2.3 | Implement implied end tags | `TreeBuilder.cs`                      | Pop elements appropriately           |
| 1.5.2.4 | Implement adoption agency  | `TreeBuilder.cs`                      | Handle misnested formatting          |
| 1.5.2.5 | Implement foster parenting | `TreeBuilder.cs`                      | Table edge cases                     |
| 1.5.2.6 | Make parser pausable       | `TreeBuilder.cs`                      | For `document.write()`               |

#### Acceptance Criteria

- [ ] `<b><i>text</b>more</i>` produces correct tree
- [ ] `<table><tbody><tr><td>x</table>` produces correct tree
- [ ] Missing `</html>`, `</body>` are implied
- [ ] Parser can be paused mid-parse
- [ ] No crashes on malformed input

---

### Phase 1.5 Exit Criteria

- [ ] Malformed HTML renders correctly (tag soup works)
- [ ] No tree corruption from edge cases
- [ ] Parser is pausable for script integration
- [ ] DOM does not leak past parsing layer

---

## Phase 2 — DOM & Event System (Engine "Brain")

---

### Module 2.1: DOM Hardening

**Spec**: [DOM Living Standard](https://dom.spec.whatwg.org/)

#### Required Elements

| Feature              | Spec Reference | Current Status             |
| -------------------- | -------------- | -------------------------- |
| `Attr` objects       | DOM �4.9       | Partial wrapper parity |
| `Node` interface     | §4.4           | ⚠️ Partial                 |
| `nodeType` constants | §4.4           | ⚠️ Assumed                 |
| `textContent`        | §4.4.3         | ⚠️ Verify                  |
| Tree traversal       | §4.4           | ⚠️ Partial                 |

#### Tasks

| #     | Task                                  | Files                       | Notes                             |
| ----- | ------------------------------------- | --------------------------- | --------------------------------- |
| 2.1.1 | Create `Attr` class                   | [NEW] `Core/Dom/Attr.cs`    | Proper attribute objects          |
| 2.1.2 | Add `nodeType` constants              | [MODIFY] `Core/Dom/Node.cs` | `ELEMENT_NODE = 1`, etc.          |
| 2.1.3 | Implement `textContent` getter/setter | [MODIFY] `Core/Dom/Node.cs` | Recursive text extraction         |
| 2.1.4 | Implement traversal APIs              | [MODIFY] `Core/Dom/Node.cs` | `firstChild`, `nextSibling`, etc. |
| 2.1.5 | Ensure DOM isolation                  | [MODIFY] parsing layer      | FenEngine DOM only past parse     |

#### Acceptance Criteria

- [ ] `element.attributes` returns `Attr` objects
- [ ] `node.nodeType` returns correct constants
- [ ] `textContent` works recursively
- [ ] External types never leak to layout/paint

---

### Module 2.2: Event System (Non-Optional)

**Spec**: [DOM Events Level 3](https://www.w3.org/TR/DOM-Level-3-Events/)

> [!TIP]
> Even without JavaScript, internal engine events depend on this system.

#### Required Features

| Feature                   | Status | Notes                                                                                                 |
| ------------------------- | ------ | ----------------------------------------------------------------------------------------------------- |
| `Event` class             | ✅     | [DomEvent.cs](file:///c:/Users/udayk/Videos/FENBROWSER/FenBrowser.FenEngine/DOM/DomEvent.cs)          |
| Capture → Target → Bubble | ✅     | In [EventTarget.cs](file:///c:/Users/udayk/Videos/FENBROWSER/FenBrowser.FenEngine/DOM/EventTarget.cs) |
| `stopPropagation()`       | ✅     | Implemented                                                                                           |
| `preventDefault()`        | ✅     | Implemented                                                                                           |
| `EventTarget` interface   | ⚠️     | Verify completeness                                                                                   |

#### Tasks

| #     | Task                             | Files                       | Notes                    |
| ----- | -------------------------------- | --------------------------- | ------------------------ |
| 2.2.1 | Audit current implementation     | `DOM/EventTarget.cs`        | Verify spec compliance   |
| 2.2.2 | Add `stopImmediatePropagation()` | `DOM/DomEvent.cs`           | If missing               |
| 2.2.3 | Add composed events              | `DOM/DomEvent.cs`           | For Shadow DOM future    |
| 2.2.4 | Add trusted event flag           | `DOM/DomEvent.cs`           | User vs script initiated |
| 2.2.5 | Add comprehensive tests          | [NEW] `Tests/EventTests.cs` | Phase order tests        |

#### Acceptance Criteria

- [ ] Events propagate: capture → target → bubble
- [ ] `stopPropagation()` works correctly
- [ ] `preventDefault()` cancels default action
- [ ] Multiple listeners on same node work

---

### Phase 2 Exit Criteria

- [ ] Events propagate correctly through phases
- [ ] DOM mutations are safe and spec-compliant
- [ ] Tree traversal matches spec
- [ ] No external leakage

---

## Phase 3 — CSS Parsing & Cascade (No Regex Left)

> [!WARNING]
> Regex-based CSS parsing is a known source of bugs. This phase eliminates it.

---

### Module 3.1: CSS Syntax Level 3 Tokenizer

**Spec**: [CSS Syntax Level 3](https://www.w3.org/TR/css-syntax-3/)

#### Required Token Types

```
<ident-token>     → property names, keywords
<function-token>  → rgb(, calc(, var(
<dimension-token> → 10px, 2em, 1.5rem
<number-token>    → 1, 1.5, -2
<percentage-token>→ 50%
<string-token>    → "hello", 'world'
<delim-token>     → single characters
<hash-token>      → #id, #fff
<at-keyword-token>→ @media, @keyframes
<url-token>       → url(image.png)
<whitespace-token>→ space, tabs, newlines
<colon-token>, <semicolon-token>, <comma-token>
<(-token>, <)-token>, <[-token>, <]-token>, <{-token>, <}-token>
```

#### Tasks

| #     | Task                       | Files                                 | Notes                        |
| ----- | -------------------------- | ------------------------------------- | ---------------------------- |
| 3.1.1 | Create `CssToken` types    | [NEW] `Rendering/Css/CssToken.cs`     | All token types              |
| 3.1.2 | Create `CssTokenizer`      | [NEW] `Rendering/Css/CssTokenizer.cs` | State machine                |
| 3.1.3 | Migrate from regex parsing | [MODIFY] `Rendering/CssLoader.cs`     | Replace regex with tokenizer |
| 3.1.4 | Implement error recovery   | `CssTokenizer.cs`                     | Invalid blocks skipped       |
| 3.1.5 | Add tokenizer tests        | [NEW] `Tests/CssTokenizerTests.cs`    | Edge cases                   |

#### Error Recovery Rules

```
1. Invalid declaration → skip to next `;` or `}`
2. Invalid block → skip to matching `}`
3. Invalid at-rule → skip to next top-level
4. Stylesheet NEVER aborts entirely
```

#### Acceptance Criteria

- [ ] All token types produced correctly
- [ ] No regex in CSS parsing hot path
- [ ] Invalid CSS gracefully skipped
- [ ] Performance ≥ current implementation

---

### Module 3.2: Cascade & Inheritance Engine

**Spec**: [CSS Cascade Level 4](https://www.w3.org/TR/css-cascade-4/)

#### Cascade Order

```
1. Origin (User-Agent < User < Author)
2. Importance (!important reverses origin)
3. Specificity (inline > ID > class > type)
4. Source Order (later wins)
```

#### Tasks

| #     | Task                                    | Files                                    | Notes                      |
| ----- | --------------------------------------- | ---------------------------------------- | -------------------------- |
| 3.2.1 | Create `CascadeComputer`                | [NEW] `Rendering/Css/CascadeComputer.cs` | Pure cascade logic         |
| 3.2.2 | Implement origin sorting                | `CascadeComputer.cs`                     | UA, User, Author separated |
| 3.2.3 | Implement `!important` handling         | `CascadeComputer.cs`                     | Reverses origin order      |
| 3.2.4 | Implement specificity calculation       | `CascadeComputer.cs`                     | (a,b,c) tuple              |
| 3.2.5 | Implement `inherit`, `initial`, `unset` | `CascadeComputer.cs`                     | Keyword resolution         |
| 3.2.6 | Migrate from current approach           | [MODIFY] `CssLoader.cs`                  | Use new CascadeComputer    |

#### Acceptance Criteria

- [ ] `!important` on UA style loses to author `!important`
- [ ] Specificity calculated correctly
- [ ] `inherit` propagates parent value
- [ ] `initial` uses spec initial value
- [ ] `unset` acts as `inherit` or `initial` appropriately

---

### Module 3.3: Units & Values

**Spec**: [CSS Values Level 4](https://www.w3.org/TR/css-values-4/)

#### Required Unit Conversions

| Unit     | Computation                   |
| -------- | ----------------------------- |
| `px`     | Absolute (base)               |
| `em`     | Parent font-size × value      |
| `rem`    | Root font-size × value        |
| `%`      | Context-dependent             |
| `vw`     | Viewport width ÷ 100 × value  |
| `vh`     | Viewport height ÷ 100 × value |
| `calc()` | Expression evaluation         |

#### Tasks

| #     | Task                           | Files                                 | Notes                    |
| ----- | ------------------------------ | ------------------------------------- | ------------------------ |
| 3.3.1 | Create `UnitResolver`          | [NEW] `Rendering/Css/UnitResolver.cs` | Central unit resolution  |
| 3.3.2 | Implement `calc()` subset      | `UnitResolver.cs`                     | +, -, \*, / with lengths |
| 3.3.3 | Document used vs computed      | Code comments                         | Per CSS spec             |
| 3.3.4 | Migrate from inline resolution | [MODIFY] `CssLoader.cs`               | Use UnitResolver         |

#### Acceptance Criteria

- [ ] `em` based on parent font-size
- [ ] `rem` based on root font-size
- [ ] `calc(100% - 50px)` works
- [ ] Computed vs used values documented

---

### Phase 3 Exit Criteria

- [ ] CSS behaves predictably
- [ ] Computed styles inspectable via DevTools
- [ ] **No regex parsing remains in hot path**
- [ ] All cascade tests pass

---

## Phase 4 — Modern Layout (Flexbox + Fonts)

---

### Module 4.1: Complete Flexbox Algorithm

**Spec**: [CSS Flexbox Level 1](https://www.w3.org/TR/css-flexbox-1/)

> [!CAUTION]
> Partial Flexbox is worse than none. Complete it or mark unsupported.

#### Algorithm Steps

```
1. Determine flex base size for each item
2. Collect items into flex lines (single vs multi)
3. Resolve flexible lengths (grow/shrink)
4. Resolve cross sizes for each line
5. Handle alignment (justify-content, align-items, align-content)
```

#### Tasks

| #     | Task                              | Files                                | Notes                              |
| ----- | --------------------------------- | ------------------------------------ | ---------------------------------- |
| 4.1.1 | Create `FlexLayoutComputer`       | [NEW] `Layout/FlexLayoutComputer.cs` | Dedicated flex logic               |
| 4.1.2 | Implement flex base size          | `FlexLayoutComputer.cs`              | `flex-basis` resolution            |
| 4.1.3 | Implement line collection         | `FlexLayoutComputer.cs`              | `flex-wrap` handling               |
| 4.1.4 | Implement free space distribution | `FlexLayoutComputer.cs`              | `flex-grow`, `flex-shrink`         |
| 4.1.5 | Implement cross axis sizing       | `FlexLayoutComputer.cs`              | `align-self`, `align-items`        |
| 4.1.6 | Implement alignment               | `FlexLayoutComputer.cs`              | `justify-content`, `align-content` |
| 4.1.7 | Add comprehensive tests           | [NEW] `Tests/FlexboxTests.cs`        | WPT test cases                     |

#### Acceptance Criteria

- [ ] `flex: 1` distributes space equally
- [ ] `flex-wrap: wrap` wraps correctly
- [ ] `justify-content: space-between` works
- [ ] `align-items: center` works
- [ ] Nested flex containers work

---

### Module 4.2: Web Fonts Pipeline

#### Flow

```
@font-face rule
  → Fetch font file (async)
  → Decode (WOFF/WOFF2/TTF)
  → Register with FontRegistry
  → Invalidate layout
  → Re-layout with new font
```

#### Tasks

| #     | Task                          | Files                      | Notes                      |
| ----- | ----------------------------- | -------------------------- | -------------------------- |
| 4.2.1 | Parse `@font-face` fully      | [MODIFY] `CssLoader.cs`    | Extract all descriptors    |
| 4.2.2 | Implement async font fetching | [MODIFY] `FontRegistry.cs` | Non-blocking               |
| 4.2.3 | Implement WOFF2 decoding      | [MODIFY] `FontRegistry.cs` | Via SkiaSharp              |
| 4.2.4 | Implement fallback rendering  | Layout layer               | Render with fallback first |
| 4.2.5 | Implement layout invalidation | `EngineLoop.cs`            | Re-layout when font loads  |

> [!TIP] > **Never block parsing or layout on font loading.** Render fallback first, swap when ready.

#### Acceptance Criteria

- [ ] Font loads asynchronously
- [ ] Fallback font renders immediately
- [ ] Font swap happens without flash of invisible text (FOIT)
- [ ] No layout jitter after swap

---

### Phase 4 Exit Criteria

- [ ] Dashboard/layout-heavy sites render correctly
- [ ] Flexbox algorithm complete
- [ ] Web fonts load without blocking
- [ ] No layout jitter during font swap

---

## Phase 5 — Interactivity (Split for Safety)

> [!CAUTION]
> This is where engines die if rushed. Phase 5 is intentionally split.

---

### Phase 5A: DOM Mutation & Reactivity (No JS)

**Objective**: Make the engine JS-ready without implementing JS.

#### Tasks

| #    | Task                               | Files                     | Notes                         |
| ---- | ---------------------------------- | ------------------------- | ----------------------------- |
| 5A.1 | Implement mutation hooks           | `DOM/DomMutationQueue.cs` | Hook into insertions/removals |
| 5A.2 | Implement dirty marking            | Layout layer              | Mark affected nodes           |
| 5A.3 | Implement incremental reflow       | `LayoutEngine.cs`         | Only relayout dirty subtrees  |
| 5A.4 | Implement event firing on mutation | Event layer               | `DOMNodeInserted`, etc.       |
| 5A.5 | Add mutation tests                 | `Tests/MutationTests.cs`  | Verify dirty propagation      |

#### Acceptance Criteria

- [ ] Adding a node triggers relayout of affected subtree only
- [ ] Removing a node triggers relayout
- [ ] Style changes trigger restyle → relayout cascade
- [ ] Performance acceptable for 1000 mutations/frame

---

### Phase 5B: Script Engine (Optional, Long-Term)

> [!WARNING]
> This phase is **open-ended** and must **not block release**.

Only after everything above is stable:

#### Tasks

| #    | Task                                | Notes                    |
| ---- | ----------------------------------- | ------------------------ |
| 5B.1 | Integrate JavaScript engine         | Jint (C#) or custom      |
| 5B.2 | Implement DOM bindings              | `document`, `window` API |
| 5B.3 | Implement blocking script execution | `<script>` handling      |
| 5B.4 | Implement security model            | Same-origin, CSP         |

#### Acceptance Criteria

- [ ] Simple `document.getElementById` works
- [ ] Event listeners via JS work
- [ ] Console logging works
- [ ] No security escapes

---

## Verification Strategy

### Automated Tests

| Test Category     | Framework             | Count Target         |
| ----------------- | --------------------- | -------------------- |
| Unit Tests        | xUnit                 | 200+ for core layout |
| Integration Tests | Headless rendering    | 50+ page tests       |
| Spec Compliance   | WPT subset            | 1000+ tests          |
| Regression Tests  | Screenshot comparison | 20+ reference pages  |

### Key Reference Tests

| Test               | Purpose              | Phase      |
| ------------------ | -------------------- | ---------- |
| Acid2              | CSS 2.1 compliance   | Phase 1    |
| CSS Test Suites    | Feature-specific     | All phases |
| Web Platform Tests | Standards compliance | Phase 3+   |
| Real-world sites   | Practical validation | Phase 4+   |

---

## Risk Mitigation

| Risk                    | Mitigation                                                      |
| ----------------------- | --------------------------------------------------------------- |
| Scope creep             | Strict exit criteria, no advancement without passing            |
| Partial implementations | "Unsupported" must fail cleanly, not partially work             |
| Spec misinterpretation  | Link to specific spec sections, create test cases from examples |
| Performance regression  | Benchmark suite, reject changes that regress                    |
| Integration complexity  | Adapter pattern for all third-party code                        |

---

## Appendix: File Inventory

### New Files to Create

| File                                           | Purpose                   | Phase |
| ---------------------------------------------- | ------------------------- | ----- |
| `Core/PipelineStage.cs`                        | Pipeline stage enum       | 0.1   |
| `Core/PipelineContext.cs`                      | Pipeline state container  | 0.1   |
| `Core/PhaseGuard.cs`                           | Phase violation detection | 0.1   |
| `Core/InputQueue.cs`                           | Input event buffer        | 0.2   |
| `Core/AnimationFrameScheduler.cs`              | RAF registry              | 0.2   |
| `Layout/MarginCollapseComputer.cs`             | Margin collapse logic     | 1.2   |
| `Layout/ContainingBlockResolver.cs`            | Find containing blocks    | 1.3   |
| `Layout/AbsolutePositionSolver.cs`             | 7-variable equation       | 1.3   |
| `Layout/FlexLayoutComputer.cs`                 | Flexbox algorithm         | 4.1   |
| `Rendering/Css/CssToken.cs`                    | CSS token types           | 3.1   |
| `Rendering/Css/CssTokenizer.cs`                | CSS tokenizer             | 3.1   |
| `Rendering/Css/CascadeComputer.cs`             | CSS cascade               | 3.2   |
| `Rendering/Css/UnitResolver.cs`                | CSS unit resolution       | 3.3   |
| `Rendering/Painting/StackingContextPainter.cs` | Layer painting            | 1.1   |
| `docs/SPECS.md`                                | Specification index       | 0.3   |
| `docs/COMPLIANCE_MATRIX.md`                    | Feature tracking          | 0.3   |

### Files to Modify

| File                              | Changes                   | Phase         |
| --------------------------------- | ------------------------- | ------------- |
| `Core/EngineLoop.cs`              | Full event loop           | 0.2           |
| `Layout/MinimalLayoutComputer.cs` | Integrate margin/position | 1.2, 1.3      |
| `Rendering/StackingContext.cs`    | Full paint order          | 1.1           |
| `Rendering/CssLoader.cs`          | Tokenizer integration     | 3.1, 3.2, 3.3 |
| `Rendering/FontRegistry.cs`       | Async font loading        | 4.2           |
| `DOM/EventTarget.cs`              | Complete event model      | 2.2           |

---

## Progress Tracking

### Phase 0 — Engine Stabilization

- [x] Module 0.1: Canonical Pipeline Contract (Typed Stages exist)
- [x] Module 0.2: Formal Event Loop Skeleton (Established in `EngineLoop`)
- [ ] Module 0.3: Spec Index & Ownership Map

### Phase 1 — Acid2 Foundation

- [x] Module 1.1: Stacking Contexts (Implemented)
- [x] Module 1.2: Full Margin Collapsing (Implemented)
- [x] Module 1.3: Absolute Positioning Resolution (Implemented)
- [ ] **Acid2 Pass** ⭐ (Current Status: ~90% Accuracy)

### Phase 1.5 — HTML Parser

- [x] Module 1.5.1: HTML5 Tokenizer (Custom engine supported)
- [x] Module 1.5.2: Tree Construction

### Phase 2 — DOM & Events

- [x] Module 2.1: DOM Hardening (`Attr`, `Node`, `Event` hierarchy)
- [x] Module 2.2: Event System (Capture/Bubble/Mutation)

### Phase 3 — CSS Parsing

- [x] Module 3.1: CSS Tokenizer (Implemented)
- [x] Module 3.2: Cascade Engine (Specificity sorting implemented)
- [x] Module 3.3: Units & Values (`calc`, viewport units implemented)

### Phase 4 — Modern Layout

- [/] Module 4.1: Complete Flexbox (Partial implementation)
- [x] Module 4.2: Web Fonts Pipeline (Implemented)
- [x] **Hardened**: Fetch API (`JsRequest`, `JsResponse.json`)

### Phase 5A — Reactivity

- [x] DOM Mutation Hooks
- [x] Incremental Reflow

### Phase 5B — Scripting (Complete)

- [x] JS Engine Integration (FenRuntime)
- [x] ES6+ Upgrade (Phases 1-10 Complete)
- [x] DOM Bindings (Mature)

---

_Last Updated: January 18, 2026_
_Document Owner: FenBrowser Engineering_



