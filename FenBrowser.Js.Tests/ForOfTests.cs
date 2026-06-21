using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ForOfTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static string RunStr(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Fact]
    public void IteratesArrayValues()
    {
        Assert.Equal(6, RunNum("var s = 0; for (var v of [1,2,3]) s = s + v; s;"));
    }

    [Fact]
    public void IteratesEmptyArrayProducesNoIterations()
    {
        Assert.Equal(0, RunNum("var c = 0; for (var v of []) c = c + 1; c;"));
    }

    [Fact]
    public void IteratesStringYieldsCodeUnits()
    {
        Assert.Equal("abc", RunStr("var r = ''; for (var c of 'abc') r = r + c; r;"));
    }

    [Fact]
    public void IteratesArrayLikeObject()
    {
        Assert.Equal(6, RunNum("var s = 0; for (var v of {length:3, 0:1, 1:2, 2:3}) s = s + v; s;"));
    }

    [Fact]
    public void BreakWorks()
    {
        Assert.Equal(3, RunNum("var c = 0; for (var v of [1,2,3,4,5]) { if (v > 3) break; c = c + 1; } c;"));
    }

    [Fact]
    public void ContinueWorks()
    {
        // Sum of evens in [1,2,3,4] = 2 + 4 = 6.
        Assert.Equal(6, RunNum("var s = 0; for (var v of [1,2,3,4]) { if (v % 2 === 1) continue; s = s + v; } s;"));
    }

    [Fact]
    public void NullAndUndefinedThrow()
    {
        Assert.Throws<JsThrownException>(() => RunNum("for (var v of null);"));
        Assert.Throws<JsThrownException>(() => RunNum("for (var v of undefined);"));
    }

    [Fact]
    public void PlainObjectThrows()
    {
        Assert.Throws<JsThrownException>(() => RunNum("for (var v of {a:1});"));
    }

    [Fact]
    public void NumberIsNotIterable()
    {
        Assert.Throws<JsThrownException>(() => RunNum("for (var v of 42);"));
    }

    [Fact]
    public void LazyIterationTerminatesOnBreakOverInfiniteIterator()
    {
        // The pull is lazy: an iterator whose next() never reports done must not
        // hang — the body's break ends iteration after the third value. Before
        // lazy iteration this eager-drained forever. Expect 3 values pulled and
        // exactly one IteratorClose (return) call.
        Assert.Equal("3,3,1", RunStr(@"
            var nextCount = 0, returnCount = 0;
            var iter = {
                [Symbol.iterator]() { return this; },
                next() { nextCount++; return { value: nextCount, done: false }; },
                return() { returnCount++; return {}; }
            };
            var seen = 0;
            for (var x of iter) { seen++; if (x === 3) break; }
            seen + ',' + nextCount + ',' + returnCount;
        "));
    }

    [Fact]
    public void NormalExhaustionDoesNotCallReturn()
    {
        // ECMA-262 13.7.5.13: a for-of that runs to iterator exhaustion does not
        // invoke IteratorClose — the iterator already completed.
        Assert.Equal("30,0", RunStr(@"
            var returnCount = 0;
            var iter = {
                vals: [10, 20],
                pos: 0,
                [Symbol.iterator]() { return this; },
                next() {
                    if (this.pos < this.vals.length) {
                        var v = this.vals[this.pos];
                        this.pos = this.pos + 1;
                        return { value: v, done: false };
                    }
                    return { value: undefined, done: true };
                },
                return() { returnCount = returnCount + 1; return {}; }
            };
            var s = 0;
            for (var x of iter) s += x;
            s + ',' + returnCount;
        "));
    }

    [Fact]
    public void ContinueDoesNotCallReturn()
    {
        // continue stays within the loop, so it must not close the iterator.
        Assert.Equal("2,0", RunStr(@"
            var returnCount = 0;
            var iter = {
                vals: [0, 1, 2],
                pos: 0,
                [Symbol.iterator]() { return this; },
                next() {
                    if (this.pos < this.vals.length) {
                        var v = this.vals[this.pos];
                        this.pos = this.pos + 1;
                        return { value: v, done: false };
                    }
                    return { done: true };
                },
                return() { returnCount = returnCount + 1; return {}; }
            };
            var s = 0;
            for (var x of iter) { if (x === 1) continue; s += x; }
            s + ',' + returnCount;
        "));
    }

    [Fact]
    public void BreakCloseThrowsWhenReturnResultIsNotObject()
    {
        // ECMA-262 7.4.11 step 8: if the iterator's return() yields a non-object
        // during a normal-completion close, IteratorClose throws a TypeError.
        Assert.Throws<JsThrownException>(() => RunNum(@"
            var iter = {
                [Symbol.iterator]() { return this; },
                next() { return { value: 1, done: false }; },
                return() { return 42; }
            };
            for (var x of iter) break;
            0;
        "));
    }

    [Fact]
    public void DestructuringNormalClosePropagatesIteratorReturnError()
    {
        Assert.Throws<JsThrownException>(() => RunNum(@"
            var inner = {
                [Symbol.iterator]() { return this; },
                next() { return { value: 1, done: false }; },
                return() { throw new Error('close'); }
            };
            var value;
            for ([value] of [inner]) {}
            0;
        "));
    }
}
