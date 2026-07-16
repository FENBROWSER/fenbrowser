using System.Reflection;
using System.Text.Json;
using FenBrowser.Tooling;

namespace FenBrowser.Tests.Tooling;

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
