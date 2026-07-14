using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Rendering.Performance;
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
                BoxCount = 10,
                PaintNodeCount = 8,
                LayoutDurationMs = 2,
                PaintDurationMs = 3,
                RasterDurationMs = 4,
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
                BoxCount = 10,
                PaintNodeCount = 8,
                LayoutDurationMs = 2,
                PaintDurationMs = 3,
                RasterDurationMs = 4,
                RasterMode = RenderFrameRasterMode.Full
            });
            Assert.Equal(12, PerformanceDiagnosticsStore.GetNavigationHistory()[^1].DomNodeCount);

            PerformanceDiagnosticsStore.StopRecording();
            PerformanceDiagnosticsStore.RecordNavigation(CreateNavigation("https://fen.test/not-recorded"));
            Assert.Equal(PerformanceDiagnosticsStore.MaximumNavigationHistory, PerformanceDiagnosticsStore.GetNavigationHistory().Count);
        }

        [Fact]
        public void PageRenderer_ExposesRequiredSectionsControlsAndUnavailableMetricsHonestly()
        {
            PerformanceDiagnosticsStore.Reset();
            PerformanceDiagnosticsStore.StartRecording();
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
