using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Security;

namespace FenBrowser.Core;

public class NetworkService : INetworkService
{
    private readonly HttpClient _httpClient;

    public NetworkService()
    {
        // Use HttpClientFactory for HTTP/2 and Brotli support
        _httpClient = HttpClientFactory.GetSharedClient();
    }

    private void LogResponse(HttpResponseMessage response)
    {
        if (!DebugConfig.LogResourceLoader) return;

        var contentType = response.Content.Headers.ContentType;
        var encoding = contentType?.CharSet ?? "utf-8 (implicit)";
        var mime = contentType?.MediaType ?? "unknown";
        var contentEncoding = string.Join(", ", response.Content.Headers.ContentEncoding);
        var requestUri = response.RequestMessage?.RequestUri;

        EngineLogCompat.Log(
            $"[Loader] {response.RequestMessage?.Method?.Method ?? "?"} {GetSafeUriForLog(requestUri)}",
            LogCategory.Network);
        EngineLogCompat.Log($"[Loader] Status: {(int)response.StatusCode} {response.ReasonPhrase}", LogCategory.Network);
        EngineLogCompat.Log($"[Loader] MIME: {mime}", LogCategory.Network);
        EngineLogCompat.Log($"[Loader] Encoding: {encoding}", LogCategory.Network);

        if (!string.IsNullOrEmpty(contentEncoding))
            EngineLogCompat.Log($"[Loader] Compression: {contentEncoding}", LogCategory.Network);
    }

    public Task<Stream> GetStreamAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Invalid network URL.");
        }

        return GetStreamAsync(uri);
    }

    public Task<string> GetStringAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException("Invalid network URL.");
        }

        return GetStringAsync(uri);
    }

    public async Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri == null)
            throw new ArgumentNullException(nameof(uri));
        if (!uri.IsAbsoluteUri)
            throw new InvalidOperationException("Invalid network URL.");

        var configuration = NetworkConfiguration.Instance;
        configuration.ValidateOrThrow();
        EnforceSecurityPolicy(uri);

        using var logScope = EngineLogCompat.BeginScope(
            component: "NetworkService",
            data: new System.Collections.Generic.Dictionary<string, object>
            {
                ["url"] = GetSafeUriForLog(uri),
                ["mode"] = "stream"
            });

        using var request = CreateRequest(uri, configuration);
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.ResourceTimeoutSeconds));

        if (DebugConfig.LogResourceLoader)
            EngineLogCompat.Log($"[Loader] GET {GetSafeUriForLog(uri)}", LogCategory.Network);

        HttpResponseMessage response = null;
        try
        {
            response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            LogResponse(response);
            var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);

            // Transfer both response and timeout ownership. Disposing the timeout CTS
            // here used to disable ResourceTimeoutSeconds as soon as headers arrived,
            // allowing a stalled response body to live forever.
            return new ResponseOwnedStream(stream, response, timeoutCts);
        }
        catch
        {
            response?.Dispose();
            timeoutCts.Dispose();
            throw;
        }
    }

    public async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri == null)
            throw new ArgumentNullException(nameof(uri));
        if (!uri.IsAbsoluteUri)
            throw new InvalidOperationException("Invalid network URL.");

        var configuration = NetworkConfiguration.Instance;
        configuration.ValidateOrThrow();
        EnforceSecurityPolicy(uri);

        using var logScope = EngineLogCompat.BeginScope(
            component: "NetworkService",
            data: new System.Collections.Generic.Dictionary<string, object>
            {
                ["url"] = GetSafeUriForLog(uri),
                ["mode"] = "string"
            });

        using var request = CreateRequest(uri, configuration);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.DocumentTimeoutSeconds));

        if (DebugConfig.LogResourceLoader)
            EngineLogCompat.Log($"[Loader] GET {GetSafeUriForLog(uri)}", LogCategory.Network);

        // Headers-first completion is required for admission control. Using
        // ResponseContentRead here would let HttpClient buffer the entire response
        // before FenBrowser can enforce MaxTextResourceBytes.
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        LogResponse(response);

        var declaredLength = response.Content.Headers.ContentLength;
        if (declaredLength.HasValue && declaredLength.Value > configuration.MaxTextResourceBytes)
        {
            throw new InvalidDataException(
                $"Text resource exceeds the configured {configuration.MaxTextResourceBytes}-byte admission limit.");
        }

        // LoadIntoBufferAsync enforces the same ceiling for chunked/unknown-length
        // responses. Only after that bounded buffer succeeds do we decode text using
        // HttpContent's charset/BOM handling.
        await response.Content.LoadIntoBufferAsync(
            configuration.MaxTextResourceBytes,
            timeoutCts.Token).ConfigureAwait(false);

        return await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
    }

    private static void EnforceSecurityPolicy(Uri uri)
    {
        var decision = BrowserSecurityPolicy.EvaluateNetworkRequest(uri);
        if (!decision.IsAllowed)
        {
            decision.Log();
            throw new InvalidOperationException(decision.Message);
        }
    }

    private HttpRequestMessage CreateRequest(Uri uri, NetworkConfiguration configuration)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri)
        {
            Version = configuration.GetPreferredHttpVersion(),
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        BrowserSettings.ApplyBrowserRequestHeaders(request);
        request.Headers.TryAddWithoutValidation("Accept-Encoding", configuration.GetAcceptEncodingHeader());
        return request;
    }

    private static string GetSafeUriForLog(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return string.Empty;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return uri.GetLeftPart(UriPartial.Authority);
            }
            catch
            {
                return uri.Scheme + ":";
            }
        }

        return uri.Scheme + ":";
    }

    private sealed class ResponseOwnedStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private readonly CancellationTokenSource _lifetimeTimeout;
        private int _disposed;

        public ResponseOwnedStream(
            Stream inner,
            HttpResponseMessage response,
            CancellationTokenSource lifetimeTimeout)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _response = response ?? throw new ArgumentNullException(nameof(response));
            _lifetimeTimeout = lifetimeTimeout ?? throw new ArgumentNullException(nameof(lifetimeTimeout));
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => _inner.CanWrite;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) =>
            FlushWithLifetimeTimeoutAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            ReadWithLifetimeTimeoutAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            ReadMemoryWithLifetimeTimeoutAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        public override void SetLength(long value) => _inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) =>
            _inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _inner.Write(buffer);
        public override Task WriteAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            WriteWithLifetimeTimeoutAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            WriteMemoryWithLifetimeTimeoutAsync(buffer, cancellationToken);

        private async Task<int> ReadWithLifetimeTimeoutAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            using var linked = CreateOperationCancellation(cancellationToken);
            return await _inner.ReadAsync(buffer, offset, count, linked.Token).ConfigureAwait(false);
        }

        private async ValueTask<int> ReadMemoryWithLifetimeTimeoutAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken)
        {
            using var linked = CreateOperationCancellation(cancellationToken);
            return await _inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
        }

        private async Task FlushWithLifetimeTimeoutAsync(CancellationToken cancellationToken)
        {
            using var linked = CreateOperationCancellation(cancellationToken);
            await _inner.FlushAsync(linked.Token).ConfigureAwait(false);
        }

        private async Task WriteWithLifetimeTimeoutAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            using var linked = CreateOperationCancellation(cancellationToken);
            await _inner.WriteAsync(buffer, offset, count, linked.Token).ConfigureAwait(false);
        }

        private async ValueTask WriteMemoryWithLifetimeTimeoutAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken)
        {
            using var linked = CreateOperationCancellation(cancellationToken);
            await _inner.WriteAsync(buffer, linked.Token).ConfigureAwait(false);
        }

        private CancellationTokenSource CreateOperationCancellation(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
                throw new ObjectDisposedException(nameof(ResponseOwnedStream));

            return cancellationToken.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetimeTimeout.Token)
                : CancellationTokenSource.CreateLinkedTokenSource(_lifetimeTimeout.Token);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    _response.Dispose();
                }
                finally
                {
                    _lifetimeTimeout.Dispose();
                }
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                try
                {
                    await _inner.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        _response.Dispose();
                    }
                    finally
                    {
                        _lifetimeTimeout.Dispose();
                    }
                }
            }

            GC.SuppressFinalize(this);
        }
    }
}
