using System;
using System.Collections.Generic;
using System.Threading;
using FenBrowser.FenEngine.Rendering.Core;

namespace FenBrowser.FenEngine.Rendering.Performance
{
    public sealed record NavigationPerformanceSnapshot(
        string Url,
        DateTimeOffset NavigationStartedAtUtc,
        long HtmlParseMs,
        long CssAndStyleMs,
        long ScriptExecutionMs,
        long InitialVisualTreeMs,
        long TotalLoadMs,
        long ManagedAllocatedBytes,
        long ManagedHeapBytes,
        long WorkingSetBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections,
        int DomNodeCount,
        int LayoutObjectCount,
        int PaintCommandCount,
        double LayoutMs,
        double PaintGenerationMs,
        double RasterMs,
        bool UsedIncrementalLayout,
        int IncrementalLayoutRootCount,
        int DamageRegionCount,
        float DamageAreaRatio,
        RenderFrameRasterMode RasterMode);

    /// <summary>
    /// Bounded, process-local navigation diagnostics. Hot paths store numeric values;
    /// formatting and JSON serialization are deferred to the internal page.
    /// </summary>
    public static class PerformanceDiagnosticsStore
    {
        public const int MaximumNavigationHistory = 20;

        private static readonly object Sync = new();
        private static readonly List<NavigationPerformanceSnapshot> Navigations = new(MaximumNavigationHistory);
        private static RenderFrameTelemetry? _latestFrame;
        private static int _isRecording = 1;

        public static bool IsRecording => Volatile.Read(ref _isRecording) != 0;

        public static void StartRecording() => Volatile.Write(ref _isRecording, 1);

        public static void StopRecording() => Volatile.Write(ref _isRecording, 0);

        public static void Reset()
        {
            lock (Sync)
            {
                Navigations.Clear();
                _latestFrame = null;
            }
        }

        public static void RecordFrame(RenderFrameTelemetry? telemetry)
        {
            if (!IsRecording || telemetry == null)
            {
                return;
            }

            lock (Sync)
            {
                _latestFrame = telemetry;
                int index = Navigations.Count - 1;
                if (index >= 0 && UrlMatches(Navigations[index].Url, telemetry.Url))
                {
                    Navigations[index] = Navigations[index] with
                    {
                        DomNodeCount = telemetry.DomNodeCount,
                        LayoutObjectCount = telemetry.BoxCount,
                        PaintCommandCount = telemetry.PaintNodeCount,
                        LayoutMs = telemetry.LayoutDurationMs,
                        PaintGenerationMs = telemetry.PaintDurationMs,
                        RasterMs = telemetry.RasterDurationMs,
                        UsedIncrementalLayout = telemetry.UsedIncrementalLayout,
                        IncrementalLayoutRootCount = telemetry.IncrementalLayoutRootCount,
                        DamageRegionCount = telemetry.DamageRegionCount,
                        DamageAreaRatio = telemetry.DamageAreaRatio,
                        RasterMode = telemetry.RasterMode
                    };
                }
            }
        }

        public static void RecordNavigation(RenderTelemetrySnapshot? telemetry)
        {
            if (!IsRecording || telemetry == null || IsPerformancePage(telemetry.Url))
            {
                return;
            }

            lock (Sync)
            {
                var frame = UrlMatches(_latestFrame?.Url, telemetry.Url) ? _latestFrame : null;
                Navigations.Add(new NavigationPerformanceSnapshot(
                    telemetry.Url ?? "about:blank",
                    telemetry.NavigationStartedAtUtc,
                    telemetry.TokenizingAndParsingMs,
                    telemetry.CssAndStyleMs,
                    telemetry.ScriptExecutionMs,
                    telemetry.InitialVisualTreeMs,
                    telemetry.TotalRenderMs,
                    telemetry.ManagedAllocatedBytes,
                    telemetry.ManagedHeapBytes,
                    telemetry.WorkingSetBytes,
                    telemetry.Gen0Collections,
                    telemetry.Gen1Collections,
                    telemetry.Gen2Collections,
                    frame?.DomNodeCount ?? 0,
                    frame?.BoxCount ?? 0,
                    frame?.PaintNodeCount ?? 0,
                    frame?.LayoutDurationMs ?? 0,
                    frame?.PaintDurationMs ?? 0,
                    frame?.RasterDurationMs ?? 0,
                    frame?.UsedIncrementalLayout ?? false,
                    frame?.IncrementalLayoutRootCount ?? 0,
                    frame?.DamageRegionCount ?? 0,
                    frame?.DamageAreaRatio ?? 0,
                    frame?.RasterMode ?? RenderFrameRasterMode.None));

                if (Navigations.Count > MaximumNavigationHistory)
                {
                    Navigations.RemoveRange(0, Navigations.Count - MaximumNavigationHistory);
                }
            }
        }

        public static IReadOnlyList<NavigationPerformanceSnapshot> GetNavigationHistory()
        {
            lock (Sync)
            {
                return Navigations.ToArray();
            }
        }

        private static bool UrlMatches(string? left, string? right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPerformancePage(string? url)
        {
            return url?.StartsWith("fen://performance", StringComparison.OrdinalIgnoreCase) == true;
        }
    }
}
