using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// bing.com's results header, reduced to the two shapes that broke it. Expected
    /// positions come from Chrome on the same markup.
    /// </summary>
    public sealed class BingHeaderInlineLayoutTests
    {
        // The search box is an inline-block inside an inline-block form. Its own inline-block
        // children were laid out relative to the page instead of the box, so the tool button,
        // the input and the icon piled up at the left edge of the header.
        private const string NestedInlineBlockHtml = @"<!doctype html><html><head><style>
body { margin: 0; font: 14px Arial, sans-serif; }
form { display: inline-block; margin-right: 10px; }
.logo { display: inline-block; width: 32px; height: 32px; margin: 8px 97px 0 31px; vertical-align: top; }
.box { display: inline-block; border: 1px solid #ccc; border-right: 0; }
.tool { display: inline-block; width: 20px; height: 20px; padding: 7px; margin: 0 3px 0 5px; vertical-align: middle; border: 1px solid #999; }
.field { display: inline-block; width: 497px; height: 44px; margin: 1px 0 1px 1px; vertical-align: top; }
.icon { display: inline-block; width: 36px; height: 36px; vertical-align: middle; }
</style></head><body><form><a class='logo' id='logo'></a><div class='box' id='box'><div class='tool' id='tool'></div><div class='field' id='field'></div><div class='icon' id='icon'></div></div></form></body></html>";

        [Fact]
        public async Task InlineBlockInsideAnInlineBlock_PlacesItsChildrenInsideItself()
        {
            var (renderer, doc) = await RenderAsync(NestedInlineBlockHtml);

            var box = Rect(renderer, doc, "box");
            var tool = Rect(renderer, doc, "tool");
            var field = Rect(renderer, doc, "field");
            var icon = Rect(renderer, doc, "icon");

            Assert.Equal(160f, box.Left, 1);
            // Chrome: tool 166, field 206, icon 703 (border 1 + margins from the box's left edge).
            Assert.Equal(box.Left + 6f, tool.Left, 1);
            Assert.Equal(box.Left + 46f, field.Left, 1);
            Assert.Equal(box.Left + 543f, icon.Left, 1);
        }

        // Scope bar tabs: inline-block list items whose ::after underline is a width:100% block.
        // The percentage was resolved against the page while the tab was being sized to its
        // content, so every tab became page-wide and they stacked instead of sitting in a row.
        private const string ScopeTabsHtml = @"<!doctype html><html><head><style>
body { margin: 0; font: 14px Arial, sans-serif; }
ul { margin: 0; padding: 0; list-style: none; }
li { display: inline-block; margin: 0 12px; padding: 3px 0; line-height: 30px; font-size: 11px; vertical-align: top; }
li::after { content: ''; width: 100%; height: 3px; display: block; background: #000; margin: 3px auto 0; }
</style></head><body><nav><ul><li id='t1'><a href='#'>ALL</a></li><li id='t2'><a href='#'>IMAGES</a></li><li id='t3'><a href='#'>VIDEOS</a></li></ul></nav></body></html>";

        [Fact]
        public async Task InlineBlockTabsWithAFullWidthUnderline_ShrinkToTheirLabelsOnOneRow()
        {
            var (renderer, doc) = await RenderAsync(ScopeTabsHtml);

            var t1 = Rect(renderer, doc, "t1");
            var t2 = Rect(renderer, doc, "t2");
            var t3 = Rect(renderer, doc, "t3");

            // Chrome: 19.6, 42.8 and 41.6 px wide, all at y = 0.
            Assert.True(t1.Width < 80f && t2.Width < 80f && t3.Width < 80f,
                $"tabs should shrink to their labels: {t1.Width}, {t2.Width}, {t3.Width}");
            Assert.Equal(t1.Top, t2.Top, 1);
            Assert.Equal(t1.Top, t3.Top, 1);
            Assert.True(t2.Left > t1.Right && t3.Left > t2.Right, "tabs should sit in one row");
        }

        // The underline itself: once the tab has shrunk to its label, a width:100% block inside
        // it must resolve against the tab, not keep the page width it was given while the tab
        // was being measured. The active tab's underline ran across the whole scope bar.
        private const string TabUnderlineHtml = @"<!doctype html><html><head><style>
body { margin: 0; font: 14px Arial, sans-serif; }
ul { margin: 0; padding: 0; list-style: none; }
li { display: inline-block; margin: 0 12px; padding: 3px 0; line-height: 30px; font-size: 11px; vertical-align: top; }
a { display: inline-block; }
.u { width: 100%; height: 3px; display: block; background: #000; margin: 3px auto 0; }
</style></head><body><nav><ul><li id='t1'><a href='#'>ALL</a><span class='u' id='u1'></span></li><li id='t2'><a href='#'>IMAGES</a><span class='u' id='u2'></span></li></ul></nav>
<div id='d' style='display:inline-block'><a href='#'>Inline block with a block child</a><div class='u' id='u3'></div></div></body></html>";

        [Fact]
        public async Task PercentageWidthBlockInsideAShrunkInlineBlock_ResolvesAgainstThatInlineBlock()
        {
            var (renderer, doc) = await RenderAsync(TabUnderlineHtml);

            foreach (var (tabId, underlineId) in new[] { ("t1", "u1"), ("t2", "u2"), ("d", "u3") })
            {
                var tab = Rect(renderer, doc, tabId);
                var underline = Rect(renderer, doc, underlineId);
                Assert.True(Math.Abs(underline.Width - tab.Width) < 1f,
                    $"#{underlineId} should be as wide as #{tabId} ({tab.Width}), was {underline.Width}");
                Assert.Equal(tab.Left, underline.Left, 1);
            }
        }

        private static async Task<(SkiaDomRenderer Renderer, Document Doc)> RenderAsync(string html)
        {
            const int width = 1280;
            const int height = 800;
            var baseUri = new Uri("https://test.local/");
            var doc = new HtmlParser(html, baseUri).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: width, viewportHeight: height);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(width, height);
            using var canvas = new SKCanvas(bitmap);
            renderer.Render(root, canvas, styles, new SKRect(0, 0, width, height), baseUri.AbsoluteUri, (_, _) => { });
            return (renderer, doc);
        }

        private static SKRect Rect(SkiaDomRenderer renderer, Document doc, string id)
        {
            var element = doc.GetElementById(id);
            Assert.True(renderer.LastLayout.TryGetElementRect(element, out var rect), $"#{id} has no layout rect");
            return new SKRect(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
        }
    }
}
