using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// CSS View Transitions Level 2: document.startViewTransition takes either the
/// update callback itself or an options object { update, types }. React 19's commit
/// path uses the object form, and treating that as "no callback" drops every DOM
/// mutation of the commit while still returning settled promises - the page renders
/// nothing and nothing is thrown.
/// </summary>
[Collection("Engine Tests")]
public sealed class StartViewTransitionOptionsTests : IDisposable
{
    public StartViewTransitionOptionsTests() => BrowserScriptEngineRuntime.Reset();

    public void Dispose() => BrowserScriptEngineRuntime.Reset();

    private static JsHostAdapter CreateHost()
        => new(navigate: _ => { }, post: (_, _) => { }, status: _ => { }, log: _ => { });

    private static FenJsBrowserScriptEngine CreateEngine()
    {
        var baseUri = new Uri("https://viewtransition.test/page");
        var document = new HtmlParser("<html><body><div id='host'></div></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    [Fact]
    public void CallbackFormRunsTheUpdate()
    {
        var engine = CreateEngine();

        Assert.Equal("ran", engine.Evaluate(@"
            globalThis.flag = 'not-run';
            document.startViewTransition(function () { globalThis.flag = 'ran'; });
            globalThis.flag;")?.ToString());
    }

    [Fact]
    public void OptionsObjectFormRunsTheUpdate()
    {
        var engine = CreateEngine();

        Assert.Equal("ran", engine.Evaluate(@"
            globalThis.flag = 'not-run';
            document.startViewTransition({ update: function () { globalThis.flag = 'ran'; } });
            globalThis.flag;")?.ToString());
    }

    // The mutations inside update() must actually reach the DOM - that is the whole
    // point of the callback, and what was being dropped.
    [Fact]
    public void OptionsObjectFormAppliesDomMutations()
    {
        var engine = CreateEngine();

        Assert.Equal("hello", engine.Evaluate(@"
            document.startViewTransition({
                update: function () { document.getElementById('host').textContent = 'hello'; }
            });
            document.getElementById('host').textContent;")?.ToString());
    }

    [Fact]
    public void BothFormsStillReturnTheTransitionPromises()
    {
        var engine = CreateEngine();

        Assert.Equal("function,function,function", engine.Evaluate(@"
            var t = document.startViewTransition({ update: function () {} });
            [typeof t.ready.then, typeof t.updateCallbackDone.then, typeof t.finished.then].join(',');")?.ToString());
    }

    [Fact]
    public void AnOptionsObjectWithNoUpdateIsNotAnError()
    {
        var engine = CreateEngine();

        Assert.Equal("ok", engine.Evaluate(@"
            document.startViewTransition({ types: ['nav'] });
            'ok';")?.ToString());
    }
}
