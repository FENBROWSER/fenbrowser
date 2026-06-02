// ECMA-262 §22.2.3 RegExp Flags.
// Canonical flag order is "dgimsuvy" per the spec.

namespace FenBrowser.Js.Regex;

/// <summary>
/// Immutable representation of ECMAScript RegExp flags with canonical ordering.
/// </summary>
public readonly struct RegexFlags : IEquatable<RegexFlags>
{
    private readonly byte _mask; // bitmask for d/g/i/m/s/u/v/y

    private const byte FlagHasIndices  = 1 << 0; // d
    private const byte FlagGlobal     = 1 << 1; // g
    private const byte FlagIgnoreCase = 1 << 2; // i
    private const byte FlagMultiline  = 1 << 3; // m
    private const byte FlagDotAll     = 1 << 4; // s
    private const byte FlagUnicode    = 1 << 5; // u
    private const byte FlagUnicodeSets = 1 << 6; // v
    private const byte FlagSticky     = 1 << 7; // y

    private RegexFlags(byte mask) => _mask = mask;

    public static RegexFlags None => new(0);

    public bool HasIndices => (_mask & FlagHasIndices) != 0;
    public bool Global => (_mask & FlagGlobal) != 0;
    public bool IgnoreCase => (_mask & FlagIgnoreCase) != 0;
    public bool Multiline => (_mask & FlagMultiline) != 0;
    public bool DotAll => (_mask & FlagDotAll) != 0;
    public bool Unicode => (_mask & FlagUnicode) != 0;
    public bool UnicodeSets => (_mask & FlagUnicodeSets) != 0;
    public bool Sticky => (_mask & FlagSticky) != 0;

    public byte Mask => _mask;

    /// <summary>
    /// Parse and normalize flags per ECMA-262 §22.2.3.1 ParseText.
    /// Returns the normalized flags or throws for invalid/duplicate flags.
    /// </summary>
    public static RegexFlags Parse(ReadOnlySpan<char> flags)
    {
        byte mask = 0;
        foreach (var c in flags)
        {
            var bit = c switch
            {
                'd' => FlagHasIndices,
                'g' => FlagGlobal,
                'i' => FlagIgnoreCase,
                'm' => FlagMultiline,
                's' => FlagDotAll,
                'u' => FlagUnicode,
                'v' => FlagUnicodeSets,
                'y' => FlagSticky,
                _ => throw new RegexSyntaxError($"Invalid RegExp flag: '{c}'")
            };
            if ((mask & bit) != 0)
                throw new RegexSyntaxError($"Duplicate RegExp flag: '{c}'");
            mask |= bit;
        }

        if ((mask & FlagUnicode) != 0 && (mask & FlagUnicodeSets) != 0)
        {
            throw new RegexSyntaxError("RegExp flags 'u' and 'v' cannot be used together.");
        }

        return new RegexFlags(mask);
    }

    /// <summary>
    /// Returns the canonical flag string (e.g. "gimsy").
    /// ECMA-262 §22.2.4 CanonicalizeFlags ordering: d, g, i, m, s, u, v, y.
    /// </summary>
    public override string ToString()
    {
        Span<char> buffer = stackalloc char[8];
        var pos = 0;
        if ((_mask & FlagHasIndices) != 0)  buffer[pos++] = 'd';
        if ((_mask & FlagGlobal) != 0)     buffer[pos++] = 'g';
        if ((_mask & FlagIgnoreCase) != 0) buffer[pos++] = 'i';
        if ((_mask & FlagMultiline) != 0)  buffer[pos++] = 'm';
        if ((_mask & FlagDotAll) != 0)     buffer[pos++] = 's';
        if ((_mask & FlagUnicode) != 0)    buffer[pos++] = 'u';
        if ((_mask & FlagUnicodeSets) != 0) buffer[pos++] = 'v';
        if ((_mask & FlagSticky) != 0)     buffer[pos++] = 'y';
        return new string(buffer[..pos]);
    }

    public string ToJsFlagsString() => ToString();

    public bool Equals(RegexFlags other) => _mask == other._mask;
    public override bool Equals(object? obj) => obj is RegexFlags other && Equals(other);
    public override int GetHashCode() => _mask.GetHashCode();
    public static bool operator ==(RegexFlags left, RegexFlags right) => left.Equals(right);
    public static bool operator !=(RegexFlags left, RegexFlags right) => !left.Equals(right);
}
