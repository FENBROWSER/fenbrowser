using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    // LAYOUT-004: out-of-flow boxes must resolve their containing block per
    // CSS 2.1 §10.1 — the nearest positioned (or transformed) ancestor's padding
    // box, or the initial containing block when none exists — not the immediate
    // parent box. Tests avoid margins for placement because this engine drops
    // collapsed top margins; body padding creates the known offsets instead.
    public class ContainingBlockResolutionTests
    {
        private static Dictionary<Node, CssComputed> NewStyles()
        {
            return new Dictionary<Node, CssComputed>();
        }

        private static CssComputed Block(double width, double height)
        {
            return new CssComputed { Display = "block", Width = width, Height = height };
        }

        private static (LayoutEngine Engine, LayoutResult Result) Layout(
            Document document, Dictionary<Node, CssComputed> styles,
            double viewportWidth = 1280, double viewportHeight = 720)
        {
            var engine = new LayoutEngine(styles, (float)viewportWidth, (float)viewportHeight);
            var result = engine.ComputeLayout(document, 0, 0, (float)viewportWidth, availableHeight: (float)viewportHeight);
            Assert.NotNull(result);
            return (engine, result);
        }

        private static ElementGeometry Rect(LayoutResult result, Element element)
        {
            Assert.True(result.ElementRects.TryGetValue(element, out var geometry),
                "Missing layout rect for element.");
            return geometry;
        }

        [Fact]
        [Trait("Category", "Layout")]
        public void Absolute_NearestPositionedAncestor_ThroughStaticIntermediate()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var anchor = new Element("DIV");
            var spacer = new Element("DIV");
            var middle = new Element("DIV");
            var abs = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(anchor);
            anchor.AppendChild(spacer);
            anchor.AppendChild(middle);
            middle.AppendChild(abs);

            var styles = NewStyles();
            styles[html] = Block(1280, 720);
            styles[body] = new CssComputed { Display = "block", Width = 1280, Height = 720, Padding = new Thickness(0, 10, 0, 0) };
            styles[anchor] = new CssComputed { Display = "block", Position = "relative", Margin = new Thickness(100, 0, 0, 0), Width = 400, Height = 300 };
            styles[spacer] = Block(400, 50);
            styles[middle] = Block(200, 100);
            styles[abs] = new CssComputed { Display = "block", Position = "absolute", Left = 10, Top = 20, Width = 30, Height = 15 };

            var (_, result) = Layout(document, styles);

            var geometry = Rect(result, abs);
            // CB is the anchor's padding box at (100, 10); the static intermediate
            // sits at (100, 60) and must not be used.
            Assert.Equal(110f, geometry.X, 0.5f);
            Assert.Equal(30f, geometry.Y, 0.5f);
        }

        [Fact]
        [Trait("Category", "Layout")]
        public void Absolute_WithoutPositionedAncestor_UsesInitialContainingBlock()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var abs = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(abs);

            var styles = NewStyles();
            styles[html] = new CssComputed { Display = "block", Width = 1280, Height = 720, Padding = new Thickness(0, 10, 0, 0) };
            styles[body] = new CssComputed { Display = "block", Margin = new Thickness(60, 0, 0, 0), Width = 1160, Height = 700 };
            styles[abs] = new CssComputed { Display = "block", Position = "absolute", Left = 10, Top = 20, Width = 30, Height = 15 };

            var (_, result) = Layout(document, styles);

            var geometry = Rect(result, abs);
            // No positioned ancestor: the containing block is the viewport at (0, 0).
            Assert.Equal(10f, geometry.X, 0.5f);
            Assert.Equal(20f, geometry.Y, 0.5f);
        }

        [Fact]
        [Trait("Category", "Layout")]
        public void Absolute_SkipsMultipleStaticAncestors()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var positioned = new Element("DIV");
            var spacer1 = new Element("DIV");
            var spacer2 = new Element("DIV");
            var abs = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(positioned);
            positioned.AppendChild(spacer1);
            positioned.AppendChild(spacer2);
            spacer2.AppendChild(abs);

            var styles = NewStyles();
            styles[html] = Block(1280, 720);
            styles[body] = new CssComputed { Display = "block", Width = 1280, Height = 720, Padding = new Thickness(0, 10, 0, 0) };
            styles[positioned] = new CssComputed { Display = "block", Position = "relative", Margin = new Thickness(30, 0, 0, 0), Width = 500, Height = 400 };
            styles[spacer1] = Block(500, 40);
            styles[spacer2] = Block(500, 30);
            styles[abs] = new CssComputed { Display = "block", Position = "absolute", Left = 5, Top = 5, Width = 20, Height = 20 };

            var (_, result) = Layout(document, styles);

            var geometry = Rect(result, abs);
            // CB is the positioned ancestor's padding box at (30, 10); the two
            // static ancestors must be skipped.
            Assert.Equal(35f, geometry.X, 0.5f);
            Assert.Equal(15f, geometry.Y, 0.5f);
        }

        [Fact]
        [Trait("Category", "Layout")]
        public void Fixed_WithoutTransformedAncestor_UsesViewport()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var static1 = new Element("DIV");
            var static2 = new Element("DIV");
            var fixedEl = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(static1);
            static1.AppendChild(static2);
            static2.AppendChild(fixedEl);

            var styles = NewStyles();
            styles[html] = Block(1280, 720);
            styles[body] = new CssComputed { Display = "block", Width = 1280, Height = 720, Padding = new Thickness(0, 10, 0, 0) };
            styles[static1] = Block(400, 300);
            styles[static2] = Block(300, 200);
            styles[fixedEl] = new CssComputed { Display = "block", Position = "fixed", Left = 30, Top = 20, Width = 50, Height = 25 };

            var (_, result) = Layout(document, styles);

            var geometry = Rect(result, fixedEl);
            Assert.Equal(30f, geometry.X, 0.5f);
            Assert.Equal(20f, geometry.Y, 0.5f);
        }

        [Fact]
        [Trait("Category", "Layout")]
        public void Fixed_TransformedAncestor_IsContainingBlock()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var wrapper = new Element("DIV");
            var fixedEl = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(wrapper);
            wrapper.AppendChild(fixedEl);

            var styles = NewStyles();
            styles[html] = Block(1280, 720);
            styles[body] = new CssComputed { Display = "block", Width = 1280, Height = 720, Padding = new Thickness(0, 10, 0, 0) };
            styles[wrapper] = new CssComputed { Display = "block", Transform = "translate(20px, 10px)", Margin = new Thickness(100, 0, 0, 0), Width = 300, Height = 200 };
            styles[fixedEl] = new CssComputed { Display = "block", Position = "fixed", Left = 5, Top = 6, Width = 40, Height = 20 };

            var (_, result) = Layout(document, styles);

            var wrapperRect = Rect(result, wrapper);
            var geometry = Rect(result, fixedEl);
            Assert.Equal(wrapperRect.X + 5f, geometry.X, 0.5f);
            Assert.Equal(wrapperRect.Y + 6f, geometry.Y, 0.5f);
        }

        [Fact]
        [Trait("Category", "Layout")]
        public void Absolute_TransformedStaticAncestor_IsContainingBlock()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var wrapper = new Element("DIV");
            var spacer = new Element("DIV");
            var abs = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(wrapper);
            wrapper.AppendChild(spacer);
            spacer.AppendChild(abs);

            var styles = NewStyles();
            styles[html] = Block(1280, 720);
            styles[body] = new CssComputed { Display = "block", Width = 1280, Height = 720, Padding = new Thickness(0, 10, 0, 0) };
            styles[wrapper] = new CssComputed { Display = "block", Transform = "translate(20px, 10px)", Margin = new Thickness(100, 0, 0, 0), Width = 300, Height = 200 };
            styles[spacer] = Block(200, 50);
            styles[abs] = new CssComputed { Display = "block", Position = "absolute", Left = 5, Top = 6, Width = 40, Height = 20 };

            var (_, result) = Layout(document, styles);

            var wrapperRect = Rect(result, wrapper);
            var geometry = Rect(result, abs);
            // The transformed (but static) wrapper establishes the containing block.
            Assert.Equal(wrapperRect.X + 5f, geometry.X, 0.5f);
            Assert.Equal(wrapperRect.Y + 6f, geometry.Y, 0.5f);
        }

        [Fact]
        [Trait("Category", "Layout")]
        public void Absolute_InFlexContainer_ResolvesAgainstPositionedAncestor()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var outer = new Element("DIV");
            var spacer = new Element("DIV");
            var flexContainer = new Element("DIV");
            var flexItem = new Element("DIV");
            var abs = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(outer);
            outer.AppendChild(spacer);
            outer.AppendChild(flexContainer);
            flexContainer.AppendChild(flexItem);
            flexContainer.AppendChild(abs);

            var styles = NewStyles();
            styles[html] = Block(1280, 720);
            styles[body] = new CssComputed { Display = "block", Width = 1280, Height = 720, Padding = new Thickness(0, 10, 0, 0) };
            styles[outer] = new CssComputed { Display = "block", Position = "relative", Margin = new Thickness(100, 0, 0, 0), Width = 300, Height = 200 };
            styles[spacer] = Block(300, 20);
            styles[flexContainer] = new CssComputed { Display = "flex", Width = 300, Height = 100 };
            styles[flexItem] = Block(80, 40);
            styles[abs] = new CssComputed { Display = "block", Position = "absolute", Left = 10, Top = 10, Width = 30, Height = 15 };

            var (_, result) = Layout(document, styles);

            var geometry = Rect(result, abs);
            // CB is the positioned outer's padding box at (100, 10); the flex
            // container itself is static and sits at (100, 30).
            Assert.Equal(110f, geometry.X, 0.5f);
            Assert.Equal(20f, geometry.Y, 0.5f);
        }

        [Fact]
        [Trait("Category", "Layout")]
        public void Absolute_InGridContainer_ResolvesAgainstPositionedAncestor()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var outer = new Element("DIV");
            var spacer = new Element("DIV");
            var gridContainer = new Element("DIV");
            var gridItem = new Element("DIV");
            var abs = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(outer);
            outer.AppendChild(spacer);
            outer.AppendChild(gridContainer);
            gridContainer.AppendChild(gridItem);
            gridContainer.AppendChild(abs);

            var styles = NewStyles();
            styles[html] = Block(1280, 720);
            styles[body] = new CssComputed { Display = "block", Width = 1280, Height = 720, Padding = new Thickness(0, 10, 0, 0) };
            styles[outer] = new CssComputed { Display = "block", Position = "relative", Margin = new Thickness(100, 0, 0, 0), Width = 300, Height = 200 };
            styles[spacer] = Block(300, 20);
            styles[gridContainer] = new CssComputed { Display = "grid", Width = 300, Height = 100 };
            styles[gridItem] = Block(80, 40);
            styles[abs] = new CssComputed { Display = "block", Position = "absolute", Left = 10, Top = 10, Width = 30, Height = 15 };

            var (_, result) = Layout(document, styles);

            var geometry = Rect(result, abs);
            Assert.Equal(110f, geometry.X, 0.5f);
            Assert.Equal(20f, geometry.Y, 0.5f);
        }

        [Fact]
        [Trait("Category", "Layout")]
        public void Absolute_PercentageAndInsetSizes_UseContainingBlock()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var anchor = new Element("DIV");
            var spacer = new Element("DIV");
            var middle = new Element("DIV");
            var abs = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(anchor);
            anchor.AppendChild(spacer);
            anchor.AppendChild(middle);
            middle.AppendChild(abs);

            var styles = NewStyles();
            styles[html] = Block(1280, 720);
            styles[body] = new CssComputed { Display = "block", Width = 1280, Height = 720, Padding = new Thickness(0, 10, 0, 0) };
            styles[anchor] = new CssComputed { Display = "block", Position = "relative", Margin = new Thickness(100, 0, 0, 0), Width = 400, Height = 300 };
            styles[spacer] = Block(400, 50);
            styles[middle] = Block(200, 100);
            styles[abs] = new CssComputed
            {
                Display = "block",
                Position = "absolute",
                LeftPercent = 10,
                TopPercent = 20,
                WidthPercent = 50,
                HeightPercent = 25
            };

            var (_, result) = Layout(document, styles);

            var geometry = Rect(result, abs);
            // Percentages resolve against the anchor's padding box (400x300 at
            // (100,10)), not the 200x100 static intermediate.
            Assert.Equal(140f, geometry.X, 0.5f);
            Assert.Equal(70f, geometry.Y, 0.5f);
            Assert.Equal(200f, geometry.Width, 0.5f);
            Assert.Equal(75f, geometry.Height, 0.5f);
        }
    }
}
