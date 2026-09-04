using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering.Css;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// The ancestor bloom filter's segment hash is memoized on the segment, because
/// it depends only on the segment's tag, id and classes but was being recomputed
/// (allocating a string per tag, id and class) for every element the selector was
/// tested against.
///
/// The filter drives a *fast reject*, so a wrong or stale hash does not throw -
/// it silently drops styles. These match the same selector against many elements,
/// which is exactly the path that reuses the memoized value.
/// </summary>
public sealed class AncestorFilterMemoizationTests
{
    private static Element BuildTree(int leaves)
    {
        var root = new Element("div");
        root.SetAttribute("class", "wrapper");
        var mid = new Element("section");
        mid.SetAttribute("class", "Box color-fg-default");
        root.AppendChild(mid);
        for (var i = 0; i < leaves; i++)
        {
            var leaf = new Element("span");
            leaf.SetAttribute("class", "text-bold");
            leaf.SetAttribute("id", "leaf" + i);
            mid.AppendChild(leaf);
        }

        return root;
    }

    [Fact]
    public void DescendantSelectorMatchesEveryLeafOnRepeatedEvaluation()
    {
        var root = BuildTree(50);
        var chain = SelectorMatcher.ParseSelectorList(".Box .text-bold")[0];

        var mid = (Element)root.FirstChild;
        var matched = 0;
        for (var child = mid.FirstChild; child != null; child = child.NextSibling)
        {
            if (child is Element leaf && SelectorMatcher.MatchesChain(leaf, chain))
            {
                matched++;
            }
        }

        Assert.Equal(50, matched);
    }

    [Fact]
    public void RepeatedEvaluationOfTheSameChainStaysStable()
    {
        var root = BuildTree(10);
        var chain = SelectorMatcher.ParseSelectorList("div.wrapper section .text-bold")[0];
        var leaf = (Element)((Element)root.FirstChild).FirstChild;

        // First call populates the memoized hash; every later call reads it.
        var first = SelectorMatcher.MatchesChain(leaf, chain);
        for (var i = 0; i < 200; i++)
        {
            Assert.Equal(first, SelectorMatcher.MatchesChain(leaf, chain));
        }

        Assert.True(first);
    }

    [Fact]
    public void NonMatchingAncestorIsStillRejectedAfterMemoization()
    {
        var root = BuildTree(5);
        var leaf = (Element)((Element)root.FirstChild).FirstChild;

        // Same leaf, an ancestor class that is not present anywhere in the chain.
        var absent = SelectorMatcher.ParseSelectorList(".not-present .text-bold")[0];
        for (var i = 0; i < 50; i++)
        {
            Assert.False(SelectorMatcher.MatchesChain(leaf, absent));
        }

        // And the genuine one still matches, so the rejection above was specific
        // rather than the filter rejecting everything.
        var present = SelectorMatcher.ParseSelectorList(".Box .text-bold")[0];
        Assert.True(SelectorMatcher.MatchesChain(leaf, present));
    }

    [Fact]
    public void IdAndTagSegmentsMemoizeToTheSameAnswer()
    {
        var root = BuildTree(4);
        var mid = (Element)root.FirstChild;
        var leaf = (Element)mid.FirstChild;

        var byId = SelectorMatcher.ParseSelectorList("#leaf0")[0];
        var byTagDescendant = SelectorMatcher.ParseSelectorList("section span")[0];

        for (var i = 0; i < 25; i++)
        {
            Assert.True(SelectorMatcher.MatchesChain(leaf, byId));
            Assert.True(SelectorMatcher.MatchesChain(leaf, byTagDescendant));
        }
    }

    // Two chains parsed separately from the same text must agree: the memo is
    // per-segment, so a second parse computes its own and both must match.
    [Fact]
    public void SeparatelyParsedChainsAgree()
    {
        var root = BuildTree(3);
        var leaf = (Element)((Element)root.FirstChild).FirstChild;

        var a = SelectorMatcher.ParseSelectorList(".Box .text-bold")[0];
        var b = SelectorMatcher.ParseSelectorList(".Box .text-bold")[0];

        Assert.True(SelectorMatcher.MatchesChain(leaf, a));
        Assert.True(SelectorMatcher.MatchesChain(leaf, b));
        Assert.Equal(
            SelectorMatcher.MatchesChain(leaf, a),
            SelectorMatcher.MatchesChain(leaf, b));
    }
}
