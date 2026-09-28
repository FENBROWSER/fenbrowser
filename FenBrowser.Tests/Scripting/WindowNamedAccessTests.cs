using System;
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
}
