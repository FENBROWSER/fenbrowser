using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using FenBrowser.Core.Parsing;

var options = BenchmarkOptions.Parse(args);
string input = File.ReadAllText(options.InputPath);
EnsureAscii(input);
Func<string, TokenizerResult> tokenize = options.Implementation switch
{
    BenchmarkImplementation.Production => RunProductionTokenizer,
    BenchmarkImplementation.CommonStatePort => CommonStateTokenizer.Run,
    _ => throw new UnreachableException()
};

for (int iteration = 0; iteration < options.WarmupIterations; iteration++)
{
    _ = tokenize(input);
}

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

var elapsedMilliseconds = new double[options.Iterations];
var allocatedBytes = new long[options.Iterations];
TokenizerResult? expected = null;

for (int iteration = 0; iteration < options.Iterations; iteration++)
{
    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    long started = Stopwatch.GetTimestamp();
    TokenizerResult result = tokenize(input);
    elapsedMilliseconds[iteration] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    allocatedBytes[iteration] = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

    expected ??= result;
    if (result != expected.Value)
    {
        throw new InvalidOperationException("Tokenizer output changed between benchmark iterations.");
    }
}

Array.Sort(elapsedMilliseconds);
Array.Sort(allocatedBytes);
double medianMilliseconds = elapsedMilliseconds[elapsedMilliseconds.Length / 2];
long medianAllocatedBytes = allocatedBytes[allocatedBytes.Length / 2];
double throughputMiBPerSecond =
    (input.Length / (1024d * 1024d)) / (medianMilliseconds / 1000d);

var report = new
{
    benchmark_id = options.Implementation == BenchmarkImplementation.Production
        ? "csharp-production"
        : "csharp-common",
    language = "csharp",
    implementation = options.Implementation == BenchmarkImplementation.Production
        ? "FenBrowser.Core.Parsing.HtmlTokenizer"
        : "benchmark common-state port",
    variant = options.Implementation == BenchmarkImplementation.Production
        ? "production"
        : "common-state-port",
    input_bytes = input.Length,
    iterations = options.Iterations,
    warmup_iterations = options.WarmupIterations,
    token_count = expected!.Value.TokenCount,
    checksum = expected.Value.Checksum.ToString("x16", CultureInfo.InvariantCulture),
    median_ms = Math.Round(medianMilliseconds, 6),
    min_ms = Math.Round(elapsedMilliseconds[0], 6),
    max_ms = Math.Round(elapsedMilliseconds[^1], 6),
    throughput_mib_per_second = Math.Round(throughputMiBPerSecond, 3),
    allocated_bytes_per_iteration = medianAllocatedBytes
};

Console.WriteLine(JsonSerializer.Serialize(report));

static TokenizerResult RunProductionTokenizer(string input)
{
    var pool = new HtmlTokenPool();
    var hash = new TokenHash();
    long tokenCount = 0;

    var tokenizer = new HtmlTokenizer(input, pool)
    {
        MaxInputLengthChars = int.MaxValue,
        MaxTokenEmissions = int.MaxValue
    };

    foreach (var token in tokenizer.Tokenize())
    {
        hash.AddByte((byte)token.Type);
        switch (token)
        {
            case StartTagToken tag:
                hash.AddAscii(tag.TagName);
                hash.AddByte(tag.SelfClosing ? (byte)1 : (byte)0);
                hash.AddUInt64((ulong)tag.Attributes.Count);
                foreach (HtmlAttribute attribute in tag.Attributes)
                {
                    hash.AddAscii(attribute.Name);
                    hash.AddAscii(attribute.Value);
                }
                break;

            case EndTagToken tag:
                hash.AddAscii(tag.TagName);
                hash.AddByte(tag.SelfClosing ? (byte)1 : (byte)0);
                hash.AddUInt64(0);
                break;

            case CharacterToken character:
                hash.AddAscii(character.Data);
                break;

            case CommentToken comment:
                hash.AddAscii(comment.Data);
                break;

            case DoctypeToken doctype:
                hash.AddAscii(doctype.Name);
                hash.AddAscii(doctype.PublicIdentifier);
                hash.AddAscii(doctype.SystemIdentifier);
                hash.AddByte(doctype.ForceQuirks ? (byte)1 : (byte)0);
                break;
        }

        tokenCount++;
    }

    return new TokenizerResult(tokenCount, hash.Value);
}

static void EnsureAscii(string input)
{
    foreach (char value in input)
    {
        if (value > 0x7f)
        {
            throw new InvalidDataException(
                "The cross-language benchmark corpus must remain ASCII.");
        }
    }
}

readonly record struct TokenizerResult(long TokenCount, ulong Checksum);

sealed class TokenHash
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    public ulong Value { get; private set; } = FnvOffsetBasis;

    public void AddByte(byte value)
    {
        Value ^= value;
        Value *= FnvPrime;
    }

    public void AddUInt64(ulong value)
    {
        for (int index = 0; index < sizeof(ulong); index++)
        {
            AddByte((byte)(value >> (index * 8)));
        }
    }

    public void AddAscii(string? value)
    {
        value ??= string.Empty;
        AddUInt64((ulong)value.Length);
        foreach (char character in value)
        {
            if (character > 0x7f)
            {
                throw new InvalidDataException(
                    "Tokenizer emitted non-ASCII output for the ASCII benchmark corpus.");
            }

            AddByte((byte)character);
        }
    }
}

enum BenchmarkImplementation
{
    Production,
    CommonStatePort
}

sealed record BenchmarkOptions(
    string InputPath,
    int Iterations,
    int WarmupIterations,
    BenchmarkImplementation Implementation)
{
    public static BenchmarkOptions Parse(string[] args)
    {
        string? inputPath = null;
        int iterations = 7;
        int warmupIterations = 2;
        BenchmarkImplementation implementation = BenchmarkImplementation.Production;

        for (int index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--input":
                    inputPath = ReadValue(args, ref index, "--input");
                    break;
                case "--iterations":
                    iterations = ParseNonNegative(ReadValue(args, ref index, "--iterations"), "--iterations");
                    break;
                case "--warmup":
                    warmupIterations = ParseNonNegative(ReadValue(args, ref index, "--warmup"), "--warmup");
                    break;
                case "--implementation":
                    implementation = ReadValue(args, ref index, "--implementation") switch
                    {
                        "production" => BenchmarkImplementation.Production,
                        "common" => BenchmarkImplementation.CommonStatePort,
                        var value => throw new ArgumentException(
                            $"--implementation must be 'production' or 'common', not '{value}'.")
                    };
                    break;
                default:
                    throw new ArgumentException($"Unknown argument '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("--input is required.");
        }

        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(iterations), "At least one measured iteration is required.");
        }

        return new BenchmarkOptions(
            Path.GetFullPath(inputPath),
            iterations,
            warmupIterations,
            implementation);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
        {
            throw new ArgumentException($"{option} requires a value.");
        }

        return args[index];
    }

    private static int ParseNonNegative(string value, string option)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) ||
            parsed < 0)
        {
            throw new ArgumentException($"{option} must be a non-negative integer.");
        }

        return parsed;
    }
}

sealed class CommonStateTokenizer
{
    private readonly string _input;
    private readonly TokenHash _hash = new();
    private int _position;
    private int _line = 1;
    private int _column = 1;
    private bool _previousWasCarriageReturn;
    private long _tokenCount;

    private CommonStateTokenizer(string input)
    {
        _input = input;
    }

    public static TokenizerResult Run(string input)
    {
        var tokenizer = new CommonStateTokenizer(input);
        tokenizer.Tokenize();
        return new TokenizerResult(tokenizer._tokenCount, tokenizer._hash.Value);
    }

    private void Tokenize()
    {
        while (_position < _input.Length)
        {
            switch (_input[_position])
            {
                case '&':
                    var reference = ParseCharacterReference(_input.AsSpan(), _position);
                    AdvanceTo(_position + reference.Consumed);
                    EmitCharacter(reference.Value);
                    break;

                case '<':
                    ConsumeLessThan();
                    break;

                default:
                    int start = _position;
                    int end = start;
                    while (end < _input.Length && _input[end] is not ('&' or '<'))
                    {
                        end++;
                    }

                    string value = _input.Substring(start, end - start);
                    AdvanceTo(end);
                    EmitCharacter(value);
                    break;
            }
        }

        EmitType(5);
    }

    private void ConsumeLessThan()
    {
        Advance();
        if (_position >= _input.Length)
        {
            EmitCharacter("<");
            return;
        }

        if (StartsWith("!--", StringComparison.Ordinal))
        {
            ConsumeComment();
        }
        else if (StartsWith("!doctype", StringComparison.OrdinalIgnoreCase))
        {
            ConsumeDoctype();
        }
        else if (_input[_position] == '/')
        {
            ConsumeEndTag();
        }
        else if (IsAsciiAlpha(_input[_position]))
        {
            ConsumeStartTag();
        }
        else
        {
            EmitCharacter("<");
        }
    }

    private void ConsumeComment()
    {
        AdvanceTo(_position + 3);
        int dataStart = _position;
        int end = _input.IndexOf("-->", dataStart, StringComparison.Ordinal);
        string data;
        if (end < 0)
        {
            data = _input[dataStart..];
            AdvanceTo(_input.Length);
        }
        else
        {
            data = _input.Substring(dataStart, end - dataStart);
            AdvanceTo(end + 3);
        }

        EmitType(3);
        _hash.AddAscii(data);
    }

    private void ConsumeDoctype()
    {
        AdvanceTo(_position + 8);
        SkipWhitespace();
        int nameStart = _position;
        while (_position < _input.Length &&
               !IsAsciiWhitespace(_input[_position]) &&
               _input[_position] != '>')
        {
            Advance();
        }

        string name = _input[nameStart.._position].ToLowerInvariant();
        while (_position < _input.Length && _input[_position] != '>')
        {
            Advance();
        }
        if (_position < _input.Length)
        {
            Advance();
        }

        EmitType(0);
        _hash.AddAscii(name);
        _hash.AddAscii(string.Empty);
        _hash.AddAscii(string.Empty);
        _hash.AddByte(0);
    }

    private void ConsumeEndTag()
    {
        Advance();
        int nameStart = _position;
        while (_position < _input.Length && IsTagNameCharacter(_input[_position]))
        {
            Advance();
        }
        string name = _input[nameStart.._position].ToLowerInvariant();

        while (_position < _input.Length && _input[_position] != '>')
        {
            Advance();
        }
        if (_position < _input.Length)
        {
            Advance();
        }

        EmitTag(2, name, selfClosing: false, []);
    }

    private void ConsumeStartTag()
    {
        int nameStart = _position;
        while (_position < _input.Length && IsTagNameCharacter(_input[_position]))
        {
            Advance();
        }
        string name = _input[nameStart.._position].ToLowerInvariant();
        var attributes = new List<CommonAttribute>();
        bool selfClosing = false;

        while (true)
        {
            SkipWhitespace();
            if (_position >= _input.Length)
            {
                break;
            }

            if (_input[_position] == '>')
            {
                Advance();
                break;
            }

            if (_input[_position] == '/' &&
                _position + 1 < _input.Length &&
                _input[_position + 1] == '>')
            {
                AdvanceTo(_position + 2);
                selfClosing = true;
                break;
            }

            int attributeNameStart = _position;
            while (_position < _input.Length &&
                   !IsAsciiWhitespace(_input[_position]) &&
                   _input[_position] is not ('=' or '/' or '>'))
            {
                Advance();
            }

            if (attributeNameStart == _position)
            {
                Advance();
                continue;
            }

            string attributeName = _input[attributeNameStart.._position].ToLowerInvariant();
            SkipWhitespace();
            string attributeValue = string.Empty;
            if (_position < _input.Length && _input[_position] == '=')
            {
                Advance();
                SkipWhitespace();
                attributeValue = ConsumeAttributeValue();
            }

            if (!attributes.Exists(
                    existing => string.Equals(
                        existing.Name,
                        attributeName,
                        StringComparison.OrdinalIgnoreCase)))
            {
                attributes.Add(new CommonAttribute(attributeName, attributeValue));
            }
        }

        EmitTag(1, name, selfClosing, attributes);
    }

    private string ConsumeAttributeValue()
    {
        if (_position >= _input.Length)
        {
            return string.Empty;
        }

        char quote = _input[_position];
        if (quote is '\'' or '"')
        {
            Advance();
            int start = _position;
            while (_position < _input.Length && _input[_position] != quote)
            {
                Advance();
            }
            string value = DecodeCharacterReferences(_input.AsSpan(start, _position - start));
            if (_position < _input.Length)
            {
                Advance();
            }
            return value;
        }

        int unquotedStart = _position;
        while (_position < _input.Length &&
               !IsAsciiWhitespace(_input[_position]) &&
               _input[_position] != '>')
        {
            Advance();
        }
        return DecodeCharacterReferences(_input.AsSpan(unquotedStart, _position - unquotedStart));
    }

    private void EmitTag(
        byte tokenType,
        string name,
        bool selfClosing,
        IReadOnlyList<CommonAttribute> attributes)
    {
        EmitType(tokenType);
        _hash.AddAscii(name);
        _hash.AddByte(selfClosing ? (byte)1 : (byte)0);
        _hash.AddUInt64((ulong)attributes.Count);
        foreach (CommonAttribute attribute in attributes)
        {
            _hash.AddAscii(attribute.Name);
            _hash.AddAscii(attribute.Value);
        }
    }

    private void EmitCharacter(string value)
    {
        EmitType(4);
        _hash.AddAscii(value);
    }

    private void EmitType(byte tokenType)
    {
        _hash.AddByte(tokenType);
        _tokenCount++;
    }

    private bool StartsWith(string value, StringComparison comparison)
    {
        return _input.AsSpan(_position).StartsWith(value, comparison);
    }

    private void SkipWhitespace()
    {
        while (_position < _input.Length && IsAsciiWhitespace(_input[_position]))
        {
            Advance();
        }
    }

    private void Advance()
    {
        AdvanceTo(_position + 1);
    }

    private void AdvanceTo(int target)
    {
        target = Math.Min(target, _input.Length);
        while (_position < target)
        {
            char value = _input[_position++];
            switch (value)
            {
                case '\r':
                    _line++;
                    _column = 1;
                    _previousWasCarriageReturn = true;
                    break;
                case '\n':
                    if (!_previousWasCarriageReturn)
                    {
                        _line++;
                        _column = 1;
                    }
                    _previousWasCarriageReturn = false;
                    break;
                default:
                    _column++;
                    _previousWasCarriageReturn = false;
                    break;
            }
        }
    }

    private static CharacterReference ParseCharacterReference(ReadOnlySpan<char> input, int position)
    {
        ReadOnlySpan<char> remaining = input[position..];
        foreach ((string encoded, string decoded) in KnownCharacterReferences)
        {
            if (remaining.StartsWith(encoded, StringComparison.Ordinal))
            {
                return new CharacterReference(decoded, encoded.Length);
            }
        }

        if (remaining.StartsWith("&#", StringComparison.Ordinal))
        {
            int index = 2;
            bool hexadecimal =
                index < remaining.Length && remaining[index] is 'x' or 'X';
            if (hexadecimal)
            {
                index++;
            }

            int digitStart = index;
            uint value = 0;
            while (index < remaining.Length)
            {
                int digit = hexadecimal
                    ? GetHexValue(remaining[index])
                    : remaining[index] is >= '0' and <= '9'
                        ? remaining[index] - '0'
                        : -1;
                if (digit < 0)
                {
                    break;
                }

                value = (value * (hexadecimal ? 16u : 10u)) + (uint)digit;
                index++;
            }

            if (index > digitStart)
            {
                if (index < remaining.Length && remaining[index] == ';')
                {
                    index++;
                }
                if (value <= 0x7f)
                {
                    return new CharacterReference(((char)value).ToString(), index);
                }
            }
        }

        return new CharacterReference("&", 1);
    }

    private static string DecodeCharacterReferences(ReadOnlySpan<char> input)
    {
        var output = new System.Text.StringBuilder(input.Length);
        int position = 0;
        while (position < input.Length)
        {
            if (input[position] == '&')
            {
                CharacterReference reference = ParseCharacterReference(input, position);
                output.Append(reference.Value);
                position += reference.Consumed;
            }
            else
            {
                output.Append(input[position]);
                position++;
            }
        }
        return output.ToString();
    }

    private static int GetHexValue(char value)
    {
        return value switch
        {
            >= '0' and <= '9' => value - '0',
            >= 'a' and <= 'f' => value - 'a' + 10,
            >= 'A' and <= 'F' => value - 'A' + 10,
            _ => -1
        };
    }

    private static bool IsAsciiAlpha(char value)
    {
        return value is >= 'a' and <= 'z' or >= 'A' and <= 'Z';
    }

    private static bool IsAsciiWhitespace(char value)
    {
        return value is '\t' or '\n' or '\f' or '\r' or ' ';
    }

    private static bool IsTagNameCharacter(char value)
    {
        return !IsAsciiWhitespace(value) && value is not ('/' or '>');
    }

    private static readonly (string Encoded, string Decoded)[] KnownCharacterReferences =
    [
        ("&quot;", "\""),
        ("&apos;", "'"),
        ("&amp;", "&"),
        ("&lt;", "<"),
        ("&gt;", ">")
    ];

    private sealed record CommonAttribute(string Name, string Value);
    private readonly record struct CharacterReference(string Value, int Consumed);
}
