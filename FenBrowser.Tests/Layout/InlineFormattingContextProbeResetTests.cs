using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class InlineFormattingContextProbeResetTests
    {
        [Fact]
        public void FloatThumbnailInlineWrapper_DoesNotKeepProbeOffset()
        {
            var root = new Element("div");
            var floated = new Element("div");
            var thumbInner = new Element("div");
            var wrapper = new Element("span");
            var link = new Element("a");
            var image = new Element("img");
            image.SetAttribute("width", "120");
            image.SetAttribute("height", "162");

            link.AppendChild(image);
            wrapper.AppendChild(link);
            thumbInner.AppendChild(wrapper);
            floated.AppendChild(thumbInner);
            root.AppendChild(floated);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 859, Height = 400 },
                [floated] = new CssComputed { Display = "block", Float = "left" },
                [thumbInner] = new CssComputed { Display = "block", TextAlign = SKTextAlign.Center },
                [wrapper] = new CssComputed { Display = "inline" },
                [link] = new CssComputed { Display = "inline" },
                [image] = new CssComputed { Display = "inline" }
            };

            var rootBox = LayoutRoot(root, styles, 859, 400);
            var floatedBox = FindBox(rootBox, floated);
            var thumbInnerBox = FindBox(rootBox, thumbInner);
            var wrapperBox = FindBox(rootBox, wrapper);
            var linkBox = FindBox(rootBox, link);
            var imageBox = FindBox(rootBox, image);

            Assert.NotNull(floatedBox);
            Assert.NotNull(thumbInnerBox);
            Assert.NotNull(wrapperBox);
            Assert.NotNull(linkBox);
            Assert.NotNull(imageBox);

            Assert.Equal(120f, floatedBox.Geometry.MarginBox.Width, 1);
            Assert.Equal(120f, thumbInnerBox.Geometry.MarginBox.Width, 1);
            Assert.Equal(0f, wrapperBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(0f, linkBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(0f, imageBox.Geometry.MarginBox.Left, 1);
            Assert.Equal(120f, imageBox.Geometry.MarginBox.Width, 1);
            Assert.Equal(162f, imageBox.Geometry.MarginBox.Height, 1);
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
