using System.Reflection;
using System.Text.Json;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tests.Logging;
using FenBrowser.Tooling;

namespace FenBrowser.Tests.Tooling;

[Collection(EngineLogTestCollection.Name)]
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

    [Fact]
    public void WriteBundle_PreservesBoundedRichMissingApiSnapshot()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-api-bundle-{Guid.NewGuid():N}");
        var siteUri = new Uri("https://bundle-missing-api.invalid/page.html");
        string? bundlePath = null;

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            MissingApiTracker.Record(new MissingApiObservation
            {
                ApiName = "Document.applicationState",
                ObjectOrPrototype = "Document",
                PropertyName = "applicationState",
                ReceiverType = "Document",
                SiteUrl = siteUri.AbsoluteUri,
                ScriptUrl = "https://bundle-missing-api.invalid/app.js",
                ScriptId = "script-7",
                NavigationId = "nav-3",
                Line = 12,
                Column = 9,
                Reason = "host property assigned before read",
                OperationKind = MissingApiOperationKind.Write,
                AssignmentBeforeRead = true
            });

            var getSnapshot = typeof(MissingApiTracker).GetMethod(
                "GetSnapshot",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("MissingApiTracker.GetSnapshot test seam was not found.");
            var snapshot = getSnapshot.Invoke(null, new object?[] { siteUri.AbsoluteUri })
                ?? throw new InvalidOperationException("Missing API snapshot was not returned.");

            var programType = typeof(Program);
            var reportType = programType.GetNestedType("DebugSiteReport", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("DebugSiteReport test seam was not found.");
            var report = Activator.CreateInstance(reportType, nonPublic: true)
                ?? throw new InvalidOperationException("DebugSiteReport could not be created.");
            SetProperty(reportType, report, "Url", siteUri.AbsoluteUri);
            SetProperty(reportType, report, "FinalUrl", siteUri.AbsoluteUri);
            SetProperty(reportType, report, "MissingApiSnapshot", snapshot);

            var writeBundle = programType.GetMethod(
                "WriteDebugSiteBundle",
                BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("WriteDebugSiteBundle test seam was not found.");
            bundlePath = (string?)writeBundle.Invoke(null, new[] { report })
                ?? throw new InvalidOperationException("Bundle writer did not return a path.");

            Assert.Contains(
                "First missing API: Document.applicationState",
                File.ReadAllText(Path.Combine(bundlePath, "summary.md")),
                StringComparison.Ordinal);
            using var artifact = JsonDocument.Parse(File.ReadAllText(Path.Combine(bundlePath, "missing_apis.json")));
            var root = artifact.RootElement;
            Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(1, root.GetProperty("totalRecordCount").GetInt32());
            Assert.Equal(1, root.GetProperty("retainedRecordCount").GetInt32());
            Assert.False(root.GetProperty("truncated").GetBoolean());
            var record = Assert.Single(root.GetProperty("records").EnumerateArray());
            Assert.Equal("Document.applicationState", record.GetProperty("apiName").GetString());
            Assert.Equal("SITE_EXPANDO", record.GetProperty("classification").GetString());
            Assert.Equal("WRITE", record.GetProperty("operationKind").GetString());
            Assert.Equal("Document", record.GetProperty("receiverType").GetString());
            Assert.Equal("script-7", record.GetProperty("scriptId").GetString());
            Assert.Equal("nav-3", record.GetProperty("navigationId").GetString());
            Assert.Equal(12, record.GetProperty("line").GetInt32());
            Assert.Equal(9, record.GetProperty("column").GetInt32());
            Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("firstSeenTraceId").GetString()));
        }
        finally
        {
            MissingApiTracker.ResetForTests();
            try
            {
                if (!string.IsNullOrWhiteSpace(bundlePath) && Directory.Exists(bundlePath))
                {
                    Directory.Delete(bundlePath, recursive: true);
                }

                var siteBundleDirectory = string.IsNullOrWhiteSpace(bundlePath)
                    ? null
                    : Directory.GetParent(bundlePath)?.FullName;
                if (!string.IsNullOrWhiteSpace(siteBundleDirectory) &&
                    Directory.Exists(siteBundleDirectory) &&
                    !Directory.EnumerateFileSystemEntries(siteBundleDirectory).Any())
                {
                    Directory.Delete(siteBundleDirectory);
                }

                if (Directory.Exists(outputRoot))
                {
                    Directory.Delete(outputRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private static void SetProperty(Type reportType, object report, string name, object value)
    {
        var property = reportType.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"DebugSiteReport.{name} was not found.");
        property.SetValue(report, value);
    }
}
