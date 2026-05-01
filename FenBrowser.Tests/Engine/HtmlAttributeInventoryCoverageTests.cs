using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine;

public class HtmlAttributeInventoryCoverageTests
{
    private static readonly string[] InventoryNonEventHtmlAttributes_2026_05_01 =
    {
        "abbr", "accept", "accept-charset", "accesskey", "action", "allow", "allowfullscreen", "alpha",
        "alt", "as", "async", "autocapitalize", "autocomplete", "autocorrect", "autofocus", "autoplay",
        "blocking", "charset", "checked", "cite", "class", "closedby", "color", "colorspace", "cols",
        "colspan", "command", "commandfor", "content", "contenteditable", "controls", "coords",
        "crossorigin", "data", "datetime", "decoding", "default", "defer", "dir", "dirname", "disabled",
        "download", "draggable", "enctype", "enterkeyhint", "fetchpriority", "for", "form", "formaction",
        "formenctype", "formmethod", "formnovalidate", "formtarget", "headers", "headingoffset",
        "headingreset", "height", "hidden", "high", "href", "hreflang", "http-equiv", "id", "imagesizes",
        "imagesrcset", "inert", "inputmode", "integrity", "is", "ismap", "itemid", "itemprop", "itemref",
        "itemscope", "itemtype", "kind", "label", "lang", "list", "loading", "loop", "low", "max",
        "maxlength", "media", "method", "min", "minlength", "multiple", "muted", "name", "nomodule",
        "nonce", "novalidate", "open", "optimum", "pattern", "ping", "placeholder", "playsinline",
        "popover", "popovertarget", "popovertargetaction", "poster", "preload", "readonly",
        "referrerpolicy", "rel", "required", "reversed", "rows", "rowspan", "sandbox", "scope", "selected",
        "shadowrootclonable", "shadowrootcustomelementregistry", "shadowrootdelegatesfocus", "shadowrootmode",
        "shadowrootserializable", "shadowrootslotassignment", "shape", "size", "sizes", "slot", "span",
        "spellcheck", "src", "srcdoc", "srclang", "srcset", "start", "step", "style", "tabindex", "target",
        "title", "translate", "type", "usemap", "value", "width", "wrap", "writingsuggestions"
    };

    private static readonly string[] BooleanReflectedAttributes_2026_05_01 =
    {
        "allowfullscreen", "async", "autofocus", "autoplay", "checked", "controls", "default", "defer",
        "disabled", "formnovalidate", "inert", "ismap", "itemscope", "loop", "multiple", "muted",
        "nomodule", "novalidate", "open", "readonly", "required", "reversed", "selected",
        "shadowrootclonable", "shadowrootcustomelementregistry", "shadowrootdelegatesfocus",
        "shadowrootserializable", "playsinline"
    };

    private static readonly string[] NonNegativeIntegerReflectedAttributes_2026_05_01 =
    {
        "cols", "colspan", "height", "maxlength", "minlength", "rows", "rowspan", "size", "span", "start", "width"
    };

    [Fact]
    public async Task HtmlInventory_2026_05_01_AllNonEventAttributes_RoundTripThroughDomApis()
    {
        var attributes = InventoryNonEventHtmlAttributes_2026_05_01
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(144, attributes.Length);

        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());
        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        var attrsJson = JsonSerializer.Serialize(attributes);
        engine.Evaluate($@"
            (function() {{
                const attrs = {attrsJson};
                const element = document.createElement('div');
                const failures = [];

                function safeValue(name) {{
                    if (name === 'style') return 'color: red;';
                    if (name === 'srcdoc') return '<p>ok</p>';
                    if (
                        name === 'href' || name === 'src' || name === 'action' ||
                        name === 'formaction' || name === 'data' || name === 'poster' ||
                        name === 'cite' || name === 'usemap'
                    ) {{
                        return 'https://example.com/' + name;
                    }}
                    return 'v_' + name.replace(/[^a-z0-9]/g, '_');
                }}

                for (const name of attrs) {{
                    const value = safeValue(name);
                    element.setAttribute(name, value);

                    if (element.getAttribute(name) !== value) {{
                        failures.push('roundtrip:' + name);
                    }}
                    if (!element.hasAttribute(name)) {{
                        failures.push('missing:' + name);
                    }}

                    element.removeAttribute(name);
                    if (element.hasAttribute(name)) {{
                        failures.push('remove:' + name);
                    }}
                }}

                globalThis.__htmlAttributeInventoryFailures = failures.join('|');
            }})();
        ");

        Assert.Equal(string.Empty, engine.Evaluate("globalThis.__htmlAttributeInventoryFailures")?.ToString());
    }

    [Fact]
    public void HtmlInventory_2026_05_01_AllNonEventAttributes_ArePreservedByHtmlParser()
    {
        var attributes = InventoryNonEventHtmlAttributes_2026_05_01
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(144, attributes.Length);

        var builder = new StringBuilder();
        builder.Append("<html><body><div");
        foreach (var name in attributes)
        {
            var value = name.Replace("-", "_", StringComparison.Ordinal);
            builder.Append(' ').Append(name).Append("=\"").Append(value).Append('"');
        }
        builder.Append("></div></body></html>");

        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser(builder.ToString(), baseUri);
        var doc = parser.Parse();
        var body = doc.Body;
        Assert.NotNull(body);
        var element = body.FirstElementChild;
        Assert.NotNull(element);

        var missing = attributes
            .Where(name => element.GetAttribute(name) is null)
            .ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public async Task HtmlInventory_2026_05_01_ReflectedIdlSemantics_AreApplied()
    {
        var attributes = InventoryNonEventHtmlAttributes_2026_05_01
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(144, attributes.Length);

        var excludedFromGenericReflectionProbe = new[]
        {
            "class", "contenteditable", "data", "disabled", "for", "form", "height", "http-equiv", "id",
            "name", "open", "popover", "sandbox", "selected", "sizes", "src", "style", "title", "type", "value",
            "width", "checked", "rows"
        };

        var reflectionProbeAttributes = attributes
            .Where(attr => !excludedFromGenericReflectionProbe.Contains(attr, StringComparer.Ordinal))
            .ToArray();

        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());
        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        var attrsJson = JsonSerializer.Serialize(reflectionProbeAttributes);
        var boolJson = JsonSerializer.Serialize(BooleanReflectedAttributes_2026_05_01);
        var intJson = JsonSerializer.Serialize(NonNegativeIntegerReflectedAttributes_2026_05_01);

        engine.Evaluate($@"
            (function() {{
                const attrs = {attrsJson};
                const boolAttrs = new Set({boolJson});
                const intAttrs = new Set({intJson});
                const failures = [];

                function propName(attr) {{
                    if (attr === 'class') return 'classname';
                    if (attr === 'for') return 'htmlfor';
                    if (attr === 'http-equiv') return 'httpequiv';
                    return attr.replace(/-/g, '');
                }}

                function safeStringValue(attr) {{
                    if (
                        attr === 'href' || attr === 'src' || attr === 'action' ||
                        attr === 'formaction' || attr === 'data' || attr === 'poster' ||
                        attr === 'cite' || attr === 'usemap'
                    ) {{
                        return 'https://example.com/' + attr;
                    }}
                    return 'p_' + attr.replace(/[^a-z0-9]/g, '_');
                }}

                for (const attr of attrs) {{
                    try {{
                        const el = document.createElement('div');
                        const prop = propName(attr);

                        if (attr === 'translate') {{
                            const parent = document.createElement('div');
                            const child = document.createElement('span');
                            parent.appendChild(child);
                            if (child[prop] !== true) failures.push('translate-default');
                            parent.setAttribute('translate', 'no');
                            if (child[prop] !== false) failures.push('translate-inherit-no');
                            child[prop] = true;
                            if (child.getAttribute('translate') !== 'yes') failures.push('translate-set-yes');
                            if (child[prop] !== true) failures.push('translate-read-yes');
                            child[prop] = false;
                            if (child.getAttribute('translate') !== 'no') failures.push('translate-set-no');
                            continue;
                        }}

                        if (attr === 'hidden') {{
                            el[prop] = true;
                            if (el.getAttribute('hidden') === null) failures.push('hidden-set-true');
                            if (el[prop] !== true) failures.push('hidden-get-true');
                            el[prop] = 'until-found';
                            if (el.getAttribute('hidden') !== 'until-found') failures.push('hidden-until-found-attr');
                            if (el[prop] !== 'until-found') failures.push('hidden-until-found-get');
                            el[prop] = false;
                            if (el.hasAttribute('hidden')) failures.push('hidden-set-false');
                            continue;
                        }}

                        if (boolAttrs.has(attr)) {{
                            el[prop] = true;
                            if (!el.hasAttribute(attr)) failures.push('bool-set-true:' + attr);
                            if (el[prop] !== true) failures.push('bool-get-true:' + attr);
                            el[prop] = false;
                            if (el.hasAttribute(attr)) failures.push('bool-set-false:' + attr);
                            if (el[prop] !== false) failures.push('bool-get-false:' + attr);
                            continue;
                        }}

                        if (intAttrs.has(attr)) {{
                            el[prop] = 42.9;
                            if (el.getAttribute(attr) !== '42') failures.push('int-serialize:' + attr);
                            if (Number(el[prop]) !== 42) failures.push('int-read:' + attr);
                            el[prop] = -3;
                            if (el.getAttribute(attr) !== '0') failures.push('int-clamp:' + attr);
                            continue;
                        }}

                        const expected = safeStringValue(attr);
                        el[prop] = expected;
                        if (el.getAttribute(attr) !== expected) {{
                            failures.push('string-set:' + attr);
                        }}

                        if (el[prop] !== expected) {{
                            failures.push('string-get:' + attr);
                        }}
                    }} catch (error) {{
                        failures.push('exception:' + attr + ':' + String(error));
                    }}
                }}

                globalThis.__htmlReflectedAttributeSemanticsFailures = failures.join('|');
            }})();
        ");

        Assert.Equal(string.Empty, engine.Evaluate("globalThis.__htmlReflectedAttributeSemanticsFailures")?.ToString());
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
