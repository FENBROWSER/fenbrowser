using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
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
        public async Task NavigateUserInputAsync_DoesNotAutoFollowGoogleRecoveryLink()
        {
            using var handler = new GoogleRecoveryLinkHandler();
            using var httpClient = new HttpClient(handler);
            var resources = new ResourceManager(httpClient, isPrivate: true);
            var navigation = new NavigationManager(resources);

            using var browser = new BrowserHost(isPrivate: true);
            SetPrivateField(browser, "_navManager", navigation);

            var navigated = await browser.NavigateUserInputAsync("https://www.google.com/search?q=test");

            Assert.True(navigated);
            Assert.Single(handler.RequestUris);
            Assert.Equal("https://www.google.com/search?q=test", browser.CurrentUri.AbsoluteUri);
        }

        [Fact]
        public async Task NavigateAsync_CoalescesDuplicateProgrammaticNavigationWhileFirstIsLoading()
        {
            using var handler = new DelayedNavigationHandler();
            using var httpClient = new HttpClient(handler);
            var resources = new ResourceManager(httpClient, isPrivate: true);
            var navigation = new NavigationManager(resources);

            using var browser = new BrowserHost(isPrivate: true);
            SetPrivateField(browser, "_navManager", navigation);

            var first = browser.NavigateAsync("https://example.test/next");
            await handler.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var duplicate = await browser.NavigateAsync("https://example.test/next");
            handler.ReleaseFirstRequest.SetResult(null);
            var firstResult = await first;

            Assert.False(duplicate);
            Assert.True(firstResult);
            Assert.Single(handler.RequestUris);
            Assert.Equal("https://example.test/next", browser.CurrentUri.AbsoluteUri);
        }

        [Fact]
        public async Task ScriptCreatedIframe_LoadsAndRunsFrameScripts()
        {
            using var handler = new ScriptFrameHandler();
            using var httpClient = new HttpClient(handler);
            var resources = new ResourceManager(httpClient, isPrivate: true);
            var navigation = new NavigationManager(resources);

            using var browser = new BrowserHost(isPrivate: true);
            SetPrivateField(browser, "_resources", resources);
            SetPrivateField(browser, "_navManager", navigation);

            var navigated = await browser.NavigateAsync("https://example.test/page");
            var marker = await WaitForElementAsync(browser, "frame-script-marker", element =>
                element.ComputedStyle?.ForegroundColor == new SKColor(1, 2, 3));
            var engine = GetPrivateField<CustomHtmlEngine>(browser, "_engine");
            var iframe = FindFirstElement(engine.GetActiveDom(), element =>
                string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase));
            var frameDocument = Assert.IsType<Document>(iframe?.FirstChild);

            Assert.True(navigated);
            Assert.Contains(handler.RequestUris, uri => uri.AbsoluteUri == "https://example.test/frames/frame.html");
            Assert.Contains(handler.RequestUris, uri => uri.AbsoluteUri == "https://example.test/frames/frame.css");
            Assert.Same(frameDocument, frameDocument.DocumentElement.OwnerDocument);
            Assert.Equal("https://example.test/frames/frame.html", frameDocument.URL);
            Assert.NotNull(marker);
            Assert.Equal(new SKColor(1, 2, 3), marker.ComputedStyle?.ForegroundColor);
            Assert.Contains("frame script ran", marker.TextContent);
        }

        [Fact]
        public async Task ScriptCreatedIframe_SrcNavigationLoadsAndRunsNextFrameScripts()
        {
            using var handler = new NavigatingScriptFrameHandler();
            using var httpClient = new HttpClient(handler);
            var resources = new ResourceManager(httpClient, isPrivate: true);
            var navigation = new NavigationManager(resources);

            using var browser = new BrowserHost(isPrivate: true);
            SetPrivateField(browser, "_resources", resources);
            SetPrivateField(browser, "_navManager", navigation);

            var navigated = await browser.NavigateAsync("https://example.test/page");
            var marker = await WaitForElementAsync(browser, "second-frame-marker");
            var engine = GetPrivateField<CustomHtmlEngine>(browser, "_engine");
            var iframe = FindFirstElement(engine.GetActiveDom(), element =>
                string.Equals(element.TagName, "iframe", StringComparison.OrdinalIgnoreCase));
            var frameDocument = Assert.IsType<Document>(iframe?.FirstChild);

            Assert.True(navigated);
            Assert.Contains(handler.RequestUris, uri => uri.AbsoluteUri == "https://example.test/frames/first.html");
            Assert.Contains(handler.RequestUris, uri => uri.AbsoluteUri == "https://example.test/frames/second.html");
            Assert.Equal("https://example.test/frames/second.html", frameDocument.URL);
            Assert.NotNull(marker);
            Assert.Contains("second frame script ran", marker.TextContent);
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

        private static void SetPrivateField(object owner, string fieldName, object value)
        {
            var field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            field!.SetValue(owner, value);
        }

        private static T GetPrivateField<T>(object owner, string fieldName)
        {
            var field = owner.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field);
            return Assert.IsType<T>(field!.GetValue(owner));
        }

        private static async Task<string> WaitForPageSourceContainsAsync(BrowserHost browser, string expected)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            string source = null;
            while (DateTime.UtcNow < deadline)
            {
                source = await browser.GetPageSourceAsync();
                if (source?.IndexOf(expected, StringComparison.Ordinal) >= 0)
                {
                    return source;
                }

                await Task.Delay(50);
            }

            return source ?? string.Empty;
        }

        private static async Task<Element> WaitForElementAsync(
            BrowserHost browser,
            string id,
            Func<Element, bool> predicate = null)
        {
            var engine = GetPrivateField<CustomHtmlEngine>(browser, "_engine");
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            Element match = null;
            while (DateTime.UtcNow < deadline)
            {
                match = FindFirstElement(engine.GetActiveDom(), element =>
                    string.Equals(element.Id, id, StringComparison.Ordinal));
                if (match != null && (predicate == null || predicate(match)))
                {
                    return match;
                }

                await Task.Delay(50);
            }

            return match;
        }

        private static Element FindFirstElement(Node root, Func<Element, bool> predicate)
        {
            if (root == null)
            {
                return null;
            }

            var stack = new Stack<Node>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                if (node is Element element && predicate(element))
                {
                    return element;
                }

                var children = node.ChildNodes;
                for (int i = children.Length - 1; i >= 0; i--)
                {
                    stack.Push(children[i]);
                }
            }

            return null;
        }

        private sealed class GoogleRecoveryLinkHandler : HttpMessageHandler
        {
            private readonly object _lock = new();
            private readonly List<Uri> _requestUris = new();

            public IReadOnlyList<Uri> RequestUris
            {
                get
                {
                    lock (_lock)
                    {
                        return _requestUris.ToArray();
                    }
                }
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (_lock)
                {
                    _requestUris.Add(request.RequestUri);
                }

                var html = request.RequestUri?.Query.IndexOf("emsg=SG_REL", StringComparison.OrdinalIgnoreCase) >= 0
                    ? "<!doctype html><html><body><main>Recovery link was followed</main></body></html>"
                    : "<!doctype html><html><head><title>Google</title></head><body>" +
                      "<main id='search'>Search result stayed here</main>" +
                      "<div id='yvlrue' style='display:none'>If you're having trouble accessing Google Search, " +
                      "please <a href='/search?q=test&amp;emsg=SG_REL&amp;sei=abc'>click here</a>.</div>" +
                      "</body></html>";

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html"),
                    RequestMessage = request
                });
            }
        }

        private sealed class ScriptFrameHandler : HttpMessageHandler
        {
            private readonly object _lock = new();
            private readonly List<Uri> _requestUris = new();

            public IReadOnlyList<Uri> RequestUris
            {
                get
                {
                    lock (_lock)
                    {
                        return _requestUris.ToArray();
                    }
                }
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (_lock)
                {
                    _requestUris.Add(request.RequestUri);
                }

                if (string.Equals(request.RequestUri?.AbsolutePath, "/frames/frame.css", StringComparison.OrdinalIgnoreCase))
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(".frame-marker{color:#010203;display:block}", Encoding.UTF8, "text/css"),
                        RequestMessage = request
                    });
                }

                var html = string.Equals(request.RequestUri?.AbsolutePath, "/frames/frame.html", StringComparison.OrdinalIgnoreCase)
                    ? "<!doctype html><html><head><link rel='stylesheet' href='frame.css'></head><body><script>" +
                      "var marker=document.createElement('p');" +
                      "marker.id='frame-script-marker';" +
                      "marker.className='frame-marker';" +
                      "marker.textContent='frame script ran';" +
                      "document.body.appendChild(marker);" +
                      "</script></body></html>"
                    : "<!doctype html><html><body><main>Top page</main><script>" +
                      "var frame=document.createElement('iframe');" +
                      "frame.src='/frames/frame.html';" +
                      "document.body.appendChild(frame);" +
                      "</script></body></html>";

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html"),
                    RequestMessage = request
                });
            }
        }

        private sealed class NavigatingScriptFrameHandler : HttpMessageHandler
        {
            private readonly object _lock = new();
            private readonly List<Uri> _requestUris = new();

            public IReadOnlyList<Uri> RequestUris
            {
                get
                {
                    lock (_lock)
                    {
                        return _requestUris.ToArray();
                    }
                }
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                lock (_lock)
                {
                    _requestUris.Add(request.RequestUri);
                }

                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                var html = path switch
                {
                    "/frames/first.html" => "<!doctype html><html><body><script>" +
                                            "window.frameElement.setAttribute('src','/frames/second.html');" +
                                            "</script></body></html>",
                    "/frames/second.html" => "<!doctype html><html><body><script>" +
                                             "var marker=document.createElement('p');" +
                                             "marker.id='second-frame-marker';" +
                                             "marker.textContent='second frame script ran';" +
                                             "document.body.appendChild(marker);" +
                                             "</script></body></html>",
                    _ => "<!doctype html><html><body><main>Top page</main><script>" +
                         "var frame=document.createElement('iframe');" +
                         "frame.src='/frames/first.html';" +
                         "document.body.appendChild(frame);" +
                         "</script></body></html>"
                };

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(html, Encoding.UTF8, "text/html"),
                    RequestMessage = request
                });
            }
        }

        private sealed class DelayedNavigationHandler : HttpMessageHandler
        {
            private readonly object _lock = new();
            private readonly List<Uri> _requestUris = new();

            public TaskCompletionSource<object> FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public TaskCompletionSource<object> ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public IReadOnlyList<Uri> RequestUris
            {
                get
                {
                    lock (_lock)
                    {
                        return _requestUris.ToArray();
                    }
                }
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var requestIndex = 0;
                lock (_lock)
                {
                    _requestUris.Add(request.RequestUri);
                    requestIndex = _requestUris.Count;
                }

                if (requestIndex == 1)
                {
                    FirstRequestStarted.SetResult(null);
                    await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("<!doctype html><html><body><main>Loaded</main></body></html>", Encoding.UTF8, "text/html"),
                    RequestMessage = request
                };
            }
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
