using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
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
        int DomNodeCount,
        int BoxCount,
        int PaintNodeCount,
        RenderFrameRasterMode DominantRasterMode,
        bool WarningGatePassed,
        bool FailureGatePassed);

    public sealed record RenderPerformanceBenchmarkReport(
        DateTimeOffset CreatedAtUtc,
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

            return new RenderPerformanceBenchmarkReport(DateTimeOffset.UtcNow, results);
        }

        public async Task<RenderPerformanceBenchmarkResult> RunScenarioAsync(RenderPerformanceBenchmarkScenario scenario, CancellationToken cancellationToken = default)
        {
            using var measurementScope = BenchmarkMeasurementScope.Enter();

            var baseUri = new Uri("https://bench.fen/");
            var parser = new HtmlParser(scenario.Html, baseUri);
            var document = parser.Parse();
            var root = document.DocumentElement;
            var styles = await CssLoader.ComputeAsync(root, baseUri, null, scenario.ViewportWidth, scenario.ViewportHeight).ConfigureAwait(false);
            var renderer = new SkiaDomRenderer();
            var totals = new List<double>(scenario.Iterations);
            var rasterModes = new Dictionary<RenderFrameRasterMode, int>();
            RenderFrameResult lastResult = null;

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
                EmitVerificationReport = false
            });
            initialCanvas.Flush();

            if (!scenario.PreferSteadyStateDamage)
            {
                totals.Add(lastResult.Telemetry?.TotalDurationMs ?? 0);
                CountRasterMode(rasterModes, lastResult.RasterMode);
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
                    EmitVerificationReport = false
                });

                canvas.Flush();
                totals.Add(lastResult.Telemetry?.TotalDurationMs ?? 0);
                CountRasterMode(rasterModes, lastResult.RasterMode);
            }

            double average = totals.Count > 0 ? totals.Average() : 0;
            double max = totals.Count > 0 ? totals.Max() : 0;
            var dominantRasterMode = rasterModes.Count == 0
                ? RenderFrameRasterMode.None
                : rasterModes.OrderByDescending(pair => pair.Value).First().Key;

            return new RenderPerformanceBenchmarkResult(
                scenario.Name,
                totals.Count,
                Math.Round(average, 2),
                Math.Round(max, 2),
                lastResult?.Telemetry?.DomNodeCount ?? 0,
                lastResult?.Telemetry?.BoxCount ?? 0,
                lastResult?.Telemetry?.PaintNodeCount ?? 0,
                dominantRasterMode,
                average <= scenario.Threshold.WarningMs,
                average <= scenario.Threshold.FailureMs);
        }

        public async Task<string> WriteReportAsync(RenderPerformanceBenchmarkReport report, string outputPath = null, CancellationToken cancellationToken = default)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            outputPath ??= DiagnosticPaths.GetLogArtifactPath($"render_perf_benchmark_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
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
                    $"{result.Name}: avg={result.AverageTotalMs:0.##}ms max={result.MaxTotalMs:0.##}ms dom={result.DomNodeCount} boxes={result.BoxCount} paint={result.PaintNodeCount} raster={result.DominantRasterMode} failGate={result.FailureGatePassed}");
            }

            return builder.ToString();
        }

        private static void CountRasterMode(Dictionary<RenderFrameRasterMode, int> rasterModes, RenderFrameRasterMode mode)
        {
            rasterModes.TryGetValue(mode, out int count);
            rasterModes[mode] = count + 1;
        }

        private sealed class BenchmarkMeasurementScope : IDisposable
        {
            private readonly bool _previousGlobalLoggingEnabled;
            private readonly bool _previousLogVerification;
            private readonly bool _previousLogFrameTiming;

            private BenchmarkMeasurementScope(bool previousGlobalLoggingEnabled, bool previousLogVerification, bool previousLogFrameTiming)
            {
                _previousGlobalLoggingEnabled = previousGlobalLoggingEnabled;
                _previousLogVerification = previousLogVerification;
                _previousLogFrameTiming = previousLogFrameTiming;
            }

            public static BenchmarkMeasurementScope Enter()
            {
                var scope = new BenchmarkMeasurementScope(
                    StructuredLogger.GlobalEnabled,
                    DebugConfig.LogVerification,
                    DebugConfig.LogFrameTiming);

                StructuredLogger.GlobalEnabled = false;
                DebugConfig.LogVerification = false;
                DebugConfig.LogFrameTiming = false;
                LogManager.Initialize(false, LogCategory.All, LogLevel.Error);
                return scope;
            }

            public void Dispose()
            {
                StructuredLogger.GlobalEnabled = _previousGlobalLoggingEnabled;
                DebugConfig.LogVerification = _previousLogVerification;
                DebugConfig.LogFrameTiming = _previousLogFrameTiming;

                if (_previousGlobalLoggingEnabled)
                {
                    try
                    {
                        LogManager.InitializeFromSettings();
                    }
                    catch
                    {
                        LogManager.Initialize(true, LogCategory.All, LogLevel.Debug);
                    }
                }
                else
                {
                    LogManager.Initialize(false, LogCategory.All, LogLevel.Error);
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
    }
}
