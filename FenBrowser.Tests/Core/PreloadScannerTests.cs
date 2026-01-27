using Xunit;
using FenBrowser.Core.Parsing;
using FenBrowser.Core.Network;
using FenBrowser.Core;
using System;
using System.Threading.Tasks;
using System.Linq;

namespace FenBrowser.Tests.Core
{
    public class PreloadScannerTests
    {
        [Fact]
        public async Task Scanner_Finds_Css_And_Scripts()
        {
            var html = @"
                <html>
                <head>
                    <link rel=""stylesheet"" href=""style.css"">
                    <script src=""app.js""></script>
                </head>
                <body>
                    <img src=""logo.png"">
                </body>
                </html>";

            var baseUri = new Uri("http://example.com/");
            var manager = new ResourceManager(new System.Net.Http.HttpClient(), false); // Added args
            using var prefetcher = new ResourcePrefetcher(manager);
            
            var scanner = new PreloadScanner(html, baseUri, prefetcher);
            await scanner.ScanAsync();

            // Allow async queue processing
            await Task.Delay(100);

            var (pending, completed, queued) = prefetcher.GetStats();
            
            // We expect 3 items in the system (pending or queue)
            // style.css, app.js, logo.png
            int total = pending + completed + queued;
            Assert.Equal(3, total);
        }

        [Fact]
        public void HtmlParser_Triggers_Scanner()
        {
            var html = @"<link rel=""stylesheet"" href=""theme.css"">";
            var baseUri = new Uri("http://test.com/");
            var manager = new ResourceManager(new System.Net.Http.HttpClient(), false);
            using var prefetcher = new ResourcePrefetcher(manager);

            var parser = new HtmlParser(html, baseUri, prefetcher);
            var doc = parser.Parse();

            Assert.NotNull(doc);
            
            // Give time for background scan
            System.Threading.Thread.Sleep(100);

            var (pending, completed, queued) = prefetcher.GetStats();
            Assert.True(pending + completed + queued >= 1, "Should have queued theme.css");
        }
    }
}
