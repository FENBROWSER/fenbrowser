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

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
