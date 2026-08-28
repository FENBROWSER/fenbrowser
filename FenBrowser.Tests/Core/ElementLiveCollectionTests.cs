using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class ElementLiveCollectionTests
{
    [Fact]
    public void GetElementsByTagNameRemainsLiveAcrossTreeMutations()
    {
        var document = new Document();
        var host = document.CreateElement("div");
        var matches = host.GetElementsByTagName("span");
        var span = document.CreateElement("span");

        Assert.Equal(0, matches.Length);
        host.AppendChild(span);
        Assert.Equal(1, matches.Length);
        Assert.Same(span, matches[0]);
        host.RemoveChild(span);
        Assert.Equal(0, matches.Length);
    }

    [Fact]
    public void GetElementsByClassNameRemainsLiveAcrossAttributeMutations()
    {
        var document = new Document();
        var host = document.CreateElement("div");
        var child = document.CreateElement("p");
        child.SetAttribute("class", "before");
        host.AppendChild(child);
        var beforeMatches = host.GetElementsByClassName("before");
        var afterMatches = host.GetElementsByClassName("after");

        Assert.Equal(1, beforeMatches.Length);
        Assert.Equal(0, afterMatches.Length);
        child.SetAttribute("class", "after");
        child.SetAttribute("id", "match");
        Assert.Equal(0, beforeMatches.Length);
        Assert.Equal(1, afterMatches.Length);
        Assert.Same(child, afterMatches.NamedItem("match"));
    }
}
