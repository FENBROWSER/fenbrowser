using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    // HTML "destroy a child navigable": removing an iframe from the tree discards
    // its nested browsing context, so the frame's scripts stop.
    //
    // Regression cover for the reCAPTCHA death spiral: the widget's own
    // anchor-ms/execute-ms deadlines expire, reCAPTCHA replaces the widget, and
    // before this every replaced frame kept its realm executing on its own worker
    // thread — 8 frame documents were observed spawned with 3+ grinding
    // concurrently, so each replacement had less CPU than the one it replaced and
    // the page could never recover.
    public sealed class FrameRealmTeardownTests
    {
        [Fact]
        public async Task RemovingIframe_AbandonsItsRealm()
        {
            var (engine, document, frameDocument) = await CreateParentWithFrameAsync();

            // The frame realm is live: script in it observes its own document.
            Assert.Equal(
                "frame-body",
                engine.EvaluateInSubdocumentForTest(frameDocument, "document.body.id")?.ToString());

            var frameElement = Assert.IsType<Element>(document.GetElementById("child"));
            frameElement.Remove();

            // The realm is gone, so there is nothing left to evaluate in — and
            // nothing left running on a worker thread.
            Assert.Null(engine.EvaluateInSubdocumentForTest(frameDocument, "document.body.id"));
        }

        // Removing an ancestor detaches the frame just as removing the frame does.
        [Fact]
        public async Task RemovingAnAncestorOfTheIframe_AbandonsItsRealm()
        {
            var (engine, document, frameDocument) = await CreateParentWithFrameAsync();

            Assert.NotNull(engine.EvaluateInSubdocumentForTest(frameDocument, "document.body.id"));

            var wrapper = Assert.IsType<Element>(document.GetElementById("wrapper"));
            wrapper.Remove();

            Assert.Null(engine.EvaluateInSubdocumentForTest(frameDocument, "document.body.id"));
        }

        // An abandoned realm must stay inert: no queued work may run afterwards,
        // and nothing may resurrect its worker.
        [Fact]
        public async Task AbandonedRealm_RunsNoFurtherScript()
        {
            var (engine, document, frameDocument) = await CreateParentWithFrameAsync();

            engine.EvaluateInSubdocumentForTest(frameDocument, "globalThis.__ran = 'before';");

            var frameElement = Assert.IsType<Element>(document.GetElementById("child"));
            frameElement.Remove();

            // Evaluating against the detached document is a no-op rather than a
            // crash, and the earlier value is unreachable because the realm is gone.
            Assert.Null(engine.EvaluateInSubdocumentForTest(frameDocument, "globalThis.__ran = 'after';"));
            Assert.Null(engine.EvaluateInSubdocumentForTest(frameDocument, "globalThis.__ran"));
        }

        private static async Task<(FenJsBrowserScriptEngine Engine, Document Document, Document FrameDocument)>
            CreateParentWithFrameAsync()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                "<html><body><div id='wrapper'>" +
                "<iframe id='child' name='child-frame' src='https://parent.test/frame'></iframe>" +
                "</div></body></html>",
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var frameUri = new Uri("https://parent.test/frame");
            var frameDocument = new HtmlParser(
                "<html><body id='frame-body'></body></html>",
                frameUri).Parse();

            var frameElement = Assert.IsType<Element>(document.GetElementById("child"));
            frameElement.AppendChild(frameDocument);
            await engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);

            return (engine, document, frameDocument);
        }

        private static JsHostAdapter CreateHost()
        {
            return new JsHostAdapter(
                navigate: _ => { },
                post: (_, __) => { },
                status: _ => { },
                log: _ => { });
        }
    }
}
