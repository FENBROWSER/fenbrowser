using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// DOM 2.7: an EventTarget has one event listener list, whichever way its
/// addEventListener/dispatchEvent are reached. For nodes the engine keeps that list;
/// the EventTarget.prototype methods kept a second one of their own. ShadyDOM in
/// noPatch mode calls the EventTarget.prototype methods as its "native" ones, so
/// Polymer's property-notify events never reached template listeners added on the
/// node - YouTube's watch page stayed single-column because query-matches-changed
/// never arrived.
/// </summary>
public sealed class EventTargetPrototypeOnNodesTests
{
    [Theory]
    [InlineData("EventTarget.prototype.addEventListener.call(d, 'x', function () { log.push('heard'); }); d.dispatchEvent(new Event('x'));")]
    [InlineData("d.addEventListener('x', function () { log.push('heard'); }); EventTarget.prototype.dispatchEvent.call(d, new Event('x'));")]
    [InlineData("document.addEventListener('x', function () { log.push('heard'); }); EventTarget.prototype.dispatchEvent.call(document, new Event('x'));")]
    [InlineData("document.body.addEventListener('x', function () { log.push('heard'); }); EventTarget.prototype.dispatchEvent.call(d, new Event('x', { bubbles: true }));")]
    public async Task NodeListeners_AreSharedWithEventTargetPrototypeMethods(string script)
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate("var log = []; var d = document.createElement('div'); document.body.appendChild(d);" + script);

        Assert.Equal("heard", engine.Evaluate("log.join(',')")?.ToString());
    }

    [Fact]
    public async Task RemoveThroughEventTargetPrototype_RemovesANodeListener()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate(
            "var log = []; var d = document.createElement('div'); var f = function () { log.push('heard'); };" +
            "d.addEventListener('x', f); EventTarget.prototype.removeEventListener.call(d, 'x', f); d.dispatchEvent(new Event('x'));");

        Assert.Equal(string.Empty, engine.Evaluate("log.join(',')")?.ToString());
    }

    [Fact]
    public async Task PlainEventTargets_KeepTheirOwnListenerList()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate(
            "var log = []; var t = new EventTarget(); t.addEventListener('x', function (e) { log.push(e.target === t); });" +
            "t.dispatchEvent(new Event('x'));");

        Assert.Equal("true", engine.Evaluate("log.join(',')")?.ToString());
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
