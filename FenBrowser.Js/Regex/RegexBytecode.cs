// ECMA-262 §22.2 — Regex bytecode instruction set for the backtracking VM.
//
// Design: each instruction is a fixed-size tuple (OpCode + 3 int operands).
// The VM executes them in a loop with a backtracking stack.
//
// Quantifiers are compiled into Split/Jump/Save patterns:
//   Greedy A*  → Split L_body, L_skip ; L_body: A ; Save ; Jump L_body ; L_skip: ...
//   Lazy A*?   → Split L_skip, L_body ; L_body: A ; Save ; Jump L_body ; L_skip: ...

namespace FenBrowser.Js.Regex;

public enum RegexOpCode : byte
{
    // --- Character matching ---
    Char,           // Match literal code point. A=codePoint.
    CharRange,      // Match code point in range. A=start, B=end (inclusive).
    CharClass,      // Match built-in class: \d \s \w. A=classKind (see CharClassKind).
    Dot,            // Match any char except line terminator. A=1 if dotAll mode.
    UnicodeProp,    // Match \p{...}. A=property table index.
    ReverseChar,    // Match previous literal code point and move backward.
    ReverseCharRange,
    ReverseCharClass,
    ReverseDot,
    ReverseUnicodeProp,
    ReverseBackRef,
    ReverseNamedBackRef,

    // --- Control flow ---
    Jump,           // Unconditional jump. A=relative offset from next instruction.
    Split,          // Push both paths for backtracking. A=offset1, B=offset2 (relative).
    Accept,         // Successful match. No operands.

    // --- Position assertions ---
    Bol,            // ^  A=1 if multiline.
    Eol,            // $  A=1 if multiline.
    WordBoundary,   // \b
    NonWordBoundary,// \B

    // --- Captures ---
    Save,           // Save current position. A=slotIndex (even=start, odd=end for group N).
    BackRef,        // Match text captured by group N. A=groupNumber, B=1 if ignoreCase.
    NamedBackRef,   // Match text captured by named group. A=nameIndex in program's name table.

    // --- Lookaround ---
    Lookahead,      // (?=...) positive lookahead. A=jump offset past body.
    NegLookahead,   // (?!...) negative lookahead. A=jump offset past body.
    Lookbehind,     // (?<=...) positive lookbehind. A=jump offset past body.
    NegLookbehind,  // (?<!...) negative lookbehind. A=jump offset past body.

    // --- Groups ---
    BeginGroup,     // Mark start of capturing group N. A=groupNumber.
    EndGroup,       // Mark end of capturing group N. A=groupNumber.
    ResetGroup,     // Reset capture group N to undefined. A=groupNumber.
}

public enum CharClassKind : byte
{
    Digit,          // \d
    NotDigit,       // \D
    Word,           // \w
    NotWord,        // \W
    Space,          // \s
    NotSpace,       // \S
}

public readonly record struct RegexInstruction(
    RegexOpCode OpCode,
    int A = 0,
    int B = 0,
    int C = 0
);

public sealed class RegexProgram
{
    public RegexInstruction[] Instructions { get; }
    public int CaptureCount { get; }
    public Dictionary<string, int[]>? NamedGroupMap { get; }
    public string[]? NamedBackReferenceNames { get; }
    public RegexFlags Flags { get; }

    // Unicode property table: maps property indices to lookup data.
    // Stored as (propertyName, valueOrNull, negated) tuples for fast access.
    public (string Property, string? Value, bool Negated)[]? UnicodeProperties { get; init; }

    // Unicode property body strings (e.g. "General_Category=Letter", "Emoji").
    // Indexed by the UnicodeProp instruction's A operand (when B=0).
    // Used by the VM to call UnicodePropertyEscapeData.TryHasPropertyCodePoint.
    public string[]? UnicodePropertyBodies { get; init; }

    // Character range tables: pre-computed arrays for character classes.
    // Used by CharClass instruction at runtime.
    public int[][]? CharClassRanges { get; init; }

    // Lazily-resolved codepoint ranges per UnicodePropertyBodies entry, filled
    // by the VM on first use so membership checks are a binary search instead
    // of a dictionary lookup per character. Empty array = not in the table
    // (fall back to the per-codepoint API). Benign race: idempotent fill.
    internal uint[]?[]? ResolvedPropertyRanges;

    public RegexProgram(
        RegexInstruction[] instructions,
        int captureCount,
        Dictionary<string, int[]>? namedGroupMap,
        RegexFlags flags,
        string[]? namedBackReferenceNames = null)
    {
        Instructions = instructions;
        CaptureCount = captureCount;
        NamedGroupMap = namedGroupMap;
        Flags = flags;
        NamedBackReferenceNames = namedBackReferenceNames;
    }
}
