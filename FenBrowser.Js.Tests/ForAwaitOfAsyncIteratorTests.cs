using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 7.4.2 GetIterator(obj, async) and 14.7.5 for-await-of. The loop used to
/// emit the plain sync EnumerateValues opcode and await each yielded value, so
/// @@asyncIterator was never consulted at all and a true async iterable reported
/// "not iterable".
/// </summary>
public class ForAwaitOfAsyncIteratorTests
{
    /// <summary>
    /// Runs source that parks its result on a global, drains microtasks, then reads
    /// the global back: a for-await-of loop settles across many microtask turns.
    /// </summary>
    private static string RunAsync(string source)
    {
        var interpreter = new BytecodeInterpreter();
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        _ = interpreter.Execute(fn);

        for (var i = 0; i < 100; i++)
        {
            interpreter.PumpMicrotasks();
        }

        var read = new BytecodeCompiler().CompileScript(new SourceText("String(globalThis.out);"));
        var result = interpreter.Execute(read);
        return result.Tag == JsValueTag.String ? result.AsString() : "<" + result.Tag + ">";
    }

    private const string AsyncIterablePrelude = @"
        globalThis.out = 'unset';
        var source = {};
        source[Symbol.asyncIterator] = function () {
            var i = 0;
            return { next: function () { i++; return Promise.resolve({ value: i, done: i > 3 }); } };
        };";

    [Fact]
    public void ForAwaitConsumesAnAsyncIterable()
    {
        Assert.Equal("1,2,3", RunAsync(AsyncIterablePrelude + @"
            (async function () {
                try {
                    var seen = [];
                    for await (var v of source) { seen.push(v); }
                    globalThis.out = seen.join(',');
                } catch (e) { globalThis.out = 'THREW ' + (e && e.message || e); }
            })();"));
    }

    // 27.1.4.3 CreateAsyncFromSyncIterator: with no @@asyncIterator the sync iterator
    // is used and each VALUE is awaited, so a list of promises yields their results.
    [Fact]
    public void ForAwaitFallsBackToTheSyncIteratorAndAwaitsEachValue()
    {
        Assert.Equal("p,plain,q", RunAsync(@"
            globalThis.out = 'unset';
            (async function () {
                try {
                    var seen = [];
                    for await (var v of [Promise.resolve('p'), 'plain', Promise.resolve('q')]) { seen.push(v); }
                    globalThis.out = seen.join(',');
                } catch (e) { globalThis.out = 'THREW ' + (e && e.message || e); }
            })();"));
    }

    [Fact]
    public void AnAsyncIterableThatIsImmediatelyDoneRunsTheBodyZeroTimes()
    {
        Assert.Equal("count:0", RunAsync(@"
            globalThis.out = 'unset';
            var source = {};
            source[Symbol.asyncIterator] = function () {
                return { next: function () { return Promise.resolve({ value: undefined, done: true }); } };
            };
            (async function () {
                try {
                    var count = 0;
                    for await (var v of source) { count++; }
                    globalThis.out = 'count:' + count;
                } catch (e) { globalThis.out = 'THREW ' + (e && e.message || e); }
            })();"));
    }

    [Fact]
    public void AsyncIteratorTakesPrecedenceOverTheSyncIterator()
    {
        Assert.Equal("async", RunAsync(@"
            globalThis.out = 'unset';
            var source = {};
            source[Symbol.iterator] = function () {
                var done = false;
                return { next: function () { var d = done; done = true; return { value: 'sync', done: d }; } };
            };
            source[Symbol.asyncIterator] = function () {
                var done = false;
                return { next: function () {
                    var d = done; done = true;
                    return Promise.resolve({ value: 'async', done: d });
                } };
            };
            (async function () {
                try {
                    var seen = [];
                    for await (var v of source) { seen.push(v); }
                    globalThis.out = seen.join(',');
                } catch (e) { globalThis.out = 'THREW ' + (e && e.message || e); }
            })();"));
    }

    [Fact]
    public void ARejectedNextPropagatesIntoTheLoopBody()
    {
        Assert.Equal("caught:boom", RunAsync(@"
            globalThis.out = 'unset';
            var source = {};
            source[Symbol.asyncIterator] = function () {
                return { next: function () { return Promise.reject(new Error('boom')); } };
            };
            (async function () {
                try {
                    for await (var v of source) { }
                    globalThis.out = 'no-throw';
                } catch (e) { globalThis.out = 'caught:' + (e && e.message || e); }
            })();"));
    }

    [Fact]
    public void ANonCallableAsyncIteratorIsATypeError()
    {
        Assert.StartsWith("THREW", RunAsync(@"
            globalThis.out = 'unset';
            var source = {};
            source[Symbol.asyncIterator] = 42;
            (async function () {
                try {
                    for await (var v of source) { }
                    globalThis.out = 'no-throw';
                } catch (e) { globalThis.out = 'THREW ' + (e && e.message || e); }
            })();"));
    }
}
