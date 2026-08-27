using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class PrivateNameRewriterPayloadTests
{
    private static JsValue Run(string source)
    {
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);
        return new BytecodeInterpreter().Execute(function);
    }

    [Theory]
    [InlineData("class C { #x; m() { let { value } = { value: 42 }; return value; } } new C().m();")]
    [InlineData("class C { #x; m() { try { throw { value: 42 }; } catch ({ value }) { return value; } } } new C().m();")]
    [InlineData("class C { #x; m() { function read({ value } = { value: 42 }) { return value; } return read(); } } new C().m();")]
    [InlineData("class C { #x; m() { let read = ({ value } = { value: 42 }) => value; return read(); } } new C().m();")]
    [InlineData("class C { #x; m() { let read = function({ value } = { value: 42 }) { return value; }; return read(); } } new C().m();")]
    public void PrivateNameRewritePreservesBindingPayloads(string source)
    {
        Assert.Equal(42, Run(source).AsNumber());
    }
}
