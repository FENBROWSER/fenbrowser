using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class NativeBuiltinFunctionPrototypeTests
{
    private static bool RunBoolean(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function).AsBoolean();
    }

    [Theory]
    [InlineData("String.prototype.charAt")]
    [InlineData("String.prototype.trimStart")]
    [InlineData("Boolean.prototype.valueOf")]
    [InlineData("Error.prototype.toString")]
    [InlineData("Error.isError")]
    [InlineData("String.prototype[Symbol.iterator]")]
    [InlineData("Object.getOwnPropertyDescriptor(Error.prototype, 'stack').get")]
    public void NativeBuiltinMethodsInheritFunctionPrototype(string expression)
    {
        Assert.True(RunBoolean($"Object.getPrototypeOf({expression}) === Function.prototype;"));
    }

    [Theory]
    [InlineData("String.prototype.charAt")]
    [InlineData("Boolean.prototype.toString")]
    [InlineData("Error.prototype.toString")]
    public void NativeBuiltinMethodsDoNotHaveFakeOwnCallProperty(string expression)
    {
        Assert.True(RunBoolean($"!Object.prototype.hasOwnProperty.call({expression}, 'call');"));
    }

    [Fact]
    public void ApplyAndBindResolveThroughFunctionPrototype()
    {
        Assert.True(RunBoolean("""
            String.prototype.charAt.apply('abc', [1]) === 'b' &&
            Boolean.prototype.valueOf.bind(true)() === true &&
            Error.prototype.toString.apply({ name: 'X', message: 'Y' }) === 'X: Y';
            """));
    }
}
