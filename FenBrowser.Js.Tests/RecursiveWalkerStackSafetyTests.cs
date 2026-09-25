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
    public void AMillionTermLogicalChainParsesWithoutWalkingItAtEveryLink()
    {
        // The ?? / && / || mixing check used to re-walk the whole chain at each
        // link, recursively: quadratic, and a stack overflow long before here.
        var program = JsParser.ParseScript(new SourceText(DeepLogicalChain(1_000_000) + ";"));
        Assert.Single(program.Body);
    }

    [Theory]
    [InlineData("a ?? b || c")]
    [InlineData("a || b ?? c")]
    [InlineData("a && b ?? c")]
    [InlineData("a ?? b && c")]
    [InlineData("a || b || c ?? d")]
    public void MixingCoalesceWithLogicalOperatorsIsStillASyntaxError(string source)
    {
        Assert.Throws<JsParserException>(() => JsParser.ParseScript(new SourceText(source + ";")));
    }

    [Theory]
    [InlineData("(a ?? b) || c")]
    [InlineData("a || (b ?? c)")]
    [InlineData("(a && b) ?? c")]
    [InlineData("a ?? (b || c)")]
    [InlineData("a ?? b ?? c")]
    public void ParenthesizedMixingIsAllowed(string source)
    {
        Assert.Single(JsParser.ParseScript(new SourceText(source + ";")).Body);
    }

    [Fact]
    public void DeepLeftNestedClassBodyCompilesOnALargeStack()
    {
        // Deeper than a default thread can compile; the compiler retries on its
        // large-stack thread.
        var source = "class C { #field; method(value) { return " + DeepLogicalChain(6_000) + "; } }";
        Assert.NotNull(new BytecodeCompiler().CompileScript(new SourceText(source)));
    }

    [Fact]
    public void NestingDeeperThanEvenTheLargeStackFailsSafely()
    {
        var source = "class C { #field; method(value) { return " + DeepLogicalChain(600_000) + "; } }";
        // Too deep even for the compiler's large-stack thread: a catchable
        // error rather than a process-ending stack overflow.
        var exception = Assert.Throws<CompilerStackExhaustedException>(() =>
            new BytecodeCompiler().CompileScript(new SourceText(source)));

        Assert.Contains("insufficient stack", exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
