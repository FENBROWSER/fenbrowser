using System;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Core;

[Collection("Synthetic CAPTCHA")]
public sealed class FontRegistryFetchContextTests
{
    [Fact]
    public async Task DocumentFontFetch_UsesOwningDocumentAndPreservesStructuredBlock()
    {
        var document = new HtmlParser(
            "<!doctype html><html><body></body></html>",
            new Uri("https://frame.example.test/widget/")).Parse();
        var genericFetchCount = 0;
        var documentFetchCount = 0;
        var previousFetcher = FontRegistry.FetchDetailedAsync;
        var previousDocumentFetcher = FontRegistry.FetchDetailedForDocumentAsync;
        FontRegistry.Clear();

        try
        {
            var context = new FontRegistry.FontLoaderRequestContext
            {
                OwnerId = "frame-font-context-test",
                FetchDetailedAsync = uri =>
                {
                    genericFetchCount++;
                    return Task.FromResult(new BinaryFetchResult { FinalUri = uri });
                },
                FetchDetailedForDocumentAsync = (uri, ownerDocument) =>
                {
                    documentFetchCount++;
                    Assert.Same(document, ownerDocument);
                    return Task.FromResult(new BinaryFetchResult
                    {
                        FinalUri = uri,
                        FailureReason = BinaryFetchFailureReason.CspBlocked,
                        FailureDetail = "Blocked by font-src",
                        CspAllowed = false
                    });
                }
            };

            using (FontRegistry.EnterRequestContext(context))
            {
                FontRegistry.ParseAndRegister(
                    "font-family: 'FrameFont'; src: url('challenge.woff2') format('woff2');",
                    new Uri("https://frame.example.test/widget/"),
                    document);
            }

            await FontRegistry.LoadPendingFontsAsync();

            Assert.Equal(0, genericFetchCount);
            Assert.Equal(1, documentFetchCount);
            Assert.True(FontRegistry.IsRegistered("FrameFont"));
            Assert.Null(FontRegistry.TryResolve("FrameFont"));
        }
        finally
        {
            FontRegistry.FetchDetailedAsync = previousFetcher;
            FontRegistry.FetchDetailedForDocumentAsync = previousDocumentFetcher;
            FontRegistry.Clear();
        }
    }
}
