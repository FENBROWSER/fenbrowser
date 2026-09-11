using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    /// <summary>
    /// google.com's "AI Mode" chip: a shrink-to-fit button whose content is a flex row of
    /// icon + label. CSS Flexbox §9.9.1: the row's max-content size is the sum of its
    /// items' max-content contributions, so the button must be wide enough for both and
    /// the label must not wrap. Also covers HTML §15.5.15 textarea intrinsic sizing.
    /// </summary>
    public sealed class ShrinkToFitFlexRowContentTests
    {
        private const string Html = @"
<!doctype html>
<html><head><style>
  body { margin: 0; font-family: Arial; font-size: 14px; }
  .row { display: flex; width: 300px; height: 50px; }
  .chip { align-self: center; flex-shrink: 0; line-height: 20px; margin-inline-start: 8px; padding: 0 8px; height: 36px; border: 1px solid #888; }
  .u { align-items: center; display: flex; justify-content: center; }
  .i { display: inline-flex; height: 20px; width: 20px; }
  .t { padding: 0 4px; }
  textarea { font-size: 16px; line-height: 22px; padding: 0; border: 0; margin: 0; }
</style></head>
<body>
  <div class='row'><div style='flex:1'></div><button id='chip' class='chip'><div class='u'><span class='i'></span><span id='label' class='t'>AI Mode</span></div></button></div>
  <textarea id='ta' rows='1'></textarea>
  <textarea id='ta3' rows='3'></textarea>
</body></html>";

        [Fact]
        public async System.Threading.Tasks.Task ShrinkToFitButton_FitsIconAndLabelOnOneLine()
        {
            var (doc, boxes) = await LayoutAsync(Html);
            var chip = boxes[Find(doc, "chip")];
            var label = boxes[Find(doc, "label")];

            // 20px icon + 8px label padding + the text itself, plus 16px button padding.
            Assert.True(chip.ContentBox.Width >= 20f + 8f + 40f, $"chip content {chip.ContentBox.Width}px is too narrow for icon + label");
            Assert.InRange(label.BorderBox.Height, 18f, 22f);
            Assert.True(label.BorderBox.Right <= chip.ContentBox.Right + 0.5f, "label overflows the chip");

            // Button contents are centered vertically inside the 36px button.
            float chipCenter = chip.BorderBox.MidY;
            Assert.InRange(Math.Abs(label.BorderBox.MidY - chipCenter), 0f, 2f);
        }

        [Fact]
        public async System.Threading.Tasks.Task Textarea_IntrinsicHeightIsRowsTimesLineHeight()
        {
            var (doc, boxes) = await LayoutAsync(Html);
            Assert.InRange(boxes[Find(doc, "ta")].ContentBox.Height, 21f, 23f);
            Assert.InRange(boxes[Find(doc, "ta3")].ContentBox.Height, 65f, 67f);
        }

        private static Element Find(Document doc, string id) => doc.Descendants().OfType<Element>().First(e => e.Id == id);

        private static async System.Threading.Tasks.Task<(Document, Dictionary<Node, BoxModel>)> LayoutAsync(string html)
        {
            var doc = new HtmlParser(html).Parse();
            var root = doc.Children.OfType<Element>().First(e => e.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, new Uri("https://test.local"), null);
            var computer = new LayoutEngineComputer(styles, 800, 600);
            computer.Measure(doc, new SKSize(800, 600));
            computer.Arrange(doc, new SKRect(0, 0, 800, 600));
            return (doc, computer.GetAllBoxes().ToDictionary(kv => kv.Key, kv => kv.Value));
        }
    }
}
