using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FenBrowser.Core.Dom.V2
{
    /// <summary>
    /// Canonical HTML tag -> Web interface mapping used by wrapper/prototype resolution.
    /// </summary>
    public static class HtmlElementInterfaceCatalog
    {
        private static readonly IReadOnlyDictionary<string, string> s_tagToInterface =
            new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["a"] = "HTMLAnchorElement",
                ["abbr"] = "HTMLElement",
                ["acronym"] = "HTMLElement",
                ["address"] = "HTMLElement",
                ["applet"] = "HTMLElement",
                ["area"] = "HTMLAreaElement",
                ["article"] = "HTMLElement",
                ["aside"] = "HTMLElement",
                ["audio"] = "HTMLAudioElement",
                ["b"] = "HTMLElement",
                ["base"] = "HTMLBaseElement",
                ["basefont"] = "HTMLElement",
                ["bdi"] = "HTMLElement",
                ["bdo"] = "HTMLElement",
                ["bgsound"] = "HTMLElement",
                ["big"] = "HTMLElement",
                ["blink"] = "HTMLElement",
                ["blockquote"] = "HTMLQuoteElement",
                ["body"] = "HTMLBodyElement",
                ["br"] = "HTMLBRElement",
                ["button"] = "HTMLButtonElement",
                ["canvas"] = "HTMLCanvasElement",
                ["caption"] = "HTMLTableCaptionElement",
                ["center"] = "HTMLElement",
                ["cite"] = "HTMLElement",
                ["code"] = "HTMLElement",
                ["col"] = "HTMLTableColElement",
                ["colgroup"] = "HTMLTableColElement",
                ["command"] = "HTMLElement",
                ["data"] = "HTMLDataElement",
                ["datalist"] = "HTMLDataListElement",
                ["dd"] = "HTMLElement",
                ["del"] = "HTMLModElement",
                ["details"] = "HTMLDetailsElement",
                ["dfn"] = "HTMLElement",
                ["dialog"] = "HTMLDialogElement",
                ["dir"] = "HTMLDirectoryElement",
                ["div"] = "HTMLDivElement",
                ["dl"] = "HTMLDListElement",
                ["dt"] = "HTMLElement",
                ["em"] = "HTMLElement",
                ["embed"] = "HTMLEmbedElement",
                ["fieldset"] = "HTMLFieldSetElement",
                ["figcaption"] = "HTMLElement",
                ["figure"] = "HTMLElement",
                ["font"] = "HTMLFontElement",
                ["footer"] = "HTMLElement",
                ["form"] = "HTMLFormElement",
                ["frame"] = "HTMLFrameElement",
                ["frameset"] = "HTMLFrameSetElement",
                ["h1"] = "HTMLHeadingElement",
                ["h2"] = "HTMLHeadingElement",
                ["h3"] = "HTMLHeadingElement",
                ["h4"] = "HTMLHeadingElement",
                ["h5"] = "HTMLHeadingElement",
                ["h6"] = "HTMLHeadingElement",
                ["head"] = "HTMLHeadElement",
                ["header"] = "HTMLElement",
                ["hgroup"] = "HTMLElement",
                ["hr"] = "HTMLHRElement",
                ["html"] = "HTMLHtmlElement",
                ["i"] = "HTMLElement",
                ["iframe"] = "HTMLIFrameElement",
                ["image"] = "HTMLImageElement",
                ["img"] = "HTMLImageElement",
                ["input"] = "HTMLInputElement",
                ["ins"] = "HTMLModElement",
                ["kbd"] = "HTMLElement",
                ["keygen"] = "HTMLElement",
                ["label"] = "HTMLLabelElement",
                ["legend"] = "HTMLLegendElement",
                ["li"] = "HTMLLIElement",
                ["link"] = "HTMLLinkElement",
                ["listing"] = "HTMLPreElement",
                ["main"] = "HTMLElement",
                ["map"] = "HTMLMapElement",
                ["mark"] = "HTMLElement",
                ["marquee"] = "HTMLMarqueeElement",
                ["menu"] = "HTMLMenuElement",
                ["menuitem"] = "HTMLElement",
                ["meta"] = "HTMLMetaElement",
                ["meter"] = "HTMLMeterElement",
                ["multicol"] = "HTMLElement",
                ["nav"] = "HTMLElement",
                ["nextid"] = "HTMLElement",
                ["nobr"] = "HTMLElement",
                ["noembed"] = "HTMLElement",
                ["noframes"] = "HTMLElement",
                ["noscript"] = "HTMLElement",
                ["object"] = "HTMLObjectElement",
                ["ol"] = "HTMLOListElement",
                ["optgroup"] = "HTMLOptGroupElement",
                ["option"] = "HTMLOptionElement",
                ["output"] = "HTMLOutputElement",
                ["p"] = "HTMLParagraphElement",
                ["param"] = "HTMLParamElement",
                ["picture"] = "HTMLPictureElement",
                ["plaintext"] = "HTMLElement",
                ["portal"] = "HTMLPortalElement",
                ["pre"] = "HTMLPreElement",
                ["progress"] = "HTMLProgressElement",
                ["q"] = "HTMLQuoteElement",
                ["rb"] = "HTMLElement",
                ["rp"] = "HTMLElement",
                ["rt"] = "HTMLElement",
                ["rtc"] = "HTMLElement",
                ["ruby"] = "HTMLElement",
                ["s"] = "HTMLElement",
                ["samp"] = "HTMLElement",
                ["script"] = "HTMLScriptElement",
                ["search"] = "HTMLElement",
                ["selectedcontent"] = "HTMLSelectedContentElement",
                ["section"] = "HTMLElement",
                ["select"] = "HTMLSelectElement",
                ["slot"] = "HTMLSlotElement",
                ["small"] = "HTMLElement",
                ["source"] = "HTMLSourceElement",
                ["spacer"] = "HTMLElement",
                ["span"] = "HTMLSpanElement",
                ["strike"] = "HTMLElement",
                ["strong"] = "HTMLElement",
                ["style"] = "HTMLStyleElement",
                ["sub"] = "HTMLElement",
                ["summary"] = "HTMLElement",
                ["sup"] = "HTMLElement",
                ["table"] = "HTMLTableElement",
                ["tbody"] = "HTMLTableSectionElement",
                ["td"] = "HTMLTableCellElement",
                ["template"] = "HTMLTemplateElement",
                ["textarea"] = "HTMLTextAreaElement",
                ["tfoot"] = "HTMLTableSectionElement",
                ["th"] = "HTMLTableCellElement",
                ["thead"] = "HTMLTableSectionElement",
                ["time"] = "HTMLTimeElement",
                ["title"] = "HTMLTitleElement",
                ["tr"] = "HTMLTableRowElement",
                ["track"] = "HTMLTrackElement",
                ["tt"] = "HTMLElement",
                ["u"] = "HTMLElement",
                ["ul"] = "HTMLUListElement",
                ["var"] = "HTMLElement",
                ["video"] = "HTMLVideoElement",
                ["wbr"] = "HTMLElement",
                ["xmp"] = "HTMLElement"
            });

        private static readonly IReadOnlyList<string> s_interfaceNames = BuildInterfaceNames();

        private static IReadOnlyList<string> BuildInterfaceNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal)
            {
                "HTMLElement",
                "HTMLUnknownElement",
                "HTMLMediaElement"
            };

            foreach (var interfaceName in s_tagToInterface.Values)
            {
                names.Add(interfaceName);
            }

            return names.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        public static IReadOnlyList<string> GetInterfaceNames() => s_interfaceNames;

        public static IReadOnlyDictionary<string, string> GetTagInterfaceMap() => s_tagToInterface;

        public static string ResolveInterfaceName(string localName, string namespaceUri)
        {
            if (!IsHtmlNamespace(namespaceUri) || string.IsNullOrWhiteSpace(localName))
            {
                return null;
            }

            var normalized = localName.Trim().ToLowerInvariant();
            if (s_tagToInterface.TryGetValue(normalized, out var interfaceName))
            {
                return interfaceName;
            }

            // Custom elements (hyphenated names) inherit from HTMLElement even when unresolved.
            if (normalized.IndexOf('-', StringComparison.Ordinal) >= 0)
            {
                return "HTMLElement";
            }

            return "HTMLUnknownElement";
        }

        public static bool IsHtmlNamespace(string namespaceUri) =>
            string.IsNullOrEmpty(namespaceUri) || string.Equals(namespaceUri, Namespaces.Html, StringComparison.Ordinal);
    }
}
