using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.Tests.Layout;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public sealed class TailwindUtilityCssContractTests
    {
        [Fact]
        public async Task VariableBackedBorderUtility_ProducesEffectiveBorder()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        * { --tw-border-style: solid; }
        .border { border-style: var(--tw-border-style); border-width: 1px; }
        .border-\[\#dadce0\] { border-color: #dadce0; }
    </style>
</head>
<body>
    <div id='target' class='border border-[#dadce0]'>x</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var target = doc.GetElementById("target");

            var computed = await CssLoader.ComputeAsync(root, new System.Uri("https://test.local"), null);
            var style = computed[target];

            Assert.Equal(1d, style.BorderThickness.Top);
            Assert.Equal("solid", style.BorderStyleTop);
            Assert.True(style.BorderBrushColor.HasValue);
            var color = style.BorderBrushColor.Value;
            Assert.Equal((byte)0xda, color.Red);
            Assert.Equal((byte)0xdc, color.Green);
            Assert.Equal((byte)0xe0, color.Blue);
        }

        [Fact]
        public async Task RegisteredBorderStyleInitialValue_ProducesEffectiveBorder()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        @property --tw-border-style { syntax: ""*""; inherits: false; initial-value: solid; }
        .border { border-style: var(--tw-border-style); border-width: 1px; }
        .border-\[\#dadce0\] { border-color: #dadce0; }
    </style>
</head>
<body>
    <div id='target' class='border border-[#dadce0]'>x</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var target = doc.GetElementById("target");

            var computed = await CssLoader.ComputeAsync(root, new System.Uri("https://test.local"), null);
            var style = computed[target];

            Assert.Equal(1d, style.BorderThickness.Top);
            Assert.Equal("solid", style.BorderStyleTop);
            Assert.True(style.BorderBrushColor.HasValue);
        }

        [Fact]
        public async Task CompactCalcDivisionWidth_ResolvesAgainstParentSize()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        #parent { width: 230px; }
        #target { width: calc(100%/1.15); height: 10px; }
    </style>
</head>
<body>
    <div id='parent'><div id='target'></div></div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var target = doc.GetElementById("target");

            var computed = await CssLoader.ComputeAsync(root, new System.Uri("https://test.local"), null, viewportWidth: 800, viewportHeight: 600);
            var computer = new LayoutEngineComputer(computed, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var targetBox = computer.GetBox(target);
            Assert.InRange(targetBox.ContentBox.Width, 199.5f, 200.5f);
        }

        [Fact]
        public async Task EscapedArbitraryCalcWidthSelector_MatchesTailwindClass()
        {
            const string html = @"
<!doctype html>
<html>
<head>
    <style>
        #parent { width: 230px; }
        .w-\[calc\(100\%\/1\.15\)\] { width: calc(100%/1.15); height: 10px; }
    </style>
</head>
<body>
    <div id='parent'><div id='target' class='w-[calc(100%/1.15)]'></div></div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var target = doc.GetElementById("target");

            var computed = await CssLoader.ComputeAsync(root, new System.Uri("https://test.local"), null, viewportWidth: 800, viewportHeight: 600);
            var computer = new LayoutEngineComputer(computed, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));

            var targetBox = computer.GetBox(target);
            Assert.InRange(targetBox.ContentBox.Width, 199.5f, 200.5f);
        }

    }
}
