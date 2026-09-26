using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

// HTML 7.2.2.3 named access on the Window object.
[Collection("Engine Tests")]
public sealed class WindowNamedAccessTests : IDisposable
{
    public WindowNamedAccessTests()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    public void Dispose()
    {
        BrowserScriptEngineRuntime.Reset();
    }

    private static FenJsBrowserScriptEngine Load(string html)
    {
        var baseUri = new Uri("https://named.test/page");
        var document = new HtmlParser(html, baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(new JsHostAdapter(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { }))
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    [Fact]
    public void ElementIdsAreReachableOnWindowAndAsBareIdentifiers()
    {
        var engine = Load("<html><body><div id='panel'></div></body></html>");

        Assert.Equal("True", engine.Evaluate(
            "window.panel === document.getElementById('panel') && panel === window.panel && 'panel' in window")?.ToString());
    }

    [Fact]
    public void NamedFormsAndImagesAreReachableButOtherNamedElementsAreNot()
    {
        var engine = Load("<html><body><form name='login'></form><img name='logo'><input name='field'></body></html>");

        Assert.Equal("FORM|IMG|undefined", engine.Evaluate(
            "[login.tagName, window.logo.tagName, typeof window.field].join('|')")?.ToString());
    }

    [Fact]
    public void SeveralMatchesAreReturnedAsALiveCollection()
    {
        var engine = Load("<html><body><p id='dup'></p><p id='dup'></p></body></html>");

        Assert.Equal("2|3", engine.Evaluate(
            "var c = window.dup; var n = c.length; document.body.appendChild(document.createElement('p')).id = 'dup'; n + '|' + c.length")?.ToString());
    }

    [Fact]
    public void WindowMembersAndDeclaredGlobalsShadowNamedElements()
    {
        var engine = Load("<html><body><div id='alert'></div><div id='later'></div></body></html>");

        Assert.Equal("function|mine", engine.Evaluate(
            "var later = 'mine'; typeof window.alert + '|' + later")?.ToString());
    }

    [Fact]
    public void RemovedElementsStopResolving()
    {
        var engine = Load("<html><body><div id='gone'></div></body></html>");

        Assert.Equal("object|undefined", engine.Evaluate(
            "var before = typeof window.gone; document.getElementById('gone').remove(); before + '|' + typeof window.gone")?.ToString());
    }

    [Fact]
    public void WindowInheritsEventTargetThroughWindowProperties()
    {
        var engine = Load("<html><body><div id='named3'></div></body></html>");

        Assert.Equal("true|WindowProperties|true|shadowing", engine.Evaluate(@"
            var npo = Object.getPrototypeOf(Window.prototype);
            EventTarget.prototype.named3 = 'shadowing';
            [window instanceof EventTarget,
             Object.prototype.toString.call(npo).slice(8, -1),
             Object.getPrototypeOf(npo) === EventTarget.prototype,
             window.named3].join('|')")?.ToString());
    }

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
