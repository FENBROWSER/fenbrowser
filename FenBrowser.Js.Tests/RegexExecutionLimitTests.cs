using FenBrowser.Js.Regex;
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
}
