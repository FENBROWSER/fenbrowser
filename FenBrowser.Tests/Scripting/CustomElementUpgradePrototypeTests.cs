using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML "upgrade an element": the HTMLElement constructor gives an upgraded element its
/// definition's prototype, so members are inherited, not copied onto the element.
/// Copying them froze a base class's plain field as an own property that shadowed the
/// subclass's accessor of the same name - Polymer's _template, which left YouTube's
/// ytd-app without its template.
/// </summary>
public sealed class CustomElementUpgradePrototypeTests
{
    [Fact]
    public async Task UpgradedElement_InheritsItsClassMembers_InsteadOfOwnCopies()
    {
        var engine = await StartAsync(
            """
            <x-app></x-app>
            <script>
            function Base() { return Reflect.construct(HTMLElement, [], new.target); }
            Base.prototype = Object.create(HTMLElement.prototype);
            Base.prototype.constructor = Base;
            Base.prototype._template = undefined;
            Base.prototype.greet = function () { return 'base'; };
            class App extends Base {
                get _template() { return 'stamped'; }
                greet() { return 'app'; }
            }
            customElements.define('x-app', App);
            var el = document.querySelector('x-app');
            globalThis.__r = JSON.stringify({
                template: el._template,
                greet: el.greet(),
                ownTemplate: Object.prototype.hasOwnProperty.call(el, '_template'),
                ownGreet: Object.prototype.hasOwnProperty.call(el, 'greet'),
                proto: Object.getPrototypeOf(el) === App.prototype,
                instance: el instanceof App
            });
            </script>
            """);

        try
        {
            Assert.Equal(
                "{\"template\":\"stamped\",\"greet\":\"app\",\"ownTemplate\":false,\"ownGreet\":false,\"proto\":true,\"instance\":true}",
                engine.Evaluate("String(globalThis.__r)")?.ToString());
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
