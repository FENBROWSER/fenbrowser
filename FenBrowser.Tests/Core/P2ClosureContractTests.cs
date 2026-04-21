using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Deadlines;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;
using FenBrowser.Core.Verification;
using FenBrowser.FenEngine.Adapters;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class P2ClosureContractTests
    {
        [Fact]
        public void DebugConfig_ShouldLog_UsesNormalizedFilters_AndResetRestoresDefaults()
        {
            DebugConfig.ResetToDefaults();
            DebugConfig.EnableDeepDebug = true;
            DebugConfig.LogAllClasses = false;
            DebugConfig.DebugClasses = new[] { " Search-Box ", "", "search-box", "hero" };

            Assert.True(DebugConfig.ShouldLog("primary search-box chrome"));
            Assert.True(DebugConfig.ShouldLog("hero"));
            Assert.False(DebugConfig.ShouldLog("footer"));

            DebugConfig.LogAllClasses = true;
            Assert.True(DebugConfig.ShouldLog("anything"));

            DebugConfig.ResetToDefaults();
        }

        [Fact]
        public void ParserSecurityPolicy_NormalizesInvalidValues_AndClones()
        {
            var policy = new ParserSecurityPolicy
            {
                HtmlMaxTokenEmissions = 0,
                HtmlMaxOpenElementsDepth = -1,
                CssMaxRules = 0,
                CssMaxDeclarationsPerBlock = -10
            };

            Assert.Equal(2_000_000, policy.HtmlMaxTokenEmissions);
            Assert.Equal(4096, policy.HtmlMaxOpenElementsDepth);
            Assert.Equal(200_000, policy.CssMaxRules);
            Assert.Equal(8192, policy.CssMaxDeclarationsPerBlock);

            var clone = policy.Clone();
            clone.CssMaxRules = 4;

            Assert.Equal(200_000, policy.CssMaxRules);
            Assert.Equal(4, clone.CssMaxRules);
        }

        [Fact]
        public void FrameDeadline_RejectsInvalidBudget_AndExposesContext()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new FrameDeadline(-1, "bad"));
            Assert.Throws<ArgumentOutOfRangeException>(() => new FrameDeadline(double.NaN, "bad"));

            var deadline = new FrameDeadline(10, "  Parse Phase  ");

            Assert.Equal(10, deadline.BudgetMs);
            Assert.Equal("Parse Phase", deadline.ContextName);
            Assert.True(deadline.TryCheck());
            Assert.Contains("Parse Phase", deadline.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void LogCategoryFacts_ExposeDefaultOperationalMask()
        {
            Assert.True(LogCategoryFacts.DefaultOperationalCategories.Includes(LogCategory.Navigation));
            Assert.True(LogCategoryFacts.DefaultOperationalCategories.Includes(LogCategory.Security));
            Assert.True(LogCategory.Rendering.IsSingleCategory());
            Assert.False((LogCategory.Rendering | LogCategory.Network).IsSingleCategory());
        }

        [Fact]
        public void RendererSafetyPolicy_NormalizesNonFiniteBudgets_ButPreservesNegativeSentinels()
        {
            var policy = new RendererSafetyPolicy
            {
                MaxFrameBudgetMs = double.NaN,
                MaxPaintStageMs = double.PositiveInfinity,
                MaxRasterStageMs = -1
            };

            Assert.Equal(16.67, policy.MaxFrameBudgetMs, 2);
            Assert.Equal(12.0, policy.MaxPaintStageMs, 2);
            Assert.Equal(-1, policy.MaxRasterStageMs, 2);

            var clone = policy.Clone();
            clone.MaxFrameBudgetMs = 5;
            Assert.NotEqual(clone.MaxFrameBudgetMs, policy.MaxFrameBudgetMs);
        }

        [Fact]
        public void BaseFrameReusePolicy_RejectsInvalidViewports()
        {
            Assert.False(BaseFrameReusePolicy.CanReuseBaseFrame(
                hasBaseFrame: true,
                previousViewport: new SKSize(float.NaN, 600),
                currentViewport: new SKSize(800, 600),
                previousScrollY: 0,
                currentScrollY: 0));
        }

        [Fact]
        public void RenderContext_NormalizesNullCollections_AndInvalidViewport()
        {
            var context = new RenderContext
            {
                Styles = null,
                Boxes = null,
                PaintTreeRoots = null,
                ViewportWidth = float.NaN,
                ViewportHeight = -1,
                Viewport = new SKRect(float.NaN, 0, 10, 10),
                BaseUrl = "  https://fenbrowser.dev  "
            };

            Assert.Empty(context.Styles);
            Assert.Empty(context.Boxes);
            Assert.Empty(context.PaintTreeRoots);
            Assert.False(context.HasViewport);
            Assert.Equal(SKRect.Empty, context.Viewport);
            Assert.Equal("https://fenbrowser.dev", context.BaseUrl);
            Assert.False(context.TryGetBox(new Text("x"), out _));
        }

        [Fact]
        public void HistoryEntry_NormalizesTitle_AndRequiresUrl()
        {
            Assert.Throws<ArgumentNullException>(() => new HistoryEntry(null));

            var entry = new HistoryEntry(new Uri("https://fenbrowser.dev/docs"), "  Docs  ");

            Assert.Equal("Docs", entry.Title);
            Assert.False(entry.HasState);
            Assert.Contains("https://fenbrowser.dev/docs", entry.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public void PositionedGlyph_NormalizesConstructorCoordinates()
        {
            var glyph = new PositionedGlyph(12, float.NaN, float.PositiveInfinity);

            Assert.Equal(0f, glyph.X);
            Assert.Equal(0f, glyph.Y);
            Assert.True(glyph.IsRenderable);
        }

        [Fact]
        public void SkiaTextMeasurer_ReturnsZero_ForInvalidFontSizes()
        {
            var measurer = new SkiaTextMeasurer();

            Assert.Equal(0f, measurer.MeasureWidth("fen", "Arial", 0));
            Assert.Equal(0f, measurer.GetLineHeight("Arial", -10));
        }

        [Fact]
        public void DiagnosticPaths_UsesWorkspaceRootLogs_WhenDiagnosticsRootEnvIsSet()
        {
            const string envName = "FEN_DIAGNOSTICS_DIR";
            string original = Environment.GetEnvironmentVariable(envName);
            string tempDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fenbrowser-diagnostics-tests", Guid.NewGuid().ToString("N"));

            try
            {
                Environment.SetEnvironmentVariable(envName, tempDirectory);

                string workspaceRoot = DiagnosticPaths.GetWorkspaceRoot();
                string logsDirectory = DiagnosticPaths.GetLogsDirectory();

                Assert.Equal(tempDirectory, workspaceRoot);
                Assert.Equal(System.IO.Path.Combine(tempDirectory, "logs"), logsDirectory);
            }
            finally
            {
                Environment.SetEnvironmentVariable(envName, original);
                if (System.IO.Directory.Exists(tempDirectory))
                {
                    System.IO.Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }

        [Fact]
        public void EngineLogCompat_UsesDiagnosticsRootLogs_WhenEnvOverrideIsSet()
        {
            const string envName = "FEN_DIAGNOSTICS_DIR";
            string original = Environment.GetEnvironmentVariable(envName);
            string tempDirectory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fenbrowser-structured-logger-tests", Guid.NewGuid().ToString("N"));

            try
            {
                Environment.SetEnvironmentVariable(envName, tempDirectory);
                EngineLogCompat.Initialize(DiagnosticPaths.GetLogsDirectory());

                string dumpPath = EngineLogCompat.DumpRawSource("https://fenbrowser.dev", "<html></html>");

                Assert.False(string.IsNullOrWhiteSpace(dumpPath));
                Assert.StartsWith(System.IO.Path.Combine(tempDirectory, "logs"), dumpPath, StringComparison.OrdinalIgnoreCase);
                Assert.True(System.IO.File.Exists(dumpPath));
            }
            finally
            {
                Environment.SetEnvironmentVariable(envName, original);
                if (System.IO.Directory.Exists(tempDirectory))
                {
                    System.IO.Directory.Delete(tempDirectory, recursive: true);
                }
            }
        }

        [Fact]
        public void ContentVerifier_AssessContentHealth_DistinguishesCorroboratedLargePages_FromRealFailures()
        {
            Assert.Equal(
                ContentHealthDisposition.ToleratedLowRatio,
                ContentVerifier.AssessContentHealth(
                    sourceLengthBytes: 1_032_931,
                    renderedTextLength: 210,
                    domNodeCount: 518,
                    screenshotSaved: true,
                    cssRuleCount: 385));

            Assert.Equal(
                ContentHealthDisposition.SuspiciousLowRatio,
                ContentVerifier.AssessContentHealth(
                    sourceLengthBytes: 50_000,
                    renderedTextLength: 10,
                    domNodeCount: 8,
                    screenshotSaved: false,
                    cssRuleCount: 0));
        }

        [Fact]
        public void EngineLog_Deduplicator_OncePerDocument_AndRateLimit_Work()
        {
            LogManager.Initialize(true, LogCategory.All, LogLevel.Debug);
            LogManager.ClearLogs();

            string docId = Guid.NewGuid().ToString("N");
            var context = new EngineLogContext(DocumentId: docId, Url: "https://fenbrowser.dev/dedup");

            EngineLog.WriteOncePerDocument(
                documentId: docId,
                key: "unsupported:subgrid",
                subsystem: LogSubsystem.Style,
                severity: LogSeverity.Warn,
                message: "Unsupported subgrid",
                marker: LogMarker.Unimplemented,
                context: context);

            EngineLog.WriteOncePerDocument(
                documentId: docId,
                key: "unsupported:subgrid",
                subsystem: LogSubsystem.Style,
                severity: LogSeverity.Warn,
                message: "Unsupported subgrid",
                marker: LogMarker.Unimplemented,
                context: context);

            EngineLog.WriteRateLimited(
                key: "rate:layout",
                window: TimeSpan.FromHours(1),
                subsystem: LogSubsystem.Layout,
                severity: LogSeverity.Info,
                message: "Layout summary",
                context: context);

            EngineLog.WriteRateLimited(
                key: "rate:layout",
                window: TimeSpan.FromHours(1),
                subsystem: LogSubsystem.Layout,
                severity: LogSeverity.Info,
                message: "Layout summary",
                context: context);

            var logs = LogManager.GetRecentLogs();
            Assert.Equal(2, logs.Count(entry =>
                entry.Message.Contains("Unsupported subgrid", StringComparison.Ordinal) ||
                entry.Message.Contains("Layout summary", StringComparison.Ordinal)));
        }

        [Fact]
        public void EngineLog_ExportFailureBundle_WritesSummaryAndLogs()
        {
            LogManager.Initialize(true, LogCategory.All, LogLevel.Debug);
            LogManager.ClearLogs();

            EngineLog.Write(
                LogSubsystem.Js,
                LogSeverity.Error,
                "Script failure",
                LogMarker.EngineBug,
                new EngineLogContext(TestId: "wpt/example.html", Url: "https://fenbrowser.dev"));

            string bundlePath = EngineLog.ExportFailureBundle(
                testId: "wpt/example.html",
                url: "https://fenbrowser.dev",
                summary: "synthetic failure bundle for test");

            Assert.True(System.IO.Directory.Exists(bundlePath));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(bundlePath, "summary.json")));
            Assert.True(System.IO.File.Exists(System.IO.Path.Combine(bundlePath, "logs.ndjson")));
        }

        [Fact]
        public void EngineLog_PerDocumentCounters_AggregateByDocumentId()
        {
            LogManager.Initialize(true, LogCategory.All, LogLevel.Debug);
            LogManager.ClearLogs();

            var context = new EngineLogContext(DocumentId: "doc-counter-1", Url: "https://fenbrowser.dev/counter", TabId: "2", FrameId: "f-1");
            EngineLog.Write(LogSubsystem.Layout, LogSeverity.Info, "layout-ok", LogMarker.None, context);
            EngineLog.Write(LogSubsystem.Layout, LogSeverity.Warn, "layout-warn", LogMarker.Partial, context);
            EngineLog.Write(LogSubsystem.Layout, LogSeverity.Error, "layout-error", LogMarker.EngineBug, context);

            var counters = EngineLog.GetPerDocumentCounters();
            var match = counters.FirstOrDefault(c => c.DocumentKey == "doc-counter-1");

            Assert.Equal(3, match.TotalCount);
            Assert.Equal(2, match.WarnCount);
            Assert.Equal(1, match.ErrorCount);
            Assert.Equal("2", match.LastTabId);
            Assert.Equal("f-1", match.LastFrameId);
        }

        [Fact]
        public void EngineLoggingPresets_Apply_TestRunPreset_ConfiguresExpectedLevels()
        {
            var options = new EngineLoggingOptions
            {
                GlobalMinimumSeverity = LogSeverity.Debug,
                EnableTraceSink = true
            };

            var applied = EngineLoggingPresets.Apply(EngineLoggingPresets.TestRun, options);

            Assert.True(applied);
            Assert.Equal(LogSeverity.Warn, options.GlobalMinimumSeverity);
            Assert.Equal(LogSeverity.Info, options.SubsystemOverrides[LogSubsystem.Style]);
            Assert.Equal(LogSeverity.Info, options.SubsystemOverrides[LogSubsystem.Layout]);
            Assert.False(options.EnableTraceSink);
        }
    }
}
