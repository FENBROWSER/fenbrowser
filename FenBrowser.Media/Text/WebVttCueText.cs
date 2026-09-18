using System.Net;
using System.Text;

namespace FenBrowser.Media.Text;

/// <summary>WebVTT §3.3 cue text node kinds ("WebVTT Internal Node Objects" and leaves).</summary>
public enum VttNodeKind
{
    Root,
    Class,
    Italic,
    Bold,
    Underline,
    Ruby,
    RubyText,
    Voice,
    Language,
    Text,
    Timestamp,
}

/// <summary>One node of a parsed cue text tree.</summary>
public sealed class VttNode
{
    public VttNode(VttNodeKind kind)
    {
        Kind = kind;
    }

    public VttNodeKind Kind { get; }

    /// <summary>Class names from the tag (<c>&lt;c.a.b&gt;</c>), for internal nodes.</summary>
    public List<string> Classes { get; } = [];

    /// <summary>The voice name or language tag from the tag's annotation, for voice and language nodes.</summary>
    public string Annotation { get; set; } = "";

    /// <summary>The applicable language: the nearest enclosing language node's tag, else the cue's.</summary>
    public string Language { get; set; } = "";

    /// <summary>Text for text nodes; the timestamp for timestamp nodes.</summary>
    public string Text { get; set; } = "";

    public MediaTime Timestamp { get; set; }

    public List<VttNode> Children { get; } = [];

    /// <summary>The visible text of the subtree, as a screen reader or a plain-text renderer sees it.</summary>
    public string PlainText()
    {
        var sb = new StringBuilder();
        var pending = new Stack<VttNode>();
        pending.Push(this);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node.Kind == VttNodeKind.Text)
                sb.Append(node.Text);
            for (int i = node.Children.Count - 1; i >= 0; i--)
                pending.Push(node.Children[i]);
        }

        return sb.ToString();
    }
}

/// <summary>
/// The WebVTT cue text parser (§6.4 "WebVTT cue text parsing rules" over the §6.5
/// tokenizer): tags for class, italic, bold, underline, ruby, ruby text, voice and
/// language, timestamp tags, and the named and numeric character references of HTML.
/// Unknown tags are dropped, mismatched end tags ignored, unclosed tags closed at the end,
/// as the specification says.
/// </summary>
public static class WebVttCueText
{
    /// <summary>
    /// Open tags beyond this depth are dropped like unknown tags. The specification has no
    /// limit, but every consumer of the tree (the DOM fragment, the renderer) walks it
    /// recursively, and no real caption nests markup this deep.
    /// </summary>
    public const int MaxDepth = 64;

    public static VttNode Parse(string text, string cueLanguage = "", IHtmlNamedCharacterReferences? references = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        references ??= MinimalHtmlNamedCharacterReferences.Instance;
        var root = new VttNode(VttNodeKind.Root) { Language = cueLanguage };
        var open = new Stack<VttNode>();
        open.Push(root);
        var languages = new Stack<string>();
        languages.Push(cueLanguage);

        if (text.Length == 0)
        {
            // The tokenizer's data state returns its (empty) string at end of input, so an
            // empty cue is one empty text node; getCueAsHTML() relies on it.
            root.Children.Add(new VttNode(VttNodeKind.Text) { Text = string.Empty, Language = cueLanguage });
            return root;
        }

        int position = 0;
        while (position < text.Length)
        {
            var token = NextToken(text, ref position, references);
            var current = open.Peek();
            switch (token.Kind)
            {
                case TokenKind.Text:
                    current.Children.Add(new VttNode(VttNodeKind.Text) { Text = token.Value, Language = languages.Peek() });
                    break;

                case TokenKind.StartTag:
                {
                    VttNodeKind? kind = token.Value switch
                    {
                        "c" => VttNodeKind.Class,
                        "i" => VttNodeKind.Italic,
                        "b" => VttNodeKind.Bold,
                        "u" => VttNodeKind.Underline,
                        "ruby" => VttNodeKind.Ruby,
                        "rt" => current.Kind == VttNodeKind.Ruby ? VttNodeKind.RubyText : null,
                        "v" => VttNodeKind.Voice,
                        "lang" => VttNodeKind.Language,
                        _ => null,
                    };
                    if (kind is null || open.Count > MaxDepth)
                        break;

                    var node = new VttNode(kind.Value) { Language = languages.Peek() };
                    node.Classes.AddRange(token.Classes);
                    if (kind == VttNodeKind.Voice)
                        node.Annotation = token.Annotation;
                    if (kind == VttNodeKind.Language)
                    {
                        node.Annotation = token.Annotation;
                        languages.Push(token.Annotation);
                        node.Language = token.Annotation;
                    }

                    current.Children.Add(node);
                    open.Push(node);
                    break;
                }

                case TokenKind.EndTag:
                {
                    var currentKind = current.Kind;
                    string name = token.Value;
                    bool matches = (name, currentKind) switch
                    {
                        ("c", VttNodeKind.Class) => true,
                        ("i", VttNodeKind.Italic) => true,
                        ("b", VttNodeKind.Bold) => true,
                        ("u", VttNodeKind.Underline) => true,
                        ("ruby", VttNodeKind.Ruby) => true,
                        ("rt", VttNodeKind.RubyText) => true,
                        ("v", VttNodeKind.Voice) => true,
                        ("lang", VttNodeKind.Language) => true,
                        _ => false,
                    };
                    if (matches)
                    {
                        Pop(open, languages);
                    }
                    else if (name == "ruby" && currentKind == VttNodeKind.RubyText)
                    {
                        // §6.4: a ruby end tag also closes an open ruby text.
                        Pop(open, languages);
                        if (open.Peek().Kind == VttNodeKind.Ruby)
                            Pop(open, languages);
                    }

                    break;
                }

                case TokenKind.Timestamp:
                {
                    int at = 0;
                    if (WebVttParser.TryParseTimestamp(token.Value, ref at, out double seconds) && at == token.Value.Length)
                        current.Children.Add(new VttNode(VttNodeKind.Timestamp) { Text = token.Value, Timestamp = MediaTime.FromSeconds(seconds), Language = languages.Peek() });
                    break;
                }
            }
        }

        return root;
    }

    private static void Pop(Stack<VttNode> open, Stack<string> languages)
    {
        if (open.Count <= 1)
            return;
        var closed = open.Pop();
        if (closed.Kind == VttNodeKind.Language && languages.Count > 1)
            languages.Pop();
    }

    private enum TokenKind
    {
        Text,
        StartTag,
        EndTag,
        Timestamp,
    }

    private readonly record struct Token(TokenKind Kind, string Value, List<string> Classes, string Annotation);

    /// <summary>§6.5 "WebVTT cue text tokenizer": one token from <paramref name="position"/>.</summary>
    private static Token NextToken(string input, ref int position, IHtmlNamedCharacterReferences references)
    {
        var result = new StringBuilder();
        var buffer = new StringBuilder();
        var classes = new List<string>();
        int state = 0; // 0 data, 2 tag, 3 start tag, 4 start tag class, 5 start tag annotation, 6 end tag, 7 timestamp tag

        while (true)
        {
            int c = position < input.Length ? input[position] : -1;
            switch (state)
            {
                case 0: // data
                    if (c == '&')
                    {
                        // "Attempt to consume an HTML character reference"; nothing consumed
                        // leaves the ampersand as text.
                        if (TryConsumeCharacterReference(input, position, references, out var replacement, out var consumed))
                        {
                            result.Append(replacement);
                            position += consumed;
                            continue;
                        }

                        result.Append('&');
                    }
                    else if (c == '<')
                    {
                        if (result.Length == 0)
                        {
                            state = 2;
                        }
                        else
                        {
                            return new Token(TokenKind.Text, result.ToString(), classes, "");
                        }
                    }
                    else if (c == -1)
                    {
                        return new Token(TokenKind.Text, result.ToString(), classes, "");
                    }
                    else
                    {
                        result.Append((char)c);
                    }
                    break;

                case 2: // tag
                    if (c is '\t' or '\n' or '\f' or ' ')
                    {
                        state = 5;
                    }
                    else if (c == '.')
                    {
                        state = 4;
                    }
                    else if (c == '/')
                    {
                        state = 6;
                    }
                    else if (c is >= '0' and <= '9')
                    {
                        result.Append((char)c);
                        state = 7;
                    }
                    else if (c == '>' || c == -1)
                    {
                        if (c == '>')
                            position++;
                        return new Token(TokenKind.StartTag, "", classes, "");
                    }
                    else
                    {
                        result.Append((char)c);
                        state = 3;
                    }
                    break;

                case 3: // start tag
                    if (c is '\t' or '\f' or ' ')
                    {
                        state = 5;
                    }
                    else if (c == '\n')
                    {
                        buffer.Clear().Append('\n');
                        state = 5;
                    }
                    else if (c == '.')
                    {
                        state = 4;
                    }
                    else if (c == '>' || c == -1)
                    {
                        if (c == '>')
                            position++;
                        return new Token(TokenKind.StartTag, result.ToString(), classes, "");
                    }
                    else
                    {
                        result.Append((char)c);
                    }
                    break;

                case 4: // start tag class
                    if (c is '\t' or '\f' or ' ')
                    {
                        if (buffer.Length > 0)
                            classes.Add(buffer.ToString());
                        buffer.Clear();
                        state = 5;
                    }
                    else if (c == '\n')
                    {
                        if (buffer.Length > 0)
                            classes.Add(buffer.ToString());
                        buffer.Clear().Append('\n');
                        state = 5;
                    }
                    else if (c == '.')
                    {
                        if (buffer.Length > 0)
                            classes.Add(buffer.ToString());
                        buffer.Clear();
                    }
                    else if (c == '>' || c == -1)
                    {
                        if (c == '>')
                            position++;
                        if (buffer.Length > 0)
                            classes.Add(buffer.ToString());
                        return new Token(TokenKind.StartTag, result.ToString(), classes, "");
                    }
                    else
                    {
                        buffer.Append((char)c);
                    }
                    break;

                case 5: // start tag annotation
                    if (c == '>' || c == -1)
                    {
                        if (c == '>')
                            position++;
                        string annotation = string.Join(' ', buffer.ToString().Split([' ', '\t', '\n', '\f', '\r'], StringSplitOptions.RemoveEmptyEntries));
                        return new Token(TokenKind.StartTag, result.ToString(), classes, annotation);
                    }
                    else if (c == '&')
                    {
                        // References in annotations decode the same way.
                        if (TryConsumeCharacterReference(input, position, references, out var replacement, out var consumed))
                        {
                            buffer.Append(replacement);
                            position += consumed;
                            continue;
                        }

                        buffer.Append('&');
                    }
                    else
                    {
                        buffer.Append((char)c);
                    }
                    break;

                case 6: // end tag
                    if (c == '>' || c == -1)
                    {
                        if (c == '>')
                            position++;
                        return new Token(TokenKind.EndTag, result.ToString(), classes, "");
                    }

                    result.Append((char)c);
                    break;

                case 7: // timestamp tag
                    if (c == '>' || c == -1)
                    {
                        if (c == '>')
                            position++;
                        return new Token(TokenKind.Timestamp, result.ToString(), classes, "");
                    }

                    result.Append((char)c);
                    break;
            }

            position++;
        }
    }

    /// <summary>
    /// HTML §13.2.5.72–80 "character reference" (not in an attribute): at the ampersand at
    /// <paramref name="position"/>, a numeric reference (<c>&amp;#32;</c>, <c>&amp;#x20;</c>,
    /// with the C1 replacements and U+FFFD for invalid code points) or the longest named
    /// reference the table knows, which may omit its semicolon only for the legacy names.
    /// </summary>
    private static bool TryConsumeCharacterReference(string input, int position, IHtmlNamedCharacterReferences references, out string replacement, out int consumed)
    {
        replacement = string.Empty;
        consumed = 0;
        int at = position + 1;
        if (at >= input.Length)
            return false;

        if (input[at] == '#')
        {
            int cursor = at + 1;
            bool hex = cursor < input.Length && (input[cursor] == 'x' || input[cursor] == 'X');
            if (hex)
                cursor++;
            int digitsStart = cursor;
            long value = 0;
            while (cursor < input.Length)
            {
                int digit = hex ? HexValue(input[cursor]) : (char.IsAsciiDigit(input[cursor]) ? input[cursor] - '0' : -1);
                if (digit < 0)
                    break;
                if (value <= 0x10FFFF)
                    value = value * (hex ? 16 : 10) + digit;
                cursor++;
            }

            if (cursor == digitsStart)
                return false; // "&#" or "&#x" with no digits: not a reference
            if (cursor < input.Length && input[cursor] == ';')
                cursor++;
            replacement = NumericReplacement(value);
            consumed = cursor - position;
            return true;
        }

        if (references.TryMatchLongest(input, at, out var named, out int length))
        {
            replacement = named;
            consumed = length + 1;
            return true;
        }

        return false;
    }

    private static int HexValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>HTML §13.2.5.80 "numeric character reference end state".</summary>
    private static string NumericReplacement(long value)
    {
        if (value == 0 || value > 0x10FFFF || value is >= 0xD800 and <= 0xDFFF)
            return "�";
        if (value is >= 0x80 and <= 0x9F && s_c1Replacements.TryGetValue((int)value, out var c1))
            return ((char)c1).ToString();
        return char.ConvertFromUtf32((int)value);
    }

    private static readonly Dictionary<int, int> s_c1Replacements = new()
    {
        [0x80] = 0x20AC, [0x82] = 0x201A, [0x83] = 0x0192, [0x84] = 0x201E, [0x85] = 0x2026, [0x86] = 0x2020, [0x87] = 0x2021,
        [0x88] = 0x02C6, [0x89] = 0x2030, [0x8A] = 0x0160, [0x8B] = 0x2039, [0x8C] = 0x0152, [0x8E] = 0x017D, [0x91] = 0x2018,
        [0x92] = 0x2019, [0x93] = 0x201C, [0x94] = 0x201D, [0x95] = 0x2022, [0x96] = 0x2013, [0x97] = 0x2014, [0x98] = 0x02DC,
        [0x99] = 0x2122, [0x9A] = 0x0161, [0x9B] = 0x203A, [0x9C] = 0x0153, [0x9E] = 0x017E, [0x9F] = 0x0178,
    };
}

/// <summary>
/// The HTML named character reference table the cue text parser consults. The browser
/// supplies the full table; <see cref="MinimalHtmlNamedCharacterReferences"/> is the
/// dependency-free default.
/// </summary>
public interface IHtmlNamedCharacterReferences
{
    /// <summary>
    /// Matches the longest reference name (including its semicolon when the name has one)
    /// starting at <paramref name="position"/>, the character after the ampersand.
    /// </summary>
    bool TryMatchLongest(string input, int position, out string replacement, out int length);
}

/// <summary>
/// The HTML 4 names <see cref="System.Net.WebUtility"/> knows (semicolon-terminated), plus the
/// legacy names HTML lets stand without a semicolon.
/// </summary>
public sealed class MinimalHtmlNamedCharacterReferences : IHtmlNamedCharacterReferences
{
    public static MinimalHtmlNamedCharacterReferences Instance { get; } = new();

    private const int MaxNameLength = 32;

    private static readonly HashSet<string> s_legacy = new(StringComparer.Ordinal)
    {
        "AElig", "AMP", "Aacute", "Acirc", "Agrave", "Aring", "Atilde", "Auml", "COPY", "Ccedil", "ETH", "Eacute", "Ecirc",
        "Egrave", "Euml", "GT", "Iacute", "Icirc", "Igrave", "Iuml", "LT", "Ntilde", "Oacute", "Ocirc", "Ograve", "Oslash",
        "Otilde", "Ouml", "QUOT", "REG", "THORN", "Uacute", "Ucirc", "Ugrave", "Uuml", "Yacute", "aacute", "acirc", "acute",
        "aelig", "agrave", "amp", "aring", "atilde", "auml", "brvbar", "ccedil", "cedil", "cent", "copy", "curren", "deg",
        "divide", "eacute", "ecirc", "egrave", "eth", "euml", "frac12", "frac14", "frac34", "gt", "iacute", "icirc", "iexcl",
        "igrave", "iquest", "iuml", "laquo", "lt", "macr", "micro", "middot", "nbsp", "not", "ntilde", "oacute", "ocirc",
        "ograve", "ordf", "ordm", "oslash", "otilde", "ouml", "para", "plusmn", "pound", "quot", "raquo", "reg", "sect",
        "shy", "sup1", "sup2", "sup3", "szlig", "thorn", "times", "uacute", "ucirc", "ugrave", "uml", "uuml", "yacute", "yen", "yuml",
    };

    public bool TryMatchLongest(string input, int position, out string replacement, out int length)
    {
        replacement = string.Empty;
        length = 0;
        int max = Math.Min(MaxNameLength, input.Length - position);
        for (int candidate = max; candidate > 0; candidate--)
        {
            string name = input.Substring(position, candidate);
            if (name[^1] == ';')
            {
                string decoded = WebUtility.HtmlDecode("&" + name);
                if (decoded != "&" + name)
                {
                    replacement = decoded;
                    length = candidate;
                    return true;
                }
            }
            else if (s_legacy.Contains(name))
            {
                replacement = WebUtility.HtmlDecode("&" + name.ToLowerInvariant() + ";");
                if (replacement == "&" + name.ToLowerInvariant() + ";")
                    replacement = WebUtility.HtmlDecode("&" + name + ";");
                length = candidate;
                return true;
            }
        }

        return false;
    }
}
