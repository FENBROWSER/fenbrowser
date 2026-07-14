using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class SelectorListSplitAllocationTests
{
    private const string Selector =
        "article:is(.featured, .pinned) > [data-label='a,b'], nav.primary > a[href], main#content .item";

    private readonly ITestOutputHelper _output;

    public SelectorListSplitAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ParseSelectorList_AvoidsIntermediatePartCollection()
    {
        const int iterations = 1_000;
        GC.KeepAlive(SelectorMatcher.ParseSelectorList(Selector));

        List<SelectorChain> parsed = null;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            parsed = SelectorMatcher.ParseSelectorList(Selector);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"Parsing {iterations:N0} selector lists allocated {allocated:N0} B.");

        Assert.NotNull(parsed);
        Assert.Equal(3, parsed.Count);
        Assert.InRange(allocated, 1, 5_450_000);

        var article = new Element("article");
        article.SetAttribute("class", "featured");
        var labelled = new Element("span");
        labelled.SetAttribute("data-label", "a,b");
        article.AppendChild(labelled);

        var nav = new Element("nav");
        nav.SetAttribute("class", "primary");
        var link = new Element("a");
        link.SetAttribute("href", "/");
        nav.AppendChild(link);

        var main = new Element("main");
        main.SetAttribute("id", "content");
        var item = new Element("span");
        item.SetAttribute("class", "item");
        main.AppendChild(item);

        Assert.True(SelectorMatcher.MatchesChain(labelled, parsed[0]));
        Assert.True(SelectorMatcher.MatchesChain(link, parsed[1]));
        Assert.True(SelectorMatcher.MatchesChain(item, parsed[2]));
    }
}
