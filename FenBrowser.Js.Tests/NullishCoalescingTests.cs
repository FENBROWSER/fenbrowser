using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NullishCoalescingTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void UsesRightWhenNull()
    {
        Assert.Equal(42, RunNum("null ?? 42;"));
    }

    [Fact]
    public void UsesRightWhenUndefined()
    {
        Assert.Equal(42, RunNum("(void 0) ?? 42;"));
    }

    [Fact]
    public void UsesLeftWhenZero()
    {
        Assert.Equal(0, RunNum("0 ?? 42;"));
    }

    [Fact]
    public void ShortCircuitsWhenLeftIsNotNullish()
    {
        Assert.Equal(1, RunNum("1 ?? (function(){ throw 'oops'; })();"));
    }
}
