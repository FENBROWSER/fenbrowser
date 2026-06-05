using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class ComputedSymbolPropertyTests
{
    [Fact]
    public void ObjectLiteralComputedSymbolGetterParticipatesInToPrimitive()
    {
        var source = new SourceText("""
            try {
              Number({ get [Symbol.toPrimitive]() { throw new Error("x"); } });
              "no";
            } catch (e) {
              e && e.name;
            }
            """);
        var fn = new BytecodeCompiler().CompileScript(source);
        var result = new BytecodeInterpreter().Execute(fn);
        Assert.Equal("Error", result.AsString());
    }
}
