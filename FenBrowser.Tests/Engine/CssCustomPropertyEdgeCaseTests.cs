using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CssCustomPropertyEdgeCaseTests
    {
        [Fact]
        public async Task ComputeAsync_RootCustomProperties_AreCaseSensitive()
        {
            CssLoader.ClearCaches();

            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    :root { --MyVar: rgb(1, 2, 3); --myvar: rgb(4,5,6); }
    #t { color: var(--MyVar); background-color: var(--myvar); }
  </style>
</head>
<body><div id='t'>X</div></body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.Id == "t");
            var style = computed[target];

            Assert.Equal("rgb(1, 2, 3)", style.CustomProperties["--MyVar"]);
            Assert.Equal("rgb(4,5,6)", style.CustomProperties["--myvar"]);
            Assert.Equal("rgb(1, 2, 3)", style.Map["color"]);
            Assert.Equal("rgb(4,5,6)", style.Map["background-color"]);
        }

        [Fact]
        public async Task ComputeAsync_InlineStyle_ParsesSemicolonsInsideFunctions()
        {
            CssLoader.ClearCaches();

            const string html = @"
<!doctype html>
<html>
<body>
  <div id='t' style=""background-image: url('data:image/svg+xml;utf8,<svg></svg>'); color: red;"">X</div>
</body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var target = doc.Descendants().OfType<Element>().First(e => e.Id == "t");
            var style = computed[target];

            Assert.Equal("red", style.Map["color"]);
            Assert.Contains("data:image/svg+xml;utf8", style.Map["background-image"], StringComparison.Ordinal);
        }

        [Fact]
        public async Task ComputeAsync_RootFontSizeFromVar_PreservesRemBasis()
        {
            CssLoader.ClearCaches();

            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    :root { --base-size: 12px; }
    html { font-size: var(--base-size); }
    .box { width: 2rem; }
  </style>
</head>
<body><div class='box'>X</div></body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var box = doc.Descendants().OfType<Element>().First(e => e.ClassList.Contains("box"));

            Assert.InRange(computed[root].FontSize ?? 0d, 11.999d, 12.001d);
            Assert.InRange(computed[box].Width ?? 0d, 23.999d, 24.001d);
        }

        [Fact]
        public async Task ComputeAsync_ButtonVarsWithCharset_ResolveDisplayAndBoxMetrics()
        {
            CssLoader.ClearCaches();
            Assert.True(CssLoader.TryThickness("calc(16px - 1px)", out var calcThickness, 16.0));
            Assert.InRange(calcThickness.Left, 14.999, 15.001);

            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    @charset ""UTF-8"";
    .button {
      --sk-button-border-width: 1px;
      --sk-button-min-width-basis: 60px;
      --sk-button-padding-horizontal: 16px;
      --sk-button-padding-vertical: 9px;
      --sk-button-background: rgb(0, 113, 227);
      --sk-button-border-color: rgb(0, 102, 204);
      --sk-button-box-sizing: content-box;
      --sk-button-width: auto;
      --sk-button-display: inline-block;
    }
    .button {
      background: var(--sk-button-background);
      border-color: var(--sk-button-border-color);
      display: var(--sk-button-display);
      box-sizing: var(--sk-button-box-sizing);
      width: var(--sk-button-width);
      min-width: calc(var(--sk-button-min-width-basis) - var(--sk-button-padding-horizontal) * 2);
      border-style: solid;
      border-width: var(--sk-button-border-width);
      padding-inline: calc(var(--sk-button-padding-horizontal) - var(--sk-button-border-width));
      padding-block: calc(var(--sk-button-padding-vertical) - var(--sk-button-border-width));
    }
  </style>
</head>
<body><a id='cta' class='button'>Learn more</a></body>
</html>";

            var parser = new HtmlParser(html);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var cta = doc.Descendants().OfType<Element>().First(e => e.Id == "cta");
            var style = computed[cta];

            Assert.Equal("inline-block", style.Map["display"]);
            Assert.Equal("inline-block", style.Display);
            Assert.True(style.Map.TryGetValue("padding-inline", out var rawPaddingInline), "padding-inline missing from computed map");
            Assert.DoesNotContain("var(", rawPaddingInline ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.True(style.BorderThickness.Left >= 1d, $"Expected left border >= 1px, got {style.BorderThickness.Left}");
            Assert.True(style.Padding.Left >= 8d, $"Expected horizontal padding from calc(var-1px), got {style.Padding.Left}, raw='{rawPaddingInline}'");
            Assert.True(
                (style.MinWidth ?? 0d) > 0d || !string.IsNullOrWhiteSpace(style.MinWidthExpression),
                $"Expected min-width to resolve as numeric or expression, got MinWidth={style.MinWidth}, MinWidthExpression='{style.MinWidthExpression}'");
        }
    }
}
