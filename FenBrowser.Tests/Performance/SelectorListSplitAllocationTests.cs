using System;
using System.Collections.Generic;
using System.Diagnostics;
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

    [Fact]
    public void ParseSelectorList_OrdinaryIdentifiersAvoidBuilderAllocations()
    {
        const int iterations = 10_000;
        const string selector = "main#content > article.card[data-state='ready']:first-child";
        GC.KeepAlive(SelectorMatcher.ParseSelectorList(selector));

        List<SelectorChain> parsed = null;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        long started = Stopwatch.GetTimestamp();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            parsed = SelectorMatcher.ParseSelectorList(selector);
        }

        double elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine(
            $"Parsing {iterations:N0} single-chain selectors took {elapsedMs:F3} ms and allocated {allocated:N0} B.");

        Assert.NotNull(parsed);
        Assert.Single(parsed);
        Assert.InRange(allocated, 1, 16_100_000);
    }

    [Fact]
    public void GetSpecificity_SelectorListHasBoundedSelectionAllocations()
    {
        const int iterations = 10_000;
        const string selector = "article .item, main#content, #hero.banner";
        GC.KeepAlive(SelectorMatcher.GetSpecificity(selector));

        (int a, int b, int c) specificity = default;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            specificity = SelectorMatcher.GetSpecificity(selector);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"Selecting specificity for {iterations:N0} selector lists allocated {allocated:N0} B.");

        Assert.Equal((1, 1, 0), specificity);
        Assert.InRange(allocated, 1, 17_100_000);
    }

    [Fact]
    public void ParseSelectorList_EscapedIdentifiersRetainDecodedValues()
    {
        List<SelectorChain> parsed = SelectorMatcher.ParseSelectorList(
            @"art\69 cle.c\61 rd#he\61 d:first-\63 hild");

        SelectorSegment segment = Assert.Single(Assert.Single(parsed).Segments);
        Assert.Equal("article", segment.TagName);
        Assert.Contains("card", segment.Classes);
        Assert.Equal("head", segment.Id);
        Assert.Equal("first-child", Assert.Single(segment.PseudoClasses).Name);
    }

    [Fact]
    public void MatchesParsedPseudoClass_DoesNotRenormalizeItsName()
    {
        const int iterations = 10_000;
        var parent = new Element("div");
        var first = new Element("span");
        parent.AppendChild(first);
        parent.AppendChild(new Element("span"));

        List<SelectorChain> parsed = SelectorMatcher.ParseSelectorList("span:FIRST-CHILD");
        Assert.Single(parsed);
        Assert.Equal("first-child", parsed[0].Segments[0].PseudoClasses[0].Name);
        Assert.True(SelectorMatcher.MatchesChain(first, parsed[0]));

        var matched = false;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterations; iteration++)
        {
            matched = SelectorMatcher.MatchesChain(first, parsed[0]);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"Matching one parsed pseudo-class {iterations:N0} times allocated {allocated:N0} B.");

        Assert.True(matched);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void MatchesParsedStructuralPseudoClasses_AvoidsSiblingIteratorAllocations()
    {
        const int iterationsPerSelector = 2_500;
        var parent = new Element("div");
        var firstSpan = new Element("span");
        var lastSpan = new Element("span");
        parent.AppendChild(new Text("before"));
        parent.AppendChild(firstSpan);
        parent.AppendChild(new Element("div"));
        parent.AppendChild(lastSpan);
        parent.AppendChild(new Text("after"));

        string[] selectors =
        {
            "span:first-child",
            "span:last-child",
            "span:first-of-type",
            "span:last-of-type"
        };
        Element[] targets = { firstSpan, lastSpan, firstSpan, lastSpan };
        var chains = new SelectorChain[selectors.Length];
        for (var index = 0; index < selectors.Length; index++)
        {
            chains[index] = SelectorMatcher.ParseSelectorList(selectors[index])[0];
            Assert.True(SelectorMatcher.MatchesChain(targets[index], chains[index]));
        }

        var matched = true;
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < iterationsPerSelector; iteration++)
        {
            for (var index = 0; index < chains.Length; index++)
            {
                matched &= SelectorMatcher.MatchesChain(targets[index], chains[index]);
            }
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine(
            $"Matching four parsed structural pseudo-classes {iterationsPerSelector:N0} times each allocated {allocated:N0} B.");

        Assert.True(matched);
        Assert.Equal(0, allocated);
    }
}
