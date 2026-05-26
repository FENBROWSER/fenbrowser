using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class OptionalChainingRuntimeTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    [Fact]
    public void NullOptionalMemberReturnsUndefined()
    {
        Assert.True(RunBool("let o = null; o?.x === undefined;"));
    }

    [Fact]
    public void UndefinedOptionalMemberReturnsUndefined()
    {
        Assert.True(RunBool("let o; o?.x === undefined;"));
    }

    [Fact]
    public void OptionalComputedMemberShortCircuitsPropertyExpression()
    {
        Assert.Equal(0, RunNum("let side = 0; let o = null; o?.[side++]; side;"));
    }

    [Fact]
    public void OptionalMemberReadsValueWhenBaseIsPresent()
    {
        Assert.Equal(3, RunNum("let o = { x: 3 }; o?.x;"));
    }

    [Fact]
    public void OptionalMemberCallPreservesThisBinding()
    {
        Assert.Equal(2, RunNum("let o = { x: 2, m() { return this.x; } }; o?.m();"));
    }

    [Fact]
    public void OptionalCallOnNullishCalleeReturnsUndefined()
    {
        Assert.True(RunBool("let f = null; f?.() === undefined;"));
    }

    [Fact]
    public void OptionalCallInvokesPresentCallee()
    {
        Assert.Equal(5, RunNum("(function(a) { return a + 1; })?.(4);"));
    }
}
