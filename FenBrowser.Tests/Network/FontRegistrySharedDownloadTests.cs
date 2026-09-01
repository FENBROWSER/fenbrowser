using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Network
{
    // A font file is one resource however many @font-face rules name it. The
    // load cache is keyed by the rule — family, weight, style, unicode-range and
    // the document's base URI — so a subset shared between weights, or a family
    // a second document declares again, used to be downloaded and decoded once
    // per rule. On Google's robot check that was nine Roboto subsets fetched
    // twenty-seven times for the anchor frame and all nine again for the
    // challenge frame.
    [Collection("FontRegistry")]
    public sealed class FontRegistrySharedDownloadTests : IDisposable
    {
        private const string FontUrl = "https://fonts.example/roboto-subset.ttf";

        private readonly byte[] _fontBytes = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test_assets", "wpt", "fonts", "Ahem.ttf"));

        public FontRegistrySharedDownloadTests() => FontRegistry.Clear();

        public void Dispose() => FontRegistry.Clear();

        [Fact]
        public async Task RulesSharingOneFile_DownloadItOnce()
        {
            var fetches = new List<Uri>();
            var context = CreateContext(fetches);

            // The same file under three weights, exactly how a Roboto subset is
            // declared by a stylesheet that serves several weights.
            RegisterFace(context, "Roboto", weight: 400, baseUri: "https://page.example/a");
            RegisterFace(context, "Roboto", weight: 500, baseUri: "https://page.example/a");
            RegisterFace(context, "Roboto", weight: 900, baseUri: "https://page.example/a");

            await FontRegistry.LoadPendingFontsAsync();

            Assert.Equal(1, CountFetches(fetches));
        }

        [Fact]
        public async Task SecondDocumentDeclaringTheSameFont_ReusesTheDownload()
        {
            var fetches = new List<Uri>();
            var context = CreateContext(fetches);

            RegisterFace(context, "Roboto", weight: 400, baseUri: "https://page.example/anchor");
            await FontRegistry.LoadPendingFontsAsync();
            Assert.Equal(1, CountFetches(fetches));

            // A second document — the challenge frame — links the same stylesheet.
            RegisterFace(context, "Roboto", weight: 400, baseUri: "https://page.example/bframe");
            await FontRegistry.LoadPendingFontsAsync();

            Assert.Equal(1, CountFetches(fetches));
        }

        [Fact]
        public async Task DistinctFilesAreStillFetchedSeparately()
        {
            var fetches = new List<Uri>();
            var context = CreateContext(fetches);

            RegisterFace(context, "Roboto", weight: 400, baseUri: "https://page.example/a");
            RegisterFace(
                context,
                "Roboto",
                weight: 400,
                baseUri: "https://page.example/a",
                url: "https://fonts.example/roboto-other-subset.ttf",
                unicodeRange: "U+0400-04FF");

            await FontRegistry.LoadPendingFontsAsync();

            Assert.Equal(2, CountFetches(fetches));
        }

        // Skia cannot decode WOFF2, which is what Google serves. Downloading it
        // again for every rule and every document buys nothing, so the decode
        // failure is remembered; a transport failure still gets another go.
        [Fact]
        public async Task UndecodableFont_IsNotDownloadedAgain()
        {
            var fetches = new List<Uri>();
            var context = new FontRegistry.FontLoaderRequestContext
            {
                OwnerId = "test",
                FetchDetailedAsync = uri =>
                {
                    lock (fetches)
                    {
                        fetches.Add(uri);
                    }

                    // A body Skia will refuse, standing in for WOFF2.
                    return Task.FromResult(new BinaryFetchResult
                    {
                        Body = new byte[] { 0x77, 0x4F, 0x46, 0x32, 0x00, 0x01, 0x02, 0x03 },
                        StatusCode = 200,
                        FinalUri = uri
                    });
                }
            };

            RegisterFace(context, "Roboto", weight: 400, baseUri: "https://page.example/anchor");
            await FontRegistry.LoadPendingFontsAsync();

            RegisterFace(context, "Roboto", weight: 400, baseUri: "https://page.example/bframe");
            RegisterFace(context, "Roboto", weight: 700, baseUri: "https://page.example/bframe");
            await FontRegistry.LoadPendingFontsAsync();

            Assert.Equal(1, CountFetches(fetches));
        }

        [Fact]
        public async Task TransportFailure_IsRetriedByALaterDocument()
        {
            var fetches = new List<Uri>();
            var context = new FontRegistry.FontLoaderRequestContext
            {
                OwnerId = "test",
                FetchDetailedAsync = uri =>
                {
                    lock (fetches)
                    {
                        fetches.Add(uri);
                    }

                    return Task.FromResult(new BinaryFetchResult
                    {
                        FailureReason = BinaryFetchFailureReason.TransportFailure,
                        FinalUri = uri
                    });
                }
            };

            RegisterFace(context, "Roboto", weight: 400, baseUri: "https://page.example/anchor");
            await FontRegistry.LoadPendingFontsAsync();

            RegisterFace(context, "Roboto", weight: 400, baseUri: "https://page.example/bframe");
            await FontRegistry.LoadPendingFontsAsync();

            Assert.Equal(2, CountFetches(fetches));
        }

        private int CountFetches(List<Uri> fetches)
        {
            lock (fetches)
            {
                return fetches.Count;
            }
        }

        private FontRegistry.FontLoaderRequestContext CreateContext(List<Uri> fetches)
        {
            return new FontRegistry.FontLoaderRequestContext
            {
                OwnerId = "test",
                FetchDetailedAsync = async uri =>
                {
                    lock (fetches)
                    {
                        fetches.Add(uri);
                    }

                    // Yield so concurrently registered rules are all in flight
                    // before the first download completes.
                    await Task.Delay(20).ConfigureAwait(false);
                    return new BinaryFetchResult { Body = _fontBytes, StatusCode = 200, FinalUri = uri };
                }
            };
        }

        private static void RegisterFace(
            FontRegistry.FontLoaderRequestContext context,
            string family,
            int weight,
            string baseUri,
            string url = FontUrl,
            string unicodeRange = "U+0000-00FF")
        {
            FontRegistry.RegisterFontFace(new FontRegistry.FontFaceDescriptor
            {
                Family = family,
                Source = $"url({url}) format('truetype')",
                Weight = weight,
                Style = SkiaSharp.SKFontStyleSlant.Upright,
                UnicodeRange = unicodeRange,
                BaseUri = new Uri(baseUri),
                RequestContext = context
            });
        }
    }
}
