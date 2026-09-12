using FenBrowser.Js.Regex;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RegexExecutionLimitTests
{
    [Fact]
    public void BacktrackingBudgetExhaustionIsNotReportedAsNoMatch()
    {
        var program = RegExpCompiler.Compile("^(a+)+$", string.Empty).Program;
        var input = new string('a', 30) + "!";

        var error = Assert.Throws<RegexExecutionLimitException>(() => new RegexVM(program).Execute(input));
        Assert.Contains("budget", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryFailedMatchStillReturnsFailure()
    {
        var program = RegExpCompiler.Compile("^a+$", string.Empty).Program;
        Assert.False(new RegexVM(program).Execute("aaab").Success);
    }

    [Fact]
    public void JavaScriptCanCatchBacktrackingBudgetExhaustion()
    {
        var source = "try { /^(a+)+$/.test('" + new string('a', 30) + "!'); false; } " +
                     "catch (error) { typeof error === 'string' && error.includes('budget'); }";
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);

        Assert.True(new BytecodeInterpreter().Execute(function).AsBoolean());
    }

    // A greedy class run followed by a literal that never follows it is
    // quadratic, not exponential: Polymer's dir-mixin runs this over every
    // element stylesheet and must finish rather than trip the budget.
    [Fact]
    public void QuadraticPatternOverAStylesheetCompletes()
    {
        var program = RegExpCompiler.Compile(@"([\s\w-#\.\[\]\*]*):dir\((ltr|rtl)\)", string.Empty).Program;
        var css = string.Concat(Enumerable.Repeat(".paper-input-container .label-is-floating { transform: translateY(-75%) scale(0.75); } ", 100));

        Assert.False(new RegexVM(program).Execute(css).Success);
    }
}
