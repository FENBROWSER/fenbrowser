using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine;

public class HtmlElementInterfaceCoverageTests
{
    [Fact]
    public async Task HtmlElementInterfaces_ArePublished_And_TagInstanceofChainsMatch()
    {
        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());
        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        var pairs = HtmlElementInterfaceCatalog.GetTagInterfaceMap()
            .Where(entry => !string.Equals(entry.Value, "HTMLElement", StringComparison.Ordinal))
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new { tag = entry.Key, iface = entry.Value })
            .ToArray();
        var pairsJson = JsonSerializer.Serialize(pairs);

        engine.Evaluate($@"
            (function() {{
                const pairs = {pairsJson};
                const failures = [];
                for (const pair of pairs) {{
                    const ctor = globalThis[pair.iface];
                    if (typeof ctor !== 'function') {{
                        failures.push(`missing constructor:${{pair.iface}}`);
                        continue;
                    }}

                    const element = document.createElement(pair.tag);
                    if (!(element instanceof HTMLElement)) {{
                        failures.push(`not HTMLElement:${{pair.tag}}`);
                    }}

                    if (!(element instanceof ctor)) {{
                        failures.push(`instanceof mismatch:${{pair.tag}}->${{pair.iface}}`);
                    }}
                }}

                const unknown = document.createElement('fenunknown');
                if (!(unknown instanceof HTMLUnknownElement)) {{
                    failures.push('unknown tag should map to HTMLUnknownElement');
                }}

                const custom = document.createElement('x-fen-custom');
                if (!(custom instanceof HTMLElement)) {{
                    failures.push('custom element should inherit HTMLElement');
                }}

                if (custom instanceof HTMLUnknownElement) {{
                    failures.push('custom element must not be HTMLUnknownElement');
                }}

                globalThis.__htmlInterfaceFailures = failures.join('|');
            }})();
        ");

        Assert.Equal(string.Empty, engine.Evaluate("globalThis.__htmlInterfaceFailures")?.ToString());
    }

    [Fact]
    public async Task ImageAudioOption_Constructors_CreateConcreteHtmlElements()
    {
        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());
        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        engine.Evaluate(@"
            (function() {
                const failures = [];
                const image = new Image(64, 32);
                if (!(image instanceof HTMLImageElement)) failures.push('Image instanceof HTMLImageElement');
                if (!(image instanceof HTMLElement)) failures.push('Image instanceof HTMLElement');
                if (String(image.width) !== '64') failures.push('Image width');
                if (String(image.height) !== '32') failures.push('Image height');

                const audio = new Audio('/assets/sample.mp3');
                if (!(audio instanceof HTMLAudioElement)) failures.push('Audio instanceof HTMLAudioElement');
                if (!(audio instanceof HTMLMediaElement)) failures.push('Audio instanceof HTMLMediaElement');
                if (String(audio.src).indexOf('/assets/sample.mp3') < 0) failures.push('Audio src');

                const video = document.createElement('video');
                if (!(video instanceof HTMLVideoElement)) failures.push('Video instanceof HTMLVideoElement');
                if (!(video instanceof HTMLMediaElement)) failures.push('Video instanceof HTMLMediaElement');

                const option = new Option('Ready', 'r', true, true);
                if (!(option instanceof HTMLOptionElement)) failures.push('Option instanceof HTMLOptionElement');
                if (option.value !== 'r') failures.push('Option value');
                if (option.selected !== true) failures.push('Option selected');
                if (option.defaultSelected !== true) failures.push('Option defaultSelected');

                globalThis.__htmlFactoryFailures = failures.join('|');
            })();
        ");

        Assert.Equal(string.Empty, engine.Evaluate("globalThis.__htmlFactoryFailures")?.ToString());
    }

    [Fact]
    public async Task LegacyRecognizedHtmlTags_DoNotFallbackToHtmlUnknownElement()
    {
        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());
        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        engine.Evaluate(@"
            (function() {
                const tags = [
                    'acronym', 'applet', 'basefont', 'bgsound', 'big', 'blink',
                    'command', 'image', 'keygen', 'menuitem', 'multicol',
                    'nextid', 'noembed', 'noframes', 'spacer'
                ];

                const failures = [];
                for (const tag of tags) {
                    const element = document.createElement(tag);
                    if (element instanceof HTMLUnknownElement) {
                        failures.push('unexpected unknown:' + tag);
                    }
                }

                const imageAlias = document.createElement('image');
                if (!(imageAlias instanceof HTMLImageElement)) {
                    failures.push('image alias instanceof HTMLImageElement');
                }

                globalThis.__legacyTagFailures = failures.join('|');
            })();
        ");

        Assert.Equal(string.Empty, engine.Evaluate("globalThis.__legacyTagFailures")?.ToString());
    }

    private static JsHostAdapter CreateHost()
    {
        return new JsHostAdapter(
            navigate: _ => { },
            post: (_, __) => { },
            status: _ => { },
            log: _ => { });
    }
}
