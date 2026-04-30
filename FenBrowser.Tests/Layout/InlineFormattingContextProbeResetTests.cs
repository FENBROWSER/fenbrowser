using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core;
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

        [Fact]
        public void InlineBlockFlow_LongUnbreakableText_DoesNotExpandFiniteUsedWidth()
        {
            var root = new Element("div");
            var headline = new Element("h3");
            var tileContent = new Element("div");
            var longUnbreakableToken = new Text(new string('A', 1024));

            headline.AppendChild(longUnbreakableToken);
            tileContent.AppendChild(headline);
            root.AppendChild(tileContent);

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 320, Height = 300 },
                [tileContent] = new CssComputed { Display = "block", Width = 320 },
                [headline] = new CssComputed { Display = "block" }
            };

            var rootBox = LayoutRoot(root, styles, 320, 300);
            var tileContentBox = FindBox(rootBox, tileContent);
            var headlineBox = FindBox(rootBox, headline);

            Assert.NotNull(tileContentBox);
            Assert.NotNull(headlineBox);

            Assert.True(
                tileContentBox.Geometry.MarginBox.Width <= 321f,
                $"Expected finite block width to remain bounded; got {tileContentBox.Geometry.MarginBox.Width}");
            Assert.True(
                headlineBox.Geometry.MarginBox.Width <= 321f,
                $"Expected inline formatting not to expand finite used width; got {headlineBox.Geometry.MarginBox.Width}");
        }

        [Fact]
        public void InlineButtons_AutoWidth_DoNotOverlap_AndRetainIntrinsicLabelWidth()
        {
            var root = new Element("div");
            var ctas = new Element("div");
            var primary = new Element("a");
            var secondary = new Element("a");
            primary.AppendChild(new Text("Learn more"));
            secondary.AppendChild(new Text("Buy"));
            ctas.AppendChild(primary);
            ctas.AppendChild(secondary);
            root.AppendChild(ctas);

            var buttonPadding = new Thickness(15, 8, 15, 8);
            var buttonBorder = new Thickness(1);
            var buttonMinContentWidth = 28d;

            var styles = new Dictionary<Node, CssComputed>
            {
                [root] = new CssComputed { Display = "block", Width = 942, Height = 200 },
                [ctas] = new CssComputed { Display = "block", Width = 942, TextAlign = SKTextAlign.Center },
                [primary] = new CssComputed
                {
                    Display = "inline-block",
                    WhiteSpace = "nowrap",
                    MinWidth = buttonMinContentWidth,
                    Padding = buttonPadding,
                    BorderThickness = buttonBorder
                },
                [secondary] = new CssComputed
                {
                    Display = "inline-block",
                    WhiteSpace = "nowrap",
                    MinWidth = buttonMinContentWidth,
                    Padding = buttonPadding,
                    BorderThickness = buttonBorder,
                    Margin = new Thickness(14, 0, 0, 0)
                }
            };

            var rootBox = LayoutRoot(root, styles, 942, 200);
            var primaryBox = FindBox(rootBox, primary);
            var secondaryBox = FindBox(rootBox, secondary);

            Assert.NotNull(primaryBox);
            Assert.NotNull(secondaryBox);
            Assert.True(
                primaryBox.Geometry.MarginBox.Width > 70f,
                $"Expected primary button to grow for label width; got {primaryBox.Geometry.MarginBox.Width}");
            Assert.True(
                secondaryBox.Geometry.MarginBox.Left >= primaryBox.Geometry.MarginBox.Right - 0.5f,
                $"Expected secondary button to not overlap primary. primary={primaryBox.Geometry.MarginBox} secondary={secondaryBox.Geometry.MarginBox}");
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
