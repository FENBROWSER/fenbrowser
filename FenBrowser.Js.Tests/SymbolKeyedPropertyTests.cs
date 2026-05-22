using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class SymbolKeyedPropertyTests
{
    private static double RunNum(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsNumber();
    }

    private static bool RunBool(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn).AsBoolean();
    }

    [Fact]
    public void SymbolKeyRoundTripsThroughElementAccessor()
    {
        Assert.Equal(7, RunNum("var s = Symbol(); var o = {}; o[s] = 7; o[s];"));
    }

    [Fact]
    public void SymbolKeysAreIndependent()
    {
        Assert.Equal(2, RunNum("var s1 = Symbol(), s2 = Symbol(); var o = {}; o[s1] = 1; o[s2] = 2; o[s2];"));
    }

    [Fact]
    public void SymbolKeysDoNotAppearInObjectKeys()
    {
        // Object.keys returns only string keys per spec.
        Assert.Equal(0, RunNum("var s = Symbol(); var o = {}; o[s] = 1; Object.keys(o).length;"));
    }

    [Fact]
    public void WellKnownSymbolIteratorIsReadableAsKey()
    {
        // Round-tripping the well-known Symbol.iterator id confirms it's stable.
        Assert.True(RunBool("var o = {}; o[Symbol.iterator] = 42; o[Symbol.iterator] === 42;"));
    }

    [Fact]
    public void MissingSymbolKeyReturnsUndefined()
    {
        Assert.True(RunBool("var s = Symbol(); ({})[s] === undefined;"));
    }

    [Fact]
    public void SymbolKeyOnPrimitiveStringPrototypeReachable()
    {
        // Install on String.prototype, then read from a primitive string.
        Assert.Equal(99, RunNum("var s = Symbol('sk'); String.prototype[s] = 99; 'x'[s];"));
    }
}
