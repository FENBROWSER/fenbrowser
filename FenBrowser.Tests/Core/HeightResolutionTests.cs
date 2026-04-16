using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;
using FenBrowser.Core;
using System.Linq;

namespace FenBrowser.Tests.Core
{
    public class HeightResolutionTests
    {
        [Fact]
        public void Body_Height_AtLeastViewport()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();
            var html = new Element("html");
            var body = new Element("body");
            html.AppendChild(body);

            // Empty body
            styles[html] = new CssComputed { Display = "block" };
            styles[body] = new CssComputed { Display = "block" };

            float viewportHeight = 600;
            renderer.Render(html, new SKCanvas(new SKBitmap(800, (int)viewportHeight)), styles, new SKRect(0, 0, 800, viewportHeight), "http://example.com", (size, overlays) => {});

            renderer.LastLayout.TryGetElementRect(body, out var bodyRect);
            Assert.Equal(viewportHeight, bodyRect.Height);
            
            renderer.LastLayout.TryGetElementRect(html, out var htmlRect);
            // HTML should be auto (intrinsic), wrapping BODY. 
            // Default margin/padding on BODY might push it slightly beyond 600.
            // Logs show 616.
            Assert.True(htmlRect.Height >= viewportHeight);
        }

        [Fact]
        public void Body_Height_ExpandsWithContent()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();
            var html = new Element("html");
            var body = new Element("body");
            var tallChild = new Element("div");
            html.AppendChild(body);
            body.AppendChild(tallChild);

            styles[html] = new CssComputed { Display = "block" };
            styles[body] = new CssComputed { Display = "block" };
            styles[tallChild] = new CssComputed { Display = "block", Height = 1000 };

            float viewportHeight = 600;
            renderer.Render(html, new SKCanvas(new SKBitmap(800, (int)viewportHeight)), styles, new SKRect(0, 0, 800, viewportHeight), "http://example.com", (size, overlays) => {});

            renderer.LastLayout.TryGetElementRect(body, out var bodyRect);
            Assert.Equal(1000, bodyRect.Height);
        }

        [Fact]
        public void FlexColumn_Height_IsIntrinsic()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();
            var root = new Element("div");
            var child1 = new Element("div");
            var child2 = new Element("div");
            root.AppendChild(child1);
            root.AppendChild(child2);

            styles[root] = new CssComputed { Display = "flex", FlexDirection = "column" };
            styles[child1] = new CssComputed { Display = "block", Height = 100 };
            styles[child2] = new CssComputed { Display = "block", Height = 150 };

            float viewportHeight = 600;
            renderer.Render(root, new SKCanvas(new SKBitmap(800, (int)viewportHeight)), styles, new SKRect(0, 0, 800, viewportHeight), "http://example.com", (size, overlays) => {});

            renderer.LastLayout.TryGetElementRect(root, out var rootRect);
            // Height should be 100 + 150 = 250, NOT clamped to viewport (600) or inherited.
            Assert.Equal(250, rootRect.Height);
        }

        [Fact]
        public void ScrollHeight_CanExceed10xViewport()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();
            var html = new Element("html");
            var body = new Element("body");
            var giant = new Element("div");
            html.AppendChild(body);
            body.AppendChild(giant);

            styles[html] = new CssComputed { Display = "block" };
            styles[body] = new CssComputed { Display = "block" };
            // 20x viewport height
            styles[giant] = new CssComputed { Display = "block", Height = 12000 };

            float viewportHeight = 600;
            float totalHeightResult = 0;
            renderer.Render(html, new SKCanvas(new SKBitmap(800, (int)viewportHeight)), styles, new SKRect(0, 0, 800, viewportHeight), "http://example.com", (size, overlays) => {
                totalHeightResult = size.Height;
            });

            // Should be at least 12000, not clamped to 6000 (10x 600)
            Assert.True(totalHeightResult >= 12000);
        }

        [Fact]
        public void NestedFlexGrowAndFixedInsetFillViewport()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var html = new Element("html");
            var body = new Element("body");
            var reactRoot = new Element("div");
            var appShell = new Element("div");
            var innerShell = new Element("div");
            var overlay = new Element("div");

            html.AppendChild(body);
            body.AppendChild(reactRoot);
            reactRoot.AppendChild(appShell);
            appShell.AppendChild(innerShell);
            innerShell.AppendChild(overlay);

            styles[html] = new CssComputed { Display = "block", HeightPercent = 100 };
            styles[body] = new CssComputed { Display = "block", HeightPercent = 100 };
            styles[reactRoot] = new CssComputed { Display = "flex", FlexDirection = "column", HeightPercent = 100 };
            styles[appShell] = new CssComputed { Display = "flex", FlexDirection = "column", FlexGrow = 1, FlexShrink = 1, FlexBasis = 0 };
            styles[innerShell] = new CssComputed { Display = "flex", FlexDirection = "column", FlexGrow = 1, FlexShrink = 1, FlexBasis = 0 };
            styles[overlay] = new CssComputed
            {
                Display = "flex",
                Position = "fixed",
                Left = 0,
                Top = 0,
                Right = 0,
                Bottom = 0
            };

            float viewportHeight = 600;
            float viewportWidth = 800;

            renderer.Render(
                html,
                new SKCanvas(new SKBitmap((int)viewportWidth, (int)viewportHeight)),
                styles,
                new SKRect(0, 0, viewportWidth, viewportHeight),
                "http://example.com",
                (size, overlaysOut) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(appShell, out var appShellRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(innerShell, out var innerShellRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(overlay, out var overlayRect));

            Assert.Equal(viewportHeight, appShellRect.Height);
            Assert.Equal(viewportHeight, innerShellRect.Height);
            Assert.Equal(viewportWidth, overlayRect.Width);
            Assert.Equal(viewportHeight, overlayRect.Height);
        }

        [Fact]
        public void FlexAutoHeightItem_DoesNotResolveChildHeightPercentAgainstViewport()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var root = new Element("div");
            var headerStart = new Element("div");
            var logoLink = new Element("a");
            var logoImage = new Element("img");

            root.AppendChild(headerStart);
            headerStart.AppendChild(logoLink);
            logoLink.AppendChild(logoImage);

            styles[root] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                AlignItems = "center",
                Width = 500,
                Height = 50
            };
            styles[headerStart] = new CssComputed
            {
                Display = "flex",
                AlignItems = "center"
            };
            styles[logoLink] = new CssComputed
            {
                Display = "flex",
                AlignItems = "center",
                HeightPercent = 100
            };
            styles[logoImage] = new CssComputed
            {
                Display = "inline-block",
                Width = 140,
                Height = 22
            };

            float viewportHeight = 600;
            renderer.Render(
                root,
                new SKCanvas(new SKBitmap(800, (int)viewportHeight)),
                styles,
                new SKRect(0, 0, 800, viewportHeight),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(headerStart, out var headerStartRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(logoLink, out var logoLinkRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(logoImage, out var logoImageRect));

            Assert.InRange(logoImageRect.Height, 20, 24);
            Assert.InRange(logoLinkRect.Height, 20, 30);
            Assert.InRange(headerStartRect.Height, 20, 30);
            Assert.True(logoLinkRect.Height < viewportHeight / 2);
        }

        [Fact]
        public void FlexAutoWidthColumnItem_ExpandsToStackedImagesSeparatedByWhitespace()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var root = new Element("div");
            var headerStart = new Element("div");
            var logoLink = new Element("a");
            var wordmark = new Element("img");
            var tagline = new Element("img");

            root.AppendChild(headerStart);
            headerStart.AppendChild(logoLink);
            logoLink.AppendChild(new Text(" "));
            logoLink.AppendChild(wordmark);
            logoLink.AppendChild(new Text(" "));
            logoLink.AppendChild(tagline);
            logoLink.AppendChild(new Text(" "));

            styles[root] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                AlignItems = "center",
                Width = 500,
                Height = 50
            };
            styles[headerStart] = new CssComputed
            {
                Display = "flex",
                AlignItems = "center"
            };
            styles[logoLink] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "column",
                AlignItems = "flex-start",
                HeightPercent = 100
            };
            styles[wordmark] = new CssComputed
            {
                Display = "inline-block",
                Width = 140,
                Height = 22
            };
            styles[tagline] = new CssComputed
            {
                Display = "inline-block",
                Width = 140,
                Height = 11
            };

            float viewportHeight = 600;
            renderer.Render(
                root,
                new SKCanvas(new SKBitmap(800, (int)viewportHeight)),
                styles,
                new SKRect(0, 0, 800, viewportHeight),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(logoLink, out var logoLinkRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(wordmark, out var wordmarkRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(tagline, out var taglineRect));

            Assert.True(logoLinkRect.Width >= 140f, $"Expected stacked logo link width to follow child images, got {logoLinkRect.Width}.");
            Assert.Equal(140f, wordmarkRect.Width, 1);
            Assert.Equal(140f, taglineRect.Width, 1);
        }

        [Fact]
        public void FlexAutoWidthRowItem_ExpandsToDescendantImagesInsideCollapsedSpan()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var root = new Element("div");
            var headerStart = new Element("div");
            var logoLink = new Element("a");
            var logoContainer = new Element("span");
            var wordmark = new Element("img");
            var tagline = new Element("img");

            root.AppendChild(headerStart);
            headerStart.AppendChild(logoLink);
            logoLink.AppendChild(logoContainer);
            logoContainer.AppendChild(new Text(" "));
            logoContainer.AppendChild(wordmark);
            logoContainer.AppendChild(new Text(" "));
            logoContainer.AppendChild(tagline);
            logoContainer.AppendChild(new Text(" "));

            styles[root] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                AlignItems = "center",
                Width = 500,
                Height = 50
            };
            styles[headerStart] = new CssComputed
            {
                Display = "flex",
                AlignItems = "center"
            };
            styles[logoLink] = new CssComputed
            {
                Display = "flex",
                AlignItems = "center",
                Height = 38
            };
            styles[wordmark] = new CssComputed
            {
                Display = "inline-block",
                Width = 140,
                Height = 22
            };
            styles[tagline] = new CssComputed
            {
                Display = "inline-block",
                Width = 140,
                Height = 11
            };

            float viewportHeight = 600;
            renderer.Render(
                root,
                new SKCanvas(new SKBitmap(800, (int)viewportHeight)),
                styles,
                new SKRect(0, 0, 800, viewportHeight),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(logoLink, out var logoLinkRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(logoContainer, out var logoContainerRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(wordmark, out var wordmarkRect));

            Assert.True(logoLinkRect.Width >= 140f, $"Expected flex row logo link width to follow descendant images, got {logoLinkRect.Width}.");
            Assert.True(logoContainerRect.Width >= 140f, $"Expected inner span width to recover from descendant images, got {logoContainerRect.Width}.");
            Assert.Equal(140f, wordmarkRect.Width, 1);
        }

        [Fact]
        public void FlexColumnWrapper_PreservesLargeInlineSvgIntrinsicSize()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var root = new Element("div");
            var logoBand = new Element("div");
            var logoWrapper = new Element("div");
            var logoSvg = new Element("svg");

            root.AppendChild(logoBand);
            logoBand.AppendChild(logoWrapper);
            logoWrapper.AppendChild(logoSvg);

            logoSvg.SetAttribute("width", "272");
            logoSvg.SetAttribute("height", "92");
            logoSvg.SetAttribute("viewBox", "0 0 272 92");

            styles[root] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "column",
                AlignItems = "center",
                Width = 800,
                Height = 92
            };
            styles[logoBand] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "column",
                AlignItems = "center",
                HeightPercent = 100,
                MinHeight = 92,
                MaxHeight = 92
            };
            styles[logoWrapper] = new CssComputed
            {
                Display = "block",
                Position = "relative",
                HeightPercent = 100
            };
            styles[logoSvg] = new CssComputed
            {
                Display = "inline-block",
                MaxWidthPercent = 100,
                MaxHeightPercent = 100,
                ObjectFit = "contain",
                ObjectPosition = "center bottom"
            };

            float viewportHeight = 600;
            renderer.Render(
                root,
                new SKCanvas(new SKBitmap(800, (int)viewportHeight)),
                styles,
                new SKRect(0, 0, 800, viewportHeight),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(logoBand, out var logoBandRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(logoWrapper, out var logoWrapperRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(logoSvg, out var logoSvgRect));

            Assert.InRange(logoBandRect.Height, 90f, 92f);
            Assert.InRange(logoWrapperRect.Height, 90f, 92f);
            Assert.True(logoSvgRect.Width >= 240f, $"Expected Google-style SVG width near intrinsic size, got {logoSvgRect.Width}.");
            Assert.True(logoSvgRect.Height >= 80f, $"Expected Google-style SVG height near intrinsic size, got {logoSvgRect.Height}.");
        }

        [Fact]
        public void FlexColumnDirectSvgWrapper_DoesNotFallbackTo24pxIconSize()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var root = new Element("div");
            var logoWrapper = new Element("div");
            var logoSvg = new Element("svg");

            root.AppendChild(logoWrapper);
            logoWrapper.AppendChild(logoSvg);

            logoSvg.SetAttribute("width", "272");
            logoSvg.SetAttribute("height", "92");
            logoSvg.SetAttribute("viewBox", "0 0 272 92");

            styles[root] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "column",
                AlignItems = "center",
                Width = 800,
                Height = 92
            };
            styles[logoWrapper] = new CssComputed
            {
                Display = "block",
                Position = "relative",
                HeightPercent = 100,
                MaxHeight = 92
            };
            styles[logoSvg] = new CssComputed
            {
                Display = "inline-block",
                MaxWidthPercent = 100,
                MaxHeightPercent = 100,
                ObjectFit = "contain",
                ObjectPosition = "center bottom"
            };

            float viewportHeight = 600;
            renderer.Render(
                root,
                new SKCanvas(new SKBitmap(800, (int)viewportHeight)),
                styles,
                new SKRect(0, 0, 800, viewportHeight),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(logoWrapper, out var logoWrapperRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(logoSvg, out var logoSvgRect));

            Assert.True(logoWrapperRect.Width >= 240f, $"Expected direct flex-item wrapper width to follow large SVG, got {logoWrapperRect.Width}.");
            Assert.True(logoSvgRect.Width >= 240f, $"Expected direct flex-item SVG width near intrinsic size, got {logoSvgRect.Width}.");
            Assert.True(logoSvgRect.Height >= 80f, $"Expected direct flex-item SVG height near intrinsic size, got {logoSvgRect.Height}.");
        }

        [Fact]
        public void GoogleHeroBand_PreservesWordmarkSizeThroughCalcAndMaxHeight()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var root = new Element("div");
            var topNav = new Element("div");
            var heroBand = new Element("div");
            var logoWrapper = new Element("div");
            var logoSvg = new Element("svg");

            root.AppendChild(topNav);
            root.AppendChild(heroBand);
            heroBand.AppendChild(logoWrapper);
            logoWrapper.AppendChild(logoSvg);

            logoSvg.SetAttribute("width", "272");
            logoSvg.SetAttribute("height", "92");
            logoSvg.SetAttribute("viewBox", "0 0 272 92");

            styles[root] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "column",
                Width = 800,
                Height = 768
            };
            styles[topNav] = new CssComputed
            {
                Display = "block",
                Height = 60
            };
            styles[heroBand] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "column",
                AlignItems = "center",
                HeightExpression = "calc(100% - 560px)",
                MinHeight = 150,
                MaxHeight = 290
            };
            styles[logoWrapper] = new CssComputed
            {
                Display = "block",
                Position = "relative",
                HeightPercent = 100,
                MaxHeight = 92,
                MarginTopAuto = true
            };
            styles[logoSvg] = new CssComputed
            {
                Display = "inline-block",
                MaxWidthPercent = 100,
                MaxHeightPercent = 100,
                ObjectFit = "contain",
                ObjectPosition = "center bottom"
            };

            float viewportHeight = 768;
            renderer.Render(
                root,
                new SKCanvas(new SKBitmap(800, (int)viewportHeight)),
                styles,
                new SKRect(0, 0, 800, viewportHeight),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(heroBand, out var heroBandRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(logoWrapper, out var logoWrapperRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(logoSvg, out var logoSvgRect));

            Assert.InRange(heroBandRect.Height, 180f, 220f);
            Assert.InRange(logoWrapperRect.Height, 90f, 92f);
            Assert.True(logoWrapperRect.Width >= 240f, $"Expected Google hero logo wrapper width near wordmark width, got {logoWrapperRect.Width}.");
            Assert.True(logoSvgRect.Width >= 240f, $"Expected Google hero SVG width near intrinsic size, got {logoSvgRect.Width}.");
            Assert.True(logoSvgRect.Height >= 80f, $"Expected Google hero SVG height near intrinsic size, got {logoSvgRect.Height}.");
        }

        [Fact]
        public void InlineSvgWithPathChildren_RemainsAtomicAtIntrinsicSize()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var root = new Element("div");
            var heroBand = new Element("div");
            var logoWrapper = new Element("div");
            var logoSvg = new Element("svg");

            root.AppendChild(heroBand);
            heroBand.AppendChild(logoWrapper);
            logoWrapper.AppendChild(logoSvg);

            logoSvg.SetAttribute("width", "272");
            logoSvg.SetAttribute("height", "92");
            logoSvg.SetAttribute("viewBox", "0 0 272 92");

            for (int i = 0; i < 6; i++)
            {
                logoSvg.AppendChild(new Element("path"));
            }

            styles[root] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "column",
                Width = 800,
                Height = 768
            };
            styles[heroBand] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "column",
                AlignItems = "center",
                HeightExpression = "calc(100% - 560px)",
                MinHeight = 150,
                MaxHeight = 290
            };
            styles[logoWrapper] = new CssComputed
            {
                Display = "block",
                Position = "relative",
                HeightPercent = 100,
                MaxHeight = 92,
                MarginTopAuto = true
            };
            styles[logoSvg] = new CssComputed
            {
                MaxWidthPercent = 100,
                MaxHeightPercent = 100,
                ObjectFit = "contain",
                ObjectPosition = "center bottom"
            };

            float viewportHeight = 768;
            renderer.Render(
                root,
                new SKCanvas(new SKBitmap(800, (int)viewportHeight)),
                styles,
                new SKRect(0, 0, 800, viewportHeight),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(logoWrapper, out var logoWrapperRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(logoSvg, out var logoSvgRect));

            Assert.InRange(logoWrapperRect.Height, 90f, 92f);
            Assert.True(logoWrapperRect.Width >= 240f, $"Expected inline SVG wrapper width near wordmark width, got {logoWrapperRect.Width}.");
            Assert.True(logoSvgRect.Width >= 240f, $"Expected inline SVG width near intrinsic size, got {logoSvgRect.Width}.");
            Assert.True(logoSvgRect.Height >= 80f, $"Expected inline SVG height near intrinsic size, got {logoSvgRect.Height}.");
        }

        [Fact]
        public void InlineButton_WithNestedFlexContent_LaysOutDescendants()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var root = new Element("div");
            var button = new Element("button");
            var glow = new Element("div");
            var fill = new Element("div");
            var background = new Element("div");
            var content = new Element("div");
            var icon = new Element("span");
            var iconSvg = new Element("svg");
            var label = new Element("span");
            var trailing = new Element("span");
            var trailingSvg = new Element("svg");

            root.AppendChild(button);
            button.AppendChild(glow);
            button.AppendChild(fill);
            button.AppendChild(background);
            button.AppendChild(content);
            content.AppendChild(icon);
            icon.AppendChild(iconSvg);
            content.AppendChild(label);
            label.AppendChild(new Text("AI Mode"));
            content.AppendChild(trailing);
            trailing.AppendChild(trailingSvg);

            iconSvg.SetAttribute("width", "24");
            iconSvg.SetAttribute("height", "24");
            iconSvg.SetAttribute("viewBox", "0 0 24 24");
            trailingSvg.SetAttribute("width", "24");
            trailingSvg.SetAttribute("height", "24");
            trailingSvg.SetAttribute("viewBox", "0 0 24 24");

            styles[root] = new CssComputed
            {
                Display = "block",
                Width = 300
            };
            styles[button] = new CssComputed
            {
                Display = "inline-block",
                Height = 36,
                Padding = new Thickness(0, 0, 0, 0),
                Margin = new Thickness(0),
                BorderThickness = new Thickness(0)
            };
            styles[glow] = new CssComputed
            {
                Display = "block",
                Position = "absolute",
                Left = 0,
                Top = 0,
                Right = 0,
                Bottom = 0
            };
            styles[fill] = new CssComputed
            {
                Display = "block",
                Position = "absolute",
                Left = 0,
                Top = 0,
                Right = 0,
                Bottom = 0
            };
            styles[background] = new CssComputed
            {
                Display = "block",
                Position = "absolute",
                Left = 2,
                Top = 2,
                Right = 2,
                Bottom = 2
            };
            styles[content] = new CssComputed
            {
                Display = "flex",
                AlignItems = "center",
                JustifyContent = "center"
            };
            styles[icon] = new CssComputed
            {
                Display = "inline-flex",
                AlignItems = "center"
            };
            styles[label] = new CssComputed
            {
                Display = "inline-block",
                Padding = new Thickness(4, 0, 4, 0)
            };
            styles[trailing] = new CssComputed
            {
                Display = "inline-flex",
                AlignItems = "center"
            };
            styles[iconSvg] = new CssComputed
            {
                Display = "inline-block"
            };
            styles[trailingSvg] = new CssComputed
            {
                Display = "inline-block"
            };

            renderer.Render(
                root,
                new SKCanvas(new SKBitmap(800, 600)),
                styles,
                new SKRect(0, 0, 800, 600),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(button, out var buttonRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(content, out var contentRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(label, out var labelRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(iconSvg, out var iconRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(trailingSvg, out var trailingRect));

            Assert.True(buttonRect.Width >= 60f, $"Expected button width to include content, got {buttonRect.Width}.");
            Assert.True(contentRect.Width > 0f, "Expected nested flex content wrapper to receive layout width.");
            Assert.True(labelRect.Width > 30f, $"Expected button label to receive text width, got {labelRect.Width}.");
            Assert.True(iconRect.Width >= 20f, $"Expected leading icon SVG to receive width, got {iconRect.Width}.");
            Assert.True(trailingRect.Width >= 20f, $"Expected trailing icon SVG to receive width, got {trailingRect.Width}.");
            Assert.True(contentRect.Right <= buttonRect.Right + 1f, "Expected content wrapper to stay within the button bounds.");
        }

        [Fact]
        public void InlineBlockAnchor_WithNestedSpan_HonorsMinWidth()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var root = new Element("div");
            var anchor = new Element("a");
            var label = new Element("span");

            root.AppendChild(anchor);
            anchor.AppendChild(label);
            label.AppendChild(new Text("Sign in"));

            styles[root] = new CssComputed
            {
                Display = "block",
                Width = 300
            };
            styles[anchor] = new CssComputed
            {
                Display = "inline-block",
                MinWidth = 85,
                MinHeight = 40,
                Padding = new Thickness(12, 10, 12, 10),
                LineHeight = 18
            };
            styles[label] = new CssComputed
            {
                Display = "inline",
                MaxWidthPercent = 100,
                MaxHeight = 40,
                Overflow = "hidden",
                OverflowWrap = "break-word",
                WordBreak = "break-word"
            };

            renderer.Render(
                root,
                new SKCanvas(new SKBitmap(800, 600)),
                styles,
                new SKRect(0, 0, 800, 600),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(anchor, out var anchorRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(label, out var labelRect));

            Assert.True(anchorRect.Width >= 85f, $"Expected anchor min-width to be honored, got {anchorRect.Width}.");
            Assert.True(anchorRect.Height >= 40f, $"Expected anchor min-height to be honored, got {anchorRect.Height}.");
            Assert.True(labelRect.Width > 30f, $"Expected nested label width to include visible text, got {labelRect.Width}.");
        }

        [Fact]
        public void FlexRowSignInAnchor_RelayoutKeepsNestedLabelInsidePill()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var root = new Element("div");
            var spacer = new Element("div");
            var anchor = new Element("a");
            var label = new Element("span");

            root.AppendChild(spacer);
            root.AppendChild(anchor);
            anchor.AppendChild(label);
            label.AppendChild(new Text("Sign in"));

            styles[root] = new CssComputed
            {
                Display = "flex",
                FlexDirection = "row",
                JustifyContent = "flex-end",
                AlignItems = "center",
                Width = 400,
                Height = 56
            };
            styles[spacer] = new CssComputed
            {
                Display = "block",
                Width = 240,
                Height = 1
            };
            styles[anchor] = new CssComputed
            {
                Display = "inline-block",
                MinWidth = 85,
                MinHeight = 40,
                Padding = new Thickness(12, 10, 12, 10),
                LineHeight = 18,
                TextAlign = SKTextAlign.Center
            };
            styles[label] = new CssComputed
            {
                Display = "inline",
                MaxWidthPercent = 100,
                MaxHeight = 40,
                Overflow = "hidden",
                OverflowWrap = "break-word",
                WordBreak = "break-word"
            };

            renderer.Render(
                root,
                new SKCanvas(new SKBitmap(800, 600)),
                styles,
                new SKRect(0, 0, 800, 600),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(anchor, out var anchorRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(label, out var labelRect));

            Assert.True(anchorRect.Width >= 85f, $"Expected sign-in anchor width to stay at least 85px, got {anchorRect.Width}.");
            Assert.True(labelRect.Left >= anchorRect.Left - 1f, $"Expected label left edge to stay inside the anchor, got label={labelRect.Left} anchor={anchorRect.Left}.");
            Assert.True(labelRect.Right <= anchorRect.Right + 1f, $"Expected label right edge to stay inside the anchor, got label={labelRect.Right} anchor={anchorRect.Right}.");
        }

        [Fact]
        public void InlineHeaderControls_WithVerticalAlignMiddle_ShareCenterline()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var root = new Element("div");
            var voiceButton = new Element("span");
            var spacer = new Text(" ");
            var launcher = new Element("span");
            var spacer2 = new Text(" ");
            var signIn = new Element("a");
            var signInLabel = new Element("span");

            root.AppendChild(voiceButton);
            root.AppendChild(spacer);
            root.AppendChild(launcher);
            root.AppendChild(spacer2);
            root.AppendChild(signIn);
            signIn.AppendChild(signInLabel);
            signInLabel.AppendChild(new Text("Sign in"));

            styles[root] = new CssComputed
            {
                Display = "block",
                Width = 320,
                LineHeight = 20
            };
            styles[voiceButton] = new CssComputed
            {
                Display = "inline-block",
                Width = 24,
                Height = 24,
                VerticalAlign = "middle"
            };
            styles[launcher] = new CssComputed
            {
                Display = "inline-block",
                Width = 20,
                Height = 20,
                VerticalAlign = "middle"
            };
            styles[signIn] = new CssComputed
            {
                Display = "inline-block",
                MinWidth = 85,
                MinHeight = 40,
                Padding = new Thickness(12, 10, 12, 10),
                LineHeight = 18,
                TextAlign = SKTextAlign.Center,
                VerticalAlign = "middle"
            };
            styles[signInLabel] = new CssComputed
            {
                Display = "inline",
                VerticalAlign = "middle"
            };

            renderer.Render(
                root,
                new SKCanvas(new SKBitmap(800, 600)),
                styles,
                new SKRect(0, 0, 800, 600),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(voiceButton, out var voiceRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(launcher, out var launcherRect));
            Assert.True(renderer.LastLayout.TryGetElementRect(signIn, out var signInRect));

            float voiceCenter = (voiceRect.Top + voiceRect.Bottom) / 2f;
            float launcherCenter = (launcherRect.Top + launcherRect.Bottom) / 2f;
            float signInCenter = (signInRect.Top + signInRect.Bottom) / 2f;

            Assert.True(Math.Abs(voiceCenter - launcherCenter) <= 2f,
                $"Expected mixed inline icons to share a centerline, got voice={voiceCenter} launcher={launcherCenter}.");
            Assert.True(Math.Abs(launcherCenter - signInCenter) <= 4f,
                $"Expected launcher and sign-in pill to share a centerline, got launcher={launcherCenter} signIn={signInCenter}.");
        }

        [Fact]
        public void BlockChild_PercentageHeight_DoesNotResolveAgainstAutoHeightContainingBlock()
        {
            var renderer = new SkiaDomRenderer();
            var styles = new Dictionary<Node, CssComputed>();

            var picture = new Element("div");
            var nose = new Element("div");
            var noseInner = new Element("div");
            var noseCore = new Element("div");

            picture.AppendChild(nose);
            nose.AppendChild(noseInner);
            noseInner.AppendChild(noseCore);

            styles[picture] = new CssComputed
            {
                Display = "block",
                Width = 600
            };
            styles[nose] = new CssComputed
            {
                Display = "block",
                Float = "left",
                Width = 192,
                HeightPercent = 60,
                MinHeightPercent = 80,
                MaxHeight = 48,
                BorderThickness = new Thickness(16, 0, 16, 16)
            };
            styles[noseInner] = new CssComputed
            {
                Display = "block",
                Height = 0,
                Padding = new Thickness(16, 16, 16, 48),
                BackgroundColor = SKColors.Yellow
            };
            styles[noseCore] = new CssComputed
            {
                Display = "block",
                Width = 32,
                Height = 32,
                BackgroundColor = SKColors.Red,
                MarginLeftAuto = true,
                MarginRightAuto = true
            };

            renderer.Render(
                picture,
                new SKCanvas(new SKBitmap(800, 600)),
                styles,
                new SKRect(0, 0, 800, 600),
                "http://example.com",
                (size, overlays) => { });

            Assert.True(renderer.LastLayout.TryGetElementRect(nose, out var noseRect));
            Assert.InRange(noseRect.Height, 40f, 80f);
        }
    }
}
