using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RestParameterRuntimeTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void CapturesTrailingArgumentsIntoArray()
    {
        Assert.Equal(3, RunNum("function f(a, ...rest) { return rest.length; } f(1, 2, 3, 4);"));
    }

    [Fact]
    public void RestArrayIsEmptyWhenNoTrailingArguments()
    {
        Assert.Equal(0, RunNum("function f(a, ...rest) { return rest.length; } f(1);"));
    }

    [Fact]
    public void RestArrayPreservesArgumentOrder()
    {
        Assert.Equal(11, RunNum("function f(...rest) { return rest[1]; } f(9, 11, 13);"));
    }

    [Fact]
    public void ArrowFunctionRestParameterWorks()
    {
        Assert.Equal(7, RunNum("((a, ...rest) => rest[0])(1, 7, 8);"));
    }
}
