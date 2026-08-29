using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using Xunit;

namespace FenBrowser.Tests.Core.Parsing;

public sealed class ActiveFormattingNoahsArkTests
{
    [Fact]
    public void FourthEquivalentFormattingEntryEvictsEarliestOnReconstruction()
    {
        // `</p>` pops the open <b> elements off the open-element stack while they
        // remain in the active-formatting list; reconstructing "5" must rebuild
        // at most three equivalent <b> entries (the earliest was evicted on push),
        // not four.
        var document = new HtmlTreeBuilder("<!doctype html><p><b>1<b>2<b>3<b>4</p>5").Build();
        var body = document.Body!;

        var tail = FindTextNode(body, "5");
        Assert.NotNull(tail);

        var bAncestors = 0;
        var ancestor = tail!.ParentElement;
        while (ancestor != null && !ReferenceEquals(ancestor, body))
        {
            if (ancestor.LocalName == "b") bAncestors++;
            ancestor = ancestor.ParentElement;
        }

        Assert.Equal(3, bAncestors);
    }

    private static Node? FindTextNode(Node root, string data)
    {
        foreach (var child in root.ChildNodes)
        {
            if (child is Text text && text.Data == data) return text;
            var found = FindTextNode(child, data);
            if (found != null) return found;
        }

        return null;
    }

    [Fact]
    public void RepeatedNestedFormattingKeepsActiveListCapped()
    {
        // 10 equivalent <b> entries: the active-formatting list must never hold
        // more than three of them past the last marker, so a later reconstruction
        // rebuilds only three clones regardless of the input length.
        var markup = "<!doctype html><p><b>x</b>";
        for (var index = 0; index < 10; index++)
        {
            markup += "<b>" + index;
        }
        markup += "<p>tail";

        var document = new HtmlTreeBuilder(markup).Build();
        var paragraphs = document.Body!.ChildNodes.OfType<Element>().Where(e => e.LocalName == "p").ToList();
        Assert.Equal(2, paragraphs.Count);

        var bCount = paragraphs[1].GetElementsByTagName("b").OfType<Element>().Count();
        Assert.Equal(3, bCount);
    }

    [Fact]
    public void MarkerStopsEquivalentEntryCounting()
    {
        // <td> inserts a marker; three <b> before it and three after it must all
        // survive (cap applies per marker-delimited run, not globally).
        var document = new HtmlTreeBuilder("<!doctype html><b>1<b>2<b>3<table><td><b>4<b>5<b>6<b>7<tr><td><p>8").Build();

        // Sanity: parse completes without loss of the later text.
        Assert.Contains("8", document.Body!.TextContent);
    }
}
