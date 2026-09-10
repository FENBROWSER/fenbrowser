using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

/// <summary>
/// Annex B.3.3.1: a function declared in a block of sloppy code is also a var
/// of the enclosing variable environment. An arrow has one of its own, so the
/// function stays inside the arrow. Every expected value here is what V8
/// returns.
/// </summary>
public sealed class AnnexBArrowBlockFunctionTests
{
    private static string RunString(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsString();
    }

    [Fact]
    public void ABlockFunctionInAnArrowDoesNotReachTheEnclosingFunction()
    {
        Assert.Equal(
            "undefined",
            RunString("(function outer() { (() => { { function inner() {} } })(); return typeof inner; })()"));
    }

    [Fact]
    public void ABlockFunctionInAnArrowIsAVarOfTheArrow()
    {
        Assert.Equal(
            "function:undefined",
            RunString("(function outer() { var r = (() => { { function inner3() {} } return typeof inner3; })(); return r + ':' + typeof inner3; })()"));
    }
}
