using FenBrowser.Js.Regex;
using FenBrowser.Js.Bytecode;
using FenBrowser.Js.Interpreter;
using FenBrowser.Js.Source;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RegexExecutionLimitTests
{
    // A backreference reads a capture slot, so the VM cannot share thread states
    // between two positions in the capture array - the pattern keeps its
    // exponential search and the budget is what stops it. Without one, the
    // memo makes this shape linear and it finishes (see MemoStopsRedosShapedSearches).
    private const string ExponentialWithBackReference = "^(?:(a)|a)+\\1b$";

    [Fact]
    public void BacktrackingBudgetExhaustionIsNotReportedAsNoMatch()
    {
        var program = RegExpCompiler.Compile(ExponentialWithBackReference, string.Empty).Program;
        var input = new string('a', 24) + "a";

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
        var source = "try { /^(?:(a)|a)+\\1b$/.test('" + new string('a', 24) + "a'); false; } " +
                     "catch (error) { typeof error === 'string' && error.includes('budget'); }";
        var function = new BytecodeCompiler().CompileScript(new SourceText(source));
        new BytecodeVerifier().Verify(function);

        Assert.True(new BytecodeInterpreter().Execute(function).AsBoolean());
    }

    // ReDoS shape with no backreference: nested quantifiers over an alternation
    // re-enter the same (instruction, position) pair from many paths. The VM
    // memoizes thread states for a whole start position, so this is answered
    // directly instead of exhausting the budget.
    [Fact]
    public void MemoStopsRedosShapedSearches()
    {
        var program = RegExpCompiler.Compile("^(a+)+$", string.Empty).Program;
        var input = new string('a', 30) + "!";

        var result = new RegexVM(program).Execute(input);
        Assert.False(result.Success);
    }

    // The shape Google's host allow-list regex in the YouTube player has: a
    // bounded host-label run repeated in front of an alternation, against a
    // subject that does not end in a member of it. Used to take ~1s and then
    // report "backtracking budget exceeded" from the page's own bootstrap.
    [Fact]
    public void MemoStopsRepeatedLabelRunsAgainstAnAlternation()
    {
        const string HostAllowList =
            "^(https?://(([a-z0-9-]{1,63}\\.)*(corp\\.example\\.com|example\\.com|video\\.example\\.com)[.]?(:[0-9]+)?/)|s2\\.exampleusercontent\\.com/f|v[3-4]\\.example\\.com/)";

        var program = RegExpCompiler.Compile(HostAllowList, string.Empty).Program;

        Assert.True(new RegexVM(program).Execute("https://video.example.com/embed?id=1").Success);
        Assert.True(new RegexVM(program).Execute("s2.exampleusercontent.com/favicon").Success);
        Assert.True(new RegexVM(program).Execute("v3.example.com/vi/x").Success);
        Assert.False(new RegexVM(program).Execute("https://not-a-listed-host.example.org/x").Success);
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
