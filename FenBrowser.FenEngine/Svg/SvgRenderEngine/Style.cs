using System.Collections.Generic;
using System.Diagnostics;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        // ------------------------------------------------------- style carrier

        /// <summary>
        /// Resolved paint for fill/stroke. Kind uses <see cref="SvgValues.PaintKind"/>
        /// so there is exactly ONE paint-kind vocabulary in the module.
        /// </summary>
        private readonly struct PaintSpec
        {
            public PaintSpec(
                SvgValues.PaintKind kind,
                SKColor color,
                string fragment,
                string fallback,
                SvgElement contextSource = null)
            {
                Kind = kind;
                Color = color;
                Fragment = fragment;
                Fallback = fallback;
                ContextSource = contextSource;
            }

            public SvgValues.PaintKind Kind { get; }
            public SKColor Color { get; }
            public string Fragment { get; }
            public string Fallback { get; }
            public SvgElement ContextSource { get; }

            public static readonly PaintSpec Black =
                new(SvgValues.PaintKind.Color, SKColors.Black, null, null);
            public static readonly PaintSpec None =
                new(SvgValues.PaintKind.None, default, null, null);
        }

        private enum PaintPhase : byte
        {
            Fill,
            Stroke,
            Markers
        }

        private readonly struct SvgPaintOrder
        {
            public SvgPaintOrder(PaintPhase first, PaintPhase second, PaintPhase third)
            {
                First = first;
                Second = second;
                Third = third;
            }

            public PaintPhase First { get; }
            public PaintPhase Second { get; }
            public PaintPhase Third { get; }

            public PaintPhase At(int index) => index switch
            {
                0 => First,
                1 => Second,
                _ => Third
            };

            public static SvgPaintOrder Normal { get; } =
                new(PaintPhase.Fill, PaintPhase.Stroke, PaintPhase.Markers);
        }

        private sealed class InheritedStyle
        {
            public PaintSpec Fill = PaintSpec.Black;      // spec default: black
            public PaintSpec Stroke = PaintSpec.None;     // spec default: none
            public float FillOpacity = 1f;
            public float StrokeOpacity = 1f;
            public float InheritedFillOpacity = 1f;
            public float InheritedStrokeOpacity = 1f;
            public float StrokeWidth = 1f;
            public SKStrokeCap Cap = SKStrokeCap.Butt;
            public SKStrokeJoin Join = SKStrokeJoin.Miter;
            public float MiterLimit = 4f;
            public float[] Dash;
            public float DashOffset;
            public SKColor CurrentColor = SKColors.Black;
            public PaintSpec? ContextFill;
            public PaintSpec? ContextStroke;
            public SvgElement ContextSource;
            public bool Visibility = true;
            public float FontSize = DefaultFontSize;
            public float RootFontSize = DefaultFontSize;
            public SvgPaintOrder PaintOrder = SvgPaintOrder.Normal;

            public InheritedStyle Clone() => (InheritedStyle)MemberwiseClone();

            /// <summary>
            /// Applies THIS element's presentation attributes plus its inline
            /// style="" declarations (which win over presentation attributes per
            /// the CSS cascade) on top of the inherited state.
            ///
            /// P0.2: all properties are evaluated against locals FIRST and a
            /// single clone is made only when something changed - previously up
            /// to nine clones per element.
            /// Returns the same instance when nothing is overridden, keeping
            /// sibling branches independent through copy-on-write.
            /// </summary>
            public InheritedStyle ResolveOverrides(SvgElement el, SvgParseReport report)
            {
                string Attr(string name) => el.GetPresentationProperty(name);

                SKColor currentColor = CurrentColor;
                var colorRaw = Attr("color");
                if (!string.IsNullOrWhiteSpace(colorRaw) &&
                    SvgValues.TryParseColor(colorRaw.AsSpan(), out var cc))
                {
                    currentColor = cc;
                }

                bool hasOwnFillOpacity = TryParseOpacity(Attr("fill-opacity"), out float ownFillOpacity);
                bool hasOwnStrokeOpacity = TryParseOpacity(Attr("stroke-opacity"), out float ownStrokeOpacity);

                var s = new InheritedStyle
                {
                    CurrentColor = currentColor,
                    ContextFill = ContextFill,
                    ContextStroke = ContextStroke,
                    ContextSource = el.Name == "use" ? el : ContextSource,
                    Fill = ParsePaintSpec(Attr("fill"), this) ?? Fill,
                    Stroke = ParsePaintSpec(Attr("stroke"), this) ?? Stroke,
                    FillOpacity = hasOwnFillOpacity ? ownFillOpacity : FillOpacity,
                    StrokeOpacity = hasOwnStrokeOpacity ? ownStrokeOpacity : StrokeOpacity,
                    InheritedFillOpacity = hasOwnFillOpacity ? 1f : FillOpacity,
                    InheritedStrokeOpacity = hasOwnStrokeOpacity ? 1f : StrokeOpacity,
                    StrokeWidth = StrokeWidth,
                    Cap = Cap,
                    Join = Join,
                    MiterLimit = MiterLimit,
                    Dash = Dash,
                    DashOffset = DashOffset,
                    Visibility = Visibility,
                    FontSize = ResolveFontSize(Attr("font-size"), FontSize, report),
                    RootFontSize = RootFontSize,
                    PaintOrder = ResolvePaintOrder(Attr("paint-order"), PaintOrder)
                };

                if (el.Parent == null) s.RootFontSize = s.FontSize;

                bool changed = !s.CurrentColor.Equals(CurrentColor) ||
                               !s.Fill.Equals(Fill) ||
                               !s.Stroke.Equals(Stroke) ||
                               s.FillOpacity != FillOpacity ||
                               s.StrokeOpacity != StrokeOpacity ||
                               s.InheritedFillOpacity != InheritedFillOpacity ||
                               s.InheritedStrokeOpacity != InheritedStrokeOpacity ||
                               s.FontSize != FontSize ||
                               s.RootFontSize != RootFontSize ||
                               !ReferenceEquals(s.ContextSource, ContextSource) ||
                               !s.PaintOrder.Equals(PaintOrder);

                float viewportDiagonal = 0f;

                var swRaw = Attr("stroke-width");
                if (!string.IsNullOrWhiteSpace(swRaw) &&
                    SvgValues.TryParseLength(swRaw.AsSpan(), out float swv, out var swu) &&
                    swv >= 0f)
                {
                    s.StrokeWidth = SvgValues.ClampCoord(
                        ResolveStrokeLength(el, swv, swu, ref viewportDiagonal));
                    changed |= s.StrokeWidth != StrokeWidth;
                    // Negative: invalid per spec -> keep inherited value.
                }

                var capRaw = Attr("stroke-linecap");
                if (!string.IsNullOrWhiteSpace(capRaw))
                {
                    s.Cap = Eq(capRaw, "round") ? SKStrokeCap.Round
                          : Eq(capRaw, "square") ? SKStrokeCap.Square
                          : Eq(capRaw, "butt") ? SKStrokeCap.Butt
                          : Cap;
                    changed |= s.Cap != Cap;
                }

                var joinRaw = Attr("stroke-linejoin");
                if (!string.IsNullOrWhiteSpace(joinRaw))
                {
                    s.Join = Eq(joinRaw, "round") ? SKStrokeJoin.Round
                           : Eq(joinRaw, "bevel") ? SKStrokeJoin.Bevel
                           : Eq(joinRaw, "miter") ? SKStrokeJoin.Miter
                           : Join;
                    changed |= s.Join != Join;
                }

                var mlRaw = Attr("stroke-miterlimit");
                if (!string.IsNullOrWhiteSpace(mlRaw) &&
                    SvgValues.TryParseNumber(mlRaw.AsSpan(), out float ml) &&
                    SvgValues.IsFinite(ml) && ml >= 1f)
                {
                    s.MiterLimit = ml;
                    changed |= s.MiterLimit != MiterLimit;
                }

                var dashRaw = Attr("stroke-dasharray");
                if (dashRaw != null)
                {
                    var dashes = ParseDashArray(el, dashRaw, ref viewportDiagonal);
                    if (!ReferenceEquals(dashes, InvalidDash))
                    {
                        s.Dash = dashes;
                        changed |= !ReferenceEquals(dashes, Dash);

                        var doffRaw = Attr("stroke-dashoffset");
                        if (!string.IsNullOrWhiteSpace(doffRaw) &&
                            SvgValues.TryParseLength(doffRaw.AsSpan(), out float dov, out var dou))
                        {
                            s.DashOffset = SvgValues.ClampCoord(
                                ResolveStrokeLength(el, dov, dou, ref viewportDiagonal));
                            changed |= s.DashOffset != DashOffset;
                        }
                    }
                }

                var visRaw = Attr("visibility");
                if (!string.IsNullOrWhiteSpace(visRaw))
                {
                    s.Visibility = Eq(visRaw, "visible") ? true
                                 : Eq(visRaw, "hidden") || Eq(visRaw, "collapse") ? false
                                 : Visibility;
                    changed |= s.Visibility != Visibility;
                }

                return changed ? s : this;

                static bool Eq(string raw, string canonical) =>
                    string.Equals(raw.Trim(), canonical, System.StringComparison.OrdinalIgnoreCase);
            }

            private static readonly float[] InvalidDash = System.Array.Empty<float>();

            private static float ResolveStrokeLength(
                SvgElement element,
                float value,
                SvgValues.SvgUnit unit,
                ref float viewportDiagonal)
            {
                if (unit == SvgValues.SvgUnit.Percent)
                {
                    if (viewportDiagonal <= 0f)
                    {
                        viewportDiagonal = NormalizedViewportDiagonal(element);
                    }
                    return (float)((double)value * 0.01 * viewportDiagonal);
                }
                return SvgValues.ResolveUnits(
                    value,
                    unit,
                    DefaultFontSize,
                    1f);
            }

            private static float NormalizedViewportDiagonal(SvgElement element)
            {
                if (!TryResolveViewportSize(element, out float width, out float height)) return 1f;
                double squared = (double)width * width + (double)height * height;
                if (!(squared > 0d) || double.IsInfinity(squared)) return 1f;
                float diagonal = (float)System.Math.Sqrt(squared * 0.5d);
                return SvgValues.IsFinite(diagonal) && diagonal > 0f ? diagonal : 1f;
            }

            private static bool TryResolveViewportSize(
                SvgElement element,
                out float width,
                out float height)
            {
                width = 0f;
                height = 0f;
                for (SvgElement current = element; current != null; current = current.Parent)
                {
                    if (current.Name is not ("svg" or "symbol")) continue;
                    if (TryParseViewBox(
                            current.GetAttribute("viewBox"),
                            out _,
                            out _,
                            out float boxWidth,
                            out float boxHeight,
                            out _) &&
                        boxWidth > 0f && boxHeight > 0f)
                    {
                        width = boxWidth;
                        height = boxHeight;
                        return true;
                    }
                    if (TryReadViewportDimension(current, "width", isVertical: false, out width) &&
                        TryReadViewportDimension(current, "height", isVertical: true, out height) &&
                        width > 0f && height > 0f)
                    {
                        return true;
                    }
                    return TryResolveViewportSize(current.Parent, out width, out height);
                }
                return false;
            }

            private static bool TryReadViewportDimension(
                SvgElement element,
                string name,
                bool isVertical,
                out float value)
            {
                value = 0f;
                if (!SvgValues.TryParseLength(
                        element.GetPresentationProperty(name).AsSpan(), out float parsed, out var unit) ||
                    !SvgValues.IsFinite(parsed))
                {
                    return false;
                }
                if (unit == SvgValues.SvgUnit.Percent)
                {
                    if (!TryResolveViewportSize(
                            element.Parent, out float parentWidth, out float parentHeight))
                    {
                        return false;
                    }
                    float reference = isVertical ? parentHeight : parentWidth;
                    if (!(reference > 0f)) return false;
                    value = parsed * 0.01f * reference;
                }
                else
                {
                    value = SvgValues.ResolveUnits(parsed, unit, DefaultFontSize, 1f);
                }
                return SvgValues.IsFinite(value);
            }

            private static SvgPaintOrder ResolvePaintOrder(string raw, SvgPaintOrder inherited)
            {
                if (string.IsNullOrWhiteSpace(raw)) return inherited;
                ReadOnlySpan<char> value = raw.AsSpan().Trim();
                if (value.Equals("normal", System.StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("initial", System.StringComparison.OrdinalIgnoreCase))
                    return SvgPaintOrder.Normal;
                if (value.Equals("inherit", System.StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("unset", System.StringComparison.OrdinalIgnoreCase))
                    return inherited;

                Span<PaintPhase> phases = stackalloc PaintPhase[3];
                int count = 0;
                int offset = 0;
                while (offset < value.Length)
                {
                    while (offset < value.Length && char.IsWhiteSpace(value[offset])) offset++;
                    if (offset == value.Length) break;
                    int start = offset;
                    while (offset < value.Length && !char.IsWhiteSpace(value[offset])) offset++;
                    ReadOnlySpan<char> token = value.Slice(start, offset - start);
                    PaintPhase phase;
                    if (token.Equals("fill", System.StringComparison.OrdinalIgnoreCase))
                        phase = PaintPhase.Fill;
                    else if (token.Equals("stroke", System.StringComparison.OrdinalIgnoreCase))
                        phase = PaintPhase.Stroke;
                    else if (token.Equals("markers", System.StringComparison.OrdinalIgnoreCase))
                        phase = PaintPhase.Markers;
                    else
                        return inherited;

                    for (int i = 0; i < count; i++)
                        if (phases[i] == phase) return inherited;
                    if (count == phases.Length) return inherited;
                    phases[count++] = phase;
                }
                if (count == 0) return inherited;

                for (int n = 0; n < 3; n++)
                {
                    PaintPhase normal = (PaintPhase)n;
                    bool present = false;
                    for (int i = 0; i < count; i++) present |= phases[i] == normal;
                    if (!present) phases[count++] = normal;
                }
                return new SvgPaintOrder(phases[0], phases[1], phases[2]);
            }

            private static float[] ParseDashArray(
                SvgElement element,
                string raw,
                ref float viewportDiagonal)
            {
                if (string.IsNullOrWhiteSpace(raw) ||
                    string.Equals(raw.Trim(), "none", System.StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var tok = SvgValues.CreateTokenizer(raw.AsSpan());
                var list = new List<float>(8);
                while (tok.Next(out var token))
                {
                    if (list.Count >= 64)
                    {
                        return InvalidDash; // Excessive dash count: property invalid.
                    }
                    if (!SvgValues.TryParseLength(token, out float v, out var u) ||
                        v < 0f || !SvgValues.IsFinite(v))
                    {
                        return InvalidDash; // Any invalid entry kills the whole list.
                    }
                    list.Add(ResolveStrokeLength(element, v, u, ref viewportDiagonal));
                }

                if (list.Count == 0)
                {
                    return InvalidDash;
                }

                if ((list.Count & 1) != 0)
                {
                    int baseCount = list.Count;
                    for (int i = 0; i < baseCount; i++)
                    {
                        list.Add(list[i]);
                    }
                }
                return list.ToArray();
            }

            private static PaintSpec? ParsePaintSpec(
                string raw,
                InheritedStyle inherited)
            {
                if (raw == null)
                {
                    return null; // Not specified: inherit.
                }

                ReadOnlySpan<char> value = raw.AsSpan().Trim();
                if (value.Equals("context-fill", System.StringComparison.OrdinalIgnoreCase))
                {
                    return ResolveContextPaint(
                        inherited.ContextFill, inherited.ContextSource);
                }
                if (value.Equals("context-stroke", System.StringComparison.OrdinalIgnoreCase))
                {
                    return ResolveContextPaint(
                        inherited.ContextStroke, inherited.ContextSource);
                }

                if (!SvgValues.TryParsePaint(
                        raw.AsSpan(),
                        out var kind,
                        out var color,
                        out var fragment,
                        out var fallbackText))
                {
                    return null; // Unrecognized paint: treated as unspecified.
                }

                switch (kind)
                {
                    case SvgValues.PaintKind.None:
                        return PaintSpec.None;
                    case SvgValues.PaintKind.CurrentColor:
                        return new PaintSpec(SvgValues.PaintKind.CurrentColor, default, null, null);
                    case SvgValues.PaintKind.Color:
                        return new PaintSpec(SvgValues.PaintKind.Color, color, null, null);
                    case SvgValues.PaintKind.ServerRef:
                        return new PaintSpec(SvgValues.PaintKind.ServerRef, default, fragment, fallbackText);
                    default:
                        return null; // inherit / unspecified
                }
            }

            private static PaintSpec ResolveContextPaint(
                PaintSpec? contextPaint,
                SvgElement contextSource)
            {
                if (!contextPaint.HasValue) return PaintSpec.None;
                PaintSpec paint = contextPaint.Value;
                if (paint.Kind != SvgValues.PaintKind.ServerRef)
                {
                    return paint;
                }
                return new PaintSpec(
                    SvgValues.PaintKind.ServerRef,
                    default,
                    paint.Fragment,
                    paint.Fallback,
                    contextSource);
            }
        }

        private static bool TryParseOpacity(string raw, out float opacity)
        {
            opacity = 1f;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            ReadOnlySpan<char> value = raw.AsSpan().Trim();
            if (value.Equals("inherit", System.StringComparison.OrdinalIgnoreCase) ||
                value.Equals("revert", System.StringComparison.OrdinalIgnoreCase) ||
                value.Equals("unset", System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (value.Equals("initial", System.StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            if (!SvgValues.TryParseLength(value, out float parsed, out var unit) ||
                (unit != SvgValues.SvgUnit.User && unit != SvgValues.SvgUnit.Percent))
            {
                return false;
            }
            float resolved = unit == SvgValues.SvgUnit.Percent ? parsed * 0.01f : parsed;
            if (!SvgValues.IsFinite(resolved))
            {
                return false;
            }
            opacity = System.Math.Clamp(resolved, 0f, 1f);
            return true;
        }

        private static SKPaint ApplyInheritedFillOpacity(SKPaint paint, InheritedStyle style) =>
            ScalePaintAlpha(paint, style.InheritedFillOpacity);

        private static SKPaint ApplyInheritedStrokeOpacity(SKPaint paint, InheritedStyle style) =>
            ScalePaintAlpha(paint, style.InheritedStrokeOpacity);

        private static SKPaint ScalePaintAlpha(SKPaint paint, float inheritedOpacity)
        {
            if (paint == null) return null;
            if (!(inheritedOpacity < 1f)) return paint;
            if (inheritedOpacity > 0f)
            {
                SKColor color = paint.Color;
                byte scaled = (byte)System.MathF.Round(color.Alpha * inheritedOpacity);
                if (scaled > 0)
                {
                    paint.Color = color.WithAlpha(scaled);
                    return paint;
                }
            }
            paint.Dispose();
            return null;
        }

        /// <summary>Scoped canvas Save/Restore (unwinds during exceptions).</summary>
        private sealed class CanvasState : System.IDisposable
        {
            private readonly SKCanvas _canvas;

            public CanvasState(SKCanvas canvas)
            {
                _canvas = canvas;
                _canvas.Save();
            }

            public void Dispose() => _canvas.Restore();
        }
    }
}
