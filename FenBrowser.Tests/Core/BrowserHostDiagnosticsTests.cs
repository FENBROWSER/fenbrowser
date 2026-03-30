using System;
using System.Linq;
using System.Reflection;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
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
    }
}
