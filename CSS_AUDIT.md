# FenBrowser CSS Engine - Comprehensive Audit Report

**Date**: 2026-02-01
**Comparison Targets**: Chromium 130, Firefox 130, Ladybird (latest)

---

## Executive Summary

| CSS Module | FenBrowser | Chromium | Firefox | Ladybird | Notes |
|------------|-----------|----------|---------|----------|-------|
| **Selectors Level 4** | 75/100 | 99 | 99 | 85 | :has(), :is(), :where() present but specificity bugs |
| **CSS Values & Units** | 45/100 | 99 | 99 | 75 | Missing many units (ch, ex, vmin, vmax, fr, etc.) |
| **Color Level 4/5** | 85/100 | 98 | 98 | 70 | Excellent! oklch, oklab, color-mix(), light-dark() |
| **CSS Variables** | 90/100 | 99 | 99 | 85 | Complete with fallback resolution |
| **Cascade & Inheritance** | 60/100 | 99 | 99 | 80 | Missing @layer, @scope |
| **Media Queries Level 4** | 30/100 | 99 | 99 | 70 | Only basic width/height support |
| **@supports** | 0/100 | 99 | 99 | 75 | Not implemented |
| **@container** | 0/100 | 98 | 98 | 40 | Not implemented |
| **Flexbox** | 70/100 | 99 | 99 | 85 | Working but edge cases broken |
| **Grid Layout** | 55/100 | 99 | 99 | 70 | Basic support, missing subgrid |
| **Transforms 2D/3D** | 65/100 | 99 | 99 | 75 | Present but untested edge cases |
| **Animations** | 60/100 | 99 | 99 | 60 | @keyframes works, timing issues |
| **Transitions** | 65/100 | 99 | 99 | 70 | Basic support |
| **Filters/Effects** | 70/100 | 99 | 99 | 65 | Most filters implemented |
| **Typography** | 50/100 | 99 | 99 | 65 | Missing @font-face, variable fonts |
| **Box Model** | 80/100 | 99 | 99 | 85 | Solid implementation |

**Overall CSS Score: 55/100**

---

## Detailed Module Analysis

### 1. CSS Selectors (75/100)

**Implemented:**
- Type, class, ID selectors ✅
- Attribute selectors (`[attr]`, `[attr=value]`, `^=`, `$=`, `*=`, `~=`, `|=`) ✅
- Combinators (descendant, child `>`, adjacent `+`, general sibling `~`) ✅
- `:is()`, `:where()` functional pseudo-classes ✅
- `:has()` relational pseudo-class ✅
- `:not()` with complex selectors ✅
- Structural pseudo-classes (`:first-child`, `:last-child`, `:nth-child()`, etc.) ✅
- State pseudo-classes (`:hover`, `:focus`, `:active`, `:focus-within`) ✅
- Pseudo-elements (`::before`, `::after`, `::first-line`, `::first-letter`, `::marker`) ✅

**Gaps:**
- `:where()` should have 0 specificity but appears to be calculated normally ❌
- `:focus-visible` is aliased to `:focus` (should check keyboard navigation) ❌
- `:current`, `:past`, `:future` (time-dimensional) ❌
- `:dir()`, `:lang()` (internationalization) ❌
- `:scope` pseudo-class ❌
- Namespace selectors (`|E`, `ns|E`) ❌

**File**: [CssSelectorAdvanced.cs](FenBrowser.FenEngine/Rendering/CssSelectorAdvanced.cs)

---

### 2. CSS Values & Units (45/100) - CRITICAL

**Implemented Units:**
- `px` (pixels) ✅
- `em`, `rem` (font-relative) ✅
- `%` (percentage) ✅
- `vw`, `vh` (viewport) ✅
- `deg`, `rad` (angles) ✅
- `s`, `ms` (time) ✅

**Missing Units (CRITICAL):**
- `ch` (0-width character) ❌
- `ex` (x-height) ❌
- `cap` (cap height) ❌
- `ic` (ideographic character) ❌
- `lh`, `rlh` (line-height relative) ❌
- `vmin`, `vmax` (viewport min/max) ❌
- `vi`, `vb` (viewport inline/block) ❌
- `svh`, `svw`, `lvh`, `lvw`, `dvh`, `dvw` (small/large/dynamic viewport) ❌
- `fr` (flex fraction - grid) ❌
- `turn`, `grad` (angle units) ❌
- `Q` (quarter-millimeter) ❌
- `cm`, `mm`, `in`, `pt`, `pc` (absolute units) ❌

**File**: [CssValueParser.cs](FenBrowser.FenEngine/Rendering/Css/CssValueParser.cs) - Only 109 lines!

---

### 3. CSS Color (85/100) - EXCELLENT

**Implemented:**
- Hex colors (#RGB, #RRGGBB, #RGBA, #RRGGBBAA) ✅
- Named colors (via reflection from SkiaSharp) ✅
- `rgb()` / `rgba()` ✅
- `hsl()` / `hsla()` ✅
- `hwb()` ✅
- `oklch()` / `oklab()` ✅
- `lch()` / `lab()` ✅
- `color()` (sRGB, display-p3) ✅
- `color-mix()` ✅
- `light-dark()` ✅
- `currentColor` ✅
- `transparent` ✅

**Minor Gaps:**
- Color interpolation in different color spaces ❌
- `from` keyword in color functions ❌
- Relative color syntax (`rgb(from red r g b / 0.5)`) ❌

**File**: [CssParser.cs](FenBrowser.FenEngine/Rendering/CssParser.cs) - 943 lines, well-implemented

---

### 4. CSS Variables / Custom Properties (90/100)

**Implemented:**
- `--property-name` declaration ✅
- `var(--name)` reference ✅
- `var(--name, fallback)` with fallback ✅
- Recursive resolution (depth limit 10) ✅
- Inheritance from parent ✅
- `:root` scoped variables ✅

**Gaps:**
- `@property` at-rule for typed CSS variables ❌
- Animation of custom properties ❌

**File**: [CssComputed.cs](FenBrowser.Core/Css/CssComputed.cs) - Lines 338-482

---

### 5. Cascade & Specificity (60/100)

**Implemented:**
- User-Agent → User → Author origin cascade ✅
- Specificity (A, B, C) calculation ✅
- Source order tiebreaker ✅
- `!important` handling ✅
- Inheritance for inherited properties ✅
- Initial values ✅

**Critical Gaps:**
- `@layer` cascade layers ❌ (Required for modern frameworks!)
- `@scope` scoped styles ❌
- `:where()` 0-specificity not correctly implemented ❌
- `revert` keyword ❌
- `revert-layer` keyword ❌
- `unset` keyword partial ⚠️

**File**: [CascadeEngine.cs](FenBrowser.FenEngine/Rendering/Css/CascadeEngine.cs)

---

### 6. Media Queries (30/100) - CRITICAL

**Implemented:**
- `screen`, `all`, `print` media types ✅
- `min-width`, `max-width` ✅
- `min-height`, `max-height` ✅
- `and` combinator ✅
- `prefers-color-scheme` ⚠️ (property exists but may not be queried)

**Missing (CRITICAL):**
- `orientation` (portrait/landscape) ❌
- `aspect-ratio`, `min-aspect-ratio`, `max-aspect-ratio` ❌
- `resolution`, `min-resolution`, `max-resolution` ❌
- `pointer` (none/coarse/fine) ❌
- `hover` (none/hover) ❌
- `any-pointer`, `any-hover` ❌
- `color-gamut` (srgb/p3/rec2020) ❌
- `dynamic-range` (standard/high) ❌
- `prefers-reduced-motion` ❌
- `prefers-contrast` ❌
- `forced-colors` ❌
- `inverted-colors` ❌
- `scripting` (none/initial-only/enabled) ❌
- `update` (none/slow/fast) ❌
- Range syntax (`width > 600px`, `600px <= width <= 1200px`) ❌
- `or` combinator ❌
- `not` modifier ❌

**File**: [CssParser.cs](FenBrowser.FenEngine/Rendering/CssParser.cs) - Lines 27-103

---

### 7. @supports (0/100) - NOT IMPLEMENTED

Feature queries are completely missing. This is critical for progressive enhancement.

```css
/* Can't detect feature support */
@supports (display: grid) {
  .container { display: grid; }
}
```

---

### 8. @container (0/100) - NOT IMPLEMENTED

Container queries are completely missing. This is critical for component-based design.

```css
/* Can't query container size */
@container (min-width: 400px) {
  .card { grid-template-columns: 1fr 1fr; }
}
```

---

### 9. Flexbox (70/100)

**Implemented:**
- `display: flex` / `inline-flex` ✅
- `flex-direction` (row, column, row-reverse, column-reverse) ✅
- `flex-wrap` (nowrap, wrap, wrap-reverse) ✅
- `justify-content` ✅
- `align-items` ✅
- `align-content` ✅
- `flex-grow`, `flex-shrink`, `flex-basis` ✅
- `gap`, `row-gap`, `column-gap` ✅
- `order` ✅
- `align-self` ✅

**Gaps:**
- `place-content` shorthand ❌
- `place-items` shorthand ❌
- Edge cases in shrink/grow distribution ⚠️
- `safe` / `unsafe` alignment keywords ❌

**File**: [CssFlexLayout.cs](FenBrowser.FenEngine/Rendering/Css/CssFlexLayout.cs)

---

### 10. Grid Layout (55/100)

**Implemented:**
- `display: grid` / `inline-grid` ✅
- `grid-template-columns`, `grid-template-rows` ✅
- `grid-template-areas` ✅
- `grid-column-start/end`, `grid-row-start/end` ✅
- `grid-area` shorthand ✅
- `gap` ✅
- `grid-auto-flow` ✅
- `grid-auto-columns`, `grid-auto-rows` ✅
- Named lines ✅
- `fr` unit (in grid context) ✅

**Gaps:**
- `subgrid` ❌
- `masonry` (experimental) ❌
- `repeat()` function ⚠️ (may be partial)
- `minmax()` function ⚠️
- `fit-content()` function ⚠️
- Intrinsic keywords (`min-content`, `max-content`) ⚠️

**File**: [CssGridAdvanced.cs](FenBrowser.FenEngine/Rendering/Css/CssGridAdvanced.cs)

---

### 11. CSS Functions (50/100)

**Implemented:**
- `calc()` ✅
- `min()`, `max()`, `clamp()` ✅
- `var()` ✅
- `url()` ✅
- Color functions (rgb, hsl, oklch, etc.) ✅

**Missing:**
- `env()` (safe area insets) ❌
- `attr()` ❌
- `fit-content()` ❌
- `repeat()` ❌
- `minmax()` ❌
- `image()` ❌
- `image-set()` ❌
- `cross-fade()` ❌
- `element()` ❌
- `paint()` (Houdini) ❌
- `counter()`, `counters()` ⚠️
- Math functions (`sin`, `cos`, `tan`, `asin`, `acos`, `atan`, `atan2`, `pow`, `sqrt`, `log`, `exp`, `abs`, `sign`, `round`, `mod`, `rem`) ❌

---

## Priority Implementation Roadmap

### Phase 1: Critical Gaps (Must Fix)
1. **CSS Units** - Add all missing units
2. **@supports** - Feature queries
3. **@layer** - Cascade layers
4. **Media Queries** - Expand to Level 4

### Phase 2: High Priority
5. **@container** - Container queries
6. **CSS Functions** - env(), fit-content(), repeat(), minmax()
7. **:where() specificity** - Fix to be 0
8. **Grid improvements** - subgrid, better intrinsic sizing

### Phase 3: Modern Features
9. **@font-face** - Web fonts
10. **Scroll-driven animations** - timeline, animation-range
11. **View Transitions API**
12. **CSS Nesting** (native)

---

## Files to Modify

| Priority | File | Changes Needed |
|----------|------|----------------|
| P0 | `CssValueParser.cs` | Add all CSS units |
| P0 | `CssParser.cs` | Expand media query support |
| P0 | `CssLoader.cs` | Add @supports, @layer parsing |
| P1 | `CascadeEngine.cs` | Add @layer cascade handling |
| P1 | `CssSelectorAdvanced.cs` | Fix :where() specificity |
| P2 | `CssFunctions.cs` | Add missing functions |
| P2 | `CssGridAdvanced.cs` | Add subgrid support |

---

## Spec References

- [CSS Selectors Level 4](https://www.w3.org/TR/selectors-4/)
- [CSS Values and Units Level 4](https://www.w3.org/TR/css-values-4/)
- [CSS Color Level 4](https://www.w3.org/TR/css-color-4/)
- [CSS Cascade and Inheritance Level 5](https://www.w3.org/TR/css-cascade-5/)
- [Media Queries Level 5](https://www.w3.org/TR/mediaqueries-5/)
- [CSS Containment Module Level 3](https://www.w3.org/TR/css-contain-3/)
- [CSS Conditional Rules Level 4](https://www.w3.org/TR/css-conditional-4/)
