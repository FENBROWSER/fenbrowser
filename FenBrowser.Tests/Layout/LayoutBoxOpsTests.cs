using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout.Contexts;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class LayoutBoxOpsTests
    {
        [Fact]
        public void ComputeBoxModelFromContent_PreservesExistingBoxOriginForAllBoxLayers()
        {
            var element = new Element("div");
            var box = new BlockBox(element, new CssComputed());
            box.Geometry.ContentBox = new SkiaSharp.SKRect(120, 56, 120, 56);
            box.Geometry.Padding = new Thickness(3, 4, 5, 6);
            box.Geometry.Border = new Thickness(7, 8, 9, 10);
            box.Geometry.Margin = new Thickness(11, 12, 13, 14);

            LayoutBoxOps.ComputeBoxModelFromContent(box, 90, 38);

            Assert.Equal(120f, box.Geometry.ContentBox.Left);
            Assert.Equal(56f, box.Geometry.ContentBox.Top);
            Assert.Equal(210f, box.Geometry.ContentBox.Right);
            Assert.Equal(94f, box.Geometry.ContentBox.Bottom);

            Assert.Equal(117f, box.Geometry.PaddingBox.Left);
            Assert.Equal(52f, box.Geometry.PaddingBox.Top);
            Assert.Equal(215f, box.Geometry.PaddingBox.Right);
            Assert.Equal(100f, box.Geometry.PaddingBox.Bottom);

            Assert.Equal(110f, box.Geometry.BorderBox.Left);
            Assert.Equal(44f, box.Geometry.BorderBox.Top);
            Assert.Equal(224f, box.Geometry.BorderBox.Right);
            Assert.Equal(110f, box.Geometry.BorderBox.Bottom);

            Assert.Equal(99f, box.Geometry.MarginBox.Left);
            Assert.Equal(32f, box.Geometry.MarginBox.Top);
            Assert.Equal(237f, box.Geometry.MarginBox.Right);
            Assert.Equal(124f, box.Geometry.MarginBox.Bottom);
        }

        [Fact]
        public void PositionSubtree_IgnoresStaleRelativeInsets_WhenInsetIsNotExplicitlyAuthored()
        {
            var style = new CssComputed
            {
                Position = "relative",
                BottomPercent = 100d
            };

            var element = new Element("div");
            var box = new BlockBox(element, style);
            box.Geometry.ContentBox = new SKRect(0, 0, 40, 20);
            box.Geometry.Padding = new Thickness();
            box.Geometry.Border = new Thickness();
            box.Geometry.Margin = new Thickness();
            LayoutBoxOps.ComputeBoxModelFromContent(box, 40, 20);

            var state = new LayoutState(new SKSize(800, 600), 800, 100, 800, 600);
            LayoutBoxOps.PositionSubtree(box, 100, 50, state);

            Assert.Equal(100f, box.Geometry.MarginBox.Left);
            Assert.Equal(50f, box.Geometry.MarginBox.Top);
        }

        [Fact]
        public void PositionSubtree_AppliesRelativeInset_WhenInsetWasExplicitlyAuthored()
        {
            var style = new CssComputed
            {
                Position = "relative",
                BottomPercent = 100d
            };
            style.Map["bottom"] = "100%";

            var element = new Element("div");
            var box = new BlockBox(element, style);
            box.Geometry.ContentBox = new SKRect(0, 0, 40, 20);
            box.Geometry.Padding = new Thickness();
            box.Geometry.Border = new Thickness();
            box.Geometry.Margin = new Thickness();
            LayoutBoxOps.ComputeBoxModelFromContent(box, 40, 20);

            var state = new LayoutState(new SKSize(800, 600), 800, 100, 800, 600);
            LayoutBoxOps.PositionSubtree(box, 100, 50, state);

            Assert.Equal(100f, box.Geometry.MarginBox.Left);
            Assert.Equal(-50f, box.Geometry.MarginBox.Top);
        }

        [Fact]
        public void PositionSubtree_UsesAuthoredInsetMapValue_WhenTypedInsetIsStale()
        {
            var style = new CssComputed
            {
                Position = "relative",
                Top = -100d
            };
            style.Map["top"] = "0px";

            var element = new Element("div");
            var box = new BlockBox(element, style);
            box.Geometry.ContentBox = new SKRect(0, 0, 40, 20);
            box.Geometry.Padding = new Thickness();
            box.Geometry.Border = new Thickness();
            box.Geometry.Margin = new Thickness();
            LayoutBoxOps.ComputeBoxModelFromContent(box, 40, 20);

            var state = new LayoutState(new SKSize(800, 600), 800, 100, 800, 600);
            LayoutBoxOps.PositionSubtree(box, 100, 50, state);

            Assert.Equal(100f, box.Geometry.MarginBox.Left);
            Assert.Equal(50f, box.Geometry.MarginBox.Top);
        }

        [Fact]
        public void PositionSubtree_AppliesInsetShorthandForBottom()
        {
            var style = new CssComputed
            {
                Position = "relative"
            };
            style.Map["inset"] = "auto auto 100% auto";

            var element = new Element("div");
            var box = new BlockBox(element, style);
            box.Geometry.ContentBox = new SKRect(0, 0, 40, 20);
            box.Geometry.Padding = new Thickness();
            box.Geometry.Border = new Thickness();
            box.Geometry.Margin = new Thickness();
            LayoutBoxOps.ComputeBoxModelFromContent(box, 40, 20);

            var state = new LayoutState(new SKSize(800, 600), 800, 100, 800, 600);
            LayoutBoxOps.PositionSubtree(box, 100, 50, state);

            Assert.Equal(100f, box.Geometry.MarginBox.Left);
            Assert.Equal(-50f, box.Geometry.MarginBox.Top);
        }
    }
}
