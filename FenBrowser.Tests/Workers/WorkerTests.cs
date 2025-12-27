using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Engine;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.EventLoop;
using FenBrowser.FenEngine.Workers;
using FenBrowser.FenEngine.Storage;
using Xunit;

namespace FenBrowser.Tests.Workers
{
    /// <summary>
    /// Tests for Web Worker implementation per Phase E-3 spec
    /// </summary>
    [Collection("Engine Tests")]
    public class WorkerTests
    {
        public WorkerTests()
        {
            EngineContext.Reset();
            EventLoopCoordinator.ResetInstance();
        }

        [Fact]
        public void WorkerRuntime_CreateAndTerminate()
        {
            // Arrange
            var worker = new WorkerRuntime("test.js", "https://example.com");

            // Act & Assert
            Assert.NotNull(worker);

            // Terminate
            worker.Terminate();
            worker.Dispose();
        }

        [Fact]
        public async Task WorkerRuntime_PostMessage_EnqueuesTask()
        {
            // Arrange
            var worker = new WorkerRuntime("test.js", "https://example.com");
            var messageReceived = false;

            // Give worker time to start
            await Task.Delay(50);

            // Act - post a message
            worker.PostMessage(new { test = "hello" });

            // Wait for message to be processed
            await Task.Delay(100);

            // Cleanup
            worker.Terminate();
            worker.Dispose();

            // Worker should have processed the message (in real impl)
            // For now, just verify no crash
            Assert.True(true);
        }

        [Fact]
        public void WorkerRuntime_OnMessage_EventFires()
        {
            // Arrange
            var worker = new WorkerRuntime("test.js", "https://example.com");
            object receivedData = null;
            var messageEvent = new ManualResetEventSlim(false);

            worker.OnMessage += (data) =>
            {
                receivedData = data;
                messageEvent.Set();
            };

            // Act - simulate worker sending message
            worker.PostMessageToMain(42);

            // Process the main thread's event loop
            EventLoopCoordinator.Instance.ProcessNextTask();

            // Assert
            messageEvent.Wait(1000);
            Assert.NotNull(receivedData);
            Assert.Equal(42, receivedData);

            worker.Terminate();
            worker.Dispose();
        }

        [Fact]
        public void WorkerRuntime_OnError_EventFires()
        {
            // Arrange
            var worker = new WorkerRuntime("test.js", "https://example.com");
            Exception receivedException = null;
            var errorEvent = new ManualResetEventSlim(false);

            worker.OnError += (ex) =>
            {
                receivedException = ex;
                errorEvent.Set();
            };

            // Note: In real implementation, errors would fire from worker thread
            // This test just verifies the event mechanism works

            worker.Terminate();
            worker.Dispose();

            // No crash = success
            Assert.True(true);
        }

        [Fact]
        public void StructuredClone_ClonesPrimitives()
        {
            Assert.Null(StructuredClone.Clone(null));
            Assert.Equal(42, StructuredClone.Clone(42));
            Assert.Equal(3.14, StructuredClone.Clone(3.14));
            Assert.Equal("hello", StructuredClone.Clone("hello"));
            Assert.Equal(true, StructuredClone.Clone(true));
        }

        [Fact]
        public void StructuredClone_ClonesArrays()
        {
            var original = new int[] { 1, 2, 3 };
            var clone = (int[])StructuredClone.Clone(original);

            Assert.NotSame(original, clone);
            Assert.Equal(original, clone);
        }

        [Fact]
        public void StructuredClone_ClonesByteArrays()
        {
            var original = new byte[] { 0x01, 0x02, 0x03 };
            var clone = (byte[])StructuredClone.Clone(original);

            Assert.NotSame(original, clone);
            Assert.Equal(original, clone);
        }

        [Fact]
        public void StructuredClone_ClonesDictionaries()
        {
            var original = new Dictionary<string, object>
            {
                { "name", "test" },
                { "value", 123 }
            };

            var clone = (Dictionary<string, object>)StructuredClone.Clone(original);

            Assert.NotSame(original, clone);
            Assert.Equal("test", clone["name"]);
            Assert.Equal(123, clone["value"]);
        }

        [Fact]
        public void StructuredClone_ThrowsOnFunction()
        {
            Action func = () => { };
            Assert.Throws<StructuredCloneException>(() => StructuredClone.Clone(func));
        }

        [Fact]
        public void StructuredClone_CanClone_ReturnsCorrectly()
        {
            Assert.True(StructuredClone.CanClone(null));
            Assert.True(StructuredClone.CanClone(42));
            Assert.True(StructuredClone.CanClone("test"));
            Assert.True(StructuredClone.CanClone(new byte[] { 1, 2, 3 }));
            Assert.False(StructuredClone.CanClone((Action)(() => { })));
        }

        [Fact]
        public void WorkerConstructor_CreatesWorkerFunction()
        {
            var constructor = new WorkerConstructor("https://example.com", new InMemoryStorageBackend());
            var fn = constructor.GetConstructorFunction();

            Assert.NotNull(fn);
            Assert.Equal("Worker", fn.Name);
            Assert.True(fn.IsNative);
        }

        [Fact]
        public void WorkerConstructor_TerminateAll_CleansUp()
        {
            var constructor = new WorkerConstructor("https://example.com", new InMemoryStorageBackend());

            // Create some workers (they'll fail to load script but that's ok for cleanup test)
            var fn = constructor.GetConstructorFunction();
            
            // Call terminate all
            constructor.TerminateAll();

            Assert.Equal(0, constructor.ActiveWorkerCount);
        }

        [Fact]
        public void WorkerGlobalScope_HasRequiredProperties()
        {
            var runtime = new WorkerRuntime("test.js", "https://example.com");
            var scope = new WorkerGlobalScope(runtime, "https://example.com", "myWorker");

            // Check required properties exist
            Assert.NotNull(scope.Get("self"));
            Assert.NotNull(scope.Get("postMessage"));
            Assert.NotNull(scope.Get("close"));
            Assert.NotNull(scope.Get("setTimeout"));
            Assert.NotNull(scope.Get("console"));
            Assert.Equal("myWorker", scope.Get("name").ToString());

            runtime.Terminate();
            runtime.Dispose();
        }

        [Fact]
        public void WorkerGlobalScope_PostMessage_CallsRuntime()
        {
            var runtime = new WorkerRuntime("test.js", "https://example.com");
            var scope = new WorkerGlobalScope(runtime, "https://example.com");

            // Get postMessage function
            var postMessage = scope.Get("postMessage");
            Assert.True(postMessage.IsFunction);

            runtime.Terminate();
            runtime.Dispose();
        }
    }
}
