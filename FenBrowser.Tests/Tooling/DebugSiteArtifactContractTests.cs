using System.Reflection;
using System.Text.Json;
using FenBrowser.Tooling;
using FenBrowser.Tests.Logging;

namespace FenBrowser.Tests.Tooling;

[Collection(EngineLogTestCollection.Name)]
public sealed class DebugSiteArtifactContractTests
{
    private static readonly string[] RequiredSupplementalArtifacts =
    {
        "ipc.json",
        "sandbox_denials.json",
        "performance.json"
    };

    [Fact]
    public void WriteBundle_AlwaysEmitsTypedSupplementalArtifactsAndManifestEntries()
    {
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

        AssertInactiveArtifact(Path.Combine(bundlePath, "ipc.json"), "eventState", "no-events", "eventCount");
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
