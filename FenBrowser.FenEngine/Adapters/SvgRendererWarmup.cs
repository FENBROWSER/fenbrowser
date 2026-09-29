using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Adapters
{
    /// <summary>
    /// Renders one small document that reaches every major SVG path (parse,
    /// cascade with custom properties, paths, gradients, clipping, use, markers,
    /// text shaping, filters) on a thread-pool thread, so the first icon on the
    /// first page does not pay for JIT and native first-use initialization
    /// (about 120 ms cold before the warm-up existed).
    ///
    /// Selected by the cross-platform <c>FEN_SVG_WARMUP</c> flag: <c>on</c>
    /// (default) or <c>off</c>. The warm-up never blocks its caller, never throws,
    /// runs at most once per process and uses the default, sandboxed limits.
    /// </summary>
    public static class SvgRendererWarmup
    {
        public const string EnvironmentVariable = "FEN_SVG_WARMUP";
        public const string OnValue = "on";
        public const string OffValue = "off";

        internal const string Document =
            "<svg xmlns='http://www.w3.org/2000/svg' width='32' height='32' viewBox='0 0 32 32'>" +
            "<style>.w{fill:var(--w,currentColor)}</style>" +
            "<defs>" +
            "<linearGradient id='g'><stop offset='0' stop-color='#09f'/><stop offset='1' stop-color='#f90'/></linearGradient>" +
            "<clipPath id='c'><circle cx='16' cy='16' r='15'/></clipPath>" +
            "<filter id='f'><feGaussianBlur stdDeviation='1'/></filter>" +
            "<marker id='m' markerWidth='2' markerHeight='2' refX='1' refY='1'><circle cx='1' cy='1' r='1'/></marker>" +
            "<symbol id='s' viewBox='0 0 4 4'><rect width='4' height='4'/></symbol>" +
            "</defs>" +
            "<g clip-path='url(#c)' style='--w:#333;color:#333'>" +
            "<rect width='32' height='32' fill='url(#g)'/>" +
            "<path class='w' d='M4 4 L28 4 Q30 16 28 28 C16 30 8 30 4 28 A4 4 0 0 1 4 4 Z' marker-mid='url(#m)'/>" +
            "<use href='#s' x='2' y='2' width='6' height='6'/>" +
            "<rect x='20' y='20' width='8' height='8' filter='url(#f)'/>" +
            "<text x='4' y='20' font-size='8' font-family='sans-serif'>Ag</text>" +
            "</g></svg>";

        private static int _started;
        private static Task _completion = Task.CompletedTask;

        /// <summary>Completes when the warm-up finished, or immediately if it never ran.</summary>
        public static Task Completion => Volatile.Read(ref _completion);

        public static bool IsEnabled(string? flagValue)
        {
            string normalized = flagValue?.Trim() ?? string.Empty;
            return !string.Equals(normalized, OffValue, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Starts the warm-up once per process unless <c>FEN_SVG_WARMUP=off</c>.
        /// Returns the task that tracks it; callers are free to ignore it.
        /// </summary>
        public static Task Start() =>
            Start(Environment.GetEnvironmentVariable(EnvironmentVariable));

        internal static Task Start(string? flagValue)
        {
            if (!IsEnabled(flagValue) || Interlocked.Exchange(ref _started, 1) != 0)
            {
                return Completion;
            }

            var task = Task.Run(Run);
            Volatile.Write(ref _completion, task);
            return task;
        }

        private static void Run()
        {
            long started = Stopwatch.GetTimestamp();
            bool admitted = false;
            try
            {
                using var result = new FenSvgRenderer().Render(
                    new SvgRenderRequest(Document, SvgRenderLimits.Default) { DiagnosticSource = "warmup" });
                admitted = SvgRenderResult.IsAdmissible(result);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A warm-up failure only means the first real render stays cold.
            }

            if (SvgDiagnostics.Mode != SvgDiagnosticsMode.Off)
            {
                try
                {
                    EngineLog.Write(
                        LogSubsystem.Svg,
                        admitted ? LogSeverity.Info : LogSeverity.Warn,
                        "SVG renderer warm-up finished",
                        admitted ? LogMarker.None : LogMarker.Unexpected,
                        default,
                        new Dictionary<string, object>(StringComparer.Ordinal)
                        {
                            ["elapsedMs"] = Math.Round(Stopwatch.GetElapsedTime(started).TotalMilliseconds, 3),
                            ["admitted"] = admitted
                        });
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }
            }
        }

        /// <summary>Test isolation: allows the once-per-process warm-up to run again.</summary>
        internal static void ResetForTesting()
        {
            Volatile.Write(ref _started, 0);
            Volatile.Write(ref _completion, Task.CompletedTask);
        }
    }
}
