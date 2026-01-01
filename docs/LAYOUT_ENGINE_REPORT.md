# State of the Layout Engine: Detailed Report (2026-01-01)

## 1. How the Engine Works (Current Architecture)

The rendering pipeline is now entirely "native" (custom C# implementation):

1.  **HTML Parsing (`HtmlLiteParser`)**:

    - **Role**: Converts raw HTML strings into a DOM tree (`Core.Dom.Element`).
    - **Mechanism**: A regex/string-scanning parser. It reads tags sequentially.
    - **Status**: **DOWNGRADED**. Unlike AngleSharp, this parser is strict and fragile. It does not handle "tag soup" (malformed HTML) well, lacks standard error recovery (e.g., missing closing tags), and supports fewer HTML5 features (like implicit checking or foster parenting).

2.  **CSS Loading & Parsing (`CustomCssEngine` / `CssLoader`)**:

    - **Role**: Fetches CSS (files/tags), parses rules, and computes styles for every element.
    - **Mechanism**:
      - **Tokenization**: Splits CSS into blocks and rules.
      - **Selector Matching**: `SelectorMatcher` checks if `div.class > span` matches a node.
      - **Cascading**: Sorts rules by specificity and origin (UA, User, Author).
      - **Property Mapping**: Converts strings (`width: 100px`) into typed values (`Computed.Width = 100`) using helpers like `TryPx`.
    - **Status**: **BROKEN SEVERELY**.
      - **Matching**: Previous logs showed the native matcher failing (`matched=false`) for standard selectors that should work. If selectors don't match, elements get _no styles_.
      - **Parsing**: Complex CSS (nested functions, modern pseudo-classes like `:not()`, `:has()`, `:is()`) will likely fail to parse, causing entire rules to be discarded.

3.  **Layout (`MinimalLayoutComputer`)**:

    - **Role**: Calculates geometry (X, Y, Width, Height) based on the computed styles.
    - **Mechanism**: A custom block/inline layout algorithm. It relies on the strongly-typed properties (e.g., `style.Width.HasValue`) populated by the step above.
    - **Status**: **INTACT BUT STARVED**. The logic here is fine, but since the CSS Engine is currently failing to match rules, this engine receives "null" for most styles, causing the "collapsed layout" or "blank screen" issues observed.

4.  **Rendering (`SkiaDomRenderer`)**:
    - **Role**: Draws the calculated boxes to the screen using SkiaSharp.
    - **Status**: **INTACT**.

## 2. What is "Broken" (The Regression List)

By removing AngleSharp, we lost the robust industry-standard parsing layer. Here is specifically what is broken or at risk:

| Feature                   | Status          | Why?                                                                                             |
| :------------------------ | :-------------- | :----------------------------------------------------------------------------------------------- |
| **CSS Rule Matching**     | 🔴 **Critical** | The native `SelectorMatcher` is buggy. It fails to match valid rules, leaving pages unstyled.    |
| **HTML Error Correction** | 🟠 **Major**    | `HtmlLiteParser` lacks standard error recovery. Malformed HTML may break the DOM tree structure. |
| **Complex CSS Functions** | 🟠 **Major**    | Functions like `min()`, `max()`, `clamp()`, `color-mix()` may fail to parse correctly.           |
| **Pseudo-Classes**        | 🟡 **Moderate** | Support for `:first-child`, `:nth-of-type`, etc., is manual and likely incomplete.               |
| **Variables (`var()`)**   | 🟡 **Moderate** | Basic support exists, but scope handling/nesting is likely less accurate than spec.              |

## 3. Summary of Findings

The _property mapping_ logic (converting `width` string to `double`) **EXISTS** in the native engine (lines 2400-2800 of `CssLoader.cs`). The layout engine is also functional. The primary blockers for correct rendering are now:

1.  **Rule Matching**: Fixing why valid CSS selectors are not matching elements.
2.  **Parser Robustness**: Improving the tolerance of `HtmlLiteParser` and the breadth of `CssParser`.
