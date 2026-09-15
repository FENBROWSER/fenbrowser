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
/// removed during the dispatch is not called. web-vitals' onLoad helper
/// re-registers itself as a window capture listener whenever the document is
/// not yet complete - which is every &lt;img&gt; load on a page still loading -
/// and walking the live list ran each new copy inside the same dispatch, forever.
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
                window.addEventListener('load', function () { count++; }, true);
            }
            window.addEventListener('load', selfSpawning, true);
            var pic = document.getElementById('pic');
            pic.dispatchEvent(new Event('load'));
            globalThis.__afterFirst = count;
            pic.dispatchEvent(new Event('load'));
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
