using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    // CSS 2.1 8.3.1: a box with no top border or padding collapses its top margin
    // with its first in-flow child's, and the collapsed margin belongs to the
    // parent. The child half was implemented (the child is placed flush against the
    // parent's content top) but the parent never adopted the margin, so the margin
    // was discarded outright: an element with margin-top landed at y=0 whenever it
    // was its parent's first child, while its margin-left applied normally.
    public sealed class FirstChildTopMarginCollapseTests
    {
        [Fact]
        public void FirstChildTopMargin_MovesTheParentDown()
        {
            var (boxes, body, target) = LayoutFirstChildWithMargin(marginTop: 50);

            // The collapsed margin is applied to BODY, not swallowed.
            Assert.Equal(50f, boxes[body].BorderBox.Top, 1f);

            // And the child sits flush against the parent's content top, so the
            // margin is applied exactly once.
            Assert.Equal(50f, boxes[target].BorderBox.Top, 1f);

            // The horizontal margin was never the broken half; keep it covered so a
            // fix here cannot regress it.
            Assert.Equal(40f, boxes[target].BorderBox.Left, 1f);
        }

        [Fact]
        public void NoTopMargin_LeavesTheParentAtTheTop()
        {
            var (boxes, body, target) = LayoutFirstChildWithMargin(marginTop: 0);

            Assert.Equal(0f, boxes[body].BorderBox.Top, 1f);
            Assert.Equal(0f, boxes[target].BorderBox.Top, 1f);
        }

        // A top border on the parent stops the collapse, so the margin stays inside
        // and the parent does not move.
        [Fact]
        public void ParentWithTopBorder_KeepsTheMarginInside()
        {
            var (boxes, body, target) = LayoutFirstChildWithMargin(marginTop: 50, bodyBorderTop: 10);

            Assert.Equal(0f, boxes[body].BorderBox.Top, 1f);
            Assert.Equal(60f, boxes[target].BorderBox.Top, 1f);
        }

        private static (IReadOnlyDictionary<Node, FenBrowser.FenEngine.Layout.BoxModel> Boxes, Element Body, Element Target)
            LayoutFirstChildWithMargin(double marginTop, double bodyBorderTop = 0)
        {
            var doc = new HtmlParser(
                "<!doctype html><html><body><div id='t'></div></body></html>",
                new System.Uri("https://margin.test/")).Parse();
            var html = doc.DocumentElement;
            var body = html.Descendants().OfType<Element>().First(e => e.LocalName == "body");
            var target = doc.GetElementById("t");

            var bodyStyle = new CssComputed
            {
                Display = "block",
                Margin = new FenBrowser.Core.Thickness(0)
            };
            if (bodyBorderTop > 0)
            {
                bodyStyle.BorderThickness = new FenBrowser.Core.Thickness(0, bodyBorderTop, 0, 0);
            }

            var styles = new Dictionary<Node, CssComputed>
            {
                [html] = new CssComputed { Display = "block" },
                [body] = bodyStyle,
                [target] = new CssComputed
                {
                    Display = "block",
                    Width = 300,
                    Height = 120,
                    Margin = new FenBrowser.Core.Thickness(40, marginTop, 0, 0)
                }
            };

            return (LayoutTestHelper.LayoutTree(html, styles, 640, 360), body, target);
        }
    }
}
