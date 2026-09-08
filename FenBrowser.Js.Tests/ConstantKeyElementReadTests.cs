using System.Linq;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ConstantKeyElementReadTests
{
    private static JsValue Run(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function);
    }

    private static BytecodeFunction Compile(string source)
        => new BytecodeCompiler().CompileScript(new SourceText(source));

    [Fact]
    public void LiteralIndexReadSkipsTheKeyRegister()
    {
        var function = Compile("var values = [10, 20, 30]; values[1];");

        var read = Assert.Single(
            function.Instructions,
            instruction => instruction.OpCode == OpCode.GetElemConst);

        Assert.Equal(JsValue.FromNumberCompact(1).AsNumber(), function.Constants[read.C].AsNumber());
        Assert.DoesNotContain(function.Instructions, instruction => instruction.OpCode == OpCode.GetElem);
    }

    [Fact]
    public void LiteralStringKeyReadSkipsTheKeyRegister()
    {
        var function = Compile("var o = { a: 1 }; o['a'];");

        var read = Assert.Single(
            function.Instructions,
            instruction => instruction.OpCode == OpCode.GetElemConst);

        Assert.Equal("a", function.Constants[read.C].AsString());
    }

    [Fact]
    public void ComputedKeyStillUsesTheGeneralForm()
    {
        var function = Compile("var values = [1, 2]; var i = 0; values[i];");

        Assert.Contains(function.Instructions, instruction => instruction.OpCode == OpCode.GetElem);
        Assert.DoesNotContain(function.Instructions, instruction => instruction.OpCode == OpCode.GetElemConst);
    }

    [Fact]
    public void ReadsDenseArrayElementsByLiteralIndex()
    {
        Assert.Equal(30d, Run("var values = [10, 20, 30]; values[2];").AsNumber());
    }

    [Fact]
    public void ReadsPastTheEndAsUndefined()
    {
        Assert.Equal(JsValueTag.Undefined, Run("var values = [10]; values[7];").Tag);
    }

    [Fact]
    public void ReadsStringKeyedProperties()
    {
        Assert.Equal(5d, Run("var o = { five: 5 }; o['five'];").AsNumber());
    }

    [Fact]
    public void ReadsInheritedPropertiesByLiteralKey()
    {
        Assert.Equal(3d, Run("var o = Object.create({ n: 3 }); o['n'];").AsNumber());
    }

    [Fact]
    public void ReadsThroughAccessorsByLiteralKey()
    {
        Assert.Equal(9d, Run("var o = { get k() { return 9; } }; o['k'];").AsNumber());
    }

    [Fact]
    public void NumericLiteralKeyIsCoercedToAStringPropertyName()
    {
        // `o[1]` and `o["1"]` name the same property: the constant path must
        // still run ToPropertyKey rather than assume an array index.
        Assert.Equal(42d, Run("var o = {}; o['1'] = 42; o[1];").AsNumber());
    }

    [Fact]
    public void ReadsCharactersOutOfAStringByLiteralIndex()
    {
        Assert.Equal("b", Run("var s = 'abc'; s[1];").AsString());
    }

    [Fact]
    public void ThrowsWhenTheBaseIsNullish()
    {
        var function = Compile("var o = null; o['x'];");
        new BytecodeVerifier().Verify(function);

        Assert.Throws<JsThrownException>(() => new BytecodeInterpreter().Execute(function));
    }

    [Fact]
    public void EvaluatesTheObjectBeforeTheRead()
    {
        // The object expression still runs exactly once, in source order, even
        // though the key no longer occupies an instruction of its own.
        Assert.Equal(1d, Run("var calls = 0; function o() { calls++; return [7]; } o()[0]; calls;").AsNumber());
    }
}
