using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Dom;
using Xunit;

namespace FenBrowser.Tests.Dom
{
    public class MutationObserverTests
    {
        [Fact]
        public async Task ChildList_AddChild_TriggersCallback()
        {
            // Arrange
            var parent = new Element("div");
            var child = new Element("span");
            var records = new List<MutationRecord>();
            var tcs = new TaskCompletionSource<bool>();

            var observer = new MutationObserver((recs, obs) =>
            {
                records.AddRange(recs);
                tcs.TrySetResult(true);
            });

            observer.Observe(parent, new MutationObserverInit { ChildList = true });

            // Act
            parent.AppendChild(child);

            // Wait for async delivery (microtask sim)
            await Task.WhenAny(tcs.Task, Task.Delay(2000));

            // Assert
            Assert.Single(records);
            Assert.Equal("childList", records[0].Type);
            Assert.Same(parent, records[0].Target);
            Assert.Contains(child, records[0].AddedNodes);
        }

        [Fact]
        public async Task ChildList_RemoveChild_TriggersCallback()
        {
            var parent = new Element("div");
            var child = new Element("span");
            parent.AppendChild(child);

            var records = new List<MutationRecord>();
            var tcs = new TaskCompletionSource<bool>();

            var observer = new MutationObserver((recs, obs) =>
            {
                records.AddRange(recs);
                tcs.TrySetResult(true);
            });

            observer.Observe(parent, new MutationObserverInit { ChildList = true });

            // Clear any initial records from setup
            observer.TakeRecords();

            child.Remove();

            await Task.WhenAny(tcs.Task, Task.Delay(2000));

            Assert.True(records.Count > 0, "Should have at least one removal record");
            Assert.Equal("childList", records[0].Type);
            Assert.Contains(child, records[0].RemovedNodes);
        }

        [Fact]
        public async Task Attributes_SetAttribute_TriggersCallback()
        {
            var elem = new Element("div");
            var records = new List<MutationRecord>();
            var tcs = new TaskCompletionSource<bool>();

            var observer = new MutationObserver((recs, obs) =>
            {
                records.AddRange(recs);
                tcs.TrySetResult(true);
            });

            observer.Observe(elem, new MutationObserverInit { Attributes = true });

            elem.SetAttribute("data-test", "value");

            await Task.WhenAny(tcs.Task, Task.Delay(2000));

            Assert.Single(records);
            Assert.Equal("attributes", records[0].Type);
            Assert.Equal("data-test", records[0].AttributeName);
        }

        [Fact]
        public async Task Subtree_ChildMutation_TriggersAncestorObserver()
        {
            var grandparent = new Element("div");
            var parent = new Element("div");
            var child = new Element("span");
            grandparent.AppendChild(parent);
            parent.AppendChild(child);

            var records = new List<MutationRecord>();
            var tcs = new TaskCompletionSource<bool>();

            var observer = new MutationObserver((recs, obs) =>
            {
                records.AddRange(recs);
                tcs.TrySetResult(true);
            });

            // Observe grandparent with subtree
            observer.Observe(grandparent, new MutationObserverInit { Attributes = true, Subtree = true });

            // Mutate the deep child
            child.SetAttribute("class", "test");

            await Task.WhenAny(tcs.Task, Task.Delay(2000));

            Assert.True(records.Count > 0, "Should observe attribute change on descendant");
            Assert.Equal("attributes", records[0].Type);
            Assert.Same(child, records[0].Target);
        }

        [Fact]
        public void Disconnect_StopsObservation()
        {
            var elem = new Element("div");
            var records = new List<MutationRecord>();

            var observer = new MutationObserver((recs, obs) =>
            {
                records.AddRange(recs);
            });

            observer.Observe(elem, new MutationObserverInit { Attributes = true });
            observer.Disconnect();

            elem.SetAttribute("id", "test");

            // Give microtask sim a chance (shouldn't fire)
            Thread.Sleep(100);

            Assert.Empty(records);
        }

        [Fact]
        public void TakeRecords_ReturnsAndClearsQueue()
        {
            var elem = new Element("div");
            var observer = new MutationObserver((recs, obs) => { });
            observer.Observe(elem, new MutationObserverInit { Attributes = true });

            elem.SetAttribute("a", "1");
            elem.SetAttribute("b", "2");

            var taken = observer.TakeRecords();

            Assert.Equal(2, taken.Count);
            Assert.Empty(observer.TakeRecords()); // Should be empty now
        }
    }
}
