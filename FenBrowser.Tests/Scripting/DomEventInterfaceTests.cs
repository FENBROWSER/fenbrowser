using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// The event interfaces a page can construct, and the inheritance that has to
/// hold between them (DOM 4.4, UI Events, HTML 8.1).
/// </summary>
[Collection("Engine Tests")]
public sealed class DomEventInterfaceTests : IDisposable
{
    public DomEventInterfaceTests()
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

    private static FenJsBrowserScriptEngine CreateEngine()
    {
        var baseUri = new Uri("https://events.test/page");
        var document = new HtmlParser("<html><body><div id='d'></div></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    [Theory]
    // A bundle that references a missing one of these dies on the
    // ReferenceError before any of its other work runs.
    [InlineData("FocusEvent")]
    [InlineData("InputEvent")]
    [InlineData("CompositionEvent")]
    [InlineData("DragEvent")]
    [InlineData("ClipboardEvent")]
    [InlineData("AnimationEvent")]
    [InlineData("TransitionEvent")]
    [InlineData("PopStateEvent")]
    [InlineData("HashChangeEvent")]
    [InlineData("PageTransitionEvent")]
    [InlineData("ProgressEvent")]
    [InlineData("StorageEvent")]
    [InlineData("SubmitEvent")]
    [InlineData("FormDataEvent")]
    [InlineData("ToggleEvent")]
    [InlineData("PromiseRejectionEvent")]
    [InlineData("SecurityPolicyViolationEvent")]
    [InlineData("CloseEvent")]
    public void EventInterface_IsAConstructorOnTheGlobal(string name)
    {
        var engine = CreateEngine();

        Assert.Equal("function", engine.Evaluate($"typeof {name}")?.ToString());
    }

    [Fact]
    public void FocusEvent_CarriesItsInitAndInheritsUIEvent()
    {
        var engine = CreateEngine();

        Assert.Equal(
            "focusin|true|true",
            engine.Evaluate(
                "(function () {" +
                "  var target = document.getElementById('d');" +
                "  var event = new FocusEvent('focusin', { bubbles: true, relatedTarget: target });" +
                "  return [event.type, event.bubbles, event.relatedTarget === target].join('|');" +
                "})()")?.ToString());

        Assert.Equal(
            "True",
            engine.Evaluate("new FocusEvent('focus') instanceof UIEvent")?.ToString());
        Assert.Equal(
            "True",
            engine.Evaluate("new FocusEvent('focus') instanceof Event")?.ToString());
    }

    [Fact]
    public void ProgressEvent_ReportsItsLoadedAndTotal()
    {
        var engine = CreateEngine();

        Assert.Equal(
            "progress|true|40|100",
            engine.Evaluate(
                "(function () {" +
                "  var event = new ProgressEvent('progress'," +
                "    { lengthComputable: true, loaded: 40, total: 100 });" +
                "  return [event.type, event.lengthComputable, event.loaded, event.total].join('|');" +
                "})()")?.ToString());
    }

    [Fact]
    public void EventSubclass_KeepsItsOwnTypeWhenThePageReplacesTheGlobalEvent()
    {
        // The web-components polyfill assigns a wrapper over window.Event. The
        // built-in subclasses have to keep calling the real base constructor,
        // not whatever the global happens to hold at call time - a wrapper that
        // returns a fresh event leaves `this` untouched, and the event then
        // dispatches with an empty type.
        var engine = CreateEngine();

        Assert.Equal(
            "yt-action|1",
            engine.Evaluate(
                "(function () {" +
                "  var original = window.Event;" +
                "  window.Event = function (type, options) { return new original(type, options); };" +
                "  window.Event.prototype = original.prototype;" +
                "  var event = new CustomEvent('yt-action', { detail: 1 });" +
                "  return [event.type, event.detail].join('|');" +
                "})()")?.ToString());
    }

    [Fact]
    public void NodePrototype_InheritsEventTargetPrototype()
    {
        // DOM 4.4 "interface Node : EventTarget". Libraries patch
        // EventTarget.prototype and expect every node to see it.
        var engine = CreateEngine();

        Assert.Equal(
            "True",
            engine.Evaluate("Object.getPrototypeOf(Node.prototype) === EventTarget.prototype")?.ToString());
        Assert.Equal(
            "True",
            engine.Evaluate("document.getElementById('d') instanceof EventTarget")?.ToString());
        Assert.Equal(
            "patched",
            engine.Evaluate(
                "(function () {" +
                "  EventTarget.prototype.__fenProbe = function () { return 'patched'; };" +
                "  return document.getElementById('d').__fenProbe();" +
                "})()")?.ToString());
    }

    [Fact]
    public void ElementEventPlumbing_StaysNativeUnderTheEventTargetPolyfill()
    {
        // Node.prototype pins native addEventListener/dispatchEvent so the
        // polyfill sitting above it in the chain cannot shadow them with its
        // own listener map.
        var engine = CreateEngine();

        Assert.Equal(
            "1",
            engine.Evaluate(
                "(function () {" +
                "  var target = document.getElementById('d');" +
                "  var seen = 0;" +
                "  target.addEventListener('fen-test', function () { seen++; });" +
                "  target.dispatchEvent(new Event('fen-test'));" +
                "  return seen;" +
                "})()")?.ToString());
    }

    [Fact]
    public void WindowEventMethods_WorkWhenReachedThroughEventTargetPrototype()
    {
        // The window never ran the EventTarget constructor, so the polyfill has
        // to create its listener map on demand and hand the global's own native
        // implementation the call.
        var engine = CreateEngine();

        Assert.Equal(
            "1",
            engine.Evaluate(
                "(function () {" +
                "  var add = EventTarget.prototype.addEventListener;" +
                "  var dispatch = EventTarget.prototype.dispatchEvent;" +
                "  var seen = 0;" +
                "  add.call(window, 'fen-window-test', function () { seen++; });" +
                "  dispatch.call(window, new Event('fen-window-test'));" +
                "  return seen;" +
                "})()")?.ToString());
    }
}
