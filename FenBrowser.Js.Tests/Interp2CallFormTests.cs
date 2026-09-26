using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Spread calls, spread construction and strict-mode tail calls on the
// register-window loop, which used to decline any body containing one.
public sealed class Interp2CallFormTests
{
    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static string Run(string source) => new BytecodeInterpreter().Execute(Compile(source)).AsString();

    [Theory]
    [InlineData("function f(a) { return g(...a); }")]
    [InlineData("function f(a) { return new C(...a); }")]
    [InlineData("function f(n) { 'use strict'; return g(n); }")]
    [InlineData("function f(n) { 'use strict'; return g(n, n, n); }")]
    public void ABodyWithTheseCallFormsRunsOnTheRegisterWindow(string declaration)
    {
        Assert.Equal(Interp2Bailout.None, FrameLayout.For(Compile(declaration).NestedFunctions[0]).Bailout);
    }

    [Theory]
    [InlineData("function add(a, b, c) { return a + b + c; } function f(x) { return add(...x); } String(f([1, 2, 3]));", "6")]
    [InlineData("function add(a, b, c) { return a + b + c; } function f() { return add(1, ...[2, 3]); } String(f());", "6")]
    [InlineData("function f() { return Math.max(...[1, 5, 3]); } String(f());", "5")]
    [InlineData("var o = { v: 10, m(a, b) { return this.v + a + b; } }; function f() { return o.m(...[1, 2]); } String(f());", "13")]
    [InlineData("function count(...r) { return r.length; } function f() { return count(...new Set([1, 2, 2, 3])); } String(f());", "3")]
    [InlineData("function f() { return String(Math.max(...[])); } f();", "-Infinity")]
    public void ASpreadCallPassesEveryElement(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    [InlineData("function P(a, b) { this.s = a + b; } function f() { return new P(...[2, 3]).s; } String(f());", "5")]
    [InlineData("class Q { constructor(a, b) { this.s = a * b; } } function f() { return new Q(...[2, 3]).s; } String(f());", "6")]
    [InlineData("function f() { return new Date(...[2020, 0, 2]).getDate(); } String(f());", "2")]
    public void ASpreadConstructPassesEveryElement(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    // Deep enough to overflow any frame stack if each call kept its frame.
    [InlineData("'use strict'; function count(n) { if (n === 0) return 'done'; return count(n - 1); } count(200000);", "done")]
    [InlineData("'use strict'; function even(n) { if (n === 0) return true; return odd(n - 1); }" +
                " function odd(n) { if (n === 0) return false; return even(n - 1); } String(even(100001));", "false")]
    // The callee need not run on this loop: a native, and a body still declined.
    [InlineData("'use strict'; function f(a, b) { return Math.max(a, b); } String(f(3, 8));", "8")]
    [InlineData("function viaEval() { return eval('1 + 1'); }" +
                " function f() { 'use strict'; return viaEval(); } String(f());", "2")]
    // A tail call from a callee reached from another frame returns to that frame.
    [InlineData("'use strict'; function leaf(x) { return x * 2; } function mid(x) { return leaf(x + 1); }" +
                " function top() { return mid(1) + mid(2); } String(top());", "10")]
    public void AStrictTailCallReplacesTheFrame(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Fact]
    public void AnErrorFromATailCalledFunctionReachesTheCallersHandler()
    {
        Assert.Equal("caught:1", Run(
            "'use strict'; function thrower() { throw 1; } function t() { return thrower(); }" +
            " function outer() { try { return String(t()); } catch (e) { return 'caught:' + e; } } outer();"));
    }

    [Fact]
    public void ObjectArgumentsSurviveAllocationDuringTheSwap()
    {
        // Each step allocates while the next frame is set up; the object passed
        // along is only held by the arguments being moved.
        Assert.Equal("3000", Run(
            "'use strict';" +
            " function step(o, n) { if (n === 0) return String(o.v);" +
            "   var junk = []; for (var i = 0; i < 20; i++) junk.push({ i: i });" +
            "   return step({ v: o.v + 1, pad: [o.v] }, n - 1); }" +
            " step({ v: 0 }, 3000);"));
    }
}
