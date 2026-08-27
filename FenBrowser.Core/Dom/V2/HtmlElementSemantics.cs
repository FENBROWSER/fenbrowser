using System;

namespace FenBrowser.Core.Dom.V2;

/// <summary>
/// Canonical namespace-aware classification for HTML element semantics.
/// </summary>
public static class HtmlElementSemantics
{
    public static bool IsVoid(string localName, string namespaceUri = Namespaces.Html)
    {
        if (!string.Equals(namespaceUri ?? Namespaces.Html, Namespaces.Html, StringComparison.Ordinal) ||
            string.IsNullOrEmpty(localName))
        {
            return false;
        }

        return localName.Length switch
        {
            2 => localName.Equals("br", StringComparison.OrdinalIgnoreCase) ||
                 localName.Equals("hr", StringComparison.OrdinalIgnoreCase),
            3 => localName.Equals("col", StringComparison.OrdinalIgnoreCase) ||
                 localName.Equals("img", StringComparison.OrdinalIgnoreCase) ||
                 localName.Equals("wbr", StringComparison.OrdinalIgnoreCase),
            4 => localName.Equals("area", StringComparison.OrdinalIgnoreCase) ||
                 localName.Equals("base", StringComparison.OrdinalIgnoreCase) ||
                 localName.Equals("link", StringComparison.OrdinalIgnoreCase) ||
                 localName.Equals("meta", StringComparison.OrdinalIgnoreCase),
            5 => localName.Equals("embed", StringComparison.OrdinalIgnoreCase) ||
                 localName.Equals("input", StringComparison.OrdinalIgnoreCase) ||
                 localName.Equals("param", StringComparison.OrdinalIgnoreCase) ||
                 localName.Equals("track", StringComparison.OrdinalIgnoreCase),
            6 => localName.Equals("source", StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }
}
