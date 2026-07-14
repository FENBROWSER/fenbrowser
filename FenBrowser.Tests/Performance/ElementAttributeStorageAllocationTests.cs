using System;
using FenBrowser.Core.Dom.V2;
using Xunit;
using Xunit.Abstractions;

namespace FenBrowser.Tests.Performance;

public sealed class ElementAttributeStorageAllocationTests
{
    private readonly ITestOutputHelper _output;

    public ElementAttributeStorageAllocationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Constructor_DefersEmptyAttributeMap()
    {
        const int count = 10_000;
        var elements = new Element[count];
        GC.KeepAlive(new Element("div"));

        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < elements.Length; index++)
        {
            elements[index] = new Element("div");
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        _output.WriteLine($"{count:N0} attribute-free elements allocated {allocated:N0} B.");
        GC.KeepAlive(elements);
        Assert.InRange(allocated, 1, 3_300_000);
    }

    [Fact]
    public void Attributes_RemainsSameObjectAndLiveAfterLazyCreation()
    {
        var element = new Element("div");
        Assert.Null(element.GetAttribute("data-value"));
        Assert.False(element.HasAttribute("data-value"));
        Assert.False(element.HasAttributes());

        var attributes = element.Attributes;
        Assert.Same(attributes, element.Attributes);
        Assert.Equal(0, attributes.Length);

        element.SetAttribute("data-value", "one");
        Assert.Same(attributes, element.Attributes);
        Assert.Equal(1, attributes.Length);
        Assert.Equal("one", element.GetAttribute("data-value"));

        element.RemoveAttribute("data-value");
        Assert.Same(attributes, element.Attributes);
        Assert.Equal(0, attributes.Length);
    }

    [Fact]
    public void EmptyAndAttributedElementsKeepCloneEqualityAndSerializationSemantics()
    {
        var empty = new Element("div");
        var emptyClone = Assert.IsType<Element>(empty.CloneNode());
        Assert.True(empty.IsEqualNode(emptyClone));
        Assert.Equal("<div></div>", empty.OuterHTML);

        var attributed = new Element("div");
        attributed.SetAttribute("id", "probe");
        var attributedClone = Assert.IsType<Element>(attributed.CloneNode());
        Assert.True(attributed.IsEqualNode(attributedClone));
        Assert.Contains("id=\"probe\"", attributed.OuterHTML, StringComparison.Ordinal);
    }
}
