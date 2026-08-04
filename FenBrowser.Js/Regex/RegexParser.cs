// ECMA-262 §22.2.1 — RegExp pattern parser (recursive descent).
// Produces a RegexPattern AST using the types defined in RegexAst.cs.
//

using System.Text;

// Grammar (simplified from §22.2.1):
//   Pattern :: Disjunction
//   Disjunction :: Alternative | Alternative | Disjunction
//   Alternative :: [empty] | Alternative Term
//   Term :: Assertion | Atom | Atom Quantifier
//   Assertion :: ^ | $ | \b | \B | (?= Disjunction ) | (?! Disjunction )
//              | (?<= Disjunction ) | (?<! Disjunction )
//   Atom :: PatternCharacter | . | \ AtomEscape | CharacterClass
//         | ( Disjunction ) | (?: Disjunction ) | (?<Name> Disjunction )
//         | (?Modifiers: Disjunction )
//   Quantifier :: QuantifierPrefix | QuantifierPrefix ?
//   QuantifierPrefix :: * | + | ? | {DecimalDigits} | {DecimalDigits,} | {DecimalDigits,DecimalDigits}

namespace FenBrowser.Js.Regex;

public static class RegexParser
{
    public static RegexPattern Parse(string pattern, RegexFlags flags)
    {
        var p = new ParserState(pattern, flags);
        var disjunction = p.ParseDisjunction();
        p.ExpectEnd();
        return new RegexPattern(disjunction, flags, p._captureCount);
    }

    private sealed class ParserState
    {
        private readonly string _pattern;
        private readonly RegexFlags _flags;
        private readonly bool _hasNamedCaptureSyntax;
        private int _pos;
        internal int _captureCount;
        private int _groupCount; // also counts non-capturing for group numbering

        // Named groups: maps name -> (groupNumber, definition position)
        // Position tracking lets us detect forward backreferences.
        private readonly Dictionary<string, int> _namedGroups = new(StringComparer.Ordinal);
        private readonly List<int> _groupPositions = new(); // 0-indexed sentinel, then group positions

        internal ParserState(string pattern, RegexFlags flags)
        {
            _pattern = pattern;
            _flags = flags;
            _hasNamedCaptureSyntax = ContainsNamedCaptureSyntax(pattern);
            _pos = 0;
            _captureCount = 0;
            _groupCount = 0;
            _groupPositions.Add(-1); // sentinel for group 0
        }

        private bool IsUnicode => _flags.Unicode || _flags.UnicodeSets;
        private bool IsUnicodeSets => _flags.UnicodeSets;
        private bool AtEnd => _pos >= _pattern.Length;
        private char Peek => AtEnd ? '\0' : _pattern[_pos];
        private char Peek1 => _pos + 1 < _pattern.Length ? _pattern[_pos + 1] : '\0';

        private void Advance(int n = 1) => _pos += n;

        private void Expect(char c)
        {
            if (AtEnd || _pattern[_pos] != c)
                throw new RegexSyntaxError($"Expected '{c}'", _pos);
            Advance();
        }

        internal void ExpectEnd()
        {
            if (!AtEnd)
                throw new RegexSyntaxError("Unexpected continuation in pattern", _pos);
        }

        // ─── Pattern :: Disjunction ────────────────────────────

        internal DisjunctionNode ParseDisjunction()
        {
            var alternatives = new List<AlternativeNode>();
            alternatives.Add(ParseAlternative());

            while (!AtEnd && Peek == '|')
            {
                Advance(); // consume '|'
                alternatives.Add(ParseAlternative());
            }

            return new DisjunctionNode(alternatives);
        }

        // ─── Alternative :: [empty] | Alternative Term ─────────

        private AlternativeNode ParseAlternative()
        {
            var terms = new List<TermNode>();
            while (!AtEnd && Peek != '|' && Peek != ')')
            {
                var term = ParseTerm();
                terms.Add(term);
            }

            return new AlternativeNode(terms);
        }

        // ─── Term :: Assertion | Atom | Atom Quantifier ────────

        private TermNode ParseTerm()
        {
            var startPos = _pos;
            TermNode term;

            if (TryParseAssertion(out var assertion))
            {
                term = assertion;
            }
            else
            {
                var atom = ParseAtom();
                if (!AtEnd && IsQuantifierStart())
                {
                    term = ParseQuantifier(atom);
                }
                else
                {
                    term = atom;
                }
            }

            return term;
        }

        // ─── Assertion ─────────────────────────────────────────

        private bool TryParseAssertion(out AssertionNode assertion)
        {
            assertion = null!;
            var ch = Peek;

            if (ch == '^')
            {
                Advance();
                assertion = new AssertionNode(AssertionKind.BeginOfInput);
                return true;
            }

            if (ch == '$')
            {
                Advance();
                assertion = new AssertionNode(AssertionKind.EndOfInput);
                return true;
            }

            if (ch == '\\')
            {
                var next = Peek1;
                if (next == 'b' || next == 'B')
                {
                    Advance(2);
                    assertion = new AssertionNode(
                        next == 'b' ? AssertionKind.WordBoundary : AssertionKind.NonWordBoundary);
                    return true;
                }
            }

            if (ch == '(' && Peek1 == '?')
            {
                var saved = _pos;
                try
                {
                    var kind = TryParseGroupPrefix();
                    if (kind == GroupPrefixKind.Lookahead)
                    {
                        var body = ParseDisjunction();
                        Expect(')');
                        assertion = new AssertionNode(AssertionKind.Lookahead, body);
                        return true;
                    }

                    if (kind == GroupPrefixKind.NegativeLookahead)
                    {
                        var body = ParseDisjunction();
                        Expect(')');
                        assertion = new AssertionNode(AssertionKind.NegativeLookahead, body);
                        return true;
                    }

                    if (kind == GroupPrefixKind.Lookbehind)
                    {
                        var body = ParseDisjunction();
                        Expect(')');
                        assertion = new AssertionNode(AssertionKind.Lookbehind, body);
                        return true;
                    }

                    if (kind == GroupPrefixKind.NegativeLookbehind)
                    {
                        var body = ParseDisjunction();
                        Expect(')');
                        assertion = new AssertionNode(AssertionKind.NegativeLookbehind, body);
                        return true;
                    }
                }
                catch
                {
                    // Not an assertion — backtrack and let ParseAtom handle it
                }

                _pos = saved;
            }

            return false;
        }

        // ─── Atom ──────────────────────────────────────────────

        private AtomNode ParseAtom()
        {
            var ch = Peek;

            // '.' metacharacter
            if (ch == '.')
            {
                Advance();
                return new DotNode();
            }

            // CharacterClass [...]
            if (ch == '[')
            {
                return ParseCharacterClass();
            }

            // Group: ( ... ), (?: ... ), (?<name> ... ), (?modifiers: ... )
            if (ch == '(')
            {
                return ParseGroup();
            }

            // Backslash escape
            if (ch == '\\')
            {
                return ParseAtomEscape();
            }

            // PatternCharacter — any source character that is not syntax
            if (!IsUnicode && (ch == ']' || ch == '}' || (ch == '{' && !IsValidBraceQuantifierAt(_pos))))
            {
                Advance();
                return new LiteralCharNode(ch);
            }

            if (IsSyntaxCharacter(ch))
            {
                throw new RegexSyntaxError($"Unexpected character '{ch}'", _pos);
            }

            Advance();
            if (IsUnicode &&
                char.IsHighSurrogate(ch) &&
                !AtEnd &&
                char.IsLowSurrogate(Peek))
            {
                var low = Peek;
                Advance();
                return new CharacterEscapeNode(char.ConvertToUtf32(ch, low));
            }

            return new LiteralCharNode(ch);
        }

        private static bool IsSyntaxCharacter(char c)
        {
            return c is '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|';
        }

        // ─── AtomEscape ────────────────────────────────────────

        private AtomNode ParseAtomEscape()
        {
            Expect('\\');
            if (AtEnd)
                throw new RegexSyntaxError("Unexpected end of pattern after '\\'", _pos);

            var ch = Peek;

            // CharacterClassEscape: \d \D \s \S \w \W
            if (ch is 'd' or 'D' or 's' or 'S' or 'w' or 'W')
            {
                Advance();
                return new ClassEscapeNode(ch);
            }

            // Unicode property escape: \p{...} \P{...}
            if ((ch == 'p' || ch == 'P') && Peek1 == '{')
            {
                return ParseUnicodePropertyEscape();
            }

            // Named backreference: \k<name> — only in Unicode mode (u/v flag).
            // In non-Unicode mode, \k is an IdentityEscape (literal 'k').
            if (ch == 'k' && (IsUnicode || _hasNamedCaptureSyntax))
            {
                if (Peek1 != '<')
                    throw new RegexSyntaxError("Invalid named backreference", _pos - 1);

                return ParseNamedBackReference();
            }

            // Decimal escape / backreference: \0, \1-\9, etc.
            if (IsDecimalDigit(ch))
            {
                return ParseDecimalEscape();
            }

            // CharacterEscape: \t \n \v \f \r
            if (TryParseSimpleEscape(ch, out var cp))
            {
                Advance();
                return new CharacterEscapeNode(cp);
            }

            // Control escape: \cX
            if (ch == 'c')
            {
                return ParseControlEscape();
            }

            // Hex escape: \xHH
            if (ch == 'x')
            {
                return ParseHexEscape();
            }

            // Unicode escape: \uHHHH or \u{H...}
            if (ch == 'u')
            {
                return ParseUnicodeEscape();
            }

            // IdentityEscape — in non-Unicode mode, \ followed by non-special char
            // is just the char itself (SyntaxCharacter or / are escaped)
            if (IsUnicode && !IsValidIdentityEscapeInUnicode(ch))
            {
                throw new RegexSyntaxError($"Invalid identity escape '\\{ch}' in Unicode regular expression", _pos - 1);
            }

            Advance();
            return new CharacterEscapeNode(ch);
        }

        private static bool TryParseSimpleEscape(char ch, out int codePoint)
        {
            codePoint = ch switch
            {
                't' => '\t',
                'n' => '\n',
                'v' => '\v',
                'f' => '\f',
                'r' => '\r',
                _ => -1
            };
            return codePoint != -1;
        }

        private static bool IsValidIdentityEscapeInUnicode(char ch)
        {
            // In Unicode mode, \ can only precede syntax characters, /, or be a known escape.
            return ch is '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '|' or '/';
        }

        private static bool IsValidIdentityEscapeInUnicodeClass(char ch)
        {
            return IsValidIdentityEscapeInUnicode(ch) || ch == '-';
        }

        private static bool IsKnownClassEscape(char ch)
        {
            return ch is 'd' or 'D' or 's' or 'S' or 'w' or 'W'
                or 't' or 'n' or 'v' or 'f' or 'r' or '0' or 'b'
                or 'c' or 'x' or 'u' or 'p' or 'P' or 'q';
        }

        // ─── Decimal Escape / Backreference ────────────────────

        private AtomNode ParseDecimalEscape()
        {
            var start = _pos;
            while (!AtEnd && IsDecimalDigit(Peek))
                Advance();

            var digits = _pattern[start.._pos];
            if (!int.TryParse(digits, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                throw new RegexSyntaxError("Invalid decimal escape", start);

            if (digits == "0")
            {
                return new CharacterEscapeNode(0);
            }

            // \0 followed by a decimal digit is an octal escape (Annex B non-Unicode)
            // or a syntax error (Unicode mode)
            if (digits[0] == '0' && digits.Length > 1)
            {
                if (IsUnicode)
                    throw new RegexSyntaxError("Invalid legacy octal escape in Unicode regular expression", start);

                // In non-Unicode, \0 followed by digits is still the null char
                // followed by literal digits — but per Annex B: \0 when NOT followed
                // by a decimal digit is the null char; \0d where d is a decimal digit
                // is treated as \0 then literal digit. We handle this:
                _pos = start + 1; // only consume the \0
                return new CharacterEscapeNode(0);
            }

            // In Unicode mode, \N must be a valid backreference
            if (IsUnicode)
            {
                if (value == 0 || value > _captureCount)
                    throw new RegexSyntaxError($"Invalid backreference \\{value} in Unicode regular expression", start);

                return new BackReferenceNode(value);
            }

            // In non-Unicode mode:
            // - \1-\9 are ALWAYS backreferences (ECMAScript Annex B)
            // - \10+ are backreferences if they reference an existing group,
            //   otherwise they're octal/character escapes
            if (value >= 1 && value <= 9)
            {
                return new BackReferenceNode(value);
            }

            if (value <= _captureCount)
            {
                return new BackReferenceNode(value);
            }

            // \10+ that exceeds capture count: treat as octal character
            return new CharacterEscapeNode(value & 0xFF);
        }

        // ─── Named Backreference ───────────────────────────────

        private AtomNode ParseNamedBackReference()
        {
            Expect('k');
            Expect('<');
            var name = ParseGroupName();
            Expect('>');
            return new NamedBackReferenceNode(name);
        }

        // ─── Unicode Property Escape ───────────────────────────

        private AtomNode ParseUnicodePropertyEscape()
        {
            var ch = Peek; // 'p' or 'P'
            Advance();
            Expect('{');
            var body = ParseUnicodePropertyBody();
            Expect('}');

            if (!IsUnicode)
            {
                // \p and \P are only valid in Unicode mode (u or v flag)
                throw new RegexSyntaxError("Unicode property escapes are only valid in Unicode regular expressions", _pos - body.Length - 2);
            }

            var negated = ch == 'P';
            var (property, value) = ParsePropertyNameAndValue(body);
            return new UnicodePropertyNode(property, value, negated);
        }

        private string ParseUnicodePropertyBody()
        {
            var start = _pos;
            while (!AtEnd && Peek != '}')
                Advance();

            if (AtEnd)
                throw new RegexSyntaxError("Unterminated Unicode property escape", start);

            return _pattern[start.._pos];
        }

        private static (string property, string? value) ParsePropertyNameAndValue(string body)
        {
            var eqIdx = body.IndexOf('=');
            if (eqIdx < 0)
            {
                // Binary property or General_Category shorthand
                return (body, null);
            }

            var property = body[..eqIdx];
            var value = body[(eqIdx + 1)..];
            return (property, value);
        }

        // ─── Control Escape ────────────────────────────────────

        private AtomNode ParseControlEscape()
        {
            Expect('c');
            if (AtEnd)
                throw new RegexSyntaxError("Invalid control escape", _pos);

            var ch = Peek;

            if (IsUnicode && !char.IsLetter(ch))
                throw new RegexSyntaxError("Invalid control escape in Unicode regular expression", _pos);

            Advance();
            // \cX → control character. Per B.1.4: character value modulo 32.
            // The old `toUpper ^ 64` only works for ASCII (where 65^64=1).
            // Non-ASCII letters need `ch % 32` to produce the correct result.
            var controlChar = (char)(ch % 32);
            return new CharacterEscapeNode(controlChar);
        }

        // ─── Hex Escape ────────────────────────────────────────

        private AtomNode ParseHexEscape()
        {
            Expect('x');
            return new CharacterEscapeNode(ParseHexDigits(2, "hex"));
        }

        private int ParseHexDigits(int count, string context)
        {
            if (_pos + count > _pattern.Length)
                throw new RegexSyntaxError($"Invalid {context} escape — expected {count} hex digits", _pos);

            var hex = _pattern.Substring(_pos, count);
            _pos += count;
            if (!int.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                throw new RegexSyntaxError($"Invalid {context} escape", _pos - count);

            return value;
        }

        // ─── Unicode Escape ────────────────────────────────────

        private AtomNode ParseUnicodeEscape()
        {
            Expect('u');

            if (!AtEnd && Peek == '{')
            {
                // \u{HHH...}
                Advance();
                var hexStart = _pos;
                while (!AtEnd && IsHexDigit(Peek))
                    Advance();

                if (AtEnd || Peek != '}')
                    throw new RegexSyntaxError("Invalid Unicode code point escape", hexStart - 2);

                var hex = _pattern[hexStart.._pos];
                Expect('}');

                if (!int.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier,
                        System.Globalization.CultureInfo.InvariantCulture, out var cp) ||
                    cp < 0 || cp > 0x10FFFF)
                    throw new RegexSyntaxError("Invalid Unicode code point escape", hexStart - 2);

                if (IsUnicode && cp >= 0xD800 && cp <= 0xDFFF)
                    throw new RegexSyntaxError("Unicode code point escape cannot be a surrogate", hexStart - 2);

                return new CharacterEscapeNode(cp);
            }
            else
            {
                // \uHHHH — exactly 4 hex digits
                var cp = ParseHexDigits(4, "Unicode");
                if (IsUnicode && cp >= 0xD800 && cp <= 0xDFFF)
                {
                    if (cp <= 0xDBFF && TryConsumeTrailingLowSurrogate(out var lowSurrogate))
                    {
                        cp = char.ConvertToUtf32((char)cp, (char)lowSurrogate);
                    }
                }

                return new CharacterEscapeNode(cp);
            }
        }

        // ─── CharacterClass ────────────────────────────────────

        private CharacterClassNode ParseCharacterClass()
        {
            Expect('[');
            var negated = false;
            if (!AtEnd && Peek == '^')
            {
                negated = true;
                Advance();
            }

            // In v-flag mode, the first character might be ']' (interpreted as literal)
            // or we might have set operations like &&, --, ~~

            var items = ParseClassRanges();

            // v-flag set operations: [A&&B], [A--B], [A~~B]
            if (IsUnicodeSets)
            {
                while (!AtEnd && (Peek == '&' || Peek == '-' || Peek == '~'))
                {
                    var opChar = Peek;
                    if (opChar == '&' && Peek1 == '&')
                    {
                        Advance(2);
                        // Intersection: parse another set of class ranges
                        items = ApplySetOperation(items, ParseClassRanges(), SetOp.Intersection);
                    }
                    else if (opChar == '-' && Peek1 == '-')
                    {
                        Advance(2);
                        items = ApplySetOperation(items, ParseClassRanges(), SetOp.Difference);
                    }
                    else if (opChar == '~' && Peek1 == '~')
                    {
                        Advance(2);
                        items = ApplySetOperation(items, ParseClassRanges(), SetOp.SymmetricDifference);
                    }
                    else
                    {
                        break;
                    }
                }
            }

            Expect(']');
            return new CharacterClassNode(items, negated);
        }

        private enum SetOp { Intersection, Difference, SymmetricDifference }

        private List<ClassItem> ApplySetOperation(List<ClassItem> left, List<ClassItem> right, SetOp op)
        {
            // For now, store the operation on a combined node.
            // The VM will evaluate the set operations during matching.
            // We wrap both sides in a special class item marker.
            // This is a placeholder — the compiler will expand these.
            // For now we just concatenate with a sentinel marker.

            // v-flag set operations are compiled during the AST→bytecode phase.
            // Here we produce nested ClassItem structures that the compiler recognizes.
            // Since we don't have a SetOperationClassItem type, we flatten both sides
            // into one combined list, tagged with the operation. The compiler resolves it.
            //
            // For simplicity: store as left-side ranges + right-side ranges with a
            // special marker. The compiler handles the actual set operation.

            var combined = new List<ClassItem>(left);
            combined.Add(new ClassSetOpMarker((int)op));
            combined.AddRange(right);
            return combined;
        }

        private List<ClassItem> ParseClassRanges()
        {
            var items = new List<ClassItem>();

            while (!AtEnd && Peek != ']')
            {
                // v-flag set operations: stop when we see &&, --, or ~~
                if (IsUnicodeSets && IsRawSetOperatorStart())
                    break;

                var ch = Peek;

                // ClassAtom can be '-' (literal or range start)
                if (ch == '-')
                {
                    // v-flag: if this is '--', stop — the set operation handler takes over
                    if (IsUnicodeSets && Peek1 == '-')
                        break;

                    Advance();
                    if (!AtEnd && Peek != ']')
                    {
                        // '-' as literal at start or end
                        items.Add(new ClassLiteralChar('-'));
                    }
                    else
                    {
                        // '-' at end of class is literal
                        items.Add(new ClassLiteralChar('-'));
                    }
                    continue;
                }

                var classAtom = ParseClassAtom();
                items.Add(classAtom);

                // Check for range: ClassAtom '-' ClassAtom
                // In v-flag mode, '--' is NOT a range start — it's the set difference operator above.
                if (!AtEnd && Peek == '-' && Peek1 != ']' && !(IsUnicodeSets && Peek1 == '-'))
                {
                    Advance(); // consume '-'
                    var endAtom = ParseClassAtom();

                    // Extract code points for range
                    int startCp = GetClassItemSingleCodePoint(classAtom);
                    int endCp = GetClassItemSingleCodePoint(endAtom);

                    if (startCp < 0 || endCp < 0)
                    {
                        throw new RegexSyntaxError("Invalid range in character class", _pos);
                    }

                    if (startCp > endCp)
                        throw new RegexSyntaxError("Range out of order in character class", _pos);

                    // Replace the start atom with the range
                    items.RemoveAt(items.Count - 1);
                    items.Add(new ClassRange(startCp, endCp));
                }
            }

            return items;
        }

        private ClassItem ParseClassAtom()
        {
            if (AtEnd)
                throw new RegexSyntaxError("Unterminated character class", _pos);

            var ch = Peek;

            if (IsUnicodeSets && ch == '[')
            {
                var nested = ParseCharacterClass();
                return new ClassNestedSet(nested.Items, nested.Negated);
            }

            if (ch == '\\')
            {
                Advance();
                if (AtEnd)
                    throw new RegexSyntaxError("Unexpected end of pattern in character class escape", _pos);

                var esc = Peek;
                Advance();

                if (IsUnicode && esc == '0' && !AtEnd && IsDecimalDigit(Peek))
                {
                    throw new RegexSyntaxError("Invalid legacy octal escape in Unicode regular expression", _pos - 1);
                }

                if (IsUnicode && !IsKnownClassEscape(esc) && !IsValidIdentityEscapeInUnicodeClass(esc))
                {
                    throw new RegexSyntaxError($"Invalid identity escape '\\{esc}' in Unicode character class", _pos - 1);
                }

                var item = esc switch
                {
                    'd' or 'D' or 's' or 'S' or 'w' or 'W' => new ClassClassEscape(esc),
                    't' => new ClassEscape('\t'),
                    'n' => new ClassEscape('\n'),
                    'v' => new ClassEscape('\v'),
                    'f' => new ClassEscape('\f'),
                    'r' => new ClassEscape('\r'),
                    '0' => new ClassEscape(0), // \0 null character
                    'b' => new ClassEscape('\b'), // \b in class is backspace
                    'c' => new ClassEscape(ParseControlCharInClass()),
                    'x' => new ClassEscape(ParseHexDigits(2, "hex")),
                    'u' => new ClassEscape(ParseUnicodeInClass()),
                    'q' when IsUnicodeSets => ParseClassStringSet(),
                    'p' or 'P' => ParseUnicodePropertyInClass(esc),
                    '-' => new ClassLiteralChar('-'), // escaped dash is literal
                    _ => new ClassLiteralChar(esc) // identity escape
                };

                return TryCombineClassSurrogatePair(item);
            }

            Advance();
            return TryCombineClassSurrogatePair(new ClassLiteralChar(ch));
        }

        private ClassItem ParseClassStringSet()
        {
            Expect('{');
            var alternatives = new List<int[]>();
            var current = new List<int>();
            while (!AtEnd)
            {
                if (Peek == '}')
                {
                    Advance();
                    alternatives.Add(current.ToArray());
                    return new ClassStringSet(alternatives.ToArray());
                }

                if (Peek == '|')
                {
                    Advance();
                    alternatives.Add(current.ToArray());
                    current.Clear();
                    continue;
                }

                current.Add(ParseClassStringSetCodePoint());
            }

            throw new RegexSyntaxError("Unterminated class string literal", _pos);
        }

        private int ParseClassStringSetCodePoint()
        {
            if (Peek != '\\')
            {
                var ch = Peek;
                Advance();
                if (char.IsHighSurrogate(ch) && !AtEnd && char.IsLowSurrogate(Peek))
                {
                    var low = Peek;
                    Advance();
                    return char.ConvertToUtf32(ch, low);
                }

                return ch;
            }

            Advance();
            if (AtEnd)
                throw new RegexSyntaxError("Unexpected end of class string literal escape", _pos);

            var esc = Peek;
            Advance();
            return esc switch
            {
                't' => '\t',
                'n' => '\n',
                'v' => '\v',
                'f' => '\f',
                'r' => '\r',
                '0' => 0,
                'x' => ParseHexDigits(2, "hex"),
                'u' => ParseUnicodeInClass(),
                _ => esc
            };
        }

        private int ParseControlCharInClass()
        {
            if (AtEnd)
                throw new RegexSyntaxError("Invalid control escape in class", _pos);
            var ch = Peek;
            Advance();
            // Per B.1.4: character value modulo 32, not toUpper ^ 64.
            return ch % 32;
        }

        private int ParseUnicodeInClass()
        {
            if (!AtEnd && Peek == '{')
            {
                Advance();
                var hexStart = _pos;
                while (!AtEnd && IsHexDigit(Peek))
                    Advance();
                if (AtEnd || Peek != '}')
                    throw new RegexSyntaxError("Invalid Unicode code point escape", _pos);
                var hex = _pattern[hexStart.._pos];
                Expect('}');
                int.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier,
                    System.Globalization.CultureInfo.InvariantCulture, out var cp);
                return cp;
            }
            else
            {
                var cp = ParseHexDigits(4, "Unicode");
                if (IsUnicode &&
                    cp is >= 0xD800 and <= 0xDBFF &&
                    TryConsumeTrailingLowSurrogate(out var lowSurrogate))
                {
                    cp = char.ConvertToUtf32((char)cp, (char)lowSurrogate);
                }
                return cp;
            }
        }

        private bool TryConsumeTrailingLowSurrogate(out int lowSurrogate)
        {
            lowSurrogate = 0;
            if (AtEnd)
            {
                return false;
            }

            if (Peek == '\\' && Peek1 == 'u')
            {
                if (_pos + 6 > _pattern.Length || (_pos + 2 < _pattern.Length && _pattern[_pos + 2] == '{'))
                {
                    return false;
                }

                var hex = _pattern.Substring(_pos + 2, 4);
                if (!int.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier,
                        System.Globalization.CultureInfo.InvariantCulture, out var escaped) ||
                    escaped < 0xDC00 || escaped > 0xDFFF)
                {
                    return false;
                }

                _pos += 6;
                lowSurrogate = escaped;
                return true;
            }

            if (char.IsLowSurrogate(Peek))
            {
                lowSurrogate = Peek;
                Advance();
                return true;
            }

            return false;
        }

        private ClassItem TryCombineClassSurrogatePair(ClassItem item)
        {
            if (!IsUnicode)
            {
                return item;
            }

            var codePoint = item switch
            {
                ClassLiteralChar literal => literal.Value,
                ClassEscape escaped => escaped.CodePoint,
                _ => -1
            };

            if (codePoint is < 0xD800 or > 0xDBFF || !TryConsumeTrailingLowSurrogate(out var lowSurrogate))
            {
                return item;
            }

            return new ClassEscape(char.ConvertToUtf32((char)codePoint, (char)lowSurrogate));
        }

        private ClassItem ParseUnicodePropertyInClass(char pOrP)
        {
            if (AtEnd || Peek != '{')
                throw new RegexSyntaxError("Invalid Unicode property escape", _pos);

            Advance(); // consume '{'
            var bodyStart = _pos;
            while (!AtEnd && Peek != '}')
                Advance();
            if (AtEnd)
                throw new RegexSyntaxError("Unterminated Unicode property escape", bodyStart - 1);
            var body = _pattern[bodyStart.._pos];
            Expect('}');

            if (!IsUnicode)
                throw new RegexSyntaxError("Unicode property escapes require Unicode flag", _pos);

            var negated = pOrP == 'P';
            var (property, value) = ParsePropertyNameAndValue(body);
            return new ClassUnicodeProperty(property, value, negated);
        }

        /// <summary>Returns true if the next two characters are a set operator (&&, --, ~~).</summary>
        private bool IsRawSetOperatorStart()
        {
            if (AtEnd) return false;
            var c = Peek;
            var c2 = Peek1;
            return (c == '&' && c2 == '&') || (c == '-' && c2 == '-') || (c == '~' && c2 == '~');
        }

        private static int GetClassItemSingleCodePoint(ClassItem item)
        {
            return item switch
            {
                ClassLiteralChar l => l.Value,
                ClassEscape e => e.CodePoint,
                _ => -1
            };
        }

        // ─── Group ─────────────────────────────────────────────

        private AtomNode ParseGroup()
        {
            Expect('(');

            if (!AtEnd && Peek == '?')
            {
                return ParseExtendedGroup();
            }

            // Plain capturing group: ( ... )
            var groupNum = ++_captureCount;
            _groupCount++;
            _groupPositions.Add(_pos - 1);
            var body = ParseDisjunction();
            Expect(')');
            return new GroupNode(GroupKind.Capturing, body, groupNum);
        }

        private enum GroupPrefixKind
        {
            None,
            NonCapturing,
            Lookahead,
            NegativeLookahead,
            Lookbehind,
            NegativeLookbehind,
            NamedCapturing,
            Modifiers
        }

        private GroupPrefixKind TryParseGroupPrefix()
        {
            Expect('(');
            if (AtEnd || Peek != '?')
            {
                // shouldn't happen — caller checks this
                return GroupPrefixKind.None;
            }

            Expect('?');

            if (AtEnd) return GroupPrefixKind.None;

            var marker = Peek;
            if (marker == ':')
            {
                Advance();
                return GroupPrefixKind.NonCapturing;
            }

            if (marker == '=')
            {
                Advance();
                return GroupPrefixKind.Lookahead;
            }

            if (marker == '!')
            {
                Advance();
                return GroupPrefixKind.NegativeLookahead;
            }

            if (marker == '<')
            {
                Advance();
                if (AtEnd) return GroupPrefixKind.None;

                var nextMarker = Peek;
                if (nextMarker == '=')
                {
                    Advance();
                    return GroupPrefixKind.Lookbehind;
                }
                if (nextMarker == '!')
                {
                    Advance();
                    return GroupPrefixKind.NegativeLookbehind;
                }
                return GroupPrefixKind.NamedCapturing;
            }

            // Inline modifiers: (?ims-ims:...) or (?ims:...)
            if (marker == 'i' || marker == 'm' || marker == 's' || marker == '-')
            {
                return GroupPrefixKind.Modifiers;
            }

            return GroupPrefixKind.None;
        }

        private AtomNode ParseExtendedGroup()
        {
            // We're at the position just after '(?'
            // The '?' has already been consumed by the caller pattern.
            // Actually, looking at the code flow above, ParseGroup has already
            // consumed '(' but NOT '?'. Let me re-check...

            // ParseGroup: Expect('('); then checks Peek=='?'
            // So we're past '(' but at '?'.
            var saved = _pos;
            Expect('?');

            var marker = Peek;

            // Non-capturing: (?: ... )
            if (marker == ':')
            {
                Advance();
                _groupCount++;
                _groupPositions.Add(-1); // non-capturing placeholder
                var body = ParseDisjunction();
                Expect(')');
                return new GroupNode(GroupKind.NonCapturing, body, 0);
            }

            if (marker == '=' || marker == '!')
            {
                Advance();
                var body = ParseDisjunction();
                Expect(')');
                return new AssertionNode(
                    marker == '=' ? AssertionKind.Lookahead : AssertionKind.NegativeLookahead,
                    body);
            }

            // Named capturing: (?<name> ... )
            if (marker == '<')
            {
                Advance();
                if (!AtEnd && (Peek == '=' || Peek == '!'))
                {
                    var isNegative = Peek == '!';
                    Advance();
                    var lookbehindBody = ParseDisjunction();
                    Expect(')');
                    return new AssertionNode(
                        isNegative ? AssertionKind.NegativeLookbehind : AssertionKind.Lookbehind,
                        lookbehindBody);
                }

                if (AtEnd)
                {
                    throw new RegexSyntaxError("Invalid group name", _pos);
                }

                var name = ParseGroupName();
                Expect('>');

                var groupNum = ++_captureCount;
                _groupCount++;
                _groupPositions.Add(saved);
                _namedGroups[name] = groupNum;

                var body = ParseDisjunction();
                Expect(')');
                return new GroupNode(GroupKind.NamedCapturing, body, groupNum, name);
            }

            // Inline modifiers: (?ims-ims:...) or (?ims:...)
            if (marker == 'i' || marker == 'm' || marker == 's' || marker == '-')
            {
                return ParseModifierGroup();
            }

            throw new RegexSyntaxError($"Invalid group syntax '(?{marker}...'", _pos);
        }

        private string ParseGroupName()
        {
            var start = _pos;
            var builder = new StringBuilder();
            while (!AtEnd && TryConsumeGroupNameCodePoint(out var codePoint))
            {
                AppendCodePoint(builder, codePoint);
            }

            if (_pos == start)
                throw new RegexSyntaxError("Empty group name", _pos);

            return builder.ToString();
        }

        /// <summary>
        /// If the next character(s) at _pos form a valid RegExpIdentifierName
        /// code point, advance _pos and return true. Handles both BMP and
        /// supplementary-plane characters via surrogate pairs.
        /// </summary>
        private bool TryConsumeGroupNameCodePoint(out int codePoint)
        {
            codePoint = 0;
            if (AtEnd) return false;
            var ch = Peek;
            int advance;

            if (ch == '\\' && Peek1 == 'u' && TryReadUnicodeEscape(_pos, out var escapedCodePoint, out var escapedLength))
            {
                codePoint = escapedCodePoint;
                advance = escapedLength;
            }
            else if (char.IsHighSurrogate(ch) && _pos + 1 < _pattern.Length && char.IsLowSurrogate(_pattern[_pos + 1]))
            {
                codePoint = char.ConvertToUtf32(ch, _pattern[_pos + 1]);
                advance = 2;
            }
            else
            {
                codePoint = ch;
                advance = 1;
            }

            // Valid group name characters: $, _, letters (any Unicode), digits, ZWNJ, ZWJ.
            // ECMA-262 22.2.2.2: RegExpIdentifierPart :: IdentifierPartChar | $ | ‌ | ‍
            if (codePoint == '$' || codePoint == '_' || codePoint == 0x200C || codePoint == 0x200D)
            {
                _pos += advance;
                return true;
            }

            try
            {
                var rune = new Rune((uint)codePoint);
                var cat = Rune.GetUnicodeCategory(rune);
                var valid = cat is >= System.Globalization.UnicodeCategory.UppercaseLetter and <= System.Globalization.UnicodeCategory.OtherLetter
                                 or System.Globalization.UnicodeCategory.LetterNumber
                                 or System.Globalization.UnicodeCategory.DecimalDigitNumber
                                 or System.Globalization.UnicodeCategory.NonSpacingMark
                                 or System.Globalization.UnicodeCategory.SpacingCombiningMark
                                 or System.Globalization.UnicodeCategory.ConnectorPunctuation;
                if (valid)
                {
                    _pos += advance;
                    return true;
                }
            }
            catch
            {
                // Invalid code point
            }

            return false;
        }

        private bool TryReadUnicodeEscape(int start, out int codePoint, out int length)
        {
            codePoint = 0;
            length = 0;

            if (start + 1 >= _pattern.Length || _pattern[start] != '\\' || _pattern[start + 1] != 'u')
            {
                return false;
            }

            if (start + 2 < _pattern.Length && _pattern[start + 2] == '{')
            {
                var pos = start + 3;
                var value = 0;
                var digitCount = 0;
                while (pos < _pattern.Length && _pattern[pos] != '}')
                {
                    if (!TryHexValue(_pattern[pos], out var digit))
                    {
                        return false;
                    }

                    value = checked((value * 16) + digit);
                    digitCount++;
                    if (value > 0x10FFFF)
                    {
                        return false;
                    }

                    pos++;
                }

                if (pos >= _pattern.Length || _pattern[pos] != '}' || digitCount == 0)
                {
                    return false;
                }

                codePoint = value;
                length = pos - start + 1;
                return true;
            }

            if (start + 5 >= _pattern.Length)
            {
                return false;
            }

            var bmp = 0;
            for (var i = start + 2; i < start + 6; i++)
            {
                if (!TryHexValue(_pattern[i], out var digit))
                {
                    return false;
                }

                bmp = (bmp * 16) + digit;
            }

            if (char.IsHighSurrogate((char)bmp) &&
                start + 11 < _pattern.Length &&
                _pattern[start + 6] == '\\' &&
                _pattern[start + 7] == 'u' &&
                TryReadFourDigitUnicodeEscape(start + 8, out var low) &&
                char.IsLowSurrogate((char)low))
            {
                codePoint = char.ConvertToUtf32((char)bmp, (char)low);
                length = 12;
                return true;
            }

            codePoint = bmp;
            length = 6;
            return true;
        }

        private bool TryReadFourDigitUnicodeEscape(int start, out int codePoint)
        {
            codePoint = 0;
            if (start + 3 >= _pattern.Length)
            {
                return false;
            }

            for (var i = start; i < start + 4; i++)
            {
                if (!TryHexValue(_pattern[i], out var digit))
                {
                    return false;
                }

                codePoint = (codePoint * 16) + digit;
            }

            return true;
        }

        private static bool TryHexValue(char ch, out int value)
        {
            if (ch is >= '0' and <= '9')
            {
                value = ch - '0';
                return true;
            }

            if (ch is >= 'A' and <= 'F')
            {
                value = ch - 'A' + 10;
                return true;
            }

            if (ch is >= 'a' and <= 'f')
            {
                value = ch - 'a' + 10;
                return true;
            }

            value = 0;
            return false;
        }

        private static void AppendCodePoint(StringBuilder builder, int codePoint)
        {
            if (codePoint <= 0xFFFF)
            {
                builder.Append((char)codePoint);
                return;
            }

            builder.Append(char.ConvertFromUtf32(codePoint));
        }

        private static bool ContainsNamedCaptureSyntax(string pattern)
        {
            var inClass = false;
            for (var i = 0; i < pattern.Length; i++)
            {
                var ch = pattern[i];
                if (ch == '\\')
                {
                    i++;
                    continue;
                }

                if (ch == '[')
                {
                    inClass = true;
                    continue;
                }

                if (ch == ']' && inClass)
                {
                    inClass = false;
                    continue;
                }

                if (!inClass &&
                    ch == '(' &&
                    i + 2 < pattern.Length &&
                    pattern[i + 1] == '?' &&
                    pattern[i + 2] == '<' &&
                    (i + 3 >= pattern.Length || pattern[i + 3] is not ('=' or '!')))
                {
                    return true;
                }
            }

            return false;
        }

        // ─── Inline Modifiers ──────────────────────────────────

        private AtomNode ParseModifierGroup()
        {
            // Parse: (?flags1-flags2: ... ) — flags can be i, m, s.
            // The add side may be empty when a '-' side follows ((?-i:...)).
            var addFlags = ParseOptionalModifierFlags();
            string? removeFlags = null;

            if (!AtEnd && Peek == '-')
            {
                Advance();
                removeFlags = ParseOptionalModifierFlags();
            }

            // Early errors per the regexp-modifiers proposal (Atom ::
            // ( ? RegularExpressionFlags - RegularExpressionFlags : Disjunction )):
            // at least one side must be non-empty, no flag may repeat within a
            // side, and no flag may appear on both sides.
            if (addFlags.Length == 0 && (removeFlags is null || removeFlags.Length == 0))
            {
                throw new RegexSyntaxError("Empty regular expression modifiers", _pos);
            }

            ThrowIfDuplicateModifierFlags(addFlags);
            if (removeFlags is not null)
            {
                ThrowIfDuplicateModifierFlags(removeFlags);
                foreach (var flag in addFlags)
                {
                    if (removeFlags.Contains(flag))
                    {
                        throw new RegexSyntaxError($"Modifier flag '{flag}' cannot be both added and removed", _pos);
                    }
                }
            }

            Expect(':');
            var body = ParseDisjunction();
            Expect(')');

            _groupCount++;
            _groupPositions.Add(-1);
            return new ModifierGroupNode(body, addFlags, removeFlags ?? string.Empty);
        }

        private string ParseOptionalModifierFlags()
        {
            var start = _pos;
            while (!AtEnd && (Peek == 'i' || Peek == 'm' || Peek == 's'))
                Advance();

            return _pattern[start.._pos];
        }

        private void ThrowIfDuplicateModifierFlags(string flags)
        {
            for (var i = 0; i < flags.Length; i++)
            {
                for (var j = i + 1; j < flags.Length; j++)
                {
                    if (flags[i] == flags[j])
                    {
                        throw new RegexSyntaxError($"Duplicate modifier flag '{flags[i]}'", _pos);
                    }
                }
            }
        }

        // ─── Quantifier ────────────────────────────────────────

        private bool IsQuantifierStart()
        {
            var ch = Peek;
            return ch == '*' || ch == '+' || ch == '?' || (ch == '{' && IsValidBraceQuantifierAt(_pos));
        }

        private bool IsValidBraceQuantifierAt(int position)
        {
            if (position >= _pattern.Length || _pattern[position] != '{')
                return false;

            var cursor = position + 1;
            var digitsStart = cursor;
            while (cursor < _pattern.Length && IsDecimalDigit(_pattern[cursor]))
                cursor++;

            if (cursor == digitsStart)
                return false;

            if (cursor < _pattern.Length && _pattern[cursor] == '}')
                return true;

            if (cursor >= _pattern.Length || _pattern[cursor] != ',')
                return false;

            cursor++;
            while (cursor < _pattern.Length && IsDecimalDigit(_pattern[cursor]))
                cursor++;

            return cursor < _pattern.Length && _pattern[cursor] == '}';
        }

        private QuantifierNode ParseQuantifier(AtomNode body)
        {
            var ch = Peek;
            int min, max;

            switch (ch)
            {
                case '*':
                    Advance();
                    min = 0; max = int.MaxValue;
                    break;
                case '+':
                    Advance();
                    min = 1; max = int.MaxValue;
                    break;
                case '?':
                    Advance();
                    min = 0; max = 1;
                    break;
                case '{':
                    (min, max) = ParseBraceQuantifier();
                    break;
                default:
                    throw new RegexSyntaxError($"Unexpected quantifier '{ch}'", _pos);
            }

            var greedy = true;
            if (!AtEnd && Peek == '?')
            {
                Advance();
                greedy = false;
            }

            // ECMAScript disallows quantifiers on lookahead/lookbehind in Unicode mode.
            // The caller (ParseTerm) handles this by not calling ParseQuantifier
            // for assertions, so we only get here for atoms.

            return new QuantifierNode(body, min, max, greedy);
        }

        private (int min, int max) ParseBraceQuantifier()
        {
            Expect('{');
            var numStart = _pos;

            while (!AtEnd && IsDecimalDigit(Peek))
                Advance();

            if (_pos == numStart)
            {
                // Not a valid quantifier — treat { as literal
                // Backtrack: the caller handles this.
                // Actually in ECMAScript, { that isn't a valid quantifier
                // is literal in non-Unicode mode and error in Unicode mode.
                throw new RegexSyntaxError("Invalid quantifier", numStart - 1);
            }

            var num1Str = _pattern[numStart.._pos];
            var min = ParseQuantifierInteger(num1Str);

            if (AtEnd)
                throw new RegexSyntaxError("Unterminated quantifier", numStart - 1);

            if (Peek == '}')
            {
                Advance();
                return (min, min); // {n} — exactly n
            }

            if (Peek != ',')
                throw new RegexSyntaxError("Invalid quantifier", _pos);

            Advance(); // consume ','

            if (AtEnd)
                throw new RegexSyntaxError("Unterminated quantifier", numStart - 1);

            if (Peek == '}')
            {
                Advance();
                return (min, int.MaxValue); // {n,} — at least n
            }

            numStart = _pos;
            while (!AtEnd && IsDecimalDigit(Peek))
                Advance();

            if (_pos == numStart)
                throw new RegexSyntaxError("Invalid quantifier", numStart);

            var max = ParseQuantifierInteger(_pattern[numStart.._pos]);

            if (AtEnd || Peek != '}')
                throw new RegexSyntaxError("Unterminated quantifier", numStart);

            Expect('}');
            if (max < min)
            {
                throw new RegexSyntaxError("Quantifier maximum is less than minimum", numStart);
            }

            return (min, max);
        }

        // ─── Helpers ───────────────────────────────────────────

        private static int ParseQuantifierInteger(string digits)
        {
            return int.TryParse(digits, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value)
                ? value
                : int.MaxValue;
        }

        private static bool IsDecimalDigit(char c) => c is >= '0' and <= '9';
        private static bool IsHexDigit(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');
    }
}
