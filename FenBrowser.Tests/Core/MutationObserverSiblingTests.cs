using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class MutationObserverSiblingTests
{
    [Fact]
    public void AppendRecordIncludesPreviousSibling()
    {
        var parent = new Element("div");
        var first = new Element("p");
        var added = new Element("span");
        parent.AppendChild(first);

        var record = ObserveSingleMutation(parent, () => parent.AppendChild(added));

        Assert.Same(first, record.PreviousSibling);
        Assert.Null(record.NextSibling);
        Assert.Equal(new Node[] { added }, record.AddedNodes);
        Assert.Empty(record.RemovedNodes);
    }

    [Fact]
    public void InsertBeforeRecordIncludesBothAdjacentSiblings()
    {
        var parent = new Element("div");
        var first = new Element("p");
        var last = new Element("i");
        var added = new Element("span");
        parent.AppendChild(first);
        parent.AppendChild(last);

        var record = ObserveSingleMutation(parent, () => parent.InsertBefore(added, last));

        Assert.Same(first, record.PreviousSibling);
        Assert.Same(last, record.NextSibling);
        Assert.Equal(new Node[] { added }, record.AddedNodes);
        Assert.Empty(record.RemovedNodes);
    }

    [Fact]
    public void RemoveRecordPreservesFormerAdjacentSiblings()
    {
        var parent = new Element("div");
        var first = new Element("p");
        var removed = new Element("span");
        var last = new Element("i");
        parent.AppendChild(first);
        parent.AppendChild(removed);
        parent.AppendChild(last);

        var record = ObserveSingleMutation(parent, () => parent.RemoveChild(removed));

        Assert.Same(first, record.PreviousSibling);
        Assert.Same(last, record.NextSibling);
        Assert.Empty(record.AddedNodes);
        Assert.Equal(new Node[] { removed }, record.RemovedNodes);
    }

    private static MutationRecord ObserveSingleMutation(ContainerNode target, Action mutate)
    {
        var scheduled = new Queue<Action>();
        var delivered = new List<MutationRecord>();
        var observer = new MutationObserver(
            (records, _) => delivered.AddRange(records),
            scheduled.Enqueue);
        observer.Observe(target, new MutationObserverInit { ChildList = true });

        mutate();
        Assert.Empty(delivered);
        Assert.Single(scheduled).Invoke();
        observer.Disconnect();

        return Assert.Single(delivered);
    }
}
