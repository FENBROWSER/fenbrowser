using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    // HTML, WindowProxy: the indexed properties of a window are the child
    // browsing contexts of its document in tree order, and the named ones are
    // those contexts' names. So a frame reaches a *sibling* through
    // parent.frames[i], parent.frames[name] or parent[name].
    //
    // Regression cover for the Google robot check: reCAPTCHA renders an anchor
    // frame named "a-<id>" and a challenge frame named "c-<id>" side by side and
    // has them talk to each other by name. The parent window a frame realm saw
    // published exactly one child — the frame asking — so every sibling lookup
    // answered undefined and the challenge frame died on
    // "Cannot read properties of undefined (reading 'postMessage')".
    public sealed class SiblingFrameMessagingTests
    {
        private static readonly TimeSpan DeliveryTimeout = TimeSpan.FromSeconds(10);

        [Fact]
        public async Task ParentWindow_PublishesEverySiblingBrowsingContext()
        {
            var world = await CreateTwoFrameDocumentAsync();

            Assert.Equal("2", Eval(world, world.FirstFrameDocument, "String(parent.length)"));
            Assert.Equal("2", Eval(world, world.FirstFrameDocument, "String(parent.frames.length)"));
        }

        [Fact]
        public async Task SiblingIsReachableByNameAndByIndex()
        {
            var world = await CreateTwoFrameDocumentAsync();

            // From the first frame, the second is the sibling.
            Assert.Equal("second-frame", Eval(world, world.FirstFrameDocument, "parent.frames[1].name"));
            Assert.Equal("second-frame", Eval(world, world.FirstFrameDocument, "parent.frames['second-frame'].name"));
            Assert.Equal("second-frame", Eval(world, world.FirstFrameDocument, "parent['second-frame'].name"));
            Assert.Equal(
                "function",
                Eval(world, world.FirstFrameDocument, "typeof parent.frames['second-frame'].postMessage"));

            // And the reverse direction resolves the first frame.
            Assert.Equal("first-frame", Eval(world, world.SecondFrameDocument, "parent.frames[0].name"));
            Assert.Equal("first-frame", Eval(world, world.SecondFrameDocument, "parent.frames['first-frame'].name"));
        }

        // A frame's own slot must still be itself, or self-reference breaks.
        [Fact]
        public async Task OwnSlotStillResolvesToTheAskingFrame()
        {
            var world = await CreateTwoFrameDocumentAsync();

            Assert.Equal("true", Eval(world, world.FirstFrameDocument, "String(parent.frames[0] === window)"));
            Assert.Equal("true", Eval(world, world.SecondFrameDocument, "String(parent.frames[1] === window)"));
        }

        [Fact]
        public async Task PostMessageToSibling_ArrivesInThatSiblingsRealm()
        {
            var world = await CreateTwoFrameDocumentAsync();

            Eval(
                world,
                world.SecondFrameDocument,
                "globalThis.__received = null;" +
                "window.addEventListener('message', function (event) { globalThis.__received = event.data; });");

            Eval(world, world.FirstFrameDocument, "parent.frames['second-frame'].postMessage('anchor-ping', '*');");

            Assert.Equal(
                "anchor-ping",
                await WaitForValueAsync(world, world.SecondFrameDocument, "globalThis.__received"));
        }

        // reCAPTCHA replies on event.source, so the source of a sibling message
        // has to be the sending sibling — not the parent.
        [Fact]
        public async Task SiblingMessageCarriesTheSendingSiblingAsItsSource()
        {
            var world = await CreateTwoFrameDocumentAsync();

            Eval(
                world,
                world.SecondFrameDocument,
                "globalThis.__sourceName = null;" +
                "window.addEventListener('message', function (event) {" +
                "  globalThis.__sourceName = event.source ? event.source.name : 'no-source';" +
                "});");

            Eval(world, world.FirstFrameDocument, "parent.frames['second-frame'].postMessage('ping', '*');");

            Assert.Equal(
                "first-frame",
                await WaitForValueAsync(world, world.SecondFrameDocument, "globalThis.__sourceName"));
        }

        // reCAPTCHA builds its challenge frame long after the anchor frame, so a
        // frame that already exists has to notice a sibling that appears later.
        [Fact]
        public async Task SiblingAddedLater_BecomesVisibleToAnExistingFrame()
        {
            var world = await CreateSingleFrameDocumentAsync();

            Assert.Equal("1", Eval(world, world.FirstFrameDocument, "String(parent.length)"));

            var lateDocument = await AddFrameAsync(world, "late", "late-frame", "https://parent.test/late");

            Assert.Equal(
                "2",
                await WaitForValueAsync(world, world.FirstFrameDocument, "String(parent.length)", "2"));
            Assert.Equal(
                "late-frame",
                await WaitForValueAsync(
                    world,
                    world.FirstFrameDocument,
                    "parent.frames['late-frame'] ? parent.frames['late-frame'].name : ''",
                    "late-frame"));
            Assert.NotNull(lateDocument);
        }

        // The port half of a sibling message. reCAPTCHA's challenge frame opens a
        // MessageChannel, keeps port1, and hands port2 to the anchor frame beside
        // it; everything that drives the challenge afterwards rides that channel.
        // Confirmed live on google.com/sorry: the challenge frame sends exactly one
        // sibling message carrying one port and is then never spoken to again -
        // every later port message is anchor <-> page, the challenge frame's own
        // port traffic is zero, no /reload is ever issued and the checkbox spins
        // forever. So a transferred port has to arrive, and the channel has to
        // carry a reply back to the frame that opened it.
        [Fact]
        public async Task PortTransferredToSibling_CarriesAReplyBack()
        {
            var world = await CreateTwoFrameDocumentAsync();

            // The anchor's side: take the port off the message and answer on it.
            Eval(
                world,
                world.SecondFrameDocument,
                "globalThis.__portCount = -1;" +
                "window.addEventListener('message', function (event) {" +
                "  globalThis.__portCount = event.ports ? event.ports.length : 0;" +
                "  if (event.ports && event.ports[0]) {" +
                "    event.ports[0].postMessage('challenge-ready');" +
                "  }" +
                "});");

            // The challenge frame's side: open the channel, keep port1, send port2.
            Eval(
                world,
                world.FirstFrameDocument,
                "globalThis.__reply = null;" +
                "var channel = new MessageChannel();" +
                "channel.port1.onmessage = function (event) { globalThis.__reply = event.data; };" +
                "channel.port1.start();" +
                "parent.frames['second-frame'].postMessage('recaptcha-setup', '*', [channel.port2]);");

            Assert.Equal(
                "1",
                await WaitForValueAsync(
                    world,
                    world.SecondFrameDocument,
                    "String(globalThis.__portCount)",
                    "1"));

            Assert.Equal(
                "challenge-ready",
                await WaitForValueAsync(world, world.FirstFrameDocument, "globalThis.__reply"));
        }

        private sealed record FrameWorld(
            FenJsBrowserScriptEngine Engine,
            Document Document,
            Document FirstFrameDocument,
            Document SecondFrameDocument);

        private static string Eval(FrameWorld world, Document frameDocument, string script) =>
            world.Engine.EvaluateInSubdocumentForTest(frameDocument, script)?.ToString();

        // Cross-realm delivery is queued on the receiving realm's worker, so the
        // observable result lands after the sending call returns.
        private static async Task<string> WaitForValueAsync(FrameWorld world, Document frameDocument, string script)
        {
            var watch = Stopwatch.StartNew();
            string last = null;
            while (watch.Elapsed < DeliveryTimeout)
            {
                last = Eval(world, frameDocument, script);
                if (!string.IsNullOrEmpty(last) && last != "null" && last != "undefined")
                {
                    return last;
                }

                await Task.Delay(50);
            }

            return last;
        }

        private static async Task<string> WaitForValueAsync(
            FrameWorld world,
            Document frameDocument,
            string script,
            string expected)
        {
            var watch = Stopwatch.StartNew();
            string last = null;
            while (watch.Elapsed < DeliveryTimeout)
            {
                last = Eval(world, frameDocument, script);
                if (string.Equals(last, expected, StringComparison.Ordinal))
                {
                    return last;
                }

                await Task.Delay(50);
            }

            return last;
        }

        private static async Task<FrameWorld> CreateSingleFrameDocumentAsync()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                "<html><body>" +
                "<iframe id='first' name='first-frame' src='https://parent.test/first'></iframe>" +
                "</body></html>",
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var world = new FrameWorld(engine, document, null, null);
            var first = await AttachFrameDocumentAsync(world, "first", "https://parent.test/first");
            return world with { FirstFrameDocument = first };
        }

        private static async Task<FrameWorld> CreateTwoFrameDocumentAsync()
        {
            var baseUri = new Uri("https://parent.test/page");
            var document = new HtmlParser(
                "<html><body>" +
                "<iframe id='first' name='first-frame' src='https://parent.test/first'></iframe>" +
                "<iframe id='second' name='second-frame' src='https://parent.test/second'></iframe>" +
                "</body></html>",
                baseUri).Parse();

            var engine = new FenJsBrowserScriptEngine(CreateHost())
            {
                Sandbox = SandboxPolicy.AllowAll
            };

            await engine.SetDomAsync(document.DocumentElement, baseUri);

            var world = new FrameWorld(engine, document, null, null);
            var first = await AttachFrameDocumentAsync(world, "first", "https://parent.test/first");
            var second = await AttachFrameDocumentAsync(world, "second", "https://parent.test/second");
            return world with { FirstFrameDocument = first, SecondFrameDocument = second };
        }

        private static async Task<Document> AddFrameAsync(FrameWorld world, string id, string name, string src)
        {
            var frame = world.Document.CreateElement("iframe");
            frame.SetAttribute("id", id);
            frame.SetAttribute("name", name);
            frame.SetAttribute("src", src);
            world.Document.Body.AppendChild(frame);

            return await AttachFrameDocumentAsync(world, id, src);
        }

        private static async Task<Document> AttachFrameDocumentAsync(FrameWorld world, string id, string src)
        {
            var frameUri = new Uri(src);
            var frameDocument = new HtmlParser(
                $"<html><body id='{id}-body'></body></html>",
                frameUri).Parse();

            var frameElement = Assert.IsType<Element>(world.Document.GetElementById(id));
            frameElement.AppendChild(frameDocument);
            await world.Engine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);
            return frameDocument;
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
