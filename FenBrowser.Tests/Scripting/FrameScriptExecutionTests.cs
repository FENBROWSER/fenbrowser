using System;
using System.Diagnostics;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    // WebDriver, Execute Script: "the script is evaluated in the current
    // browsing context". Selecting a frame swapped the DOM root the top-level
    // realm reported, but the script itself still ran in the top realm, so
    // location, window and anything the frame's own scripts had set belonged to
    // the wrong document.
    //
    // Found while driving Google's robot check: dumping the anchor frame, the
    // challenge frame and a sibling all returned byte-identical HTML, because
    // all three "frame" evaluations were really running against the /sorry
    // document.
    public sealed class FrameScriptExecutionTests
    {
        private static readonly TimeSpan AttachTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task ScriptSeesTheSelectedFramesDocument()
        {
            var world = await CreateTwoFrameWorldAsync();

            Assert.Equal("first-body", InFrame(world, "first", "document.body.id"));
            Assert.Equal("second-body", InFrame(world, "second", "document.body.id"));
        }

        // The symptom that gave this away: every frame answered with the top
        // document, so two different frames looked identical.
        [Fact]
        public async Task TwoFramesDoNotReportTheSameDocument()
        {
            var world = await CreateTwoFrameWorldAsync();

            var first = InFrame(world, "first", "document.body.id");
            var second = InFrame(world, "second", "document.body.id");

            Assert.NotEqual(first, second);
            Assert.NotEqual("top-body", first);
            Assert.NotEqual("top-body", second);
        }

        [Fact]
        public async Task ScriptSeesTheSelectedFramesLocation()
        {
            var world = await CreateTwoFrameWorldAsync();

            Assert.Equal("https://parent.test/first", InFrame(world, "first", "location.href"));
            Assert.Equal("https://parent.test/second", InFrame(world, "second", "location.href"));
        }

        // HTML 3.1.5 document.referrer: a framed document reports the document
        // that embedded it. We reported the empty string, which leaves a frame
        // whose own URL carries no origin -- reCAPTCHA's challenge frame, for
        // one -- with no way to learn where it is embedded.
        [Fact]
        public async Task AFramedDocumentReportsTheEmbeddingDocument()
        {
            var world = await CreateTwoFrameWorldAsync();

            Assert.Equal("https://parent.test/page", InFrame(world, "first", "document.referrer"));
            Assert.Equal("https://parent.test/page", InFrame(world, "second", "document.referrer"));
        }

        [Fact]
        public async Task ATopLevelDocumentReportsNoReferrer()
        {
            var world = await CreateTwoFrameWorldAsync();

            Assert.Equal(string.Empty, world.Engine.Evaluate("document.referrer")?.ToString() ?? string.Empty);
        }

        // A global belongs to one realm. Reading the top realm's global from a
        // frame - or the frame's from the top - is how the old routing showed up
        // as "the state I just set is missing".
        [Fact]
        public async Task GlobalsStayInTheRealmThatSetThem()
        {
            var world = await CreateTwoFrameWorldAsync();

            world.Engine.Evaluate("globalThis.__whichRealm = 'top';");
            world.Engine.EvaluateInFrame(Frame(world, "first"), "globalThis.__whichRealm = 'first';");

            Assert.Equal("top", world.Engine.Evaluate("globalThis.__whichRealm")?.ToString());
            Assert.Equal("first", InFrame(world, "first", "globalThis.__whichRealm"));
            Assert.Equal("undefined", InFrame(world, "second", "typeof globalThis.__whichRealm"));
        }

        // A frame two levels down is owned by the realm that embeds it, not by
        // the top one, so the lookup has to walk the realm tree.
        [Fact]
        public async Task ScriptReachesAFrameNestedInsideAnotherFrame()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                "<html><body id='top-body'>" +
                "<iframe id='outer' name='outer-frame' src='https://parent.test/outer'></iframe>" +
                "</body></html>",
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll,
                // srcdoc content is parsed in-process, but a frame is only loaded
                // at all once the host has said it will service frame loads.
                FrameElementLoader = (_, __) => Task.CompletedTask
            };
            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var world = new FrameWorld(engine, document);
            var outerDocument = await AttachFrameAsync(
                world,
                "outer",
                "https://parent.test/outer",
                "<html><body id='outer-body'><iframe id='inner'></iframe></body></html>");

            // The outer realm loads its own child, registering it in the outer
            // realm's table - exactly how a nested frame is created at runtime.
            engine.EvaluateInFrame(
                Frame(world, "outer"),
                "document.getElementById('inner')" +
                ".setAttribute('srcdoc', \"<html><body id='inner-body'></body></html>\");");

            var innerFrame = Assert.IsType<Element>(outerDocument.GetElementById("inner"));
            Assert.Equal(
                "inner-body",
                await WaitForAsync(engine, innerFrame, "document.body ? document.body.id : ''", "inner-body"));
        }

        [Fact]
        public async Task AFrameWithNoRealmOfItsOwnFallsBackToTheTopLevelRealm()
        {
            var world = await CreateTwoFrameWorldAsync();

            // A frame element that was never given a document has no realm.
            var detached = world.Document.CreateElement("iframe");
            world.Document.Body.AppendChild(detached);

            Assert.Equal("top-body", world.Engine.EvaluateInFrame(detached, "document.body.id")?.ToString());
            Assert.Equal("top-body", world.Engine.EvaluateInFrame(null, "document.body.id")?.ToString());
        }

        private sealed record FrameWorld(FenJsBrowserScriptEngine Engine, Document Document);

        private static Element Frame(FrameWorld world, string id) =>
            Assert.IsType<Element>(world.Document.GetElementById(id));

        private static string InFrame(FrameWorld world, string frameId, string script) =>
            world.Engine.EvaluateInFrame(Frame(world, frameId), script)?.ToString();

        private static async Task<string> WaitForAsync(
            FenJsBrowserScriptEngine engine,
            Element frame,
            string script,
            string expected)
        {
            var watch = Stopwatch.StartNew();
            string last = null;
            while (watch.Elapsed < AttachTimeout)
            {
                last = engine.EvaluateInFrame(frame, script)?.ToString();
                if (string.Equals(last, expected, StringComparison.Ordinal))
                {
                    return last;
                }

                await Task.Delay(50);
            }

            return last;
        }

        private static async Task<FrameWorld> CreateTwoFrameWorldAsync()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                "<html><body id='top-body'>" +
                "<iframe id='first' name='first-frame' src='https://parent.test/first'></iframe>" +
                "<iframe id='second' name='second-frame' src='https://parent.test/second'></iframe>" +
                "</body></html>",
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var world = new FrameWorld(engine, document);
            await AttachFrameAsync(world, "first", "https://parent.test/first", "<html><body id='first-body'></body></html>");
            await AttachFrameAsync(world, "second", "https://parent.test/second", "<html><body id='second-body'></body></html>");
            return world;
        }

        private static async Task<Document> AttachFrameAsync(FrameWorld world, string id, string src, string html)
        {
            var frameUri = new Uri(src);
            var frameDocument = new HtmlParser(html, frameUri).Parse();

            Frame(world, id).AppendChild(frameDocument);
            await world.Engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);
            return frameDocument;
        }

        private static JsHostAdapter CreateHost() =>
            new JsHostAdapter(navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { });
    }
}
