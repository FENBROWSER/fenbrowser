using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    // A frame's bootstrap script commonly builds nothing itself and defers to the
    // document becoming ready - "if readyState is loading, wait for
    // DOMContentLoaded, else build now". If a subdocument never leaves loading,
    // or never fires the event, such a script registers a callback that is never
    // called: no error, no DOM, nothing to see.
    //
    // That is exactly the shape of reCAPTCHA's anchor frame failing to build its
    // widget while re-running the very same bootstrap by hand builds it.
    public sealed class SubdocumentLifecycleTests
    {
        [Fact]
        public async Task SubdocumentScript_SeesLoadingAndThenGetsDomContentLoaded()
        {
            var world = await LoadFrameAsync(
                "<html><body><div id='out'></div><script>" +
                "window.__stateAtScript = document.readyState;" +
                "document.addEventListener('DOMContentLoaded', function () {" +
                "  window.__dclFired = true;" +
                "  document.getElementById('out').setAttribute('data-built', 'yes');" +
                "});" +
                "</script></body></html>");

            Assert.Equal("loading", Eval(world, "window.__stateAtScript"));
            Assert.Equal("true", Eval(world, "String(window.__dclFired === true)"));
            Assert.Equal("complete", Eval(world, "document.readyState"));
        }

        // The callback has to actually reach the frame's own document, not build
        // into some other one.
        [Fact]
        public async Task DomContentLoadedCallback_BuildsIntoTheFramesDocument()
        {
            var world = await LoadFrameAsync(
                "<html><body><div id='out'></div><script>" +
                "document.addEventListener('DOMContentLoaded', function () {" +
                "  document.getElementById('out').setAttribute('data-built', 'yes');" +
                "});" +
                "</script></body></html>");

            var built = Assert.IsType<Element>(world.FrameDocument.GetElementById("out"));
            Assert.Equal("yes", built.GetAttribute("data-built"));
        }

        // window.onload is the other half of the same pattern.
        [Fact]
        public async Task SubdocumentAlsoFiresWindowLoad()
        {
            var world = await LoadFrameAsync(
                "<html><body><script>" +
                "window.addEventListener('load', function () { window.__loadFired = true; });" +
                "</script></body></html>");

            Assert.Equal("true", Eval(world, "String(window.__loadFired === true)"));
        }

        private sealed record FrameWorld(
            FenJsBrowserScriptEngine Engine,
            Document Document,
            Document FrameDocument,
            Element FrameElement);

        private static string Eval(FrameWorld world, string script) =>
            world.Engine.EvaluateInFrame(world.FrameElement, script)?.ToString();

        private static async Task<FrameWorld> LoadFrameAsync(string frameHtml)
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                "<html><body>" +
                "<iframe id='f' name='frame' src='https://parent.test/frame'></iframe>" +
                "</body></html>",
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };
            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var frameUri = new Uri("https://parent.test/frame");
            var frameDocument = new HtmlParser(frameHtml, frameUri).Parse();
            var frameElement = Assert.IsType<Element>(document.GetElementById("f"));
            frameElement.AppendChild(frameDocument);
            await engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);

            return new FrameWorld(engine, document, frameDocument, frameElement);
        }

        private static JsHostAdapter CreateHost() =>
            new JsHostAdapter(navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { });
    }
}
