using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.WebAPIs;
using NUnit.Framework;

namespace FenBrowser.FenEngine.Tests.Phase4
{
    /// <summary>
    /// Test suite for Phase 4 DOM4 compliance: Async MutationObserver
    /// </summary>
    [TestFixture]
    public class MutationObserverAsyncTests
    {
        private TestExecutionContext _context;
        private Element _testElement;
        private List<MutationRecord> _receivedRecords;
        
        [SetUp]
        public void Setup()
        {
            _context = new TestExecutionContext();
            _testElement = new Element("div");
            _receivedRecords = new List<MutationRecord>();
        }
        
        [Test]
        public async Task MutationObserver_QueuesAsync_Microtask()
        {
            bool callbackInvoked = false;
            var callback = new FenFunction("testCallback", (args, thisVal) =>
            {
                callbackInvoked = true;
                Assert.That(args.Length, Is.GreaterThan(0));
                return FenValue.Undefined;
            });
            
            var observer = new MutationObserverWrapper(callback, _context);
            
            // Act: Record a mutation
            observer.RecordMutation(_testElement, "attributes", "class", "old-class");
            
            // Assert: Callback should NOT be invoked immediately (synchronous)
            Assert.That(callbackInvoked, Is.False, "MutationObserver callback should be queued as microtask, not sync");
            
            // Act: Wait for microtask to process
            await Task.Delay(50);
            
            // Assert: Now callback should have been invoked
            Assert.That(callbackInvoked, Is.True, "MutationObserver callback should be invoked asynchronously via microtask");
        }
        
        [Test]
        public void MutationObserver_Observe_CapturesMutations()
        {
            var callbackInvokedTimes = 0;
            var callback = new FenFunction("testCallback", (args, thisVal) =>
            {
                callbackInvokedTimes++;
                return FenValue.Undefined;
            });
            
            var observer = new MutationObserverWrapper(callback, _context);
            var options = new MutationObserverOptions 
            { 
                Attributes = true, 
                ChildList = true, 
                CharacterData = false 
            };
            
            observer.Observe(_testElement, options);
            
            // Act: Trigger different types of mutations
            observer.RecordMutation(_testElement, "attributes", "id", "old-id");
            observer.RecordMutation(_testElement, "childList");
            observer.RecordMutation(_testElement, "characterData"); // Should be ignored
            
            var records = observer.TakeRecords(_context);
            
            // Assert: Should have captured 2 mutations (attributes and childList)
            Assert.That(records.Length, Is.EqualTo(2));
            Assert.That(callbackInvokedTimes, Is.EqualTo(0), "Callback should not be invoked synchronously");
        }
        
        [Test]
        public void MutationObserver_Subtree_Option_Works()
        {
            var callback = new FenFunction("testCallback", (args, thisVal) => FenValue.Undefined);
            var observer = new MutationObserverWrapper(callback, _context);
            
            var parent = new Element("div");
            var child = new Element("span");
            var grandchild = new Element("em");
            
            parent.AppendChild(child);
            child.AppendChild(grandchild);
            
            var options = new MutationObserverOptions { Subtree = true, Attributes = true };
            observer.Observe(parent, options);
            
            // Act: Mutate at different depths
            observer.RecordMutation(grandchild, "attributes", "data", "old-data");
            
            var records = observer.TakeRecords(_context);
            
            // Assert: Should have captured mutation from subtree
            Assert.That(records.Length, Is.EqualTo(1));
            Assert.That(records[0].Target, Is.EqualTo(grandchild));
        }
        
        [Test]
        public void MutationObserver_Disconnect_ClearsPendingRecords()
        {
            var callback = new FenFunction("testCallback", (args, thisVal) => FenValue.Undefined);
            var observer = new MutationObserverWrapper(callback, _context);
            
            observer.RecordMutation(_testElement, "attributes", "class", "old-class");
            Assert.That(observer.HasPendingRecords, Is.True);
            
            // Act: Disconnect
            observer.Disconnect();
            
            // Assert: Records should be cleared
            Assert.That(observer.HasPendingRecords, Is.False);
            var records = observer.TakeRecords(_context);
            Assert.That(records.Length, Is.EqualTo(0));
        }
    }
    
    /// <summary>
    /// Test suite for Phase 4 Worker: Async initialization
    /// </summary>
    [TestFixture]
    public class WorkerAsyncInitTests
    {
        [Test]
        public async Task WorkerRuntime_CreateAsync_DoesNotBlock()
        {
            // Arrange
            var scriptUrl = "https://example.com/worker.js";
            var origin = "https://example.com";
            var scriptContent = "self.postMessage('worker ready');";
            
            // Act: Create worker asynchronously
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var workerTask = WorkerRuntime.CreateAsync(
                scriptUrl,
                origin,
                scriptFetcher: uri => Task.FromResult(scriptContent)
            );
            
            // Assert: Should return quickly without blocking
            Assert.That(stopwatch.ElapsedMilliseconds, Is.LessThan(100), "CreateAsync should not block");
            
            // Wait for worker to be ready
            var worker = await workerTask;
            stopwatch.Stop();
            
            Assert.That(worker, Is.Not.Null);
            Assert.That(stopwatch.ElapsedMilliseconds, Is.GreaterThan(0), "Worker initialization should take some time");
        }
        
        [Test]
        public async Task WorkerRuntime_PostMessage_WithStructuredClone()
        {
            string receivedMessage = null;
            var worker = await WorkerRuntime.CreateAsync(
                "https://example.com/worker.js",
                "https://example.com",
                scriptFetcher: uri => Task.FromResult("")
            );
            
            worker.OnMessage += data =>
            {
                receivedMessage = data?.ToString();
            };
            
            // Act: Post structured clone message
            var testData = new Dictionary<string, object> 
            { 
                ["type"] = "test",
                ["value"] = 42
            };
            
            Assert.DoesNotThrow(() => worker.PostMessage(testData));
            
            // Assert: Message was accepted
            Assert.That(receivedMessage, Is.Null); // Worker hasn't sent anything back yet
        }
        
        [Test]
        public async Task StructuredClone_SupportsComplexObjects()
        {
            // Arrange
            var testData = new Dictionary<string, object>
            {
                ["string"] = "hello",
                ["number"] = 42,
                ["boolean"] = true,
                ["null"] = null,
                ["array"] = new byte[] { 1, 2, 3 },
                ["nested"] = new Dictionary<string, object> { ["deep"] = "value" }
            };
            
            // Act: Structured clone
            var cloned = StructuredClone.Clone(testData);
            
            // Assert: Clone successful and equivalent
            Assert.That(cloned, Is.Not.Null);
            var clonedDict = cloned as Dictionary<string, object>;
            Assert.That(clonedDict["string"], Is.EqualTo("hello"));
            Assert.That(clonedDict["number"], Is.EqualTo(42));
            Assert.That(clonedDict["boolean"], Is.EqualTo(true));
        }
    }
    
    /// <summary>
    /// Test suite for Phase 4 Web APIs: AbortController and ReadableStream
    /// </summary>
    [TestFixture]
    public class Phase4WebApiTests
    {
        [Test]
        public void AbortController_CanAbort()
        {
            var context = new TestExecutionContext();
            var controller = new JsAbortController(context);
            
            // Act: Create abort signal
            var signal = controller.Signal;
            Assert.That(signal.Aborted, Is.False);
            
            // Act: Abort
            controller.Abort();
            
            // Assert: Signal should be aborted
            Assert.That(signal.Aborted, Is.True);
        }
        
        [Test]
        public void AbortController_Signal_ThrowsOnAbort()
        {
            var context = new TestExecutionContext();
            var controller = new JsAbortController(context);
            var signal = controller.Signal;
            
            controller.Abort();
            
            var result = signal.ThrowIfAborted(new FenValue[0], FenValue.Undefined);
            Assert.That(result.IsError, Is.True);
        }
        
        [Test]
        public void ReadableStream_CanRead()
        {
            var context = new TestExecutionContext();
            var stream = JsReadableStream.CreateEmpty(context);
            
            Assert.That(stream.IsLocked, Is.False);
            Assert.That(stream.IsDisturbed, Is.False);
            
            // Act: Get reader
            var readerResult = stream.GetReader(new FenValue[0], FenValue.Undefined);
            
            // Assert: Reader returned
            Assert.That(readerResult.IsObject, Is.True);
            Assert.That(stream.IsDisturbed, Is.True);
            Assert.That(stream.IsLocked, Is.True);
        }
        
        [Test]
        public void ReadableStream_Tee_CreatesTwoBranches()
        {
            var context = new TestExecutionContext();
            var stream = new JsReadableStream(new System.IO.MemoryStream(new byte[] { 1, 2, 3 }), context);
            
            // Act: Tee the stream
            var teeResult = stream.Tee(new FenValue[0], FenValue.Undefined);
            
            // Assert: Should return two streams
            Assert.That(teeResult.IsObject, Is.True);
            var resultObj = teeResult.AsObject();
            Assert.That(resultObj.Get("0").AsObject(), Is.TypeOf<JsReadableStream>());
            Assert.That(resultObj.Get("1").AsObject(), Is.TypeOf<JsReadableStream>());
        }
    }
}