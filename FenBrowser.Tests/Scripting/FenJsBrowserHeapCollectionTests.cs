using System;
using System.Reflection;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Scripting;
using FenBrowser.Js.Interpreter;
using Xunit;

namespace FenBrowser.Tests.Scripting;

[Collection("Engine Tests")]
public sealed class FenJsBrowserHeapCollectionTests
{
    [Fact]
    public async Task AllocationBurstCollectsWithoutLosingHostEventListener()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate(
            """
            (function () {
                var state = { value: 'preserved' };
                window.addEventListener('fen-gc-test', function () {
                    window.__fenGcListenerResult = state.value;
                });
            })();
            for (var i = 0; i < 12000; i++) {
                [i, { value: i }, i + 1];
            }
            """);

        var result = engine.Evaluate(
            """
            window.dispatchEvent(new Event('fen-gc-test'));
            window.__fenGcListenerResult;
            """);

        var interpreter = GetInterpreter(engine);
        Assert.Equal("preserved", result?.ToString());
        Assert.True(interpreter.Heap.GcCollectionCount > 0);
        Assert.True(interpreter.Heap.LastGcSweptCells > 0);
        Assert.True(interpreter.Heap.MinorCollectionCount > 0);
    }

    [Fact]
    public async Task AllocationBurstCollectsWithoutLosingPendingTimerCallback()
    {
        var engine = await CreateEngineAsync();

        engine.Evaluate(
            """
            (function () {
                var state = { value: 42 };
                setTimeout(function () {
                    window.__fenGcTimerResult = state.value;
                }, 250);
            })();
            for (var i = 0; i < 12000; i++) {
                [i, { value: i }, i + 1];
            }
            """);

        engine.Evaluate("void 0;");

        await Task.Delay(500);

        var interpreter = GetInterpreter(engine);
        Assert.Equal("42", engine.Evaluate("String(window.__fenGcTimerResult);")?.ToString());
        Assert.True(interpreter.Heap.GcCollectionCount > 0);
        Assert.True(interpreter.Heap.MinorCollectionCount > 0);
    }

    private static async Task<FenJsBrowserScriptEngine> CreateEngineAsync()
    {
        var baseUri = new Uri("https://fixture.test/heap-collection.html");
        var document = new HtmlParser("<html><body></body></html>", baseUri).Parse();
        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll
        };

        await engine.SetDomAsync(document.DocumentElement, baseUri);

        // These tests are about surviving a collection, not about how often
        // one happens. Pin the cadence they need rather than depending on the
        // engine's default nursery, which is sized for real pages and is far
        // larger than the burst below.
        GetInterpreter(engine).Heap.YoungAllocationsPerMinorGc = 4096;
        return engine;
    }

    private static BytecodeInterpreter GetInterpreter(FenJsBrowserScriptEngine engine)
    {
        var field = typeof(FenJsBrowserScriptEngine).GetField(
            "_interpreter",
            BindingFlags.Instance | BindingFlags.NonPublic);
        return Assert.IsType<BytecodeInterpreter>(field?.GetValue(engine));
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
