using System;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class CharacterDataMutationAllocationTests
{
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

        var directRecords = new List<MutationRecord>();
        var subtreeRecords = new List<MutationRecord>();
        var directObserver = new MutationObserver((records, _) => directRecords.AddRange(records));
        var subtreeObserver = new MutationObserver((records, _) => subtreeRecords.AddRange(records));
        directObserver.Observe(parent, new MutationObserverInit
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

        text.Data = "after";

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
