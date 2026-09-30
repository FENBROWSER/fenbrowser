using System.IO;
using System.IO.Pipelines;
using System.Text;
using FenBrowser.Host.ProcessIsolation;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// The renderer child's message loop awaits input dispatch, and a click on a link or a
/// submit button awaits the navigation, whose fetch completes only when the broker's
/// reply is read. The inbox reads the pipe on its own task and handles those replies
/// there, so they never wait behind the click that needs them.
/// </summary>
public sealed class RendererChildInboxTests
{
    [Fact]
    public async Task RoutedReply_IsHandledWhileTheLoopReadsNothing()
    {
        var pipe = new Pipe();
        using var reader = new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8);
        var handled = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var inbox = new RendererChildInbox(reader, line =>
        {
            if (!line.StartsWith("reply:", StringComparison.Ordinal)) return false;
            handled.TrySetResult(line);
            return true;
        });

        // The loop is "stuck" in a click: nobody calls ReadLineWithTimeoutAsync.
        await WriteLineAsync(pipe, "reply:body-pipe");

        Assert.Equal("reply:body-pipe", await handled.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task UnroutedLines_ReachTheLoopInOrder_AndRoutedOnesDoNot()
    {
        var pipe = new Pipe();
        using var reader = new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8);
        using var inbox = new RendererChildInbox(reader, line => line.StartsWith("reply:", StringComparison.Ordinal));

        await WriteLineAsync(pipe, "input:1");
        await WriteLineAsync(pipe, "reply:x");
        await WriteLineAsync(pipe, "input:2");

        var first = await inbox.ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(5));
        var second = await inbox.ReadLineWithTimeoutAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("input:1", first.Line);
        Assert.Equal("input:2", second.Line);
    }

    [Fact]
    public async Task NothingQueued_TimesOut_AndAClosedPipeIsEndOfStream()
    {
        var pipe = new Pipe();
        using var reader = new StreamReader(pipe.Reader.AsStream(), Encoding.UTF8);
        using var inbox = new RendererChildInbox(reader, _ => false);

        var idle = await inbox.ReadLineWithTimeoutAsync(TimeSpan.FromMilliseconds(30));
        Assert.True(idle.TimedOut);

        await pipe.Writer.CompleteAsync();
        TimedReadLineResult end = default;
        for (var i = 0; i < 50 && !end.Completed; i++)
        {
            end = await inbox.ReadLineWithTimeoutAsync(TimeSpan.FromMilliseconds(100));
        }

        Assert.True(end.EndOfStream);
    }

    private static async Task WriteLineAsync(Pipe pipe, string line)
    {
        await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
        await pipe.Writer.FlushAsync();
    }
}
