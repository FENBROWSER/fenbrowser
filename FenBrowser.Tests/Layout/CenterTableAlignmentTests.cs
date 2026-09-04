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

namespace FenBrowser.Tests.Layout
{
    // Hacker News wraps its whole layout in <center><table id="hnmain">. Chrome
    // spells <center> as text-align:-webkit-center and then resets that legacy
    // value to start on table boxes, so the table is centred but its contents are
    // not. We had plain text-align:center, which inherited all the way down and
    // drew the header nav ("Hacker News new | past | comments ...") centred in its
    // cell instead of packed left after the logo.
    //
    // Values below are Chrome 152's own, read off news.ycombinator.com at 1280px.
    public sealed class CenterTableAlignmentTests
    {
        private const string HnHeader = """
<!doctype html>
<html><head><style>
  body { margin: 8px }
</style></head>
<body>
<center>
<table id="hnmain" border="0" cellpadding="0" cellspacing="0" width="85%">
<tr><td>
  <table border="0" cellpadding="0" cellspacing="0" width="100%" style="padding:2px">
  <tr>
    <td id="logocell" style="width:18px;padding-right:4px"><a href="/"><b>Y</b></a></td>
    <td id="navcell" style="line-height:12pt; height:10px;"><span class="pagetop"><b class="hnname"><a href="news">Hacker News</a></b>
      <a href="newest">new</a> | <a href="front">past</a> | <a href="ask">ask</a></span></td>
    <td id="logincell" style="text-align:right;padding-right:4px;"><span class="pagetop"><a id="loginlink" href="login">login</a></span></td>
  </tr>
  </table>
</td></tr>
</table>
</center>
</body></html>
""";

        private static async Task<(IReadOnlyDictionary<Node, BoxModel> Boxes, IReadOnlyDictionary<Node, CssComputed> Styles, Document Doc)> LayoutHeaderAsync()
        {
            var doc = new HtmlParser(HnHeader, new Uri("https://news.ycombinator.test/")).Parse();
            var root = doc.DocumentElement;
            var computed = await CssLoader.ComputeAsync(root, new Uri("https://news.ycombinator.test/"), null);
            var styles = new Dictionary<Node, CssComputed>(computed);
            var boxes = LayoutTestHelper.LayoutTree(root, styles, 1280, 800);
            return (boxes, styles, doc);
        }

        // The centring must not reach the table's contents.
        [Fact]
        public async Task CenterDoesNotCentreTableContents()
        {
            var (_, styles, doc) = await LayoutHeaderAsync();
            var nav = doc.GetElementById("navcell");
            Assert.NotNull(nav);

            styles.TryGetValue(nav, out var navStyle);
            Assert.NotNull(navStyle);
            Assert.NotEqual(SKTextAlign.Center, navStyle.TextAlign ?? SKTextAlign.Left);
        }

        // ...but the table itself is still centred in the <center>.
        [Fact]
        public async Task CenterStillCentresTheTableItself()
        {
            var (boxes, _, doc) = await LayoutHeaderAsync();
            var main = doc.GetElementById("hnmain");
            Assert.NotNull(main);
            Assert.True(boxes.TryGetValue(main, out var box));

            var leftGap = box.BorderBox.Left;
            var rightGap = 1280 - box.BorderBox.Right;
            // Symmetric within a pixel of rounding.
            Assert.True(Math.Abs(leftGap - rightGap) <= 2f,
                $"table not centred: left gap {leftGap}, right gap {rightGap}");
        }

        // The nav cell's own content starts at its left edge, right after the logo.
        [Fact]
        public async Task NavLinksPackLeftAfterTheLogo()
        {
            var (boxes, _, doc) = await LayoutHeaderAsync();
            var nav = doc.GetElementById("navcell");
            var hnname = doc.QuerySelector(".hnname") as Element;
            Assert.NotNull(nav);
            Assert.NotNull(hnname);
            Assert.True(boxes.TryGetValue(nav, out var navBox));
            Assert.True(boxes.TryGetValue(hnname, out var nameBox));

            // Chrome: nav cell x=128, "Hacker News" x=129 - one pixel of margin.
            var inset = nameBox.BorderBox.Left - navBox.BorderBox.Left;
            Assert.True(inset >= 0 && inset <= 8f,
                $"nav content is not packed left: inset {inset}px from the cell edge");
        }

        // The login cell reserves padding-right:4px, so "login" is not flush
        // against the table's right edge.
        [Fact]
        public async Task LoginKeepsItsRightPadding()
        {
            var (boxes, _, doc) = await LayoutHeaderAsync();
            var cell = doc.GetElementById("logincell");
            var link = doc.GetElementById("loginlink");
            Assert.NotNull(cell);
            Assert.NotNull(link);
            Assert.True(boxes.TryGetValue(cell, out var cellBox));
            Assert.True(boxes.TryGetValue(link, out var linkBox));

            var gap = cellBox.BorderBox.Right - linkBox.BorderBox.Right;
            Assert.True(gap >= 3.5f,
                $"login is flush against the cell edge: gap {gap}px, expected the 4px padding-right");

            // ...and the cell must not sit outside the table that contains it.
            // A width:100% table with padding overflowed by its padding until
            // tables were made border-box, which ate the gap on screen even
            // though the padding itself was applied correctly.
            var inner = doc.QuerySelector("#navcell")?.ParentElement?.ParentElement as Element;
            Assert.NotNull(inner);
            Assert.True(boxes.TryGetValue(inner, out var innerBox));
            var main = doc.GetElementById("hnmain");
            Assert.True(boxes.TryGetValue(main, out var mainBox));
            Assert.True(innerBox.BorderBox.Right <= mainBox.BorderBox.Right + 0.5f,
                $"header table overflows its container: inner right {innerBox.BorderBox.Right}, " +
                $"container right {mainBox.BorderBox.Right}");
        }
    }
}
