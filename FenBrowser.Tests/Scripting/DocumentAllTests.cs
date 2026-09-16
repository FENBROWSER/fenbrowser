using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML 3.1.5 document.all, an HTMLAllCollection with the Annex B
/// [[IsHTMLDDA]] slot (ECMA-262 B.3.6).
/// </summary>
[Collection("Engine Tests")]
public sealed class DocumentAllTests : IDisposable
{
    public DocumentAllTests()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    public void Dispose()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    private static JsHostAdapter CreateHost()
        => new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });

    private static FenJsBrowserScriptEngine CreateEngine(string html)
    {
        var baseUri = new Uri("https://all.test/page");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    [Fact]
    public void DocumentAll_LiesAboutBeingUndefinedTheWayTheSpecSays()
    {
        var engine = CreateEngine("<html><body><div id='a'></div></body></html>");

        // B.3.6.1: typeof reports "undefined" and ToBoolean is false...
        Assert.Equal("undefined", engine.Evaluate("typeof document.all")?.ToString());
        Assert.Equal("False", engine.Evaluate("document.all ? true : false")?.ToString());

        // ...and loose equality with null/undefined holds...
        Assert.Equal("True", engine.Evaluate("document.all == null")?.ToString());
        Assert.Equal("True", engine.Evaluate("document.all == undefined")?.ToString());

        // ...but strict equality does not, which is the entire point: the
        // `value || value === document.all` idiom must not match a plain
        // undefined, or every unset value takes document.all's branch.
        Assert.Equal("False", engine.Evaluate("document.all === undefined")?.ToString());
        Assert.Equal("False", engine.Evaluate("undefined === document.all")?.ToString());
    }

    [Fact]
    public void DocumentAll_IsOneStableObject()
    {
        var engine = CreateEngine("<html><body></body></html>");

        Assert.Equal("True", engine.Evaluate("document.all === document.all")?.ToString());
    }

    [Fact]
    public void DocumentAll_IsALiveCollectionOfEveryElement()
    {
        var engine = CreateEngine(
            "<html><body><div id='first'></div><span id='second'></span></body></html>");

        Assert.Equal(
            engine.Evaluate("document.getElementsByTagName('*').length")?.ToString(),
            engine.Evaluate("document.all.length")?.ToString());
        Assert.Equal("DIV", engine.Evaluate("document.all.namedItem('first').tagName")?.ToString());
        Assert.Equal("SPAN", engine.Evaluate("document.all.item('second').tagName")?.ToString());
        Assert.Equal("HTML", engine.Evaluate("document.all[0].tagName")?.ToString());

        // Live, not a snapshot taken when the collection was created.
        Assert.Equal(
            "True",
            engine.Evaluate(
                "(function () {" +
                "  var before = document.all.length;" +
                "  document.body.appendChild(document.createElement('p'));" +
                "  return document.all.length === before + 1;" +
                "})()")?.ToString());
    }

    [Fact]
    public void DocumentAll_IsLegacyCallable()
    {
        var engine = CreateEngine("<html><body><div id='target'></div></body></html>");

        Assert.Equal("DIV", engine.Evaluate("document.all('target').tagName")?.ToString());
        Assert.Equal("True", engine.Evaluate("document.all() === null")?.ToString());
    }

    [Fact]
    public void ClosureStyleNullishGuard_LetsUndefinedThrough()
    {
        // The exact shape Closure (and polymer-resin through it) ships. Before
        // document.all existed this returned "sanitized" for every unset
        // binding, which is how YouTube's content area ended up hidden.
        var engine = CreateEngine("<html><body></body></html>");

        const string guard =
            "(function (value) { return value || value === document.all ? 'sanitized' : 'passthrough'; })";

        Assert.Equal("passthrough", engine.Evaluate(guard + "(undefined)")?.ToString());
        Assert.Equal("passthrough", engine.Evaluate(guard + "(null)")?.ToString());
        Assert.Equal("passthrough", engine.Evaluate(guard + "(false)")?.ToString());
        Assert.Equal("sanitized", engine.Evaluate(guard + "('x')")?.ToString());
        Assert.Equal("sanitized", engine.Evaluate(guard + "(document.all)")?.ToString());
    }
}
