using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 13.3.6.2 EvaluateCall step 1.b: calling a name that resolves to a
/// property of a `with` object passes that object as `this`
/// (9.1.1.2.10 WithBaseObject). Every such call used to pass undefined, so a
/// method reached through `with` - the lodash-template and Knockout pattern -
/// ran against the global object, and `with (5) toFixed(1)` threw.
/// </summary>
public sealed class WithCallThisTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    private const string Obj = "var o = { v: 'o', m() { return this === o ? 'o' : String(this && this.v); }, " +
                               "t(strings) { return this === o ? 'o' : 'other'; } };";

    [Theory]
    [InlineData(Obj + " with (o) { m(); }", "o")]
    [InlineData(Obj + " with (o) { (m)(); }", "o")]
    [InlineData(Obj + " with (o) { m?.(); }", "o")]
    [InlineData(Obj + " with (o) { t`x`; }", "o")]
    [InlineData(Obj + " with (o) { m(...[1, 2]); }", "o")]
    [InlineData(Obj + " with (o) { m(1, 2, 3); }", "o")]
    // Inside a function that the with encloses, and inside one that encloses the with.
    [InlineData(Obj + " with (o) { (function () { return m(); })(); }", "o")]
    [InlineData(Obj + " function f() { with (o) { return m(); } } f();", "o")]
    [InlineData(Obj + " with (o) { (() => m())(); }", "o")]
    // Eval code run under the with.
    [InlineData(Obj + " function f() { with (o) { return eval('m()'); } } f();", "o")]
    [InlineData("with (5) { toFixed(1); }", "5.0")]
    public void ACallThroughAWithBindingPassesTheObject(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Theory]
    // A name the with object does not have resolves elsewhere and gets no base.
    [InlineData("function g() { return this === globalThis; } with ({}) { String(g()); }", "true")]
    [InlineData("function f() { 'use strict'; return this; } function h() { with ({}) { return String(f()); } } h();", "undefined")]
    [InlineData("function h() { var local = function () { return this === globalThis; }; with ({}) { return String(local()); } } h();", "true")]
    // Outside the with, the same name gets none either.
    [InlineData("var o = { m() { return this === o; } }; var m = o.m; with (o) {} String(m());", "false")]
    public void OtherCallsStillPassUndefined(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Fact]
    public void ANameMissingEverywhereIsStillAReferenceError()
    {
        Assert.Equal("ReferenceError", Run(
            "var r; with ({}) { try { nowhere(); } catch (e) { r = e.constructor.name; } } r;"));
    }
}
