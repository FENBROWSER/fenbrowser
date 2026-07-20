using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering.Css;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Performance
{
    public readonly record struct RenderPerformanceThreshold(double WarningMs, double FailureMs);

    public sealed record RenderPerformanceBenchmarkScenario(
        string Name,
        string Html,
        int ViewportWidth,
        int ViewportHeight,
        int Iterations,
        RenderPerformanceThreshold Threshold,
        bool PreferSteadyStateDamage);

    public sealed record RenderPerformanceBenchmarkResult(
        string Name,
        int Iterations,
        double AverageTotalMs,
        double MaxTotalMs,
        double HtmlParseMs,
        long HtmlParseAllocatedBytes,
        double CssParseAndStyleMs,
        long CssParseAndStyleAllocatedBytes,
        double CssCoreTotalMs,
        double CssQueueWaitMs,
        double CssDiscoveryAndFetchMs,
        double CssImportExpansionMs,
        double CssRuleParseMs,
        double CssVariableResolutionMs,
        double CssCascadeMs,
        int CssSourceCount,
        int CssRuleCount,
        int ComputedStyleCount,
        int InlineStyleCacheHits,
        int InlineStyleCacheMisses,
        int InlineStyleCacheEvictions,
        int InlineStyleCacheEntries,
        double AverageLayoutMs,
        double AveragePaintGenerationMs,
        double AverageRasterMs,
        long AverageLayoutAllocatedBytes,
        long AveragePaintGenerationAllocatedBytes,
        long AverageRasterAllocatedBytes,
        long RenderAllocatedBytes,
        double PipelineDurationMs,
        long ManagedAllocatedBytes,
        long ManagedHeapBytesAfter,
        long WorkingSetBytesAfter,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        int DomNodeCount,
        int BoxCount,
        int PaintNodeCount,
        RenderFrameRasterMode DominantRasterMode,
        bool WarningGatePassed,
        bool FailureGatePassed,
        // --- Phase 0 global performance counters (aggregated across a run) ---
        int FrameRequests,
        int CommittedFrames,
        int FullLayouts,
        int PaintTreeRebuilds,
        int FullRasters,
        int DamageRasters,
        int CompositorOnlyUpdates,
        int HiddenTabFrames,
        int NoOpRepaintReadySignals,
        int NoOpFramesAvoided);

    /// <summary>
    /// Accumulates the Phase 0 global performance counters from each rendered
    /// frame. Driven by RenderFrameResult so it stays decoupled from the host
    /// integration; later phases populate HiddenTabFrames / NoOp* via hooks.
    /// </summary>
    internal sealed class FrameCounters
    {
        public int FrameRequests;
        public int CommittedFrames;
        public int FullLayouts;
        public int PaintTreeRebuilds;
        public int FullRasters;
        public int DamageRasters;
        public int CompositorOnlyUpdates;
        public int HiddenTabFrames;
        public int NoOpRepaintReadySignals;
        public int NoOpFramesAvoided;

        public void Add(RenderFrameResult result)
        {
            CommittedFrames++;
            FrameRequests++;

            var telemetry = result?.Telemetry;
            if (telemetry != null)
            {
                if (telemetry.PaintTreeRebuilt)
                {
                    PaintTreeRebuilds++;
                }

                if (telemetry.LayoutUpdated)
                {
                    FullLayouts++;
                }
            }

            switch (result?.RasterMode)
            {
                case RenderFrameRasterMode.Full:
                    FullRasters++;
                    break;
                case RenderFrameRasterMode.Damage:
                    DamageRasters++;
                    break;
                case RenderFrameRasterMode.PreservedBaseFrame:
                    CompositorOnlyUpdates++;
                    break;
            }
        }
    }

    public sealed record RenderPerformanceEnvironment(
        string OperatingSystem,
        string OsArchitecture,
        string ProcessArchitecture,
        string Framework,
        string RuntimeVersion,
        string BuildConfiguration,
        string GitCommit,
        string ProcessorName,
        int ProcessorCount,
        long TotalAvailableMemoryBytes,
        bool ServerGc,
        string GcLatencyMode,
        string TieredCompilation,
        string TieredPgo,
        string ReadyToRun);

    public sealed record RenderPerformanceBenchmarkReport(
        DateTimeOffset CreatedAtUtc,
        RenderPerformanceEnvironment Environment,
        IReadOnlyList<RenderPerformanceBenchmarkResult> Results)
    {
        public bool FailureGatePassed => Results.All(result => result.FailureGatePassed);
    }

    public sealed class RenderPerformanceBenchmarkRunner
    {
        public async Task<RenderPerformanceBenchmarkReport> RunDefaultSuiteAsync(CancellationToken cancellationToken = default)
        {
            var scenarios = BuildDefaultSuite();
            var results = new List<RenderPerformanceBenchmarkResult>(scenarios.Count);
            foreach (var scenario in scenarios)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await RunScenarioAsync(scenario, cancellationToken).ConfigureAwait(false));
            }

            return new RenderPerformanceBenchmarkReport(DateTimeOffset.UtcNow, CaptureEnvironment(), results);
        }

        public async Task<RenderPerformanceBenchmarkResult> RunScenarioAsync(RenderPerformanceBenchmarkScenario scenario, CancellationToken cancellationToken = default)
        {
            using var measurementScope = BenchmarkMeasurementScope.Enter();

            long allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            int gen0Before = GC.CollectionCount(0);
            int gen1Before = GC.CollectionCount(1);
            int gen2Before = GC.CollectionCount(2);
            var pipelineStopwatch = Stopwatch.StartNew();

            var baseUri = new Uri("https://bench.fen/");
            long htmlAllocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            long parseStarted = Stopwatch.GetTimestamp();
            var parser = new HtmlParser(scenario.Html, baseUri);
            var document = parser.Parse();
            double htmlParseMs = Stopwatch.GetElapsedTime(parseStarted).TotalMilliseconds;
            long htmlParseAllocatedBytes = Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - htmlAllocatedBefore);
            var root = document.DocumentElement;
            long cssAllocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
            long cssStarted = Stopwatch.GetTimestamp();
            var cssResult = await CssLoader.ComputeWithResultAsync(root, baseUri, null, scenario.ViewportWidth, scenario.ViewportHeight).ConfigureAwait(false);
            var styles = cssResult.Computed;
            double cssParseAndStyleMs = Stopwatch.GetElapsedTime(cssStarted).TotalMilliseconds;
            long cssParseAndStyleAllocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - cssAllocatedBefore);
            var renderer = new SkiaDomRenderer();
            var totals = new List<double>(scenario.Iterations);
            var layoutTotals = new List<double>(scenario.Iterations);
            var paintTotals = new List<double>(scenario.Iterations);
            var rasterTotals = new List<double>(scenario.Iterations);
            var layoutAllocationTotals = new List<long>(scenario.Iterations);
            var paintAllocationTotals = new List<long>(scenario.Iterations);
            var rasterAllocationTotals = new List<long>(scenario.Iterations);
            var rasterModes = new Dictionary<RenderFrameRasterMode, int>();
            var counters = new FrameCounters();
            RenderFrameResult lastResult = null;
            long renderAllocatedBefore = GC.GetTotalAllocatedBytes(precise: false);

            using var initialBitmap = new SKBitmap(scenario.ViewportWidth, scenario.ViewportHeight);
            using var initialCanvas = new SKCanvas(initialBitmap);
            lastResult = renderer.RenderFrame(new RenderFrameRequest
            {
                Root = root,
                Canvas = initialCanvas,
                Styles = styles,
                Viewport = new SKRect(0, 0, scenario.ViewportWidth, scenario.ViewportHeight),
                BaseUrl = baseUri.AbsoluteUri,
                InvalidationReason = RenderFrameInvalidationReason.Navigation,
                RequestedBy = $"RenderPerformanceBenchmark.{scenario.Name}.Initial",
                EmitVerificationReport = false,
                CollectAllocationTelemetry = true
            });
            initialCanvas.Flush();

            if (!scenario.PreferSteadyStateDamage)
            {
                RecordTelemetry(
                    lastResult.Telemetry,
                    totals,
                    layoutTotals,
                    paintTotals,
                    rasterTotals,
                    layoutAllocationTotals,
                    paintAllocationTotals,
                    rasterAllocationTotals);
                CountRasterMode(rasterModes, lastResult.RasterMode);
                counters.Add(lastResult);
            }

            for (int i = 1; i < scenario.Iterations; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (scenario.PreferSteadyStateDamage)
                {
                    root.MarkDirty(InvalidationKind.Paint);
                }

                using var bitmap = new SKBitmap(scenario.ViewportWidth, scenario.ViewportHeight);
                using var canvas = new SKCanvas(bitmap);
                if (scenario.PreferSteadyStateDamage)
                {
                    canvas.DrawBitmap(initialBitmap, 0, 0);
                }

                lastResult = renderer.RenderFrame(new RenderFrameRequest
                {
                    Root = root,
                    Canvas = canvas,
                    Styles = styles,
                    Viewport = new SKRect(0, 0, scenario.ViewportWidth, scenario.ViewportHeight),
                    BaseUrl = baseUri.AbsoluteUri,
                    HasBaseFrame = scenario.PreferSteadyStateDamage,
                    InvalidationReason = scenario.PreferSteadyStateDamage
                        ? RenderFrameInvalidationReason.Animation
                        : RenderFrameInvalidationReason.Navigation,
                    RequestedBy = $"RenderPerformanceBenchmark.{scenario.Name}.{i}",
                    EmitVerificationReport = false,
                    CollectAllocationTelemetry = true
                });

                canvas.Flush();
                RecordTelemetry(
                    lastResult.Telemetry,
                    totals,
                    layoutTotals,
                    paintTotals,
                    rasterTotals,
                    layoutAllocationTotals,
                    paintAllocationTotals,
                    rasterAllocationTotals);
                CountRasterMode(rasterModes, lastResult.RasterMode);
                counters.Add(lastResult);
            }

            pipelineStopwatch.Stop();
            double average = totals.Count > 0 ? totals.Average() : 0;
            double max = totals.Count > 0 ? totals.Max() : 0;
            var dominantRasterMode = rasterModes.Count == 0
                ? RenderFrameRasterMode.None
                : rasterModes.OrderByDescending(pair => pair.Value).First().Key;
            long renderAllocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - renderAllocatedBefore);

            return new RenderPerformanceBenchmarkResult(
                scenario.Name,
                totals.Count,
                Math.Round(average, 2),
                Math.Round(max, 2),
                Math.Round(htmlParseMs, 2),
                htmlParseAllocatedBytes,
                Math.Round(cssParseAndStyleMs, 2),
                cssParseAndStyleAllocatedBytes,
                Math.Round(cssResult.Timing.TotalMs, 2),
                Math.Round(cssResult.Timing.QueueWaitMs, 2),
                Math.Round(cssResult.Timing.DiscoveryAndFetchMs, 2),
                Math.Round(cssResult.Timing.ImportExpansionMs, 2),
                Math.Round(cssResult.Timing.RuleParseMs, 2),
                Math.Round(cssResult.Timing.VariableResolutionMs, 2),
                Math.Round(cssResult.Timing.CascadeMs, 2),
                cssResult.Timing.SourceCount,
                cssResult.Timing.RuleCount,
                cssResult.Timing.ComputedStyleCount,
                cssResult.Timing.InlineStyleCacheHits,
                cssResult.Timing.InlineStyleCacheMisses,
                cssResult.Timing.InlineStyleCacheEvictions,
                cssResult.Timing.InlineStyleCacheEntries,
                Math.Round(AverageOrZero(layoutTotals), 2),
                Math.Round(AverageOrZero(paintTotals), 2),
                Math.Round(AverageOrZero(rasterTotals), 2),
                AverageOrZero(layoutAllocationTotals),
                AverageOrZero(paintAllocationTotals),
                AverageOrZero(rasterAllocationTotals),
                renderAllocatedBytes,
                Math.Round(pipelineStopwatch.Elapsed.TotalMilliseconds, 2),
                Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore),
                GC.GetTotalMemory(forceFullCollection: false),
                GetWorkingSetBytes(),
                Math.Max(0, GC.CollectionCount(0) - gen0Before),
                Math.Max(0, GC.CollectionCount(1) - gen1Before),
                Math.Max(0, GC.CollectionCount(2) - gen2Before),
                lastResult?.Telemetry?.DomNodeCount ?? 0,
                lastResult?.Telemetry?.BoxCount ?? 0,
                lastResult?.Telemetry?.PaintNodeCount ?? 0,
                dominantRasterMode,
                average <= scenario.Threshold.WarningMs,
                average <= scenario.Threshold.FailureMs,
                counters.FrameRequests,
                counters.CommittedFrames,
                counters.FullLayouts,
                counters.PaintTreeRebuilds,
                counters.FullRasters,
                counters.DamageRasters,
                counters.CompositorOnlyUpdates,
                counters.HiddenTabFrames,
                counters.NoOpRepaintReadySignals,
                counters.NoOpFramesAvoided);
        }

        public async Task<string> WriteReportAsync(RenderPerformanceBenchmarkReport report, string outputPath = null, CancellationToken cancellationToken = default)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            outputPath ??= Path.Combine(
                DiagnosticPaths.GetWorkspaceRoot(),
                "Results",
                "performance",
                $"render_perf_benchmark_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            await File.WriteAllTextAsync(
                outputPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken).ConfigureAwait(false);

            return outputPath;
        }

        public static IReadOnlyList<RenderPerformanceBenchmarkScenario> BuildDefaultSuite()
        {
            return new[]
            {
                new RenderPerformanceBenchmarkScenario(
                    "first-frame-heavy-layout",
                    BuildGridPage(blockCount: 140, includeInputs: false),
                    1366,
                    768,
                    1,
                    new RenderPerformanceThreshold(250, 800),
                    PreferSteadyStateDamage: false),
                new RenderPerformanceBenchmarkScenario(
                    "steady-state-damage-animation",
                    BuildGridPage(blockCount: 80, includeInputs: true),
                    1366,
                    768,
                    5,
                    new RenderPerformanceThreshold(16, 50),
                    PreferSteadyStateDamage: true),
                new RenderPerformanceBenchmarkScenario(
                    "dense-text-flow",
                    BuildTextPage(paragraphCount: 180),
                    1280,
                    720,
                    2,
                    new RenderPerformanceThreshold(120, 400),
                    PreferSteadyStateDamage: false),
                new RenderPerformanceBenchmarkScenario(
                    "wrapped-multiline-text",
                    BuildWrappedTextPage(paragraphCount: 80),
                    640,
                    720,
                    2,
                    new RenderPerformanceThreshold(150, 500),
                    PreferSteadyStateDamage: false)
            };
        }

        public static string FormatSummary(RenderPerformanceBenchmarkReport report)
        {
            var builder = new StringBuilder();
            builder.AppendLine("Fen render/perf benchmarks");
            foreach (var result in report.Results)
            {
                builder.AppendLine(
                    $"{result.Name}: total={result.AverageTotalMs:0.##}ms html={result.HtmlParseMs:0.##}ms htmlAlloc={result.HtmlParseAllocatedBytes}B cssRules={result.CssRuleParseMs:0.##}ms cascade={result.CssCascadeMs:0.##}ms cssTotal={result.CssParseAndStyleMs:0.##}ms cssAlloc={result.CssParseAndStyleAllocatedBytes}B inlineStyleCache={result.InlineStyleCacheHits}h/{result.InlineStyleCacheMisses}m/{result.InlineStyleCacheEvictions}e layout={result.AverageLayoutMs:0.##}ms/{result.AverageLayoutAllocatedBytes}B paint={result.AveragePaintGenerationMs:0.##}ms/{result.AveragePaintGenerationAllocatedBytes}B raster={result.AverageRasterMs:0.##}ms/{result.AverageRasterAllocatedBytes}B renderAlloc={result.RenderAllocatedBytes}B alloc={result.ManagedAllocatedBytes}B dom={result.DomNodeCount} boxes={result.BoxCount} paintNodes={result.PaintNodeCount} rasterMode={result.DominantRasterMode} failGate={result.FailureGatePassed}");
            }

            return builder.ToString();
        }

        private static void CountRasterMode(Dictionary<RenderFrameRasterMode, int> rasterModes, RenderFrameRasterMode mode)
        {
            rasterModes.TryGetValue(mode, out int count);
            rasterModes[mode] = count + 1;
        }

        private static void RecordTelemetry(
            RenderFrameTelemetry telemetry,
            List<double> totals,
            List<double> layoutTotals,
            List<double> paintTotals,
            List<double> rasterTotals,
            List<long> layoutAllocationTotals,
            List<long> paintAllocationTotals,
            List<long> rasterAllocationTotals)
        {
            totals.Add(telemetry?.TotalDurationMs ?? 0);
            layoutTotals.Add(telemetry?.LayoutDurationMs ?? 0);
            paintTotals.Add(telemetry?.PaintDurationMs ?? 0);
            rasterTotals.Add(telemetry?.RasterDurationMs ?? 0);
            layoutAllocationTotals.Add(telemetry?.LayoutAllocatedBytes ?? 0);
            paintAllocationTotals.Add(telemetry?.PaintAllocatedBytes ?? 0);
            rasterAllocationTotals.Add(telemetry?.RasterAllocatedBytes ?? 0);
        }

        private static double AverageOrZero(List<double> values)
        {
            return values.Count == 0 ? 0 : values.Average();
        }

        private static long AverageOrZero(List<long> values)
        {
            return values.Count == 0 ? 0 : (long)Math.Round(values.Average());
        }

        private static RenderPerformanceEnvironment CaptureEnvironment()
        {
            var gcInfo = GC.GetGCMemoryInfo();
            return new RenderPerformanceEnvironment(
                RuntimeInformation.OSDescription,
                RuntimeInformation.OSArchitecture.ToString(),
                RuntimeInformation.ProcessArchitecture.ToString(),
                RuntimeInformation.FrameworkDescription,
                Environment.Version.ToString(),
#if DEBUG
                "Debug",
#else
                "Release",
#endif
                TryResolveGitCommit(),
                Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? RuntimeInformation.ProcessArchitecture.ToString(),
                Environment.ProcessorCount,
                gcInfo.TotalAvailableMemoryBytes,
                GCSettings.IsServerGC,
                GCSettings.LatencyMode.ToString(),
                ReadRuntimeSetting("DOTNET_TieredCompilation"),
                ReadRuntimeSetting("DOTNET_TieredPGO"),
                ReadRuntimeSetting("DOTNET_ReadyToRun"));
        }

        private static string ReadRuntimeSetting(string name)
        {
            return Environment.GetEnvironmentVariable(name) ?? "runtime-default";
        }

        private static long GetWorkingSetBytes()
        {
            using var process = Process.GetCurrentProcess();
            return process.WorkingSet64;
        }

        private static string TryResolveGitCommit()
        {
            foreach (var startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                var directory = new DirectoryInfo(startPath);
                while (directory != null)
                {
                    string gitPath = Path.Combine(directory.FullName, ".git");
                    if (Directory.Exists(gitPath))
                    {
                        try
                        {
                            string head = File.ReadAllText(Path.Combine(gitPath, "HEAD")).Trim();
                            if (!head.StartsWith("ref: ", StringComparison.Ordinal))
                            {
                                return head;
                            }

                            string refPath = Path.Combine(gitPath, head.Substring(5).Replace('/', Path.DirectorySeparatorChar));
                            return File.Exists(refPath) ? File.ReadAllText(refPath).Trim() : "unknown";
                        }
                        catch (IOException)
                        {
                            return "unknown";
                        }
                        catch (UnauthorizedAccessException)
                        {
                            return "unknown";
                        }
                    }

                    directory = directory.Parent;
                }
            }

            return "unknown";
        }

        private sealed class BenchmarkMeasurementScope : IDisposable
        {
            private readonly bool _previousGlobalLoggingEnabled;
            private readonly bool _previousLogVerification;
            private readonly bool _previousLogFrameTiming;
            private readonly bool _previousPerformanceRecording;

            private BenchmarkMeasurementScope(
                bool previousGlobalLoggingEnabled,
                bool previousLogVerification,
                bool previousLogFrameTiming,
                bool previousPerformanceRecording)
            {
                _previousGlobalLoggingEnabled = previousGlobalLoggingEnabled;
                _previousLogVerification = previousLogVerification;
                _previousLogFrameTiming = previousLogFrameTiming;
                _previousPerformanceRecording = previousPerformanceRecording;
            }

            public static BenchmarkMeasurementScope Enter()
            {
                var scope = new BenchmarkMeasurementScope(
                    EngineLogCompat.IsEnabled,
                    DebugConfig.LogVerification,
                    DebugConfig.LogFrameTiming,
                    PerformanceDiagnosticsStore.IsRecording);

                EngineLogCompat.IsEnabled = false;
                DebugConfig.LogVerification = false;
                DebugConfig.LogFrameTiming = false;
                PerformanceDiagnosticsStore.StopRecording();
                return scope;
            }

            public void Dispose()
            {
                EngineLogCompat.IsEnabled = _previousGlobalLoggingEnabled;
                DebugConfig.LogVerification = _previousLogVerification;
                DebugConfig.LogFrameTiming = _previousLogFrameTiming;
                if (_previousPerformanceRecording)
                {
                    PerformanceDiagnosticsStore.StartRecording();
                }
            }
        }

        private static string BuildGridPage(int blockCount, bool includeInputs)
        {
            var builder = new StringBuilder();
            builder.Append("<!doctype html><html><body style='margin:0;font-family:Segoe UI;background:#f8f7f2'>");
            builder.Append("<main style='display:grid;grid-template-columns:repeat(4, minmax(0,1fr));gap:14px;padding:18px'>");
            for (int i = 0; i < blockCount; i++)
            {
                builder.Append("<section style='background:white;border:1px solid #d8d4ca;border-radius:12px;padding:12px;box-shadow:0 2px 6px rgba(0,0,0,0.05)'>");
                builder.Append($"<h2 style='margin:0 0 8px 0;font-size:18px'>Card {i}</h2>");
                builder.Append("<p style='margin:0 0 8px 0;color:#333'>Fen render benchmark content with repeated layout and paint surfaces.</p>");
                if (includeInputs && i % 10 == 0)
                {
                    builder.Append("<input placeholder='Search benchmark' style='width:100%;height:38px;border:1px solid #b8b2a5;border-radius:999px;padding:0 12px' />");
                }
                builder.Append("</section>");
            }
            builder.Append("</main></body></html>");
            return builder.ToString();
        }

        private static string BuildTextPage(int paragraphCount)
        {
            var builder = new StringBuilder();
            builder.Append("<!doctype html><html><body style='margin:0;padding:24px;font-family:Georgia;line-height:1.6;color:#222'>");
            for (int i = 0; i < paragraphCount; i++)
            {
                builder.Append($"<p>Paragraph {i}: Fenbrowser aims for strong modularity, predictable rendering cost, and truthful diagnostics under steady-state pressure.</p>");
            }
            builder.Append("</body></html>");
            return builder.ToString();
        }

        private static string BuildWrappedTextPage(int paragraphCount)
        {
            var builder = new StringBuilder();
            builder.Append("<!doctype html><html><body style='margin:0;padding:16px;font-family:Georgia;font-size:16px;line-height:1.5'><main style='width:320px'>");
            for (int i = 0; i < paragraphCount; i++)
            {
                builder.Append($"<p>Wrapped paragraph {i} exercises repeated visual lines in one text node while preserving deterministic local content and stable alignment ancestry.</p>");
            }
            builder.Append("</main></body></html>");
            return builder.ToString();
        }

        // ------------------------------------------------------------------
        // Phase 0 — repeatable performance baseline scenarios.
        // These pages mirror the plan's required workload: static, the four
        // animation property classes, an animated image, a no-op RepaintReady
        // (steady-state paint-dirty) case, a slow-JS pointer page, and three
        // representative tab pages (complex / inactive-static / inactive-gif).
        // ------------------------------------------------------------------

        public static List<RenderPerformanceBenchmarkScenario> BuildPerformanceBaselineSuite()
        {
            return new List<RenderPerformanceBenchmarkScenario>
            {
                new("baseline.static", BuildStaticPage(), 800, 600, 10, new RenderPerformanceThreshold(50, 200), false),
                new("baseline.opacity-animation", BuildOpacityAnimationPage(), 800, 600, 10, new RenderPerformanceThreshold(50, 200), false),
                new("baseline.transform-animation", BuildTransformAnimationPage(), 800, 600, 10, new RenderPerformanceThreshold(50, 200), false),
                new("baseline.background-color-animation", BuildBackgroundColorAnimationPage(), 800, 600, 10, new RenderPerformanceThreshold(50, 200), false),
                new("baseline.width-animation", BuildWidthAnimationPage(), 800, 600, 10, new RenderPerformanceThreshold(50, 200), false),
                new("baseline.animated-gif", BuildAnimatedGifPage(), 800, 600, 10, new RenderPerformanceThreshold(50, 200), false),
                new("baseline.no-op-repaint-ready", BuildStaticPage(), 800, 600, 10, new RenderPerformanceThreshold(50, 200), true),
                new("baseline.slow-js-pointer", BuildSlowJsPointerPage(), 800, 600, 10, new RenderPerformanceThreshold(50, 200), false),
                new("baseline.three-tab-complex", BuildGridPage(60, true), 800, 600, 10, new RenderPerformanceThreshold(80, 300), false),
                new("baseline.three-tab-inactive-static", BuildTextPage(80), 800, 600, 10, new RenderPerformanceThreshold(50, 200), false),
                new("baseline.three-tab-inactive-gif", BuildAnimatedGifPage(), 800, 600, 10, new RenderPerformanceThreshold(50, 200), false)
            };
        }

        private static string BuildStaticPage()
        {
            var b = new StringBuilder();
            b.Append("<!doctype html><html><head><style>body{margin:0;font-family:'Segoe UI';background:#f8f7f2}</style></head><body>");
            b.Append("<header style='padding:16px;background:#202124;color:#fff'><h1 style='margin:0;font-size:20px'>FenBrowser Baseline</h1></header>");
            b.Append("<main style='padding:18px'>");
            for (int i = 0; i < 40; i++)
            {
                b.Append($"<p style='margin:0 0 10px 0;color:#222'>Static paragraph {i} with stable layout and paint surfaces.</p>");
            }

            b.Append("</main></body></html>");
            return b.ToString();
        }

        private static string BuildOpacityAnimationPage()
        {
            return "<!doctype html><html><head><style>" +
                "body{margin:0;background:#fff}" +
                "@keyframes fade{from{opacity:1}to{opacity:0.2}}" +
                ".box{width:200px;height:200px;background:#4a90d9;animation:fade 1s linear infinite}" +
                "</style></head><body><div class='box'></div></body></html>";
        }

        private static string BuildTransformAnimationPage()
        {
            return "<!doctype html><html><head><style>" +
                "body{margin:0;background:#fff}" +
                "@keyframes spin{from{transform:rotate(0)}to{transform:rotate(360deg)}}" +
                ".box{width:200px;height:200px;background:#e74c3c;animation:spin 1s linear infinite}" +
                "</style></head><body><div class='box'></div></body></html>";
        }

        private static string BuildBackgroundColorAnimationPage()
        {
            return "<!doctype html><html><head><style>" +
                "body{margin:0;background:#fff}" +
                "@keyframes col{from{background:#ffffff}to{background:#333333}}" +
                ".box{width:200px;height:200px;animation:col 1s linear infinite}" +
                "</style></head><body><div class='box'></div></body></html>";
        }

        private static string BuildWidthAnimationPage()
        {
            return "<!doctype html><html><head><style>" +
                "body{margin:0;background:#fff}" +
                "@keyframes grow{from{width:100px}to{width:400px}}" +
                ".box{height:100px;background:#27ae60;animation:grow 1s linear infinite}" +
                "</style></head><body><div class='box'></div></body></html>";
        }

        private static string BuildAnimatedGifPage()
        {
            return "<!doctype html><html><body style='margin:0'>" +
                "<img src='data:image/gif;base64,R0lGODlhAQABAIAAAP///wAAACH5BAEAAAAALAAAAAABAAEAAAICRAEAOw==' width='120' height='120' alt='anim'/>" +
                "<p>Animated image placeholder.</p></body></html>";
        }

        private static string BuildSlowJsPointerPage()
        {
            return "<!doctype html><html><body style='margin:0'>" +
                "<button id='b'>Click</button>" +
                "<script>document.getElementById('b').addEventListener('click',function(){var s=Date.now();while(Date.now()-s<2000){}});</script>" +
                "</body></html>";
        }
    }
}
