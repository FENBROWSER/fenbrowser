using System.Reflection;
using System.Text.Json;
using FenBrowser.Core.Logging;
using FenBrowser.Tooling;
using FenBrowser.Tests.Logging;

namespace FenBrowser.Tests.Tooling;

[Collection(EngineLogTestCollection.Name)]
public sealed class DebugSiteArtifactContractTests
{
    private static readonly string[] RequiredSupplementalArtifacts =
    {
        "compatibility_events.json",
        "ipc.json",
        "sandbox_denials.json",
        "performance.json"
    };

    [Fact]
    public void WriteBundle_AlwaysEmitsTypedSupplementalArtifactsAndManifestEntries()
    {
        CompatibilityEventRecorder.Clear();
        var programType = typeof(Program);
        var reportType = programType.GetNestedType("DebugSiteReport", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DebugSiteReport test seam was not found.");
        var report = Activator.CreateInstance(reportType, nonPublic: true)
            ?? throw new InvalidOperationException("DebugSiteReport could not be created.");
        SetProperty(reportType, report, "Url", "https://bundle-contract.invalid/");
        SetProperty(reportType, report, "FinalUrl", "https://bundle-contract.invalid/");

        var writeBundle = programType.GetMethod("WriteDebugSiteBundle", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("WriteDebugSiteBundle test seam was not found.");
        var bundlePath = (string?)writeBundle.Invoke(null, new[] { report })
            ?? throw new InvalidOperationException("Bundle writer did not return a path.");

        foreach (var name in RequiredSupplementalArtifacts)
        {
            Assert.True(File.Exists(Path.Combine(bundlePath, name)), $"Required artifact was not emitted: {name}");
        }

        using (var ipc = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundlePath, "ipc.json"))))
        {
            Assert.Equal(2, ipc.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("IMPLEMENTED", ipc.RootElement.GetProperty("status").GetString());
            Assert.Equal("active", ipc.RootElement.GetProperty("state").GetString());
            Assert.Equal("compatibility-event-recorder", ipc.RootElement.GetProperty("configuration").GetString());
            Assert.Equal("no-events", ipc.RootElement.GetProperty("eventState").GetString());
            Assert.Equal(0, ipc.RootElement.GetProperty("eventCount").GetInt32());
        }

        using (var compatibility = JsonDocument.Parse(
                   File.ReadAllText(Path.Combine(bundlePath, "compatibility_events.json"))))
        {
            Assert.Equal("fenbrowser.compatibility-events.v1", compatibility.RootElement.GetProperty("schema").GetString());
            Assert.Equal(0, compatibility.RootElement.GetProperty("eventCount").GetInt32());
        }
        AssertInactiveArtifact(Path.Combine(bundlePath, "sandbox_denials.json"), "denialState", "no-denials", "denialCount");

        using (var performance = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundlePath, "performance.json"))))
        {
            Assert.Equal(1, performance.RootElement.GetProperty("schemaVersion").GetInt32());
            Assert.Equal("partial", performance.RootElement.GetProperty("state").GetString());
            Assert.Equal(1, performance.RootElement.GetProperty("sampleCount").GetInt32());
        }

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundlePath, "artifact_manifest.json")));
        foreach (var name in RequiredSupplementalArtifacts)
        {
            var entry = Assert.Single(
                manifest.RootElement.EnumerateArray(),
                item => string.Equals(item.GetProperty("name").GetString(), name, StringComparison.Ordinal));
            Assert.True(entry.GetProperty("exists").GetBoolean(), $"Manifest did not mark {name} present.");
        }
    }

    [Fact]
    public void WriteBundle_ExportsCorrelatedBrokeredIpcEvidence()
    {
        CompatibilityEventRecorder.Clear();
        var correlationId = Guid.NewGuid();
        CompatibilityEventRecorder.Record(new EngineLogEvent(
            new EngineLogHeader(
                DateTimeOffset.UtcNow,
                1,
                Environment.ProcessId,
                Environment.CurrentManagedThreadId,
                LogSubsystem.Ipc,
                LogSeverity.Debug,
                LogMarker.None,
                Guid.NewGuid(),
                null,
                correlationId,
                new EngineLogContext(TabId: "9", TaskId: "task-9", Source: "renderer")),
            new EngineLogPayload
            {
                MessageTemplate = "Renderer command acknowledged",
                Fields = new Dictionary<string, object> { ["eventKind"] = "ipc.ack" }
            }));

        var programType = typeof(Program);
        var reportType = programType.GetNestedType("DebugSiteReport", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("DebugSiteReport test seam was not found.");
        var report = Activator.CreateInstance(reportType, nonPublic: true)
            ?? throw new InvalidOperationException("DebugSiteReport could not be created.");
        SetProperty(reportType, report, "Url", "https://brokered-evidence.invalid/");
        SetProperty(reportType, report, "FinalUrl", "https://brokered-evidence.invalid/");
        var writeBundle = programType.GetMethod("WriteDebugSiteBundle", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("WriteDebugSiteBundle test seam was not found.");
        var bundlePath = (string?)writeBundle.Invoke(null, new[] { report })
            ?? throw new InvalidOperationException("Bundle writer did not return a path.");

        using var ipc = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundlePath, "ipc.json")));
        Assert.Equal("captured", ipc.RootElement.GetProperty("state").GetString());
        Assert.Equal("brokered", ipc.RootElement.GetProperty("processMode").GetString());
        Assert.Equal(1, ipc.RootElement.GetProperty("eventCount").GetInt32());
        var captured = Assert.Single(ipc.RootElement.GetProperty("events").EnumerateArray());
        Assert.Equal("ipc.ack", captured.GetProperty("kind").GetString());
        Assert.Equal(correlationId.ToString("N"), captured.GetProperty("correlationId").GetString());
        Assert.Equal("9", captured.GetProperty("tabId").GetString());
        Assert.Equal("task-9", captured.GetProperty("taskId").GetString());
    }

    [Fact]
    public void WriteBundle_ContinuesAfterExceptionsArtifactExportFailure()
    {
        var diagnosticsRoot = Path.Combine(
            Path.GetTempPath(),
            "fenbrowser-bundle-export-" + Guid.NewGuid().ToString("N"));
        var previousDiagnosticsRoot = Environment.GetEnvironmentVariable("FEN_DIAGNOSTICS_DIR");
        try
        {
            Environment.SetEnvironmentVariable("FEN_DIAGNOSTICS_DIR", diagnosticsRoot);
            var siteRoot = Path.Combine(
                diagnosticsRoot,
                "logs",
                "real-site",
                "bundle-export-failure.invalid");
            var now = DateTime.UtcNow;
            for (var offsetSeconds = -2; offsetSeconds <= 5; offsetSeconds++)
            {
                var runId = now.AddSeconds(offsetSeconds)
                    .ToString("yyyyMMddTHHmmssZ", System.Globalization.CultureInfo.InvariantCulture);
                Directory.CreateDirectory(Path.Combine(siteRoot, runId, "exceptions.json"));
            }

            var programType = typeof(Program);
            var reportType = programType.GetNestedType("DebugSiteReport", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("DebugSiteReport test seam was not found.");
            var report = Activator.CreateInstance(reportType, nonPublic: true)
                ?? throw new InvalidOperationException("DebugSiteReport could not be created.");
            SetProperty(reportType, report, "Url", "https://bundle-export-failure.invalid/");
            SetProperty(reportType, report, "FinalUrl", "https://bundle-export-failure.invalid/");

            var writeBundle = programType.GetMethod("WriteDebugSiteBundle", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("WriteDebugSiteBundle test seam was not found.");
            var bundlePath = (string?)writeBundle.Invoke(null, new[] { report })
                ?? throw new InvalidOperationException("Bundle writer did not return a path.");

            Assert.True(File.Exists(Path.Combine(bundlePath, "event_loop.json")));
            Assert.True(File.Exists(Path.Combine(bundlePath, "first_blocker.json")));
            using (var firstBlocker = JsonDocument.Parse(
                       File.ReadAllText(Path.Combine(bundlePath, "first_blocker.json"))))
            {
                Assert.Equal(
                    "insufficient-evidence",
                    firstBlocker.RootElement.GetProperty("Result").GetString());
                Assert.Equal(
                    "ArtifactCompleteness",
                    firstBlocker.RootElement.GetProperty("MilestoneBlocked").GetString());
                Assert.Contains(
                    firstBlocker.RootElement
                        .GetProperty("EvidenceQualityWarnings")
                        .EnumerateArray()
                        .Select(item => item.GetString()),
                    warning => string.Equals(
                        warning,
                        "Required artifact missing: exceptions.json.",
                        StringComparison.Ordinal));
            }

            using var manifest = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(bundlePath, "artifact_manifest.json")));
            var exceptionEntry = Assert.Single(
                manifest.RootElement.EnumerateArray(),
                item => string.Equals(
                    item.GetProperty("name").GetString(),
                    "exceptions.json",
                    StringComparison.Ordinal));
            Assert.False(exceptionEntry.GetProperty("exists").GetBoolean());
            Assert.Contains(
                "UnauthorizedAccessException",
                exceptionEntry.GetProperty("exportError").GetString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("FEN_DIAGNOSTICS_DIR", previousDiagnosticsRoot);
            if (Directory.Exists(diagnosticsRoot))
            {
                Directory.Delete(diagnosticsRoot, recursive: true);
            }
        }
    }

    private static void AssertInactiveArtifact(
        string path,
        string dispositionProperty,
        string expectedDisposition,
        string countProperty)
    {
        using var artifact = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(1, artifact.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("inactive", artifact.RootElement.GetProperty("state").GetString());
        Assert.Equal("not-configured", artifact.RootElement.GetProperty("configuration").GetString());
        Assert.Equal(expectedDisposition, artifact.RootElement.GetProperty(dispositionProperty).GetString());
        Assert.Equal(0, artifact.RootElement.GetProperty(countProperty).GetInt32());
    }

    private static void SetProperty(Type reportType, object report, string name, string value)
    {
        var property = reportType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"DebugSiteReport.{name} was not found.");
        property.SetValue(report, value);
    }
}
