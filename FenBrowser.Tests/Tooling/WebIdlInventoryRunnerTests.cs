using System;
using System.IO;
using FenBrowser.Tooling;
using Xunit;

namespace FenBrowser.Tests.Tooling;

public sealed class WebIdlInventoryRunnerTests
{
    [Fact]
    public void Build_ReportsActiveManualEvidenceAndExcludedGeneratedOutputDeterministically()
    {
        var root = Path.Combine(Path.GetTempPath(), $"fen-webidl-inventory-{Guid.NewGuid():N}");
        try
        {
            var idlRoot = Path.Combine(root, "FenBrowser.Core", "WebIDL", "Idl");
            var wptRoot = Path.Combine(root, "wpt");
            Directory.CreateDirectory(idlRoot);
            Directory.CreateDirectory(Path.Combine(root, "FenBrowser.Core", "Dom"));
            Directory.CreateDirectory(Path.Combine(root, "FenBrowser.FenEngine", "Scripting"));
            Directory.CreateDirectory(Path.Combine(root, "FenBrowser.FenEngine", "Bindings", "Generated"));
            Directory.CreateDirectory(Path.Combine(root, "FenBrowser.Tests", "Tooling"));
            Directory.CreateDirectory(Path.Combine(wptRoot, "dom"));

            File.WriteAllText(
                Path.Combine(idlRoot, "EventTarget.idl"),
                "interface EventTarget { undefined addEventListener(DOMString type); };\n" +
                "dictionary EventListenerOptions { boolean capture = false; };\n");
            File.WriteAllText(
                Path.Combine(root, "FenBrowser.Core", "FenBrowser.Core.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(
                Path.Combine(root, "FenBrowser.FenEngine", "FenBrowser.FenEngine.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>" +
                "<Compile Remove=\"Bindings\\Generated\\**\\*.cs\" />" +
                "</ItemGroup></Project>");
            File.WriteAllText(
                Path.Combine(root, "FenBrowser.Tests", "FenBrowser.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(
                Path.Combine(root, "FenBrowser.Core", "Dom", "EventTarget.cs"),
                "public sealed class EventTarget { public bool Capture { get; set; } }");
            File.WriteAllText(
                Path.Combine(root, "FenBrowser.FenEngine", "Scripting", "Runtime.cs"),
                "case \"addEventListener\": return GetOrCreateHostCallable(receiver, \"addEventListener\");");
            File.WriteAllText(
                Path.Combine(root, "FenBrowser.FenEngine", "Bindings", "Generated", "EventTargetBinding.g.cs"),
                "public sealed class EventTargetBinding { }");
            File.WriteAllText(
                Path.Combine(root, "FenBrowser.Tests", "Tooling", "EventTargetTests.cs"),
                "public sealed class EventTargetTests { void addEventListener() { } }");
            File.WriteAllText(
                Path.Combine(wptRoot, "dom", "event-target.html"),
                "<script>target.addEventListener('click', callback);</script>");

            var options = new WebIdlInventoryOptions
            {
                RepositoryRoot = root,
                IdlDirectory = idlRoot,
                OutputDirectory = Path.Combine(root, "Results"),
                SelectedWptRoot = wptRoot,
                SelectedWptPaths = { "dom/event-target.html" }
            };

            var first = WebIdlInventoryRunner.Build(options);
            var second = WebIdlInventoryRunner.Build(options);

            Assert.Equal(WebIdlInventoryRunner.Serialize(first), WebIdlInventoryRunner.Serialize(second));
            Assert.Equal(2, first.Summary.Definitions);
            Assert.Equal(2, first.Summary.Members);
            Assert.Equal(0, first.Summary.GeneratedOutputsCompiled);
            Assert.Equal("EventListenerOptions", first.LowLifetimeRiskCandidate.Definition);

            var eventTarget = Assert.Single(first.Definitions, definition => definition.Name == "EventTarget");
            Assert.True(eventTarget.GeneratedOutputExists);
            Assert.False(eventTarget.GeneratedCompileIncluded);
            var addEventListener = Assert.Single(eventTarget.Members);
            Assert.Contains(
                addEventListener.ManualImplementationLocations,
                evidence => evidence.Path == "FenBrowser.FenEngine/Scripting/Runtime.cs" && evidence.ActiveCompileInclusion);
            Assert.Contains("FenBrowser.Tests/Tooling/EventTargetTests.cs", addEventListener.TestCoverage);
            Assert.Contains("dom/event-target.html", addEventListener.SelectedWptCoverage);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
