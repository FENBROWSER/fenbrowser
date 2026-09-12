using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class FenJsCustomElementReactionTests
{
    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string body)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body>" + body + "</body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }

    // HTML 4.13.5 "upgrade an element" step 4 and 4.13.4 attribute change
    // reactions: observed attributes already present fire on upgrade, later
    // changes fire whatever path changes them (setAttribute, a reflected
    // property, removeAttribute), and unobserved attributes stay silent.
    // Polymer's <dom-module> registers its id from exactly this callback.
    [Fact]
    public async Task AttributeChangedCallbackFiresOnUpgradeAndOnLaterChanges()
    {
        var engine = await CreateEngineAsync("<x-mod id='styles' other='1'></x-mod>");

        var result = engine.Evaluate("""
            (function () {
                var calls = [];
                class Mod extends HTMLElement {
                    static get observedAttributes() { return ['id', 'label']; }
                    attributeChangedCallback(name, oldValue, newValue) {
                        calls.push(name + ':' + oldValue + '>' + newValue);
                    }
                }
                customElements.define('x-mod', Mod);
                var el = document.querySelector('x-mod');
                el.setAttribute('label', 'first');
                el.setAttribute('label', 'second');
                el.id = 'renamed';
                el.setAttribute('other', '2');
                el.removeAttribute('label');
                var created = document.createElement('x-mod');
                created.setAttribute('label', 'fresh');
                return calls.join('|');
            })();
            """);

        Assert.Equal(
            "id:null>styles|label:null>first|label:first>second|id:styles>renamed|label:second>null|label:null>fresh",
            result?.ToString());
    }

    // WebIDL 3.7.10: NamedNodeMap and DOMTokenList are iterable and array-like,
    // so Array.from, spread and for-of see every entry.
    [Fact]
    public async Task AttributeAndTokenCollectionsAreIterable()
    {
        var engine = await CreateEngineAsync("<span id='s' a='1' b='2' class='p q'></span>");

        var result = engine.Evaluate("""
            (function () {
                var s = document.getElementById('s');
                var names = Array.from(s.attributes).map(function (a) { return a.name; });
                var spread = [...s.attributes].length;
                var count = 0;
                for (var a of s.attributes) count++;
                return [names.join(','), spread, count, Array.from(s.classList).join('+'), s.attributes instanceof NamedNodeMap].join('|');
            })();
            """);

        Assert.Equal("id,a,b,class|4|4|p+q|true", result?.ToString());
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
