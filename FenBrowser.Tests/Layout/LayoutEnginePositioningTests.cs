using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using Xunit;

namespace FenBrowser.Tests.Layout
{
    public class LayoutEnginePositioningTests
    {
        [Fact]
        public void FixedPosition_UsesViewportContainingBlock_WhenPositionComesFromComputedMap()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var spacer = new Element("DIV");
            var picture = new Element("DIV");
            var scalp = new Element("P");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(spacer);
            body.AppendChild(picture);
            picture.AppendChild(scalp);

            var styles = new Dictionary<Node, CssComputed>();

            styles[html] = new CssComputed { Display = "block", Width = 1920, Height = 927 };
            styles[body] = new CssComputed { Display = "block", Width = 1920, Height = 927 };
            styles[spacer] = new CssComputed { Display = "block", Height = 2000 };
            styles[picture] = new CssComputed
            {
                Display = "block",
                Position = "relative",
                Margin = new Thickness(48, 0, 0, 0)
            };

            var fixedFromMap = new CssComputed
            {
                Display = "block",
                Width = 48,
                Height = 16,
                Top = 144,
                Left = 176
            };
            fixedFromMap.Map["position"] = "fixed";
            styles[scalp] = fixedFromMap;

            var engine = new LayoutEngine(styles, 1920, 927);
            var result = engine.ComputeLayout(document, 0, 0, 1920, availableHeight: 927);

            Assert.NotNull(result);
            Assert.True(result.ElementRects.TryGetValue(scalp, out var scalpGeometry));
            Assert.Equal(176f, scalpGeometry.X, 0.5f);
            Assert.Equal(144f, scalpGeometry.Y, 0.5f);
        }

        [Fact]
        public void AbsolutePosition_UsesNearestPositionedAncestor_WhenAncestorPositionComesFromComputedMap()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var spacer = new Element("DIV");
            var hero = new Element("SECTION");
            var icons = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(spacer);
            body.AppendChild(hero);
            hero.AppendChild(icons);

            var styles = new Dictionary<Node, CssComputed>();

            styles[html] = new CssComputed { Display = "block", Width = 1280, Height = 720 };
            styles[body] = new CssComputed { Display = "block", Width = 1280, Height = 720 };
            styles[spacer] = new CssComputed { Display = "block", Height = 180 };

            var heroStyle = new CssComputed
            {
                Display = "block",
                Width = 1280,
                Height = 320
            };
            heroStyle.Map["position"] = "relative";
            styles[hero] = heroStyle;

            styles[icons] = new CssComputed
            {
                Display = "block",
                Position = "absolute",
                Width = 80,
                Height = 80,
                Left = 24,
                Top = 36
            };

            var engine = new LayoutEngine(styles, 1280, 720);
            var result = engine.ComputeLayout(document, 0, 0, 1280, availableHeight: 720);

            Assert.NotNull(result);
            Assert.True(result.ElementRects.TryGetValue(hero, out var heroGeometry));
            Assert.True(result.ElementRects.TryGetValue(icons, out var iconGeometry));
            Assert.Equal(heroGeometry.X + 24f, iconGeometry.X, 0.5f);
            Assert.Equal(heroGeometry.Y + 36f, iconGeometry.Y, 0.5f);
        }

        [Fact]
        public void AbsolutePosition_AndPercentSize_UseComputedMap_InLayoutEngine()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var hero = new Element("SECTION");
            var inlineMedia = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(hero);
            hero.AppendChild(inlineMedia);

            var styles = new Dictionary<Node, CssComputed>();

            styles[html] = new CssComputed { Display = "block", Width = 1280, Height = 720 };
            styles[body] = new CssComputed { Display = "block", Width = 1280, Height = 720 };
            styles[hero] = new CssComputed
            {
                Display = "block",
                Position = "relative",
                Width = 1280,
                Height = 320
            };

            var inlineMediaStyle = new CssComputed
            {
                Display = "block",
                Position = string.Empty
            };
            inlineMediaStyle.Map["position"] = "absolute";
            inlineMediaStyle.Map["width"] = "100%";
            inlineMediaStyle.Map["height"] = "100%";
            styles[inlineMedia] = inlineMediaStyle;

            var engine = new LayoutEngine(styles, 1280, 720);
            var result = engine.ComputeLayout(document, 0, 0, 1280, availableHeight: 720);

            Assert.NotNull(result);
            Assert.True(result.ElementRects.TryGetValue(hero, out var heroGeometry));
            Assert.True(result.ElementRects.TryGetValue(inlineMedia, out var inlineMediaGeometry));
            Assert.Equal(heroGeometry.X, inlineMediaGeometry.X, 0.5f);
            Assert.Equal(heroGeometry.Y, inlineMediaGeometry.Y, 0.5f);
            Assert.Equal(heroGeometry.Width, inlineMediaGeometry.Width, 0.5f);
            Assert.Equal(heroGeometry.Height, inlineMediaGeometry.Height, 0.5f);
        }

        [Fact]
        public void AbsolutePosition_UsesLogicalInlineStart_FromComputedMap_InLayoutEngine()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var hero = new Element("SECTION");
            var startFrame = new Element("IMG");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(hero);
            hero.AppendChild(startFrame);

            var styles = new Dictionary<Node, CssComputed>();

            styles[html] = new CssComputed { Display = "block", Width = 1280, Height = 720 };
            styles[body] = new CssComputed { Display = "block", Width = 1280, Height = 720 };
            styles[hero] = new CssComputed
            {
                Display = "block",
                Position = "relative",
                Width = 400,
                Height = 200
            };
            styles[startFrame] = new CssComputed
            {
                Display = "block",
                Position = "absolute",
                Width = 100,
                Height = 50,
                Top = 0
            };
            styles[startFrame].Map["inset-inline-start"] = "50%";

            var engine = new LayoutEngine(styles, 1280, 720);
            var result = engine.ComputeLayout(document, 0, 0, 1280, availableHeight: 720);

            Assert.NotNull(result);
            Assert.True(result.ElementRects.TryGetValue(hero, out var heroGeometry));
            Assert.True(result.ElementRects.TryGetValue(startFrame, out var startFrameGeometry));
            Assert.Equal(heroGeometry.X + 200f, startFrameGeometry.X, 0.5f);
            Assert.Equal(heroGeometry.Y, startFrameGeometry.Y, 0.5f);
        }

        [Fact]
        public void FixedPosition_RightInsetAutoWidth_ShrinksToChildContent()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var prompt = new Element("DIV");
            var label = new Element("SPAN");
            var qr = new Element("DIV");
            var qrInner = new Element("DIV");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(prompt);
            label.AppendChild(new Text("Scan to get the app"));
            prompt.AppendChild(label);
            prompt.AppendChild(qr);
            qr.AppendChild(qrInner);

            var styles = new Dictionary<Node, CssComputed>();
            styles[html] = new CssComputed { Display = "block", Width = 1920, Height = 899 };
            styles[body] = new CssComputed { Display = "block", Width = 1920, Height = 899 };
            styles[prompt] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "column",
                AlignItems = "center",
                Position = "fixed",
                Right = 58,
                Bottom = 58,
                Gap = 8,
                Padding = new Thickness(16)
            };
            styles[label] = new CssComputed
            {
                Display = "block",
                MaxWidthExpression = "clamp(80px,10vw,160px)",
                Height = 16
            };
            styles[label.ChildNodes[0]] = new CssComputed { Display = "inline" };
            styles[qr] = new CssComputed
            {
                Display = "block",
                WidthExpression = "clamp(80px,10vw,160px)",
                HeightExpression = "clamp(80px,10vw,160px)"
            };
            styles[qrInner] = new CssComputed
            {
                Display = "block",
                WidthPercent = 100,
                HeightPercent = 100
            };

            var engine = new LayoutEngine(styles, 1920, 899);
            var result = engine.ComputeLayout(document, 0, 0, 1920, availableHeight: 899);

            Assert.NotNull(result);
            Assert.True(result.ElementRects.TryGetValue(prompt, out var promptGeometry));
            Assert.True(result.ElementRects.TryGetValue(qr, out var qrGeometry));
            Assert.True(result.ElementRects.TryGetValue(qrInner, out var qrInnerGeometry));
            Assert.InRange(qrGeometry.Width, 159.5f, 160.5f);
            Assert.InRange(qrInnerGeometry.Height, 159.5f, 160.5f);
            Assert.True(promptGeometry.Width <= 192.5f, $"Expected fixed prompt border box to shrink around clamped child and padding; got {promptGeometry.Width}");
            Assert.True(promptGeometry.X > 1660f, $"Expected fixed prompt to stay anchored near the right edge; got x={promptGeometry.X}");
        }

        [Fact]
        public void FixedPosition_BottomInsetAutoHeight_UsesTextLineHeight()
        {
            var document = new Document();
            var html = new Element("HTML");
            var body = new Element("BODY");
            var toast = new Element("DIV");
            var message = new Text("Fallback toast notification shown inside the page.");

            document.AppendChild(html);
            html.AppendChild(body);
            body.AppendChild(toast);
            toast.AppendChild(message);

            var styles = new Dictionary<Node, CssComputed>
            {
                [html] = new CssComputed { Display = "block", Width = 1200, Height = 800 },
                [body] = new CssComputed { Display = "block", Width = 1200, Height = 800 },
                [toast] = new CssComputed
                {
                    Display = "block",
                    Position = "fixed",
                    Right = 18,
                    Bottom = 18,
                    Padding = new Thickness(14),
                    BorderThickness = new Thickness(3),
                    FontSize = 16,
                    LineHeight = 1.45
                },
                [message] = new CssComputed
                {
                    Display = "inline",
                    FontSize = 16,
                    LineHeight = 1.45
                }
            };

            var engine = new LayoutEngine(styles, 1200, 800);
            var result = engine.ComputeLayout(document, 0, 0, 1200, availableHeight: 800);

            Assert.NotNull(result);
            Assert.True(result.ElementRects.TryGetValue(toast, out var toastGeometry));
            Assert.InRange(toastGeometry.Height, 56.5f, 58.0f);
            Assert.Equal(800f - 18f, toastGeometry.Y + toastGeometry.Height, 0.5f);
        }

    }
}
