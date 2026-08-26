using System;
using FenBrowser.Core.Accessibility;
using FenBrowser.Core.Dom.V2;
using Xunit;

namespace FenBrowser.Tests.Core.Accessibility
{
    /// <summary>
    /// Regression tests for CORE-INF-001: AccessibilityTree must own its static
    /// Node.OnMutation subscription deterministically â€” invalidating while active,
    /// and stopping all notification after disposal.
    /// </summary>
    public sealed class AccessibilityTreeLifecycleTests
    {
        private static Document CreateDocumentWithDiv()
        {
            var doc = Document.CreateHtmlDocument();
            var div = doc.CreateElement("div");
            doc.DocumentElement.AppendChild(div);
            return doc;
        }

        private static void Mutate(Document doc)
        {
            var div = (Element)doc.DocumentElement.FirstChild;
            div.SetAttribute("data-probe", Guid.NewGuid().ToString("N"));
        }

        [Fact]
        public void DomMutations_InvalidateActiveTree()
        {
            var doc = CreateDocumentWithDiv();
            var tree = AccessibilityTree.For(doc);

            int invalidated = 0;
            tree.TreeInvalidated += () => invalidated++;

            Mutate(doc);
            Assert.True(invalidated >= 1, "Expected at least one invalidation from a DOM mutation.");
        }

        [Fact]
        public void DisposedTree_IsNoLongerNotifiedOfMutations()
        {
            var doc = CreateDocumentWithDiv();
            var tree = AccessibilityTree.For(doc);

            _ = tree.Root;

            int invalidations = 0;
            tree.TreeInvalidated += () => invalidations++;

            Mutate(doc);
            Assert.True(invalidations >= 1, "Active tree must be invalidated by DOM mutations.");

            tree.Dispose();
            Mutate(doc);
            Mutate(doc);

            Assert.Equal(1, invalidations);
        }

        [Fact]
        public void DisposedTree_DetachesFromStaticMutationEvent()
        {
            var doc = CreateDocumentWithDiv();
            var tree = AccessibilityTree.For(doc);

            int before = GetOnMutationHandlerCount();
            tree.Dispose();
            int after = GetOnMutationHandlerCount();

            Assert.Equal(before - 1, after);
        }

        private static int GetOnMutationHandlerCount()
        {
            var field = typeof(Node).GetField("OnMutation",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            var handler = (Delegate)field?.GetValue(null);
            return handler?.GetInvocationList().Length ?? 0;
        }    }
}