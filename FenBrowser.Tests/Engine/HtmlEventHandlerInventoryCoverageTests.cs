using System;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.DOM;
using FenBrowser.FenEngine.Scripting;
using Xunit;

namespace FenBrowser.Tests.Engine;

public class HtmlEventHandlerInventoryCoverageTests
{
    private static readonly string[] InventoryEventHandlerAttributes_2026_05_01 =
    {
        "onafterprint", "onauxclick", "onbeforeinput", "onbeforematch", "onbeforeprint", "onbeforeunload",
        "onbeforetoggle", "onblur", "oncancel", "oncanplay", "oncanplaythrough", "onchange", "onclick",
        "onclose", "oncommand", "oncontextlost", "oncontextmenu", "oncontextrestored", "oncopy", "oncuechange",
        "oncut", "ondblclick", "ondrag", "ondragend", "ondragenter", "ondragleave", "ondragover", "ondragstart",
        "ondrop", "ondurationchange", "onemptied", "onended", "onerror", "onfocus", "onformdata", "onhashchange",
        "oninput", "oninvalid", "onkeydown", "onkeypress", "onkeyup", "onlanguagechange", "onload", "onloadeddata",
        "onloadedmetadata", "onloadstart", "onmessage", "onmessageerror", "onmousedown", "onmouseenter",
        "onmouseleave", "onmousemove", "onmouseout", "onmouseover", "onmouseup", "onoffline", "ononline",
        "onpagehide", "onpagereveal", "onpageshow", "onpageswap", "onpaste", "onpause", "onplay", "onplaying",
        "onpopstate", "onprogress", "onratechange", "onreset", "onresize", "onrejectionhandled", "onscroll",
        "onscrollend", "onsecuritypolicyviolation", "onseeked", "onseeking", "onselect", "onslotchange",
        "onstalled", "onstorage", "onsubmit", "onsuspend", "ontimeupdate", "ontoggle", "onunhandledrejection",
        "onunload", "onvolumechange", "onwaiting", "onwheel"
    };

    [Fact]
    public void HtmlInventory_2026_05_01_EventHandlerAttributeCount_IsStable()
    {
        var attributes = InventoryEventHandlerAttributes_2026_05_01
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(89, attributes.Length);
    }

    [Fact]
    public async Task HtmlInventory_2026_05_01_EventHandlerAttributes_RoundTripThroughDomApis()
    {
        var attributes = InventoryEventHandlerAttributes_2026_05_01
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());
        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        var attrsJson = JsonSerializer.Serialize(attributes);
        engine.Evaluate($@"
            (function() {{
                const attrs = {attrsJson};
                const el = document.createElement('div');
                const failures = [];
                for (const name of attrs) {{
                    const value = 'handler_' + name;
                    el.setAttribute(name, value);
                    if (el.getAttribute(name) !== value) failures.push('roundtrip:' + name);
                    if (!el.hasAttribute(name)) failures.push('missing:' + name);
                    el.removeAttribute(name);
                    if (el.hasAttribute(name)) failures.push('remove:' + name);
                }}
                globalThis.__htmlEventAttributeRoundTripFailures = failures.join('|');
            }})();
        ");

        Assert.Equal(string.Empty, engine.Evaluate("globalThis.__htmlEventAttributeRoundTripFailures")?.ToString());
    }

    [Fact]
    public void HtmlInventory_2026_05_01_EventHandlerAttributes_ArePreservedByHtmlParser()
    {
        var attributes = InventoryEventHandlerAttributes_2026_05_01
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder();
        builder.Append("<html><body><div");
        foreach (var name in attributes)
        {
            builder.Append(' ').Append(name).Append("=\"handler\""); // keep value safe and deterministic
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
    public async Task HtmlInventory_2026_05_01_EventHandlerProperties_ArePublishedOnWindowAndNormalizeAssignments()
    {
        var attributes = InventoryEventHandlerAttributes_2026_05_01
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var baseUri = new Uri("https://example.com/index.html");
        var parser = new HtmlParser("<html><body></body></html>", baseUri);
        var doc = parser.Parse();
        var engine = new JavaScriptEngine(CreateHost());
        await engine.SetDomAsync(doc.DocumentElement, baseUri);

        var attrsJson = JsonSerializer.Serialize(attributes);
        engine.Evaluate($@"
            (function() {{
                const attrs = {attrsJson};
                const failures = [];
                for (const name of attrs) {{
                    if (!(name in window)) {{
                        failures.push('missing-property:' + name);
                        continue;
                    }}

                    const before = window[name];
                    if (!(before === null || typeof before === 'function')) {{
                        failures.push('unexpected-default:' + name);
                    }}

                    window[name] = function() {{}};
                    if (typeof window[name] !== 'function') {{
                        failures.push('function-set-failed:' + name);
                    }}

                    window[name] = 42;
                    if (window[name] !== null) {{
                        failures.push('noncallable-not-normalized:' + name);
                    }}
                }}

                globalThis.__htmlEventWindowPropertyFailures = failures.join('|');
            }})();
        ");

        Assert.Equal(string.Empty, engine.Evaluate("globalThis.__htmlEventWindowPropertyFailures")?.ToString());
    }

    [Fact]
    public void HtmlInventory_2026_05_01_EventHandlerNames_ArePresentInRuntimeRegistrationSets()
    {
        var inventory = InventoryEventHandlerAttributes_2026_05_01
            .Select(name => name.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var windowField = typeof(FenRuntime).GetField("s_windowDefaultEventHandlerNames", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(windowField);
        var windowNames = ((string[]?)windowField!.GetValue(null) ?? Array.Empty<string>())
            .Select(name => name.Trim().ToLowerInvariant());

        var elementGlobalField = typeof(ElementWrapper).GetField("GlobalEventHandlerProperties", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(elementGlobalField);
        var elementGlobalNames = (((System.Collections.IEnumerable?)elementGlobalField!.GetValue(null)) ?? Array.Empty<string>())
            .Cast<object>()
            .Select(name => name.ToString())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim().ToLowerInvariant());

        var legacyForwardedField = typeof(ElementWrapper).GetField("LegacyBodyFrameSetForwardedHandlers", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(legacyForwardedField);
        var legacyForwardedNames = ((string[]?)legacyForwardedField!.GetValue(null) ?? Array.Empty<string>())
            .Select(name => name.Trim().ToLowerInvariant());

        var runtimeNames = windowNames
            .Concat(elementGlobalNames)
            .Concat(legacyForwardedNames)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var missing = inventory.Where(name => !runtimeNames.Contains(name)).ToArray();
        Assert.Empty(missing);
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
