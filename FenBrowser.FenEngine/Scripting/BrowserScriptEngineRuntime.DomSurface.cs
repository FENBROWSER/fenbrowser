using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.Js.Runtime;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// DOM surface members that fingerprinting scripts read and that every
/// mainstream browser exposes: hit testing from script, rendered text, ARIA
/// attribute reflection, image dimensions and whole-text on text nodes.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private static readonly HashSet<string> BlockLevelForInnerText = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "details", "dialog", "dd", "div", "dl", "dt",
        "fieldset", "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6",
        "header", "hgroup", "hr", "li", "main", "nav", "ol", "p", "pre", "section", "summary",
        "table", "tbody", "thead", "tfoot", "tr", "ul", "body", "html", "option", "legend", "caption"
    };

    private static readonly HashSet<string> SkippedForInnerText = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style", "template", "noscript", "head", "title", "meta", "link"
    };

    internal JsValue CreateHostArray(IReadOnlyList<JsValue> elements) => _interpreter.AllocateArray(elements);

    /// <summary>
    /// CSSOM View elementFromPoint: the topmost element of <paramref name="document"/>
    /// at client coordinates (x, y). Points in a nested frame resolve to the
    /// frame element, as the spec's "retarget to this document" step requires.
    /// </summary>
    internal Element ElementFromPoint(Document document, double x, double y)
    {
        if (document == null || double.IsNaN(x) || double.IsNaN(y) || x < 0 || y < 0)
        {
            return null;
        }

        // Frame client coordinates become top-viewport coordinates by adding
        // each enclosing frame's padding-box origin.
        var viewportX = x;
        var viewportY = y;
        for (var doc = document; doc != null;)
        {
            var frame = TryGetFrameElementForDocument(doc);
            if (frame == null)
            {
                break;
            }

            if (LayoutBoxResolver?.Invoke(frame) is BoxModel frameBox)
            {
                viewportX += frameBox.PaddingBox.Left;
                viewportY += frameBox.PaddingBox.Top;
            }

            doc = frame.OwnerDocument;
        }

        Element hit = null;
        try
        {
            hit = ViewportHitTester?.Invoke(viewportX, viewportY);
        }
        catch
        {
            hit = null;
        }

        if (hit == null)
        {
            hit = FindElementByLayoutBoxes(document, x, y);
        }

        // Retarget: climb out of nested documents until the element belongs to
        // the asking document; a hit in a child frame reports the frame.
        for (var depth = 0; hit != null && !ReferenceEquals(hit.OwnerDocument, document) && depth < 32; depth++)
        {
            hit = TryGetFrameElementForDocument(hit.OwnerDocument);
        }

        return hit ?? document.DocumentElement;
    }

    // Without a host hit tester (headless engine tests), the last element in
    // tree order whose border box contains the point is the topmost one for
    // the plain flow that such pages have.
    private Element FindElementByLayoutBoxes(Document document, double x, double y)
    {
        if (LayoutBoxResolver == null)
        {
            return null;
        }

        Element best = null;
        foreach (var node in document.Descendants())
        {
            if (node is not Element element || LayoutBoxResolver(element) is not BoxModel box)
            {
                continue;
            }

            var r = box.BorderBox;
            if (x >= r.Left && x < r.Left + r.Width && y >= r.Top && y < r.Top + r.Height)
            {
                best = element;
            }
        }

        return best;
    }

    /// <summary>DOM Text.wholeText: the data of this and its contiguous Text siblings.</summary>
    private static string ReadWholeText(CharacterData text)
    {
        var first = (Node)text;
        while (first.PreviousSibling is Text previous)
        {
            first = previous;
        }

        var sb = new StringBuilder();
        for (var node = first; node is Text current; node = node.NextSibling)
        {
            sb.Append(current.Data);
        }

        return sb.ToString();
    }

    /// <summary>
    /// HTML innerText getter, approximated without a full layout pass: block
    /// boundaries and br become line breaks, whitespace collapses inside
    /// runs, script-like and hidden content is skipped.
    /// </summary>
    private static string ReadInnerText(Element element)
    {
        if (element == null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        AppendInnerText(element, sb, preformatted: false);
        var text = sb.ToString();
        // Required line-break counts collapse: at most one blank line, none at the ends.
        while (text.Contains("\n\n\n", StringComparison.Ordinal))
        {
            text = text.Replace("\n\n\n", "\n\n", StringComparison.Ordinal);
        }

        return text.Trim('\n');
    }

    private static void AppendInnerText(Node node, StringBuilder sb, bool preformatted)
    {
        for (var child = node.FirstChild; child != null; child = child.NextSibling)
        {
            switch (child)
            {
                case Text text:
                    AppendCollapsed(sb, text.Data, preformatted);
                    break;
                case Element el:
                {
                    var tag = el.TagName ?? string.Empty;
                    if (SkippedForInnerText.Contains(tag) || el.HasAttribute("hidden") || IsDisplayNone(el))
                    {
                        break;
                    }

                    if (string.Equals(tag, "br", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.Append('\n');
                        break;
                    }

                    var block = BlockLevelForInnerText.Contains(tag);
                    var cell = string.Equals(tag, "td", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(tag, "th", StringComparison.OrdinalIgnoreCase);
                    if (block && sb.Length > 0 && sb[^1] != '\n')
                    {
                        sb.Append('\n');
                    }

                    AppendInnerText(el, sb, preformatted || string.Equals(tag, "pre", StringComparison.OrdinalIgnoreCase));
                    if (cell && el.NextSibling != null)
                    {
                        sb.Append('\t');
                    }

                    if (block && sb.Length > 0 && sb[^1] != '\n')
                    {
                        sb.Append('\n');
                    }

                    break;
                }
            }
        }
    }

    private static bool IsDisplayNone(Element element)
    {
        var style = element.GetAttribute("style");
        return !string.IsNullOrEmpty(style) &&
               style.Replace(" ", string.Empty).Contains("display:none", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendCollapsed(StringBuilder sb, string data, bool preformatted)
    {
        if (string.IsNullOrEmpty(data))
        {
            return;
        }

        if (preformatted)
        {
            sb.Append(data);
            return;
        }

        var pendingSpace = false;
        foreach (var ch in data)
        {
            if (ch is ' ' or '\t' or '\n' or '\r' or '\f')
            {
                pendingSpace = true;
                continue;
            }

            if (pendingSpace && sb.Length > 0 && sb[^1] != '\n' && sb[^1] != ' ' && sb[^1] != '\t')
            {
                sb.Append(' ');
            }

            pendingSpace = false;
            sb.Append(ch);
        }

        if (pendingSpace && sb.Length > 0 && sb[^1] != '\n' && sb[^1] != ' ')
        {
            sb.Append(' ');
        }
    }

    /// <summary>HTML innerText setter: text with each line break becoming a br.</summary>
    private static void WriteInnerText(Element element, string value)
    {
        var document = element.OwnerDocument;
        element.TextContent = string.Empty;
        if (string.IsNullOrEmpty(value) || document == null)
        {
            return;
        }

        var lines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0)
            {
                element.AppendChild(document.CreateElement("br"));
            }

            if (lines[i].Length > 0)
            {
                element.AppendChild(document.CreateTextNode(lines[i]));
            }
        }
    }

    /// <summary>
    /// HTMLImageElement.width/height: the rendered size, which is the
    /// attribute when given, else the intrinsic size of the loaded image.
    /// </summary>
    internal int ReadImageDimension(Element image, string property)
    {
        var attribute = image.GetAttribute(property);
        if (!string.IsNullOrWhiteSpace(attribute) &&
            int.TryParse(attribute.Trim().TrimEnd('p', 'x'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var declared))
        {
            return Math.Max(0, declared);
        }

        if (LayoutBoxResolver?.Invoke(image) is BoxModel box)
        {
            var rendered = string.Equals(property, "width", StringComparison.Ordinal) ? box.ContentBox.Width : box.ContentBox.Height;
            if (rendered > 0)
            {
                return (int)Math.Round(rendered);
            }
        }

        var natural = GetImageNaturalSize(image);
        return string.Equals(property, "width", StringComparison.Ordinal) ? natural.Width : natural.Height;
    }

    /// <summary>
    /// ARIA 1.2 IDL attribute reflection: ariaLabel is aria-label, ariaValueMax
    /// is aria-valuemax. Only the ARIA-prefixed camel-case names qualify.
    /// </summary>
    private static bool TryMapAriaReflection(string property, out string attribute)
    {
        attribute = null;
        if (property == null || property.Length < 6 || !property.StartsWith("aria", StringComparison.Ordinal) ||
            !char.IsUpper(property[4]))
        {
            return false;
        }

        // The element-reference variants (ariaLabelledByElements ...) are a
        // different, object-valued API; they are not attribute reflection.
        if (property.EndsWith("Elements", StringComparison.Ordinal) || string.Equals(property, "ariaElements", StringComparison.Ordinal))
        {
            return false;
        }

        attribute = "aria-" + property.Substring(4).ToLowerInvariant();
        return true;
    }
}
