using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core;

// DOM 4.3: records are queued and delivered from a microtask, so the tests read
// the queue with takeRecords() rather than waiting on the callback.
public sealed class DomMutationNotificationTests
{
    // The delivery microtask never runs here, so takeRecords() reads the whole
    // queue. Without a scheduler, Core delivers on the thread pool, which can
    // drain the queue into the callback before the test looks.
    private static void HoldDelivery(Action deliver) { }

    [Fact]
    public void RemovePublishesDirectMutationObserverRecord()
    {
        var parent = new Element("div");
        var child = new Element("span");
        parent.AppendChild(child);
        var observer = new MutationObserver((_, _) => { }, HoldDelivery);
        observer.Observe(parent, new MutationObserverInit { ChildList = true });

        parent.RemoveChild(child);
        var records = observer.TakeRecords();
        observer.Disconnect();

        var record = Assert.Single(records);
        Assert.Equal(MutationRecordType.ChildList, record.Type);
        Assert.Same(parent, record.Target);
        Assert.Equal([child], record.RemovedNodes);
        Assert.True(record.AddedNodes is null || record.AddedNodes.Count == 0);
    }

    [Fact]
    public void AppendPublishesDirectMutationObserverRecord()
    {
        var parent = new Element("div");
        var child = new Element("span");
        var observer = new MutationObserver((_, _) => { }, HoldDelivery);
        observer.Observe(parent, new MutationObserverInit { ChildList = true });

        parent.AppendChild(child);
        var records = observer.TakeRecords();
        observer.Disconnect();

        var record = Assert.Single(records);
        Assert.Equal(MutationRecordType.ChildList, record.Type);
        Assert.Same(parent, record.Target);
        Assert.Equal([child], record.AddedNodes);
        Assert.True(record.RemovedNodes is null || record.RemovedNodes.Count == 0);
    }

    [Fact]
    public void AppendPublishesSubtreeMutationObserverRecord()
    {
        var ancestor = new Element("main");
        var parent = new Element("div");
        ancestor.AppendChild(parent);
        var child = new Element("span");
        var observer = new MutationObserver((_, _) => { }, HoldDelivery);
        observer.Observe(ancestor, new MutationObserverInit { ChildList = true, Subtree = true });

        parent.AppendChild(child);
        var records = observer.TakeRecords();
        observer.Disconnect();

        var record = Assert.Single(records);
        Assert.Equal(MutationRecordType.ChildList, record.Type);
        Assert.Same(parent, record.Target);
        Assert.Equal([child], record.AddedNodes);
        Assert.True(record.RemovedNodes is null || record.RemovedNodes.Count == 0);
    }

    [Fact]
    public void AppendAndRemovePublishDevToolsMutationPayloads()
    {
        var parent = new Element("div");
        var child = new Element("span");
        var records = new List<(Node Target, string Type, IReadOnlyList<Node> Added, IReadOnlyList<Node> Removed)>();

        void OnMutation(
            Node target,
            string type,
            string attributeName,
            string attributeNamespace,
            List<Node> added,
            List<Node> removed)
        {
            if (!ReferenceEquals(target, parent))
            {
                return;
            }

            records.Add((
                target,
                type,
                added is null ? Array.Empty<Node>() : added,
                removed is null ? Array.Empty<Node>() : removed));
        }

        Node.OnMutation += OnMutation;
        try
        {
            parent.AppendChild(child);
            parent.RemoveChild(child);
        }
        finally
        {
            Node.OnMutation -= OnMutation;
        }

        Assert.Collection(
            records,
            record =>
            {
                Assert.Same(parent, record.Target);
                Assert.Equal("childList", record.Type);
                Assert.Equal([child], record.Added);
                Assert.Empty(record.Removed);
            },
            record =>
            {
                Assert.Same(parent, record.Target);
                Assert.Equal("childList", record.Type);
                Assert.Empty(record.Added);
                Assert.Equal([child], record.Removed);
            });
    }
}
