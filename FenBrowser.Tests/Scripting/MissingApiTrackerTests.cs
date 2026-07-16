using System.Text.Json;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Tests.Logging;

namespace FenBrowser.Tests.Scripting;

[Collection(EngineLogTestCollection.Name)]
public sealed class MissingApiTrackerTests
{
    [Fact]
    public async Task MissingGlobalReference_WritesPerSiteJsonAndTrace()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var tracePath = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-trace-{Guid.NewGuid():N}.jsonl");
        var baseUri = new Uri("https://example.test/app/index.html");
        const string navigationId = "nav-missing-api-test";

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            ConfigureTrace(tracePath);

            var document = new HtmlParser(
                "<html>\n<body>\n<script>FenMissingGlobalProbe();</script>\n</body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.Sandbox = SandboxPolicy.AllowAll;

            using (EngineLogCompat.BeginCorrelationScope(navigationId, "missing-api-test", new Dictionary<string, object>
            {
                ["url"] = baseUri.AbsoluteUri
            }))
            {
                await engine.SetDomAsync(document.DocumentElement, baseUri);
            }

            DisableTrace();

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var outputRootElement = outputJson.RootElement;
            Assert.Equal("fenbrowser.missing-apis.v2", outputRootElement.GetProperty("schema").GetString());
            Assert.Equal("example.test", outputRootElement.GetProperty("siteKey").GetString());
            Assert.Equal(baseUri.AbsoluteUri, outputRootElement.GetProperty("siteUrl").GetString());

            var record = Assert.Single(outputRootElement.GetProperty("records").EnumerateArray());
            Assert.Equal("globalThis.FenMissingGlobalProbe", record.GetProperty("apiName").GetString());
            Assert.Equal("globalThis", record.GetProperty("objectOrPrototype").GetString());
            Assert.Equal("FenMissingGlobalProbe", record.GetProperty("propertyName").GetString());
            Assert.Equal(baseUri.AbsoluteUri, record.GetProperty("siteUrl").GetString());
            Assert.Equal(string.Empty, record.GetProperty("scriptUrl").GetString());
            Assert.Equal("script-1", record.GetProperty("scriptId").GetString());
            Assert.Equal(3, record.GetProperty("line").GetInt32());
            Assert.Equal(1, record.GetProperty("column").GetInt32());
            Assert.Equal(1, record.GetProperty("encounterCount").GetInt32());
            Assert.Equal("UNCLASSIFIED", record.GetProperty("classification").GetString());
            Assert.Equal("READ", record.GetProperty("operationKind").GetString());
            Assert.False(record.GetProperty("standardPriorityEligible").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("firstSeenTraceId").GetString()));
            Assert.Contains("ReferenceError: FenMissingGlobalProbe is not defined", record.GetProperty("exceptionText").GetString());

            var traceEvent = Assert.Single(LoadTraceEvents(tracePath));
            Assert.Equal("MissingAPI", traceEvent.GetProperty("category").GetString());
            Assert.Equal("MissingApiObserved", traceEvent.GetProperty("event").GetString());
            Assert.Equal(navigationId, traceEvent.GetProperty("nav_id").GetString());
            Assert.Equal("script-1", traceEvent.GetProperty("script_id").GetString());
            var traceData = traceEvent.GetProperty("data");
            Assert.Equal("globalThis.FenMissingGlobalProbe", traceData.GetProperty("apiName").GetString());
            Assert.Equal("UNCLASSIFIED", traceData.GetProperty("classification").GetString());
            Assert.Equal("READ", traceData.GetProperty("operationKind").GetString());
            Assert.False(traceData.GetProperty("standardPriorityEligible").GetBoolean());
            Assert.Equal(3, traceData.GetProperty("line").GetInt32());
            Assert.Equal(1, traceData.GetProperty("column").GetInt32());
        }
        finally
        {
            DisableTrace();
            MissingApiTracker.ResetForTests();
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            TryDeleteFile(tracePath);
            TryDeleteDirectory(outputRoot);
        }
    }

    [Fact]
    public void Classifier_RequiresPositiveEvidenceBeforeStandardsPrioritization()
    {
        var unknown = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "closure_uid"));
        var expando = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Element",
            "applicationState",
            AssignmentBeforeRead: true));
        var legacy = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Navigator",
            "msPointerEnabled"));
        var wrongReceiver = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "className"));
        var unresolvedReceiver = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "compareDocumentPosition",
            KnownWebIdlMember: true,
            DefinedInterface: "Node"));
        var standard = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "compareDocumentPosition"));
        var inheritedStandard = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "CharacterData",
            "childNodes"));
        var assignedStandard = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "compareDocumentPosition",
            MissingApiOperationKind.Write,
            AssignmentBeforeRead: true));

        Assert.Equal("UNCLASSIFIED", MissingApiClassifier.ToToken(unknown.Classification));
        Assert.Equal("SITE_EXPANDO", MissingApiClassifier.ToToken(expando.Classification));
        Assert.Equal("LEGACY_PROBE", MissingApiClassifier.ToToken(legacy.Classification));
        Assert.Equal("WRONG_RECEIVER", MissingApiClassifier.ToToken(wrongReceiver.Classification));
        Assert.Equal("UNCLASSIFIED", MissingApiClassifier.ToToken(unresolvedReceiver.Classification));
        Assert.Equal("STANDARD_API", MissingApiClassifier.ToToken(standard.Classification));
        Assert.Equal("STANDARD_API", MissingApiClassifier.ToToken(inheritedStandard.Classification));
        Assert.Equal("STANDARD_API", MissingApiClassifier.ToToken(assignedStandard.Classification));
        Assert.False(unknown.StandardPriorityEligible);
        Assert.False(expando.StandardPriorityEligible);
        Assert.False(legacy.StandardPriorityEligible);
        Assert.False(wrongReceiver.StandardPriorityEligible);
        Assert.False(unresolvedReceiver.StandardPriorityEligible);
        Assert.True(standard.StandardPriorityEligible);
        Assert.True(inheritedStandard.StandardPriorityEligible);
        Assert.True(assignedStandard.StandardPriorityEligible);
        Assert.Equal("Element", wrongReceiver.DefinedInterface);
        Assert.False(wrongReceiver.ReceiverMatchesDefinedInterface);
        Assert.Equal("Node", standard.DefinedInterface);
        Assert.True(standard.ReceiverMatchesDefinedInterface);
    }

    [Fact]
    public async Task MissingHostProperty_DeduplicatesByApiNameInPerSiteJson()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/");

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            DisableTrace();

            var document = new HtmlParser(
                "<html><body><div id=\"app\">ok</div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.Sandbox = SandboxPolicy.AllowAll;

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("undefined", engine.Evaluate("typeof document.fenMissingApiProbe")?.ToString());
            Assert.Equal("undefined", engine.Evaluate("typeof document.fenMissingApiProbe")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var record = Assert.Single(outputJson.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal("Document.fenMissingApiProbe", record.GetProperty("apiName").GetString());
            Assert.Equal("Document", record.GetProperty("objectOrPrototype").GetString());
            Assert.Equal("fenMissingApiProbe", record.GetProperty("propertyName").GetString());
            Assert.Equal(baseUri.AbsoluteUri, record.GetProperty("siteUrl").GetString());
            Assert.Equal(2, record.GetProperty("encounterCount").GetInt32());
            Assert.Equal("UNCLASSIFIED", record.GetProperty("classification").GetString());
            Assert.Equal("READ", record.GetProperty("operationKind").GetString());
            Assert.False(record.GetProperty("standardPriorityEligible").GetBoolean());
            Assert.False(string.IsNullOrWhiteSpace(record.GetProperty("firstSeenTraceId").GetString()));
        }
        finally
        {
            MissingApiTracker.ResetForTests();
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            TryDeleteDirectory(outputRoot);
        }
    }

    [Fact]
    public async Task HostExpandoAssignment_RecordsWriteBeforeSuccessfulRead()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/expando.html");

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            DisableTrace();

            var document = new HtmlParser(
                "<html><body><div id=\"app\">ok</div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.Sandbox = SandboxPolicy.AllowAll;

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("42", engine.Evaluate(
                "document.applicationState = 42; String(document.applicationState)")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var record = Assert.Single(outputJson.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal("Document.applicationState", record.GetProperty("apiName").GetString());
            Assert.Equal("SITE_EXPANDO", record.GetProperty("classification").GetString());
            Assert.Equal("WRITE", record.GetProperty("operationKind").GetString());
            Assert.True(record.GetProperty("assignmentBeforeRead").GetBoolean());
            Assert.False(record.GetProperty("standardPriorityEligible").GetBoolean());
        }
        finally
        {
            MissingApiTracker.ResetForTests();
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            TryDeleteDirectory(outputRoot);
        }
    }

    [Fact]
    public async Task KnownWebIdlHostMiss_RecordsReceiverEvidenceAndStandardsPriority()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/known-idl.html");

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            DisableTrace();

            var document = new HtmlParser("<html><body>ok</body></html>", baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.Sandbox = SandboxPolicy.AllowAll;

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("undefined", engine.Evaluate("typeof document.charset")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var record = Assert.Single(outputJson.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal("Document.charset", record.GetProperty("apiName").GetString());
            Assert.Equal("STANDARD_API", record.GetProperty("classification").GetString());
            Assert.Equal("READ", record.GetProperty("operationKind").GetString());
            Assert.True(record.GetProperty("knownWebIdlMember").GetBoolean());
            Assert.Equal("Document", record.GetProperty("definedInterface").GetString());
            Assert.True(record.GetProperty("receiverMatchesDefinedInterface").GetBoolean());
            Assert.True(record.GetProperty("standardPriorityEligible").GetBoolean());
        }
        finally
        {
            MissingApiTracker.ResetForTests();
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            TryDeleteDirectory(outputRoot);
        }
    }

    private static void ConfigureTrace(string tracePath)
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
    }

    private static void DisableTrace()
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
    }

    private static List<JsonElement> LoadTraceEvents(string tracePath)
    {
        var events = new List<JsonElement>();
        foreach (var line in File.ReadAllLines(tracePath))
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.GetProperty("category").GetString() == "MissingAPI")
            {
                events.Add(root.Clone());
            }
        }

        return events;
    }

    private static JsHostAdapter CreateHost()
    {
        return new JsHostAdapter(
            navigate: _ => { },
            post: (_, __) => { },
            status: _ => { },
            log: _ => { });
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
