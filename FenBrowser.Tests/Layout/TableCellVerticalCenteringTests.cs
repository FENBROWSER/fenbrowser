using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    // reCAPTCHA centres its "I'm not a robot" checkbox with the classic
    // table-cell idiom, straight out of the widget's own stylesheet:
    //   .rc-anchor-center-container { display: table; height: 100% }
    //   .rc-anchor-center-item      { display: table-cell; vertical-align: middle }
    // Without it the checkbox renders jammed into the widget's top-left corner
    // instead of centred against the "I'm not a robot" label.
    public sealed class TableCellVerticalCenteringTests
    {
        [Fact]
        public void TableCellWithVerticalAlignMiddle_CentresItsChild()
        {
            var container = new Element("div");
            var cell = new Element("div");
            var box = new Element("span");

            container.AppendChild(cell);
            cell.AppendChild(box);

            var styles = new Dictionary<Node, CssComputed>
            {
                [container] = new CssComputed { Display = "table", Width = 52, Height = 74 },
                [cell] = new CssComputed { Display = "table-cell", VerticalAlign = "middle" },
                [box] = new CssComputed { Display = "inline-block", Width = 28, Height = 28 }
            };

            var boxes = LayoutTestHelper.LayoutTree(container, styles, width: 300, height: 300);

            Assert.True(boxes.TryGetValue(container, out var containerBox));
            Assert.True(boxes.TryGetValue(box, out var checkbox));

            // 74 tall cell, 28 tall content => 23px of free space above.
            var expectedTop = containerBox.ContentBox.Top + ((74f - 28f) / 2f);
            Assert.Equal(expectedTop, checkbox.MarginBox.Top, 1f);
        }

        // The real widget sizes the table with height:100% against a fixed-height
        // parent rather than a fixed height of its own.
        [Fact]
        public void TableCellWithPercentageHeight_CentresItsChild()
        {
            var parent = new Element("div");
            var container = new Element("div");
            var cell = new Element("div");
            var box = new Element("span");

            parent.AppendChild(container);
            container.AppendChild(cell);
            cell.AppendChild(box);

            var styles = new Dictionary<Node, CssComputed>
            {
                [parent] = new CssComputed { Display = "block", Width = 52, Height = 74 },
                [container] = new CssComputed { Display = "table", HeightPercent = 100 },
                [cell] = new CssComputed { Display = "table-cell", VerticalAlign = "middle" },
                [box] = new CssComputed { Display = "inline-block", Width = 28, Height = 28 }
            };

            var boxes = LayoutTestHelper.LayoutTree(parent, styles, width: 300, height: 300);

            Assert.True(boxes.TryGetValue(parent, out var parentBox));
            Assert.True(boxes.TryGetValue(box, out var checkbox));

            var expectedTop = parentBox.ContentBox.Top + ((74f - 28f) / 2f);
            Assert.Equal(expectedTop, checkbox.MarginBox.Top, 1f);
        }
    }
}
