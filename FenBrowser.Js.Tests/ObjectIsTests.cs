using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ObjectIsTests
{
    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Theory]
    [InlineData("Object.is(1, 1);", true)]
    [InlineData("Object.is(1, 2);", false)]
    [InlineData("Object.is('a', 'a');", true)]
    [InlineData("Object.is('a', 'b');", false)]
    [InlineData("Object.is(null, null);", true)]
    [InlineData("Object.is(undefined, undefined);", true)]
    [InlineData("Object.is(null, undefined);", false)]
    [InlineData("Object.is(true, true);", true)]
    public void StandardEqualityCases(string source, bool expected)
    {
        Assert.Equal(expected, RunBool(source));
    }

    [Fact]
    public void NaNIsSameAsNaN()
    {
        // ECMA-262 7.2.10 step 4.a - SameValue(NaN, NaN) is true. Diverges from
        // strict equality.
        Assert.True(RunBool("Object.is(NaN, NaN);"));
    }

    [Fact]
    public void PlusZeroAndMinusZeroAreNotSame()
    {
        // ECMA-262 7.2.10 step 4.b-c. Diverges from strict equality, which would
        // treat +0 === -0 as true.
        Assert.False(RunBool("Object.is(0, -0);"));
        Assert.False(RunBool("Object.is(-0, 0);"));
    }

    [Fact]
    public void PlusZeroIsSameAsPlusZero()
    {
        Assert.True(RunBool("Object.is(0, 0);"));
        Assert.True(RunBool("Object.is(-0, -0);"));
    }

    [Fact]
    public void DefaultsToUndefinedForMissingArguments()
    {
        Assert.True(RunBool("Object.is();"));
        Assert.True(RunBool("Object.is(undefined);"));
        Assert.False(RunBool("Object.is(1);"));
    }
}
