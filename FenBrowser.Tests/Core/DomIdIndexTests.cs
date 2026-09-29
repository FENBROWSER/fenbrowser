using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class DomIdIndexTests
{
    [Fact]
    public void DuplicateIdMutation_UpdatesOnlyAffectedBucket()
    {
        var document = Document.CreateHtmlDocument();
        var first = document.CreateElement("div");
        var second = document.CreateElement("div");
        first.Id = "duplicate";
        second.Id = "duplicate";
        document.Body.AppendChild(first);
        document.Body.AppendChild(second);

        Assert.Same(first, document.GetElementById("duplicate"));
        Assert.Equal(1, document.IdIndexFullRebuildCount);

        first.Id = "other";
        Assert.Same(second, document.GetElementById("duplicate"));
        Assert.Same(first, document.GetElementById("other"));
        Assert.Equal(1, document.IdIndexFullRebuildCount);
    }

    [Fact]
    public void ElementInsertedAfterTheIndexIsBuilt_IsFound()
    {
        var document = Document.CreateHtmlDocument();
        Assert.Null(document.GetElementById("late"));

        var late = document.CreateElement("div");
        late.Id = "late";
        var wrapper = document.CreateElement("section");
        var nested = document.CreateElement("span");
        nested.Id = "nested";
        wrapper.AppendChild(nested);
        document.Body.AppendChild(late);
        document.Body.AppendChild(wrapper);

        Assert.Same(late, document.GetElementById("late"));
        Assert.Same(nested, document.GetElementById("nested"));
        Assert.Equal(1, document.IdIndexFullRebuildCount);
    }

    [Fact]
    public void RemovedSubtree_IsNoLongerFound()
    {
        var document = Document.CreateHtmlDocument();
        var wrapper = document.CreateElement("section");
        wrapper.Id = "wrapper";
        var nested = document.CreateElement("span");
        nested.Id = "nested";
        wrapper.AppendChild(nested);
        document.Body.AppendChild(wrapper);
        Assert.Same(nested, document.GetElementById("nested"));

        document.Body.RemoveChild(wrapper);

        Assert.Null(document.GetElementById("wrapper"));
        Assert.Null(document.GetElementById("nested"));
    }

    [Fact]
    public void ElementMovedToAnotherDocument_BelongsToThatDocumentOnly()
    {
        var first = Document.CreateHtmlDocument();
        var second = Document.CreateHtmlDocument();
        var moved = first.CreateElement("div");
        moved.Id = "moved";
        first.Body.AppendChild(moved);
        Assert.Same(moved, first.GetElementById("moved"));
        Assert.Null(second.GetElementById("moved"));

        second.Body.AppendChild(second.AdoptNode(moved));

        Assert.Null(first.GetElementById("moved"));
        Assert.Same(moved, second.GetElementById("moved"));
    }
}
