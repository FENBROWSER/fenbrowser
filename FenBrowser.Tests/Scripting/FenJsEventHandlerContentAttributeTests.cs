using System.Diagnostics;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML 8.1.8.1 event handler content attributes: setting an on* attribute
/// runs the attribute change steps, which install the handler it names, and
/// removing the attribute deactivates it. A handler a script sets after the
/// document loaded has to behave like one written in the markup. Acid3 test 48
/// sets an iframe's onload this way; test 80 then burned its whole retry budget
/// waiting for a handler that never existed.
/// </summary>
public sealed class FenJsEventHandlerContentAttributeTests
{
    [Fact]
    public async Task SetAttribute_InstallsHandlerThatSeesThisAndEvent()
    {
        var engine = await CreateEngineAsync("<html><body><div id='d'></div></body></html>");

        engine.Evaluate("""
            var d = document.getElementById('d');
            d.setAttribute('onclick', "globalThis.__seen = this.id + ':' + event.type;");
            d.dispatchEvent(new Event('click'));
            """);

        Assert.Equal("d:click", engine.Evaluate("String(globalThis.__seen)")?.ToString());
    }

    [Fact]
    public async Task SetAttribute_ReplacesHandlerAssignedThroughProperty()
    {
        var engine = await CreateEngineAsync("<html><body><div id='d'></div></body></html>");

        engine.Evaluate("""
            globalThis.__log = [];
            var d = document.getElementById('d');
            d.onclick = function () { globalThis.__log.push('property'); };
            d.setAttribute('onclick', "globalThis.__log.push('attribute');");
            d.dispatchEvent(new Event('click'));
            """);

        Assert.Equal("attribute", engine.Evaluate("globalThis.__log.join(',')")?.ToString());
    }

    [Fact]
    public async Task RemoveAttribute_DeactivatesHandler()
    {
        var engine = await CreateEngineAsync("<html><body><div id='d'></div></body></html>");

        engine.Evaluate("""
            globalThis.__log = [];
            var d = document.getElementById('d');
            d.setAttribute('onclick', "globalThis.__log.push('attribute');");
            d.removeAttribute('onclick');
            d.dispatchEvent(new Event('click'));
            """);

        Assert.Equal(string.Empty, engine.Evaluate("globalThis.__log.join(',')")?.ToString());
    }

    [Fact]
    public async Task RemoveAttribute_WhenAbsent_KeepsHandlerAssignedThroughProperty()
    {
        var engine = await CreateEngineAsync("<html><body><div id='d'></div></body></html>");

        engine.Evaluate("""
            globalThis.__log = [];
            var d = document.getElementById('d');
            d.onclick = function () { globalThis.__log.push('property'); };
            d.removeAttribute('onclick');
            d.dispatchEvent(new Event('click'));
            """);

        Assert.Equal("property", engine.Evaluate("globalThis.__log.join(',')")?.ToString());
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string html)
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
