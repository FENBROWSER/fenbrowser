using System;
using System.Linq;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class CssSyntaxParserAllocationTests
{
    private const int RuleCount = 32;
    private const int Iterations = 100;

    private readonly ITestOutputHelper _output;

    public CssSyntaxParserAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ParseStylesheet_OrdinarySelectorIdentifiersHaveBoundedAllocations()
    {
        string css = CreateStylesheet("color: rgb(20, 30, 40); margin: 1px;");

        var warmup = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
        Assert.Equal(RuleCount, warmup.Rules.Count);

        CssStylesheet sheet = null;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            sheet = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine(
            $"Parsing {RuleCount:N0} ordinary selector rules {Iterations:N0} times allocated {allocated:N0} B.");

        Assert.NotNull(sheet);
        Assert.Equal(RuleCount, sheet.Rules.Count);
        var firstRule = Assert.IsType<CssStyleRule>(sheet.Rules[0]);
        Assert.Equal(".component-0 article[data-state=\"ready\"] > span.label-0:hover", firstRule.Selector.Raw);
        Assert.InRange(allocated, 1, 32_100_000);
    }

    [Fact]
    public void ParseStylesheet_SelectorPreludesHaveBoundedAllocations()
    {
        string css = CreateStylesheet(string.Empty);
        var warmup = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
        Assert.Equal(RuleCount, warmup.Rules.Count);

        CssStylesheet sheet = null;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            sheet = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine(
            $"Parsing {RuleCount:N0} selector preludes {Iterations:N0} times allocated {allocated:N0} B.");

        Assert.NotNull(sheet);
        Assert.Equal(RuleCount, sheet.Rules.Count);
        var firstRule = Assert.IsType<CssStyleRule>(sheet.Rules[0]);
        Assert.Equal(".component-0 article[data-state=\"ready\"] > span.label-0:hover", firstRule.Selector.Raw);
        Assert.InRange(allocated, 1, 23_100_000);
    }

    [Fact]
    public void ParseStylesheet_UnterminatedSelectorPreludeHasBoundedAllocations()
    {
        string css = string.Join(
            " ",
            Enumerable.Range(0, 128).Select(index => $".component-{index} span.label-{index}"));
        Assert.Empty(new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet().Rules);

        CssStylesheet sheet = null;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            sheet = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine(
            $"Parsing one unterminated selector prelude {Iterations:N0} times allocated {allocated:N0} B.");

        Assert.NotNull(sheet);
        Assert.Empty(sheet.Rules);
        Assert.InRange(allocated, 1, 15_300_000);
    }

    [Fact]
    public void CssTokenizer_OrdinaryInputHasBoundedPreprocessAllocations()
    {
        const int iterations = 10_000;
        string css = new('a', 256);

        GC.KeepAlive(new CssTokenizer(css));

        CssTokenizer tokenizer = null;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            tokenizer = new CssTokenizer(css);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"Constructing {iterations:N0} ordinary CSS tokenizers allocated {allocated:N0} B.");

        Assert.NotNull(tokenizer);
        Assert.Equal(css, tokenizer!.Consume().Value);
        Assert.InRange(allocated, 1, 350_000);
    }

    [Fact]
    public void CssTokenizer_PreprocessesCarriageReturnsAndNulls()
    {
        var tokenizer = new CssTokenizer("one\r\ntwo\rthree\0four");

        Assert.Equal("one", tokenizer.Consume().Value);
        Assert.Equal(CssTokenType.Whitespace, tokenizer.Consume().Type);
        Assert.Equal("two", tokenizer.Consume().Value);
        Assert.Equal(CssTokenType.Whitespace, tokenizer.Consume().Type);
        Assert.Equal("three\uFFFDfour", tokenizer.Consume().Value);
        Assert.Equal(CssTokenType.EOF, tokenizer.Consume().Type);
    }

    [Fact]
    public void CssTokenizer_OrdinaryNamesHaveBoundedAllocations()
    {
        const int nameCount = 10_000;
        const string name = "ordinaryidentifierwithlength";
        var source = new StringBuilder(nameCount * (name.Length + 1));
        for (var index = 0; index < nameCount; index++)
        {
            if (index != 0)
            {
                source.Append(' ');
            }

            source.Append(name);
        }

        string css = source.ToString();
        ConsumeIdentifierTokens(css, name, out _);

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        int consumedNames = ConsumeIdentifierTokens(css, name, out bool allNamesMatch);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        _output.WriteLine($"Tokenizing {consumedNames:N0} ordinary CSS names allocated {allocated:N0} B.");
        Assert.Equal(nameCount, consumedNames);
        Assert.True(allNamesMatch);
        Assert.InRange(allocated, 1, 830_000);
    }

    [Fact]
    public void CssTokenizer_EscapedNameRetainsDecodedValue()
    {
        var tokenizer = new CssTokenizer(@"ord\69 n\61 ry");

        Assert.Equal("ordinary", tokenizer.Consume().Value);
        Assert.Equal(CssTokenType.EOF, tokenizer.Consume().Type);
    }

    [Fact]
    public void ParseStylesheet_EscapedIdentifierPreservesSelectorText()
    {
        const string selector = @".component\ name";
        var sheet = new CssSyntaxParser(new CssTokenizer($"{selector} {{ color: red; }}")).ParseStylesheet();

        var rule = Assert.IsType<CssStyleRule>(Assert.Single(sheet.Rules));

        Assert.Equal(selector, rule.Selector.Raw);
        Assert.Single(rule.Selector.Chains);
    }

    [Fact]
    public void ParseStylesheet_EscapedPunctuationStillMatchesDecodedClass()
    {
        const string selector = @".a\+b";
        var sheet = new CssSyntaxParser(new CssTokenizer($"{selector} {{ color: red; }}")).ParseStylesheet();
        var rule = Assert.IsType<CssStyleRule>(Assert.Single(sheet.Rules));
        var element = new Element("div");
        element.SetAttribute("class", "a+b");

        Assert.True(SelectorMatcher.Matches(element, rule.Selector));
    }

    [Fact]
    public void ParseStylesheet_NestedSelectorsPreserveResolvedPreludeOrder()
    {
        const string css = """
            .card {
              color: red;
              & .title { color: blue; }
              .body { color: green; }
            }
            """;

        var sheet = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
        var parent = Assert.IsType<CssStyleRule>(Assert.Single(sheet.Rules));
        Assert.Equal(".card", parent.Selector.Raw);
        Assert.Equal(2, parent.NestedRules.Count);
        var explicitRule = Assert.IsType<CssStyleRule>(parent.NestedRules[0]);
        var implicitRule = Assert.IsType<CssStyleRule>(parent.NestedRules[1]);

        Assert.Equal(".card .title", explicitRule.Selector.Raw);
        Assert.Equal(".card .body", implicitRule.Selector.Raw);
        Assert.Contains(explicitRule.Declarations, declaration => declaration.Property == "color" && declaration.Value == "blue");
        Assert.Contains(implicitRule.Declarations, declaration => declaration.Property == "color" && declaration.Value == "green");
    }

    [Fact]
    public void ParseStylesheet_NestedMediaRulePreservesParentPrelude()
    {
        const string css = """
            .card {
              @media (min-width: 600px) {
                & .title { color: purple; }
              }
            }
            """;

        var sheet = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
        var parent = Assert.IsType<CssStyleRule>(Assert.Single(sheet.Rules));
        var media = Assert.IsType<CssMediaRule>(Assert.Single(parent.NestedRules));
        var nested = Assert.IsType<CssStyleRule>(Assert.Single(media.Rules));

        Assert.Equal("(min-width: 600px)", media.Condition);
        Assert.Equal(".card .title", nested.Selector.Raw);
        Assert.Contains(nested.Declarations, declaration => declaration.Property == "color" && declaration.Value == "purple");
    }

    private static string CreateStylesheet(string declarations)
    {
        return string.Join(
            Environment.NewLine,
            Enumerable.Range(0, RuleCount).Select(index =>
                $".component-{index} article[data-state='ready'] > span.label-{index}:hover {{ {declarations} }}"));
    }

    private static int ConsumeIdentifierTokens(string css, string expectedName, out bool allNamesMatch)
    {
        var tokenizer = new CssTokenizer(css);
        int nameCount = 0;
        allNamesMatch = true;
        while (true)
        {
            CssToken token = tokenizer.Consume();
            if (token.Type == CssTokenType.EOF)
            {
                return nameCount;
            }

            if (token.Type != CssTokenType.Ident)
            {
                continue;
            }

            nameCount++;
            allNamesMatch &= string.Equals(token.Value, expectedName, StringComparison.Ordinal);
        }
    }
}
