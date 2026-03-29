using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Threading;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class UiThreadHelperTests
    {
        public UiThreadHelperTests()
        {
            UiThreadHelper.Reset();
        }

        [Fact]
        public async Task RunAsyncAwaitable_UsesConfiguredDispatcher_WhenThreadAccessMissing()
        {
            var adapter = new StubDispatcherAdapter(hasThreadAccess: false);
            UiThreadHelper.Configure(adapter);

            var dispatcher = UiThreadHelper.TryGetDispatcher();
            var ran = false;

            await UiThreadHelper.RunAsyncAwaitable(dispatcher, priority: null, () =>
            {
                ran = true;
                return Task.CompletedTask;
            });

            Assert.True(ran);
            Assert.Equal(1, adapter.InvokeAsyncCallCount);
        }

        [Fact]
        public void RunAsync_PostsToDispatcher_WhenThreadAccessMissing()
        {
            var adapter = new StubDispatcherAdapter(hasThreadAccess: false);
            UiThreadHelper.Configure(adapter);

            var dispatcher = UiThreadHelper.TryGetDispatcher();
            var ran = false;

            UiThreadHelper.RunAsync(dispatcher, priority: null, () => ran = true);

            Assert.True(ran);
            Assert.Equal(1, adapter.PostCallCount);
        }

        [Fact]
        public void HasThreadAccess_ReturnsTrue_WhenNoDispatcherIsConfigured()
        {
            Assert.True(UiThreadHelper.HasThreadAccess(dispatcher: null));
        }

        private sealed class StubDispatcherAdapter : IUiDispatcherAdapter
        {
            private readonly bool _hasThreadAccess;

            public StubDispatcherAdapter(bool hasThreadAccess)
            {
                _hasThreadAccess = hasThreadAccess;
            }

            public object Dispatcher { get; } = new object();
            public int InvokeAsyncCallCount { get; private set; }
            public int PostCallCount { get; private set; }

            public bool HasThreadAccess(object dispatcher) => _hasThreadAccess;

            public async Task InvokeAsync(object dispatcher, object priority, Func<Task> action)
            {
                InvokeAsyncCallCount++;
                await action();
            }

            public void Post(object dispatcher, object priority, Action callback)
            {
                PostCallCount++;
                callback();
            }
        }
    }
}
