using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// DOM 4.2.3 "insert" step 7.7 upgrades an inserted inclusive descendant only when
    /// it is connected. Trees built while disconnected - template content moved into a
    /// fragment, innerHTML on a detached element - keep their custom elements
    /// un-upgraded until they are inserted into the document. Polymer records child
    /// indexes over a nested template's content after moving it into a fragment;
    /// upgrading there stamped shadow content into the template and YouTube's stamping
    /// threw "Cannot set properties of undefined (setting '__dataHost')".
    /// </summary>
    public sealed class CustomElementInsertionUpgradeTests
    {
        private const string Setup =
            "var constructed = 0;" +
            "class XU extends HTMLElement { constructor() { super(); constructed++; this.upgraded = true; } }" +
            "customElements.define('x-u', XU);";

        [Theory]
        // Template content moved into a fragment (Polymer's nested-template parse).
        [InlineData("var t = document.createElement('template'); t.innerHTML = '<x-u></x-u>';" +
                    "var f = t.content.ownerDocument.createDocumentFragment(); f.appendChild(t.content);" +
                    "f.firstChild.upgraded === undefined && constructed === 0")]
        // innerHTML on a detached element.
        [InlineData("var d = document.createElement('div'); d.innerHTML = '<x-u></x-u>';" +
                    "d.firstChild.upgraded === undefined && constructed === 0")]
        // appendChild into a detached parent.
        [InlineData("var d = document.createElement('div'); var e = document.createElement('section');" +
                    "e.innerHTML = '<x-u></x-u>'; d.appendChild(e);" +
                    "e.firstChild.upgraded === undefined && constructed === 0")]
        // Connecting the tree upgrades it.
        [InlineData("var d = document.createElement('div'); d.innerHTML = '<x-u></x-u>';" +
                    "document.body.appendChild(d); d.firstChild.upgraded === true && d.firstChild instanceof XU")]
        // innerHTML on a connected element upgrades immediately.
        [InlineData("document.body.innerHTML = '<p><x-u></x-u></p>';" +
                    "document.body.querySelector('x-u').upgraded === true")]
        // insertAdjacentHTML on a detached element does not.
        [InlineData("var d = document.createElement('div'); d.insertAdjacentHTML('beforeend', '<x-u></x-u>');" +
                    "d.firstChild.upgraded === undefined && constructed === 0")]
        public async Task InsertionUpgradesOnlyConnectedElements(string assertion)
        {
            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate(Setup + assertion));
        }

        [Fact]
        public async Task NestedTemplateContentKeepsItsShapeForIndexedLookups()
        {
            // An element whose constructor adds children - like a ShadyDOM-stamped
            // Polymer element - must not run inside the moved template content, or
            // every child index recorded from the content before the move is wrong.
            const string script =
                "class XStamp extends HTMLElement { constructor() { super(); this.insertBefore(document.createComment('stamped'), this.firstChild); } }" +
                "customElements.define('x-stamp', XStamp);" +
                "var t = document.createElement('template'); t.innerHTML = '<a><x-stamp><span></span></x-stamp></a>';" +
                "var f = t.content.ownerDocument.createDocumentFragment(); f.appendChild(t.content);" +
                "var stamp = f.firstChild.firstChild;" +
                "stamp.childNodes.length === 1 && stamp.firstChild.localName === 'span' &&" +
                "document.importNode(f, true).firstChild.firstChild.childNodes.length === 2";

            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate(script));
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
