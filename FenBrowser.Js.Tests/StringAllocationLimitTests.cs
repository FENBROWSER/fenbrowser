using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StringAllocationLimitTests
{
    private static void Run(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        _ = new BytecodeInterpreter().Execute(function);
    }

    [Theory]
    [InlineData("'x'.repeat(16777217);")]
    [InlineData("'x'.padStart(16777217, 'y');")]
    [InlineData("'x'.padEnd(16777217, 'y');")]
    public void OversizedResultsThrowBeforeAllocation(string source)
    {
        Assert.Throws<JsThrownException>(() => Run(source));
    }

    [Fact]
    public void EmptyRepeatDoesNotAllocateRegardlessOfCount()
    {
        Run("''.repeat(1e20);");
    }
}
