using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML 4.13.5 "try to upgrade an element" runs for each inclusive descendant of an
/// inserted node in tree order, looking the definition up by the element's own local
/// name (or its is value, for a customized built-in). The engine used to search the
/// inserted subtree once per defined name, so YouTube's hundreds of definitions turned
/// each Polymer insertion into hundreds of subtree searches.
/// </summary>
public sealed class CustomElementUpgradeWalkTests
{
    [Fact]
    public async Task InsertedSubtree_UpgradesInTreeOrder_ByLocalNameAndIsValue()
    {
        var engine = await StartAsync(
            """
            <script>
            var order = [];
            function track(tag) {
                return class extends HTMLElement { connectedCallback() { order.push(tag + '#' + this.id); } };
            }
            for (var i = 0; i < 50; i++) customElements.define('x-unused-' + i, class extends HTMLElement {});
            customElements.define('x-b', track('x-b'));
            customElements.define('x-a', track('x-a'));
            customElements.define('fancy-button', class extends HTMLButtonElement {
                connectedCallback() { order.push('fancy#' + this.id); }
            }, { extends: 'button' });
            var host = document.createElement('div');
            host.innerHTML =
                '<x-a id="1"><x-b id="2"></x-b></x-a>' +
                '<button is="fancy-button" id="3"></button>' +
                '<div is="fancy-button" id="4"></div>' +
                '<x-b id="5"></x-b>';
            document.body.appendChild(host);
            globalThis.__r = order.join(',');
            </script>
            """);

        try
        {
            Assert.Equal("x-a#1,x-b#2,fancy#3,x-b#5", engine.Evaluate("String(globalThis.__r)")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static async Task<FenJsBrowserScriptEngine> StartAsync(string body)
    {
        var baseUri = new Uri("https://fixture.test/index.html");
        var document = new HtmlParser("<html><body>" + body + "</body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }
}
