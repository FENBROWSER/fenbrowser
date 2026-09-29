using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Svg
{
    /// <summary>
    /// use elements that reference an element in another document (SVG 2 §5.6), the
    /// sprite-sheet pattern. The document is only ever obtained through the authorized
    /// resolver, same-origin; it keeps its own style sheets and paint servers, and the
    /// instance inherits the use element's style.
    /// </summary>
    public sealed class SvgExternalUseTests
    {
        private static readonly Uri Page = new("https://fen.test/page.svg");

        private const string Sprites =
            "<svg xmlns='http://www.w3.org/2000/svg'>" +
            "<style>.accent { fill: lime; }</style>" +
            "<linearGradient id='g'><stop offset='0' stop-color='blue'/><stop offset='1' stop-color='blue'/></linearGradient>" +
            "<symbol id='inherit' viewBox='0 0 10 10'><rect width='10' height='10'/></symbol>" +
            "<symbol id='styled' viewBox='0 0 10 10'><rect class='accent' width='10' height='10'/></symbol>" +
            "<symbol id='painted' viewBox='0 0 10 10'><rect width='10' height='10' fill='url(#g)'/></symbol>" +
            "<symbol id='self' viewBox='0 0 10 10'><use href='sprites.svg#inherit' fill='red'/></symbol>" +
            "</svg>";

        [Theory]
        [InlineData("inherit", 255, 0, 0)]
        [InlineData("styled", 0, 255, 0)]
        [InlineData("painted", 0, 0, 255)]
        [InlineData("self", 255, 0, 0)]
        public void SameOriginSprite_RendersWithItsOwnStylesAndTheUsesInheritance(
            string symbol, byte red, byte green, byte blue)
        {
            using var result = Render($"<use href='sprites.svg#{symbol}' width='20' height='20' fill='red'/>");

            Assert.True(SvgRenderResult.IsAdmissible(result), result.ErrorMessage + " :: " + string.Join(" | ", result.Warnings));
            Assert.Equal(new SKColor(red, green, blue), result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void MissingElement_IsADanglingReferenceThatPaintsNothing()
        {
            using var result = Render(
                "<rect width='20' height='20' fill='lime'/><use href='sprites.svg#absent' width='20' height='20'/>");

            Assert.True(SvgRenderResult.IsAdmissible(result), result.ErrorMessage + " :: " + string.Join(" | ", result.Warnings));
            Assert.Equal(SKColors.Lime, result.Bitmap.GetPixel(10, 10));
        }

        [Fact]
        public void CrossOriginDocument_IsRefusedEvenWithAResolver()
        {
            using var result = Render("<use href='https://other.test/sprites.svg#inherit'/>");

            Assert.False(result.Success);
            Assert.True(result.HadResourceRejection);
        }

        [Fact]
        public void UnresolvedOrMalformedDocuments_AreRefused()
        {
            using var unresolved = Render("<use href='missing.svg#inherit'/>");
            using var malformed = Render("<use href='broken.svg#inherit'/>");

            Assert.True(unresolved.HadResourceRejection);
            Assert.True(malformed.HadResourceRejection);
        }

        [Fact]
        public void ARefusalInsideTheSpriteSheet_RefusesTheRender()
        {
            // The sprite sheet itself references a cross-origin document.
            using var result = Render("<use href='nested.svg#outer' width='20' height='20'/>");

            Assert.False(result.Success);
            Assert.True(result.HadResourceRejection);
        }

        private static SvgRenderResult Render(string body)
        {
            var limits = SvgRenderLimits.Default;
            limits.AllowExternalReferences = true;
            return new FenSvgRenderer().Render(new SvgRenderRequest(
                "<svg xmlns='http://www.w3.org/2000/svg' width='20' height='20'>" + body + "</svg>", limits)
            {
                BaseUri = Page,
                ResourceResolver = new DictionaryResolver(new Dictionary<string, string>
                {
                    ["https://fen.test/sprites.svg"] = Sprites,
                    ["https://fen.test/broken.svg"] = "<svg",
                    ["https://fen.test/nested.svg"] =
                        "<svg xmlns='http://www.w3.org/2000/svg'><symbol id='outer' viewBox='0 0 10 10'>" +
                        "<use href='https://other.test/x.svg#y'/></symbol></svg>"
                })
            });
        }

        private sealed class DictionaryResolver : ISvgResourceResolver
        {
            private readonly Dictionary<string, string> _documents;

            public DictionaryResolver(Dictionary<string, string> documents) => _documents = documents;

            public bool TryResolve(Uri absoluteUri, SvgResourceKind kind, out SvgResolvedResource resource, out string error)
            {
                if (_documents.TryGetValue(absoluteUri.AbsoluteUri, out string? text))
                {
                    resource = new SvgResolvedResource(absoluteUri, "image/svg+xml", Encoding.UTF8.GetBytes(text));
                    error = string.Empty;
                    return true;
                }
                resource = default;
                error = "not found";
                return false;
            }
        }
    }
}
