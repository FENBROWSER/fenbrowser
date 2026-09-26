using System;
using System.IO;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Typography;
using Xunit;

namespace FenBrowser.Tests.Network
{
    // CSS Fonts 4 §5.1: a family named by @font-face rules matches those faces.
    // Layout's font service resolved family lists against system fonts only, so
    // text in a web font was measured in its fallback while paint drew the web
    // font, and paint then shifted runs to "fit": w3schools' "Result Size:" label
    // overlapped the number next to it. A family that resolved to its fallback
    // before the file arrived must also resolve again once it has loaded.
    [Collection("FontRegistry")]
    public sealed class WebFontLayoutMeasurementTests : IDisposable
    {
        private readonly byte[] _ahem = File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "test_assets", "wpt", "fonts", "Ahem.ttf"));

        public WebFontLayoutMeasurementTests() => FontRegistry.Clear();

        public void Dispose() => FontRegistry.Clear();

        [Fact]
        public async Task LayoutMeasuresInTheWebFontOnceItLoads()
        {
            var service = new SkiaFontService();
            const string family = "'WebAhemMeasure', sans-serif";

            float beforeLoad = service.MeasureTextWidth("XXXXXXXXXX", family, 20f);
            Assert.NotEqual(200f, beforeLoad, 1);

            FontRegistry.RegisterFontFace(new FontRegistry.FontFaceDescriptor
            {
                Family = "WebAhemMeasure",
                Source = "url(https://fonts.example/ahem.ttf) format('truetype')",
                Weight = 400,
                Style = SkiaSharp.SKFontStyleSlant.Upright,
                BaseUri = new Uri("https://page.example/"),
                RequestContext = new FontRegistry.FontLoaderRequestContext
                {
                    OwnerId = "test",
                    FetchDetailedAsync = uri => Task.FromResult(
                        new BinaryFetchResult { Body = _ahem, StatusCode = 200, FinalUri = uri })
                }
            });
            await FontRegistry.LoadPendingFontsAsync();

            // Ahem glyphs are exactly 1em wide.
            Assert.Equal(200f, service.MeasureTextWidth("XXXXXXXXXX", family, 20f), 1);
        }
    }
}
