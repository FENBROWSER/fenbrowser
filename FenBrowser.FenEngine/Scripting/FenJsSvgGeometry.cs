using System;
using System.Globalization;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Svg;
using FenBrowser.Js.Host;
using FenBrowser.Js.Runtime;
using SkiaSharp;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// SVGGeometryElement (SVG 2 §9.1): getTotalLength and getPointAtLength on path
/// and the basic shapes. The outline is built by <see cref="SvgGeometryOutline"/>,
/// the same code the renderer paints with, from the element's used geometry
/// values: inline style, then the cascade, then the presentation attribute.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    /// <summary>DOMPoint, as returned by getPointAtLength. Its coordinates are writable.</summary>
    private sealed class SvgPointHost : FenJsSvgDomHost
    {
        public SvgPointHost(float x, float y)
        {
            X = x;
            Y = y;
        }

        public override string InterfaceName => "DOMPoint";

        public double X { get; set; }

        public double Y { get; set; }

        public double Z { get; set; }

        public double W { get; set; } = 1d;
    }

    /// <summary>
    /// The geometry properties that are CSS properties (SVG 2 §10 and §9.3); x1, y1,
    /// x2, y2 and points stay attributes only, so style never sets them.
    /// </summary>
    private static readonly System.Collections.Generic.HashSet<string> SvgGeometryCssProperties = new(StringComparer.Ordinal)
    {
        "cx", "cy", "r", "rx", "ry", "x", "y", "width", "height", "d"
    };

    private bool TryGetSvgGeometryProperty(Element element, string property, out JsValue value)
    {
        value = JsValue.Undefined;
        if (!SvgGeometryOutline.IsGeometryElement(element.LocalName))
        {
            return false;
        }

        switch (property)
        {
            case "getTotalLength":
                value = GetOrCreateHostCallable(element, property, (_, _) =>
                {
                    using var outline = BuildSvgOutline(element);
                    return JsValue.FromNumber(SvgGeometryOutline.MeasureLength(outline));
                });
                return true;
            case "getPointAtLength":
                value = GetOrCreateHostCallable(element, property, (_, args) =>
                {
                    float distance = FloatArgument(args, 0);
                    using var outline = BuildSvgOutline(element);
                    SKPoint point = SvgGeometryOutline.PointAtLength(outline, distance);
                    return ToHostOrNull(new SvgPointHost(point.X, point.Y), HostObjectKind.Other);
                }, length: 1);
                return true;
            default:
                return false;
        }
    }

    private bool TryGetSvgPointProperty(SvgPointHost point, string property, out JsValue value)
    {
        switch (property)
        {
            case "x": value = JsValue.FromNumber(point.X); return true;
            case "y": value = JsValue.FromNumber(point.Y); return true;
            case "z": value = JsValue.FromNumber(point.Z); return true;
            case "w": value = JsValue.FromNumber(point.W); return true;
            default:
                value = JsValue.Undefined;
                return false;
        }
    }

    private bool TrySetSvgPointProperty(SvgPointHost point, string property, JsValue value)
    {
        double number = CoerceToFiniteNumber(value, double.NaN);
        switch (property)
        {
            case "x": point.X = number; return true;
            case "y": point.Y = number; return true;
            case "z": point.Z = number; return true;
            case "w": point.W = number; return true;
            default: return false;
        }
    }

    private SKPath BuildSvgOutline(Element element)
    {
        var style = element.GetComputedStyle();
        float fontSize = (float)(style?.FontSize ?? 16d);
        float rootFontSize = (float)(element.OwnerDocument?.DocumentElement?.GetComputedStyle()?.FontSize ?? 16d);
        var (viewportWidth, viewportHeight) = ResolveSvgViewportSize(element);

        string Specified(string property)
        {
            if (!SvgGeometryCssProperties.Contains(property))
            {
                return element.GetAttribute(property);
            }

            string inline = ReadInlineStyleDeclaration(element.GetAttribute("style"), property);
            if (inline != null)
            {
                return inline;
            }
            if (style?.Map != null && style.Map.TryGetValue(property, out string cascaded) &&
                !string.IsNullOrWhiteSpace(cascaded))
            {
                return cascaded;
            }
            return element.GetAttribute(property);
        }

        bool Length(string property, SvgGeometryOutline.PercentBasis basis, out float resolved)
        {
            resolved = 0f;
            string raw = Specified(property)?.Trim();
            if (string.IsNullOrEmpty(raw) || raw.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            float percentReference = basis switch
            {
                SvgGeometryOutline.PercentBasis.Width => viewportWidth,
                SvgGeometryOutline.PercentBasis.Height => viewportHeight,
                _ => MathF.Sqrt((viewportWidth * viewportWidth + viewportHeight * viewportHeight) / 2f)
            };
            if (SvgValues.TryParseLength(raw.AsSpan(), out float parsed, out var unit))
            {
                resolved = SvgValues.ResolveUnits(parsed, unit, fontSize, percentReference);
            }
            else if (!SvgCssLengthEvaluator.TryEvaluate(
                         raw, percentReference, fontSize, rootFontSize, viewportWidth, viewportHeight, out resolved))
            {
                return false;
            }

            resolved = SvgValues.ClampCoord(resolved);
            return true;
        }

        return SvgGeometryOutline.TryBuild(element.LocalName, Length, Specified);
    }

    /// <summary>
    /// The size percentages resolve against: the nearest svg ancestor's viewBox, else
    /// its absolute width and height, else the window.
    /// </summary>
    private (float Width, float Height) ResolveSvgViewportSize(Element element)
    {
        for (var ancestor = element.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
        {
            if (ancestor.LocalName != "svg" || ancestor.NamespaceUri != Namespaces.Svg)
            {
                continue;
            }

            string viewBox = ancestor.GetAttribute("viewBox");
            if (!string.IsNullOrWhiteSpace(viewBox))
            {
                var parts = viewBox.Split(new[] { ' ', ',', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 4 &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float vw) &&
                    float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float vh) &&
                    vw > 0f && vh > 0f && float.IsFinite(vw) && float.IsFinite(vh))
                {
                    return (vw, vh);
                }
            }

            float width = AbsoluteSvgLength(ancestor.GetAttribute("width"), (float)WindowWidth);
            float height = AbsoluteSvgLength(ancestor.GetAttribute("height"), (float)WindowHeight);
            return (width, height);
        }

        return ((float)WindowWidth, (float)WindowHeight);
    }

    private static float AbsoluteSvgLength(string raw, float fallback) =>
        !string.IsNullOrWhiteSpace(raw) &&
        SvgValues.TryParseLength(raw.AsSpan(), out float value, out var unit) &&
        unit != SvgValues.SvgUnit.Percent &&
        SvgValues.ResolveUnits(value, unit, 16f, fallback) is float resolved && resolved > 0f
            ? resolved
            : fallback;

    /// <summary>The last declaration of a property in a style attribute, without !important.</summary>
    private static string ReadInlineStyleDeclaration(string styleAttribute, string property)
    {
        if (string.IsNullOrWhiteSpace(styleAttribute))
        {
            return null;
        }

        string found = null;
        foreach (var declaration in styleAttribute.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = declaration.IndexOf(':');
            if (colon <= 0 || !declaration[..colon].Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = declaration[(colon + 1)..].Trim();
            int important = value.LastIndexOf("!important", StringComparison.OrdinalIgnoreCase);
            found = important >= 0 ? value[..important].TrimEnd() : value;
        }
        return found;
    }
}
