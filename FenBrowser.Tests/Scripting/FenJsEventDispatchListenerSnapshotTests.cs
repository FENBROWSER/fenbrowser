using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// DOM §2.9 "inner invoke": the listener list is cloned before any listener
/// runs. A listener added during a dispatch waits for the next event; one
/// removed during the dispatch is not called. A listener that re-registers
/// itself on every event (web-vitals' onLoad helper does) must not run its own
/// new copy inside the same dispatch: walking the live list did, forever.
/// </summary>
public sealed class FenJsEventDispatchListenerSnapshotTests
{
    [Fact]
    public async Task ListenerAddedDuringDispatch_RunsOnTheNextDispatchOnly()
    {
        var baseUri = new Uri("https://fixture.test/index.html");
        var document = new HtmlParser(
            """
            <html><body><img id="pic"><script>
            var count = 0;
            function selfSpawning() {
                count++;
                if (count > 500) throw new Error('runaway dispatch');
                window.addEventListener('probe', function () { count++; }, true);
            }
            window.addEventListener('probe', selfSpawning, true);
            var pic = document.getElementById('pic');
            pic.dispatchEvent(new Event('probe'));
            globalThis.__afterFirst = count;
            pic.dispatchEvent(new Event('probe'));
            globalThis.__afterSecond = count;

            var removedRan = false;
            function a() { pic.removeEventListener('ping', b); }
            function b() { removedRan = true; }
            pic.addEventListener('ping', a);
            pic.addEventListener('ping', b);
            pic.dispatchEvent(new Event('ping'));
            globalThis.__removedRan = removedRan;

            var onceRuns = 0;
            pic.addEventListener('pong', function () { onceRuns++; }, { once: true });
            pic.dispatchEvent(new Event('pong'));
            pic.dispatchEvent(new Event('pong'));
            globalThis.__onceRuns = onceRuns;

            // An element listener that registers a guard for the same event type: the
            // guard sees the next dispatch, not the one that installed it
            // (mediasource-redundant-seek's "Unexpected event 'seeked'").
            var guardRuns = 0;
            pic.addEventListener('seeked', function () {
                pic.addEventListener('seeked', function () { guardRuns++; });
            }, { once: true });
            pic.dispatchEvent(new Event('seeked'));
            globalThis.__guardAfterFirst = guardRuns;
            pic.dispatchEvent(new Event('seeked'));
            globalThis.__guardAfterSecond = guardRuns;
            </script></body></html>
            """,
            baseUri).Parse();

        var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
        try
        {
            await engine.SetDomAsync(document.DocumentElement, baseUri);

            // First dispatch: only the original listener; the one it added waits.
            Assert.Equal("1", engine.Evaluate("String(globalThis.__afterFirst)")?.ToString());
            // Second dispatch: original + the copy added last time (adds a third).
            Assert.Equal("3", engine.Evaluate("String(globalThis.__afterSecond)")?.ToString());
            Assert.Equal("false", engine.Evaluate("String(globalThis.__removedRan)")?.ToString());
            Assert.Equal("1", engine.Evaluate("String(globalThis.__onceRuns)")?.ToString());
            Assert.Equal("0", engine.Evaluate("String(globalThis.__guardAfterFirst)")?.ToString());
            Assert.Equal("1", engine.Evaluate("String(globalThis.__guardAfterSecond)")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    // DOM 2.9 "get the parent": a Document's parent is null for a "load" event, so an
    // element's load stops at the document and never reaches Window's listeners in
    // either phase. Other event types still propagate to Window.
    [Fact]
    public async Task ElementLoadEvent_StopsAtTheDocument_AndNeverReachesWindow()
    {
        var baseUri = new Uri("https://fixture.test/index.html");
        var document = new HtmlParser(
            """
            <html><body><img id="pic"><script>
            var seen = [];
            window.addEventListener('load', function () { seen.push('window-capture'); }, true);
            window.addEventListener('load', function () { seen.push('window-bubble'); });
            document.addEventListener('load', function () { seen.push('document-capture'); }, true);
            window.addEventListener('probe', function () { seen.push('window-probe'); }, true);
            var pic = document.getElementById('pic');
            pic.addEventListener('load', function () { seen.push('target'); });
            pic.dispatchEvent(new Event('load', { bubbles: true }));
            pic.dispatchEvent(new Event('probe'));
            globalThis.__seen = seen.join(',');
            </script></body></html>
            """,
            baseUri).Parse();

        var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
        try
        {
            await engine.SetDomAsync(document.DocumentElement, baseUri);
            Assert.Equal("document-capture,target,window-probe", engine.Evaluate("globalThis.__seen")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
