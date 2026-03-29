using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Rendering
{
    public class BrowserEngineTests
    {
        [Fact]
        public async Task LoadAsync_ExtractsTitleFromHtml()
        {
            var network = new StubNetworkService(_ => "<html><head><title>Fen Browser</title></head><body></body></html>");
            var engine = new BrowserEngine(network, new NullLogger());

            await engine.LoadAsync("https://example.com/page");

            Assert.Equal("Fen Browser", engine.Title);
            Assert.Equal("https://example.com/page", engine.Url);
            Assert.Equal(BrowserEngineLoadState.Complete, engine.LoadState);
            Assert.False(engine.IsLoading);
        }

        [Fact]
        public async Task LoadAsync_DecodesAndNormalizesTitleWhitespace()
        {
            var network = new StubNetworkService(_ => "<title>  Fen &amp; Browser\n\tEngine  </title>");
            var engine = new BrowserEngine(network, new NullLogger());

            await engine.LoadAsync("https://example.com");

            Assert.Equal("Fen & Browser Engine", engine.Title);
        }

        [Fact]
        public async Task LoadAsync_UsesHostFallbackWhenTitleMissing()
        {
            var network = new StubNetworkService(_ => "<html><body>No head/title</body></html>");
            var engine = new BrowserEngine(network, new NullLogger());

            await engine.LoadAsync("https://docs.example.com/path");

            Assert.Equal("docs.example.com", engine.Title);
        }

        [Fact]
        public async Task LoadAsync_UsesErrorTitleWhenNetworkThrows()
        {
            var network = new StubNetworkService(_ => throw new InvalidOperationException("network down"));
            var engine = new BrowserEngine(network, new NullLogger());

            await engine.LoadAsync("https://example.com/error");

            Assert.Equal("Error loading page", engine.Title);
            Assert.Equal(BrowserEngineLoadState.Failed, engine.LoadState);
            Assert.Equal("network down", engine.LastError);
        }

        [Fact]
        public async Task LoadAsync_TracksCancelledState()
        {
            var network = new StubNetworkService((_, token) => Task.FromCanceled<string>(token));
            var engine = new BrowserEngine(network, new NullLogger());
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                engine.LoadAsync(new Uri("https://example.com/cancel"), cts.Token));

            Assert.Equal(BrowserEngineLoadState.Cancelled, engine.LoadState);
            Assert.Equal("Load cancelled", engine.Title);
            Assert.False(string.IsNullOrWhiteSpace(engine.LastError));
        }

        [Fact]
        public async Task LoadAsync_RejectsRelativeUrls()
        {
            var network = new StubNetworkService(_ => "<title>ignored</title>");
            var engine = new BrowserEngine(network, new NullLogger());

            await Assert.ThrowsAsync<ArgumentException>(() => engine.LoadAsync("/relative/path"));
        }

        private sealed class StubNetworkService : INetworkService
        {
            private readonly Func<Uri, CancellationToken, Task<string>> _getStringAsync;

            public StubNetworkService(Func<string, string> getString)
                : this((uri, _) => Task.FromResult(getString(uri.AbsoluteUri)))
            {
            }

            public StubNetworkService(Func<Uri, CancellationToken, Task<string>> getStringAsync)
            {
                _getStringAsync = getStringAsync;
            }

            public Task<Stream> GetStreamAsync(string url)
            {
                return GetStreamAsync(new Uri(url), CancellationToken.None);
            }

            public Task<string> GetStringAsync(string url)
            {
                return GetStringAsync(new Uri(url), CancellationToken.None);
            }

            public async Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken = default)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(await _getStringAsync(uri, cancellationToken));
                return new MemoryStream(bytes);
            }

            public Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default)
            {
                return _getStringAsync(uri, cancellationToken);
            }
        }

        private sealed class NullLogger : ILogger
        {
            public void Log(LogLevel level, string message)
            {
            }

            public void LogError(string message, Exception ex)
            {
            }
        }
    }
}
