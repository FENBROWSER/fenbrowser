using System;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>
    /// Reads the renderer child's inbound pipe on its own task. Lines the router claims
    /// are handled there and then; every other line queues, in order, for the child's
    /// message loop. The loop awaits input dispatch, and activating a link or a submit
    /// button awaits the navigation, whose fetch goes through the broker and completes
    /// only when the broker's reply is read. Read by that same loop, the reply waited
    /// behind the click that needed it until the fetch timed out: every link click and
    /// form submission in a brokered tab failed with "The broker did not answer the
    /// fetch in time."
    /// </summary>
    public sealed class RendererChildInbox : IDisposable
    {
        private readonly Channel<string?> _lines = Channel.CreateUnbounded<string?>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _pump;
        private Task<bool>? _pendingWait;

        /// <param name="routeImmediately">
        /// Called on the reader task for every line; true when it handled the line, which
        /// then never reaches the message loop. Must not block.
        /// </param>
        public RendererChildInbox(TextReader reader, Func<string, bool> routeImmediately)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentNullException.ThrowIfNull(routeImmediately);
            _pump = Task.Run(() => PumpAsync(reader, routeImmediately));
        }

        /// <summary>
        /// The next queued line for the message loop, with the same contract as
        /// <see cref="RendererChildLoopIo.ReadLineWithTimeoutAsync"/>: not completed on
        /// timeout, a null line at end of stream.
        /// </summary>
        public async Task<TimedReadLineResult> ReadLineWithTimeoutAsync(TimeSpan timeout)
        {
            if (_lines.Reader.TryRead(out var line))
            {
                return new TimedReadLineResult(true, line);
            }

            // One outstanding wait, reused across timeouts, so an idle loop polling every
            // few milliseconds does not pile up abandoned waiters.
            var wait = _pendingWait;
            if (wait == null || wait.IsCompleted)
            {
                wait = _lines.Reader.WaitToReadAsync().AsTask();
                _pendingWait = wait;
            }

            try
            {
                bool available = await wait.WaitAsync(timeout).ConfigureAwait(false);
                _pendingWait = null;
                if (available && _lines.Reader.TryRead(out line))
                {
                    return new TimedReadLineResult(true, line);
                }

                return available
                    ? new TimedReadLineResult(false, null)
                    : new TimedReadLineResult(true, null);
            }
            catch (TimeoutException)
            {
                return new TimedReadLineResult(false, null);
            }
        }

        private async Task PumpAsync(TextReader reader, Func<string, bool> routeImmediately)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                    if (line == null)
                    {
                        break;
                    }

                    bool routed;
                    try
                    {
                        routed = routeImmediately(line);
                    }
                    catch
                    {
                        routed = false;
                    }

                    if (!routed)
                    {
                        _lines.Writer.TryWrite(line);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
                // Broken pipe: the loop sees end of stream, as with a clean close.
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                _lines.Writer.TryWrite(null);
                _lines.Writer.TryComplete();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _stop.Dispose();
        }
    }
}
