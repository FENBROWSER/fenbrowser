using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class MediaWikiDeduplicatedInlineStyleTests
    {
        [Fact]
        public async Task MwDeduplicatedInlineStyleLink_ReappliesCachedTemplateStyle()
        {
            const string html = @"<!doctype html>
<html>
<head>
  <style data-mw-deduplicate='mw-data:TemplateStyles:r1'>
    .hlist li { display: inline; }
    .hlist ul { list-style: none; margin: 0; padding: 0; }
  </style>
</head>
<body>
  <div class='hlist'>
    <ul><li>alpha</li><li>beta</li></ul>
  </div>
  <link rel='mw-deduplicated-inline-style' href='mw-data:TemplateStyles:r1'>
  <div class='hlist' id='later-list'>
    <ul><li id='later-item'>gamma</li><li>delta</li></ul>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://example.test/"));
            var document = parser.Parse();
            var root = document.DocumentElement ?? document.Children.OfType<Element>().First();

            var computed = await CssLoader.ComputeAsync(root, new Uri("https://example.test/"), null);

            var laterItem = root.Descendants().OfType<Element>()
                .First(e => string.Equals(e.GetAttribute("id"), "later-item", StringComparison.Ordinal));
            var laterList = laterItem.ParentElement;

            Assert.True(computed.TryGetValue(laterItem, out var liStyle));
            Assert.Equal("inline", liStyle.Display);

            Assert.True(computed.TryGetValue(laterList, out var ulStyle));
            Assert.Equal("none", ulStyle.ListStyleType);
        }

        [Fact]
        public async Task MwDeduplicatedInlineStyleLink_Reapplies_WhenStyleKeyOmitsMwDataPrefix()
        {
            const string html = @"<!doctype html>
<html>
<head>
  <style data-mw-deduplicate='TemplateStyles:r1'>
    .hlist li { display: inline; }
    .hlist ul { list-style: none; margin: 0; padding: 0; }
  </style>
</head>
<body>
  <div class='hlist'>
    <ul><li>alpha</li><li>beta</li></ul>
  </div>
  <link rel='mw-deduplicated-inline-style' href='mw-data:TemplateStyles:r1'>
  <div class='hlist' id='later-list'>
    <ul><li id='later-item'>gamma</li><li>delta</li></ul>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://example.test/"));
            var document = parser.Parse();
            var root = document.DocumentElement ?? document.Children.OfType<Element>().First();

            var computed = await CssLoader.ComputeAsync(root, new Uri("https://example.test/"), null);

            var laterItem = root.Descendants().OfType<Element>()
                .First(e => string.Equals(e.GetAttribute("id"), "later-item", StringComparison.Ordinal));
            var laterList = laterItem.ParentElement;

            Assert.True(computed.TryGetValue(laterItem, out var liStyle));
            Assert.Equal("inline", liStyle.Display);

            Assert.True(computed.TryGetValue(laterList, out var ulStyle));
            Assert.Equal("none", ulStyle.ListStyleType);
        }
    }
}
