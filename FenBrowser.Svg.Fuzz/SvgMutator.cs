using System.Text;

namespace FenBrowser.Svg.Fuzz;

/// <summary>
/// Deterministic, seeded mutations: byte-level noise plus SVG-aware edits that
/// build deep nesting, reference cycles, hostile attribute values and element
/// soup. Output is bounded so a case never stalls the suite on size alone.
/// </summary>
internal sealed class SvgMutator
{
    public const int MaxOutputChars = 64 * 1024;

    private static readonly string[] Elements =
    {
        "svg", "g", "rect", "circle", "ellipse", "line", "polyline", "polygon", "path", "text", "tspan",
        "textPath", "use", "symbol", "defs", "linearGradient", "radialGradient", "stop", "pattern",
        "clipPath", "mask", "marker", "filter", "feGaussianBlur", "feOffset", "feBlend", "feColorMatrix",
        "feComposite", "feFlood", "feMerge", "feMergeNode", "feMorphology", "feTurbulence",
        "feConvolveMatrix", "feDisplacementMap", "feImage", "feTile", "feComponentTransfer", "feFuncR",
        "feDiffuseLighting", "feSpecularLighting", "fePointLight", "feSpotLight", "feDistantLight",
        "feDropShadow", "image", "foreignObject", "switch", "a", "style", "script", "animate",
        "animateTransform", "animateMotion", "set", "mpath", "view", "title", "desc", "metadata"
    };

    private static readonly string[] Attributes =
    {
        "id", "href", "xlink:href", "d", "x", "y", "width", "height", "viewBox", "preserveAspectRatio",
        "transform", "fill", "stroke", "stroke-width", "stroke-dasharray", "stroke-dashoffset", "style",
        "class", "clip-path", "mask", "filter", "marker-start", "marker-mid", "marker-end", "stdDeviation",
        "in", "in2", "result", "points", "r", "rx", "ry", "cx", "cy", "x1", "y1", "x2", "y2", "offset",
        "stop-color", "gradientUnits", "gradientTransform", "patternUnits", "patternTransform",
        "font-size", "font-family", "textLength", "lengthAdjust", "startOffset", "pathLength", "dx", "dy",
        "rotate", "values", "dur", "begin", "repeatCount", "attributeName", "from", "to", "by",
        "keyTimes", "keySplines", "numOctaves", "baseFrequency", "kernelMatrix", "order", "scale",
        "radius", "xml:space", "xml:lang", "systemLanguage", "requiredExtensions", "writing-mode",
        "direction", "unicode-bidi", "opacity", "mix-blend-mode", "orient", "refX", "markerWidth"
    };

    private static readonly string[] Values =
    {
        "0", "-0", "-1", "1e38", "-1e38", "1e-45", "3.4e38", "NaN", "Infinity", "-Infinity",
        "99999999999", "1%", "1000000%", "-100%", "1em", "1ex", "1vw", "100vmax", "calc(1px*999999)",
        "var(--x)", "var(--x, var(--x))", "url(#a)", "url(#self)", "url(#g)", "url(#f)", "none",
        "inherit", "currentColor", "#", "#fff", "rgb(300,-1,0)", "hsl(1e9 50% 50%)",
        "M0 0L1e38 1e38Z", "M0,0 A1e30 1e30 0 1 1 1,1", "M0 0 " + string.Concat(Enumerable.Repeat("l1 1 ", 200)),
        "0 0 0 0", "0 0 -1 -1", "xMidYMid slice", "translate(1e38) scale(1e-38)",
        "matrix(0 0 0 0 0 0)", "rotate(NaN)", "skewX(90)", "1 1 1 1 1 1 1 1 1", "&amp;", "&#x0;",
        "&#xFFFF;", "&#xD800;", "&unknown;", new string('A', 1024), "SourceGraphic", "BackgroundImage",
        "indefinite", "0;1;0", "0 0 1 1", "data:image/svg+xml,<svg xmlns='http://www.w3.org/2000/svg'/>"
    };

    private static readonly string[] Tokens =
    {
        "<", ">", "/>", "</g>", "<g>", "<!--", "-->", "<![CDATA[", "]]>", "<?xml ?>", "<!DOCTYPE svg>",
        "&", "&lt;", "&#65;", "=", "\"", "'", "\0", "￾", "\uD800", "‮", "xmlns=''",
        "xmlns:x='urn:x'", "x:rect", "id='a'", "href='#a'", "style='fill:red'", ";", "{", "}", "@import url(x);"
    };

    private readonly Random _random;

    public SvgMutator(int seed)
    {
        _random = new Random(seed);
    }

    public Random Random => _random;

    public string Next(IReadOnlyList<string> seeds)
    {
        string text = seeds[_random.Next(seeds.Count)];
        int mutations = 1 + _random.Next(4);
        for (int i = 0; i < mutations; i++)
        {
            text = Mutate(text);
            if (text.Length > MaxOutputChars)
            {
                text = text[..MaxOutputChars];
            }
        }
        return text;
    }

    public string Mutate(string text) => _random.Next(10) switch
    {
        0 => ReplaceChar(text),
        1 => Insert(text, Pick(Tokens)),
        2 => DeleteSpan(text),
        3 => DuplicateSpan(text),
        4 => NestGroups(text),
        5 => InjectAttribute(text),
        6 => AppendElement(text),
        7 => text.Length == 0 ? text : text[.._random.Next(text.Length)],
        8 => InjectReferenceCycle(text),
        _ => ReplaceValue(text)
    };

    public string RandomText(int maxLength)
    {
        int length = _random.Next(maxLength + 1);
        var builder = new StringBuilder(length);
        for (int i = 0; i < length; i++)
        {
            builder.Append(_random.Next(8) == 0
                ? (char)_random.Next(0x10000)
                : (char)_random.Next(0x20, 0x7F));
        }
        return builder.ToString();
    }

    public string Pick(string[] values) => values[_random.Next(values.Length)];

    public string PickValue() => Pick(Values);

    private string ReplaceChar(string text)
    {
        if (text.Length == 0) return RandomText(8);
        int index = _random.Next(text.Length);
        string replacement = Pick(Tokens);
        return text[..index] + replacement + text[(index + 1)..];
    }

    private string Insert(string text, string token)
    {
        int index = _random.Next(text.Length + 1);
        return text[..index] + token + text[index..];
    }

    private string DeleteSpan(string text)
    {
        if (text.Length == 0) return text;
        int start = _random.Next(text.Length);
        int length = Math.Min(text.Length - start, 1 + _random.Next(64));
        return text.Remove(start, length);
    }

    private string DuplicateSpan(string text)
    {
        if (text.Length == 0) return text;
        int start = _random.Next(text.Length);
        int length = Math.Min(text.Length - start, 1 + _random.Next(256));
        string span = text.Substring(start, length);
        int copies = 1 + _random.Next(_random.Next(4) == 0 ? 64 : 3);
        return Insert(text, string.Concat(Enumerable.Repeat(span, copies)));
    }

    private string NestGroups(string text)
    {
        int body = BodyStart(text);
        if (body < 0) return text;
        int depth = 1 + _random.Next(_random.Next(4) == 0 ? 400 : 40);
        string open = string.Concat(Enumerable.Repeat("<g transform='scale(1.01)'>", depth));
        string close = string.Concat(Enumerable.Repeat("</g>", depth));
        int end = text.LastIndexOf("</svg>", StringComparison.Ordinal);
        if (end < body) end = text.Length;
        return text[..body] + open + text[body..end] + close + text[end..];
    }

    private string InjectAttribute(string text)
    {
        int tag = FindStartTag(text);
        if (tag < 0) return text;
        int nameEnd = tag + 1;
        while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]) && text[nameEnd] is not ('>' or '/'))
        {
            nameEnd++;
        }
        string attribute = $" {Pick(Attributes)}='{PickValue().Replace("'", "&apos;", StringComparison.Ordinal)}'";
        return text[..nameEnd] + attribute + text[nameEnd..];
    }

    private string AppendElement(string text)
    {
        string element = Pick(Elements);
        var builder = new StringBuilder().Append('<').Append(element);
        int attributes = _random.Next(5);
        for (int i = 0; i < attributes; i++)
        {
            builder.Append(' ').Append(Pick(Attributes)).Append("='")
                   .Append(PickValue().Replace("'", "&apos;", StringComparison.Ordinal)).Append('\'');
        }
        builder.Append(_random.Next(2) == 0 ? "/>" : ">" + Pick(Values) + "</" + element + ">");
        int end = text.LastIndexOf("</svg>", StringComparison.Ordinal);
        return end < 0 ? text + builder : text[..end] + builder + text[end..];
    }

    private string InjectReferenceCycle(string text)
    {
        int end = text.LastIndexOf("</svg>", StringComparison.Ordinal);
        if (end < 0) return text;
        string kind = Pick(new[] { "use", "pattern", "linearGradient", "filter", "mask", "clipPath", "marker" });
        string cycle = kind == "use"
            ? "<g id='cy1'><use href='#cy2'/></g><g id='cy2'><use href='#cy1'/></g><use href='#cy1'/>"
            : $"<{kind} id='cy1' href='#cy2'><rect width='4' height='4' fill='url(#cy1)' filter='url(#cy2)'/></{kind}>" +
              $"<{kind} id='cy2' href='#cy1'/><rect width='9' height='9' fill='url(#cy1)' clip-path='url(#cy1)' mask='url(#cy2)'/>";
        return text[..end] + cycle + text[end..];
    }

    private string ReplaceValue(string text)
    {
        int quote = text.IndexOf('\'', _random.Next(text.Length + 1));
        if (quote < 0) return text;
        int close = text.IndexOf('\'', quote + 1);
        if (close < 0) return text;
        return text[..(quote + 1)] + PickValue().Replace("'", "&apos;", StringComparison.Ordinal) + text[close..];
    }

    private int FindStartTag(string text)
    {
        if (text.Length == 0) return -1;
        int from = _random.Next(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            int index = (from + i) % text.Length;
            if (text[index] == '<' && index + 1 < text.Length && char.IsLetter(text[index + 1]))
            {
                return index;
            }
        }
        return -1;
    }

    private static int BodyStart(string text)
    {
        int svg = text.IndexOf("<svg", StringComparison.Ordinal);
        if (svg < 0) return -1;
        int close = text.IndexOf('>', svg);
        return close < 0 ? -1 : close + 1;
    }
}
