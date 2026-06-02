// ECMA-262 §22.2.1 Pattern semantics — Abstract Syntax Tree.
//
// Represents a parsed ECMAScript regular expression as a tree structure.
// The grammar mirrors the spec's productions:
//   Pattern :: Disjunction
//   Disjunction :: Alternative | Alternative | Disjunction
//   Alternative :: [empty] | Alternative Term
//   Term :: Assertion | Atom | Atom Quantifier
//
// Each node type maps to a production in §22.2.1.

using System.Diagnostics;

namespace FenBrowser.Js.Regex;

// ─── Top-level ───────────────────────────────────────────────────

/// <summary>A complete regex pattern: a disjunction + flags.</summary>
[DebuggerDisplay("Pattern: AltCount={Disjunction.Alternatives.Count}")]
public sealed class RegexPattern
{
    public readonly DisjunctionNode Disjunction;
    public readonly RegexFlags Flags;
    public readonly int CaptureCount; // total number of capturing groups (for result array sizing)

    public RegexPattern(DisjunctionNode disjunction, RegexFlags flags, int captureCount)
    {
        Disjunction = disjunction;
        Flags = flags;
        CaptureCount = captureCount;
    }
}

// ─── Disjunction / Alternative ───────────────────────────────────

/// <summary>Disjunction :: Alternative | ... | Alternative (at least one).</summary>
[DebuggerDisplay("Disjunction: {Alternatives.Count} alternatives")]
public sealed class DisjunctionNode
{
    public readonly List<AlternativeNode> Alternatives;

    public DisjunctionNode(List<AlternativeNode> alternatives)
    {
        Alternatives = alternatives;
    }

    public DisjunctionNode(AlternativeNode single)
    {
        Alternatives = new List<AlternativeNode> { single };
    }
}

/// <summary>Alternative :: sequence of Terms (may be empty).</summary>
[DebuggerDisplay("Alternative: {Terms.Count} terms")]
public sealed class AlternativeNode
{
    public readonly List<TermNode> Terms;

    public AlternativeNode(List<TermNode> terms)
    {
        Terms = terms;
    }

    public AlternativeNode() : this(new List<TermNode>()) { }
}

// ─── Term ────────────────────────────────────────────────────────

/// <summary>Base class for terms within an alternative.</summary>
[DebuggerDisplay("{GetType().Name}")]
public abstract class TermNode
{
    // Whether this term is inside a negative character class or negative assertion context.
    // Used during compilation to invert matching logic.
    internal bool Negated;
}

// ─── Assertion ───────────────────────────────────────────────────

public enum AssertionKind
{
    BeginOfInput,        // ^
    EndOfInput,          // $
    WordBoundary,        // \b
    NonWordBoundary,     // \B
    Lookahead,           // (?= ...)
    NegativeLookahead,   // (?! ...)
    Lookbehind,          // (?<= ...)
    NegativeLookbehind,  // (?<! ...)
}

/// <summary>Assertion :: ^ | $ | \b | \B | (?= Disjunction ) | (?! Disjunction ) | (?<= Disjunction ) | (?<! Disjunction ).</summary>
public sealed class AssertionNode : TermNode
{
    public readonly AssertionKind Kind;
    public readonly DisjunctionNode? Body; // null for ^ $ \b \B; present for lookahead/lookbehind

    public AssertionNode(AssertionKind kind, DisjunctionNode? body = null)
    {
        Kind = kind;
        Body = body;
    }
}

// ─── Atom ────────────────────────────────────────────────────────

/// <summary>Base class for atoms (non-assertion, non-quantified leaf nodes).</summary>
public abstract class AtomNode : TermNode { }

/// <summary>A literal character matched verbatim.</summary>
[DebuggerDisplay("Char: '{Value}' (U+{(int)Value:X4})")]
public sealed class LiteralCharNode : AtomNode
{
    public readonly char Value;
    public LiteralCharNode(char value) => Value = value;
}

/// <summary>The . metacharacter — any single character (respecting /s and /u flags).</summary>
public sealed class DotNode : AtomNode { }

/// <summary>A single-character escape: \\t \\n \\v \\f \\r \\0 \\xHH \\uHHHH \\u{H...} \\cX.</summary>
[DebuggerDisplay("Escape: U+{CodePoint:X4}")]
public sealed class CharacterEscapeNode : AtomNode
{
    public readonly int CodePoint; // full Unicode code point (0–0x10FFFF)
    public CharacterEscapeNode(int codePoint) => CodePoint = codePoint;
}

/// <summary>A character class escape: \\d \\D \\s \\S \\w \\W.</summary>
[DebuggerDisplay("ClassEscape: {Kind}")]
public sealed class ClassEscapeNode : AtomNode
{
    public readonly char Kind; // 'd', 'D', 's', 'S', 'w', 'W'

    public ClassEscapeNode(char kind)
    {
        Kind = kind;
        Negated = kind is 'D' or 'S' or 'W';
    }

    public bool IsNegated => Kind is 'D' or 'S' or 'W';
}

/// <summary>A Unicode property escape: \\p{...} or \\P{...}.</summary>
[DebuggerDisplay("UnicodeProperty: {Property}={Value ?? \"(binary)\"}")]
public sealed class UnicodePropertyNode : AtomNode
{
    public readonly string Property;  // e.g. "General_Category", "Script", or binary property name
    public readonly string? Value;    // null for binary properties, e.g. "Letter", "Latin"
    public readonly new bool Negated;     // true for \P{...}

    public UnicodePropertyNode(string property, string? value, bool negated)
    {
        Property = property;
        Value = value;
        Negated = negated;
    }
}

/// <summary>A backreference: \\1 through \\9 (or higher in Unicode/strict mode).</summary>
[DebuggerDisplay("BackRef: \\{GroupNumber}")]
public sealed class BackReferenceNode : AtomNode
{
    public readonly int GroupNumber;
    public BackReferenceNode(int groupNumber) => GroupNumber = groupNumber;
}

/// <summary>A character class: [...] or [^...].</summary>
[DebuggerDisplay("CharClass: {(Negated ? \"negated\" : \"normal\")}, {Items.Count} items")]
public sealed class CharacterClassNode : AtomNode
{
    public readonly List<ClassItem> Items;
    public new bool Negated => base.Negated;

    public CharacterClassNode(List<ClassItem> items, bool negated)
    {
        Items = items;
        base.Negated = negated;
    }
}

/// <summary>A capturing or non-capturing group.</summary>
public enum GroupKind
{
    Capturing,         // ( ... )
    NonCapturing,      // (?: ... )
    NamedCapturing,    // (?<name> ... )
}

[DebuggerDisplay("Group: {Kind} #{GroupNumber}")]
public sealed class GroupNode : AtomNode
{
    public readonly GroupKind Kind;
    public readonly DisjunctionNode Body;
    public readonly string? Name;      // null for non-named groups
    public readonly int GroupNumber;   // 1-based capture group index; 0 for non-capturing

    public GroupNode(GroupKind kind, DisjunctionNode body, int groupNumber, string? name = null)
    {
        Kind = kind;
        Body = body;
        GroupNumber = groupNumber;
        Name = name;
    }
}

// ─── Quantifier ──────────────────────────────────────────────────

/// <summary>A quantifier applied to an atom: * + ? {n} {n,} {n,m}.</summary>
[DebuggerDisplay("Quantifier: {Min}-{Max} {(Greedy ? \"greedy\" : \"lazy\")}")]
public sealed class QuantifierNode : TermNode
{
    public readonly AtomNode Body;  // the atom being quantified
    public readonly int Min;        // minimum repetitions
    public readonly int Max;        // maximum repetitions (int.MaxValue for unbounded)
    public readonly bool Greedy;    // true = greedy (default), false = lazy (followed by ?)

    public QuantifierNode(AtomNode body, int min, int max, bool greedy)
    {
        Body = body;
        Min = min;
        Max = max;
        Greedy = greedy;
    }
}

// ─── Character Class Items ───────────────────────────────────────

/// <summary>An item within a character class [...].</summary>
[DebuggerDisplay("{GetType().Name}")]
public abstract class ClassItem { }

/// <summary>A single literal character within a class.</summary>
public sealed class ClassLiteralChar : ClassItem
{
    public readonly char Value;
    public ClassLiteralChar(char value) => Value = value;
}

/// <summary>A character range a-z within a class.</summary>
public sealed class ClassRange : ClassItem
{
    public readonly int Start;  // code point
    public readonly int End;    // code point (inclusive)
    public ClassRange(int start, int end) { Start = start; End = end; }
}

/// <summary>An escaped character within a class: \\t \\n \\xHH etc.</summary>
public sealed class ClassEscape : ClassItem
{
    public readonly int CodePoint;
    public ClassEscape(int codePoint) => CodePoint = codePoint;
}

/// <summary>A character class escape within a class: \\d \\D \\s \\S \\w \\W.</summary>
public sealed class ClassClassEscape : ClassItem
{
    public readonly char Kind; // 'd', 'D', 's', 'S', 'w', 'W'
    public ClassClassEscape(char kind) => Kind = kind;
}

/// <summary>A Unicode property escape within a class: \\p{...} or \\P{...}.</summary>
public sealed class ClassUnicodeProperty : ClassItem
{
    public readonly string Property;
    public readonly string? Value;
    public readonly bool Negated;
    public ClassUnicodeProperty(string property, string? value, bool negated)
    {
        Property = property;
        Value = value;
        Negated = negated;
    }
}
