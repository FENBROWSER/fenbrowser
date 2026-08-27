using System.Linq;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlTokenizerErrorDiagnosticsTests
{
    [Fact]
    public void TokenizerRecordsRecoverableErrorsWithCursorLocation()
    {
        var tokenizer = new HtmlTokenizer("a\0b");

        _ = tokenizer.Tokenize().ToArray();

        var error = Assert.Single(tokenizer.Errors);
        Assert.Equal("Unexpected Null Character In Data", error.Message);
        Assert.Equal(2, error.Offset);
        Assert.Equal(1, error.Line);
        Assert.Equal(3, error.Column);
        Assert.Equal(1, tokenizer.ErrorCount);
        Assert.False(tokenizer.ErrorsTruncated);
    }

    [Fact]
    public void ParserOutcomeCarriesTokenizerErrorsWithoutMarkingRecoveryAsFailure()
    {
        _ = HtmlParser.ParseDocument("<p>a\0b</p>", out var outcome);

        Assert.Equal(HtmlParsingOutcomeClass.Success, outcome.OutcomeClass);
        Assert.Equal(HtmlParsingReasonCode.None, outcome.ReasonCode);
        Assert.Equal(1, outcome.TokenizerErrorCount);
        Assert.False(outcome.TokenizerErrorsTruncated);
        Assert.Equal("Unexpected Null Character In Data", Assert.Single(outcome.TokenizerErrors).Message);
    }

    [Fact]
    public void ErrorStorageIsBoundedWhileTotalCountRemainsAccurate()
    {
        var tokenizer = new HtmlTokenizer(new string('\0', 300));

        _ = tokenizer.Tokenize().ToArray();

        Assert.Equal(300, tokenizer.ErrorCount);
        Assert.Equal(256, tokenizer.Errors.Count);
        Assert.True(tokenizer.ErrorsTruncated);
    }
}
