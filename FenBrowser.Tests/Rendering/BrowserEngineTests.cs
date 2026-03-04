using System;
using System.IO;
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
        }

        private sealed class StubNetworkService : INetworkService
        {
            private readonly Func<string, string> _getString;

            public StubNetworkService(Func<string, string> getString)
            {
                _getString = getString;
            }

            public Task<Stream> GetStreamAsync(string url)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(_getString(url));
                return Task.FromResult<Stream>(new MemoryStream(bytes));
            }

            public Task<string> GetStringAsync(string url)
            {
                return Task.FromResult(_getString(url));
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
