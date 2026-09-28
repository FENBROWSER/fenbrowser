using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Adapters;
using Xunit;

namespace FenBrowser.Tests.Logging
{
    /// <summary>
    /// Runs in the engine-log collection because it reconfigures the process-wide
    /// log. Every render carries a per-test DiagnosticSource so parallel SVG tests
    /// that also render cannot be mistaken for this test's events.
    /// </summary>
    [Collection(EngineLogTestCollection.Name)]
    public sealed class SvgDiagnosticsTests
    {
        private const string Admitted =
            "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='4'><rect width='8' height='4'/></svg>";
        private const string Rejected =
            "<svg xmlns='http://www.w3.org/2000/svg' width='8' height='8'><script>secret()</script></svg>";

        [Fact]
        public void Off_EmitsNothing()
        {
            var events = Capture(SvgDiagnosticsMode.Off, source => Render(Rejected, source));

            Assert.Empty(events);
        }

        [Fact]
        public void Failures_EmitsOneWarnPerRejectedRender_WithoutSource()
        {
            var events = Capture(SvgDiagnosticsMode.Failures, source =>
            {
                Render(Rejected, source);
                Render(Admitted, source);
            });

            var rejected = Assert.Single(events);
            Assert.Equal(LogSeverity.Warn, rejected.Header.Severity);
            Assert.Equal(LogMarker.Fallback, rejected.Header.Marker);
            var fields = rejected.Payload.Fields;
            Assert.Contains("dynamic-content", (string)fields["fallbackReasonCodes"]);
            Assert.Equal(Rejected.Length, (int)fields["sourceChars"]);
            Assert.Matches("^[0-9a-f]{16}$", (string)fields["sourceSha256"]);
            Assert.DoesNotContain(fields.Values, value => value is string text &&
                (text.Contains("<svg", StringComparison.Ordinal) || text.Contains("secret", StringComparison.Ordinal)));
        }

        [Fact]
        public void Failures_RateLimitsRepeatedRejections()
        {
            var events = Capture(SvgDiagnosticsMode.Failures, source =>
            {
                Render(Rejected, source);
                Render(Rejected, source);
                Render(Rejected, source);
            });

            Assert.Single(events);
        }

        [Fact]
        public void Verbose_AlsoEmitsAdmittedRenders()
        {
            var events = Capture(SvgDiagnosticsMode.Verbose, source => Render(Admitted, source));

            var admitted = Assert.Single(events);
            Assert.Equal(LogSeverity.Info, admitted.Header.Severity);
            Assert.Equal(8, (int)admitted.Payload.Fields["rasterWidth"]);
            Assert.Equal(4, (int)admitted.Payload.Fields["rasterHeight"]);
        }

        [Theory]
        [InlineData(null, SvgDiagnosticsMode.Off, true)]
        [InlineData("", SvgDiagnosticsMode.Off, true)]
        [InlineData("off", SvgDiagnosticsMode.Off, true)]
        [InlineData(" Failures ", SvgDiagnosticsMode.Failures, true)]
        [InlineData("VERBOSE", SvgDiagnosticsMode.Verbose, true)]
        [InlineData("on", SvgDiagnosticsMode.Off, false)]
        [InlineData("1", SvgDiagnosticsMode.Off, false)]
        public void TryParse_AcceptsOnlyNamedModesAndFailsQuiet(
            string? value, SvgDiagnosticsMode expected, bool recognized)
        {
            Assert.Equal(recognized, SvgDiagnostics.TryParse(value, out var mode));
            Assert.Equal(expected, mode);
        }

        [Theory]
        [InlineData(null, "unspecified")]
        [InlineData("Inline SVG", "inline-svg")]
        [InlineData("image\r\nforged: yes", "image--forged--yes")]
        [InlineData("abcdefghijklmnopqrstuvwxyz0123456789", "abcdefghijklmnopqrstuvwxyz012345")]
        public void SourceLabels_AreSanitizedAndBounded(string? label, string expected)
        {
            Assert.Equal(expected, SvgDiagnostics.SanitizeSource(label));
        }

        private static void Render(string svg, string source)
        {
            using var result = new FenSvgRenderer().Render(
                new SvgRenderRequest(svg, SvgRenderLimits.Default) { DiagnosticSource = source });
        }

        private static List<EngineLogEvent> Capture(SvgDiagnosticsMode mode, Action<string> act)
        {
            string source = "test-" + Guid.NewGuid().ToString("N")[..12];
            var events = new List<EngineLogEvent>();
            void OnEvent(EngineLogEvent evt)
            {
                if (evt.Header.Subsystem == LogSubsystem.Svg &&
                    evt.Payload.Fields != null &&
                    evt.Payload.Fields.TryGetValue("source", out var value) &&
                    Equals(value, source))
                {
                    lock (events) events.Add(evt);
                }
            }

            var previous = SvgDiagnostics.Mode;
            EngineLog.ClearEnsuredSubsystemLevels();
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = true,
                GlobalMinimumSeverity = LogSeverity.Error,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = true,
                EnableTraceSink = false
            });
            SvgDiagnostics.ResetForTesting(mode);
            EngineLog.EngineEventWritten += OnEvent;
            try
            {
                act(source);
            }
            finally
            {
                EngineLog.EngineEventWritten -= OnEvent;
                SvgDiagnostics.ResetForTesting(previous);
                EngineLog.ClearEnsuredSubsystemLevels();
                EngineLog.Configure(new EngineLoggingOptions
                {
                    Enabled = true,
                    GlobalMinimumSeverity = LogSeverity.Warn,
                    EnableConsoleSink = false,
                    EnableDebugSink = false,
                    EnableNdjsonSink = false,
                    EnableRingBufferSink = true,
                    EnableTraceSink = false
                });
            }

            return events;
        }
    }
}
