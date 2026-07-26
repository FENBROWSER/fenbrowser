using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class FenJsDocumentFragmentTests
{
    [Fact]
    public async Task CustomElementMethodObservesLaterHelperAssignment()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body><x-late-helper></x-late-helper></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var result = engine.Evaluate("""
            (function () {
                var brand, helper;
                class LateHelperElement extends HTMLElement {
                    constructor() {
                        super(...arguments);
                        brand.add(this);
                    }
                    connectedCallback() {
                        helper.call(this);
                    }
                }
                brand = new WeakSet(),
                helper = function () { this.setAttribute('ready', 'yes'); },
                customElements.define('x-late-helper', LateHelperElement);
                return document.querySelector('x-late-helper').getAttribute('ready');
            })();
            """);

        Assert.Equal("yes", result?.ToString());
    }

    [Fact]
    public async Task ShadowRootSupportsPrependAndEventTargetMethods()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var result = engine.Evaluate("""
            (function () {
                var host = document.createElement('div');
                document.body.appendChild(host);
                var root = host.attachShadow({ mode: 'open' });
                var tail = document.createElement('span');
                tail.id = 'tail';
                root.appendChild(tail);
                var originalQuerySelectorAll = DocumentFragment.prototype.querySelectorAll;
                DocumentFragment.prototype.querySelectorAll = function (selector) {
                    return originalQuerySelectorAll.call(this, selector);
                };
                var style = document.createElement('style');
                root.prepend('head', style);
                var observed = false;
                root.addEventListener('ready', function () { observed = true; });
                root.dispatchEvent(new Event('ready'));
                return root.childNodes.length === 3 &&
                    root.firstChild.textContent === 'head' &&
                    root.childNodes[1] === style &&
                    root.lastChild === tail &&
                    root.querySelectorAll('span').length === 1 &&
                    observed;
            })();
            """);

        Assert.Equal("True", result?.ToString());
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
