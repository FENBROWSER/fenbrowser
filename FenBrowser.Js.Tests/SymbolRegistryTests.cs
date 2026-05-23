using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class SymbolRegistryTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void ForReturnsSameSymbolForSameKey()
    {
        Assert.True(Run("Symbol.for('k') === Symbol.for('k');").AsBoolean());
    }

    [Fact]
    public void ForDifferentKeysAreDistinct()
    {
        Assert.False(Run("Symbol.for('a') === Symbol.for('b');").AsBoolean());
    }

    [Fact]
    public void KeyForRoundTrips()
    {
        Assert.Equal("hello", Run("Symbol.keyFor(Symbol.for('hello'));").AsString());
    }

    [Fact]
    public void KeyForUnregisteredSymbolReturnsUndefined()
    {
        Assert.Equal(JsValueTag.Undefined, Run("Symbol.keyFor(Symbol('local'));").Tag);
    }

    [Fact]
    public void ForCoercesKeyToString()
    {
        // Symbol.for(5) and Symbol.for("5") share the same key (ToString rule).
        Assert.True(Run("Symbol.for(5) === Symbol.for('5');").AsBoolean());
    }

    [Fact]
    public void KeyForOnNonSymbolThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("Symbol.keyFor('not a symbol');"));
    }
}
