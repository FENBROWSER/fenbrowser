# FenBrowser CSS Engine - Roadmap to 100/100

**Current Score: ~80/100**
**Target: 100/100 (Full CSS Spec Compliance)**

---

## Completed Features (This Session)

- [x] `:focus-visible` with keyboard navigation tracking
- [x] `:dir()` pseudo-class for text direction
- [x] `revert` and `revert-layer` keywords
- [x] `:where()` specificity fix (0,0,0)
- [x] All CSS units (80+ units)
- [x] All 147 CSS named colors
- [x] Media Queries Level 5 expansion
- [x] CSS math functions (sin, cos, tan, sqrt, pow, etc.)

---

## Remaining Features for 100/100

### 1. CSS Nesting (Priority: HIGH)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/Css/CssSyntaxParser.cs`
- `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`

**Implementation:**
```css
/* Native CSS Nesting - must support this syntax */
.parent {
  color: blue;

  .child {
    color: red;
  }

  &:hover {
    color: green;
  }

  & .descendant {
    color: yellow;
  }
}
```

**Steps:**
1. In `CssSyntaxParser.cs`, detect nested rule blocks within style rules
2. Track parent selector context during parsing
3. Expand `&` to parent selector
4. Flatten nested rules into standard selector chains
5. Add `FlattenNesting()` method in `CssLoader.cs`

**Reference:** https://www.w3.org/TR/css-nesting-1/

---

### 2. Subgrid for CSS Grid (Priority: HIGH)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/Css/CssGridAdvanced.cs`
- `FenBrowser.FenEngine/Layout/GridLayoutComputer.cs`

**Implementation:**
```css
.grid {
  display: grid;
  grid-template-columns: 1fr 2fr 1fr;
}
.subgrid-item {
  display: grid;
  grid-template-columns: subgrid; /* Inherit parent's column tracks */
  grid-column: span 3;
}
```

**Steps:**
1. In `ParseGridTemplate()`, detect "subgrid" keyword
2. Store subgrid flag on grid container
3. During layout, if subgrid:
   - Get parent grid's track sizes
   - Use parent tracks instead of own tracks
4. Handle `subgrid` for both columns and rows independently

**Reference:** https://www.w3.org/TR/css-grid-2/#subgrids

---

### 3. Variable Fonts (Priority: MEDIUM)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/FontRegistry.cs`
- `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`
- `FenBrowser.FenEngine/Typography/SkiaFontService.cs`

**Implementation:**
```css
.text {
  font-variation-settings: "wght" 650, "wdth" 100;
  font-optical-sizing: auto;
}
```

**Steps:**
1. Parse `font-variation-settings` value (axis-value pairs)
2. Parse `font-optical-sizing` (auto/none)
3. In `SkiaFontService`, apply variation settings via `SKFont.SetVariationDesignPosition()`
4. Add `FontFaceDescriptor.VariationSettings` parsing
5. Support `font-stretch`, `font-weight` as continuous ranges

**Reference:** https://www.w3.org/TR/css-fonts-4/#font-variation-settings-def

---

### 4. `image-set()` Function (Priority: MEDIUM)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/ImageLoader.cs`
- `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`

**Implementation:**
```css
.hero {
  background-image: image-set(
    "image.webp" type("image/webp"),
    "image.png" 1x,
    "image@2x.png" 2x
  );
}
```

**Steps:**
1. Parse `image-set()` function arguments
2. Extract image URLs with resolution descriptors (1x, 2x) or type hints
3. Select best image based on device pixel ratio (`CssParser.MediaDppx`)
4. Support format hints: `type("image/webp")`
5. Fallback to lower resolution if higher not available

**Reference:** https://www.w3.org/TR/css-images-4/#image-set-notation

---

### 5. `cross-fade()` Function (Priority: LOW)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/ImageLoader.cs`
- `FenBrowser.FenEngine/Rendering/SkiaRenderer.cs`

**Implementation:**
```css
.blend {
  background-image: cross-fade(url(a.png) 30%, url(b.png));
}
```

**Steps:**
1. Parse `cross-fade()` arguments (images with percentages)
2. Load both images
3. Composite using SkiaSharp blend modes with alpha
4. Cache composited result

**Reference:** https://www.w3.org/TR/css-images-4/#cross-fade-function

---

### 6. Scroll-Driven Animations (Priority: MEDIUM)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/CssAnimationEngine.cs`
- `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`

**Implementation:**
```css
@keyframes reveal {
  from { opacity: 0; }
  to { opacity: 1; }
}
.element {
  animation: reveal linear;
  animation-timeline: scroll();
  animation-range: entry 0% entry 100%;
}
```

**Steps:**
1. Parse `animation-timeline: scroll()` and `view()`
2. Parse `animation-range` property
3. Track scroll position during render
4. Map scroll position to animation progress
5. Update animation state based on scroll rather than time

**Reference:** https://www.w3.org/TR/scroll-animations-1/

---

### 7. View Transitions API (Priority: LOW)
**Files to modify:**
- New file: `FenBrowser.FenEngine/Rendering/ViewTransitions.cs`
- `FenBrowser.FenEngine/Rendering/CustomHtmlEngine.cs`

**Implementation:**
```css
::view-transition-old(root) {
  animation: fade-out 0.3s;
}
::view-transition-new(root) {
  animation: fade-in 0.3s;
}
.card {
  view-transition-name: card;
}
```

**Steps:**
1. Implement `view-transition-name` property
2. Add `::view-transition-*` pseudo-elements
3. Capture old state before navigation
4. Animate transition between states
5. Handle `document.startViewTransition()` API

**Reference:** https://www.w3.org/TR/css-view-transitions-1/

---

### 8. CSS Anchor Positioning (Priority: LOW)
**Files to modify:**
- `FenBrowser.FenEngine/Layout/AbsolutePositionSolver.cs`
- `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`

**Implementation:**
```css
.anchor {
  anchor-name: --tooltip-anchor;
}
.tooltip {
  position: absolute;
  position-anchor: --tooltip-anchor;
  top: anchor(bottom);
  left: anchor(center);
}
```

**Steps:**
1. Parse `anchor-name` property
2. Parse `position-anchor` property
3. Parse `anchor()` function in position properties
4. During layout, resolve anchor references
5. Position element relative to anchor

**Reference:** https://www.w3.org/TR/css-anchor-position-1/

---

### 9. `@property` At-Rule (Priority: MEDIUM)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`
- `FenBrowser.Core/Css/CssComputed.cs`

**Implementation:**
```css
@property --my-color {
  syntax: "<color>";
  inherits: false;
  initial-value: red;
}
```

**Steps:**
1. Parse `@property` rules
2. Store property definitions (syntax, inherits, initial-value)
3. Validate custom property values against syntax
4. Enable animation of typed custom properties
5. Use proper initial value from definition

**Reference:** https://www.w3.org/TR/css-properties-values-api-1/

---

### 10. Relative Color Syntax (Priority: MEDIUM)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/Css/CssParser.cs`

**Implementation:**
```css
.element {
  --base: oklch(70% 0.15 200);
  color: oklch(from var(--base) l c calc(h + 30));
  background: rgb(from blue r g b / 50%);
}
```

**Steps:**
1. Parse `from <color>` in color functions
2. Extract components (r, g, b, h, s, l, etc.)
3. Allow `calc()` on color components
4. Reconstruct final color

**Reference:** https://www.w3.org/TR/css-color-5/#relative-colors

---

### 11. Color Interpolation Improvements (Priority: LOW)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/Css/CssParser.cs`
- `FenBrowser.FenEngine/Rendering/CssTransitionEngine.cs`

**Implementation:**
```css
.gradient {
  background: linear-gradient(in oklch, red, blue);
}
```

**Steps:**
1. Parse `in <color-space>` in gradients
2. Convert colors to target color space before interpolation
3. Interpolate in specified color space
4. Support: srgb, srgb-linear, lab, oklab, xyz, xyz-d50, xyz-d65, hsl, hwb, lch, oklch

**Reference:** https://www.w3.org/TR/css-color-4/#interpolation

---

### 12. `@scope` Full Implementation (Priority: MEDIUM)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`
- `FenBrowser.FenEngine/Rendering/Css/CascadeEngine.cs`

**Current:** Basic @scope parsing exists
**Missing:** Proximity-based specificity, `:scope` within @scope

**Implementation:**
```css
@scope (.card) to (.card-footer) {
  :scope { border: 1px solid; }
  p { margin: 0; }
}
```

**Steps:**
1. Track scope boundaries during cascade
2. Calculate scope proximity (generations between scope root and matched element)
3. Use proximity in cascade sorting
4. `:scope` should match scope root, not document root

**Reference:** https://www.w3.org/TR/css-cascade-6/#scope-atrule

---

### 13. `text-wrap: balance` and `pretty` (Priority: LOW)
**Files to modify:**
- `FenBrowser.FenEngine/Layout/TextLayoutComputer.cs`
- `FenBrowser.FenEngine/Typography/TextShaper.cs`

**Implementation:**
```css
h1 { text-wrap: balance; }
p { text-wrap: pretty; }
```

**Steps:**
1. Parse `text-wrap` property values
2. For `balance`: Calculate optimal line breaks to equalize line lengths
3. For `pretty`: Avoid orphans, widows, and awkward breaks
4. Implement Knuth-Plass or similar algorithm

**Reference:** https://www.w3.org/TR/css-text-4/#text-wrap

---

### 14. `@starting-style` At-Rule (Priority: LOW)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`
- `FenBrowser.FenEngine/Rendering/CssTransitionEngine.cs`

**Implementation:**
```css
dialog {
  opacity: 1;
  transition: opacity 0.3s;
}
@starting-style {
  dialog {
    opacity: 0;
  }
}
```

**Steps:**
1. Parse `@starting-style` blocks
2. Apply starting styles on element first render
3. Transition to normal styles

**Reference:** https://www.w3.org/TR/css-transitions-2/#defining-before-change-style-the-starting-style-rule

---

### 15. Container Style Queries (Priority: LOW)
**Files to modify:**
- `FenBrowser.FenEngine/Rendering/Css/CssLoader.cs`

**Current:** Container size queries work
**Missing:** Style queries

**Implementation:**
```css
@container style(--theme: dark) {
  .card { background: #333; }
}
```

**Steps:**
1. Parse `style()` in container queries
2. Evaluate custom property values on container
3. Match based on computed style values

**Reference:** https://www.w3.org/TR/css-contain-3/#container-style-query

---

## Implementation Priority Order

### Phase 1 (Highest Impact)
1. CSS Nesting - Modern frameworks rely on this
2. Subgrid - Critical for complex layouts
3. `@property` - Enables typed custom properties

### Phase 2 (Medium Impact)
4. Variable Fonts - Typography improvement
5. `image-set()` - Responsive images
6. Scroll-Driven Animations - Modern UX patterns
7. Relative Color Syntax - Design system flexibility
8. `@scope` Full Implementation - Component isolation

### Phase 3 (Polish)
9. `cross-fade()` - Image effects
10. View Transitions - Page transitions
11. CSS Anchor Positioning - Tooltips, popovers
12. Color Interpolation - Gradient improvements
13. `text-wrap: balance` - Typography refinement
14. `@starting-style` - Animation polish
15. Container Style Queries - Advanced containment

---

## Testing Strategy

For each feature:
1. Create test HTML file in `FenBrowser.Tests/CssFeatures/`
2. Compare rendering with Chromium reference
3. Add unit tests for parsing
4. Add integration tests for cascade/layout

---

## Spec References

- CSS Nesting: https://www.w3.org/TR/css-nesting-1/
- CSS Grid Level 2: https://www.w3.org/TR/css-grid-2/
- CSS Fonts Level 4: https://www.w3.org/TR/css-fonts-4/
- CSS Images Level 4: https://www.w3.org/TR/css-images-4/
- Scroll Animations: https://www.w3.org/TR/scroll-animations-1/
- View Transitions: https://www.w3.org/TR/css-view-transitions-1/
- Anchor Positioning: https://www.w3.org/TR/css-anchor-position-1/
- Properties and Values: https://www.w3.org/TR/css-properties-values-api-1/
- CSS Color Level 5: https://www.w3.org/TR/css-color-5/
- CSS Cascade Level 6: https://www.w3.org/TR/css-cascade-6/
- CSS Text Level 4: https://www.w3.org/TR/css-text-4/
- CSS Transitions Level 2: https://www.w3.org/TR/css-transitions-2/
- CSS Containment Level 3: https://www.w3.org/TR/css-contain-3/

---

**Last Updated:** 2026-02-01
**Author:** Claude Code Session
