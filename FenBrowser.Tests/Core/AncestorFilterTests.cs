using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Dom.V2.Selectors;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class AncestorFilterTests
{
    [Fact]
    public void AppendPropagatesParentFeatures()
    {
        var grandparent = new Element("main");
        grandparent.SetAttribute("id", "root");
        var parent = new Element("div");
        parent.SetAttribute("class", "card active");
        var child = new Element("span");

        grandparent.AppendChild(parent);
        parent.AppendChild(child);

        Assert.Equal(grandparent.ComputeFeatureHash(), parent.AncestorFilter);
        Assert.Equal(parent.AncestorFilter | parent.ComputeFeatureHash(), child.AncestorFilter);
        Assert.True(SelectorEngine.Matches(child, "#root .card span"));
        Assert.False(SelectorEngine.Matches(child, "#other .card span"));
    }

    [Fact]
    public void MovingElementRefreshesDescendantFilter()
    {
        var firstRoot = new Element("section");
        firstRoot.SetAttribute("id", "first");
        var secondRoot = new Element("section");
        secondRoot.SetAttribute("id", "second");
        var parent = new Element("div");
        var child = new Element("span");
        parent.AppendChild(child);

        firstRoot.AppendChild(parent);
        var firstFilter = child.AncestorFilter;
        secondRoot.AppendChild(parent);

        Assert.NotEqual(firstFilter, child.AncestorFilter);
        Assert.True(SelectorEngine.Matches(child, "#second span"));
        Assert.False(SelectorEngine.Matches(child, "#first span"));
    }
}
