using System;
using System.Linq;
using FenBrowser.Core.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class HtmlTokenizerDoctypeRecoveryTests
{
    private readonly ITestOutputHelper _output;

    public HtmlTokenizerDoctypeRecoveryTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Theory]
    [InlineData("<!DOCTYPE a\0b>", "a\uFFFDb")]
    [InlineData("<!DOCTYPE ab\0>", "ab\uFFFD")]
    [InlineData("<!DOCTYPE a\0\0b>", "a\uFFFD\uFFFDb")]
    public void DoctypeName_ReplacesEveryNullCodePoint(string html, string expectedName)
    {
        DoctypeToken token = TokenizeSingleDoctype(html);

        Assert.Equal(expectedName, token.Name);
        Assert.DoesNotContain('\0', token.Name);
    }

    [Theory]
    [InlineData("<!DOCTYPE html PUBLIC \"a\0b\">", true)]
    [InlineData("<!DOCTYPE html SYSTEM \"a\0b\">", false)]
    public void QuotedDoctypeIdentifier_ReplacesEveryNullCodePoint(
        string html,
        bool isPublicIdentifier)
    {
        DoctypeToken token = TokenizeSingleDoctype(html);
        string identifier = isPublicIdentifier
            ? token.PublicIdentifier
            : token.SystemIdentifier;

        Assert.Equal("a\uFFFDb", identifier);
        Assert.DoesNotContain('\0', identifier);
        Assert.False(token.ForceQuirks);
    }

    [Theory]
    [InlineData("<!DOCTYPE html PUBLIC \"a>b\">", true)]
    [InlineData("<!DOCTYPE html SYSTEM \"a>b\">", false)]
    public void GreaterThanInsideQuotedIdentifier_RemainsData(
        string html,
        bool isPublicIdentifier)
    {
        HtmlToken[] tokens = new HtmlTokenizer(html).Tokenize().ToArray();
        DoctypeToken token = Assert.Single(tokens.OfType<DoctypeToken>());
        string identifier = isPublicIdentifier
            ? token.PublicIdentifier
            : token.SystemIdentifier;

        Assert.Equal("a>b", identifier);
        Assert.False(token.ForceQuirks);
        Assert.DoesNotContain(tokens, candidate => candidate is CharacterToken);
    }

    [Fact]
    public void PublicAndSystemIdentifiers_NormalizeNullIndependently()
    {
        DoctypeToken token = TokenizeSingleDoctype(
            "<!DOCTYPE html PUBLIC \"p\0id\" \"s\0id\">");

        Assert.Equal("p\uFFFDid", token.PublicIdentifier);
        Assert.Equal("s\uFFFDid", token.SystemIdentifier);
        Assert.False(token.ForceQuirks);
    }

    [Theory]
    [InlineData("<!DOCTYPE html PUBLIC 'a\0b", true)]
    [InlineData("<!DOCTYPE html SYSTEM 'a\0b", false)]
    public void EofInQuotedIdentifier_PreservesNormalizedPartialDataAndForcesQuirks(
        string html,
        bool isPublicIdentifier)
    {
        DoctypeToken token = TokenizeSingleDoctype(html);
        string identifier = isPublicIdentifier
            ? token.PublicIdentifier
            : token.SystemIdentifier;

        Assert.Equal("a\uFFFDb", identifier);
        Assert.True(token.ForceQuirks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsecutiveDoctypes_ClearTokenizerOwnedBuffers(bool useTokenPool)
    {
        const string html = "<!DOCTYPE one><!DOCTYPE two SYSTEM 'id'>";
        var tokenizer = useTokenPool
            ? new HtmlTokenizer(html, new HtmlTokenPool())
            : new HtmlTokenizer(html);
        DoctypeToken[] doctypes = tokenizer.Tokenize().OfType<DoctypeToken>().ToArray();

        Assert.Equal(2, doctypes.Length);
        Assert.Equal("one", doctypes[0].Name);
        Assert.Null(doctypes[0].SystemIdentifier);
        Assert.Equal("two", doctypes[1].Name);
        Assert.Equal("id", doctypes[1].SystemIdentifier);
    }

    [Theory]
    [InlineData("<!DOCTYPE")]
    [InlineData("<!DOCTYPE ")]
    [InlineData("<!DOCTYPE html")]
    [InlineData("<!DOCTYPE html ")]
    [InlineData("<!DOCTYPE html PUBLIC")]
    [InlineData("<!DOCTYPE html PUBLIC ")]
    [InlineData("<!DOCTYPE html PUBLIC 'id'")]
    [InlineData("<!DOCTYPE html SYSTEM")]
    [InlineData("<!DOCTYPE html SYSTEM 'id'")]
    [InlineData("<!DOCTYPE html invalid")]
    public void EofRecovery_EmitsExactlyOneDoctypeThenEof(string html)
    {
        HtmlToken[] tokens = new HtmlTokenizer(html).Tokenize().ToArray();

        Assert.Equal(2, tokens.Length);
        DoctypeToken doctype = Assert.IsType<DoctypeToken>(tokens[0]);
        Assert.IsType<EofToken>(tokens[1]);
        Assert.True(doctype.ForceQuirks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DoctypeRecovery_IsIdenticalWithAndWithoutTokenPool(bool useTokenPool)
    {
        const string html = "<!DOCTYPE a\0b SYSTEM \"s\0id\">";
        var tokenizer = useTokenPool
            ? new HtmlTokenizer(html, new HtmlTokenPool())
            : new HtmlTokenizer(html);
        DoctypeToken token = Assert.Single(tokenizer.Tokenize().OfType<DoctypeToken>());

        Assert.Equal("a\uFFFDb", token.Name);
        Assert.Equal("s\uFFFDid", token.SystemIdentifier);
        Assert.False(token.ForceQuirks);
    }

    [Fact]
    public void LongDoctypeName_AccumulatesWithinLinearAllocationBudget()
    {
        const int nameLength = 12_000;
        _ = new HtmlTokenizer("<!DOCTYPE warmup>").Tokenize().ToArray();
        string html = $"<!DOCTYPE {new string('a', nameLength)}>";

        long before = GC.GetAllocatedBytesForCurrentThread();
        DoctypeToken token = Assert.Single(
            new HtmlTokenizer(html).Tokenize().OfType<DoctypeToken>());
        long allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        _output.WriteLine($"{nameLength:N0}-character doctype name allocated {allocatedBytes:N0} bytes.");
        Assert.Equal(nameLength, token.Name.Length);
        Assert.All(token.Name, character => Assert.Equal('a', character));
        Assert.True(
            allocatedBytes < 1_000_000,
            $"Expected linear doctype-name accumulation below 1,000,000 bytes; allocated {allocatedBytes:N0} bytes.");
    }

    private static DoctypeToken TokenizeSingleDoctype(string html)
    {
        return Assert.Single(new HtmlTokenizer(html).Tokenize().OfType<DoctypeToken>());
    }
}
