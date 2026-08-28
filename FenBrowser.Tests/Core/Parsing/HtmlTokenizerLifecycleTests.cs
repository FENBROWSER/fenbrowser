using System;
using System.Linq;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlTokenizerLifecycleTests
{
    [Fact]
    public void SecondEnumerationFailsInsteadOfReturningAStateDependentStream()
    {
        var tokenizer = new HtmlTokenizer("<p>text</p>");
        _ = tokenizer.Tokenize().ToArray();

        var error = Assert.Throws<InvalidOperationException>(() => tokenizer.Tokenize().ToArray());

        Assert.Contains("single-use", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AbandonedEnumerationStillConsumesTheTokenizerLifecycle()
    {
        var tokenizer = new HtmlTokenizer("abcdef");
        using (var tokens = tokenizer.Tokenize().GetEnumerator())
        {
            Assert.True(tokens.MoveNext());
        }

        Assert.Throws<InvalidOperationException>(() => tokenizer.Tokenize().ToArray());
    }
}
