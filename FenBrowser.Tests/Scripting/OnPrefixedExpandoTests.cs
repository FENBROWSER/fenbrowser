using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML 8.1.8.2: event handler IDL attributes are all lower case (onclick, onload).
/// Any other property whose name happens to start with "on" is an ordinary expando
/// and keeps its exact name. Element property writes folded every on-prefixed name
/// to lower case, so YouTube's image loader lost img.onViewportEntered - the
/// callback its IntersectionObserver reads back - and no thumbnail loaded.
/// </summary>
public sealed class OnPrefixedExpandoTests
{
    [Theory]
    [InlineData("var img = document.createElement('img'); var f = function () {}; img.onViewportEntered = f; img.onViewportEntered === f")]
    [InlineData("var d = document.createElement('div'); d.onCustomThing = 5; d.onCustomThing === 5 && d.oncustomthing === undefined")]
    [InlineData("var d = document.createElement('div'); d.onClick = 1; d.onClick === 1 && d.onclick !== 1")]
    [InlineData("var d = document.createElement('div'); d.onViewportEntered = 1; Object.prototype.hasOwnProperty.call(d, 'onViewportEntered')")]
    public async Task MixedCaseOnPrefixedNames_AreOrdinaryExpandos(string assertion)
    {
        var engine = await CreateEngineAsync();
        Assert.Equal(true, engine.Evaluate(assertion));
    }

    [Fact]
    public async Task LowerCaseEventHandlerProperty_StillHandlesItsEvent()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate("""
            globalThis.__log = [];
            var d = document.createElement('div');
            d.onclick = function () { globalThis.__log.push('onclick'); };
            d.onClick = function () { globalThis.__log.push('onClick'); };
            d.dispatchEvent(new Event('click'));
            """);

        Assert.Equal("onclick", engine.Evaluate("globalThis.__log.join(',')")?.ToString());
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync()
    {
        var baseUri = new Uri("https://example.test/");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        { Sandbox = SandboxPolicy.AllowAll };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }
}
