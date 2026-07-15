using System;
using System.Linq;
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
}
