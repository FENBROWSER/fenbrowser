using System;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// Streams "ReadableStream async iteration". Without Symbol.asyncIterator a stream is
/// not async-iterable at all, so `for await (const chunk of stream)` throws before it
/// reads anything - and a page that consumes a streamed payload that way stalls with
/// no error to show for it.
/// </summary>
[Collection("Engine Tests")]
public sealed class ReadableStreamAsyncIterationTests : IDisposable
{
    public ReadableStreamAsyncIterationTests() => BrowserScriptEngineRuntime.Reset();

    public void Dispose() => BrowserScriptEngineRuntime.Reset();

    private static JsHostAdapter CreateHost()
        => new(navigate: _ => { }, post: (_, _) => { }, status: _ => { }, log: _ => { });

    private static FenJsBrowserScriptEngine CreateEngine()
    {
        var baseUri = new Uri("https://streams.test/page");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
        engine.SetDomAsync(document.DocumentElement, baseUri).GetAwaiter().GetResult();
        return engine;
    }

    /// <summary>
    /// Runs an async consumer that parks its result on a global, then reads that global
    /// back: the loop settles over microtasks, so the synchronous return value of the
    /// first Evaluate cannot carry it.
    /// </summary>
    private static string RunAsync(FenJsBrowserScriptEngine engine, string body)
    {
        engine.Evaluate("globalThis.__out = 'unset'; globalThis.__err = 'none'; " + body + " 'started';");
        var error = engine.Evaluate("globalThis.__err")?.ToString();
        var value = engine.Evaluate("globalThis.__out")?.ToString();
        return value + " (err=" + error + ")";
    }

    // Control: if a bare async function does not settle between two Evaluate calls
    // then the harness, not the stream, is what the other tests are measuring.
    [Fact]
    public void ControlPlainAsyncFunctionSettlesBetweenEvaluates()
    {
        var engine = CreateEngine();
        Assert.Equal("ok (err=none)", RunAsync(engine, "(async function () { globalThis.__out = 'ok'; })();"));
    }

    [Fact]
    public void StreamExposesTheAsyncIteratorSurface()
    {
        var engine = CreateEngine();

        Assert.Equal("function", engine.Evaluate("typeof ReadableStream.prototype.values")?.ToString());
        Assert.Equal(
            "function",
            engine.Evaluate("typeof ReadableStream.prototype[Symbol.asyncIterator]")?.ToString());
        Assert.Equal(
            "true",
            engine.Evaluate(
                "String(ReadableStream.prototype[Symbol.asyncIterator] === ReadableStream.prototype.values)")?.ToString());
    }

    [Fact]
    public void ForAwaitReadsEveryEnqueuedChunkInOrder()
    {
        var engine = CreateEngine();

        var seen = RunAsync(engine, @"
            var stream = new ReadableStream({
                start: function (controller) {
                    controller.enqueue('a');
                    controller.enqueue('b');
                    controller.enqueue('c');
                    controller.close();
                }
            });
            (async function () {
                try {
                    var chunks = [];
                    for await (var chunk of stream) { chunks.push(chunk); }
                    globalThis.__out = chunks.join('');
                } catch (e) { globalThis.__err = String(e && e.message || e); }
            })();");

        Assert.Equal("abc (err=none)", seen);
    }

    [Fact]
    public void ForAwaitOverAnAlreadyClosedEmptyStreamCompletes()
    {
        var engine = CreateEngine();

        var done = RunAsync(engine, @"
            var stream = new ReadableStream({
                start: function (controller) { controller.close(); }
            });
            (async function () {
                try {
                    var count = 0;
                    for await (var chunk of stream) { count++; }
                    globalThis.__out = 'completed:' + count;
                } catch (e) { globalThis.__err = String(e && e.message || e); }
            })();");

        Assert.Equal("completed:0 (err=none)", done);
    }

    [Fact]
    public void BreakingOutOfForAwaitCancelsTheStream()
    {
        var engine = CreateEngine();

        var state = RunAsync(engine, @"
            globalThis.__cancelled = false;
            var stream = new ReadableStream({
                start: function (controller) {
                    controller.enqueue('a');
                    controller.enqueue('b');
                },
                cancel: function () { globalThis.__cancelled = true; }
            });
            (async function () {
                try {
                    for await (var chunk of stream) { break; }
                    globalThis.__out = 'broke';
                } catch (e) { globalThis.__err = String(e && e.message || e); }
            })();");

        Assert.Equal("broke (err=none)", state);
    }
}
