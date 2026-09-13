using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class CssBorderShorthandTests
    {
        [Theory]
        [InlineData("max(1px, .0625rem)")]
        [InlineData("calc(1px + 1px)")]
        [InlineData("min(2px, 1rem)")]
        [InlineData("clamp(1px, 0.1em, 3px)")]
        public async Task BorderShorthand_MathFunctionIsTheWidth_NotTheColor(string width)
        {
            // CSS Values 4 §10: a math function is a <length> wherever a length is
            // accepted. Primer's buttons write `border: solid max(1px, .0625rem)
            // transparent`; treating the function as the color left the width at
            // its `medium` initial value, so every button grew a 3px frame.
            string html = @"
<!doctype html>
<html>
<head>
    <style>.box { border: solid " + width + @" red; }</style>
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

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.Equal("solid", style.Map["border-top-style"]);
            Assert.StartsWith(width.Substring(0, width.IndexOf('(') + 1), style.Map["border-top-width"]);
            Assert.Equal("red", style.Map["border-top-color"]);
            Assert.InRange(style.BorderThickness.Top, 0.5, 2.5);
        }

        [Theory]
        [InlineData("--w: max(1px, .0625rem); --c: red", "border: solid var(--w) var(--c);", 1.0, "red")]
        [InlineData("--w: 2px", "border: solid var(--w) transparent;", 2.0, "transparent")]
        [InlineData("--b: 2px dashed blue", "border: var(--b);", 2.0, "blue")]
        [InlineData("--w: 2px", "border: solid var(--w) red; border-width: 4px;", 4.0, "red")]
        [InlineData("--w: 2px", "border: solid var(--w) red; border-color: green;", 2.0, "green")]
        public async Task BorderShorthand_WithVar_IsSplitAfterSubstitution(string custom, string declarations, double expectedWidth, string expectedColor)
        {
            // CSS Variables §3.1: a shorthand carrying var() makes its longhands
            // pending-substitution values, split only once the reference is
            // resolved. Classifying the raw tokens filed the var() as the color and
            // left the width at `medium`; a later longhand still overrides.
            string html = @"
<!doctype html>
<html>
<head>
    <style>:root { " + custom + @" } .box { " + declarations + @" }</style>
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

            Assert.True(computed.TryGetValue(box, out var style));
            Assert.InRange(style.BorderThickness.Top, expectedWidth - 0.1, expectedWidth + 0.1);
            Assert.InRange(style.BorderThickness.Left, expectedWidth - 0.1, expectedWidth + 0.1);
            Assert.Equal(expectedColor, style.Map["border-top-color"]);
            Assert.DoesNotContain("pending", style.Map["border-top-style"]);
        }
    }
}
