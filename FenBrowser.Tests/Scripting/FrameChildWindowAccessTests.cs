using System;
using System.Globalization;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    // HTML 7.2.3 WindowProxy, the page's side: iframe.contentWindow of a same-origin
    // frame is that frame's window, so a property the page sets on it is set on the
    // frame's global, and the frame's own globals read and call through it.
    public sealed class FrameChildWindowAccessTests
    {
        [Fact]
        public async Task APropertyThePageSetsReachesTheFramesGlobal()
        {
            var world = await CreateWorldAsync("https://parent.test/child");
            world.Engine.Evaluate("document.getElementById('child').contentWindow.__label = 'wptrunner-7';");

            Assert.Equal("wptrunner-7", InFrame(world, "String(window.__label)"));
            Assert.Equal("wptrunner-7", world.Engine.Evaluate("document.getElementById('child').contentWindow.__label")?.ToString());
        }

        [Fact]
        public async Task TheFramesGlobalsReadAndCallThroughContentWindow()
        {
            var world = await CreateWorldAsync("https://parent.test/child");
            InFrame(world, "var answer = 42; function twice(n) { return n * 2; }");

            Assert.Equal("42:84:true", world.Engine.Evaluate(
                "var w = document.getElementById('child').contentWindow; w.answer + ':' + w.twice(w.answer) + ':' + ('answer' in w)")?.ToString());
        }

        [Fact]
        public async Task StandInPropertiesAndIdentityStayAsBefore()
        {
            var world = await CreateWorldAsync("https://parent.test/child");

            Assert.Equal("true:function:object", world.Engine.Evaluate(
                "var f = document.getElementById('child'); String(f.contentWindow === f.contentWindow) + ':' + typeof f.contentWindow.postMessage + ':' + typeof f.contentWindow.document")?.ToString());
        }

        [Fact]
        public async Task TheFramesDocumentKnowsItsWindow()
        {
            var world = await CreateWorldAsync("https://parent.test/child");

            Assert.Equal("true:false", world.Engine.Evaluate(
                "var f = document.getElementById('child'); var d = f.contentDocument; String(d.defaultView === f.contentWindow) + ':' + String(d.defaultView === window)")?.ToString());
            Assert.Equal("true", world.Engine.Evaluate(
                "var b = document.getElementById('child').contentDocument.createElement('button'); document.getElementById('child').contentDocument.body.appendChild(b); String(b.ownerDocument.defaultView === document.getElementById('child').contentWindow)")?.ToString());
        }

        [Fact]
        public async Task ANameOnlyThePageDefinedIsNotTheFramesToo()
        {
            var world = await CreateWorldAsync("https://parent.test/child");
            world.Engine.Evaluate("window.__onlyTop = 'top';");

            Assert.Equal("undefined", world.Engine.Evaluate("typeof document.getElementById('child').contentWindow.__onlyTop")?.ToString());
            Assert.Equal("undefined", InFrame(world, "typeof window.__onlyTop"));
        }

        [Fact]
        public async Task ACrossOriginFramesGlobalsStayOutOfReach()
        {
            var world = await CreateWorldAsync("https://other.test/child");
            InFrame(world, "var secret = 'x';");

            Assert.Equal("undefined", world.Engine.Evaluate("typeof document.getElementById('child').contentWindow.secret")?.ToString());
        }

        private sealed record FrameWorld(FenJsBrowserScriptEngine Engine, Document Document);

        private static string InFrame(FrameWorld world, string script) =>
            Convert.ToString(
                world.Engine.EvaluateInFrame(Assert.IsType<Element>(world.Document.GetElementById("child")), script),
                CultureInfo.InvariantCulture);

        private static async Task<FrameWorld> CreateWorldAsync(string frameSrc)
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                "<html><body id='top-body'><iframe id='child' src='" + frameSrc + "'></iframe></body></html>",
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var frameUri = new Uri(frameSrc);
            var frameDocument = new HtmlParser("<html><body id='child-body'></body></html>", frameUri).Parse();
            Assert.IsType<Element>(document.GetElementById("child")).AppendChild(frameDocument);
            await engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);
            return new FrameWorld(engine, document);
        }

        private static JsHostAdapter CreateHost() =>
            new JsHostAdapter(navigate: _ => { }, post: (_, __) => { }, status: _ => { }, log: _ => { });
    }
}
