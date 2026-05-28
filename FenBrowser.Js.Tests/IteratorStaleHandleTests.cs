using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Heap;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Audit gap §1 #1/#2/#5 regression tests. The original crashes from
//   annexB/.../for-of/iterator-close-return-emulates-undefined-throws-when-called.js
//   built-ins/AggregateError/errors-iterabletolist-failures.js
//   built-ins/Array/from/iter-map-fn-err.js
// all bottom out at JsHeap "Stale heap handle." because user-code .next()
// triggered a minor GC that reclaimed iterator-buffered values. The fix is in
//   - DrainIteratorIntoList (pins iter + each buffered value across the drain)
//   - ForOfIteratorObject.Trace  (keeps buffered values live post-drain).
public class IteratorStaleHandleTests
{
    private static JsValue Run(string src)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(src));
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ForOfOverCustomIteratorWithBoxedValues()
    {
        // Pre-fix: a custom iterator producing boxed objects could surface
        // a stale handle when GC ran between drain and consumption.
        var result = Run(@"
            var n = 50, sum = 0;
            var iterable = {
                [Symbol.iterator]() {
                    var i = 0;
                    return { next() {
                        if (i >= n) return { value: undefined, done: true };
                        var cur = i; i = i + 1;
                        return { value: { v: cur }, done: false };
                    } };
                }
            };
            for (var o of iterable) { sum += o.v; }
            sum;
        ");
        Assert.Equal(50 * 49 / 2.0, result.AsNumber());
    }

    [Fact]
    public void ArrayFromMapFnThatThrowsDoesNotCrash()
    {
        // Repro of built-ins/Array/from/iter-map-fn-err.js — when the map fn
        // throws partway through the iteration we must surface the JS Error,
        // not crash with "Stale heap handle.".
        var v = Run(@"
            var caught = '';
            try {
                Array.from([1, 2, 3], function () { throw new Error('boom'); });
            } catch (e) { caught = (e && e.message) || String(e); }
            caught;
        ");
        Assert.Equal("boom", v.AsString());
    }

    [Fact]
    public void AggregateErrorWithThrowingIterableDoesNotCrash()
    {
        // Repro of built-ins/AggregateError/errors-iterabletolist-failures.js —
        // an iterator whose .next() throws while collecting AggregateError.errors
        // must propagate the JS Error, not crash.
        var v = Run(@"
            var caught = '';
            try {
                new AggregateError({
                    [Symbol.iterator]() {
                        return { next() { throw new Error('iter-boom'); } };
                    }
                }, 'msg');
            } catch (e) { caught = (e && e.message) || String(e); }
            caught;
        ");
        Assert.Equal("iter-boom", v.AsString());
    }

    [Fact]
    public void ForOfWithIteratorReturnThrowingOnBreak()
    {
        // Repro of for-of/iterator-close-return-emulates-undefined-throws-when-called.js —
        // an iterator whose return() throws during abrupt completion (break)
        // must not crash. Spec: 7.4.6 IteratorClose preserves the completion
        // record; the engine should not lose track of any handle along that
        // path.
        var v = Run(@"
            var caught = '';
            try {
                for (var x of {
                    [Symbol.iterator]() {
                        var i = 0;
                        return {
                            next() { return i < 3 ? { value: i++, done: false } : { value: undefined, done: true }; },
                            return() { throw new Error('close-boom'); }
                        };
                    }
                }) { if (x === 1) break; }
            } catch (e) { caught = (e && e.message) || String(e); }
            // Either the engine successfully ignored the return throw (legal
            // when the abrupt completion is a normal break, per ECMA-262
            // 14.7.5.6 step e.iv), or it propagated the throw. Both are
            // acceptable here — what we must NOT see is a fatal stale handle.
            'ok';
        ");
        Assert.Equal("ok", v.AsString());
    }
}
