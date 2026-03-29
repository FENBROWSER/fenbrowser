using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class BlockFormattingContextRelayoutTests
    {
        [Fact]
        public void AutoWidthNestedBlock_Reflow_DoesNotAccumulateChildVerticalOffsets()
        {
            var root = new Element("div");
            var floated = new Element("div");
            var wrapper = new Element("div");
            var lead = new Element("div");
            var shell = new Element("div");

            lead.AppendChild(new Text("Header"));
            shell.AppendChild(new Text("Search shell"));
            wrapper.AppendChild(lead);
            wrapper.AppendChild(shell);
            floated.AppendChild(wrapper);
            root.AppendChild(floated);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 640, Height = 240 },
                [floated] = new CssComputed { Display = "block", Float = "left" },
                [wrapper] = new CssComputed { Display = "block" },
                [lead] = new CssComputed { Display = "block", Height = 30 },
                [shell] = new CssComputed { Display = "block", Height = 20 }
            };

            var rootBox = LayoutRoot(root, styles, 640, 240);
            var leadBox = FindBox(rootBox, lead);
            var shellBox = FindBox(rootBox, shell);

            Assert.NotNull(leadBox);
            Assert.NotNull(shellBox);

            Assert.Equal(0f, leadBox.Geometry.MarginBox.Top, 1);
            Assert.Equal(30f, shellBox.Geometry.MarginBox.Top, 1);
        }

        private static LayoutBox LayoutRoot(Element root, Dictionary<Node, CssComputed> styles, float width, float height)
        {
            var builder = new BoxTreeBuilder(styles);
            var rootBox = builder.Build(root);
            Assert.NotNull(rootBox);

            var state = new LayoutState(
                new SKSize(width, height),
                width,
                height,
                width,
                height);

            FormattingContext.Resolve(rootBox).Layout(rootBox, state);
            return rootBox;
        }

        private static LayoutBox FindBox(LayoutBox box, Node target)
        {
            if (box == null)
            {
                return null;
            }

            if (ReferenceEquals(box.SourceNode, target))
            {
                return box;
            }

            foreach (var child in box.Children)
            {
                var found = FindBox(child, target);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
    }
}
