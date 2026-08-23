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
            public PaintSpec(SvgValues.PaintKind kind, SKColor color, string fragment, string fallback)
            {
                Kind = kind;
                Color = color;
                Fragment = fragment;
                Fallback = fallback;
            }

            public SvgValues.PaintKind Kind { get; }
            public SKColor Color { get; }
            public string Fragment { get; }
            public string Fallback { get; }

            public static readonly PaintSpec Black =
                new(SvgValues.PaintKind.Color, SKColors.Black, null, null);
            public static readonly PaintSpec None =
                new(SvgValues.PaintKind.None, default, null, null);
        }

        private sealed class InheritedStyle
        {
            public PaintSpec Fill = PaintSpec.Black;      // spec default: black
            public PaintSpec Stroke = PaintSpec.None;     // spec default: none
            public float StrokeWidth = 1f;
            public SKStrokeCap Cap = SKStrokeCap.Butt;
            public SKStrokeJoin Join = SKStrokeJoin.Miter;
            public float MiterLimit = 4f;
            public float[] Dash;
            public float DashOffset;
            public SKColor CurrentColor = SKColors.Black;
            public bool Visibility = true;
            public float FontSize = DefaultFontSize;
            public float RootFontSize = DefaultFontSize;

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

                var s = new InheritedStyle
                {
                    CurrentColor = currentColor,
                    Fill = ParsePaintSpec(Attr("fill"), this, currentColor) ?? Fill,
                    Stroke = ParsePaintSpec(Attr("stroke"), this, currentColor) ?? Stroke,
                    StrokeWidth = StrokeWidth,
                    Cap = Cap,
                    Join = Join,
                    MiterLimit = MiterLimit,
                    Dash = Dash,
                    DashOffset = DashOffset,
                    Visibility = Visibility,
                    FontSize = ResolveFontSize(Attr("font-size"), FontSize),
                    RootFontSize = RootFontSize
                };

                if (el.Parent == null) s.RootFontSize = s.FontSize;

                bool changed = !s.CurrentColor.Equals(CurrentColor) ||
                               !s.Fill.Equals(Fill) ||
                               !s.Stroke.Equals(Stroke) ||
                               s.FontSize != FontSize ||
                               s.RootFontSize != RootFontSize;

                var swRaw = Attr("stroke-width");
                if (!string.IsNullOrWhiteSpace(swRaw) &&
                    SvgValues.TryParseLength(swRaw.AsSpan(), out float swv, out var swu) &&
                    swv >= 0f)
                {
                    s.StrokeWidth = SvgValues.ClampCoord(SvgValues.ResolveUnits(swv, swu, DefaultFontSize, 1f));
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
                    var dashes = ParseDashArray(dashRaw);
                    if (!ReferenceEquals(dashes, InvalidDash))
                    {
                        s.Dash = dashes;
                        changed |= !ReferenceEquals(dashes, Dash);

                        var doffRaw = Attr("stroke-dashoffset");
                        if (!string.IsNullOrWhiteSpace(doffRaw) &&
                            SvgValues.TryParseLength(doffRaw.AsSpan(), out float dov, out var dou))
                        {
                            s.DashOffset = System.Math.Max(0f, SvgValues.ResolveUnits(dov, dou, DefaultFontSize, 1f));
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

            private static float[] ParseDashArray(string raw)
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
                    list.Add(SvgValues.ResolveUnits(v, u, DefaultFontSize, 1f));
                }

                if (list.Count == 0)
                {
                    return InvalidDash;
                }

                if (list.Count == 1)
                {
                    list.Add(list[0]); // Spec: odd counts are duplicated.
                }
                return list.ToArray();
            }

            private static PaintSpec? ParsePaintSpec(
                string raw,
                InheritedStyle inherited,
                SKColor currentColor)
            {
                if (raw == null)
                {
                    return null; // Not specified: inherit.
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
                        return new PaintSpec(SvgValues.PaintKind.Color, currentColor, null, null);
                    case SvgValues.PaintKind.Color:
                        return new PaintSpec(SvgValues.PaintKind.Color, color, null, null);
                    case SvgValues.PaintKind.ServerRef:
                        return new PaintSpec(SvgValues.PaintKind.ServerRef, default, fragment, fallbackText);
                    default:
                        return null; // inherit / unspecified
                }
            }
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
