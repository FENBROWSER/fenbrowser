using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Parser;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RecursiveWalkerStackSafetyTests
{
    private static string DeepLogicalChain(int terms) => string.Join(" || ", Enumerable.Repeat("value", terms));

    [Fact]
    public void DeepLeftNestedScriptSurvivesPostParseValidation()
    {
        var program = JsParser.ParseScript(new SourceText(DeepLogicalChain(6_000) + ";"));

        Assert.Single(program.Body);
    }

    [Fact]
    public void DeepLeftNestedModuleSurvivesRestrictionValidation()
    {
        var program = JsParser.ParseModule(new SourceText("const value = 1; " + DeepLogicalChain(6_000) + ";"));

        Assert.Equal(2, program.Body.Count);
    }

    [Fact]
    public void DeepLeftNestedClassBodyFailsSafelyDuringCompilation()
    {
        var source = "class C { #field; method(value) { return " + DeepLogicalChain(6_000) + "; } }";
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new BytecodeCompiler().CompileScript(new SourceText(source)));

        Assert.Contains("insufficient stack", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
