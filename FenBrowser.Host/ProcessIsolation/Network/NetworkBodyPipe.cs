using System;
using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Host.ProcessIsolation.Network
{
    internal sealed class NetworkBodyPipe : IAsyncDisposable, IDisposable
    {
        internal const int MaxFrameBytes = 64 * 1024;
        private const int MaxHandshakeBytes = 256;

        private readonly PipeStream _pipe;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly byte[] _writeHeader = new byte[sizeof(int)];
        private int _disposed;

        private NetworkBodyPipe(PipeStream pipe)
        {
            _pipe = pipe ?? throw new ArgumentNullException(nameof(pipe));
        }

        public string PipeName { get; private init; }
        public string AuthenticationToken { get; private init; }

        public static NetworkBodyPipe CreateServer()
        {
            var pipeName = $"fen_network_body_{Environment.ProcessId}_{Guid.NewGuid():N}";
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                1,
                OperatingSystem.IsWindows() ? PipeTransmissionMode.Message : PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            return new NetworkBodyPipe(pipe)
            {
                PipeName = pipeName,
                AuthenticationToken = token
            };
        }

        public static async Task<NetworkBodyPipe> ConnectClientAsync(
            string pipeName,
            string authenticationToken,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new ArgumentException("Body pipe name is required.", nameof(pipeName));
            if (string.IsNullOrWhiteSpace(authenticationToken))
                throw new ArgumentException("Body pipe token is required.", nameof(authenticationToken));

            var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.ConnectAsync(10_000, cancellationToken).ConfigureAwait(false);
                var result = new NetworkBodyPipe(pipe)
                {
                    PipeName = pipeName,
                    AuthenticationToken = authenticationToken
                };
                await result.WriteHandshakeAsync(authenticationToken, cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task WaitForAuthenticatedClientAsync(
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (_pipe is not NamedPipeServerStream server)
                throw new InvalidOperationException("Only a body-pipe server can accept a client.");

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                _lifetimeCts.Token,
                cancellationToken);
            timeoutCts.CancelAfter(timeout);
            await server.WaitForConnectionAsync(timeoutCts.Token).ConfigureAwait(false);

            var header = new byte[sizeof(int)];
            await ReadExactlyAsync(_pipe, header, timeoutCts.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length <= 0 || length > MaxHandshakeBytes)
                throw new InvalidDataException("Network body-pipe handshake length was invalid.");

            var rented = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                await ReadExactlyAsync(_pipe, rented.AsMemory(0, length), timeoutCts.Token).ConfigureAwait(false);
                var supplied = Encoding.ASCII.GetString(rented, 0, length);
                if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(supplied),
                    Encoding.ASCII.GetBytes(AuthenticationToken)))
                {
                    throw new UnauthorizedAccessException("Network body-pipe authentication failed.");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented, clearArray: true);
            }
        }

        public async Task SendContentAsync(HttpContent content, CancellationToken cancellationToken)
        {
            if (content == null)
            {
                await WriteEndAsync(cancellationToken).ConfigureAwait(false);
                return;
            }

            using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await SendStreamAsync(source, cancellationToken).ConfigureAwait(false);
        }

        public async Task SendStreamAsync(Stream source, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(source);
            var rented = ArrayPool<byte>.Shared.Rent(MaxFrameBytes);
            try
            {
                while (true)
                {
                    var read = await source.ReadAsync(
                        rented.AsMemory(0, MaxFrameBytes),
                        cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                        break;

                    await WriteFrameAsync(rented.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }

                await WriteEndAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        public Stream OpenReadStream(long maxBytes, Action completed = null) =>
            new FramedReadStream(_pipe, maxBytes, completed);

        private async Task WriteHandshakeAsync(string token, CancellationToken cancellationToken)
        {
            var bytes = Encoding.ASCII.GetBytes(token);
            if (bytes.Length == 0 || bytes.Length > MaxHandshakeBytes)
                throw new InvalidOperationException("Network body-pipe token length was invalid.");

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                BinaryPrimitives.WriteInt32LittleEndian(_writeHeader, bytes.Length);
                await _pipe.WriteAsync(_writeHeader, cancellationToken).ConfigureAwait(false);
                await _pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private async Task WriteFrameAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            if (bytes.Length <= 0 || bytes.Length > MaxFrameBytes)
                throw new ArgumentOutOfRangeException(nameof(bytes));

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                BinaryPrimitives.WriteInt32LittleEndian(_writeHeader, bytes.Length);
                await _pipe.WriteAsync(_writeHeader, cancellationToken).ConfigureAwait(false);
                await _pipe.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private async Task WriteEndAsync(CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                Array.Clear(_writeHeader);
                await _pipe.WriteAsync(_writeHeader, cancellationToken).ConfigureAwait(false);
                await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private static async Task ReadExactlyAsync(
            Stream stream,
            Memory<byte> destination,
            CancellationToken cancellationToken)
        {
            var offset = 0;
            while (offset < destination.Length)
            {
                var read = await stream.ReadAsync(destination[offset..], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Network body pipe closed before the frame completed.");
                offset += read;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _lifetimeCts.Cancel();
            _pipe.Dispose();
            _writeGate.Dispose();
            _lifetimeCts.Dispose();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _lifetimeCts.Cancel();
            await _pipe.DisposeAsync().ConfigureAwait(false);
            _writeGate.Dispose();
            _lifetimeCts.Dispose();
        }

        private sealed class FramedReadStream : Stream
        {
            private readonly Stream _source;
            private readonly long _maxBytes;
            private readonly Action _completed;
            private readonly byte[] _header = new byte[sizeof(int)];
            private int _frameRemaining;
            private long _bytesRead;
            private bool _eof;
            private int _disposed;

            public FramedReadStream(Stream source, long maxBytes, Action completed)
            {
                _source = source ?? throw new ArgumentNullException(nameof(source));
                _maxBytes = maxBytes > 0 ? maxBytes : long.MaxValue;
                _completed = completed;
            }

            public override bool CanRead => Volatile.Read(ref _disposed) == 0;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                if (buffer.Length == 0 || _eof)
                    return 0;

                if (_frameRemaining == 0)
                {
                    await NetworkBodyPipe.ReadExactlyAsync(_source, _header, cancellationToken).ConfigureAwait(false);
                    _frameRemaining = BinaryPrimitives.ReadInt32LittleEndian(_header);
                    if (_frameRemaining == 0)
                    {
                        _eof = true;
                        _completed?.Invoke();
                        return 0;
                    }
                    if (_frameRemaining < 0 || _frameRemaining > MaxFrameBytes)
                        throw new InvalidDataException("Network body frame length was invalid.");
                    if (_bytesRead > _maxBytes - _frameRemaining)
                        throw new InvalidDataException("Network response exceeded its destination limit.");
                }

                var requested = Math.Min(buffer.Length, _frameRemaining);
                var read = await _source.ReadAsync(buffer[..requested], cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Network body pipe closed during a frame.");
                _frameRemaining -= read;
                _bytesRead += read;
                return read;
            }

            public override Task<int> ReadAsync(
                byte[] buffer,
                int offset,
                int count,
                CancellationToken cancellationToken) =>
                ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

            public override int Read(byte[] buffer, int offset, int count) =>
                throw new NotSupportedException("Network response bodies are async-only.");
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
                    _completed?.Invoke();
                base.Dispose(disposing);
            }
        }
    }
}
