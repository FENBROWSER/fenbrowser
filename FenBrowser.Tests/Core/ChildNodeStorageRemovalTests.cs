using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class ChildNodeStorageRemovalTests
{
    [Fact]
    public void RepeatedFrontRemovalPreservesOrderAndSiblingLinksAcrossOverflowCompaction()
    {
        var parent = new Element("div");
        var expected = new List<Node>();

        for (var cycle = 0; cycle < 4; cycle++)
        {
            for (var index = 0; index < 160; index++)
            {
                var child = new Element("span");
                child.SetAttribute("data-key", $"{cycle}:{index}");
                parent.AppendChild(child);
                expected.Add(child);
            }

            for (var index = 0; index < 140; index++)
            {
                var removed = expected[0];
                expected.RemoveAt(0);
                parent.RemoveChild(removed);
                Assert.Null(removed.ParentNode);
                Assert.Null(removed.PreviousSibling);
                Assert.Null(removed.NextSibling);
            }

            AssertTreeMatches(parent, expected);
        }
    }

    [Fact]
    public void MixedOverflowInsertAndRemoveRepairsOnlyAdjacentLinks()
    {
        var parent = new Element("div");
        var expected = new List<Node>();
        for (var index = 0; index < 32; index++)
        {
            var child = new Element("i");
            parent.AppendChild(child);
            expected.Add(child);
        }

        var insertedAtFront = new Element("b");
        parent.InsertBefore(insertedAtFront, expected[0]);
        expected.Insert(0, insertedAtFront);

        var insertedInOverflow = new Element("em");
        parent.InsertBefore(insertedInOverflow, expected[17]);
        expected.Insert(17, insertedInOverflow);

        foreach (var index in new[] { 4, 19, 7 })
        {
            var removed = expected[index];
            expected.RemoveAt(index);
            parent.RemoveChild(removed);
        }

        AssertTreeMatches(parent, expected);
    }

    private static void AssertTreeMatches(ContainerNode parent, IReadOnlyList<Node> expected)
    {
        Assert.Equal(expected.Count, parent.ChildNodes.Length);
        Assert.Same(expected.Count == 0 ? null : expected[0], parent.FirstChild);
        Assert.Same(expected.Count == 0 ? null : expected[^1], parent.LastChild);

        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Same(expected[index], parent.ChildNodes[index]);
            Assert.Same(index == 0 ? null : expected[index - 1], expected[index].PreviousSibling);
            Assert.Same(index + 1 == expected.Count ? null : expected[index + 1], expected[index].NextSibling);
        }
    }
}
