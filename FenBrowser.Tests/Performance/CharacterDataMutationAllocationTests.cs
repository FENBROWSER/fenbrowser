using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class CharacterDataMutationAllocationTests
{
    // The delivery microtask never runs here, so takeRecords() reads the whole
    // queue. Without a scheduler, Core delivers on the thread pool, which can
    // drain the queue into the callback before the test looks.
    private static void HoldDelivery(Action deliver) { }

    private readonly ITestOutputHelper _output;

    public CharacterDataMutationAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DataChange_WithoutObserversAvoidsMutationRecordAllocation()
    {
        var parent = new Element("div");
        var text = new Text("a");
        parent.AppendChild(text);

        text.Data = "b";
        text.Data = "a";

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            text.Data = (iteration & 1) == 0 ? "b" : "a";
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"10,000 unobserved character-data changes allocated {allocated:N0} B.");
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void DataChange_PreservesDirectAndSubtreeObserverRecords()
    {
        var ancestor = new Element("section");
        var parent = new Element("div");
        var text = new Text("before");
        ancestor.AppendChild(parent);
        parent.AppendChild(text);

        // DOM 4.3.2 "queue a mutation record": a registration on the target itself
        // always sees the change, one on an ancestor only with subtree. Delivery is
        // a microtask, so the queues are read with takeRecords().
        var directObserver = new MutationObserver((_, _) => { }, HoldDelivery);
        var subtreeObserver = new MutationObserver((_, _) => { }, HoldDelivery);
        var parentObserver = new MutationObserver((_, _) => { }, HoldDelivery);
        directObserver.Observe(text, new MutationObserverInit
        {
            CharacterData = true,
            CharacterDataOldValue = true
        });
        subtreeObserver.Observe(ancestor, new MutationObserverInit
        {
            CharacterData = true,
            CharacterDataOldValue = true,
            Subtree = true
        });
        parentObserver.Observe(parent, new MutationObserverInit
        {
            CharacterData = true,
            CharacterDataOldValue = true
        });

        text.Data = "after";

        var directRecords = directObserver.TakeRecords();
        var subtreeRecords = subtreeObserver.TakeRecords();
        Assert.Empty(parentObserver.TakeRecords());

        var direct = Assert.Single(directRecords);
        Assert.Equal(MutationRecordType.CharacterData, direct.Type);
        Assert.Same(text, direct.Target);
        Assert.Equal("before", direct.OldValue);

        var subtree = Assert.Single(subtreeRecords);
        Assert.Equal(MutationRecordType.CharacterData, subtree.Type);
        Assert.Same(text, subtree.Target);
        Assert.Equal("before", subtree.OldValue);
    }
}
