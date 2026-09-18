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

    public static VttNode Parse(string text, string cueLanguage = "")
    {
        ArgumentNullException.ThrowIfNull(text);
        var root = new VttNode(VttNodeKind.Root) { Language = cueLanguage };
        var open = new Stack<VttNode>();
        open.Push(root);
        var languages = new Stack<string>();
        languages.Push(cueLanguage);

        int position = 0;
        while (position < text.Length)
        {
            var token = NextToken(text, ref position);
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
    private static Token NextToken(string input, ref int position)
    {
        var result = new StringBuilder();
        var buffer = new StringBuilder();
        var classes = new List<string>();
        int state = 0; // 0 data, 1 escape, 2 tag, 3 start tag, 4 start tag class, 5 start tag annotation, 6 end tag, 7 timestamp tag

        while (true)
        {
            int c = position < input.Length ? input[position] : -1;
            switch (state)
            {
                case 0: // data
                    if (c == '&')
                    {
                        buffer.Clear().Append('&');
                        state = 1;
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

                case 1: // HTML character reference in data
                    if (c == ';')
                    {
                        buffer.Append(';');
                        result.Append(DecodeReference(buffer.ToString()));
                        state = 0;
                    }
                    else if (c == -1 || c == '<' || c == '&' || char.IsWhiteSpace((char)c))
                    {
                        result.Append(buffer);
                        state = 0;
                        continue; // reprocess c in the data state
                    }
                    else
                    {
                        buffer.Append((char)c);
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
                        int at = position;
                        int end = input.IndexOf(';', at);
                        int stop = end < 0 ? -1 : end;
                        if (stop > at && !input.AsSpan(at + 1, stop - at - 1).ContainsAny(' ', '<', '&'))
                        {
                            buffer.Append(DecodeReference(input[at..(stop + 1)]));
                            position = stop + 1;
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

    /// <summary>An HTML character reference (named or numeric, terminated by ';'); anything unknown stays literal.</summary>
    private static string DecodeReference(string reference)
    {
        string decoded = WebUtility.HtmlDecode(reference);
        return decoded == reference ? reference : decoded;
    }
}
