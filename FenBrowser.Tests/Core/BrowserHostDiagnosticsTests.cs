using System;
using System.Linq;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class BrowserHostDiagnosticsTests
    {
        [Fact]
        public void BuildEngineSourceSnapshot_UsesOwningDocument_WhenActiveNodeIsHtmlElement()
        {
            var document = ParseHtml("<!DOCTYPE html><html><head><title>Fen</title></head><body><main><p>Hello diagnostics</p></main></body></html>");
            var html = document.DocumentElement;

            var snapshot = (string)InvokePrivateStatic(
                typeof(BrowserHost),
                "BuildEngineSourceSnapshot",
                html,
                new Uri("https://example.test/"));

            Assert.Contains("<title>Fen</title>", snapshot);
            Assert.Contains("<body><main><p>Hello diagnostics</p></main></body>", snapshot);
        }

        [Fact]
        public void IsDiagnosticsSnapshotReady_WaitsForMeaningfulBodyCoverage()
        {
            var smallDocument = ParseHtml("<!DOCTYPE html><html><head><title>Fen</title></head><body><div></div></body></html>");
            var smallReady = (bool)InvokePrivateStatic(
                typeof(BrowserHost),
                "IsDiagnosticsSnapshotReady",
                smallDocument.DocumentElement,
                12,
                string.Empty);

            Assert.False(smallReady);

            var bodyText = string.Join(" ", Enumerable.Repeat("capability", 16));
            var textReadyDocument = ParseHtml($"<!DOCTYPE html><html><head></head><body><p>{bodyText}</p></body></html>");
            var textReady = (bool)InvokePrivateStatic(
                typeof(BrowserHost),
                "IsDiagnosticsSnapshotReady",
                textReadyDocument.DocumentElement,
                24,
                bodyText);

            Assert.True(textReady);
        }

        [Fact]
        public void GetTextContent_FallsBackToBodyText_WhenFilteredTraversalProducesNothing()
        {
            using var browser = new BrowserHost();
            var document = ParseHtml("<!DOCTYPE html><html><head><title>Fen</title></head><body><main><p>Visible diagnostics text</p><p>Second line</p></main></body></html>");

            SetPrivateField(browser, "_engine", "_activeDom", document.DocumentElement);

            var text = browser.GetTextContent();

            Assert.Contains("Visible diagnostics text", text);
            Assert.Contains("Second line", text);
        }

        [Fact]
        public void ClearFaviconForNavigation_RaisesNullFaviconUpdate()
        {
            using var browser = new BrowserHost();
            using var bitmap = new SKBitmap(2, 2);
            bitmap.Erase(SKColors.DeepSkyBlue);
            SetPrivateProperty(browser, "Favicon", bitmap);

            SKBitmap raisedIcon = bitmap;
            var eventRaised = false;
            browser.FaviconChanged += (_, icon) =>
            {
                eventRaised = true;
                raisedIcon = icon;
            };

            InvokePrivateInstance(browser, "ClearFaviconForNavigation");

            Assert.True(eventRaised);
            Assert.Null(raisedIcon);
            Assert.Null(browser.Favicon);
        }

        [Fact]
        public void ExtractDocumentTitle_ReturnsParsedTitleText()
        {
            var document = ParseHtml("<!DOCTYPE html><html><head><title> Google </title></head><body></body></html>");

            var title = (string)InvokePrivateStatic(
                typeof(BrowserHost),
                "ExtractDocumentTitle",
                document.DocumentElement);

            Assert.Equal("Google", title);
        }

        [Fact]
        public void DecodeFavicon_DecodesPngBackedIcoContainer()
        {
            var icoBytes = CreatePngBackedIcoBytes();

            using var bitmap = (SKBitmap)InvokePrivateStatic(
                typeof(BrowserHost),
                "DecodeFavicon",
                icoBytes);

            Assert.NotNull(bitmap);
            Assert.Equal(16, bitmap.Width);
            Assert.Equal(16, bitmap.Height);
        }

        private static Document ParseHtml(string html)
        {
            return new HtmlParser(html, new Uri("https://example.test/")).Parse();
        }

        private static object InvokePrivateStatic(Type type, string methodName, params object[] args)
        {
            var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method!.Invoke(null, args);
        }

        private static object InvokePrivateInstance(object owner, string methodName, params object[] args)
        {
            var method = owner.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method);
            return method!.Invoke(owner, args);
        }

        private static void SetPrivateProperty(object owner, string propertyName, object value)
        {
            var property = owner.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.NotNull(property);
            property!.SetValue(owner, value);
        }

        private static void SetPrivateField(object owner, string fieldName, string nestedFieldName, object value)
        {
            var field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);

            var nestedOwner = field!.GetValue(owner);
            Assert.NotNull(nestedOwner);

            var nestedField = nestedOwner!.GetType().GetField(nestedFieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(nestedField);
            nestedField!.SetValue(nestedOwner, value);
        }

        private static byte[] CreatePngBackedIcoBytes()
        {
            using var bitmap = new SKBitmap(16, 16);
            bitmap.Erase(SKColors.DeepSkyBlue);
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            var png = data.ToArray();

            var ico = new byte[22 + png.Length];
            WriteUInt16Le(ico, 0, 0);
            WriteUInt16Le(ico, 2, 1);
            WriteUInt16Le(ico, 4, 1);
            ico[6] = 16;
            ico[7] = 16;
            ico[8] = 0;
            ico[9] = 0;
            WriteUInt16Le(ico, 10, 1);
            WriteUInt16Le(ico, 12, 32);
            WriteUInt32Le(ico, 14, (uint)png.Length);
            WriteUInt32Le(ico, 18, 22);
            Buffer.BlockCopy(png, 0, ico, 22, png.Length);
            return ico;
        }

        private static void WriteUInt16Le(byte[] bytes, int offset, ushort value)
        {
            bytes[offset] = (byte)(value & 0xff);
            bytes[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32Le(byte[] bytes, int offset, uint value)
        {
            bytes[offset] = (byte)(value & 0xff);
            bytes[offset + 1] = (byte)((value >> 8) & 0xff);
            bytes[offset + 2] = (byte)((value >> 16) & 0xff);
            bytes[offset + 3] = (byte)((value >> 24) & 0xff);
        }
    }
}
