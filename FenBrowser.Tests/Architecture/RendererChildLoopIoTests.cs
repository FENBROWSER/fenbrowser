using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Host.ProcessIsolation;
using Xunit;

namespace FenBrowser.Tests.Architecture;

public class RendererChildLoopIoTests
{
    [Fact]
    public async Task ReadLineWithTimeoutAsync_ReturnsLine_WhenReaderCompletesBeforeTimeout()
    {
        using var reader = new StringReader("hello\n");

        var result = await RendererChildLoopIo.ReadLineWithTimeoutAsync(reader, TimeSpan.FromSeconds(1));

        Assert.True(result.Completed);
        Assert.Equal("hello", result.Line);
        Assert.False(result.TimedOut);
        Assert.False(result.EndOfStream);
    }

    [Fact]
    public async Task ReadLineWithTimeoutAsync_ReturnsTimeout_WhenReaderDoesNotCompleteInTime()
    {
        using var reader = new DelayedTextReader();

        var result = await RendererChildLoopIo.ReadLineWithTimeoutAsync(reader, TimeSpan.FromMilliseconds(20));

        Assert.False(result.Completed);
        Assert.True(result.TimedOut);
        Assert.Null(result.Line);
    }

    [Fact]
    public async Task ReadLineWithTimeoutAsync_ReturnsEndOfStream_WhenReaderCompletesWithNull()
    {
        using var reader = new EndOfStreamTextReader();

        var result = await RendererChildLoopIo.ReadLineWithTimeoutAsync(reader, TimeSpan.FromSeconds(1));

        Assert.True(result.Completed);
        Assert.True(result.EndOfStream);
        Assert.Null(result.Line);
    }

    private sealed class DelayedTextReader : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            return "late";
        }
    }

    private sealed class EndOfStreamTextReader : TextReader
    {
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            return ValueTask.FromResult<string?>(null);
        }
    }
}
