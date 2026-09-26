using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// DOM 2.9 dispatch on an EventTarget that is not a node - <c>new EventTarget()</c>, a
/// subclass, and every script-built interface (MediaKeySession, SourceBuffer, TextTrack,
/// AbortSignal). Its event path is the target alone.
/// </summary>
public sealed class ScriptEventTargetDispatchTests
{
    [Fact]
    public async Task ListenersSeeTheTargetAsCurrentTargetAndPathAtTarget()
    {
        var engine = await CreateEngineAsync();
        var result = engine.Evaluate(
            "(function () {" +
            "  class C extends EventTarget {}" +
            "  var t = new C(), ev = new Event('x'), seen = '';" +
            "  t.addEventListener('x', function (e) { seen = (e.currentTarget === t) + ':' + e.eventPhase + ':' + (this === t) + ':' + (e.composedPath()[0] === t); });" +
            "  t.dispatchEvent(ev);" +
            "  return seen + '|' + ev.currentTarget + ':' + ev.eventPhase + ':' + ev.composedPath().length;" +
            "})()")?.ToString();

        Assert.Equal("true:2:true:true|null:0:0", result);
    }

    [Fact]
    public async Task HandleEventObjectsRunAndStopImmediatePropagationStops()
    {
        var engine = await CreateEngineAsync();
        var result = engine.Evaluate(
            "(function () {" +
            "  var t = new EventTarget(), log = [];" +
            "  var listener = { handleEvent: function () { log.push(this === listener ? 'object' : 'wrong-this'); } };" +
            "  t.addEventListener('x', listener);" +
            "  t.addEventListener('x', function (e) { log.push('stop'); e.stopImmediatePropagation(); });" +
            "  t.addEventListener('x', function () { log.push('never'); });" +
            "  t.dispatchEvent(new Event('x'));" +
            "  return log.join(',');" +
            "})()")?.ToString();

        Assert.Equal("object,stop", result);
    }

    [Fact]
    public async Task AThrowingListenerIsReportedAndTheNextOneStillRuns()
    {
        var engine = await CreateEngineAsync();
        var result = engine.Evaluate(
            "(function () {" +
            "  var t = new EventTarget(), log = [];" +
            "  var removed = function () { log.push('removed-ran'); };" +
            "  globalThis.reportError = function (e) { log.push('reported:' + e.message); };" +
            "  t.addEventListener('x', function () { t.removeEventListener('x', removed); throw new Error('boom'); });" +
            "  t.addEventListener('x', removed);" +
            "  t.addEventListener('x', function () { log.push('next'); });" +
            "  t.dispatchEvent(new Event('x'));" +
            "  return log.join(',');" +
            "})()")?.ToString();

        Assert.Equal("reported:boom,next", result);
    }

    [Fact]
    public async Task DispatchingAnEventAlreadyInFlightThrows()
    {
        var engine = await CreateEngineAsync();
        var result = engine.Evaluate(
            "(function () {" +
            "  var t = new EventTarget(), ev = new Event('x'), name = 'none';" +
            "  t.addEventListener('x', function () { try { t.dispatchEvent(ev); } catch (e) { name = e.name; } });" +
            "  t.dispatchEvent(ev);" +
            "  return name;" +
            "})()")?.ToString();

        Assert.Equal("InvalidStateError", result);
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
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        await engine.SetDomAsync(document.DocumentElement, baseUri);
        return engine;
    }
}
