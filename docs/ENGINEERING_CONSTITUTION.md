# FenBrowser Engineering Constitution

> **"Architecture is Destiny"**
> These 5 rules are non-negotiable. Violating them means building a fragile tech demo, not a browser.

---

## RULE 1: You Own Layout. Libraries Assist, Never Decide.

### The Law

Layout authority lives **ONLY** in FenEngine.

| Library     | Role            | What It CANNOT Do  |
| ----------- | --------------- | ------------------ |
| HarfBuzz    | Shaping ONLY    | Decide line breaks |
| Skia        | Drawing ONLY    | Decide line height |
| RichTextKit | Optional helper | Be authoritative   |

### Enforcement

```csharp
// ❌ FORBIDDEN - RichTextKit decides layout
var tb = new TextBlock();
tb.MaxWidth = 200; // RichTextKit chooses line breaks!
tb.Paint(canvas);

// ✅ REQUIRED - FenEngine decides, RichTextKit obeys
var width = TextMeasurer.Measure(text);
var breaks = FenEngine.CalculateBreakPoints(text, maxWidth); // WE decide
foreach (var line in breaks) {
    RenderQueue.Add(new DrawTextCommand(line.Text, line.X, line.Y));
}
```

---

## RULE 2: Separate Layout Metrics from Render Metrics

### The Law

FenEngine maintains its own metric models. Skia informs, never defines.

### Required Abstractions

```csharp
// FenEngine's model (CSS-semantic)
public struct NormalizedFontMetrics {
    public float Ascent;     // CSS baseline
    public float Descent;
    public float LineHeight; // The "strut" - WE calculate this
    public float XHeight;    // For 'ex' units
}

// Skia provides data, we normalize it
metrics.LineHeight = fontSize * 1.2f; // NOT Skia's suggestion
```

### Why This Matters

- Skia says: "Ascent is 19.2px"
- CSS says: "Line-height is normal (1.2x)"
- **FenEngine says**: "Line box is 24px. Center the glyphs."

---

## RULE 3: SVG Must Be Sandboxed Deliberately

### The Law

SVG is an implicit attack surface. It must be explicitly constrained.

### Required Limits

| Limit               | Value    | Rationale                 |
| ------------------- | -------- | ------------------------- |
| Max recursion depth | 32       | Prevent stack overflow    |
| Max filter count    | 10       | Prevent DoS               |
| Max render time     | 100ms    | Prevent freeze            |
| External references | DISABLED | Prevent data exfiltration |

### Failure Mode

SVG failures **degrade gracefully**. Never crash.

```csharp
try {
    var svg = SvgRenderer.Render(svgContent, limits);
} catch (SvgComplexityException) {
    return FallbackPlaceholder(); // Gray box, not crash
}
```

---

## RULE 4: Rendering Backend Must Be Abstract

### The Law

No OpenGL/Skia types may escape the rendering layer.

### Required Interface

```csharp
public interface IRenderBackend {
    void DrawRect(Rect rect, Color color);
    void DrawBorder(Rect rect, BorderData border);
    void DrawGlyphRun(Point location, GlyphRun glyphs, Color color);
    void PushClip(Rect clipRect);
    void PopClip();
    void PushLayer(float opacity);
    void PopLayer();
}
```

### Why This Matters

- **Testing**: HeadlessRenderer logs commands for unit tests
- **Portability**: Swap Skia for Direct2D without touching layout
- **Future-proof**: macOS deprecated OpenGL; we're ready

---

## RULE 5: Long-Term Dependency Ownership

### The Law

For every hot-path dependency: "If it dies tomorrow, can we survive?"

### Current Assessment

| Dependency  | Survival | Action Required             |
| ----------- | -------- | --------------------------- |
| SkiaSharp   | ✅ YES   | None                        |
| HarfBuzz    | ✅ YES   | None                        |
| Silk.NET    | ✅ YES   | None                        |
| RichTextKit | ⚠️ MAYBE | Wrap behind `ITextMeasurer` |
| Svg.Skia    | ⚠️ MAYBE | Wrap behind `ISvgRenderer`  |

### Adapter Pattern

```
FenEngine.Core.ITextMeasurer        (Interface)
    └── FenEngine.Adapters.RichTextKitMeasurer  (Replaceable)

FenEngine.Core.ISvgRenderer         (Interface)
    └── FenEngine.Adapters.SvgSkiaRenderer      (Replaceable)
```

If RichTextKit dies in 2026:

1. Delete `RichTextKitMeasurer.cs`
2. Write `HarfBuzzMeasurer.cs`
3. Browser doesn't know anything changed

---

## Compliance Checklist

Before any merge to main:

- [ ] No `TextBlock.MaxWidth` usage
- [ ] No raw `SKFontMetrics` in layout code
- [ ] No `SKCanvas` outside `IRenderBackend`
- [ ] No direct `Svg.Skia` calls outside adapter
- [ ] No direct `RichTextKit` calls outside adapter
- [ ] SVG render limits enforced
- [ ] All new code has unit tests via HeadlessRenderer

---

_This constitution supersedes all previous architectural decisions._
_Last updated: December 29, 2024_
