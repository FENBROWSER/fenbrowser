using System;
using System.Linq;
using System.Threading;
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

    [Fact]
    public async Task FailedFontLoad_IsCoalescedAndMemoizedForDocument()
    {
        var fetchCount = 0;
        var releaseFetch = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var context = new FontRegistry.FontLoaderRequestContext
        {
            OwnerId = "font-coalescing-test",
            FetchDetailedAsync = async uri =>
            {
                Interlocked.Increment(ref fetchCount);
                await releaseFetch.Task;
                return new BinaryFetchResult
                {
                    FinalUri = uri,
                    StatusCode = 404,
                    FailureReason = BinaryFetchFailureReason.HttpError
                };
            }
        };

        FontRegistry.Clear();
        try
        {
            var registrations = Enumerable.Range(0, 16)
                .Select(_ => Task.Run(() =>
                {
                    using (FontRegistry.EnterRequestContext(context))
                    {
                        FontRegistry.ParseAndRegister(
                            "font-family: 'MissingFont'; src: url('https://cdn.example.test/missing.woff2') format('woff2');");
                    }
                }))
                .ToArray();

            await Task.WhenAll(registrations);
            releaseFetch.TrySetResult(true);
            await FontRegistry.LoadPendingFontsAsync();

            using (FontRegistry.EnterRequestContext(context))
            {
                FontRegistry.ParseAndRegister(
                    "font-family: 'MissingFont'; src: url('https://cdn.example.test/missing.woff2') format('woff2');");
            }
            await FontRegistry.LoadPendingFontsAsync();

            Assert.Equal(1, Volatile.Read(ref fetchCount));
            Assert.Null(FontRegistry.TryResolve("MissingFont"));
        }
        finally
        {
            releaseFetch.TrySetResult(true);
            FontRegistry.Clear();
        }
    }
}
