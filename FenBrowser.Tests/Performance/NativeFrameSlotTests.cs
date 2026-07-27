using System;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Host;

namespace FenBrowser.Tests.Performance;

public sealed class NativeFrameSlotTests
{
    [Fact]
    public async Task Publish_WaitsForActiveReaderBeforeDisposingRetiredFrame()
    {
        using var slot = new NativeFrameSlot<TestFrame>();
        var first = new TestFrame();
        var second = new TestFrame();
        slot.Publish(first);

        var readerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = Task.Run(() => slot.TryUse(_ =>
        {
            readerEntered.SetResult();
            releaseReader.Task.GetAwaiter().GetResult();
            Assert.False(first.IsDisposed);
        }));

        await readerEntered.Task;
        var publisher = Task.Run(() => slot.Publish(second));
        var prematurePublish = await Task.WhenAny(publisher, Task.Delay(100));

        Assert.NotSame(publisher, prematurePublish);
        Assert.False(first.IsDisposed);

        releaseReader.SetResult();
        Assert.True(await reader);
        await publisher;

        Assert.True(first.IsDisposed);
        Assert.False(second.IsDisposed);
    }

    private sealed class TestFrame : IDisposable
    {
        private int _disposed;

        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
