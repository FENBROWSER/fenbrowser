using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// Which functions have a [[Construct]] internal method: function declarations
// and expressions and class constructors, never arrows, methods, generators or
// async functions (ECMA-262 15.2.4, 15.3.4, 15.4.4, 15.5.4, 15.8.4, 15.7.14).
// `new`, Reflect.construct and `extends` must agree on it.
public sealed class ConstructTargetTests
{
    [Theory]
    [InlineData("async function f() {} new f();")]
    [InlineData("var f = async () => {}; new f();")]
    [InlineData("var f = Object.getPrototypeOf(async function () {}).constructor(); new f();")]
    [InlineData("function* g() {} new g();")]
    [InlineData("var o = { m() {} }; new o.m();")]
    [InlineData("var f = () => {}; new f();")]
    [InlineData("async function f() {} Reflect.construct(f, []);")]
    [InlineData("var f = () => {}; Reflect.construct(function () {}, [], f);")]
    [InlineData("var o = { m() {} }; Reflect.construct(o.m, []);")]
    [InlineData("async function f() {} class C extends f {}")]
    [InlineData("var f = () => {}; class C extends f {}")]
    public void FunctionsWithoutConstructThrowTypeError(string source)
    {
        var thrown = Assert.Throws<JsThrownException>(() => Run(source + " 0;"));
        Assert.Contains("TypeError", thrown.Message);
    }

    [Theory]
    [InlineData("function F() { this.v = 1; } new F().v;")]
    [InlineData("var F = function () { this.v = 1; }; new F().v;")]
    [InlineData("class C { constructor() { this.v = 1; } } new C().v;")]
    [InlineData("function F() { this.v = 1; } Reflect.construct(F, []).v;")]
    [InlineData("var F = Function('this.v = 1'); new F().v;")]
    public void FunctionsWithConstructStillConstruct(string source)
        => Assert.Equal(1, Run(source).AsNumber());

    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }
}
