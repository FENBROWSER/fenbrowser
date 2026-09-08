using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using System.Reflection;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class JitFinallyTests
{
    [Theory]
    [InlineData("function f(){ var x=1; try { x=2; } finally { x=x+3; } return x; }", 5)]
    [InlineData("function f(){ try { return 4; } finally { } }", 4)]
    [InlineData("function f(){ try { return 4; } finally { return 7; } }", 7)]
    public void EndFinallyExecutesInCompiledFunction(string declaration, double expected)
    {
        var script = new BytecodeCompiler().CompileScript(new SourceText(declaration + " f();"));
        var function = Assert.Single(script.NestedFunctions);
        var compiled = Assert.IsType<JitCompiler.JitDelegate>(JitCompiler.TryCompile(function));
        typeof(BytecodeFunction)
            .GetField("JitDelegate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(function, compiled);

        var result = new BytecodeInterpreter().Execute(script);

        Assert.Equal(expected, result.AsNumber());
    }

    [Fact]
    public void CompiledFunctionCanCallItselfReentrantly()
    {
        var script = new BytecodeCompiler().CompileScript(new SourceText(
            "function f(n){ return n < 1 ? 0 : f(n - 1) + 1; } f(10);"));
        var function = Assert.Single(script.NestedFunctions);
        var compiled = Assert.IsType<JitCompiler.JitDelegate>(JitCompiler.TryCompile(function));
        typeof(BytecodeFunction)
            .GetField("JitDelegate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(function, compiled);

        var result = new BytecodeInterpreter().Execute(script);

        Assert.Equal(10, result.AsNumber());
    }
}
