using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class FenJsCustomElementInsertionTests
{
    // ECMA-262 13.10.2: `v instanceof X` calls X[@@hasInstance], which a class
    // extending HTMLElement inherits from the host constructor. The host's brand
    // check must answer only for HTMLElement itself; a subclass is answered by
    // the ordinary prototype walk. Polymer's mixin machinery
    // (`superCtor.prototype instanceof PropertiesMixin`) depends on this.
    [Fact]
    public async Task InstanceofOnHtmlElementSubclassUsesOrdinaryPrototypeWalk()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body><div id='d'></div></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var result = engine.Evaluate("""
            (function () {
                class Base extends HTMLElement {}
                class Mid extends Base {}
                class Top extends Mid {}
                var div = document.getElementById('d');
                return [
                    Base.prototype instanceof Base,
                    Mid.prototype instanceof Base,
                    Base.prototype instanceof Mid,
                    Top.prototype instanceof Mid,
                    div instanceof HTMLElement,
                    div instanceof Base,
                    Object.getPrototypeOf(Top) === Mid
                ].join('|');
            })();
            """);

        Assert.Equal("false|true|false|true|true|false|true", result?.ToString());
    }

    // DOM 4.2.3 "insert": a DocumentFragment hands its children to the parent, so
    // the custom-element upgrade must cover the nodes that moved rather than the
    // (now empty) fragment; and a ShadowRoot is a parent like any other.
    [Fact]
    public async Task CustomElementsUpgradeWhenInsertedThroughFragmentsAndShadowRoots()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body><div id='host'></div><div id='light'></div></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        var result = engine.Evaluate("""
            (function () {
                var connected = [];
                class Widget extends HTMLElement {
                    connectedCallback() { connected.push(this.id); this.textContent = 'w:' + this.id; }
                }
                customElements.define('x-widget', Widget);

                function fragmentWith(id) {
                    var fragment = document.createDocumentFragment();
                    var wrapper = document.createElement('div');
                    wrapper.innerHTML = '<x-widget id="' + id + '"></x-widget>';
                    fragment.appendChild(wrapper);
                    return fragment;
                }

                document.getElementById('light').appendChild(fragmentWith('light'));
                var shadow = document.getElementById('host').attachShadow({ mode: 'open' });
                shadow.appendChild(fragmentWith('shadow-frag'));
                var direct = document.createElement('div');
                direct.innerHTML = '<x-widget id="shadow-direct"></x-widget>';
                shadow.insertBefore(direct, null);

                var light = document.querySelector('#light x-widget');
                var viaFragment = shadow.querySelector('#shadow-frag');
                var viaDirect = shadow.querySelector('#shadow-direct');
                return [
                    light.textContent,
                    viaFragment.textContent,
                    viaDirect.textContent,
                    connected.join(',')
                ].join('|');
            })();
            """);

        Assert.Equal("w:light|w:shadow-frag|w:shadow-direct|light,shadow-frag,shadow-direct", result?.ToString());
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
