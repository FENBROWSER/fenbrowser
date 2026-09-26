using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// ECMA-262 13.3.6.1: whether a call to %eval% is direct is a property of that
/// call. The calling scope was handed to Eval through interpreter state that
/// Eval only cleared once it got as far as parsing, so `eval(1)` - which
/// returns its argument before that - left the caller's scope armed, and the
/// next indirect eval, from anywhere, ran in it.
/// </summary>
public sealed class DirectEvalArmingTests
{
    private static string Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsString();
    }

    [Theory]
    [InlineData("function f() { var probe = 'local'; eval(1); return (0, eval)('typeof probe'); } f();", "undefined")]
    [InlineData("function f() { eval(); return (0, eval)('typeof arguments'); } f();", "undefined")]
    [InlineData("function g() { eval({}); (0, eval)('var leaked = 1'); return typeof leaked; } g() + ',' + typeof leaked;",
                "number,number")]
    [InlineData("function g() { var probe = 1; eval(1); } g(); (0, eval)('typeof probe');", "undefined")]
    public void AnEvalThatDoesNotParseLeavesNothingArmed(string source, string expected)
    {
        Assert.Equal(expected, Run(source));
    }

    [Fact]
    public void ADirectEvalStillSeesItsCaller()
    {
        Assert.Equal("local", Run("function f() { var probe = 'local'; eval(1); return eval('probe'); } f();"));
    }
}
