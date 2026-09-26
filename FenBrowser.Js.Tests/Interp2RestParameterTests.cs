using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Interpreter2;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

// ECMA-262 10.2.11 FunctionDeclarationInstantiation: a rest parameter is bound
// to a new array of every argument from its position on. The register-window
// loop used to decline any body with one, which on a real library was the
// largest share of calls then still running on the old loop.
public sealed class Interp2RestParameterTests
{
    private static BytecodeFunction Compile(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return fn;
    }

    private static string Run(string source) => new BytecodeInterpreter().Execute(Compile(source)).AsString();

    [Theory]
    [InlineData("function f(...r) {}")]
    [InlineData("function f(a, b, ...r) { return r; }")]
    [InlineData("function f(...r) { return function () { return r; }; }")]
    public void ABodyWithARestParameterRunsOnTheRegisterWindow(string declaration)
    {
        var layout = FrameLayout.For(Compile(declaration).NestedFunctions[0]);
        Assert.Equal(Interp2Bailout.None, layout.Bailout);
    }

    [Theory]
    [InlineData("function f(...r) { return r.length + ':' + r.join(); } f(1, 2, 3);", "3:1,2,3")]
    [InlineData("function f(...r) { return r.length + ':' + Array.isArray(r); } f();", "0:true")]
    [InlineData("function f(a, b, ...r) { return a + b + '|' + r.join(); } f(1, 2, 3, 4);", "3|3,4")]
    [InlineData("function f(a, b, ...r) { return String(b) + '|' + r.length; } f(1);", "undefined|0")]
    // Each call gets its own array.
    [InlineData("function f(...r) { return r; } var a = f(1), b = f(1); String(a !== b);", "true")]
    // A closure that captures it keeps the array alive past the call.
    [InlineData("function f(...r) { return function () { return r.join('-'); }; } f(1, 2)();", "1-2")]
    [InlineData("var g = (...r) => r.length; String(g(1, 2, 3));", "3")]
    [InlineData("var o = { m(first, ...r) { return first + r[1]; } }; String(o.m(1, 2, 3));", "4")]
    // The arguments object still sees every argument; the rest array is separate.
    [InlineData("function f(a, ...r) { r[0] = 9; return arguments.length + ':' + r.length + ':' + arguments[1]; }" +
                " f(1, 2, 3);", "3:2:2")]
    [InlineData("function f(a = 5, ...r) { return String(a + r.length); } f(undefined, 1, 2);", "7")]
    [InlineData("function f(...[x, y]) { return String(x + y); } f(2, 3);", "5")]
    [InlineData("function f(...r) { return r.length; } String(f.length) + ',' + String(f(...[1, 2]));", "0,2")]
    public void TheRestArrayHoldsTheRemainingArguments(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }
}
