using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
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
        private static readonly Lock PendingReadsGate = new();
        private static readonly Dictionary<TextReader, Task<string?>> PendingReads = new(ReferenceEqualityComparer.Instance);

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
                var lineTask = GetOrCreatePendingRead(reader, cancellationToken);
                var line = timeout == Timeout.InfiniteTimeSpan
                    ? await lineTask.ConfigureAwait(false)
                    : await lineTask.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
                ClearPendingRead(reader, lineTask);
                return new TimedReadLineResult(true, line);
            }
            catch (TimeoutException)
            {
                return new TimedReadLineResult(false, null);
            }
            catch
            {
                ClearPendingRead(reader, null);
                throw;
            }
        }

        private static Task<string?> GetOrCreatePendingRead(TextReader reader, CancellationToken cancellationToken)
        {
            lock (PendingReadsGate)
            {
                if (PendingReads.TryGetValue(reader, out var pending))
                {
                    return pending;
                }

                var task = reader.ReadLineAsync(cancellationToken).AsTask();
                PendingReads[reader] = task;
                return task;
            }
        }

        private static void ClearPendingRead(TextReader reader, Task<string?>? expectedTask)
        {
            lock (PendingReadsGate)
            {
                if (!PendingReads.TryGetValue(reader, out var pending))
                {
                    return;
                }

                if (expectedTask is not null && !ReferenceEquals(pending, expectedTask))
                {
                    return;
                }

                PendingReads.Remove(reader);
            }
        }
    }
}
