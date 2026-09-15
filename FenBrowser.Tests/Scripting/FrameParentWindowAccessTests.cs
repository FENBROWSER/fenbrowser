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
    // HTML 7.2.3 WindowProxy: a same-origin frame's `parent` is the parent's
    // window, so a function the parent page declared is callable from the frame.
    // The frame's parent was a stand-in with a fixed set of properties, so
    // Acid3's XHTML support files threw "parent.notify is not a function" and
    // test 80 failed with "Script in XHTML didn't execute".
    public sealed class FrameParentWindowAccessTests
    {
        [Fact]
        public async Task SameOriginFrameCallsAFunctionTheParentPageDeclared()
        {
            var world = await CreateWorldAsync("https://parent.test/child");
            world.Engine.Evaluate("function notify(file) { globalThis.__notified = file; return file.length; }");

            Assert.Equal("7", InFrame(world, "parent.notify('xhtml.1')"));
            Assert.Equal("xhtml.1", world.Engine.Evaluate("globalThis.__notified")?.ToString());
        }

        [Fact]
        public async Task ParentKeepsOneIdentity()
        {
            var world = await CreateWorldAsync("https://parent.test/child");
            world.Engine.Evaluate("function notify() {}");

            Assert.Equal(
                "true",
                InFrame(world, "String(parent === window.parent && parent.parent === parent && parent.top === parent && parent.notify === parent.notify)"));
        }

        [Fact]
        public async Task StandInPropertiesStillReadAsBefore()
        {
            var world = await CreateWorldAsync("https://parent.test/child");

            Assert.Equal("top-body", InFrame(world, "parent.document.body.id"));
            Assert.Equal("function", InFrame(world, "typeof parent.postMessage"));
            Assert.Equal("undefined", InFrame(world, "typeof parent.neverDefined"));
        }

        [Fact]
        public async Task AnErrorThrownByTheParentReachesTheFrame()
        {
            var world = await CreateWorldAsync("https://parent.test/child");
            world.Engine.Evaluate("function explode() { throw new TypeError('boom'); }");

            Assert.Equal(
                "TypeError:boom",
                InFrame(world, "(function () { try { parent.explode(); return 'no throw'; } catch (e) { return (e instanceof TypeError ? 'TypeError' : 'other') + ':' + e.message; } })()"));
        }

        [Fact]
        public async Task DomNodesCrossAsTheSameNode()
        {
            var world = await CreateWorldAsync("https://parent.test/child");
            world.Engine.Evaluate("function tagOf(element) { return element.tagName; } function bodyOf() { return document.body; }");

            Assert.Equal("BODY", InFrame(world, "parent.tagOf(parent.document.body)"));
            Assert.Equal("true", InFrame(world, "String(parent.bodyOf() === parent.document.body)"));
        }

        [Fact]
        public async Task PrimitiveGlobalsAreCopied()
        {
            var world = await CreateWorldAsync("https://parent.test/child");
            world.Engine.Evaluate("var answer = 42; var label = 'acid';");

            Assert.Equal("42:acid:true", InFrame(world, "parent.answer + ':' + parent.label + ':' + ('answer' in parent)"));
        }

        // WindowProxy named properties: a sibling frame is reachable by name as
        // soon as it is in the parent document, before any refresh of the
        // frame's published table has run.
        [Fact]
        public async Task ASiblingFrameIsReachableByNameBeforeTheTableRefreshes()
        {
            var world = await CreateWorldAsync("https://parent.test/child");
            var late = world.Document.CreateElement("iframe");
            late.SetAttribute("id", "late");
            late.SetAttribute("name", "late-frame");
            world.Document.Body.AppendChild(late);

            Assert.Equal("late-frame", InFrame(world, "parent.frames['late-frame'].name"));
            Assert.Equal("true", InFrame(world, "String(parent['late-frame'] === parent.frames['late-frame'])"));
        }

        [Fact]
        public async Task CrossOriginFrameDoesNotSeeTheParentPagesFunctions()
        {
            var world = await CreateWorldAsync("https://other.test/child");
            world.Engine.Evaluate("function notify() {}");

            Assert.Equal("undefined", InFrame(world, "typeof parent.notify"));
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
