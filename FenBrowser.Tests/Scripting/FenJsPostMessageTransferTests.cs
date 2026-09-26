using System;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Scripting;

/// <summary>
/// HTML "StructuredSerializeWithTransfer": an ArrayBuffer in postMessage's transfer list
/// is detached for the sender (byteLength 0) and transferring it again is a
/// DataCloneError. mediasource-append-buffer appends such a neutered buffer.
/// </summary>
public sealed class FenJsPostMessageTransferTests
{
    [Fact]
    public async Task TransferredArrayBuffer_IsDetachedForTheSender()
    {
        var baseUri = new Uri("https://fixture.test/index.html");
        var document = new HtmlParser(
            """
            <html><body><script>
            var buffer = new Uint8Array(16).buffer;
            window.postMessage('test', '*', [buffer]);
            globalThis.__afterTransfer = buffer.byteLength;
            var again = 'none';
            try { window.postMessage('test', '*', [buffer]); } catch (e) { again = e.name; }
            globalThis.__again = again;
            </script></body></html>
            """,
            baseUri).Parse();

        var engine = new FenJsBrowserScriptEngine(CreateHost()) { Sandbox = SandboxPolicy.AllowAll };
        try
        {
            await engine.SetDomAsync(document.DocumentElement, baseUri);
            Assert.Equal("0", engine.Evaluate("String(globalThis.__afterTransfer)")?.ToString());
            Assert.Equal("DataCloneError", engine.Evaluate("String(globalThis.__again)")?.ToString());
        }
        finally
        {
            BrowserScriptEngineRuntime.Reset();
        }
    }

    private static JsHostAdapter CreateHost() =>
        new(
            navigate: _ => { },
            post: (_, _) => { },
            status: _ => { },
            log: _ => { });
}
