using System;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.WebDriver
{
    /// <summary>
    /// Serializes commands handled by a WebDriver remote end.
    /// </summary>
    internal sealed class WebDriverCommandQueue : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _disposed;

        public async Task<T> ExecuteAsync<T>(Func<Task<T>> command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            ObjectDisposedException.ThrowIf(_disposed, this);

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await command().ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Compatibility entry point retained for existing server call sites. Queue
        /// admission is asynchronous so waiting commands do not pin request worker
        /// or thread-pool threads while another command owns the gate.
        /// </summary>
        public Task<T> ExecuteWithSynchronousAdmissionAsync<T>(
            Func<Task<T>> command,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(command, cancellationToken);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _gate.Dispose();
        }
    }
}
