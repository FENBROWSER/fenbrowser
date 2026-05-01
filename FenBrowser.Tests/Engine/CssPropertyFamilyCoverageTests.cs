using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssPropertyFamilyCoverageTests
    {
        [Theory]
        [MemberData(nameof(SupportsFamilyCases))]
        public async Task Supports_RequestedPropertyFamilies_AndChildProperties(string[] checks)
        {
            var condition = string.Join(" and ", checks);
            var (computed, box) = await ComputeForSingleBoxAsync(
                $"@supports {condition} {{ .box {{ width: 123px; }} }}");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("123px", style.Map["width"]);
        }

        [Fact]
        public async Task BackgroundShorthand_ExpandsChildPropertiesIncludingSizeOriginAndClip()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { background: #112233 url(hero.png) no-repeat fixed left 20% / 40px 60% padding-box content-box; }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("#112233", style.Map["background-color"]);
            Assert.Equal("url(hero.png)", style.Map["background-image"]);
            Assert.Equal("no-repeat", style.Map["background-repeat"]);
            Assert.Equal("fixed", style.Map["background-attachment"]);
            Assert.Equal("left 20%", style.Map["background-position"]);
            Assert.Equal("left", style.Map["background-position-x"]);
            Assert.Equal("20%", style.Map["background-position-y"]);
            Assert.Equal("40px 60%", style.Map["background-size"]);
            Assert.Equal("padding-box", style.Map["background-origin"]);
            Assert.Equal("content-box", style.Map["background-clip"]);
        }

        [Fact]
        public async Task TransitionShorthand_ExpandsChildProperties()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { transition: opacity 200ms ease-in 50ms, transform 1s linear; }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("opacity, transform", style.Map["transition-property"]);
            Assert.Equal("200ms, 1s", style.Map["transition-duration"]);
            Assert.Equal("ease-in, linear", style.Map["transition-timing-function"]);
            Assert.Equal("50ms, 0s", style.Map["transition-delay"]);
        }

        [Fact]
        public async Task OverflowLogicalChildren_MapToPhysicalAxes()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { overflow-inline: clip; overflow-block: scroll; }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("clip", style.OverflowX);
            Assert.Equal("scroll", style.OverflowY);
        }

        [Fact]
        public async Task LogicalSizeChildren_SupportPercentAndFunctionValues()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { min-inline-size: 25%; min-block-size: 10%; max-inline-size: calc(70% - 10px); max-block-size: calc(50% - 2px); }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal(25d, style.MinWidthPercent);
            Assert.Equal(10d, style.MinHeightPercent);
            Assert.Equal("calc(70% - 10px)", style.Map["max-inline-size"]);
            Assert.Equal("calc(50% - 2px)", style.Map["max-block-size"]);
            Assert.True(!string.IsNullOrWhiteSpace(style.MaxWidthExpression) || style.MaxWidth.HasValue || style.MaxWidthPercent.HasValue);
            Assert.True(!string.IsNullOrWhiteSpace(style.MaxHeightExpression) || style.MaxHeight.HasValue || style.MaxHeightPercent.HasValue);
        }

        [Fact]
        public async Task SupportsUnderstandsRequestedChildProperties()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                "@supports (background-position-x: left) and (inset-inline-start: 0) and (min-inline-size: 1px) and (transition-behavior: allow-discrete) { .box { width: 123px; } }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("123px", style.Map["width"]);
        }

        [Fact]
        public async Task PlaceShorthands_MapToLonghands_WhenExplicitLonghandsMissing()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { display:flex; place-items: center end; place-content: space-between stretch; place-self: flex-end center; }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("center", style.AlignItems);
            Assert.Equal("end", style.JustifyItems);
            Assert.Equal("space-between", style.AlignContent);
            Assert.Equal("stretch", style.JustifyContent);
            Assert.Equal("flex-end", style.AlignSelf);
            Assert.Equal("center", style.JustifySelf);
            Assert.Equal("center", style.Map["align-items"]);
            Assert.Equal("end", style.Map["justify-items"]);
            Assert.Equal("space-between", style.Map["align-content"]);
            Assert.Equal("stretch", style.Map["justify-content"]);
            Assert.Equal("flex-end", style.Map["align-self"]);
            Assert.Equal("center", style.Map["justify-self"]);
        }

        [Fact]
        public async Task IntrinsicSizingKeywords_AreTrackedAsExpressions()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { width: fit-content; min-width: min-content; max-width: max-content; }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("fit-content", style.WidthExpression);
            Assert.Equal("min-content", style.MinWidthExpression);
            Assert.Equal("max-content", style.MaxWidthExpression);

            var (logicalComputed, logicalBox) = await ComputeForSingleBoxAsync(
                ".box { max-inline-size: fit-content(120px); }");

            Assert.True(logicalComputed.TryGetValue(logicalBox, out var logicalStyle));
            Assert.Equal("fit-content(120px)", logicalStyle.Map["max-inline-size"]);
            Assert.Equal("fit-content(120px)", logicalStyle.MaxWidthExpression);
        }

        [Fact]
        public async Task HasSelector_WithRelationalCombinators_AppliesInCssPipeline()
        {
            var html = @"
<!doctype html>
<html>
<head>
    <style>
        .host:has(> img) { width: 101px; }
        .adjacent-host:has(+ .next-hit) { width: 102px; }
        .sibling-host:has(~ .later-hit) { width: 103px; }
        .nested-host:has(> article .leaf) { width: 104px; }
    </style>
</head>
<body>
    <div class='host'><img alt='ok'/></div>
    <div class='adjacent-host'></div><div class='next-hit'></div>
    <div class='sibling-host'></div><p></p><div class='later-hit'></div>
    <section class='nested-host'><article><span class='leaf'>x</span></article></section>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://test.local"));
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            Assert.Equal("101px", computed[doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("host"))].Map["width"]);
            Assert.Equal("102px", computed[doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("adjacent-host"))].Map["width"]);
            Assert.Equal("103px", computed[doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("sibling-host"))].Map["width"]);
            Assert.Equal("104px", computed[doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("nested-host"))].Map["width"]);
        }

        [Fact]
        public async Task FlexItemProperties_FromShorthandAndLonghands_AreProjected()
        {
            var html = @"
<!doctype html>
<html>
<head>
    <style>
        .parent { display: flex; }
        .box {
            flex: 2 0 120px;
            align-self: center;
            order: 3;
            min-width: 85px;
            min-height: 40px;
            max-width: 320px;
            max-height: 200px;
        }
    </style>
</head>
<body>
    <div class='parent'><div class='box'>probe</div></div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://test.local"));
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));
            var style = computed[box];

            Assert.Equal(2d, style.FlexGrow ?? -1d);
            Assert.Equal(0d, style.FlexShrink ?? -1d);
            Assert.InRange(style.FlexBasis ?? -1d, 119.99d, 120.01d);
            Assert.Equal("center", style.AlignSelf);
            Assert.Equal(3, style.Order ?? -1);
            Assert.InRange(style.MinWidth ?? -1d, 84.99d, 85.01d);
            Assert.InRange(style.MinHeight ?? -1d, 39.99d, 40.01d);
            Assert.InRange(style.MaxWidth ?? -1d, 319.99d, 320.01d);
            Assert.InRange(style.MaxHeight ?? -1d, 199.99d, 200.01d);
        }

        [Fact]
        public async Task TranslateLonghand_ComposesWithTransformShorthand()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { translate: -50% 0; transform: scale(1.2); }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("-50% 0", style.Map["translate"]);
            Assert.Equal("scale(1.2)", style.Map["transform"]);
            Assert.Contains("translate(", style.Transform ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("scale(1.2)", style.Transform ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task ModernSizingFunctions_ArePreservedAsExpressions()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { width: min(80%, 600px); height: clamp(32px, 5vw, 64px); max-width: max(20rem, 50%); }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("min(80%, 600px)", style.WidthExpression);
            Assert.Equal("clamp(32px, 5vw, 64px)", style.HeightExpression);
            Assert.Equal("max(20rem, 50%)", style.MaxWidthExpression);
        }

        [Fact]
        public async Task LogicalMapping_LtrInlineAndInset_AppliesToTypedProjections()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { position: absolute; inline-size: 240px; block-size: 120px; margin-inline: 10px 20px; padding-inline: 8px 12px; inset-inline: 4px 6px; }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.InRange(style.Width ?? -1d, 239.99d, 240.01d);
            Assert.InRange(style.Height ?? -1d, 119.99d, 120.01d);
            Assert.InRange(style.Margin.Left, 9.99d, 10.01d);
            Assert.InRange(style.Margin.Right, 19.99d, 20.01d);
            Assert.InRange(style.Padding.Left, 7.99d, 8.01d);
            Assert.InRange(style.Padding.Right, 11.99d, 12.01d);
            Assert.InRange(style.Left ?? -1d, 3.99d, 4.01d);
            Assert.InRange(style.Right ?? -1d, 5.99d, 6.01d);
        }

        [Fact]
        public async Task LogicalBorderDirectionalLonghands_ProjectToPhysicalSides()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { direction: rtl; border-inline-start: 5px solid #010203; border-inline-end-width: 7px; border-inline-end-style: solid; border-inline-color: #111111 #222222; border-block-width: 3px 4px; border-block-start-style: dashed; border-block-end-style: solid; }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.InRange(style.BorderThickness.Left, 6.99d, 7.01d);
            Assert.InRange(style.BorderThickness.Right, 4.99d, 5.01d);
            Assert.InRange(style.BorderThickness.Top, 2.99d, 3.01d);
            Assert.InRange(style.BorderThickness.Bottom, 3.99d, 4.01d);
            Assert.Equal("dashed", style.BorderStyleTop);
            Assert.Equal("solid", style.BorderStyleBottom);
            Assert.Equal("solid", style.BorderStyleLeft);
            Assert.Equal("solid", style.BorderStyleRight);
            Assert.Equal("#111111", style.Map["border-right-color"]);
            Assert.Equal("#222222", style.Map["border-left-color"]);
        }

        [Fact]
        public async Task InventoryAliasAndLogicalBackgroundAxes_AreNormalizedIntoRuntimeMap()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { background-position-inline: right; background-position-block: 25%; background-repeat-inline: repeat; background-repeat-block: no-repeat; word-wrap: break-word; font-width: condensed; }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("right", style.Map["background-position-x"]);
            Assert.Equal("25%", style.Map["background-position-y"]);
            Assert.Equal("right 25%", style.Map["background-position"]);
            Assert.Equal("repeat-x", style.Map["background-repeat"]);
            Assert.Equal("break-word", style.Map["overflow-wrap"]);
            Assert.Equal("condensed", style.Map["font-stretch"]);
            Assert.Equal("break-word", style.OverflowWrap);
        }

        [Fact]
        public async Task ReplacedAndContainmentProperties_AreProjected()
        {
            var (computed, box) = await ComputeForSingleBoxAsync(
                ".box { object-fit: cover; object-position: center top; aspect-ratio: 16 / 9; overflow: hidden; clip-path: inset(10%); contain: layout paint; border-radius: 50%; }");

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("cover", style.ObjectFit);
            Assert.Equal("center top", style.ObjectPosition);
            Assert.InRange(style.AspectRatio ?? 0d, 1.777d, 1.778d);
            Assert.Equal("hidden", style.Overflow);
            Assert.Equal("inset(10%)", style.ClipPath);
            Assert.Equal("layout paint", style.Contain);
            Assert.Equal("50%", style.Map["border-radius"]);
        }

        [Fact]
        public async Task CssWideKeywords_ResolveForInheritedAndNonInheritedProperties()
        {
            var html = @"
<!doctype html>
<html>
<head>
    <style>
        .parent { color: rgb(10, 20, 30); width: 240px; }
        .unset { color: unset; width: unset; }
        .initial { color: initial; width: initial; }
        .revert { color: revert; width: revert; }
    </style>
</head>
<body>
    <div class='parent'>
        <span class='unset'>u</span>
        <span class='initial'>i</span>
        <span class='revert'>r</span>
    </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://test.local"));
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var parent = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("parent"));
            var unset = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("unset"));
            var initial = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("initial"));
            var revert = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("revert"));

            var parentStyle = computed[parent];
            var unsetStyle = computed[unset];
            var initialStyle = computed[initial];
            var revertStyle = computed[revert];

            Assert.Equal(parentStyle.ForegroundColor, unsetStyle.ForegroundColor);
            Assert.Equal("auto", unsetStyle.Map["width"]);
            Assert.Equal("auto", initialStyle.Map["width"]);
            Assert.Equal("auto", revertStyle.Map["width"]);
            Assert.Equal(CssComputed.GetInitialValue("color"), initialStyle.Map["color"]);
            Assert.Equal(CssComputed.GetInitialValue("color"), revertStyle.Map["color"]);
        }

        [Fact]
        public async Task BorderBoxWidth_DoesNotInflateWithPaddingAndBorder()
        {
            var html = @"
<!doctype html>
<html>
<head>
    <style>
        .box {
            box-sizing: border-box;
            width: 100px;
            padding: 20px;
            border: 1px solid black;
            display: block;
        }
    </style>
</head>
<body>
    <div class='box'>probe</div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://test.local"));
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var body = doc.Descendants().OfType<Element>().First(e => e.TagName == "BODY");
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var computer = new MinimalLayoutComputer(computed, 400, 300, "https://test.local/");
            computer.Arrange(body, new SKRect(0, 0, 400, 300));
            var layout = computer.GetBox(box);

            Assert.NotNull(layout);
            Assert.InRange(layout.BorderBox.Width, 99.5f, 100.5f);
        }

        [Fact]
        public async Task SvgFillAndStroke_AreSupportedAndProjected()
        {
            var html = @"
<!doctype html>
<html>
<head>
    <style>
        .icon { fill: rgb(1, 2, 3); stroke: rgb(4, 5, 6); }
        @supports (fill: red) and (stroke: blue) { .probe { width: 77px; } }
    </style>
</head>
<body>
    <svg class='icon' viewBox='0 0 24 24'><path d='M0 0h24v24H0z'/></svg>
    <div class='probe'></div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://test.local"));
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);

            var icon = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("icon"));
            var probe = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("probe"));
            var iconStyle = computed[icon];
            var probeStyle = computed[probe];

            Assert.Equal("77px", probeStyle.Map["width"]);
            Assert.Contains("rgb", iconStyle.Map["fill"], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("rgb", iconStyle.Map["stroke"], StringComparison.OrdinalIgnoreCase);
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

        public static System.Collections.Generic.IEnumerable<object[]> SupportsFamilyCases()
        {
            yield return new object[] { new[] { "(display: inline-flex)" } };
            yield return new object[] { new[] { "(position: absolute)", "(top: 1px)", "(right: 2px)", "(bottom: 3px)", "(left: 4px)", "(inset: 1px 2px 3px 4px)", "(inset-block: 1px 2px)", "(inset-inline: 3px 4px)", "(inset-block-start: 1px)", "(inset-block-end: 2px)", "(inset-inline-start: 3px)", "(inset-inline-end: 4px)" } };
            yield return new object[] { new[] { "(width: 10px)", "(min-width: 1px)", "(max-width: 99%)", "(inline-size: 20%)", "(min-inline-size: 5%)", "(max-inline-size: calc(80% - 2px))" } };
            yield return new object[] { new[] { "(height: 10px)", "(min-height: 1px)", "(max-height: 99%)", "(block-size: 20%)", "(min-block-size: 5%)", "(max-block-size: calc(80% - 2px))" } };
            yield return new object[] { new[] { "(margin: 1px 2px 3px 4px)", "(margin-top: 1px)", "(margin-right: 2px)", "(margin-bottom: 3px)", "(margin-left: 4px)", "(margin-block: 1px 2px)", "(margin-block-start: 1px)", "(margin-block-end: 2px)", "(margin-inline: 3px 4px)", "(margin-inline-start: 3px)", "(margin-inline-end: 4px)" } };
            yield return new object[] { new[] { "(padding: 1px 2px 3px 4px)", "(padding-top: 1px)", "(padding-right: 2px)", "(padding-bottom: 3px)", "(padding-left: 4px)", "(padding-block: 1px 2px)", "(padding-block-start: 1px)", "(padding-block-end: 2px)", "(padding-inline: 3px 4px)", "(padding-inline-start: 3px)", "(padding-inline-end: 4px)" } };
            yield return new object[] { new[] { "(color: rgb(1, 2, 3))" } };
            yield return new object[] { new[] { "(background: #112233 url(hero.png) no-repeat fixed left top / 40px 60% padding-box content-box)", "(background-color: #112233)", "(background-image: url(hero.png))", "(background-repeat: no-repeat)", "(background-attachment: fixed)", "(background-position: left top)", "(background-position-x: left)", "(background-position-y: top)", "(background-size: cover)", "(background-origin: padding-box)", "(background-clip: content-box)" } };
            yield return new object[] { new[] { "(border: 1px solid #000)", "(border-width: 1px)", "(border-style: solid)", "(border-color: #000)", "(border-top: 1px solid #000)", "(border-right: 1px solid #000)", "(border-bottom: 1px solid #000)", "(border-left: 1px solid #000)", "(border-top-width: 1px)", "(border-right-width: 1px)", "(border-bottom-width: 1px)", "(border-left-width: 1px)", "(border-top-style: solid)", "(border-right-style: solid)", "(border-bottom-style: solid)", "(border-left-style: solid)", "(border-top-color: #000)", "(border-right-color: #000)", "(border-bottom-color: #000)", "(border-left-color: #000)", "(border-block: 1px solid #000)", "(border-inline: 1px solid #000)", "(border-block-start: 1px solid #000)", "(border-block-end: 1px solid #000)", "(border-inline-start: 1px solid #000)", "(border-inline-end: 1px solid #000)", "(border-block-start-width: 1px)", "(border-block-end-width: 1px)", "(border-inline-start-width: 1px)", "(border-inline-end-width: 1px)", "(border-block-start-style: solid)", "(border-block-end-style: solid)", "(border-inline-start-style: solid)", "(border-inline-end-style: solid)", "(border-block-start-color: #000)", "(border-block-end-color: #000)", "(border-inline-start-color: #000)", "(border-inline-end-color: #000)" } };
            yield return new object[] { new[] { "(font-size: 16px)" } };
            yield return new object[] { new[] { "(font-family: serif)" } };
            yield return new object[] { new[] { "(font-weight: 700)" } };
            yield return new object[] { new[] { "(line-height: 1.5)" } };
            yield return new object[] { new[] { "(vertical-align: middle)" } };
            yield return new object[] { new[] { "(white-space: nowrap)" } };
            yield return new object[] { new[] { "(text-align: center)" } };
            yield return new object[] { new[] { "(overflow: auto)", "(overflow-x: clip)", "(overflow-y: scroll)", "(overflow-inline: hidden)", "(overflow-block: visible)" } };
            yield return new object[] { new[] { "(box-sizing: border-box)" } };
            yield return new object[] { new[] { "(justify-content: space-between)" } };
            yield return new object[] { new[] { "(align-items: center)" } };
            yield return new object[] { new[] { "(flex: 1 1 auto)", "(flex-grow: 2)", "(flex-shrink: 1)", "(flex-basis: 10px)", "(align-self: center)", "(order: 3)" } };
            yield return new object[] { new[] { "(gap: 12px 8px)", "(row-gap: 12px)", "(column-gap: 8px)" } };
            yield return new object[] { new[] { "(place-items: center end)", "(place-content: space-between stretch)", "(place-self: center start)" } };
            yield return new object[] { new[] { "(z-index: 10)", "(transform: translateX(-50%))", "(translate: -50% 0)" } };
            yield return new object[] { new[] { "(object-fit: cover)", "(object-position: center top)", "(aspect-ratio: 16 / 9)" } };
            yield return new object[] { new[] { "(clip-path: inset(10%))", "(contain: layout paint)" } };
            yield return new object[] { new[] { "(fill: currentColor)", "(stroke: #000)" } };
            yield return new object[] { new[] { "(transition: opacity 200ms ease-in 50ms)", "(transition-property: opacity)", "(transition-duration: 200ms)", "(transition-timing-function: ease-in)", "(transition-delay: 50ms)", "(transition-behavior: allow-discrete)" } };
            yield return new object[] { new[] { "(width: fit-content)", "(min-width: min-content)", "(max-width: max-content)", "(max-inline-size: fit-content(80%))" } };
            yield return new object[] { new[] { "(width: min(80%, 600px))", "(height: clamp(32px, 5vw, 64px))", "(max-width: max(20rem, 50%))" } };
        }
    }
}
