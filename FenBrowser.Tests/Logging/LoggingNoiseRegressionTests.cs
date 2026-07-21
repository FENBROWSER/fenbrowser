using System;
using System.IO;
using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using Xunit;

namespace FenBrowser.Tests.Logging;

/// <summary>
/// Regression tests for the logging noise reduction refactor.
/// Covers preset precedence, severity policy, dedup bounding,
/// file sink behaviour, dispatcher reliability, and privacy redaction.
/// </summary>
[Collection(EngineLogTestCollection.Name)]
public sealed class LoggingNoiseRegressionTests
{
    // ── Phase 1: Preset / configuration tests ──

    [Fact]
    public void NormalPreset_UsesRingBufferOnly()
    {
        var opts = new EngineLoggingOptions();
        Assert.True(EngineLoggingPresets.Apply(EngineLoggingPresets.Normal, opts));

        Assert.True(opts.EnableRingBufferSink);
        Assert.False(opts.EnableConsoleSink);
        Assert.False(opts.EnableDebugSink);
        Assert.False(opts.EnableNdjsonSink);
        Assert.False(opts.EnableTraceSink);
        Assert.Equal(LogSeverity.Warn, opts.GlobalMinimumSeverity);
    }

    [Fact]
    public void CiPreset_DoesNotEnableConsole()
    {
        var opts = new EngineLoggingOptions();
        Assert.True(EngineLoggingPresets.Apply(EngineLoggingPresets.Ci, opts));

        Assert.False(opts.EnableConsoleSink);
        Assert.True(opts.EnableNdjsonSink);
        Assert.True(opts.EnableRingBufferSink);
    }

    [Fact]
    public void PerfPreset_DoesNotEnableConsole()
    {
        var opts = new EngineLoggingOptions();
        Assert.True(EngineLoggingPresets.Apply(EngineLoggingPresets.Perf, opts));

        Assert.False(opts.EnableConsoleSink);
        Assert.True(opts.EnableTraceSink);
    }

    [Fact]
    public void DeveloperPreset_PrefersDebuggerWhenAttached()
    {
        var opts = new EngineLoggingOptions();
        Assert.True(EngineLoggingPresets.Apply(EngineLoggingPresets.Developer, opts));

        // Both console and debug should not be true simultaneously.
        Assert.False(opts.EnableConsoleSink && opts.EnableDebugSink,
            "Developer preset must not enable both console and debug sinks");
    }

    [Fact]
    public void UnknownPreset_FallsBackToNormal()
    {
        var opts = new EngineLoggingOptions
        {
            EnableConsoleSink = true,
            EnableDebugSink = true,
            EnableNdjsonSink = true,
            EnableTraceSink = true
        };

        var applied = EngineLoggingPresets.Apply("bogus-preset-name", opts);
        Assert.False(applied);

        // Fall back to normal.
        Assert.True(EngineLoggingPresets.Apply(EngineLoggingPresets.Normal, opts));
        Assert.False(opts.EnableConsoleSink);
        Assert.False(opts.EnableNdjsonSink);
        Assert.True(opts.EnableRingBufferSink);
    }

    [Fact]
    public void UserSettings_CanDisablePresetSink()
    {
        var opts = new EngineLoggingOptions();
        EngineLoggingPresets.Apply(EngineLoggingPresets.TestRun, opts);

        // TestRun enables NDJSON by default.
        Assert.True(opts.EnableNdjsonSink);

        // Simulate user settings disabling file output.
        opts.EnableNdjsonSink = false;
        opts.EnableTraceSink = false;

        Assert.False(opts.EnableNdjsonSink);
        Assert.False(opts.EnableTraceSink);
    }

    [Fact]
    public void NormalizedLogSettings_HaveQuietDefaults()
    {
        var settings = new LogSettings();
        settings.Normalize();

        Assert.False(settings.LogToFile);
        Assert.False(settings.LogToDebug);
        Assert.Equal("normal", settings.LoggingPreset);
        Assert.Equal(5000, settings.MemoryBufferSize);
    }

    // ── Phase 2: Severity / classification tests ──

    [Fact]
    public void Classifier_StandardApiIsNotFiltered()
    {
        var result = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document", "charset"));

        Assert.Equal(MissingApiClassification.StandardApi, result.Classification);
        Assert.True(result.StandardPriorityEligible);
    }

    [Fact]
    public void Classifier_SiteExpandoIsFiltered()
    {
        var result = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "HTMLDivElement", "myCustomField",
            MissingApiOperationKind.Write,
            AssignmentObserved: true));

        Assert.Equal(MissingApiClassification.SiteExpando, result.Classification);
        Assert.False(result.StandardPriorityEligible);
    }

    [Fact]
    public void Classifier_LegacyProbeIsFiltered()
    {
        var result = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Navigator", "msPointerEnabled"));

        Assert.Equal(MissingApiClassification.LegacyProbe, result.Classification);
        Assert.False(result.StandardPriorityEligible);
    }

    [Fact]
    public void Classifier_WrongReceiverIsNotStandardsPriority()
    {
        var result = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document", "className"));

        Assert.Equal(MissingApiClassification.WrongReceiver, result.Classification);
        Assert.False(result.StandardPriorityEligible);
    }

    // ── Phase 6: Bounded collection tests ──

    [Fact]
    public void Deduplicator_TruncatesLongKeys()
    {
        var dedup = new EngineLogDeduplicator();
        var longKey = new string('x', 500);

        // Should not throw.
        var result = dedup.ShouldLogOncePerSession(longKey);
        Assert.True(result); // First call logs.

        var second = dedup.ShouldLogOncePerSession(longKey);
        Assert.False(second); // Second call suppressed (key was truncated).
    }

    [Fact]
    public void Deduplicator_SuppressedCountsAreBounded()
    {
        var dedup = new EngineLogDeduplicator();

        // Emit many unique keys that get suppressed.
        for (var i = 0; i < 100; i++)
        {
            var key = $"rate.key.{i}";
            dedup.ShouldLogRateLimited(key, TimeSpan.FromMinutes(10));
            dedup.ShouldLogRateLimited(key, TimeSpan.FromMinutes(10)); // Suppressed.
        }

        var counts = dedup.DrainSuppressedCounts();
        Assert.True(counts.Count <= 100); // Not unbounded.
    }

    // ── File sink tests ──

    [Fact]
    public void FileSink_FileIsReadableBetweenWrites()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-readable-{Guid.NewGuid():N}.jsonl");
        try
        {
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = true,
                GlobalMinimumSeverity = LogSeverity.Trace,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = true,
                TraceFilePath = tracePath
            });

            for (var i = 0; i < 16; i++)
            {
                EngineLog.Write(LogSubsystem.Verification, LogSeverity.Info,
                    $"readable-{i}", fields: new Dictionary<string, object> { ["n"] = i });
            }

            Assert.True(EngineLog.Flush(TimeSpan.FromSeconds(2)));

            // File must be readable immediately after flush.
            var lines = File.ReadAllLines(tracePath);
            Assert.Equal(16, lines.Length);

            // Each line must be valid JSON.
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                Assert.NotNull(doc);
            }
        }
        finally
        {
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = false,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = false
            });
            try { File.Delete(tracePath); } catch { }
        }
    }

    [Fact]
    public void FileSink_ErrorEventIsWrittenImmediately()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-error-flush-{Guid.NewGuid():N}.jsonl");
        try
        {
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = true,
                GlobalMinimumSeverity = LogSeverity.Trace,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = true,
                TraceFilePath = tracePath
            });

            EngineLog.Write(LogSubsystem.Js, LogSeverity.Error, "critical-error",
                LogMarker.EngineBug);

            Assert.True(EngineLog.Flush(TimeSpan.FromSeconds(2)));

            var lines = File.ReadAllLines(tracePath);
            Assert.Single(lines);

            using var doc = JsonDocument.Parse(lines[0]);
            Assert.Equal("ERROR", doc.RootElement.GetProperty("level").GetString());
        }
        finally
        {
            EngineLog.Configure(new EngineLoggingOptions
            {
                Enabled = false,
                EnableConsoleSink = false,
                EnableDebugSink = false,
                EnableNdjsonSink = false,
                EnableRingBufferSink = false,
                EnableTraceSink = false
            });
            try { File.Delete(tracePath); } catch { }
        }
    }

    // ── Dispatcher tests ──

    [Fact]
    public void QueueOverflow_DropsTraceBeforeWarn()
    {
        EngineLog.Configure(new EngineLoggingOptions
        {
            Enabled = true,
            GlobalMinimumSeverity = LogSeverity.Trace,
            EnableConsoleSink = false,
            EnableDebugSink = false,
            EnableNdjsonSink = false,
            EnableRingBufferSink = true,
            EnableTraceSink = false,
            DispatcherQueueCapacity = 4
        });
        EngineLog.ClearCompatibilityBuffer();

        // Fill the queue with trace events.
        for (var i = 0; i < 100; i++)
        {
            EngineLog.Write(LogSubsystem.Paint, LogSeverity.Trace, $"trace-{i}");
        }

        // Then write a warn — it should be accepted (emergency path).
        EngineLog.Write(LogSubsystem.Paint, LogSeverity.Warn, "important-warning");

        var entries = EngineLog.GetCompatibilityRecentEntries();
        Assert.Contains(entries, e => e.Message.Contains("important-warning"));
    }

    // ── Privacy / redaction tests ──

    [Fact]
    public void AuthorizationHeader_IsRedacted()
    {
        var fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["authorization"] = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.secret"
        };

        var result = LogFieldRedactor.Redact(fields);
        Assert.True(result.ContainsKey("authorization"));
        var val = result["authorization"] as string;
        Assert.NotNull(val);
        Assert.Contains("redacted", val);
    }

    [Fact]
    public void CookieValue_IsRedacted()
    {
        var str = "Cookie: session_id=abc123secret; path=/";
        var result = LogFieldRedactor.RedactStringContent(str);
        Assert.DoesNotContain("abc123secret", result);
    }

    [Fact]
    public void SensitiveQueryParameter_IsRedacted()
    {
        var str = "https://example.com/api?access_token=gho_secret123&other=ok";
        var result = LogFieldRedactor.RedactStringContent(str);
        Assert.DoesNotContain("gho_secret123", result);
    }

    [Fact]
    public void OrdinaryDiagnosticFields_ArePreserved()
    {
        var fields = new Dictionary<string, object>
        {
            ["url"] = "https://example.com/page",
            ["statusCode"] = 200,
            ["durationMs"] = 42L
        };

        var result = LogFieldRedactor.Redact(fields);
        Assert.Equal("https://example.com/page", result["url"]);
        Assert.Equal(200, result["statusCode"]);
    }

    [Fact]
    public void NullFields_AreNotRedacted()
    {
        var result = LogFieldRedactor.Redact(null);
        Assert.Null(result);
    }

    [Fact]
    public void EmptyFields_AreNotRedacted()
    {
        var fields = new Dictionary<string, object>();
        var result = LogFieldRedactor.Redact(fields);
        Assert.Empty(result);
    }

    // ── Console / debug separation test ──

    [Fact]
    public void ConsoleAndDebugSinks_DoNotDuplicateByDefault()
    {
        var opts = new EngineLoggingOptions();
        EngineLoggingPresets.Apply(EngineLoggingPresets.Normal, opts);

        Assert.False(opts.EnableConsoleSink);
        Assert.False(opts.EnableDebugSink);
    }
}
