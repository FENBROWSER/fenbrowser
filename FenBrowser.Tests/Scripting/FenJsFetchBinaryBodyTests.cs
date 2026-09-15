using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// Fetch §Response body: a response is bytes. text() decodes them and
/// arrayBuffer()/blob() hand them over intact - a font, a 3D model or a wasm
/// module fetched by a page must survive the round trip byte for byte.
/// github.com's landing page loads its WebGL mascot assets this way and threw
/// "e.arrayBuffer is not a function".
/// </summary>
public sealed class FenJsFetchBinaryBodyTests
{
    [Fact]
    public async Task ArrayBufferAndBlob_ReturnTheResponseBytesIntact()
    {
        var baseUri = new Uri("https://fixture.test/index.html");
        var document = new HtmlParser(
            """
            <html><body><script>
            fetch('/assets/model.bin').then(function (response) {
                globalThis.__hasArrayBuffer = typeof response.arrayBuffer === 'function';
                return response.clone().arrayBuffer().then(function (buffer) {
                    var bytes = new Uint8Array(buffer);
                    globalThis.__bytes = Array.prototype.join.call(bytes, ',');
                    globalThis.__isArrayBuffer = buffer instanceof ArrayBuffer;
                    return response.blob();
                }).then(function (blob) {
                    globalThis.__blobSize = blob.size;
                    globalThis.__blobType = blob.type;
                    return blob.arrayBuffer();
                }).then(function (buffer) {
                    globalThis.__blobBytes = Array.prototype.join.call(new Uint8Array(buffer), ',');
                });
            });
            fetch('/assets/text.json').then(function (r) { return r.text(); }).then(function (t) {
                globalThis.__text = t;
            });
            </script></body></html>
            """,
            baseUri).Parse();

        var engine = new FenJsBrowserScriptEngine(CreateHost())
        {
            Sandbox = SandboxPolicy.AllowAll,
            FetchHandler = request =>
            {
                var path = request.RequestUri?.AbsolutePath ?? string.Empty;
                if (path == "/assets/model.bin")
                {
                    // Bytes that are not valid UTF-8: a string round trip would mangle them.
                    var content = new ByteArrayContent(new byte[] { 0x00, 0xFF, 0x80, 0xC3, 0x28, 0x7F, 0xFE });
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
                }

                var text = new ByteArrayContent(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'1', (byte)'}' });
                text.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = text });
            }
        };

        try
        {
            await engine.SetDomAsync(document.DocumentElement, baseUri);
            await WaitForGlobalAsync(engine, "globalThis.__blobBytes !== undefined && globalThis.__text !== undefined");

            Assert.Equal("true", engine.Evaluate("String(globalThis.__hasArrayBuffer)")?.ToString());
            Assert.Equal("true", engine.Evaluate("String(globalThis.__isArrayBuffer)")?.ToString());
            Assert.Equal("0,255,128,195,40,127,254", engine.Evaluate("String(globalThis.__bytes)")?.ToString());
            Assert.Equal("0,255,128,195,40,127,254", engine.Evaluate("String(globalThis.__blobBytes)")?.ToString());
            Assert.Equal("7", engine.Evaluate("String(globalThis.__blobSize)")?.ToString());
            Assert.Equal("application/octet-stream", engine.Evaluate("String(globalThis.__blobType)")?.ToString());
            // The BOM is not part of the decoded text.
            Assert.Equal("{\"a\":1}", engine.Evaluate("String(globalThis.__text)")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }


    // fetch() settles in a later task now that it is asynchronous, so a test
    // waits for the page script's own signal instead of reading it back at once.
    private static async Task WaitForGlobalAsync(FenJsBrowserScriptEngine engine, string expression)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (string.Equals(engine.Evaluate("String(" + expression + ")")?.ToString(), "true", StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(20);
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
