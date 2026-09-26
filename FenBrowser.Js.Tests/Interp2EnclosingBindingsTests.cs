using System.Text;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// What an arrow reads from the function around it - `this`, `arguments`,
// `new.target`, `super` - and the other bodies the register-window loop used to
// decline: a named function expression whose own name a closure reads, and a
// frame wider than a fixed cap.
public sealed class Interp2EnclosingBindingsTests
{
    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static string Run(string source) => new BytecodeInterpreter().Execute(Compile(source)).AsString();

    [Theory]
    [InlineData("function f() { return () => arguments[0]; }")]
    [InlineData("var o = { m() { return () => super.x; } };")]
    [InlineData("function f() { return () => new.target; }")]
    [InlineData("function f() { return () => () => this; }")]
    [InlineData("var f = function named() { return () => named; };")]
    public void TheEnclosingBodyAndItsArrowRunOnTheRegisterWindow(string declaration)
    {
        var outer = Compile(declaration).NestedFunctions[0];
        Assert.Equal(Interp2Bailout.None, FrameLayout.For(outer).Bailout);
        foreach (var inner in outer.NestedFunctions)
        {
            Assert.Equal(Interp2Bailout.None, FrameLayout.For(inner).Bailout);
        }
    }

    [Theory]
    [InlineData("function f() { return () => arguments[0] + arguments.length; } String(f(5, 6)());", "7")]
    [InlineData("function f() { var n = arguments.length; return () => arguments[1] + n; } String(f(1, 2)());", "4")]
    [InlineData("function f() { return () => () => arguments[0]; } f('x')()();", "x")]
    // Each call's arrow sees that call's arguments.
    [InlineData("function f() { return () => arguments[0]; } var a = f(1), b = f(2); String(a() + b());", "3")]
    public void AnArrowReadsTheEnclosingArguments(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    [InlineData("class A { m() { return 'a'; } } class B extends A { m() { var g = () => super.m() + '!'; return g(); } }" +
                " new B().m();", "a!")]
    [InlineData("var base = { x: 1 }; var o = { __proto__: base, m() { return (() => super.x)(); } }; String(o.m());", "1")]
    // super.method() inside the arrow still calls with the method's `this`.
    [InlineData("var base = { who() { return this.name; } }; var o = { __proto__: base, name: 'o'," +
                " m() { return (() => super.who())(); } }; o.m();", "o")]
    public void AnArrowReadsTheEnclosingMethodsSuper(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    [InlineData("function F() { var g = () => new.target === F; this.r = g(); } String(new F().r);", "true")]
    [InlineData("function G() { return (() => new.target)(); } String(G());", "undefined")]
    [InlineData("function H() { this.v = new.target === H; } String(new H().v);", "true")]
    [InlineData("function K() { return typeof new.target; } K();", "undefined")]
    [InlineData("class C { constructor() { this.t = (() => new.target)(); } } String(new C().t === C);", "true")]
    // The arrow keeps its own call's new.target after the constructor returns.
    [InlineData("function M() { this.g = () => new.target; } var m = new M(); String(m.g() === M);", "true")]
    public void NewTargetIsTheConstructorsOwnOrTheEnclosingOne(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    [InlineData("var o = { v: 3, m() { return (() => (() => this.v)())(); } }; String(o.m());", "3")]
    [InlineData("function f() { return (() => () => this.tag)()(); } f.call({ tag: 't' });", "t")]
    public void NestedArrowsReadTheEnclosingThis(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    [InlineData("var f = function fact() { var g = () => fact; return String(g() === fact); }; f();", "true")]
    // ECMA-262 15.2.5: the binding is immutable; only strict code is told.
    [InlineData("var f = function nm() { var g = () => { nm = 1; return typeof nm; }; return g(); }; f();", "function")]
    [InlineData("var f = function nm() { 'use strict'; var g = () => { nm = 1; };" +
                " try { g(); } catch (e) { return e.constructor.name; } }; f();", "TypeError")]
    public void AClosureReadsTheFunctionsOwnName(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Fact]
    public void AFrameWiderThanTheOldCapRuns()
    {
        var body = new StringBuilder("function wide() { var total = 0;");
        for (var i = 0; i < 5000; i++)
        {
            body.Append(" var v").Append(i).Append(" = ").Append(i % 3).Append(';');
        }

        body.Append(" total = v0 + v4999 + v2500; return String(total); }");
        var script = Compile(body + " wide();");
        Assert.Equal(Interp2Bailout.None, FrameLayout.For(script.NestedFunctions[0]).Bailout);
        Assert.Equal("2", new BytecodeInterpreter().Execute(script).AsString());
    }
}
