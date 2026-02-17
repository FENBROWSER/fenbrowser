using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
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
            Assert.Equal(MutationRecordType.ChildList, records[0].Type);
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
            Assert.Equal(MutationRecordType.ChildList, records[0].Type);
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
            Assert.Equal(MutationRecordType.Attributes, records[0].Type);
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
            Assert.Equal(MutationRecordType.Attributes, records[0].Type);
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
        public async Task TakeRecords_ReturnsAndClearsQueue()
        {
            var elem = new Element("div");
            var callbackRecords = new List<MutationRecord>();
            var tcs = new TaskCompletionSource<bool>();

            var observer = new MutationObserver((recs, obs) => 
            {
                lock(callbackRecords) 
                {
                    callbackRecords.AddRange(recs);
                }
                tcs.TrySetResult(true);
            });
            observer.Observe(elem, new MutationObserverInit { Attributes = true });

            elem.SetAttribute("a", "1");
            elem.SetAttribute("b", "2");

            // Small yield to allow race but we handle it
            await Task.Delay(10); 

            var taken = observer.TakeRecords();
            
            // Wait for callback to potentially finish if it started
            // But if we took records, callback might receive empty list or not fire if empty.
            // If we took ALL records, callback won't be called (count > 0 check in ProcessPendingObservers).
            // If callback beat us, taken is empty.
            
            // Give a bit more time for callback if it was scheduled
            await Task.Delay(100);

            int total;
            lock(callbackRecords)
            {
                total = taken.Count + callbackRecords.Count;
            }

            Assert.Equal(2, total);
            Assert.Empty(observer.TakeRecords()); // Should be empty now
        }
    }
}
