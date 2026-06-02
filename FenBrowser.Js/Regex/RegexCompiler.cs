// ECMA-262 §22.2 — Regex AST → bytecode compiler.
//
// Walks the RegexPattern AST and emits a flat RegexInstruction[] for the backtracking VM.
// Key compilation patterns:
//   - Disjunction: Split to alternatives, Jump to end after each alt
//   - Quantifier *: Split(body, skip) ; body ; Jump(back to split) ; skip:
//   - Quantifier +: body ; Split(back to body, accept)
//   - Quantifier ?: Split(body, skip)
//   - Group: Save(startSlot) ; body ; Save(endSlot)

using System.Text;

namespace FenBrowser.Js.Regex;

public static class RegexCompiler
{
    public static RegexProgram Compile(RegexPattern pattern)
    {
        var c = new CompilerState(pattern.Flags, pattern.CaptureCount);
        var bodyStart = c.EmitDisjunction(pattern.Disjunction);
        c.Emit(RegexOpCode.Accept);

        // Patch jumps
        c.Patch();

        var program = new RegexProgram(
            c._instructions.ToArray(),
            pattern.CaptureCount,
            c._namedGroupMap.Count > 0 ? new Dictionary<string, int>(c._namedGroupMap) : null,
            pattern.Flags)
        {
            UnicodePropertyBodies = c._unicodePropertyBodies.Count > 0
                ? c._unicodePropertyBodies.ToArray()
                : null
        };

        return program;
    }

    private sealed class CompilerState
    {
        private readonly RegexFlags _flags;
        private readonly int _captureCount;
        internal readonly List<RegexInstruction> _instructions = new();
        internal readonly Dictionary<string, int> _namedGroupMap = new(StringComparer.Ordinal);
        internal readonly List<string> _unicodePropertyBodies = new();

        // Pending jumps that need address resolution (position → target label)
        private readonly Dictionary<int, int> _pendingJumps = new();

        public CompilerState(RegexFlags flags, int captureCount)
        {
            _flags = flags;
            _captureCount = captureCount;
        }

        private bool IsUnicode => _flags.Unicode || _flags.UnicodeSets;
        private bool IgnoreCase => _flags.IgnoreCase;
        private bool Multiline => _flags.Multiline;
        private bool DotAll => _flags.DotAll;

        // ─── Instruction emission ─────────────────────────

        internal int Emit(RegexOpCode op, int a = 0, int b = 0, int c = 0)
        {
            _instructions.Add(new RegexInstruction(op, a, b, c));
            return _instructions.Count - 1;
        }

        private int EmitPlaceholder(RegexOpCode op)
        {
            return Emit(op, -1, 0, 0);
        }

        private void PatchJump(int fromIndex, int toIndex)
        {
            // Relative offset: distance from the instruction AFTER the jump
            var offset = toIndex - fromIndex - 1;
            var ins = _instructions[fromIndex];
            _instructions[fromIndex] = new RegexInstruction(ins.OpCode, offset, ins.B, ins.C);
        }

        private void PatchSplit(int fromIndex, int toAlt1, int toAlt2)
        {
            var offset1 = toAlt1 - fromIndex - 1;
            var offset2 = toAlt2 - fromIndex - 1;
            var ins = _instructions[fromIndex];
            _instructions[fromIndex] = new RegexInstruction(ins.OpCode, offset1, offset2, ins.C);
        }

        internal void Patch()
        {
            foreach (var (from, to) in _pendingJumps)
            {
                PatchJump(from, to);
            }

            _pendingJumps.Clear();
        }

        private int CurrentPos => _instructions.Count;

        private int ReserveJump(RegexOpCode op = RegexOpCode.Jump)
        {
            var pos = Emit(op, -999, 0, 0);
            return pos;
        }

        private int ReserveSplit()
        {
            var pos = Emit(RegexOpCode.Split, -999, -999, 0);
            return pos;
        }

        // ─── Top-level compilation ────────────────────────

        internal int EmitDisjunction(DisjunctionNode disjunction)
        {
            if (disjunction.Alternatives.Count == 1)
            {
                return EmitAlternative(disjunction.Alternatives[0]);
            }

            // Multiple alternatives: Split to each.
            // Pattern: Split(alt1, alt2_or_next) ; alt1 ; Jump(end) ; alt2 ; ...
            var altCount = disjunction.Alternatives.Count;
            var altStarts = new int[altCount];
            var altJumps = new int[altCount];

            // Emit placeholder splits that we patch later
            var splitPositions = new List<int>();
            for (int a = 0; a < altCount - 1; a++)
            {
                splitPositions.Add(ReserveSplit());
            }

            // Compile each alternative
            for (int a = 0; a < altCount; a++)
            {
                altStarts[a] = CurrentPos;
                EmitAlternative(disjunction.Alternatives[a]);
                if (a < altCount - 1)
                {
                    altJumps[a] = ReserveJump();
                }
            }

            var endPos = CurrentPos;

            // Patch splits: split[i] → altStarts[i], start of next split (or next alt)
            for (int a = 0; a < altCount - 1; a++)
            {
                var nextPos = a + 1 < splitPositions.Count ? splitPositions[a + 1] : altStarts[a + 1];
                PatchSplit(splitPositions[a], altStarts[a], nextPos);
            }

            // Patch end jumps
            for (int a = 0; a < altCount - 1; a++)
            {
                PatchJump(altJumps[a], endPos);
            }

            return splitPositions.Count > 0 ? splitPositions[0] : altStarts[0];
        }

        private int EmitAlternative(AlternativeNode alt)
        {
            var start = CurrentPos;
            foreach (var term in alt.Terms)
            {
                EmitTerm(term);
            }

            return start;
        }

        // ─── Term compilation ─────────────────────────────

        private void EmitTerm(TermNode term)
        {
            switch (term)
            {
                case AssertionNode assertion:
                    EmitAssertion(assertion);
                    break;
                case QuantifierNode quantifier:
                    EmitQuantifier(quantifier);
                    break;
                case AtomNode atom:
                    EmitAtom(atom);
                    break;
            }
        }

        // ─── Assertion compilation ────────────────────────

        private void EmitAssertion(AssertionNode assertion)
        {
            switch (assertion.Kind)
            {
                case AssertionKind.BeginOfInput:
                    Emit(RegexOpCode.Bol, Multiline ? 1 : 0);
                    break;
                case AssertionKind.EndOfInput:
                    Emit(RegexOpCode.Eol, Multiline ? 1 : 0);
                    break;
                case AssertionKind.WordBoundary:
                    Emit(RegexOpCode.WordBoundary);
                    break;
                case AssertionKind.NonWordBoundary:
                    Emit(RegexOpCode.NonWordBoundary);
                    break;
                case AssertionKind.Lookahead:
                case AssertionKind.NegativeLookahead:
                case AssertionKind.Lookbehind:
                case AssertionKind.NegativeLookbehind:
                    EmitLookaround(assertion);
                    break;
            }
        }

        private void EmitLookaround(AssertionNode assertion)
        {
            if (assertion.Body == null) return;

            var isNegative = assertion.Kind is AssertionKind.NegativeLookahead or AssertionKind.NegativeLookbehind;
            var isLookbehind = assertion.Kind is AssertionKind.Lookbehind or AssertionKind.NegativeLookbehind;

            var bodyStart = CurrentPos;
            EmitDisjunction(assertion.Body);
            // Emit Accept so sub-match knows where the body ends
            Emit(RegexOpCode.Accept);

            // Store offset back to the body start, for the VM to use as a jump target.
            // The offset includes the Accept so the sub-match can reach it.
            var bodyOffset = bodyStart - CurrentPos;

            if (isLookbehind)
            {
                Emit(isNegative ? RegexOpCode.NegLookbehind : RegexOpCode.Lookbehind, bodyOffset);
            }
            else
            {
                Emit(isNegative ? RegexOpCode.NegLookahead : RegexOpCode.Lookahead, bodyOffset);
            }
        }

        // ─── Quantifier compilation ────────────────────────

        private void EmitQuantifier(QuantifierNode q)
        {
            var body = q.Body;
            var isBounded = q.Max != int.MaxValue;

            if (q.Min == 0 && q.Max == int.MaxValue)
            {
                // A*  — greedy, A*? — lazy
                EmitStar(body, q.Greedy);
            }
            else if (q.Min == 1 && q.Max == int.MaxValue)
            {
                // A+  — greedy, A+? — lazy
                EmitPlus(body, q.Greedy);
            }
            else if (q.Min == 0 && q.Max == 1)
            {
                // A?  — greedy, A?? — lazy
                EmitOptional(body, q.Greedy);
            }
            else if (isBounded)
            {
                EmitBounded(body, q.Min, q.Max, q.Greedy);
            }
            else
            {
                // {n,} unbounded
                EmitAtLeast(body, q.Min, q.Greedy);
            }
        }

        private void EmitStar(AtomNode body, bool greedy)
        {
            // Greedy A* : Split L_body, L_skip ; L_body: body ; Jump L_loop ; L_skip:
            // Lazy A*? : Split L_skip, L_body ; L_body: body ; Jump L_loop ; L_skip:
            var loopPos = CurrentPos;
            var splitPos = Emit(RegexOpCode.Split, 0, 0);
            var bodyStart = CurrentPos;
            EmitAtom(body);
            var jumpPos = Emit(RegexOpCode.Jump, 0);
            PatchJump(jumpPos, loopPos);
            var skipPos = CurrentPos;

            if (greedy)
                PatchSplit(splitPos, bodyStart, skipPos);  // try body first
            else
                PatchSplit(splitPos, skipPos, bodyStart);  // try skip first
        }

        private void EmitPlus(AtomNode body, bool greedy)
        {
            // Greedy A+ : body ; Split L_body, L_skip ; L_skip:
            // Lazy A+? : body ; Split L_skip, L_body ; L_skip:
            var bodyStart = CurrentPos;
            EmitAtom(body);
            var splitPos = Emit(RegexOpCode.Split, 0, 0);
            var skipPos = CurrentPos;

            if (greedy)
                PatchSplit(splitPos, bodyStart, skipPos);
            else
                PatchSplit(splitPos, skipPos, bodyStart);
        }

        private void EmitOptional(AtomNode body, bool greedy)
        {
            // Greedy A? : Split L_body, L_skip ; L_body: body ; L_skip:
            // Lazy A?? : Split L_skip, L_body ; L_body: body ; L_skip:
            var splitPos = Emit(RegexOpCode.Split, 0, 0);
            var bodyStart = CurrentPos;
            EmitAtom(body);
            var skipPos = CurrentPos;

            if (greedy)
                PatchSplit(splitPos, bodyStart, skipPos);
            else
                PatchSplit(splitPos, skipPos, bodyStart);
        }

        private void EmitBounded(AtomNode body, int min, int max, bool greedy)
        {
            // {n,m}: unroll into sequence.
            // First emit mandatory repetitions (min times):
            for (int i = 0; i < min; i++)
            {
                EmitAtom(body);
            }

            // Then emit optional repetitions (max - min times):
            var optionalCount = max - min;
            for (int i = 0; i < optionalCount; i++)
            {
                var splitPos = Emit(RegexOpCode.Split, 0, 0);
                var bodyStart = CurrentPos;
                EmitAtom(body);
                var skipPos = CurrentPos;
                if (greedy)
                    PatchSplit(splitPos, bodyStart, skipPos);
                else
                    PatchSplit(splitPos, skipPos, bodyStart);
            }
        }

        private void EmitAtLeast(AtomNode body, int min, bool greedy)
        {
            // {n,}: emit n mandatory then * for the rest
            for (int i = 0; i < min; i++)
            {
                EmitAtom(body);
            }

            EmitStar(body, greedy);
        }

        // ─── Atom compilation ──────────────────────────────

        private void EmitAtom(AtomNode atom)
        {
            switch (atom)
            {
                case LiteralCharNode lit:
                    EmitChar(lit.Value);
                    break;
                case DotNode:
                    Emit(RegexOpCode.Dot, DotAll ? 1 : 0);
                    break;
                case CharacterEscapeNode esc:
                    EmitChar(esc.CodePoint);
                    break;
                case ClassEscapeNode ce:
                    EmitClassEscape(ce.Kind);
                    break;
                case UnicodePropertyNode up:
                    EmitUnicodeProp(up);
                    break;
                case BackReferenceNode br:
                    Emit(RegexOpCode.BackRef, br.GroupNumber, IgnoreCase ? 1 : 0);
                    break;
                case CharacterClassNode cc:
                    EmitCharacterClass(cc);
                    break;
                case GroupNode g:
                    EmitGroup(g);
                    break;
            }
        }

        private void EmitChar(int codePoint)
        {
            // B=1 tells the VM to do case-insensitive comparison.
            // This is more correct than CharRange(lo, hi) which matches
            // intervening code points between upper and lower case.
            Emit(RegexOpCode.Char, codePoint, b: IgnoreCase ? 1 : 0);
        }

        private static int SimpleCaseFold(int cp)
        {
            // Simple case folding for /i flag (ECMA-262 §22.2.2.1.2).
            // Fast path for ASCII and Latin-1.
            if (cp is >= 'A' and <= 'Z')
                return cp + 32;
            if (cp is >= 'a' and <= 'z')
                return cp - 32;
            if (cp is >= 0xC0 and <= 0xD6)
                return cp + 32;
            if (cp is >= 0xD8 and <= 0xDE)
                return cp + 32;
            if (cp is >= 0xE0 and <= 0xF6)
                return cp - 32;
            if (cp is >= 0xF8 and <= 0xFE)
                return cp - 32;
            // Full Unicode: use Rune.ToLowerInvariant
            try
            {
                var lower = Rune.ToLowerInvariant(new Rune((uint)cp));
                if (lower.Value != (uint)cp)
                    return (int)lower.Value;
            }
            catch
            {
                // Rune creation failed — return unchanged
            }
            return cp;
        }

        /// <summary>
        /// For /i mode, emit the case-folded counterpart of a character range.
        /// ASCII-only for now (A-Z ↔ a-z).
        /// </summary>
        private void EmitCaseFoldedRange(int start, int end)
        {
            // Only fold pure ASCII letter ranges for now
            if (start is >= 'A' and <= 'Z' && end is >= 'A' and <= 'Z')
            {
                // Uppercase range → also emit lowercase range
                Emit(RegexOpCode.CharRange, start + 32, end + 32);
            }
            else if (start is >= 'a' and <= 'z' && end is >= 'a' and <= 'z')
            {
                // Lowercase range → also emit uppercase range
                Emit(RegexOpCode.CharRange, start - 32, end - 32);
            }
            else
            {
                // For non-ASCII ranges, emit individual folded chars
                // (simplified: only fold if start==end, i.e. single char)
                if (start == end)
                {
                    var folded = SimpleCaseFold(start);
                    if (folded != start)
                        Emit(RegexOpCode.Char, folded);
                }
                // Otherwise skip — full Unicode case folding is complex
            }
        }

        private void EmitClassEscape(char kind)
        {
            var classKind = kind switch
            {
                'd' => CharClassKind.Digit,
                'D' => CharClassKind.NotDigit,
                'w' => CharClassKind.Word,
                'W' => CharClassKind.NotWord,
                's' => CharClassKind.Space,
                'S' => CharClassKind.NotSpace,
                _ => throw new InvalidOperationException($"Unknown class escape: {kind}")
            };
            Emit(RegexOpCode.CharClass, (int)classKind);
        }

        private void EmitUnicodeProp(UnicodePropertyNode up)
        {
            // Build the property body string (e.g. "General_Category=Letter" or "Emoji")
            var body = up.Value is not null
                ? up.Property + "=" + up.Value
                : up.Property;
            // Store the body in the program's property table and emit its index.
            // Operand A = negated flag. Operand B = index into UnicodePropertyBodies.
            var index = _unicodePropertyBodies.Count;
            _unicodePropertyBodies.Add(body);
            Emit(RegexOpCode.UnicodeProp, up.Negated ? 1 : 0, index, 0);
        }

        private void EmitCharacterClass(CharacterClassNode cc)
        {
            if (cc.Negated)
            {
                // Negated class [^...]: match any char NOT in the set.
                // Strategy: peek-negate each item (B=peek, C=negated).
                // If any item matches the current char → backtrack.
                // If no item matches → consume one char via Dot.
                foreach (var item in cc.Items)
                {
                    switch (item)
                    {
                        case ClassLiteralChar lc:
                            Emit(RegexOpCode.Char, lc.Value, c: 1);
                            break;
                        case ClassRange cr:
                            Emit(RegexOpCode.CharRange, cr.Start, cr.End, c: 1);
                            break;
                        case ClassEscape ce:
                            Emit(RegexOpCode.Char, ce.CodePoint, c: 1);
                            break;
                        case ClassClassEscape cce:
                            EmitClassEscapePeekNegated(cce.Kind);
                            break;
                        case ClassUnicodeProperty cup:
                            // Peek-negate: if char HAS the property → backtrack.
                            // For \p{X} in [^...]: cup.Negated=false, we want CharClass to succeed when
                            // property matches, then negate → backtrack. So emit with C=1.
                            EmitUnicodeProp(new UnicodePropertyNode(cup.Property, cup.Value, cup.Negated));
                            // Override: mark as peek-negated
                            {
                                var lastIdx = _instructions.Count - 1;
                                var last = _instructions[lastIdx];
                                _instructions[lastIdx] = new RegexInstruction(last.OpCode, last.A, last.B, C: 1);
                            }
                            break;
                    }
                }

                // No item matched — consume one character
                Emit(RegexOpCode.Dot, DotAll ? 1 : 0);
                return;
            }

            // Positive class [abc...]: original behaviour — match any item, consume cp
            foreach (var item in cc.Items)
            {
                switch (item)
                {
                    case ClassLiteralChar lc:
                        EmitChar(lc.Value);
                        break;
                    case ClassRange cr:
                        Emit(RegexOpCode.CharRange, cr.Start, cr.End);
                        // With /i, also emit the case-folded range for ASCII letter ranges
                        if (IgnoreCase)
                        {
                            EmitCaseFoldedRange(cr.Start, cr.End);
                        }
                        break;
                    case ClassEscape ce:
                        EmitChar(ce.CodePoint);
                        break;
                    case ClassClassEscape cce:
                        EmitClassEscape(cce.Kind);
                        break;
                    case ClassUnicodeProperty cup:
                        EmitUnicodeProp(new UnicodePropertyNode(cup.Property, cup.Value, cup.Negated));
                        break;
                }
            }
        }

        private void EmitClassEscapePeekNegated(char kind)
        {
            // Emit a peek-negated class escape for negated character classes.
            // For [^\d]: peek, if char IS a digit → backtrack (negated).
            // So we want to fail when CharClass matches — use C=1 (negated).
            var classKind = kind switch
            {
                'd' => CharClassKind.Digit,
                'D' => CharClassKind.NotDigit,
                'w' => CharClassKind.Word,
                'W' => CharClassKind.NotWord,
                's' => CharClassKind.Space,
                'S' => CharClassKind.NotSpace,
                _ => throw new InvalidOperationException($"Unknown class escape: {kind}")
            };
            // For [^\d]: char IS digit → fail. CharClassKind.Digit succeeds for digits.
            // B=1 (peek), C=1 (negate result)
            Emit(RegexOpCode.CharClass, (int)classKind, c: 1);
        }

        private void EmitGroup(GroupNode group)
        {
            if (group.Kind == GroupKind.Capturing || group.Kind == GroupKind.NamedCapturing)
            {
                var n = group.GroupNumber;
                // Save start position
                Emit(RegexOpCode.Save, n * 2);
            }

            EmitDisjunction(group.Body);

            if (group.Kind == GroupKind.Capturing || group.Kind == GroupKind.NamedCapturing)
            {
                var n = group.GroupNumber;
                // Save end position
                Emit(RegexOpCode.Save, n * 2 + 1);
            }
        }
    }
}
