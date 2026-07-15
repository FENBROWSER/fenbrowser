using System;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class CssSyntaxParserAllocationTests
{
    private readonly ITestOutputHelper _output;

    public CssSyntaxParserAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ParseStylesheet_OrdinarySelectorIdentifiersHaveBoundedAllocations()
    {
        const int ruleCount = 32;
        const int iterations = 100;
        string css = string.Join(
            Environment.NewLine,
            Enumerable.Range(0, ruleCount).Select(index =>
                $".component-{index} article[data-state='ready'] > span.label-{index}:hover {{ color: rgb(20, 30, 40); margin: 1px; }}"));

        var warmup = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
        Assert.Equal(ruleCount, warmup.Rules.Count);

        CssStylesheet sheet = null;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            sheet = new CssSyntaxParser(new CssTokenizer(css)).ParseStylesheet();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine(
            $"Parsing {ruleCount:N0} ordinary selector rules {iterations:N0} times allocated {allocated:N0} B.");

        Assert.NotNull(sheet);
        Assert.Equal(ruleCount, sheet.Rules.Count);
        var firstRule = Assert.IsType<CssStyleRule>(sheet.Rules[0]);
        Assert.Equal(".component-0 article[data-state=\"ready\"] > span.label-0:hover", firstRule.Selector.Raw);
        Assert.InRange(allocated, 1, 32_100_000);
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
}
