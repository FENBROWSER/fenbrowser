using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Host.ProcessIsolation
{
    public readonly record struct TimedReadLineResult(bool Completed, string? Line)
    {
        public bool TimedOut => !Completed;
        public bool EndOfStream => Completed && Line == null;
    }

    public static class RendererChildLoopIo
    {
        public static async Task<TimedReadLineResult> ReadLineWithTimeoutAsync(
            TextReader reader,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(reader);
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            try
            {
                var lineTask = reader.ReadLineAsync(cancellationToken).AsTask();
                var line = timeout == Timeout.InfiniteTimeSpan
                    ? await lineTask.ConfigureAwait(false)
                    : await lineTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                return new TimedReadLineResult(true, line);
            }
            catch (TimeoutException)
            {
                return new TimedReadLineResult(false, null);
            }
        }
    }
}
