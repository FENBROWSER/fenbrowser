using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// DOMTokenList on an element's class attribute: every mutation must reflect
/// in className the same way Closure's goog.dom.classlist expects.
/// </summary>
public sealed class FenJsClassListTests
{
    [Fact]
    public async Task AddThenRemoveRoundTripsThroughClassName()
    {
        var engine = await CreateEngineAsync("<html><body><td id=\"t\" class=\"tile\"></td></body></html>");

        var result = engine.Evaluate("""
            (function () {
                var t = document.getElementById('t');
                var out = [];
                t.classList.add('sel'); out.push('add:' + t.className);
                out.push('has:' + t.classList.contains('sel'));
                t.classList.remove('sel'); out.push('remove:' + t.className);
                out.push('has:' + t.classList.contains('sel'));
                t.classList.add('a', 'b'); t.classList.remove('a'); out.push('multi:' + t.className);
                t.classList.toggle('b'); out.push('toggle:' + t.className);
                t.classList.toggle('c', true); t.classList.toggle('c', true); out.push('force:' + t.className);
                return out.join(' ');
            })();
            """);

        Assert.Equal("add:tile sel has:true remove:tile has:false multi:tile b toggle:tile force:tile c", result?.ToString());
    }

    [Fact]
    public async Task RemoveOnASecondClassListReferenceSeesTheSameAttribute()
    {
        // Closure fetches element.classList fresh on every call; two token
        // lists for one element must observe each other's writes.
        var engine = await CreateEngineAsync("<html><body><td id=\"t\" class=\"tile\"></td></body></html>");

        var result = engine.Evaluate("""
            (function () {
                var t = document.getElementById('t');
                var a = t.classList; var b = t.classList;
                a.add('sel');
                b.remove('sel');
                return t.className + '|' + t.getAttribute('class') + '|' + a.contains('sel');
            })();
            """);

        Assert.Equal("tile|tile|false", result?.ToString());
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
