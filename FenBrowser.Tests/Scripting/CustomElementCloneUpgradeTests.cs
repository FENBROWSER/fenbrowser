using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting
{
    /// <summary>
    /// DOM 4.5 "clone a node" creates each copy through "create an element" in the
    /// copy's document, so a defined custom element comes back upgraded from
    /// importNode / cloneNode even while disconnected. Template contents' document
    /// has no registry, so nothing in it upgrades.
    /// </summary>
    public sealed class CustomElementCloneUpgradeTests
    {
        private const string Setup =
            "class XT extends HTMLElement { constructor() { super(); this.upgraded = true; } }" +
            "customElements.define('x-t', XT);" +
            "var t = document.createElement('template'); t.innerHTML = '<x-t></x-t>';";

        [Theory]
        [InlineData("t.content.firstChild.upgraded === undefined")]
        [InlineData("document.importNode(t.content, true).firstChild.upgraded === true")]
        [InlineData("document.importNode(t.content, true).cloneNode(true).firstChild.upgraded === true")]
        [InlineData("document.importNode(t.content, true).firstChild.cloneNode(true).upgraded === true")]
        [InlineData("document.importNode(t.content, true).firstChild instanceof XT")]
        [InlineData("t.content.cloneNode(true).firstChild.upgraded === undefined")]
        public async Task ClonesUpgradeOnlyInTheRealmDocument(string assertion)
        {
            var baseUri = new Uri("https://example.com/index.html");
            var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
            var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
            await engine.SetDomAsync(document.DocumentElement, baseUri);

            Assert.Equal(true, engine.Evaluate(Setup + assertion));
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
