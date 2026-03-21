using System;
using System.Collections.Generic;
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
    public class CssGridTemplateShorthandTests
    {
        [Fact]
        public async Task ComputeAsync_GridTemplateShorthand_PopulatesTypedGridFields()
        {
            var (root, grid, _, _, _, _, _) = await BuildWikipediaStyleGridAsync();
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://en.wikipedia.org"), null);

            Assert.True(computed.TryGetValue(grid, out var gridStyle));
            Assert.Equal("min-content min-content min-content 1fr", gridStyle.GridTemplateRows);
            Assert.Equal("minmax(0, 59.25rem) min-content", gridStyle.GridTemplateColumns);
            Assert.Equal("\"top side\" \"title side\" \"toolbar side\" \"content side\"", gridStyle.GridTemplateAreas);
        }

        [Fact]
        public async Task GridFormattingContext_UsesGridTemplateShorthand_ForWikipediaStyleLayout()
        {
            var (root, grid, left, right, footer) = await BuildSimpleAreaGridAsync();
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://example.test"), null);

            LayoutMetrics MeasureNode(Node node, SKSize _, int __)
            {
                if (!computed.TryGetValue(node, out var style))
                {
                    return new LayoutMetrics();
                }

                return new LayoutMetrics
                {
                    MaxChildWidth = (float)(style.Width ?? 0),
                    ContentHeight = (float)(style.Height ?? 0),
                    ActualHeight = (float)(style.Height ?? 0)
                };
            }

            var arranged = new Dictionary<Node, SKRect>();
            GridLayoutComputer.Measure(grid, new SKSize(200, 60), computed, 0, MeasureNode);
            GridLayoutComputer.Arrange(
                grid,
                new SKRect(0, 0, 200, 60),
                computed,
                new Dictionary<Node, BoxModel>(),
                0,
                (node, rect, depth) => arranged[node] = rect,
                MeasureNode);

            Assert.Equal(0f, arranged[left].Left, 1);
            Assert.Equal(0f, arranged[left].Top, 1);
            Assert.Equal(100f, arranged[right].Left, 1);
            Assert.Equal(0f, arranged[right].Top, 1);
            Assert.Equal(0f, arranged[footer].Left, 1);
            Assert.Equal(40f, arranged[footer].Top, 1);
        }

        private static async Task<(Element Root, Element Grid, Element Top, Element Title, Element Toolbar, Element Content, Element Side)> BuildWikipediaStyleGridAsync()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    html, body { margin: 0; }
    #grid {
      display: grid;
      width: 1200px;
      grid-template: min-content min-content min-content 1fr / minmax(0, 59.25rem) min-content;
      grid-template-areas:
        'top side'
        'title side'
        'toolbar side'
        'content side';
    }
    #top, #title, #toolbar, #content, #side {
      display: block;
      font-size: 0;
      line-height: 0;
    }
    #top { grid-area: top; height: 10px; }
    #title { grid-area: title; height: 40px; }
    #toolbar { grid-area: toolbar; height: 20px; }
    #content { grid-area: content; height: 200px; }
    #side { grid-area: side; width: 120px; height: 300px; }
  </style>
</head>
<body>
  <div id='grid'>
    <div id='top'></div>
    <div id='title'></div>
    <div id='toolbar'></div>
    <div id='content'></div>
    <div id='side'></div>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://en.wikipedia.org"));
            var document = parser.Parse();
            var root = document.DocumentElement;
            Assert.NotNull(root);

            Element FindById(string id) =>
                root.Descendants().OfType<Element>().First(e => string.Equals(e.GetAttribute("id"), id, StringComparison.Ordinal));

            return (
                root,
                FindById("grid"),
                FindById("top"),
                FindById("title"),
                FindById("toolbar"),
                FindById("content"),
                FindById("side"));
        }

        private static async Task<(Element Root, Element Grid, Element Left, Element Right, Element Footer)> BuildSimpleAreaGridAsync()
        {
            const string html = @"
<!doctype html>
<html>
<head>
  <style>
    html, body { margin: 0; }
    #grid {
      display: grid;
      width: 200px;
      grid-template: 40px 20px / 100px 100px;
      grid-template-areas:
        'left right'
        'footer right';
    }
    #left, #right, #footer {
      display: block;
      font-size: 0;
      line-height: 0;
    }
    #left { grid-area: left; width: 100px; height: 40px; }
    #right { grid-area: right; width: 100px; height: 60px; }
    #footer { grid-area: footer; width: 100px; height: 20px; }
  </style>
</head>
<body>
  <div id='grid'>
    <div id='left'></div>
    <div id='right'></div>
    <div id='footer'></div>
  </div>
</body>
</html>";

            var parser = new HtmlParser(html, new Uri("https://example.test"));
            var document = parser.Parse();
            var root = document.DocumentElement;
            Assert.NotNull(root);

            Element FindById(string id) =>
                root.Descendants().OfType<Element>().First(e => string.Equals(e.GetAttribute("id"), id, StringComparison.Ordinal));

            return (
                root,
                FindById("grid"),
                FindById("left"),
                FindById("right"),
                FindById("footer"));
        }

    }
}
