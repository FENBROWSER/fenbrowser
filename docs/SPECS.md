# FenEngine Specification Index

> **Purpose**: Central reference mapping engine subsystems to their authoritative specifications.
> Every code module should reference this document and link to the specific spec sections it implements.

---

## Quick Reference Table

| Subsystem            | Primary Spec                                                                             | Version         | Status             | Owner                                       |
| -------------------- | ---------------------------------------------------------------------------------------- | --------------- | ------------------ | ------------------------------------------- |
| **HTML Parsing**     | [WHATWG HTML §13](https://html.spec.whatwg.org/multipage/parsing.html)                   | Living Standard | ❌ Not Implemented | —                                           |
| **DOM Core**         | [DOM Living Standard](https://dom.spec.whatwg.org/)                                      | 2024-12         | ⚠️ Partial         | `Core/Dom/`                                 |
| **DOM Events**       | [UI Events](https://www.w3.org/TR/uievents/)                                             | W3C WD          | ⚠️ Partial         | `FenEngine/DOM/EventTarget.cs`              |
| **CSS Syntax**       | [CSS Syntax L3](https://www.w3.org/TR/css-syntax-3/)                                     | CR 2021         | ❌ Regex-based     | `FenEngine/Rendering/CssLoader.cs`          |
| **CSS Cascade**      | [CSS Cascade L4](https://www.w3.org/TR/css-cascade-4/)                                   | CR 2022         | ⚠️ Partial         | `FenEngine/Rendering/CssLoader.cs`          |
| **CSS Box Model**    | [CSS Box Model L3](https://www.w3.org/TR/css-box-3/)                                     | CR 2023         | ⚠️ Partial         | `FenEngine/Layout/`                         |
| **CSS 2.1 VFM**      | [CSS 2.1 §9-10](https://www.w3.org/TR/CSS21/visuren.html)                                | REC 2011        | ⚠️ Partial         | `FenEngine/Layout/MinimalLayoutComputer.cs` |
| **CSS Flexbox**      | [CSS Flexbox L1](https://www.w3.org/TR/css-flexbox-1/)                                   | CR 2018         | ⚠️ Partial         | `FenEngine/Layout/MinimalLayoutComputer.cs` |
| **CSS Grid**         | [CSS Grid L1](https://www.w3.org/TR/css-grid-1/)                                         | CR 2020         | ⚠️ Partial         | `FenEngine/Rendering/CssGridAdvanced.cs`    |
| **Stacking Context** | [CSS 2.1 Appendix E](https://www.w3.org/TR/CSS21/zindex.html)                            | REC 2011        | ⚠️ Partial         | `FenEngine/Rendering/StackingContext.cs`    |
| **Event Loop**       | [WHATWG HTML §8.1.7](https://html.spec.whatwg.org/multipage/webappapis.html#event-loops) | Living Standard | ✅ Good            | `FenEngine/Core/EventLoop/`                 |
| **Web Fonts**        | [CSS Fonts L4](https://www.w3.org/TR/css-fonts-4/)                                       | WD 2024         | ⚠️ Partial         | `FenEngine/Rendering/FontRegistry.cs`       |

---

## Detailed Subsystem Mappings

### 1. HTML Parsing

**Primary Spec**: [WHATWG HTML Living Standard §13 - Parsing](https://html.spec.whatwg.org/multipage/parsing.html)

| Component         | Spec Section | Status | Notes                        |
| ----------------- | ------------ | ------ | ---------------------------- |
| Tokenizer         | §13.2.5      | ❌     | Custom implementation needed |
| Tree Construction | §13.2.6      | ❌     | Custom implementation needed |
| Error Handling    | §13.2.6.4.7  | ❌     | Adoption Agency not explicit |
| Script Execution  | §13.2.7      | ❌     | Blocking scripts not handled |

**Recommended**: Implement HTML5 tokenizer interface.

---

### 2. DOM Core

**Primary Spec**: [DOM Living Standard](https://dom.spec.whatwg.org/)

| Component            | Spec Section | File                  | Status              |
| -------------------- | ------------ | --------------------- | ------------------- |
| `Node` interface     | §4.4         | `Core/Dom/Node.cs`    | ⚠️ Partial          |
| `Element` interface  | §4.9         | `Core/Dom/Element.cs` | ⚠️ Partial          |
| `Attr` interface     | §4.9.1       | —                     | ❌ Using dictionary |
| `nodeType` constants | §4.4         | `Core/Dom/Node.cs`    | ✅ Defined          |
| `textContent`        | §4.4.3       | `Core/Dom/Node.cs`    | ⚠️ Verify           |
| Tree traversal       | §4.4         | `Core/Dom/Node.cs`    | ⚠️ Partial          |

---

### 3. DOM Events

**Primary Spec**: [UI Events](https://www.w3.org/TR/uievents/)

| Component                    | Spec Section | File                           | Status         |
| ---------------------------- | ------------ | ------------------------------ | -------------- |
| Event dispatch               | §2.1         | `FenEngine/DOM/EventTarget.cs` | ✅ Good        |
| Capture phase                | §3.1         | `FenEngine/DOM/EventTarget.cs` | ✅ Implemented |
| Bubble phase                 | §3.1         | `FenEngine/DOM/EventTarget.cs` | ✅ Implemented |
| `stopPropagation()`          | §3.1         | `FenEngine/DOM/DomEvent.cs`    | ✅ Implemented |
| `preventDefault()`           | §3.1         | `FenEngine/DOM/DomEvent.cs`    | ✅ Implemented |
| `stopImmediatePropagation()` | §3.1         | —                              | ⚠️ Verify      |

---

### 4. CSS Syntax

**Primary Spec**: [CSS Syntax Level 3](https://www.w3.org/TR/css-syntax-3/)

| Component      | Spec Section | File                               | Status          |
| -------------- | ------------ | ---------------------------------- | --------------- |
| Tokenizer      | §4           | `FenEngine/Rendering/CssLoader.cs` | ❌ Regex-based  |
| Parser         | §5           | `FenEngine/Rendering/CssParser.cs` | ⚠️ Partial      |
| Error recovery | §2.2         | —                                  | ⚠️ Inconsistent |

**Action Required**: Replace regex parsing with spec-compliant tokenizer.

---

### 5. CSS Cascade

**Primary Spec**: [CSS Cascade Level 4](https://www.w3.org/TR/css-cascade-4/)

| Component                     | Spec Section | File                               | Status      |
| ----------------------------- | ------------ | ---------------------------------- | ----------- |
| Cascade order                 | §6           | `FenEngine/Rendering/CssLoader.cs` | ⚠️ Partial  |
| Origin sorting                | §6.1         | —                                  | ⚠️ Implicit |
| `!important`                  | §6.4         | `FenEngine/Rendering/CssLoader.cs` | ⚠️ Partial  |
| Specificity                   | §6.5         | `FenEngine/Rendering/CssLoader.cs` | ⚠️ Partial  |
| `inherit`, `initial`, `unset` | §7           | `FenEngine/Rendering/CssLoader.cs` | ⚠️ Partial  |

---

### 6. CSS Visual Formatting Model (CSS 2.1)

**Primary Spec**: [CSS 2.1 §9-10](https://www.w3.org/TR/CSS21/visuren.html)

| Component                 | Spec Section | File                       | Status             |
| ------------------------- | ------------ | -------------------------- | ------------------ |
| Block formatting context  | §9.4.1       | `MinimalLayoutComputer.cs` | ⚠️ Partial         |
| Inline formatting context | §9.4.2       | `InlineLayoutComputer.cs`  | ⚠️ Partial         |
| Positioning schemes       | §9.3         | `MinimalLayoutComputer.cs` | ⚠️ Partial         |
| Float behavior            | §9.5         | `CssFloatLayout.cs`        | ⚠️ Partial         |
| Normal flow               | §9.4         | `MinimalLayoutComputer.cs` | ⚠️ Partial         |
| Margin collapsing         | §8.3.1       | —                          | ❌ Not Implemented |
| Containing block          | §10.1        | —                          | ⚠️ Implicit        |
| Width calculation         | §10.3        | `MinimalLayoutComputer.cs` | ⚠️ Partial         |
| Height calculation        | §10.6        | `MinimalLayoutComputer.cs` | ⚠️ Partial         |

---

### 7. CSS Flexbox

**Primary Spec**: [CSS Flexbox Level 1](https://www.w3.org/TR/css-flexbox-1/)

| Component         | Spec Section | File                       | Status     |
| ----------------- | ------------ | -------------------------- | ---------- |
| Flex container    | §3           | `MinimalLayoutComputer.cs` | ⚠️ Partial |
| Flex items        | §4           | `MinimalLayoutComputer.cs` | ⚠️ Partial |
| Flex lines        | §9.3         | `MinimalLayoutComputer.cs` | ⚠️ Partial |
| Main axis sizing  | §9.2         | `MinimalLayoutComputer.cs` | ⚠️ Partial |
| Cross axis sizing | §9.4         | `MinimalLayoutComputer.cs` | ⚠️ Partial |
| `flex-grow`       | §9.7         | `MinimalLayoutComputer.cs` | ⚠️ Partial |
| `flex-shrink`     | §9.7         | `MinimalLayoutComputer.cs` | ⚠️ Partial |
| Alignment         | §8           | `MinimalLayoutComputer.cs` | ⚠️ Partial |

---

### 8. Stacking Context

**Primary Spec**: [CSS 2.1 Appendix E](https://www.w3.org/TR/CSS21/zindex.html)

| Component                 | Spec Section | File                     | Status         |
| ------------------------- | ------------ | ------------------------ | -------------- |
| Stacking context creation | E.2          | `StackingContext.cs`     | ⚠️ Partial     |
| Paint order (7 phases)    | E.2          | `StackingContextBuilder` | ⚠️ Partial     |
| Negative z-index          | E.2          | `StackingContext.cs`     | ✅ Implemented |
| Positive z-index          | E.2          | `StackingContext.cs`     | ✅ Implemented |

---

### 9. Event Loop

**Primary Spec**: [WHATWG HTML §8.1.7](https://html.spec.whatwg.org/multipage/webappapis.html#event-loops)

| Component        | Spec Section | File                                     | Status  |
| ---------------- | ------------ | ---------------------------------------- | ------- |
| Task queues      | §8.1.7.1     | `Core/EventLoop/TaskQueue.cs`            | ✅ Good |
| Microtask queue  | §8.1.7.2     | `Core/EventLoop/MicrotaskQueue.cs`       | ✅ Good |
| Rendering update | §8.1.7.3     | `Core/EventLoop/EventLoopCoordinator.cs` | ✅ Good |
| Animation frame  | §8.1.7.4     | `Core/EventLoop/EventLoopCoordinator.cs` | ✅ Good |

---

## Status Legend

| Symbol | Meaning                           |
| ------ | --------------------------------- |
| ✅     | Fully implemented and tested      |
| ⚠️     | Partially implemented or untested |
| ❌     | Not implemented                   |

---

## Adding Spec References to Code

Every source file should include a header comment with spec references:

```csharp
// =============================================================================
// ClassName.cs
// Brief description
//
// SPEC REFERENCE: [Spec Name] [Section]
//                 [URL]
//
// STATUS: ✅ Implemented | ⚠️ Partial | ❌ Stub
// =============================================================================
```

### Example

```csharp
// =============================================================================
// StackingContext.cs
// CSS Stacking Context management for z-index ordering
//
// SPEC REFERENCE: CSS 2.1 Appendix E - Elaborate description of Stacking Contexts
//                 https://www.w3.org/TR/CSS21/zindex.html
//
// STATUS: ⚠️ Partial - Missing CSS3 stacking context triggers (opacity, transform)
// =============================================================================
```

---

## References

### Primary Specifications

1. **WHATWG HTML**: https://html.spec.whatwg.org/multipage/
2. **DOM Standard**: https://dom.spec.whatwg.org/
3. **CSS 2.1**: https://www.w3.org/TR/CSS21/
4. **CSS Syntax L3**: https://www.w3.org/TR/css-syntax-3/
5. **CSS Cascade L4**: https://www.w3.org/TR/css-cascade-4/
6. **CSS Box Model L3**: https://www.w3.org/TR/css-box-3/
7. **CSS Flexbox L1**: https://www.w3.org/TR/css-flexbox-1/
8. **CSS Grid L1**: https://www.w3.org/TR/css-grid-1/
9. **UI Events**: https://www.w3.org/TR/uievents/
10. **CSS Fonts L4**: https://www.w3.org/TR/css-fonts-4/

### Test Suites

1. **Acid2**: https://www.webstandards.org/files/acid2/test.html
2. **Web Platform Tests**: https://web-platform-tests.org/
3. **CSS Test Suite**: https://test.csswg.org/

---

_Last Updated: December 30, 2024_
