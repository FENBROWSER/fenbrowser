using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering.Performance;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Performance
{
    [CollectionDefinition("Performance diagnostics", DisableParallelization = true)]
    public sealed class PerformanceDiagnosticsCollection
    {
    }

    [Collection("Performance diagnostics")]
    public sealed class PerformanceDiagnosticsTests
    {
        [Fact]
        public void Store_IsBoundedAndRecordingCanBeDisabled()
        {
            PerformanceDiagnosticsStore.Reset();
            PerformanceDiagnosticsStore.StartRecording();
            PerformanceDiagnosticsStore.RecordFrame(new RenderFrameTelemetry
            {
                Url = "https://fen.test/page",
                DomNodeCount = 12,
                ElementNodeCount = 7,
                TextNodeCount = 4,
                AttributeCount = 3,
                BoxCount = 10,
                PaintNodeCount = 8,
                LayoutDurationMs = 2,
                PaintDurationMs = 3,
                RasterDurationMs = 4,
                LayoutAllocatedBytes = 200,
                PaintAllocatedBytes = 300,
                RasterAllocatedBytes = 400,
                RasterMode = RenderFrameRasterMode.Full
            });

            for (int i = 0; i < PerformanceDiagnosticsStore.MaximumNavigationHistory + 5; i++)
            {
                PerformanceDiagnosticsStore.RecordNavigation(CreateNavigation($"https://fen.test/page?i={i}"));
            }

            var history = PerformanceDiagnosticsStore.GetNavigationHistory();
            Assert.Equal(PerformanceDiagnosticsStore.MaximumNavigationHistory, history.Count);
            Assert.EndsWith("i=5", history[0].Url, StringComparison.Ordinal);
            Assert.EndsWith("i=24", history[^1].Url, StringComparison.Ordinal);

            PerformanceDiagnosticsStore.RecordFrame(new RenderFrameTelemetry
            {
                Url = history[^1].Url,
                DomNodeCount = 12,
                ElementNodeCount = 7,
                TextNodeCount = 4,
                AttributeCount = 3,
                BoxCount = 10,
                PaintNodeCount = 8,
                LayoutDurationMs = 2,
                PaintDurationMs = 3,
                RasterDurationMs = 4,
                LayoutAllocatedBytes = 200,
                PaintAllocatedBytes = 300,
                RasterAllocatedBytes = 400,
                RasterMode = RenderFrameRasterMode.Full
            });
            Assert.Equal(12, PerformanceDiagnosticsStore.GetNavigationHistory()[^1].DomNodeCount);
            Assert.Equal(7, PerformanceDiagnosticsStore.GetNavigationHistory()[^1].ElementNodeCount);
            Assert.Equal(4, PerformanceDiagnosticsStore.GetNavigationHistory()[^1].TextNodeCount);
            Assert.Equal(3, PerformanceDiagnosticsStore.GetNavigationHistory()[^1].AttributeCount);
            Assert.Equal(200, PerformanceDiagnosticsStore.GetNavigationHistory()[^1].LayoutAllocatedBytes);
            Assert.Equal(300, PerformanceDiagnosticsStore.GetNavigationHistory()[^1].PaintAllocatedBytes);
            Assert.Equal(400, PerformanceDiagnosticsStore.GetNavigationHistory()[^1].RasterAllocatedBytes);

            PerformanceDiagnosticsStore.StopRecording();
            var stoppedTelemetry = new RenderFrameTelemetry { Url = history[^1].Url };
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000; i++)
            {
                PerformanceDiagnosticsStore.RecordFrame(stoppedTelemetry);
            }
            long disabledPathAllocations = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            PerformanceDiagnosticsStore.RecordNavigation(CreateNavigation("https://fen.test/not-recorded"));
            Assert.Equal(PerformanceDiagnosticsStore.MaximumNavigationHistory, PerformanceDiagnosticsStore.GetNavigationHistory().Count);
            Assert.Equal(0, disabledPathAllocations);
        }

        [Fact]
        public void PageRenderer_ExposesRequiredSectionsControlsAndUnavailableMetricsHonestly()
        {
            PerformanceDiagnosticsStore.Reset();
            PerformanceDiagnosticsStore.StartRecording();
            PerformanceDiagnosticsStore.RecordInlineStyleCache(new InlineStyleCacheStatistics(7, 2, 1, 4, 256));
            PerformanceDiagnosticsStore.RecordNavigation(CreateNavigation("https://fen.test/latest"));

            string html = PerformancePageRenderer.Render(new Uri("fen://performance"));

            Assert.Contains("Navigation timings", html);
            Assert.Contains("Memory and GC", html);
            Assert.Contains("Document statistics", html);
            Assert.Contains("Invalidation statistics", html);
            Assert.Contains("FenJS statistics", html);
            Assert.Contains("Renderer statistics", html);
            Assert.Contains("Start recording", html);
            Assert.Contains("Stop recording", html);
            Assert.Contains("Reset counters", html);
            Assert.Contains("Copy report", html);
            Assert.Contains("Export results", html);
            Assert.Contains("Not instrumented", html);
            Assert.Contains("https://fen.test/latest", html);
            Assert.Contains("Text measurement cache hits", html);
            Assert.Contains("Image cache bytes", html);
            Assert.Contains("Font cache hits", html);
            Assert.Contains("Inline style cache hits", html);
            Assert.Contains("Layout allocated bytes", html);
            Assert.Contains("Paint allocated bytes", html);
            Assert.Contains("Raster allocated bytes", html);
            Assert.Contains(">7<", html);

            PerformancePageRenderer.Render(new Uri("fen://performance?action=stop"));
            Assert.False(PerformanceDiagnosticsStore.IsRecording);
            PerformancePageRenderer.Render(new Uri("fen://performance?action=reset"));
            Assert.Empty(PerformanceDiagnosticsStore.GetNavigationHistory());
        }

        [Fact]
        public async Task NavigationManager_RoutesPerformancePageWithoutNetworkFetch()
        {
            using var httpClient = new HttpClient();
            var manager = new NavigationManager(new ResourceManager(httpClient));

            var result = await manager.NavigateAsync("fen://performance");

            Assert.Equal(FetchStatus.Success, result.Status);
            Assert.Equal("fen", result.FinalUri.Scheme);
            Assert.Equal("performance", result.FinalUri.Host);
            Assert.Equal("text/html", result.ContentType);
            Assert.Contains("FenBrowser Performance", result.Content);
        }

        [Fact]
        public async Task CustomHtmlEngine_RecordsNavigationAllocationAndGcSnapshot()
        {
            PerformanceDiagnosticsStore.Reset();
            PerformanceDiagnosticsStore.StartRecording();
            using var engine = new CustomHtmlEngine();

            await engine.RenderAsync(
                "<!doctype html><html><body><p>performance probe</p></body></html>",
                new Uri("https://fen.test/performance-probe"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<Stream>(null),
                _ => { },
                viewportWidth: 320,
                viewportHeight: 200,
                forceJavascript: false);

            var telemetry = Assert.IsType<RenderTelemetrySnapshot>(engine.LastRenderTelemetry);
            Assert.Equal("https://fen.test/performance-probe", telemetry.Url);
            Assert.True(telemetry.ManagedAllocatedBytes > 0);
            Assert.True(telemetry.ManagedHeapBytes > 0);
            Assert.True(telemetry.WorkingSetBytes > 0);
            Assert.Single(PerformanceDiagnosticsStore.GetNavigationHistory());
            string page = PerformancePageRenderer.Render(new Uri("fen://performance"));
            Assert.Contains("https://fen.test/performance-probe", page);
            Assert.Contains(telemetry.ManagedAllocatedBytes.ToString(), page);
        }

        [Fact]
        public async Task RenderFrame_RecordsDocumentAndBoundedCacheStatistics()
        {
            const string source = "<!doctype html><html><body><div id='probe' class='sample'>text</div></body></html>";
            var baseUri = new Uri("https://fen.test/frame-statistics");
            var document = new HtmlParser(source, baseUri).Parse();
            var root = document.Children.OfType<Element>().First(element => element.TagName == "HTML");
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, viewportWidth: 320, viewportHeight: 200);
            PerformanceDiagnosticsStore.Reset();
            PerformanceDiagnosticsStore.StartRecording();
            PerformanceDiagnosticsStore.RecordNavigation(CreateNavigation(baseUri.AbsoluteUri));

            var renderer = new SkiaDomRenderer();
            using var bitmap = new SKBitmap(320, 200);
            using var canvas = new SKCanvas(bitmap);
            renderer.RenderFrame(new RenderFrameRequest
            {
                Root = root,
                Canvas = canvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, 320, 200),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Navigation | RenderFrameInvalidationReason.Style,
                RequestedBy = nameof(RenderFrame_RecordsDocumentAndBoundedCacheStatistics)
            });

            var snapshot = Assert.Single(PerformanceDiagnosticsStore.GetNavigationHistory());
            Assert.True(snapshot.DomNodeCount >= 4);
            Assert.True(snapshot.ElementNodeCount >= 3);
            Assert.True(snapshot.TextNodeCount >= 1);
            Assert.True(snapshot.AttributeCount >= 2);
            Assert.True(snapshot.LayoutObjectCount > 0);
            Assert.True(snapshot.PaintCommandCount > 0);
            Assert.True(snapshot.LayoutAllocatedBytes > 0);
            Assert.True(snapshot.PaintAllocatedBytes > 0);
            Assert.True(snapshot.RasterAllocatedBytes >= 0);
            Assert.True(snapshot.RendererCachesCaptured);
            Assert.True(snapshot.TextMeasurementCalls >= 0);
            Assert.True(snapshot.ImageCacheBytes >= 0);
            Assert.True(snapshot.FontCacheBytes >= 0);
        }

        private static RenderTelemetrySnapshot CreateNavigation(string url)
        {
            return new RenderTelemetrySnapshot
            {
                Url = url,
                NavigationStartedAtUtc = DateTimeOffset.UtcNow,
                TokenizingAndParsingMs = 1,
                CssAndStyleMs = 2,
                InitialVisualTreeMs = 3,
                TotalRenderMs = 6,
                ManagedAllocatedBytes = 1024,
                ManagedHeapBytes = 2048,
                WorkingSetBytes = 4096
            };
        }
    }
}
