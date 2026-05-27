using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Runtime;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class TypeOfRuntimeTests
{
    private static JsValue Run(string source)
    {
        var compiler = new BytecodeCompiler();
        var fn = compiler.CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(fn);
        return new BytecodeInterpreter().Execute(fn);
    }

    [Fact]
    public void TypeOfUndeclaredIdentifierReturnsUndefined()
    {
        Assert.Equal("undefined", Run("typeof NotDefined;").AsString());
    }

    [Fact]
    public void TypeOfTdzBindingThrowsReferenceError()
    {
        var code = @"
            var caught = false;
            try { typeof tdz; } catch (e) { caught = e instanceof ReferenceError; }
            let tdz = 1;
            caught;
        ";
        Assert.True(Run(code).AsBoolean());
    }
}
