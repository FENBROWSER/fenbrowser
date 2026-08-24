using System;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Rendering;
using Xunit;

namespace FenBrowser.Tests.Engine
{
    public class CustomHtmlEngineNoJsNormalizationTests
    {
        private const string SvgDocument = "<?xml version='1.0' encoding='UTF-8'?>" +
            "<svg xmlns='http://www.w3.org/2000/svg' xmlns:h='http://www.w3.org/1999/xhtml' width='800px' height='8000px'>" +
            "<title>conformance</title><metadata><h:link rel='help' href='https://example.test/spec'/>" +
            "<h:meta name='assert' content='cx supports length-percentage'/></metadata>" +
            "<g id='target'/><h:script src='/resources/harness.js'/>" +
            "<script><![CDATA[window.inlineReady = true;]]></script></svg>";

        [Fact]
        public async Task RenderAsync_SvgXmlDocument_CompletesWithoutHtmlWrapper()
        {
            using var engine = new CustomHtmlEngine { EnableJavaScript = false };

            await engine.RenderAsync(
                SvgDocument,
                new Uri("https://example.test/conformance.svg"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: 1200,
                viewportHeight: 800,
                forceJavascript: false,
                documentContentType: "image/svg+xml").WaitAsync(TimeSpan.FromSeconds(5));

            var root = Assert.IsType<Element>(engine.GetActiveDom());
            Assert.Equal("svg", root.LocalName);
            Assert.Equal("image/svg+xml", root.OwnerDocument.ContentType);
            Assert.Equal(2, root.Descendants().OfType<Element>().Count(element =>
                string.Equals(element.LocalName, "script", StringComparison.Ordinal)));
        }

        [Fact]
        public async Task RenderAsync_SvgXmlDocument_DiscoversAndExecutesScripts()
        {
            using var engine = new CustomHtmlEngine { EnableJavaScript = true };
            engine.ScriptFetcher = _ => Task.FromResult("window.externalReady = true;");

            await engine.RenderAsync(
                SvgDocument,
                new Uri("https://example.test/conformance.svg"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: 1200,
                viewportHeight: 800,
                forceJavascript: true,
                documentContentType: "image/svg+xml").WaitAsync(TimeSpan.FromSeconds(5));

            var deadline = DateTime.UtcNow.AddSeconds(5);
            var snapshot = engine.ScriptEngine.GetScriptLoadingSnapshot();
            while (!string.Equals(snapshot.BaseUrl, "https://example.test/conformance.svg", StringComparison.Ordinal) ||
                   !string.Equals(snapshot.Status, "completed", StringComparison.OrdinalIgnoreCase))
            {
                Assert.True(DateTime.UtcNow < deadline, $"script status={snapshot.Status} base={snapshot.BaseUrl}");
                await Task.Delay(25);
                snapshot = engine.ScriptEngine.GetScriptLoadingSnapshot();
            }

            Assert.Equal(2, snapshot.ScriptElements);
            Assert.Equal(2, snapshot.ExecutionCompleted);
            Assert.Equal(0, snapshot.ExecutionFailed);
        }

        [Fact]
        public async Task RenderAsync_HtmlThenSvgXmlDocument_ReplacesRuntimeWithoutStalling()
        {
            using var engine = new CustomHtmlEngine { EnableJavaScript = true };
            engine.ScriptFetcher = _ => Task.FromResult("window.externalReady = true;");

            await engine.RenderAsync(
                "<!doctype html><html><body><script>window.runnerReady=true;</script></body></html>",
                new Uri("https://example.test/testharness_runner.html"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                forceJavascript: true).WaitAsync(TimeSpan.FromSeconds(5));

            await engine.RenderAsync(
                SvgDocument,
                new Uri("https://example.test/conformance.svg"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: 1200,
                viewportHeight: 800,
                forceJavascript: true,
                documentContentType: "image/svg+xml").WaitAsync(TimeSpan.FromSeconds(5));

            var root = Assert.IsType<Element>(engine.GetActiveDom());
            Assert.Equal("svg", root.LocalName);
        }

        [Fact]
        public async Task RenderAsync_WithJavaScriptEnabled_PreservesAuthoredFeatureClasses()
        {
            const string html = @"<!DOCTYPE html>
<html class='no-js'>
<head><title>no-js normalization</title></head>
<body>
  <nav id='globalnav' class='globalnav no-js'></nav>
  <footer class='no-js'></footer>
</body>
</html>";

            using var engine = new CustomHtmlEngine
            {
                EnableJavaScript = true
            };

            await engine.RenderAsync(
                html,
                new Uri("https://normalize.test/"),
                _ => Task.FromResult(string.Empty),
                _ => Task.FromResult<System.IO.Stream>(null),
                _ => { },
                viewportWidth: 1200,
                viewportHeight: 800,
                forceJavascript: true);

            var root = Assert.IsType<Element>(engine.GetActiveDom());
            Assert.True(HasClassToken(root.ClassName, "no-js"));
            Assert.False(HasClassToken(root.ClassName, "js"));

            var nav = root.Descendants().OfType<Element>()
                .First(e => string.Equals(e.TagName, "NAV", StringComparison.OrdinalIgnoreCase));
            Assert.True(HasClassToken(nav.ClassName, "no-js"));

            var footer = root.Descendants().OfType<Element>()
                .First(e => string.Equals(e.TagName, "FOOTER", StringComparison.OrdinalIgnoreCase));
            Assert.True(HasClassToken(footer.ClassName, "no-js"));
        }

        private static bool HasClassToken(string classValue, string token)
        {
            if (string.IsNullOrWhiteSpace(classValue) || string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            var parts = classValue.Split(new[] { ' ', '\t', '\r', '\n', '\f' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.Equals(parts[i], token, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
