using System.Reflection;
using System.Text.Json;
using FenBrowser.Core.Logging;
using FenBrowser.Tooling;

namespace FenBrowser.Tests.Tooling;

public sealed class DebugSiteMissingApiClassificationTests
{
    [Fact]
    public void Projection_ExcludesLegacyAndUnclassifiedReadsFromStandardPriority()
    {
        var features = new[]
        {
            new FeatureInfo
            {
                Name = "Navigator.msPointerEnabled",
                Status = FeatureStatus.Unsupported,
                Reason = "missing host property",
                EncounterCount = 2
            },
            new FeatureInfo
            {
                Name = "Document.closure_uid",
                Status = FeatureStatus.Unsupported,
                Reason = "missing host property",
                EncounterCount = 1
            },
            new FeatureInfo
            {
                Name = "Document.charset",
                Status = FeatureStatus.Unsupported,
                Reason = "missing host property",
                EncounterCount = 1
            }
        };
        var extract = typeof(Program).GetMethod(
            "ExtractMissingApiRecords",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("ExtractMissingApiRecords test seam was not found.");

        var projection = extract.Invoke(null, new object?[] { Array.Empty<string>(), features })
            ?? throw new InvalidOperationException("Missing API projection was not created.");
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(
            projection,
            projection.GetType(),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        var records = json.RootElement.EnumerateArray().ToDictionary(
            record => record.GetProperty("api").GetString()!,
            StringComparer.Ordinal);
        Assert.Equal("LEGACY_PROBE", records["Navigator.msPointerEnabled"].GetProperty("classification").GetString());
        Assert.Equal("UNCLASSIFIED", records["Document.closure_uid"].GetProperty("classification").GetString());
        Assert.Equal("STANDARD_API", records["Document.charset"].GetProperty("classification").GetString());
        Assert.True(records["Document.charset"].GetProperty("standardPriorityEligible").GetBoolean());
        Assert.All(records.Values, record =>
        {
            Assert.Equal("READ", record.GetProperty("operationKind").GetString());
        });
        Assert.False(records["Navigator.msPointerEnabled"].GetProperty("standardPriorityEligible").GetBoolean());
        Assert.False(records["Document.closure_uid"].GetProperty("standardPriorityEligible").GetBoolean());
    }
}
