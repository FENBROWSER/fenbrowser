using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class StringRawTests
{
    private static JsValue Run(string source)
    {
        var fn = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void InterleavesRawAndSubstitutions()
    {
        Assert.Equal("a1b2c", Run("String.raw({raw:['a','b','c']}, 1, 2);").AsString());
    }

    [Fact]
    public void SubstitutionsCoerced()
    {
        Assert.Equal("xtruey", Run("String.raw({raw:['x','y']}, true);").AsString());
    }

    [Fact]
    public void FewerSubstitutionsThanGapsLeavesGapsEmpty()
    {
        Assert.Equal("ab", Run("String.raw({raw:['a','b']});").AsString());
    }

    [Fact]
    public void EmptyRawReturnsEmptyString()
    {
        Assert.Equal(string.Empty, Run("String.raw({raw:[]});").AsString());
    }

    [Fact]
    public void MissingTemplateThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("String.raw();"));
    }

    [Fact]
    public void NonObjectRawThrows()
    {
        Assert.Throws<JsThrownException>(() => Run("String.raw({raw:5});"));
    }
}
