using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core;

public sealed class DomMutationNotificationTests
{
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
