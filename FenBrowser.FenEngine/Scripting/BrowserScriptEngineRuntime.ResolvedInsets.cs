using System;
using System.Collections.Generic;
using System.Globalization;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Js.Runtime;
using SkiaSharp;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Resolved values of the inset properties (<c>top</c>, <c>right</c>,
/// <c>bottom</c>, <c>left</c>) as <c>getComputedStyle</c> reports them.
/// </summary>
/// <remarks>
/// CSSOM §6.7.2 "resolved value", special case for properties like top, with
/// CSS Positioned Layout 3 §3.2 (relative), §3.4 (sticky) and §4 (absolute):
/// <list type="bullet">
/// <item>An element that generates no box, or is <c>position: static</c>, reports
/// the computed value: lengths absolutized, percentages and <c>auto</c> as
/// written.</item>
/// <item>A relatively or sticky positioned element resolves percentages against
/// its containing block (sticky: its nearest scrollport). A relative
/// <c>auto</c> side is the negation of the opposite side, or 0 when both are
/// <c>auto</c>; a sticky <c>auto</c> stays <c>auto</c>.</item>
/// <item>An absolutely or fixed positioned element resolves percentages against
/// its containing block's padding box, and an <c>auto</c> side reports the used
/// distance between its margin box and that padding box.</item>
/// </list>
/// </remarks>
public sealed partial class FenJsBrowserScriptEngine
{
    private static readonly string[] InsetSides = { "top", "right", "bottom", "left" };

    private void ResolveComputedInsets(Element element, CssComputed cs, Dictionary<string, JsValue> props)
    {
        var specified = new string[4];
        for (int i = 0; i < 4; i++)
        {
            specified[i] = props.TryGetValue(InsetSides[i], out var value) && value.Tag == JsValueTag.String
                ? value.AsString().Trim()
                : "auto";
            if (specified[i].Length == 0)
            {
                specified[i] = "auto";
            }
        }

        var position = ReadStringProp(props, "position")?.ToLowerInvariant() ?? "static";
        var display = ReadStringProp(props, "display")?.ToLowerInvariant();
        var emBase = ReadPxProp(props, "font-size") ?? cs.FontSize ?? 16.0;
        var box = display == "none" ? null : LayoutBoxResolver?.Invoke(element) as BoxModel;

        var resolved = new string[4];
        if (box == null || position is not ("relative" or "sticky" or "absolute" or "fixed"))
        {
            for (int i = 0; i < 4; i++)
            {
                resolved[i] = AbsolutizeInset(specified[i], emBase, percentBase: null);
            }
        }
        else if (position is "relative" or "sticky")
        {
            var reference = position == "sticky"
                ? NearestScrollportContentBox(element) ?? ParentContentBox(element)
                : ParentContentBox(element);
            for (int i = 0; i < 4; i++)
            {
                resolved[i] = AbsolutizeInset(specified[i], emBase, InsetPercentBase(reference, i));
            }

            if (position == "relative")
            {
                ResolveRelativeAutoPair(resolved, 0, 2);
                ResolveRelativeAutoPair(resolved, 3, 1);
            }
        }
        else
        {
            var containingBlock = AbsoluteContainingBlockPaddingBox(element, position == "fixed");
            for (int i = 0; i < 4; i++)
            {
                if (specified[i] != "auto")
                {
                    resolved[i] = AbsolutizeInset(specified[i], emBase, InsetPercentBase(containingBlock, i));
                    continue;
                }

                var margin = box.MarginBox;
                double used = i switch
                {
                    0 => margin.Top - containingBlock.Top,
                    1 => containingBlock.Right - margin.Right,
                    2 => containingBlock.Bottom - margin.Bottom,
                    _ => margin.Left - containingBlock.Left,
                };
                resolved[i] = FormatCssPx(used);
            }
        }

        for (int i = 0; i < 4; i++)
        {
            props[InsetSides[i]] = JsValue.FromString(resolved[i]);
        }
    }

    private static string ReadStringProp(Dictionary<string, JsValue> props, string name)
    {
        return props.TryGetValue(name, out var value) && value.Tag == JsValueTag.String ? value.AsString().Trim() : null;
    }

    private static double? ReadPxProp(Dictionary<string, JsValue> props, string name)
    {
        var text = ReadStringProp(props, name);
        if (text != null && text.EndsWith("px", StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(text.AsSpan(0, text.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
        {
            return px;
        }

        return null;
    }

    // Vertical insets take percentages of the reference height, horizontal ones of its width.
    private static double? InsetPercentBase(SKRect? reference, int side)
    {
        if (reference is not SKRect rect)
        {
            return null;
        }

        return side % 2 == 0 ? rect.Height : rect.Width;
    }

    // A null percentBase keeps percentages (and calc() holding them) as written,
    // which is the computed value; with one, the value resolves to pixels.
    private static string AbsolutizeInset(string value, double emBase, double? percentBase)
    {
        if (string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return "auto";
        }

        if (percentBase == null && value.Contains('%'))
        {
            return value;
        }

        var expression = value.StartsWith("calc(", StringComparison.OrdinalIgnoreCase) ? value : "calc(" + value + ")";
        return CssLoader.TryEvaluateCalcExpression(expression, out var px, emBase, percentBase ?? 0)
            ? FormatCssPx(px)
            : value;
    }

    private static void ResolveRelativeAutoPair(string[] resolved, int start, int end)
    {
        bool startAuto = resolved[start] == "auto", endAuto = resolved[end] == "auto";
        if (startAuto && endAuto)
        {
            resolved[start] = resolved[end] = "0px";
        }
        else if (startAuto)
        {
            resolved[start] = NegatePx(resolved[end]);
        }
        else if (endAuto)
        {
            resolved[end] = NegatePx(resolved[start]);
        }
    }

    private static string NegatePx(string px)
    {
        return px.EndsWith("px", StringComparison.Ordinal) &&
               double.TryParse(px.AsSpan(0, px.Length - 2), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? FormatCssPx(-value)
            : "0px";
    }

    private static string FormatCssPx(double px)
    {
        px = Math.Round(px, 4);
        if (px == 0)
        {
            px = 0; // no "-0px"
        }

        return px.ToString("0.####", CultureInfo.InvariantCulture) + "px";
    }

    private SKRect? ParentContentBox(Element element)
    {
        return element.ParentElement is Element parent && LayoutBoxResolver?.Invoke(parent) is BoxModel box
            ? box.ContentBox
            : null;
    }

    // CSS Position 3 §3.4: sticky insets are relative to the nearest ancestor scrollport.
    private SKRect? NearestScrollportContentBox(Element element)
    {
        for (var ancestor = element.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
        {
            var style = ancestor.GetComputedStyle();
            if (style != null && (IsScrollContainerOverflow(style.OverflowX ?? style.Overflow) ||
                                  IsScrollContainerOverflow(style.OverflowY ?? style.Overflow)))
            {
                return LayoutBoxResolver?.Invoke(ancestor) is BoxModel box ? box.ContentBox : null;
            }
        }

        return null;
    }

    private static bool IsScrollContainerOverflow(string overflow)
    {
        return overflow != null && overflow.Trim().ToLowerInvariant() is "hidden" or "scroll" or "auto";
    }

    // CSS Position 3 §2.1 / CSS Transforms 1 §2: an absolutely positioned box's
    // containing block is formed by the nearest positioned or transformed ancestor,
    // a fixed one's by the nearest transformed ancestor; otherwise the viewport.
    private SKRect AbsoluteContainingBlockPaddingBox(Element element, bool isFixed)
    {
        for (var ancestor = element.ParentElement; ancestor != null; ancestor = ancestor.ParentElement)
        {
            var style = ancestor.GetComputedStyle();
            if (style == null)
            {
                continue;
            }

            var ancestorPosition = style.Position?.Trim().ToLowerInvariant();
            bool establishes = HasTransform(style) ||
                               (!isFixed && ancestorPosition is "relative" or "absolute" or "fixed" or "sticky");
            if (establishes && LayoutBoxResolver?.Invoke(ancestor) is BoxModel box)
            {
                return box.PaddingBox;
            }
        }

        return new SKRect(0, 0, (float)WindowWidth, (float)WindowHeight);
    }

    private static bool HasTransform(CssComputed style)
    {
        return style.Map != null &&
               style.Map.TryGetValue("transform", out var transform) &&
               !string.IsNullOrWhiteSpace(transform) &&
               !string.Equals(transform.Trim(), "none", StringComparison.OrdinalIgnoreCase);
    }
}
