using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Security;
using System.Linq;

namespace FenBrowser.Core;

public class NetworkService : INetworkService
{
    private readonly HttpClient _httpClient;

    public NetworkService()
    {
        // Use HttpClientFactory for HTTP/2 and Brotli support
        _httpClient = HttpClientFactory.GetSharedClient();
    }

    /// <summary>
    /// Gets the current User-Agent from BrowserSettings
    /// </summary>
    private string GetCurrentUserAgent()
    {
        return BrowserSettings.GetUserAgentString(BrowserSettings.Instance.SelectedUserAgent);
    }

    private void LogResponse(HttpResponseMessage response)
    {
        if (!DebugConfig.LogResourceLoader) return;
        
        var contentType = response.Content.Headers.ContentType;
        var encoding = contentType?.CharSet ?? "utf-8 (implicit)";
        var mime = contentType?.MediaType ?? "unknown";
        var contentEncoding = string.Join(", ", response.Content.Headers.ContentEncoding);
        
        EngineLogCompat.Log($"[Loader] {response.RequestMessage.Method} {response.RequestMessage.RequestUri}", LogCategory.Network);
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
            throw new InvalidOperationException($"Invalid network URL: {url}");
        }

        return GetStreamAsync(uri);
    }

    public Task<string> GetStringAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid network URL: {url}");
        }

        return GetStringAsync(uri);
    }

    public async Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri == null)
            throw new ArgumentNullException(nameof(uri));
        if (!uri.IsAbsoluteUri)
            throw new InvalidOperationException($"Invalid network URL: {uri}");

        var configuration = NetworkConfiguration.Instance;
        configuration.ValidateOrThrow();
        EnforceSecurityPolicy(uri);

        using var logScope = EngineLogCompat.BeginScope(
            component: "NetworkService",
            data: new System.Collections.Generic.Dictionary<string, object>
            {
                ["url"] = uri.AbsoluteUri,
                ["mode"] = "stream"
            });

        using var request = CreateRequest(uri, configuration);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.ResourceTimeoutSeconds));

        if (DebugConfig.LogResourceLoader)
            EngineLogCompat.Log($"[Loader] GET {uri}", LogCategory.Network);

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

            // The content stream is owned by HttpResponseMessage. Returning the raw
            // stream would abandon that owner and retain response/content resources
            // until finalization. Transfer response ownership to the returned stream.
            return new ResponseOwnedStream(stream, response);
        }
        catch
        {
            response?.Dispose();
            throw;
        }
    }

    public async Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        if (uri == null)
            throw new ArgumentNullException(nameof(uri));
        if (!uri.IsAbsoluteUri)
            throw new InvalidOperationException($"Invalid network URL: {uri}");

        var configuration = NetworkConfiguration.Instance;
        configuration.ValidateOrThrow();
        EnforceSecurityPolicy(uri);

        using var logScope = EngineLogCompat.BeginScope(
            component: "NetworkService",
            data: new System.Collections.Generic.Dictionary<string, object>
            {
                ["url"] = uri.AbsoluteUri,
                ["mode"] = "string"
            });

        using var request = CreateRequest(uri, configuration);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(configuration.DocumentTimeoutSeconds));

        if (DebugConfig.LogResourceLoader)
            EngineLogCompat.Log($"[Loader] GET {uri}", LogCategory.Network);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            timeoutCts.Token).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        LogResponse(response);
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

    private sealed class ResponseOwnedStream : Stream
    {
        private readonly Stream _inner;
        private readonly HttpResponseMessage _response;
        private int _disposed;

        public ResponseOwnedStream(Stream inner, HttpResponseMessage response)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _response = response ?? throw new ArgumentNullException(nameof(response));
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
            _inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken) =>
            _inner.ReadAsync(buffer, offset, count, cancellationToken);
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.ReadAsync(buffer, cancellationToken);
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
            _inner.WriteAsync(buffer, offset, count, cancellationToken);
        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _inner.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                // Disposing the response disposes its HttpContent and therefore the
                // owned content stream. Keep one authoritative owner to avoid leaks.
                _response.Dispose();
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
                    _response.Dispose();
                }
            }

            GC.SuppressFinalize(this);
        }
    }
}
