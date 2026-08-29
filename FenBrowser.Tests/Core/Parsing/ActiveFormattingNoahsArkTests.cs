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
    public void MarkerScopedFormattingRunDiesWithItsTableCell()
    {
        // <td> pushes a marker; the b run accumulated after it must be discarded
        // when the cell closes (ClearActiveFormattingElementsMarker), so nothing
        // reconstructs into the next cell.
        var document = new HtmlTreeBuilder(
            "<!doctype html><table><tr><td><b>1<b>2<b>3<b>4</td><td>5</td></table>").Build();
        var body = document.Body!;

        var cells = body.GetElementsByTagName("td").OfType<Element>().ToList();
        Assert.Equal(2, cells.Count);

        var firstCell = cells[0];
        var secondCell = cells[1];

        // First cell keeps its nested formatting run in the DOM...
        Assert.Equal("1234", firstCell.TextContent);
        Assert.NotEmpty(firstCell.GetElementsByTagName("b").OfType<Element>());

        // ...but the marker-scoped run is gone from the active-formatting list:
        // the next cell must contain plain text with no reconstructed <b> clones.
        Assert.Equal("5", secondCell.TextContent);
        Assert.Empty(secondCell.GetElementsByTagName("b").OfType<Element>());
    }

    [Fact]
    public void FormattingElementPoppedByTableContextClear_IsNotResurrected()
    {
        // `</table>` pops the fostered <b> off the open-element stack while it
        // stays in the active-formatting list (stale reference). A later `</b>`
        // must hit the AAA "not on the stack" recovery path: remove from the list
        // and return, never reconstructing a clone.
        var document = new HtmlTreeBuilder("<!doctype html><table><b>x</table></b>y").Build();
        var body = document.Body!;

        var table = body.ChildNodes.OfType<Element>().Single(e => e.LocalName == "table");
        Assert.Equal("x", table.PreviousSibling!.TextContent); // fostered <b> keeps "x"

        // The trailing text is a direct child of body: no <b> clone wraps "y".
        Assert.Equal("y", body.ChildNodes.OfType<Text>().Last().Data);
    }
}
