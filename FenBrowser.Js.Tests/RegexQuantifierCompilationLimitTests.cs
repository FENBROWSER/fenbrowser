using FenBrowser.Js.Regex;
using Xunit;

namespace FenBrowser.Js.Tests;

public sealed class RegexQuantifierCompilationLimitTests
{
    [Theory]
    [InlineData("a{100001}")]
    [InlineData("a{100001,}")]
    [InlineData("(?<=a{100001})b")]
    public void QuantifiersThatWouldOverExpandBytecodeAreRejected(string pattern)
    {
        var error = Assert.Throws<RegexSyntaxError>(() => RegExpCompiler.Compile(pattern, string.Empty));
        Assert.Contains("implementation limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void QuantifierAtExpansionLimitStillCompiles()
    {
        var compiled = RegExpCompiler.Compile("a{100000}", string.Empty);
        Assert.Equal(100001, compiled.Program.Instructions.Length);
    }
}
