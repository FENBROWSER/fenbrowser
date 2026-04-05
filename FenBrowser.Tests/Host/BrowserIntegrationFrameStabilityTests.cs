using System.Reflection;
using System.Threading;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.Host;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Host
{
    [Collection("Host UI Tests")]
    public class BrowserIntegrationFrameStabilityTests
    {
        [Fact]
        public void RecordFrame_WithCommittedFrameAndMissingStyles_KeepsPreviousFrame()
        {
            var integration = new BrowserIntegration();

            try
            {
                var currentFrameField = typeof(BrowserIntegration).GetField("_currentFrame", BindingFlags.Instance | BindingFlags.NonPublic);
                var rootField = typeof(BrowserIntegration).GetField("_root", BindingFlags.Instance | BindingFlags.NonPublic);
                var stylesField = typeof(BrowserIntegration).GetField("_styles", BindingFlags.Instance | BindingFlags.NonPublic);
                var needsRepaintField = typeof(BrowserIntegration).GetField("_needsRepaint", BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.NotNull(currentFrameField);
                Assert.NotNull(rootField);
                Assert.NotNull(stylesField);
                Assert.NotNull(needsRepaintField);

                using var recorder = new SKPictureRecorder();
                using var canvas = recorder.BeginRecording(new SKRect(0, 0, 40, 40));
                canvas.Clear(SKColors.White);
                var committedFrame = recorder.EndRecording();

                currentFrameField!.SetValue(integration, committedFrame);
                rootField!.SetValue(integration, new Element("html"));
                stylesField!.SetValue(integration, null);
                needsRepaintField!.SetValue(integration, true);

                integration.RecordFrame(new SKSize(1280, 720));

                Assert.Same(committedFrame, currentFrameField.GetValue(integration));
                Assert.False((bool)needsRepaintField.GetValue(integration)!);
            }
            finally
            {
                ShutdownEngineLoop(integration);
            }
        }

        [Fact]
        public async Task RecordFrame_And_Render_Preserve_NewTab_Input_Dark_Background()
        {
            const int viewportWidth = 1600;
            const int viewportHeight = 900;

            var integration = new BrowserIntegration();

            try
            {
                string html = NewTabRenderer.Render();
                var baseUri = new Uri("https://fen.newtab/");
                var parser = new FenBrowser.Core.Parsing.HtmlParser(html, baseUri);
                var doc = parser.Parse();
                var root = doc.Children.OfType<Element>().First(e => string.Equals(e.TagName, "HTML", StringComparison.OrdinalIgnoreCase));
                var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth, viewportHeight);
                var searchBox = doc.Descendants().OfType<Element>().First(e => string.Equals(e.Id, "url-bar", StringComparison.Ordinal));

                var rootField = typeof(BrowserIntegration).GetField("_root", BindingFlags.Instance | BindingFlags.NonPublic);
                var stylesField = typeof(BrowserIntegration).GetField("_styles", BindingFlags.Instance | BindingFlags.NonPublic);
                var viewportField = typeof(BrowserIntegration).GetField("_lastViewportSize", BindingFlags.Instance | BindingFlags.NonPublic);
                var viewportReceivedField = typeof(BrowserIntegration).GetField("_hasReceivedViewportSize", BindingFlags.Instance | BindingFlags.NonPublic);
                var firstStyledField = typeof(BrowserIntegration).GetField("_hasFirstStyledRender", BindingFlags.Instance | BindingFlags.NonPublic);
                var needsRepaintField = typeof(BrowserIntegration).GetField("_needsRepaint", BindingFlags.Instance | BindingFlags.NonPublic);

                Assert.NotNull(rootField);
                Assert.NotNull(stylesField);
                Assert.NotNull(viewportField);
                Assert.NotNull(viewportReceivedField);
                Assert.NotNull(firstStyledField);
                Assert.NotNull(needsRepaintField);

                rootField!.SetValue(integration, root);
                stylesField!.SetValue(integration, styles);
                viewportField!.SetValue(integration, new SKSize(viewportWidth, viewportHeight));
                viewportReceivedField!.SetValue(integration, true);
                firstStyledField!.SetValue(integration, true);
                needsRepaintField!.SetValue(integration, true);

                integration.RecordFrame(new SKSize(viewportWidth, viewportHeight));

                using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
                using var canvas = new SKCanvas(bitmap);
                integration.Render(canvas, new SKRect(0, 0, viewportWidth, viewportHeight));
                canvas.Flush();

                var renderer = new SkiaDomRenderer();
                renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
                {
                    Root = root,
                    Canvas = new SKCanvas(new SKBitmap(1, 1)),
                    Styles = styles,
                    Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                    BaseUrl = baseUri.AbsoluteUri,
                    InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Navigation,
                    RequestedBy = "BrowserIntegrationFrameStabilityTests",
                    EmitVerificationReport = false
                });

                var searchBoxLayout = renderer.GetElementBox(searchBox);
                Assert.NotNull(searchBoxLayout);

                int sampleX = (int)Math.Round(searchBoxLayout!.BorderBox.MidX);
                int sampleY = (int)Math.Round(searchBoxLayout.BorderBox.MidY);
                var pixel = bitmap.GetPixel(sampleX, sampleY);

                Assert.InRange(pixel.Red, 10, 30);
                Assert.InRange(pixel.Green, 18, 36);
                Assert.InRange(pixel.Blue, 34, 56);
            }
            finally
            {
                ShutdownEngineLoop(integration);
            }
        }

        [Fact]
        public async Task NavigateAsync_FenNewTab_Preserves_Live_Input_Background()
        {
            const int viewportWidth = 1600;
            const int viewportHeight = 900;

            var integration = new BrowserIntegration();

            try
            {
                integration.UpdateViewport(new SKSize(viewportWidth, viewportHeight));
                await integration.NavigateAsync("fen://newtab");

                await WaitUntilAsync(
                    () => integration.Document != null &&
                          integration.ComputedStyles != null &&
                          integration.ComputedStyles.Count > 0 &&
                          HasCommittedFrame(integration),
                    TimeSpan.FromSeconds(7));

                var root = integration.Document;
                Assert.NotNull(root);

                var searchBox = root!
                    .Descendants()
                    .OfType<Element>()
                    .First(e => string.Equals(e.Id, "url-bar", StringComparison.Ordinal));

                Assert.True(integration.ComputedStyles.TryGetValue(searchBox, out var style));
                Assert.NotNull(style);

                using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
                using var canvas = new SKCanvas(bitmap);
                integration.Render(canvas, new SKRect(0, 0, viewportWidth, viewportHeight));
                canvas.Flush();

                var renderer = new SkiaDomRenderer();
                renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
                {
                    Root = root,
                    Canvas = new SKCanvas(new SKBitmap(1, 1)),
                    Styles = integration.ComputedStyles,
                    Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                    BaseUrl = integration.CurrentUrl,
                    InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Navigation,
                    RequestedBy = "BrowserIntegrationFrameStabilityTests.NavigateAsync",
                    EmitVerificationReport = false
                });

                var searchBoxLayout = renderer.GetElementBox(searchBox);
                Assert.NotNull(searchBoxLayout);

                int sampleX = (int)Math.Round(searchBoxLayout!.BorderBox.MidX);
                int sampleY = (int)Math.Round(searchBoxLayout.BorderBox.MidY);
                var pixel = bitmap.GetPixel(sampleX, sampleY);

                Assert.InRange(pixel.Red, 10, 30);
                Assert.InRange(pixel.Green, 18, 36);
                Assert.InRange(pixel.Blue, 34, 56);
            }
            finally
            {
                ShutdownEngineLoop(integration);
            }
        }

        [Fact]
        public async Task NavigateAsync_FenNewTab_PostLoad_Frame_Keeps_Input_Dark_At_Window_Viewport()
        {
            const int viewportWidth = 1920;
            const int viewportHeight = 927;

            var integration = new BrowserIntegration();

            try
            {
                integration.UpdateViewport(new SKSize(viewportWidth, viewportHeight));
                await integration.NavigateAsync("fen://newtab");

                await WaitUntilAsync(
                    () => integration.Document != null &&
                          integration.ComputedStyles != null &&
                          integration.ComputedStyles.Count > 0 &&
                          HasCommittedFrame(integration),
                    TimeSpan.FromSeconds(2));

                await Task.Delay(TimeSpan.FromSeconds(5));

                var root = integration.Document;
                Assert.NotNull(root);

                var searchBox = root!
                    .Descendants()
                    .OfType<Element>()
                    .First(e => string.Equals(e.Id, "url-bar", StringComparison.Ordinal));

                Assert.True(integration.ComputedStyles.TryGetValue(searchBox, out var style));
                Assert.NotNull(style);
                Assert.True(style!.BackgroundColor.HasValue);

                using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
                using var canvas = new SKCanvas(bitmap);
                integration.Render(canvas, new SKRect(0, 0, viewportWidth, viewportHeight));
                canvas.Flush();

                var renderer = new SkiaDomRenderer();
                renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
                {
                    Root = root,
                    Canvas = new SKCanvas(new SKBitmap(1, 1)),
                    Styles = integration.ComputedStyles,
                    Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                    BaseUrl = integration.CurrentUrl,
                    InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Navigation,
                    RequestedBy = "BrowserIntegrationFrameStabilityTests.NavigateAsync.PostLoad",
                    EmitVerificationReport = false
                });

                var searchBoxLayout = renderer.GetElementBox(searchBox);
                Assert.NotNull(searchBoxLayout);

                int sampleX = (int)Math.Round(searchBoxLayout!.BorderBox.MidX);
                int sampleY = (int)Math.Round(searchBoxLayout.BorderBox.MidY);
                var pixel = bitmap.GetPixel(sampleX, sampleY);

                Assert.Equal((byte)15, style.BackgroundColor.Value.Red);
                Assert.Equal((byte)23, style.BackgroundColor.Value.Green);
                Assert.Equal((byte)42, style.BackgroundColor.Value.Blue);

                Assert.InRange(pixel.Red, 10, 30);
                Assert.InRange(pixel.Green, 18, 36);
                Assert.InRange(pixel.Blue, 34, 56);
            }
            finally
            {
                ShutdownEngineLoop(integration);
            }
        }

        [Fact]
        public async Task NavigateAsync_Before_Viewport_Update_Keeps_NewTab_Input_Dark()
        {
            const int viewportWidth = 1920;
            const int viewportHeight = 927;

            var integration = new BrowserIntegration();

            try
            {
                await integration.NavigateAsync("fen://newtab");
                await Task.Delay(50);
                integration.UpdateViewport(new SKSize(viewportWidth, viewportHeight));

                await WaitUntilAsync(
                    () => integration.Document != null &&
                          integration.ComputedStyles != null &&
                          integration.ComputedStyles.Count > 0 &&
                          HasCommittedFrame(integration),
                    TimeSpan.FromSeconds(3));

                await Task.Delay(TimeSpan.FromSeconds(5));

                var root = integration.Document;
                Assert.NotNull(root);

                var searchBox = root!
                    .Descendants()
                    .OfType<Element>()
                    .First(e => string.Equals(e.Id, "url-bar", StringComparison.Ordinal));

                Assert.True(integration.ComputedStyles.TryGetValue(searchBox, out var style));
                Assert.NotNull(style);
                Assert.True(style!.BackgroundColor.HasValue);

                using var bitmap = new SKBitmap(viewportWidth, viewportHeight);
                using var canvas = new SKCanvas(bitmap);
                integration.Render(canvas, new SKRect(0, 0, viewportWidth, viewportHeight));
                canvas.Flush();

                var renderer = new SkiaDomRenderer();
                renderer.RenderFrame(new global::FenBrowser.FenEngine.Rendering.Core.RenderFrameRequest
                {
                    Root = root,
                    Canvas = new SKCanvas(new SKBitmap(1, 1)),
                    Styles = integration.ComputedStyles,
                    Viewport = new SKRect(0, 0, viewportWidth, viewportHeight),
                    BaseUrl = integration.CurrentUrl,
                    InvalidationReason = global::FenBrowser.FenEngine.Rendering.Core.RenderFrameInvalidationReason.Navigation,
                    RequestedBy = "BrowserIntegrationFrameStabilityTests.NavigateBeforeViewport",
                    EmitVerificationReport = false
                });

                var searchBoxLayout = renderer.GetElementBox(searchBox);
                Assert.NotNull(searchBoxLayout);

                int sampleX = (int)Math.Round(searchBoxLayout!.BorderBox.MidX);
                int sampleY = (int)Math.Round(searchBoxLayout.BorderBox.MidY);
                var pixel = bitmap.GetPixel(sampleX, sampleY);

                Assert.InRange(pixel.Red, 10, 30);
                Assert.InRange(pixel.Green, 18, 36);
                Assert.InRange(pixel.Blue, 34, 56);
            }
            finally
            {
                ShutdownEngineLoop(integration);
            }
        }

        private static void ShutdownEngineLoop(BrowserIntegration integration)
        {
            var runningField = typeof(BrowserIntegration).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic);
            var wakeEventField = typeof(BrowserIntegration).GetField("_wakeEvent", BindingFlags.Instance | BindingFlags.NonPublic);
            var engineThreadField = typeof(BrowserIntegration).GetField("_engineThread", BindingFlags.Instance | BindingFlags.NonPublic);
            var currentFrameField = typeof(BrowserIntegration).GetField("_currentFrame", BindingFlags.Instance | BindingFlags.NonPublic);

            runningField?.SetValue(integration, false);
            (wakeEventField?.GetValue(integration) as AutoResetEvent)?.Set();
            (engineThreadField?.GetValue(integration) as Thread)?.Join(TimeSpan.FromSeconds(2));
            (currentFrameField?.GetValue(integration) as SKPicture)?.Dispose();
        }

        private static bool HasCommittedFrame(BrowserIntegration integration)
        {
            var currentFrameField = typeof(BrowserIntegration).GetField("_currentFrame", BindingFlags.Instance | BindingFlags.NonPublic);
            return currentFrameField?.GetValue(integration) is SKPicture;
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (predicate())
                {
                    return;
                }

                await Task.Delay(50);
            }

            Assert.True(predicate(), $"Condition was not satisfied within {timeout.TotalMilliseconds:F0}ms.");
        }
    }
}
