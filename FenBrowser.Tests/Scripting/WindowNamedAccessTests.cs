using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML §7.2.2.3 named access on the Window object, through the WindowProperties named
/// properties object in the global's prototype chain.
/// </summary>
public sealed class WindowNamedAccessTests
{
    [Fact]
    public async Task AnIdIsReachableFromWindowAndAsABareName()
    {
        var engine = await CreateEngineAsync("<div id=foo></div>");
        Assert.Equal("true", Eval(engine, "window.foo === document.getElementById('foo')"));
        Assert.Equal("true", Eval(engine, "foo === document.getElementById('foo')"));
        Assert.Equal("true", Eval(engine, "'foo' in window"));
        Assert.Equal("false", Eval(engine, "Object.prototype.hasOwnProperty.call(window, 'foo')"));
    }

    [Fact]
    public async Task FormsImagesEmbedsAndObjectsAreNamedByTheirNameAttribute()
    {
        var engine = await CreateEngineAsync("<form name=f></form><img name=pic><span name=notme></span>");
        Assert.Equal("FORM", Eval(engine, "f.tagName"));
        Assert.Equal("IMG", Eval(engine, "window.pic.tagName"));
        Assert.Equal("undefined", Eval(engine, "typeof window.notme"));
    }

    [Fact]
    public async Task SeveralNamedObjectsComeBackAsALiveCollection()
    {
        var engine = await CreateEngineAsync("<a id=dup></a><b id=dup></b>");
        Assert.Equal("2:A,B", Eval(engine, "window.dup.length + ':' + dup[0].tagName + ',' + dup[1].tagName"));
    }

    [Fact]
    public async Task ANameFollowsTheDocument()
    {
        var engine = await CreateEngineAsync(string.Empty);
        Assert.Equal("undefined", Eval(engine, "typeof later"));
        Eval(engine, "var d = document.createElement('div'); d.id = 'later'; document.body.appendChild(d);");
        Assert.Equal("DIV", Eval(engine, "later.tagName"));
        Eval(engine, "d.id = 'renamed';");
        Assert.Equal("undefined", Eval(engine, "typeof later"));
        Assert.Equal("DIV", Eval(engine, "renamed.tagName"));
        Eval(engine, "d.remove();");
        Assert.Equal("undefined", Eval(engine, "typeof window.renamed"));
    }

    [Fact]
    public async Task WhatTheWindowDefinesWins()
    {
        var engine = await CreateEngineAsync("<div id=location></div><div id=mine></div>");
        Assert.Equal("object", Eval(engine, "typeof window.location.href === 'string' ? 'object' : 'element'"));
        Eval(engine, "var mine = 5;");
        Assert.Equal("5", Eval(engine, "String(mine)"));
        Eval(engine, "window.foo2 = 1;");
        Assert.Equal("1", Eval(engine, "String(foo2)"));
    }

    [Fact]
    public async Task WindowPropertiesSitsBetweenWindowAndEventTarget()
    {
        var engine = await CreateEngineAsync(string.Empty);
        Assert.Equal("[object WindowProperties]", Eval(engine, "Object.prototype.toString.call(Object.getPrototypeOf(Window.prototype))"));
        Assert.Equal("true", Eval(engine, "window instanceof EventTarget"));
        Assert.Equal("false", Eval(engine, "Reflect.defineProperty(Object.getPrototypeOf(Window.prototype), 'x', { value: 1 })"));
    }

    private static string Eval(FenJsBrowserScriptEngine engine, string script)
    {
        var text = engine.Evaluate(script)?.ToString() ?? "<null>";
        return text is "True" or "False" ? text.ToLowerInvariant() : text;
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync(string body)
    {
        var baseUri = new Uri("https://example.test/");
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
