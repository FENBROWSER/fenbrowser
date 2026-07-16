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
        var assignedStandardAfterRead = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "compareDocumentPosition",
            MissingApiOperationKind.Write,
            AssignmentObserved: true));
        var assignedWrongReceiver = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "className",
            MissingApiOperationKind.Write,
            AssignmentObserved: true));
        var prototypeMarker = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "HTMLDivElement",
            "protocolMarker",
            FunctionPrototypeMarkerObserved: true));
        var prototypeMarkerStandard = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "compareDocumentPosition",
            FunctionPrototypeMarkerObserved: true));
        var prototypeMarkerWrongReceiver = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "className",
            FunctionPrototypeMarkerObserved: true));
        var descriptorOnInstance = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "compareDocumentPosition",
            MissingApiOperationKind.DescriptorOperation));
        var descriptorOnPrototype = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "compareDocumentPosition",
            MissingApiOperationKind.DescriptorOperation,
            DescriptorTargetIsPrototype: true));

        Assert.Equal("UNCLASSIFIED", MissingApiClassifier.ToToken(unknown.Classification));
        Assert.Equal("SITE_EXPANDO", MissingApiClassifier.ToToken(expando.Classification));
        Assert.Equal("LEGACY_PROBE", MissingApiClassifier.ToToken(legacy.Classification));
        Assert.Equal("WRONG_RECEIVER", MissingApiClassifier.ToToken(wrongReceiver.Classification));
        Assert.Equal("UNCLASSIFIED", MissingApiClassifier.ToToken(unresolvedReceiver.Classification));
        Assert.Equal("STANDARD_API", MissingApiClassifier.ToToken(standard.Classification));
        Assert.Equal("STANDARD_API", MissingApiClassifier.ToToken(inheritedStandard.Classification));
        Assert.Equal("STANDARD_API", MissingApiClassifier.ToToken(assignedStandard.Classification));
        Assert.Equal("STANDARD_API", MissingApiClassifier.ToToken(assignedStandardAfterRead.Classification));
        Assert.Equal("WRONG_RECEIVER", MissingApiClassifier.ToToken(assignedWrongReceiver.Classification));
        Assert.Equal("SITE_EXPANDO", MissingApiClassifier.ToToken(prototypeMarker.Classification));
        Assert.Equal("STANDARD_API", MissingApiClassifier.ToToken(prototypeMarkerStandard.Classification));
        Assert.Equal("WRONG_RECEIVER", MissingApiClassifier.ToToken(prototypeMarkerWrongReceiver.Classification));
        Assert.Equal("UNCLASSIFIED", MissingApiClassifier.ToToken(descriptorOnInstance.Classification));
        Assert.Equal("STANDARD_API", MissingApiClassifier.ToToken(descriptorOnPrototype.Classification));
        Assert.False(unknown.StandardPriorityEligible);
        Assert.False(expando.StandardPriorityEligible);
        Assert.False(legacy.StandardPriorityEligible);
        Assert.False(wrongReceiver.StandardPriorityEligible);
        Assert.False(unresolvedReceiver.StandardPriorityEligible);
        Assert.True(standard.StandardPriorityEligible);
        Assert.True(inheritedStandard.StandardPriorityEligible);
        Assert.True(assignedStandard.StandardPriorityEligible);
        Assert.True(assignedStandardAfterRead.StandardPriorityEligible);
        Assert.False(assignedWrongReceiver.StandardPriorityEligible);
        Assert.False(prototypeMarker.StandardPriorityEligible);
        Assert.True(prototypeMarkerStandard.StandardPriorityEligible);
        Assert.False(prototypeMarkerWrongReceiver.StandardPriorityEligible);
        Assert.False(descriptorOnInstance.StandardPriorityEligible);
        Assert.True(descriptorOnPrototype.StandardPriorityEligible);
        Assert.Equal("Element", wrongReceiver.DefinedInterface);
        Assert.False(wrongReceiver.ReceiverMatchesDefinedInterface);
        Assert.Equal("Node", standard.DefinedInterface);
        Assert.True(standard.ReceiverMatchesDefinedInterface);
    }

    [Fact]
    public void Classifier_RecognizesCheckedInStringifierAndPartialInterfaceMembers()
    {
        var locationStringifier = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Location",
            "toString",
            MissingApiOperationKind.Read));
        var navigatorGeolocation = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Navigator",
            "geolocation",
            MissingApiOperationKind.Read));
        var wrongReceiver = MissingApiClassifier.Classify(new MissingApiClassificationInput(
            "Document",
            "geolocation",
            MissingApiOperationKind.Read));

        Assert.Equal("STANDARD_API", MissingApiClassifier.ToToken(locationStringifier.Classification));
        Assert.Equal("Location", locationStringifier.DefinedInterface);
        Assert.True(locationStringifier.ReceiverMatchesDefinedInterface);
        Assert.True(locationStringifier.StandardPriorityEligible);

        Assert.Equal("STANDARD_API", MissingApiClassifier.ToToken(navigatorGeolocation.Classification));
        Assert.Equal("Navigator", navigatorGeolocation.DefinedInterface);
        Assert.True(navigatorGeolocation.ReceiverMatchesDefinedInterface);
        Assert.True(navigatorGeolocation.StandardPriorityEligible);

        Assert.Equal("WRONG_RECEIVER", MissingApiClassifier.ToToken(wrongReceiver.Classification));
        Assert.Equal("Navigator", wrongReceiver.DefinedInterface);
        Assert.False(wrongReceiver.ReceiverMatchesDefinedInterface);
        Assert.False(wrongReceiver.StandardPriorityEligible);
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
    public async Task HostExpandoReadThenAssignment_PreservesBothOperationsAndPageOwnership()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/read-then-write.html");

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

            Assert.Equal("42", engine.Evaluate(@"
                var target = document.getElementById('app');
                var state = target.closure_state_probe;
                if (!state) target.closure_state_probe = 42;
                String(target.closure_state_probe);")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var record = Assert.Single(outputJson.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal("HTMLDivElement.closure_state_probe", record.GetProperty("apiName").GetString());
            Assert.Equal("SITE_EXPANDO", record.GetProperty("classification").GetString());
            Assert.Equal("page-assignment-observed", record.GetProperty("classificationReason").GetString());
            Assert.Equal("READ", record.GetProperty("operationKind").GetString());
            Assert.Equal(
                new[] { "READ", "WRITE" },
                record.GetProperty("operationKindsObserved").EnumerateArray().Select(value => value.GetString()));
            Assert.True(record.GetProperty("assignmentObserved").GetBoolean());
            Assert.False(record.GetProperty("assignmentBeforeRead").GetBoolean());
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
    public async Task PageFunctionPrototypeMarkerRead_IsClassifiedAsSiteExpando()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/prototype-marker.html");

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            DisableTrace();

            var document = new HtmlParser(
                "<html><body><div id=\"target\">ok</div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.Sandbox = SandboxPolicy.AllowAll;

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("undefined", engine.Evaluate(@"
                var marker = 'protocol_' + 'marker_fixture';
                function ProtocolType() {}
                ProtocolType.prototype[marker] = true;
                var target = document.getElementById('target');
                typeof target[marker];")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var record = Assert.Single(outputJson.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal("HTMLDivElement.protocol_marker_fixture", record.GetProperty("apiName").GetString());
            Assert.Equal("SITE_EXPANDO", record.GetProperty("classification").GetString());
            Assert.Equal("page-function-prototype-marker", record.GetProperty("classificationReason").GetString());
            Assert.Equal("READ", record.GetProperty("operationKind").GetString());
            Assert.True(record.GetProperty("functionPrototypeMarkerObserved").GetBoolean());
            Assert.False(record.GetProperty("assignmentObserved").GetBoolean());
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
    public async Task OrdinaryObjectPropertyWithSameName_DoesNotBecomePrototypeMarkerEvidence()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/ordinary-object-marker.html");

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            DisableTrace();

            var document = new HtmlParser(
                "<html><body><div id=\"target\">ok</div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.Sandbox = SandboxPolicy.AllowAll;

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("undefined", engine.Evaluate(@"
                var marker = 'ordinary_' + 'object_fixture';
                var unrelated = {};
                unrelated[marker] = true;
                var target = document.getElementById('target');
                typeof target[marker];")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var record = Assert.Single(outputJson.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal("HTMLDivElement.ordinary_object_fixture", record.GetProperty("apiName").GetString());
            Assert.Equal("UNCLASSIFIED", record.GetProperty("classification").GetString());
            Assert.Equal("insufficient-classification-evidence", record.GetProperty("classificationReason").GetString());
            Assert.False(record.GetProperty("functionPrototypeMarkerObserved").GetBoolean());
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
    public async Task FunctionPrototypeMethodName_DoesNotBecomeBooleanMarkerEvidence()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/prototype-method.html");

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            DisableTrace();

            var document = new HtmlParser(
                "<html><body><div id=\"target\">ok</div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.Sandbox = SandboxPolicy.AllowAll;

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("undefined", engine.Evaluate(@"
                var methodName = 'protocol_' + 'method_fixture';
                function ProtocolType() {}
                ProtocolType.prototype[methodName] = function () {};
                var target = document.getElementById('target');
                typeof target[methodName];")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var record = Assert.Single(outputJson.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal("HTMLDivElement.protocol_method_fixture", record.GetProperty("apiName").GetString());
            Assert.Equal("UNCLASSIFIED", record.GetProperty("classification").GetString());
            Assert.Equal("insufficient-classification-evidence", record.GetProperty("classificationReason").GetString());
            Assert.False(record.GetProperty("functionPrototypeMarkerObserved").GetBoolean());
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
    public async Task MissingHostDescriptorQuery_RecordsDescriptorOperation()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/descriptor-operation.html");

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            DisableTrace();

            var document = new HtmlParser(
                "<html><body><div id=\"target\">ok</div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.Sandbox = SandboxPolicy.AllowAll;

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("True", engine.Evaluate(@"
                var target = document.getElementById('target');
                Object.getOwnPropertyDescriptor(target, 'descriptor_probe_fixture') === undefined;")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var record = Assert.Single(outputJson.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal("HTMLDivElement.descriptor_probe_fixture", record.GetProperty("apiName").GetString());
            Assert.Equal("UNCLASSIFIED", record.GetProperty("classification").GetString());
            Assert.Equal("DESCRIPTOR_OPERATION", record.GetProperty("operationKind").GetString());
            Assert.Equal(
                new[] { "DESCRIPTOR_OPERATION" },
                record.GetProperty("operationKindsObserved").EnumerateArray().Select(value => value.GetString()));
            Assert.False(record.GetProperty("descriptorTargetIsPrototype").GetBoolean());
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
    public async Task HostPropertyChecks_RecordDistinctOperationKinds()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/property-check-operations.html");

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            DisableTrace();

            var document = new HtmlParser(
                "<html><body><div id=\"target\">ok</div></body></html>",
                baseUri).Parse();
            var engine = Assert.IsType<FenJsBrowserScriptEngine>(BrowserScriptEngineRuntime.Create(CreateHost()));
            engine.Sandbox = SandboxPolicy.AllowAll;

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal("false|false", engine.Evaluate(@"
                var target = document.getElementById('target');
                String('in_check_fixture' in target) + '|' +
                  String(Object.prototype.hasOwnProperty.call(target, 'own_check_fixture'));")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var records = outputJson.RootElement.GetProperty("records")
                .EnumerateArray()
                .ToDictionary(record => record.GetProperty("apiName").GetString()!, record => record);

            Assert.Equal(
                "IN_CHECK",
                records["HTMLDivElement.in_check_fixture"].GetProperty("operationKind").GetString());
            Assert.Equal(
                "DESCRIPTOR_OPERATION",
                records["HTMLDivElement.own_check_fixture"].GetProperty("operationKind").GetString());
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
    public async Task KnownWebIdlDescriptorQueryOnInstance_DoesNotClaimMissingStandardApi()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/descriptor-standard-instance.html");

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

            Assert.Equal("True", engine.Evaluate(@"
                Object.getOwnPropertyDescriptor(document, 'compareDocumentPosition') === undefined;")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var record = Assert.Single(outputJson.RootElement.GetProperty("records").EnumerateArray());
            Assert.Equal("Document.compareDocumentPosition", record.GetProperty("apiName").GetString());
            Assert.Equal("DESCRIPTOR_OPERATION", record.GetProperty("operationKind").GetString());
            Assert.True(record.GetProperty("knownWebIdlMember").GetBoolean());
            Assert.False(record.GetProperty("descriptorTargetIsPrototype").GetBoolean());
            Assert.Equal("UNCLASSIFIED", record.GetProperty("classification").GetString());
            Assert.Equal(
                "known-webidl-member-descriptor-target-is-instance",
                record.GetProperty("classificationReason").GetString());
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
    public async Task KnownWebIdlStringifierAndPartialInterfaceMisses_AreStandardsPriority()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/stringifier-partial-interface.html");

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

            Assert.Equal("undefined|undefined", engine.Evaluate(@"
                typeof location.toString + '|' + typeof navigator.geolocation;")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var records = outputJson.RootElement.GetProperty("records")
                .EnumerateArray()
                .ToDictionary(record => record.GetProperty("apiName").GetString()!, record => record);

            AssertStandard(records["Location.toString"], "Location");
            AssertStandard(records["Navigator.geolocation"], "Navigator");
        }
        finally
        {
            MissingApiTracker.ResetForTests();
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            TryDeleteDirectory(outputRoot);
        }

        static void AssertStandard(JsonElement record, string definedInterface)
        {
            Assert.Equal("READ", record.GetProperty("operationKind").GetString());
            Assert.Equal("STANDARD_API", record.GetProperty("classification").GetString());
            Assert.Equal(definedInterface, record.GetProperty("definedInterface").GetString());
            Assert.True(record.GetProperty("knownWebIdlMember").GetBoolean());
            Assert.True(record.GetProperty("receiverMatchesDefinedInterface").GetBoolean());
            Assert.True(record.GetProperty("standardPriorityEligible").GetBoolean());
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

    [Fact]
    public void Snapshot_PreservesIdenticalApiAcrossNavigations()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var siteUrl = "https://example.test/navigation.html";

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            DisableTrace();

            MissingApiTracker.Record(CreateObservation(siteUrl, "nav-1", "Document.navigationProbe"));
            MissingApiTracker.Record(CreateObservation(siteUrl, "nav-2", "Document.navigationProbe"));

            var snapshot = MissingApiTracker.GetSnapshot(siteUrl);

            Assert.Equal(2, snapshot.TotalRecordCount);
            Assert.Equal(2, snapshot.RetainedRecordCount);
            Assert.False(snapshot.Truncated);
            Assert.Equal(new[] { "nav-1", "nav-2" }, snapshot.Records.Select(record => record.NavigationId));
        }
        finally
        {
            MissingApiTracker.ResetForTests();
            TryDeleteDirectory(outputRoot);
        }
    }

    [Fact]
    public void Snapshot_IsBoundedAndReportsTruncation()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var siteUrl = "https://example.test/bounded.html";

        try
        {
            MissingApiTracker.ConfigureForTests(outputRoot);
            DisableTrace();

            for (var index = 0; index <= MissingApiTracker.SnapshotRecordLimit; index++)
            {
                MissingApiTracker.Record(CreateObservation(
                    siteUrl,
                    "nav-bounded",
                    $"Document.probe{index:D4}"));
            }

            var snapshot = MissingApiTracker.GetSnapshot(siteUrl);

            Assert.Equal(MissingApiTracker.SnapshotRecordLimit + 1, snapshot.TotalRecordCount);
            Assert.Equal(MissingApiTracker.SnapshotRecordLimit, snapshot.RetainedRecordCount);
            Assert.True(snapshot.Truncated);
            Assert.Equal(MissingApiTracker.SnapshotRecordLimit, snapshot.Records.Count);
        }
        finally
        {
            MissingApiTracker.ResetForTests();
            TryDeleteDirectory(outputRoot);
        }
    }

    [Fact]
    public async Task StandardElementAssignments_UseConcreteWebIdlReceiver()
    {
        var outputRoot = Path.Combine(Path.GetTempPath(), $"fenbrowser-missing-apis-{Guid.NewGuid():N}");
        var baseUri = new Uri("https://example.test/concrete-element.html");

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

            Assert.Equal("assigned", engine.Evaluate(@"
                var scriptProbe = document.createElement('script');
                scriptProbe.async = true;
                scriptProbe.fetchPriority = 'high';
                var linkProbe = document.createElement('link');
                linkProbe.as = 'script';
                var imageProbe = document.createElement('img');
                imageProbe.fetchPriority = 'low';
                'assigned';")?.ToString());

            using var outputJson = JsonDocument.Parse(File.ReadAllText(MissingApiTracker.GetOutputPathForTests(baseUri)));
            var records = outputJson.RootElement.GetProperty("records")
                .EnumerateArray()
                .ToDictionary(record => record.GetProperty("apiName").GetString()!, record => record);

            AssertStandardWrite(records, "HTMLScriptElement.async", "HTMLScriptElement");
            AssertStandardWrite(records, "HTMLScriptElement.fetchPriority", "HTMLScriptElement");
            AssertStandardWrite(records, "HTMLLinkElement.as", "HTMLLinkElement");
            AssertStandardWrite(records, "HTMLImageElement.fetchPriority", "HTMLImageElement");
        }
        finally
        {
            MissingApiTracker.ResetForTests();
            EngineCapabilities.Reset();
            BrowserScriptEngineRuntime.Reset();
            TryDeleteDirectory(outputRoot);
        }
    }

    private static void AssertStandardWrite(
        IReadOnlyDictionary<string, JsonElement> records,
        string apiName,
        string receiverType)
    {
        var record = records[apiName];
        Assert.Equal("STANDARD_API", record.GetProperty("classification").GetString());
        Assert.Equal("WRITE", record.GetProperty("operationKind").GetString());
        Assert.Equal(receiverType, record.GetProperty("receiverType").GetString());
        Assert.Equal(receiverType, record.GetProperty("definedInterface").GetString());
        Assert.True(record.GetProperty("receiverMatchesDefinedInterface").GetBoolean());
        Assert.True(record.GetProperty("standardPriorityEligible").GetBoolean());
    }

    private static MissingApiObservation CreateObservation(string siteUrl, string navigationId, string apiName)
    {
        var separator = apiName.IndexOf('.');
        return new MissingApiObservation
        {
            ApiName = apiName,
            ObjectOrPrototype = apiName.Substring(0, separator),
            PropertyName = apiName.Substring(separator + 1),
            ReceiverType = "Document",
            SiteUrl = siteUrl,
            ScriptUrl = siteUrl,
            ScriptId = "script-snapshot",
            NavigationId = navigationId,
            OperationKind = MissingApiOperationKind.Read
        };
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
