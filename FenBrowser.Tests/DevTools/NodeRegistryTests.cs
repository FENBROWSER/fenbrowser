using Xunit;
using FenBrowser.Core.Dom.V2;
using FenBrowser.DevTools.Core;
using System;

namespace FenBrowser.Tests.DevTools;

public class NodeRegistryTests
{
    [Fact]
    public void Assigns_Incremental_Ids()
    {
        var registry = new NodeRegistry();
        var node1 = new Element("div");
        var node2 = new Element("span");

        int id1 = registry.GetId(node1);
        int id2 = registry.GetId(node2);

        Assert.Equal(1, id1);
        Assert.Equal(2, id2);
    }

    [Fact]
    public void Maintains_Stable_Identity()
    {
        var registry = new NodeRegistry();
        var node = new Element("div");

        int id1 = registry.GetId(node);
        int id2 = registry.GetId(node);

        Assert.Equal(id1, id2);
        Assert.Same(node, registry.GetNode(id1));
    }

    [Fact]
    public void Handles_Garbage_Collection()
    {
        var registry = new NodeRegistry();
        int id = 0;

        // Use a scope to ensure the node is collectible
        new Action(() => {
            var node = new Element("div");
            id = registry.GetId(node);
            Assert.True(registry.IsRegistered(node));
        })();

        // Force GC
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Node should be gone from registry
        var recoveredNode = registry.GetNode(id);
        Assert.Null(recoveredNode);
    }

    [Fact]
    public void Clear_Resets_Registry()
    {
        var registry = new NodeRegistry();
        var node = new Element("div");
        registry.GetId(node);

        registry.Clear();

        Assert.False(registry.IsRegistered(node));
        Assert.Null(registry.GetNode(1));
        
        // Next ID should be 1 again
        Assert.Equal(1, registry.GetId(new Element("p")));
    }
}
