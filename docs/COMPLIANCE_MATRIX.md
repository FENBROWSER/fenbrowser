# FenEngine Compliance Matrix

> **Purpose**: Feature-by-feature tracking of specification compliance.
> Use this document to track progress and identify gaps.

---

## Summary Dashboard

| Category      | ✅ Implemented | ⚠️ Partial | ❌ Missing | Total  | Coverage |
| ------------- | -------------- | ---------- | ---------- | ------ | -------- |
| HTML Parsing  | 0              | 1          | 5          | 6      | 8%       |
| DOM Core      | 2              | 4          | 2          | 8      | 50%      |
| DOM Events    | 4              | 1          | 1          | 6      | 75%      |
| CSS Syntax    | 0              | 2          | 3          | 5      | 20%      |
| CSS Cascade   | 0              | 5          | 1          | 6      | 42%      |
| CSS Box Model | 0              | 4          | 3          | 7      | 29%      |
| CSS Flexbox   | 0              | 6          | 2          | 8      | 38%      |
| CSS Grid      | 0              | 3          | 4          | 7      | 21%      |
| Stacking      | 2              | 2          | 2          | 6      | 50%      |
| Event Loop    | 4              | 0          | 0          | 4      | 100%     |
| **TOTAL**     | **12**         | **28**     | **23**     | **63** | **41%**  |

---

## Detailed Feature Matrix

### HTML Parsing (WHATWG HTML §13)

| Feature           | Status | Test Coverage | Notes                        |
| ----------------- | ------ | ------------- | ---------------------------- |
| HTML5 Tokenizer   | ⚠️     | ❌            | Custom implementation needed |
| Tree Construction | ❌     | ❌            | Custom implementation needed |
| Insertion Modes   | ❌     | ❌            | Custom implementation needed |
| Implied End Tags  | ❌     | ❌            | Custom implementation needed |
| Adoption Agency   | ❌     | ❌            | Custom implementation needed |
| Foster Parenting  | ❌     | ❌            | Custom implementation needed |

**Action Items**:

- [ ] Create tokenizer interface
- [ ] Expose tree building for debugging
- [ ] Add malformed HTML test suite

---

### DOM Core (DOM Living Standard)

| Feature                          | Status | Test Coverage | Spec Section |
| -------------------------------- | ------ | ------------- | ------------ |
| `Node` interface                 | ⚠️     | ⚠️            | §4.4         |
| `Element` interface              | ⚠️     | ⚠️            | §4.9         |
| `Attr` objects                   | ❌     | ❌            | §4.9.1       |
| `nodeType` constants             | ✅     | ⚠️            | §4.4         |
| `textContent`                    | ⚠️     | ❌            | §4.4.3       |
| `parentNode`, `childNodes`       | ✅     | ⚠️            | §4.4         |
| `firstChild`, `lastChild`        | ⚠️     | ❌            | §4.4         |
| `previousSibling`, `nextSibling` | ❌     | ❌            | §4.4         |

**Action Items**:

- [ ] Implement `Attr` class (not string dictionary)
- [ ] Verify `textContent` getter/setter
- [ ] Add sibling navigation

---

### DOM Events (UI Events)

| Feature                      | Status | Test Coverage | Spec Section |
| ---------------------------- | ------ | ------------- | ------------ |
| Event dispatch               | ✅     | ⚠️            | §2.1         |
| Capture phase                | ✅     | ⚠️            | §3.1         |
| Bubble phase                 | ✅     | ⚠️            | §3.1         |
| `stopPropagation()`          | ✅     | ⚠️            | §3.1         |
| `stopImmediatePropagation()` | ⚠️     | ❌            | §3.1         |
| `preventDefault()`           | ❌     | ❌            | §3.1         |

**Action Items**:

- [ ] Verify `stopImmediatePropagation()` implementation
- [ ] Add comprehensive event phase tests

---

### CSS Syntax (CSS Syntax L3)

| Feature                                 | Status | Test Coverage | Spec Section |
| --------------------------------------- | ------ | ------------- | ------------ |
| Tokenizer                               | ❌     | ❌            | §4           |
| Token types (`ident`, `function`, etc.) | ❌     | ❌            | §4.2         |
| Parser                                  | ⚠️     | ⚠️            | §5           |
| Error recovery                          | ⚠️     | ❌            | §2.2         |
| At-rules                                | ❌     | ❌            | §5.4.4       |

**Priority**: HIGH - Regex parsing is a known bug source.

**Action Items**:

- [ ] Implement CSS tokenizer (state machine)
- [ ] Define token types enum
- [ ] Implement error recovery per spec

---

### CSS Cascade (CSS Cascade L4)

| Feature                           | Status | Test Coverage | Spec Section |
| --------------------------------- | ------ | ------------- | ------------ |
| Origin order (UA < User < Author) | ⚠️     | ❌            | §6.1         |
| `!important`                      | ⚠️     | ❌            | §6.4         |
| Specificity                       | ⚠️     | ❌            | §6.5         |
| Source order                      | ⚠️     | ❌            | §6.6         |
| `inherit`                         | ⚠️     | ❌            | §7.1         |
| `initial`, `unset`                | ❌     | ❌            | §7.2-7.3     |

**Action Items**:

- [ ] Formalize cascade algorithm
- [ ] Add specificity calculation tests
- [ ] Implement `initial` and `unset` keywords

---

### CSS Box Model (CSS 2.1 §8, CSS Box Model L3)

| Feature                 | Status | Test Coverage | Spec Section |
| ----------------------- | ------ | ------------- | ------------ |
| Margin computation      | ⚠️     | ⚠️            | §8.3         |
| Margin collapsing       | ❌     | ❌            | §8.3.1       |
| Padding computation     | ⚠️     | ⚠️            | §8.4         |
| Border computation      | ⚠️     | ⚠️            | §8.5         |
| `box-sizing`            | ⚠️     | ❌            | CSS Box L3   |
| `width`/`height` auto   | ❌     | ❌            | §10.3-10.6   |
| `min-width`/`max-width` | ❌     | ❌            | §10.4, §10.7 |

**Priority**: HIGH for Acid2

**Action Items**:

- [ ] Implement margin collapsing algorithm
- [ ] Add width/height auto resolution
- [ ] Implement min/max constraints

---

### CSS Flexbox (CSS Flexbox L1)

| Feature                      | Status | Test Coverage | Spec Section |
| ---------------------------- | ------ | ------------- | ------------ |
| Flex container               | ⚠️     | ⚠️            | §3           |
| Flex items                   | ⚠️     | ⚠️            | §4           |
| Flex base size               | ⚠️     | ❌            | §9.2         |
| Collecting flex lines        | ⚠️     | ❌            | §9.3         |
| Distributing free space      | ⚠️     | ❌            | §9.7         |
| Cross axis sizing            | ⚠️     | ❌            | §9.4         |
| `justify-content`            | ❌     | ❌            | §8.3         |
| `align-items` / `align-self` | ❌     | ❌            | §8.3         |

**Action Items**:

- [ ] Complete flex line collection
- [ ] Implement free space distribution properly
- [ ] Implement all alignment properties

---

### CSS Grid (CSS Grid L1)

| Feature        | Status | Test Coverage | Spec Section |
| -------------- | ------ | ------------- | ------------ |
| Grid container | ⚠️     | ⚠️            | §5           |
| Explicit grid  | ⚠️     | ❌            | §7           |
| Implicit grid  | ❌     | ❌            | §7.5         |
| Auto placement | ❌     | ❌            | §8           |
| Track sizing   | ⚠️     | ❌            | §7.2         |
| `fr` units     | ❌     | ❌            | §7.2.3       |
| Alignment      | ❌     | ❌            | §10          |

**Action Items**:

- [ ] Implement implicit grid tracks
- [ ] Implement auto-placement algorithm
- [ ] Implement `fr` unit distribution

---

### Stacking Context (CSS 2.1 Appendix E)

| Feature                     | Status | Test Coverage | Spec Section |
| --------------------------- | ------ | ------------- | ------------ |
| Root stacking context       | ✅     | ⚠️            | E.2          |
| Positioned + z-index        | ✅     | ⚠️            | E.2          |
| Negative z-index painting   | ⚠️     | ❌            | E.2          |
| Positive z-index painting   | ⚠️     | ❌            | E.2          |
| `opacity` creates context   | ❌     | ❌            | CSS3         |
| `transform` creates context | ❌     | ❌            | CSS3         |

**Action Items**:

- [ ] Implement 7-phase paint order
- [ ] Add CSS3 stacking context triggers
- [ ] Add paint order tests

---

### Event Loop (WHATWG HTML §8.1.7)

| Feature          | Status | Test Coverage | Spec Section |
| ---------------- | ------ | ------------- | ------------ |
| Task queue       | ✅     | ⚠️            | §8.1.7.1     |
| Microtask queue  | ✅     | ⚠️            | §8.1.7.2     |
| Rendering update | ✅     | ⚠️            | §8.1.7.3     |
| Animation frames | ✅     | ⚠️            | §8.1.7.4     |

**Status**: COMPLETE ✅

---

## Test Coverage Summary

| Test Category           | Count | Status                   |
| ----------------------- | ----- | ------------------------ |
| Unit Tests              | ~50   | ⚠️ Need expansion        |
| Integration Tests       | ~10   | ❌ Need more             |
| Spec Compliance Tests   | 0     | ❌ Need WPT subset       |
| Visual Regression Tests | 0     | ❌ Need screenshot tests |

---

## Priority Matrix

### P0 - Critical (Acid2 Blockers)

1. ❌ Margin Collapsing (CSS Box §8.3.1)
2. ❌ Absolute Positioning (CSS 2.1 §10.1-10.6)
3. ⚠️ Stacking Context Paint Order (CSS 2.1 Appendix E)

### P1 - High (Modern Layout)

1. ⚠️ Complete Flexbox Algorithm
2. ❌ CSS Tokenizer (replace regex)
3. ❌ Complete Grid Auto-Placement

### P2 - Medium (Correctness)

1. ❌ `Attr` objects (DOM spec)
2. ⚠️ Cascade formalization
3. ⚠️ Specificity calculation

### P3 - Low (Polish)

1. ⚠️ Event phase edge cases
2. ❌ Font loading waterfall
3. ❌ CSS variables inheritance

---

## Change Log

| Date       | Change                 | By     |
| ---------- | ---------------------- | ------ |
| 2024-12-30 | Initial matrix created | System |

---

_This document should be updated as features are implemented._
