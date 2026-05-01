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
    private static readonly string[] InventoryNamedHtmlElements_2026_05_01 =
    {
        "a", "abbr", "address", "area", "article", "aside", "audio", "b", "base", "bdi", "bdo",
        "blockquote", "body", "br", "button", "canvas", "caption", "cite", "code", "col", "colgroup",
        "data", "datalist", "dd", "del", "details", "dfn", "dialog", "div", "dl", "dt", "em", "embed",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6",
        "head", "header", "hgroup", "hr", "html", "i", "iframe", "img", "input", "ins", "kbd", "label",
        "legend", "li", "link", "main", "map", "mark", "menu", "meta", "meter", "nav", "noscript",
        "object", "ol", "optgroup", "option", "output", "p", "picture", "pre", "progress", "q", "rp",
        "rt", "ruby", "s", "samp", "script", "search", "section", "select", "selectedcontent", "slot",
        "small", "source", "span", "strong", "style", "sub", "summary", "sup", "table", "tbody", "td",
        "template", "textarea", "tfoot", "th", "thead", "time", "title", "tr", "track", "u", "ul",
        "var", "video", "wbr"
    };

    [Fact]
    public async Task HtmlInventory_2026_05_01_AllNamedElements_AreRecognizedAsKnownHtmlElements()
    {
        var uniqueTags = InventoryNamedHtmlElements_2026_05_01
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(113, uniqueTags.Length);

        var missingInCatalog = uniqueTags
            .Where(tag => !HtmlElementInterfaceCatalog.GetTagInterfaceMap().ContainsKey(tag))
            .ToArray();
        Assert.Empty(missingInCatalog);

        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());
        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        var pairs = uniqueTags
            .Select(tag => new
            {
                tag,
                iface = HtmlElementInterfaceCatalog.ResolveInterfaceName(tag, Namespaces.Html)
            })
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

                    if (element instanceof HTMLUnknownElement) {{
                        failures.push(`unexpected unknown:${{pair.tag}}`);
                    }}

                    if (!(element instanceof ctor)) {{
                        failures.push(`instanceof mismatch:${{pair.tag}}->${{pair.iface}}`);
                    }}
                }}

                globalThis.__htmlInventoryFailures = failures.join('|');
            }})();
        ");

        Assert.Equal(string.Empty, engine.Evaluate("globalThis.__htmlInventoryFailures")?.ToString());
    }

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

    [Fact]
    public async Task HtmlInventory_2026_05_01_NamedElements_GlobalSemantics_Baseline()
    {
        var uniqueTags = InventoryNamedHtmlElements_2026_05_01
            .Select(tag => tag.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(tag => tag, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(113, uniqueTags.Length);

        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());
        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        var tagsJson = JsonSerializer.Serialize(uniqueTags);
        engine.Evaluate($@"
            (function() {{
                const tags = {tagsJson};
                const failures = [];

                for (const tag of tags) {{
                    const element = document.createElement(tag);

                    // hidden: true/false + until-found
                    element.hidden = true;
                    if (!element.hasAttribute('hidden')) failures.push('hidden-set:' + tag);
                    if (element.hidden !== true) failures.push('hidden-get-true:' + tag);
                    element.hidden = 'until-found';
                    if (element.getAttribute('hidden') !== 'until-found') failures.push('hidden-until-found-attr:' + tag);
                    if (element.hidden !== 'until-found') failures.push('hidden-until-found-get:' + tag);
                    element.hidden = false;
                    if (element.hasAttribute('hidden')) failures.push('hidden-clear:' + tag);

                    // translate: boolean reflection and inherited semantics baseline
                    element.translate = true;
                    if (element.getAttribute('translate') !== 'yes') failures.push('translate-yes:' + tag);
                    if (element.translate !== true) failures.push('translate-get-yes:' + tag);
                    element.translate = false;
                    if (element.getAttribute('translate') !== 'no') failures.push('translate-no:' + tag);
                    if (element.translate !== false) failures.push('translate-get-no:' + tag);

                    // Global reflected string properties baseline
                    element.lang = 'en';
                    if (element.getAttribute('lang') !== 'en' || element.lang !== 'en') failures.push('lang:' + tag);
                    element.dir = 'rtl';
                    if (element.getAttribute('dir') !== 'rtl' || element.dir !== 'rtl') failures.push('dir:' + tag);
                    element.title = 'hello';
                    if (element.getAttribute('title') !== 'hello' || element.title !== 'hello') failures.push('title:' + tag);
                    element.accessKey = 'k';
                    if (element.getAttribute('accesskey') !== 'k' || element.accessKey !== 'k') failures.push('accesskey:' + tag);
                }}

                // details.open should reflect boolean attribute
                const details = document.createElement('details');
                details.open = true;
                if (!details.hasAttribute('open') || details.open !== true) failures.push('details-open-true');
                details.open = false;
                if (details.hasAttribute('open') || details.open !== false) failures.push('details-open-false');

                // dialog.open should reflect boolean attribute
                const dialog = document.createElement('dialog');
                dialog.open = true;
                if (!dialog.hasAttribute('open') || dialog.open !== true) failures.push('dialog-open-true');
                dialog.open = false;
                if (dialog.hasAttribute('open') || dialog.open !== false) failures.push('dialog-open-false');

                globalThis.__htmlElementGlobalSemanticsFailures = failures.join('|');
            }})();
        ");

        Assert.Equal(string.Empty, engine.Evaluate("globalThis.__htmlElementGlobalSemanticsFailures")?.ToString());
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
