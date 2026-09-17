using System.Text;

namespace FenBrowser.Media.Types;

/// <summary>
/// A parsed MIME type record (WHATWG MIME Sniffing §4.1 "MIME type representation").
/// </summary>
public sealed class MimeType
{
    private readonly Dictionary<string, string> _parameters;

    private MimeType(string type, string subtype, Dictionary<string, string> parameters)
    {
        Type = type;
        Subtype = subtype;
        _parameters = parameters;
    }

    /// <summary>ASCII-lowercase type, for example <c>video</c>.</summary>
    public string Type { get; }

    /// <summary>ASCII-lowercase subtype, for example <c>webm</c>.</summary>
    public string Subtype { get; }

    /// <summary><c>type/subtype</c>.</summary>
    public string Essence => Type + "/" + Subtype;

    /// <summary>Parameters in insertion order, names ASCII-lowercased, values as written.</summary>
    public IReadOnlyDictionary<string, string> Parameters => _parameters;

    public int ParameterCount => _parameters.Count;

    public string? GetParameter(string name) => _parameters.TryGetValue(name, out var value) ? value : null;

    /// <summary>§4.4 "parse a MIME type". Returns null for failure.</summary>
    public static MimeType? Parse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // 1. Remove leading and trailing HTTP whitespace.
        input = TrimHttpWhitespace(input);

        // 2. position = start of input.
        int position = 0;

        // 3–4. type: code points up to "/", non-empty HTTP token.
        string type = CollectUntil(input, ref position, '/');
        if (type.Length == 0 || !IsHttpToken(type))
            return null;

        // 5–6. Past the end means there was no "/"; otherwise skip it.
        if (position >= input.Length)
            return null;
        position++;

        // 7–9. subtype: code points up to ";", trailing HTTP whitespace removed, non-empty token.
        string subtype = TrimTrailingHttpWhitespace(CollectUntil(input, ref position, ';'));
        if (subtype.Length == 0 || !IsHttpToken(subtype))
            return null;

        // 10. New record with lowercased type and subtype.
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        var mimeType = new MimeType(type.ToLowerInvariant(), subtype.ToLowerInvariant(), parameters);

        // 11. Parameters.
        while (position < input.Length)
        {
            // 11.1 Skip ";".
            position++;

            // 11.2 Skip HTTP whitespace.
            while (position < input.Length && IsHttpWhitespace(input[position]))
                position++;

            // 11.3–11.4 parameterName up to ";" or "=", lowercased.
            int nameStart = position;
            while (position < input.Length && input[position] != ';' && input[position] != '=')
                position++;
            string parameterName = input[nameStart..position].ToLowerInvariant();

            // 11.5 A ";" here means a name without a value; otherwise skip "=".
            if (position < input.Length)
            {
                if (input[position] == ';')
                    continue;
                position++;
            }

            // 11.6 Nothing after the name.
            if (position >= input.Length)
                break;

            // 11.7–11.8 parameterValue.
            string parameterValue;
            if (input[position] == '"')
            {
                parameterValue = CollectHttpQuotedStringValue(input, ref position);
                CollectUntil(input, ref position, ';');
            }
            else
            {
                parameterValue = TrimTrailingHttpWhitespace(CollectUntil(input, ref position, ';'));
                if (parameterValue.Length == 0)
                    continue;
            }

            // 11.9 Keep the first valid occurrence of each name.
            if (parameterName.Length != 0
                && IsHttpToken(parameterName)
                && IsHttpQuotedStringTokens(parameterValue)
                && !parameters.ContainsKey(parameterName))
            {
                parameters[parameterName] = parameterValue;
            }
        }

        // 12.
        return mimeType;
    }

    /// <summary>§4.5 "serialize a MIME type".</summary>
    public override string ToString()
    {
        var sb = new StringBuilder(Essence);
        foreach (var (name, value) in _parameters)
        {
            sb.Append(';').Append(name).Append('=');
            if (value.Length == 0 || !IsHttpToken(value))
            {
                sb.Append('"');
                foreach (char c in value)
                {
                    if (c is '"' or '\\')
                        sb.Append('\\');
                    sb.Append(c);
                }

                sb.Append('"');
            }
            else
            {
                sb.Append(value);
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// WHATWG Fetch "collect an HTTP quoted string" with extract-value true.
    /// <paramref name="position"/> must point at the opening quote.
    /// </summary>
    private static string CollectHttpQuotedStringValue(string input, ref int position)
    {
        var value = new StringBuilder();
        position++; // skip the opening quote
        while (true)
        {
            while (position < input.Length && input[position] != '"' && input[position] != '\\')
                value.Append(input[position++]);

            if (position >= input.Length)
                break;

            char quoteOrBackslash = input[position++];
            if (quoteOrBackslash == '\\')
            {
                if (position >= input.Length)
                {
                    value.Append('\\');
                    break;
                }

                value.Append(input[position++]);
            }
            else
            {
                break; // closing quote
            }
        }

        return value.ToString();
    }

    private static string CollectUntil(string input, ref int position, char stop)
    {
        int start = position;
        while (position < input.Length && input[position] != stop)
            position++;
        return input[start..position];
    }

    internal static bool IsHttpWhitespace(char c) => c is '\n' or '\r' or '\t' or ' ';

    internal static string TrimHttpWhitespace(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsHttpWhitespace(s[start]))
            start++;
        while (end > start && IsHttpWhitespace(s[end - 1]))
            end--;
        return s[start..end];
    }

    private static string TrimTrailingHttpWhitespace(string s)
    {
        int end = s.Length;
        while (end > 0 && IsHttpWhitespace(s[end - 1]))
            end--;
        return s[..end];
    }

    /// <summary>HTTP token code points: RFC 9110 tchar.</summary>
    internal static bool IsHttpToken(string s)
    {
        foreach (char c in s)
        {
            bool ok = c is '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~'
                || char.IsAsciiLetterOrDigit(c);
            if (!ok)
                return false;
        }

        return true;
    }

    /// <summary>HTTP quoted-string token code points: U+0009, U+0020–U+007E, U+0080–U+00FF.</summary>
    private static bool IsHttpQuotedStringTokens(string s)
    {
        foreach (char c in s)
        {
            if (!(c == '\t' || (c >= ' ' && c <= '~') || (c >= '' && c <= 'ÿ')))
                return false;
        }

        return true;
    }
}
