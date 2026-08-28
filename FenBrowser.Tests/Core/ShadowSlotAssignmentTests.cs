using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class ShadowSlotAssignmentTests
{
    [Fact]
    public void NamedModeAssignsUnslottedNodesToDefaultSlot()
    {
        var document = new Document();
        var host = document.CreateElement("div");
        var shadow = host.AttachShadow(new ShadowRootInit
        {
            Mode = ShadowRootMode.Open,
            SlotAssignment = SlotAssignmentMode.Named
        });
        var defaultSlot = document.CreateElement("slot");
        var namedSlot = document.CreateElement("slot");
        namedSlot.SetAttribute("name", "named");
        shadow.AppendChild(defaultSlot);
        shadow.AppendChild(namedSlot);
        var text = document.CreateTextNode("text");
        var unslotted = document.CreateElement("p");
        var named = document.CreateElement("span");
        named.SetAttribute("slot", "named");
        var unmatched = document.CreateElement("i");
        unmatched.SetAttribute("slot", "missing");
        host.AppendChild(text);
        host.AppendChild(unslotted);
        host.AppendChild(named);
        host.AppendChild(unmatched);

        Assert.Equal(new Node[] { text, unslotted }, shadow.GetAssignedNodesForSlot(defaultSlot));
        Assert.Equal(new Node[] { named }, shadow.GetAssignedNodesForSlot(namedSlot));
        Assert.Same(defaultSlot, text.AssignedSlot);
        Assert.Same(defaultSlot, unslotted.AssignedSlot);
        Assert.Same(namedSlot, named.AssignedSlot);
        Assert.Null(unmatched.AssignedSlot);
    }

    [Fact]
    public void ManualModeSupportsAssignReassignAndClear()
    {
        var document = new Document();
        var host = document.CreateElement("div");
        var shadow = host.AttachShadow(new ShadowRootInit
        {
            Mode = ShadowRootMode.Open,
            SlotAssignment = SlotAssignmentMode.Manual
        });
        var firstSlot = document.CreateElement("slot");
        var secondSlot = document.CreateElement("slot");
        shadow.AppendChild(firstSlot);
        shadow.AppendChild(secondSlot);
        var first = document.CreateElement("p");
        var second = document.CreateElement("span");
        host.AppendChild(first);
        host.AppendChild(second);

        firstSlot.Assign(first, second);
        Assert.Equal(new Node[] { first, second }, firstSlot.AssignedNodes());
        Assert.Same(firstSlot, second.AssignedSlot);

        secondSlot.Assign(second);
        Assert.Equal(new Node[] { first }, firstSlot.AssignedNodes());
        Assert.Equal(new Node[] { second }, secondSlot.AssignedNodes());
        Assert.Same(secondSlot, second.AssignedSlot);

        firstSlot.Assign();
        Assert.Empty(firstSlot.AssignedNodes());
        Assert.Null(first.AssignedSlot);
    }

    [Fact]
    public void ManualAssignmentPersistsUntilNodesJoinTheHost()
    {
        var document = new Document();
        var host = document.CreateElement("div");
        var shadow = host.AttachShadow(new ShadowRootInit
        {
            Mode = ShadowRootMode.Open,
            SlotAssignment = SlotAssignmentMode.Manual
        });
        var slot = document.CreateElement("slot");
        shadow.AppendChild(slot);
        var detached = document.CreateElement("p");

        slot.Assign(detached);
        Assert.Empty(slot.AssignedNodes());
        Assert.Null(detached.AssignedSlot);

        host.AppendChild(detached);
        Assert.Equal(new Node[] { detached }, slot.AssignedNodes());
        Assert.Same(slot, detached.AssignedSlot);

        host.RemoveChild(detached);
        Assert.Empty(slot.AssignedNodes());
        Assert.Null(detached.AssignedSlot);

        host.AppendChild(detached);
        Assert.Equal(new Node[] { detached }, slot.AssignedNodes());
        Assert.Same(slot, detached.AssignedSlot);
    }

    [Fact]
    public void AssignWorksOnDisconnectedSlotsAndRejectsNonSlottables()
    {
        var document = new Document();
        var slot = document.CreateElement("slot");
        var element = document.CreateElement("p");

        slot.Assign(element, element);
        Assert.Empty(slot.AssignedNodes());

        var error = Assert.Throws<DomException>(() => slot.Assign(document.CreateComment("no")));
        Assert.Equal("TypeError", error.Name);
    }
}
