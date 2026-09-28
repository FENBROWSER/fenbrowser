using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    /// <summary>
    /// CSS Cascade 4 §7.2: an inherited property takes its parent's computed value. An
    /// incremental recascade rooted at one element still has that element's real parent.
    /// Typing into x.com's username field re-styles the &lt;input&gt; on its own; its
    /// `color: inherit` white text came out black until a wider pass ran.
    /// </summary>
    public class IncrementalCascadeInheritanceTests
    {
        [Fact]
        public async Task ComputeSubtreeAsync_RootInheritsFromItsParentsStoredStyle()
        {
            CssLoader.ClearCaches();

            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    #wrap { color: rgb(255, 255, 255); font-size: 17px; }
    input { color: inherit; font: inherit; }
  </style>
</head>
<body><label id='wrap'><input id='field' type='text'></label></body>
</html>";

            var doc = new HtmlParser(html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var uri = new Uri("https://test.local");
            var full = await CssLoader.ComputeAsync(root, uri, null);
            var field = doc.Descendants().OfType<Element>().First(e => e.Id == "field");
            Assert.Equal(new SKColor(255, 255, 255), full[field].ForegroundColor);

            var subtree = await CssLoader.ComputeSubtreeAsync(root, field, uri, null);

            Assert.Equal(new SKColor(255, 255, 255), subtree[field].ForegroundColor);
            Assert.Equal(17d, subtree[field].FontSize ?? 0d, 1);
        }
    }
}
