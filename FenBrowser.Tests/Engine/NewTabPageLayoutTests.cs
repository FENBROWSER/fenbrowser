using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class NewTabPageLayoutTests
    {
        [Fact]
        public async Task Layout_Centers_NewTabShell_And_Keeps_Search_Surface_Usable()
        {
            const float viewportWidth = 1600f;
            const float viewportHeight = 900f;

            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth, viewportHeight);
            var body = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "BODY", StringComparison.OrdinalIgnoreCase));

            var computer = new MinimalLayoutComputer(styles, viewportWidth, viewportHeight, baseUri.AbsoluteUri);
            computer.Measure(body, new SKSize(viewportWidth, viewportHeight));
            computer.Arrange(body, new SKRect(0, 0, viewportWidth, viewportHeight));

            var shell = FirstByClass(doc, "shell");
            var searchPanel = FirstByClass(doc, "search-panel");
            var searchBox = ById(doc, "url-bar");
            var quickLinks = FirstByClass(doc, "quick-links");

            var shellBox = AssertBox(computer, shell);
            var searchPanelBox = AssertBox(computer, searchPanel);
            var searchBoxBox = AssertBox(computer, searchBox);
            var quickLinksBox = AssertBox(computer, quickLinks);

            float viewportCenter = viewportWidth / 2f;
            float shellCenter = shellBox.ContentBox.Left + (shellBox.ContentBox.Width / 2f);
            float searchCenter = searchPanelBox.ContentBox.Left + (searchPanelBox.ContentBox.Width / 2f);

            Assert.InRange(shellCenter, viewportCenter - 24f, viewportCenter + 24f);
            Assert.InRange(searchCenter, viewportCenter - 24f, viewportCenter + 24f);
            Assert.True(shellBox.ContentBox.Width >= 680f, $"Expected a wide hero shell, got {shellBox.ContentBox}.");
            Assert.True(searchPanelBox.ContentBox.Width >= 540f, $"Expected a full search panel, got {searchPanelBox.ContentBox}.");
            Assert.True(searchBoxBox.ContentBox.Width >= 480f, $"Expected a usable search input width, got {searchBoxBox.ContentBox}.");
            Assert.True(searchPanelBox.ContentBox.Top >= shellBox.ContentBox.Top + 120f, $"Expected search panel below header copy, got shell={shellBox.ContentBox} panel={searchPanelBox.ContentBox}.");
            Assert.True(quickLinksBox.ContentBox.Top >= searchPanelBox.ContentBox.Bottom - 1f, $"Expected quick links below search panel, got panel={searchPanelBox.ContentBox} quickLinks={quickLinksBox.ContentBox}.");
        }

        [Fact]
        public async Task LayoutEngine_Does_Not_DoubleShift_NewTab_Block_Boxes()
        {
            const float viewportWidth = 1600f;
            const float viewportHeight = 900f;

            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth, viewportHeight);

            var shell = FirstByClass(doc, "shell");
            var searchPanel = FirstByClass(doc, "search-panel");
            var quickLinks = FirstByClass(doc, "quick-links");
            var searchBox = ById(doc, "url-bar");

            var engine = new LayoutEngine(styles, viewportWidth, viewportHeight, null, baseUri.AbsoluteUri);
            engine.ComputeLayout(root, viewportWidth, viewportHeight);

            var boxes = engine.AllBoxes;
            var shellBox = AssertBox(boxes, shell);
            var searchPanelBox = AssertBox(boxes, searchPanel);
            var quickLinksBox = AssertBox(boxes, quickLinks);
            var searchBoxBox = AssertBox(boxes, searchBox);

            Assert.InRange(searchPanelBox.ContentBox.Left, shellBox.ContentBox.Left - 1f, shellBox.ContentBox.Right);
            Assert.InRange(quickLinksBox.ContentBox.Left, shellBox.ContentBox.Left - 1f, shellBox.ContentBox.Right);
            Assert.True(searchPanelBox.ContentBox.Right <= viewportWidth, $"Search panel should stay inside the viewport, got {searchPanelBox.ContentBox}.");
            Assert.True(quickLinksBox.ContentBox.Right <= viewportWidth, $"Quick links should stay inside the viewport, got {quickLinksBox.ContentBox}.");
            Assert.True(Math.Abs(searchPanelBox.ContentBox.Left - searchBoxBox.ContentBox.Left) < 80f, $"Search panel and visible search surface drifted apart: panel={searchPanelBox.ContentBox} search={searchBoxBox.ContentBox}.");
            Assert.True(searchBoxBox.ContentBox.Width >= 480f, $"Search input should preserve its authored width instead of collapsing to intrinsic fallback, got {searchBoxBox.ContentBox}.");
            Assert.True(searchBoxBox.ContentBox.Top >= searchPanelBox.ContentBox.Top - 1f, $"Search surface should remain inside the panel, got panel={searchPanelBox.ContentBox} search={searchBoxBox.ContentBox}.");
            Assert.True(quickLinksBox.ContentBox.Top >= searchPanelBox.ContentBox.Bottom - 1f, $"Quick links should remain below the search region, got panel={searchPanelBox.ContentBox} quickLinks={quickLinksBox.ContentBox}.");
        }

        [Fact]
        public async Task Cascade_Preserves_NewTab_SearchBox_Background_From_Author_Shorthand()
        {
            const float viewportWidth = 1600f;
            const float viewportHeight = 900f;

            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth, viewportHeight);
            var searchBox = ById(doc, "url-bar");

            Assert.True(styles.TryGetValue(searchBox, out var style));
            Assert.NotNull(style);
            Assert.True(style.BackgroundColor.HasValue, $"Expected computed background color on #url-bar, got BackgroundImage='{style.BackgroundImage}' and map keys '{string.Join(",", style.Map.Keys)}'.");
            var bg = style.BackgroundColor.Value;
            Assert.Equal((byte)15, bg.Red);
            Assert.Equal((byte)23, bg.Green);
            Assert.Equal((byte)42, bg.Blue);
            Assert.Equal((byte)224, bg.Alpha);
        }

        [Fact]
        public async Task PaintTree_Preserves_NewTab_SearchBox_Background_Color()
        {
            const float viewportWidth = 1600f;
            const float viewportHeight = 900f;

            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var body = doc.Descendants().OfType<Element>().First(e => string.Equals(e.TagName, "BODY", StringComparison.OrdinalIgnoreCase));
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth, viewportHeight);
            var searchBox = ById(doc, "url-bar");

            var computer = new MinimalLayoutComputer(styles, viewportWidth, viewportHeight, baseUri.AbsoluteUri);
            computer.Measure(body, new SKSize(viewportWidth, viewportHeight));
            computer.Arrange(body, new SKRect(0, 0, viewportWidth, viewportHeight));

            var boxesField = typeof(MinimalLayoutComputer).GetField("_boxes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var boxes = boxesField!.GetValue(computer) as System.Collections.Concurrent.ConcurrentDictionary<Node, BoxModel>;
            Assert.NotNull(boxes);

            var tree = NewPaintTreeBuilder.Build(body, new System.Collections.Generic.Dictionary<Node, BoxModel>(boxes!), styles, viewportWidth, viewportHeight, null);
            var inputBackgrounds = Flatten(tree.Roots)
                .OfType<BackgroundPaintNode>()
                .Where(n => ReferenceEquals(n.SourceNode, searchBox))
                .ToList();

            var inputBackground = Assert.Single(inputBackgrounds);
            Assert.NotNull(inputBackground!.Color);
            Assert.Equal((byte)15, inputBackground.Color!.Value.Red);
            Assert.Equal((byte)23, inputBackground.Color!.Value.Green);
            Assert.Equal((byte)42, inputBackground.Color!.Value.Blue);
        }

        [Fact]
        public async Task RenderFrame_Rasters_NewTab_SearchBox_With_Authored_Dark_Background()
        {
            const int viewportWidth = 1600;
            const int viewportHeight = 900;

            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var searchBox = ById(doc, "url-bar");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth, viewportHeight);

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
            using var canvas = new SKCanvas(bitmap);

            var result = renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
            {
                Root = root,
                Canvas = canvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Navigation,
                RequestedBy = "NewTabPageLayoutTests.RenderFrame",
                EmitVerificationReport = false
            });
            canvas.Flush();

            Assert.NotNull(result);
            var searchBoxLayout = renderer.GetElementBox(searchBox);
            Assert.NotNull(searchBoxLayout);

            int sampleX = (int)Math.Round(searchBoxLayout!.BorderBox.MidX);
            int sampleY = (int)Math.Round(searchBoxLayout.BorderBox.MidY);
            var pixel = bitmap.GetPixel(sampleX, sampleY);

            Assert.InRange(pixel.Red, 10, 30);
            Assert.InRange(pixel.Green, 18, 36);
            Assert.InRange(pixel.Blue, 34, 56);
        }

        [Fact]
        public async Task RecordedFrame_Replay_Keeps_NewTab_SearchBox_Dark()
        {
            const int viewportWidth = 1600;
            const int viewportHeight = 900;

            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var searchBox = ById(doc, "url-bar");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth, viewportHeight);

            var renderer = new SkiaDomRenderer();
            using var recorder = new SKPictureRecorder();
            var recordingCanvas = recorder.BeginRecording(new SKRect(0, 0, viewportWidth, viewportHeight));

            var result = renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
            {
                Root = root,
                Canvas = recordingCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Navigation,
                RequestedBy = "NewTabPageLayoutTests.RecordedFrame",
                EmitVerificationReport = false
            });

            using var picture = recorder.EndRecording();
            using var surface = SKSurface.Create(new SKImageInfo(viewportWidth, viewportHeight));
            Assert.NotNull(surface);

            var replayCanvas = surface.Canvas;
            replayCanvas.Clear(SKColors.White);
            replayCanvas.DrawPicture(picture);
            replayCanvas.Flush();

            using var image = surface.Snapshot();
            using var bitmap = SKBitmap.FromImage(image);

            Assert.NotNull(result);
            var searchBoxLayout = renderer.GetElementBox(searchBox);
            Assert.NotNull(searchBoxLayout);

            int sampleX = (int)Math.Round(searchBoxLayout!.BorderBox.MidX);
            int sampleY = (int)Math.Round(searchBoxLayout.BorderBox.MidY);
            var pixel = bitmap.GetPixel(sampleX, sampleY);

            Assert.InRange(pixel.Red, 10, 30);
            Assert.InRange(pixel.Green, 18, 36);
            Assert.InRange(pixel.Blue, 34, 56);
        }

        [Fact]
        public async Task RenderFrame_Does_Not_Create_Host_Text_Overlay_For_Visible_NewTab_Input_Text()
        {
            const int viewportWidth = 1600;
            const int viewportHeight = 900;

            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var searchBox = ById(doc, "url-bar");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth, viewportHeight);

            Assert.True(styles.TryGetValue(searchBox, out var style));
            Assert.NotNull(style);
            Assert.True(style!.ForegroundColor.HasValue);
            Assert.True(style.ForegroundColor!.Value.Alpha > 0);

            var renderer = new SkiaDomRenderer();
            using var surface = SKSurface.Create(new SKImageInfo(viewportWidth, viewportHeight));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            var result = renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
            {
                Root = root,
                Canvas = canvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Navigation,
                RequestedBy = "NewTabPageLayoutTests.NoHostTextOverlay",
                EmitVerificationReport = false
            });

            Assert.NotNull(result);
            Assert.DoesNotContain(result.Overlays, overlay => ReferenceEquals(overlay.Node, searchBox));
        }

        [Fact]
        public async Task IncrementalRecascade_Preserves_Selector_And_Inline_Style_Correctness_For_NewTab_Input()
        {
            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var searchBox = ById(doc, "url-bar");

            using var engine = new CustomHtmlEngine();
            SetPrivateField(engine, "_activeDom", root);
            SetPrivateField(engine, "_activeBaseUri", baseUri);
            SetPrivateField(engine, "_activeFetchCss", new Func<Uri, Task<string>>(_ => Task.FromResult(string.Empty)));

            await engine.RecascadeAsync();
            Assert.True(engine.LastComputedStyles.TryGetValue(searchBox, out var initialStyle));
            Assert.NotNull(initialStyle);
            Assert.Equal((byte)15, initialStyle.BackgroundColor!.Value.Red);

            searchBox.SetAttribute("style", "background: rgb(255, 0, 0);");

            var method = typeof(CustomHtmlEngine).GetMethod("IncrementalRecascadeAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            var recascadeTask = method!.Invoke(engine, null) as Task;
            Assert.NotNull(recascadeTask);
            await recascadeTask;

            Assert.True(engine.LastComputedStyles.TryGetValue(searchBox, out var updatedStyle));
            Assert.NotNull(updatedStyle);
            Assert.Equal((byte)255, updatedStyle.BackgroundColor!.Value.Red);
            Assert.Equal((byte)0, updatedStyle.BackgroundColor.Value.Green);
            Assert.Equal((byte)0, updatedStyle.BackgroundColor.Value.Blue);
        }

        [Fact]
        public async Task CustomHtmlEngine_RenderAsync_Preserves_NewTab_Input_Background()
        {
            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");

            using var engine = new CustomHtmlEngine
            {
                EnableJavaScript = true
            };

            await engine.RenderAsync(
                html,
                baseUri,
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: 1600,
                viewportHeight: 900);

            var activeRoot = Assert.IsType<Element>(engine.GetActiveDom());
            var searchBox = activeRoot
                .Descendants()
                .OfType<Element>()
                .First(e => string.Equals(e.Id, "url-bar", StringComparison.Ordinal));

            Assert.True(engine.LastComputedStyles.TryGetValue(searchBox, out var style));
            Assert.NotNull(style);
            Assert.True(style.BackgroundColor.HasValue, "Expected #url-bar to retain authored background color after live RenderAsync.");
            Assert.Equal((byte)15, style.BackgroundColor.Value.Red);
            Assert.Equal((byte)23, style.BackgroundColor.Value.Green);
            Assert.Equal((byte)42, style.BackgroundColor.Value.Blue);
            Assert.Equal((byte)224, style.BackgroundColor.Value.Alpha);
        }

        [Fact]
        public async Task CustomHtmlEngine_RenderAsync_Rasters_NewTab_Input_With_Dark_Background()
        {
            const int viewportWidth = 1600;
            const int viewportHeight = 900;

            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");

            using var engine = new CustomHtmlEngine
            {
                EnableJavaScript = true
            };

            await engine.RenderAsync(
                html,
                baseUri,
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: viewportWidth,
                viewportHeight: viewportHeight);

            var activeRoot = Assert.IsType<Element>(engine.GetActiveDom());
            var searchBox = activeRoot
                .Descendants()
                .OfType<Element>()
                .First(e => string.Equals(e.Id, "url-bar", StringComparison.Ordinal));

            using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
            using var canvas = new SKCanvas(bitmap);
            var renderer = new SkiaDomRenderer();

            var result = renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
            {
                Root = activeRoot,
                Canvas = canvas,
                Styles = engine.LastComputedStyles,
                Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Navigation,
                RequestedBy = "NewTabPageLayoutTests.CustomHtmlEngine.RenderFrame",
                EmitVerificationReport = false
            });
            canvas.Flush();

            Assert.NotNull(result);
            var searchBoxLayout = renderer.GetElementBox(searchBox);
            Assert.NotNull(searchBoxLayout);

            int sampleX = (int)Math.Round(searchBoxLayout!.BorderBox.MidX);
            int sampleY = (int)Math.Round(searchBoxLayout.BorderBox.MidY);
            var pixel = bitmap.GetPixel(sampleX, sampleY);

            Assert.InRange(pixel.Red, 10, 30);
            Assert.InRange(pixel.Green, 18, 36);
            Assert.InRange(pixel.Blue, 34, 56);
        }

        [Fact]
        public async Task Hovering_NewTab_Input_Does_Not_Modulate_Page_Backdrop()
        {
            const int viewportWidth = 1600;
            const int viewportHeight = 900;

            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var searchBox = ById(doc, "url-bar");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth, viewportHeight);

            var renderer = new SkiaDomRenderer();
            using var beforeBitmap = new SKBitmap(viewportWidth, viewportHeight);
            using var beforeCanvas = new SKCanvas(beforeBitmap);
            renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
            {
                Root = root,
                Canvas = beforeCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Navigation,
                RequestedBy = "NewTabPageLayoutTests.HoverBackdrop.before",
                EmitVerificationReport = false
            });
            beforeCanvas.Flush();
            var beforePixel = beforeBitmap.GetPixel(30, 30);

            using var hoveredBitmap = new SKBitmap(viewportWidth, viewportHeight);
            using var hoveredCanvas = new SKCanvas(hoveredBitmap);
            try
            {
                ElementStateManager.Instance.SetHoveredElement(searchBox);
                renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
                {
                    Root = root,
                    Canvas = hoveredCanvas,
                    Styles = styles,
                    Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                    BaseUrl = baseUri.AbsoluteUri,
                    InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Input,
                    RequestedBy = "NewTabPageLayoutTests.HoverBackdrop.hovered",
                    EmitVerificationReport = false
                });
                hoveredCanvas.Flush();
            }
            finally
            {
                ElementStateManager.Instance.SetHoveredElement(null);
            }

            var hoveredPixel = hoveredBitmap.GetPixel(30, 30);
            Assert.Equal(beforePixel.Red, hoveredPixel.Red);
            Assert.Equal(beforePixel.Green, hoveredPixel.Green);
            Assert.Equal(beforePixel.Blue, hoveredPixel.Blue);
        }

        [Fact]
        public async Task HoverHighlight_Respects_NewTab_SearchBox_Rounded_Corners()
        {
            const int viewportWidth = 1600;
            const int viewportHeight = 900;

            string html = NewTabRenderer.Render();
            var baseUri = new Uri("https://fen.newtab/");
            var parser = new HtmlParser(html, baseUri);
            var doc = parser.Parse();
            var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
            var searchBox = ById(doc, "url-bar");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth, viewportHeight);

            var renderer = new SkiaDomRenderer();
            using var beforeBitmap = new SKBitmap(viewportWidth, viewportHeight);
            using var beforeCanvas = new SKCanvas(beforeBitmap);
            renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
            {
                Root = root,
                Canvas = beforeCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Navigation,
                RequestedBy = "NewTabPageLayoutTests.RoundedHover.before",
                EmitVerificationReport = false
            });
            beforeCanvas.Flush();

            var box = renderer.GetElementBox(searchBox);
            Assert.NotNull(box);
            int cornerX = (int)Math.Round(box!.BorderBox.Right - 2);
            int cornerY = (int)Math.Round(box.BorderBox.Top + 2);
            var beforeCorner = beforeBitmap.GetPixel(cornerX, cornerY);

            using var hoveredBitmap = new SKBitmap(viewportWidth, viewportHeight);
            using var hoveredCanvas = new SKCanvas(hoveredBitmap);
            try
            {
                ElementStateManager.Instance.SetHoveredElement(searchBox);
                renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
                {
                    Root = root,
                    Canvas = hoveredCanvas,
                    Styles = styles,
                    Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                    BaseUrl = baseUri.AbsoluteUri,
                    InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Input,
                    RequestedBy = "NewTabPageLayoutTests.RoundedHover.hovered",
                    EmitVerificationReport = false
                });
                hoveredCanvas.Flush();
            }
            finally
            {
                ElementStateManager.Instance.SetHoveredElement(null);
            }

            var hoveredCorner = hoveredBitmap.GetPixel(cornerX, cornerY);
            Assert.Equal(beforeCorner.Red, hoveredCorner.Red);
            Assert.Equal(beforeCorner.Green, hoveredCorner.Green);
            Assert.Equal(beforeCorner.Blue, hoveredCorner.Blue);
        }

        [Fact]
        public void CascadeEngine_Author_Background_Shorthand_Overrides_UserAgent_BackgroundColor_Longhand()
        {
            var html = new Element("html");
            var body = new Element("body");
            var input = new Element("input");
            input.SetAttribute("class", "search-box");
            input.SetAttribute("type", "text");
            body.AppendChild(input);
            html.AppendChild(body);

            var stylesheet = new CssStylesheet();

            var uaRule = new CssStyleRule
            {
                Origin = CssOrigin.UserAgent,
                Order = 0,
                Selector = CreateSelector("input", tagName: "input")
            };
            uaRule.Declarations.Add(new CssDeclaration { Property = "background-color", Value = "white" });
            stylesheet.Rules.Add(uaRule);

            var authorRule = new CssStyleRule
            {
                Origin = CssOrigin.Author,
                Order = 1,
                Selector = CreateSelector(".search-box", className: "search-box")
            };
            authorRule.Declarations.Add(new CssDeclaration { Property = "background", Value = "rgba(15, 23, 42, 0.88)" });
            stylesheet.Rules.Add(authorRule);

            var styleSet = new StyleSet(); styleSet.SetSingleSheet(stylesheet); var engine = new CascadeEngine(styleSet);
            var cascaded = engine.ComputeCascadedValues(input);

            Assert.True(cascaded.TryGetValue("background", out var background));
            Assert.Equal("rgba(15, 23, 42, 0.88)", background.Value);

            Assert.True(cascaded.TryGetValue("background-color", out var backgroundColor));
            Assert.Equal("rgba(15, 23, 42, 0.88)", backgroundColor.Value);
        }

        private static Element ById(Document doc, string id)
        {
            return doc.Descendants().OfType<Element>().First(e => string.Equals(e.Id, id, StringComparison.Ordinal));
        }

        private static Element FirstByClass(Document doc, string className)
        {
            return doc.Descendants().OfType<Element>().First(e =>
                (e.ClassName ?? string.Empty)
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Contains(className, StringComparer.Ordinal));
        }

        private static BoxModel AssertBox(MinimalLayoutComputer computer, Element element)
        {
            var box = computer.GetBox(element);
            Assert.NotNull(box);
            return box;
        }

        private static BoxModel AssertBox(System.Collections.Generic.IReadOnlyDictionary<Node, BoxModel> boxes, Element element)
        {
            Assert.True(boxes.TryGetValue(element, out var box));
            Assert.NotNull(box);
            return box;
        }

        private static System.Collections.Generic.IEnumerable<PaintNodeBase> Flatten(System.Collections.Generic.IReadOnlyList<PaintNodeBase> roots)
        {
            foreach (var root in roots)
            {
                foreach (var node in Flatten(root))
                {
                    yield return node;
                }
            }
        }

        private static System.Collections.Generic.IEnumerable<PaintNodeBase> Flatten(PaintNodeBase node)
        {
            yield return node;
            if (node.Children == null)
            {
                yield break;
            }

            foreach (var child in node.Children)
            {
                foreach (var descendant in Flatten(child))
                {
                    yield return descendant;
                }
            }
        }

        private static CssSelector CreateSelector(string raw, string tagName = null, string className = null)
        {
            var segment = new SelectorSegment { TagName = tagName };
            if (!string.IsNullOrEmpty(className))
            {
                segment.Classes.Add(className);
            }

            var chain = new SelectorChain();
            chain.Segments.Add(segment);

            return new CssSelector
            {
                Raw = raw,
                Chains = new System.Collections.Generic.List<SelectorChain> { chain }
            };
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            var field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            field!.SetValue(target, value);
        }
    }
}
