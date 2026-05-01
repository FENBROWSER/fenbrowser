using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine;

public class CssSpecInventoryCoverageTests
{
    private static readonly string[] InventoryCssProperties_2026_05_01 =
    {
            "align-content",
            "align-items",
            "align-self",
            "anchor-name",
            "anchor-scope",
            "animation",
            "animation-composition",
            "animation-delay",
            "animation-direction",
            "animation-duration",
            "animation-fill-mode",
            "animation-iteration-count",
            "animation-name",
            "animation-play-state",
            "animation-range",
            "animation-range-end",
            "animation-range-start",
            "animation-timeline",
            "animation-timing-function",
            "aspect-ratio",
            "azimuth",
            "backface-visibility",
            "background",
            "background-attachment",
            "background-blend-mode",
            "background-clip",
            "background-color",
            "background-image",
            "background-origin",
            "background-position",
            "background-position-block",
            "background-position-inline",
            "background-position-x",
            "background-position-y",
            "background-repeat",
            "background-repeat-block",
            "background-repeat-inline",
            "background-repeat-x",
            "background-repeat-y",
            "background-size",
            "block-size",
            "border",
            "border-block",
            "border-block-color",
            "border-block-end",
            "border-block-end-color",
            "border-block-end-radius",
            "border-block-end-style",
            "border-block-end-width",
            "border-block-start",
            "border-block-start-color",
            "border-block-start-radius",
            "border-block-start-style",
            "border-block-start-width",
            "border-block-style",
            "border-block-width",
            "border-bottom",
            "border-bottom-color",
            "border-bottom-left-radius",
            "border-bottom-right-radius",
            "border-bottom-style",
            "border-bottom-width",
            "border-collapse",
            "border-color",
            "border-end-end-radius",
            "border-end-start-radius",
            "border-image",
            "border-image-outset",
            "border-image-repeat",
            "border-image-slice",
            "border-image-source",
            "border-image-width",
            "border-inline",
            "border-inline-color",
            "border-inline-end",
            "border-inline-end-color",
            "border-inline-end-style",
            "border-inline-end-width",
            "border-inline-start",
            "border-inline-start-color",
            "border-inline-start-style",
            "border-inline-start-width",
            "border-inline-style",
            "border-inline-width",
            "border-left",
            "border-left-color",
            "border-left-style",
            "border-left-width",
            "border-radius",
            "border-right",
            "border-right-color",
            "border-right-style",
            "border-right-width",
            "border-spacing",
            "border-start-end-radius",
            "border-start-start-radius",
            "border-style",
            "border-top",
            "border-top-color",
            "border-top-left-radius",
            "border-top-right-radius",
            "border-top-style",
            "border-top-width",
            "border-width",
            "bottom",
            "box-decoration-break",
            "box-shadow",
            "box-sizing",
            "break-after",
            "break-before",
            "break-inside",
            "caption-side",
            "clear",
            "clip",
            "clip-path",
            "color",
            "color-interpolation",
            "color-interpolation-filters",
            "color-rendering",
            "color-scheme",
            "column-count",
            "column-fill",
            "column-gap",
            "column-rule",
            "column-rule-color",
            "column-rule-style",
            "column-rule-width",
            "columns",
            "column-span",
            "column-width",
            "contain",
            "container",
            "container-name",
            "container-type",
            "contain-intrinsic-block-size",
            "contain-intrinsic-height",
            "contain-intrinsic-inline-size",
            "contain-intrinsic-size",
            "contain-intrinsic-width",
            "content-visibility",
            "cursor",
            "direction",
            "display",
            "empty-cells",
            "fill",
            "fill-opacity",
            "fill-rule",
            "filter",
            "flex",
            "flex-basis",
            "flex-direction",
            "flex-flow",
            "flex-grow",
            "flex-shrink",
            "flex-wrap",
            "float",
            "flood-color",
            "flood-opacity",
            "font",
            "font-family",
            "font-feature-settings",
            "font-kerning",
            "font-language-override",
            "font-optical-sizing",
            "font-palette",
            "font-size",
            "font-size-adjust",
            "font-stretch",
            "font-style",
            "font-synthesis",
            "font-synthesis-position",
            "font-synthesis-small-caps",
            "font-synthesis-style",
            "font-synthesis-weight",
            "font-variant",
            "font-variant-alternates",
            "font-variant-caps",
            "font-variant-east-asian",
            "font-variant-emoji",
            "font-variant-ligatures",
            "font-variant-numeric",
            "font-variant-position",
            "font-weight",
            "font-width",
            "gap",
            "grid",
            "grid-area",
            "grid-auto-columns",
            "grid-auto-flow",
            "grid-auto-rows",
            "grid-column",
            "grid-column-end",
            "grid-column-start",
            "grid-row",
            "grid-row-end",
            "grid-row-start",
            "grid-template",
            "grid-template-areas",
            "grid-template-columns",
            "grid-template-rows",
            "hanging-punctuation",
            "height",
            "hyphenate-character",
            "hyphenate-limit-chars",
            "hyphens",
            "image-rendering",
            "inline-size",
            "inset",
            "inset-block",
            "inset-block-end",
            "inset-block-start",
            "inset-inline",
            "inset-inline-end",
            "inset-inline-start",
            "--inventory-probe",
            "justify-content",
            "justify-self",
            "left",
            "letter-spacing",
            "lighting-color",
            "line-break",
            "line-height",
            "list-style",
            "list-style-image",
            "list-style-position",
            "list-style-type",
            "margin",
            "margin-block",
            "margin-block-end",
            "margin-block-start",
            "margin-bottom",
            "margin-inline",
            "margin-inline-end",
            "margin-inline-start",
            "margin-left",
            "margin-right",
            "margin-top",
            "mask",
            "mask-border",
            "mask-border-mode",
            "mask-border-outset",
            "mask-border-repeat",
            "mask-border-slice",
            "mask-border-source",
            "mask-border-width",
            "mask-clip",
            "mask-composite",
            "mask-image",
            "mask-mode",
            "mask-origin",
            "mask-position",
            "mask-repeat",
            "mask-size",
            "mask-type",
            "max-block-size",
            "max-height",
            "max-inline-size",
            "max-width",
            "min-block-size",
            "min-height",
            "min-inline-size",
            "min-width",
            "mix-blend-mode",
            "object-fit",
            "object-position",
            "offset",
            "offset-anchor",
            "offset-distance",
            "offset-path",
            "offset-position",
            "offset-rotate",
            "opacity",
            "order",
            "orphans",
            "outline",
            "outline-color",
            "outline-offset",
            "outline-style",
            "outline-width",
            "overflow",
            "overflow-block",
            "overflow-clip-margin",
            "overflow-inline",
            "overflow-wrap",
            "overflow-x",
            "overflow-y",
            "padding",
            "padding-block",
            "padding-block-end",
            "padding-block-start",
            "padding-bottom",
            "padding-inline",
            "padding-inline-end",
            "padding-inline-start",
            "padding-left",
            "padding-right",
            "padding-top",
            "paint-order",
            "perspective",
            "perspective-origin",
            "place-content",
            "place-items",
            "place-self",
            "pointer-events",
            "position",
            "position-anchor",
            "position-area",
            "position-try",
            "position-try-fallbacks",
            "position-try-order",
            "position-visibility",
            "quotes",
            "right",
            "rotate",
            "row-gap",
            "ruby-align",
            "ruby-merge",
            "ruby-position",
            "scale",
            "scroll-behavior",
            "scroll-margin",
            "scroll-margin-block",
            "scroll-margin-block-end",
            "scroll-margin-block-start",
            "scroll-margin-bottom",
            "scroll-margin-inline",
            "scroll-margin-inline-end",
            "scroll-margin-inline-start",
            "scroll-margin-left",
            "scroll-margin-right",
            "scroll-margin-top",
            "scroll-padding",
            "scroll-padding-block",
            "scroll-padding-block-end",
            "scroll-padding-block-start",
            "scroll-padding-bottom",
            "scroll-padding-inline",
            "scroll-padding-inline-end",
            "scroll-padding-inline-start",
            "scroll-padding-left",
            "scroll-padding-right",
            "scroll-padding-top",
            "scroll-snap-align",
            "scroll-snap-stop",
            "scroll-snap-type",
            "scroll-timeline",
            "scroll-timeline-axis",
            "scroll-timeline-name",
            "shape-image-threshold",
            "shape-margin",
            "shape-outside",
            "stop-color",
            "stop-opacity",
            "stroke",
            "stroke-dasharray",
            "stroke-dashoffset",
            "stroke-linecap",
            "stroke-linejoin",
            "stroke-miterlimit",
            "stroke-opacity",
            "stroke-width",
            "tab-size",
            "text-align",
            "text-align-all",
            "text-align-last",
            "text-autospace",
            "text-combine-upright",
            "text-emphasis",
            "text-emphasis-color",
            "text-emphasis-position",
            "text-emphasis-skip",
            "text-emphasis-style",
            "text-indent",
            "text-justify",
            "text-orientation",
            "text-rendering",
            "text-shadow",
            "text-spacing",
            "text-spacing-trim",
            "text-transform",
            "text-underline-position",
            "text-wrap",
            "text-wrap-mode",
            "text-wrap-style",
            "timeline-scope",
            "top",
            "transform",
            "transform-box",
            "transform-origin",
            "transform-style",
            "transition",
            "transition-behavior",
            "transition-delay",
            "transition-duration",
            "transition-property",
            "transition-timing-function",
            "translate",
            "vertical-align",
            "view-timeline",
            "view-timeline-axis",
            "view-timeline-inset",
            "view-timeline-name",
            "view-transition-name",
            "visibility",
            "white-space",
            "white-space-collapse",
            "widows",
            "width",
            "will-change",
            "word-break",
            "word-spacing",
            "word-wrap",
            "writing-mode",
            "z-index",
    };

    [Fact]
    public void Inventory_2026_05_01_PropertyCount_IsStable()
    {
        Assert.Equal(414, InventoryCssProperties_2026_05_01.Length);
    }

    [Fact]
    public void Inventory_2026_05_01_Properties_AreAcceptedBySupportsPropertyCheck()
    {
        var method = typeof(CssLoader).GetMethod("IsSupportedProperty", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);

        foreach (var property in InventoryCssProperties_2026_05_01)
        {
            var supported = method!.Invoke(null, new object[] { property, "initial" });
            Assert.IsType<bool>(supported);
            Assert.True((bool)supported, $"Expected property support for '{property}'");
        }
    }

    [Fact]
    public async Task LogicalInventoryAliases_ProjectToCanonicalRuntimeKeys()
    {
        var (computed, box) = await ComputeForSingleBoxAsync(
            ".box { direction: rtl; flex-flow: column wrap-reverse; container: stage / inline-size; scroll-margin-inline: 1px 2px; scroll-margin-block: 3px 4px; scroll-padding-inline: 5px 6px; scroll-padding-block: 7px 8px; border-start-start-radius: 10px; border-start-end-radius: 11px; border-end-start-radius: 12px; border-end-end-radius: 13px; }");

        Assert.True(computed.TryGetValue(box, out var style));

        Assert.Equal("column", style.Map["flex-direction"]);
        Assert.Equal("wrap-reverse", style.Map["flex-wrap"]);

        Assert.Equal("stage", style.Map["container-name"]);
        Assert.Equal("inline-size", style.Map["container-type"]);

        Assert.Equal("1px", style.Map["scroll-margin-inline-start"]);
        Assert.Equal("2px", style.Map["scroll-margin-inline-end"]);
        Assert.Equal("1px", style.Map["scroll-margin-right"]);
        Assert.Equal("2px", style.Map["scroll-margin-left"]);
        Assert.Equal("3px", style.Map["scroll-margin-top"]);
        Assert.Equal("4px", style.Map["scroll-margin-bottom"]);

        Assert.Equal("5px", style.Map["scroll-padding-inline-start"]);
        Assert.Equal("6px", style.Map["scroll-padding-inline-end"]);
        Assert.Equal("5px", style.Map["scroll-padding-right"]);
        Assert.Equal("6px", style.Map["scroll-padding-left"]);
        Assert.Equal("7px", style.Map["scroll-padding-top"]);
        Assert.Equal("8px", style.Map["scroll-padding-bottom"]);

        Assert.Equal("10px", style.Map["border-top-right-radius"]);
        Assert.Equal("11px", style.Map["border-top-left-radius"]);
        Assert.Equal("12px", style.Map["border-bottom-right-radius"]);
        Assert.Equal("13px", style.Map["border-bottom-left-radius"]);
    }

    [Fact]
    public async Task BlockAxisLogicalRadiusShorthands_ProjectToPhysicalCorners()
    {
        var (computed, box) = await ComputeForSingleBoxAsync(
            ".box { direction: rtl; border-block-start-radius: 21px 22px; border-block-end-radius: 23px 24px; }");

        Assert.True(computed.TryGetValue(box, out var style));
        Assert.Equal("21px", style.Map["border-top-right-radius"]);
        Assert.Equal("22px", style.Map["border-top-left-radius"]);
        Assert.Equal("23px", style.Map["border-bottom-right-radius"]);
        Assert.Equal("24px", style.Map["border-bottom-left-radius"]);
    }

    [Fact]
    public async Task TextWrapAndBreakAliases_ProjectToCanonicalKeys()
    {
        var (computed, box) = await ComputeForSingleBoxAsync(
            ".box { text-wrap: nowrap; break-before: page; break-after: avoid-page; break-inside: avoid; text-align-all: center; }");

        Assert.True(computed.TryGetValue(box, out var style));
        Assert.Equal("nowrap", style.Map["text-wrap-mode"]);
        Assert.Equal("auto", style.Map["text-wrap-style"]);
        Assert.Equal("nowrap", style.Map["white-space"]);
        Assert.Equal("always", style.Map["page-break-before"]);
        Assert.Equal("avoid", style.Map["page-break-after"]);
        Assert.Equal("avoid", style.Map["page-break-inside"]);
        Assert.Equal("center", style.Map["text-align"]);
    }

    [Fact]
    public async Task WhiteSpaceCollapse_ProjectsToWhiteSpaceFallback()
    {
        var (computed, box) = await ComputeForSingleBoxAsync(
            ".box { white-space-collapse: preserve-breaks; }");

        Assert.True(computed.TryGetValue(box, out var style));
        Assert.Equal("pre-wrap", style.Map["white-space"]);
    }

    [Fact]
    public async Task TimelineShorthands_ProjectNameAndAxis()
    {
        var (computed, box) = await ComputeForSingleBoxAsync(
            ".box { scroll-timeline: hero y; view-timeline: card inline; animation-timeline: auto; timeline-scope: --scope-root; }");

        Assert.True(computed.TryGetValue(box, out var style));
        Assert.Equal("hero", style.Map["scroll-timeline-name"]);
        Assert.Equal("y", style.Map["scroll-timeline-axis"]);
        Assert.Equal("card", style.Map["view-timeline-name"]);
        Assert.Equal("inline", style.Map["view-timeline-axis"]);
        Assert.Equal("hero", style.Map["animation-timeline"]);
        Assert.Equal("--scope-root", style.Map["timeline-scope"]);
    }

    [Fact]
    public async Task OffsetAndImageBorderShorthands_ProjectSubProperties()
    {
        var (computed, box) = await ComputeForSingleBoxAsync(
            ".box { offset: path('M 0 0 L 10 10') 25% auto / center; border-image: url(frame.png) 30 / 10 / 2 stretch; mask-border: url(mask.png) 20 / 3 / 1 round; }");

        Assert.True(computed.TryGetValue(box, out var style));

        Assert.Contains("path(", style.Map["offset-path"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal("25%", style.Map["offset-distance"]);
        Assert.Equal("auto", style.Map["offset-rotate"]);
        Assert.Equal("center", style.Map["offset-anchor"]);

        Assert.Equal("url(frame.png)", style.Map["border-image-source"]);
        Assert.Equal("30", style.Map["border-image-slice"]);
        Assert.Equal("10", style.Map["border-image-width"]);
        Assert.Equal("2", style.Map["border-image-outset"]);
        Assert.Equal("stretch", style.Map["border-image-repeat"]);

        Assert.Equal("url(mask.png)", style.Map["mask-border-source"]);
        Assert.Equal("20", style.Map["mask-border-slice"]);
        Assert.Equal("3", style.Map["mask-border-width"]);
        Assert.Equal("1", style.Map["mask-border-outset"]);
        Assert.Equal("round", style.Map["mask-border-repeat"]);
    }

    [Fact]
    public async Task ContainIntrinsicAliases_ProjectCanonicalSizeKeys()
    {
        var (computed, box) = await ComputeForSingleBoxAsync(
            ".box { contain-intrinsic-inline-size: 120px; contain-intrinsic-block-size: 80px; }");

        Assert.True(computed.TryGetValue(box, out var style));
        Assert.Equal("120px", style.Map["contain-intrinsic-inline-size"]);
        Assert.Equal("80px", style.Map["contain-intrinsic-block-size"]);
        Assert.Equal("120px", style.Map["contain-intrinsic-width"]);
        Assert.Equal("80px", style.Map["contain-intrinsic-height"]);
        Assert.Equal("120px 80px", style.Map["contain-intrinsic-size"]);
    }

    [Fact]
    public async Task FontAndTextDetailAliases_AggregateParentProperties()
    {
        var (computed, box) = await ComputeForSingleBoxAsync(
            ".box { font-variant-ligatures: common-ligatures; font-variant-numeric: tabular-nums; font-synthesis-style: auto; font-synthesis-weight: auto; text-align-last: right; }");

        Assert.True(computed.TryGetValue(box, out var style));
        Assert.Equal("common-ligatures tabular-nums", style.Map["font-variant"]);
        Assert.Equal("auto", style.Map["font-synthesis"]);
        Assert.Equal("right", style.Map["text-align"]);
    }

    [Fact]
    public async Task AnimationAndTimelineAliases_ProjectRangeAndInset()
    {
        var (computed, box) = await ComputeForSingleBoxAsync(
            ".box { view-timeline: hero block / 10%; animation-range: entry 20%; }");

        Assert.True(computed.TryGetValue(box, out var style));
        Assert.Equal("hero", style.Map["view-timeline-name"]);
        Assert.Equal("block", style.Map["view-timeline-axis"]);
        Assert.Equal("10%", style.Map["view-timeline-inset"]);
        Assert.Equal("entry", style.Map["animation-range-start"]);
        Assert.Equal("20%", style.Map["animation-range-end"]);
        Assert.Equal("entry 20%", style.Map["animation-range"]);
    }

    private static async Task<(System.Collections.Generic.Dictionary<Node, CssComputed> Computed, Element Box)> ComputeForSingleBoxAsync(string css)
    {
        var html = $@"
<!doctype html>
<html>
<head>
    <style>{css}</style>
</head>
<body>
    <div class='box'>Probe</div>
</body>
</html>";

        var parser = new HtmlParser(html, new Uri("https://test.local"));
        var doc = parser.Parse();
        var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
        var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
        var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));
        return (computed, box);
    }
}
