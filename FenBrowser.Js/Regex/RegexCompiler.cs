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
        c.CollectNamedGroups(pattern.Disjunction);
        var bodyStart = c.EmitDisjunction(pattern.Disjunction);
        c.Emit(RegexOpCode.Accept);

        // Patch jumps
        c.Patch();

        var program = new RegexProgram(
            c._instructions.ToArray(),
            pattern.CaptureCount,
            c._namedGroupMap.Count > 0
                ? c._namedGroupMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray(), StringComparer.Ordinal)
                : null,
            pattern.Flags,
            c._namedBackReferenceNames.Count > 0 ? c._namedBackReferenceNames.ToArray() : null)
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
        internal readonly Dictionary<string, List<int>> _namedGroupMap = new(StringComparer.Ordinal);
        internal readonly List<string> _namedBackReferenceNames = new();
        private readonly Dictionary<string, int> _namedBackReferenceNameIndices = new(StringComparer.Ordinal);
        internal readonly List<string> _unicodePropertyBodies = new();

        // Pending jumps that need address resolution (position → target label)
        private readonly Dictionary<int, int> _pendingJumps = new();

        // Current modifier state — the pattern flags, possibly overridden inside
        // (?ims-ims:...) modifier groups (regexp-modifiers proposal, UpdateModifiers).
        private bool _ignoreCase;
        private bool _multiline;
        private bool _dotAll;

        public CompilerState(RegexFlags flags, int captureCount)
        {
            _flags = flags;
            _captureCount = captureCount;
            _ignoreCase = flags.IgnoreCase;
            _multiline = flags.Multiline;
            _dotAll = flags.DotAll;
        }

        private bool IsUnicode => _flags.Unicode || _flags.UnicodeSets;
        private bool IgnoreCase => _ignoreCase;
        private bool Multiline => _multiline;
        private bool DotAll => _dotAll;

        // \w and \b extend to the case-fold extras (U+017F, U+212A) only when
        // both unicode and ignoreCase are in effect (ECMA-262 WordCharacters).
        private int WordFoldOperand => IsUnicode && IgnoreCase ? 1 : 0;

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
            // Relative offset applied directly by the VM as `pc += offset` (no implicit
            // +1), matching the lookaround offset convention (bodyStart - instrIndex).
            var offset = toIndex - fromIndex;
            var ins = _instructions[fromIndex];
            _instructions[fromIndex] = new RegexInstruction(ins.OpCode, offset, ins.B, ins.C);
        }

        private void PatchSplit(int fromIndex, int toAlt1, int toAlt2)
        {
            var offset1 = toAlt1 - fromIndex;
            var offset2 = toAlt2 - fromIndex;
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
            var groupsToReset = CollectCaptureGroups(disjunction);
            for (int a = 0; a < altCount; a++)
            {
                altStarts[a] = CurrentPos;
                foreach (var groupNumber in groupsToReset)
                {
                    Emit(RegexOpCode.ResetGroup, groupNumber);
                }
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

        private static int[] CollectCaptureGroups(DisjunctionNode disjunction)
        {
            var groups = new SortedSet<int>();
            foreach (var alternative in disjunction.Alternatives)
            {
                CollectCaptureGroups(alternative, groups);
            }

            return groups.ToArray();
        }

        private static void CollectCaptureGroups(AlternativeNode alternative, SortedSet<int> groups)
        {
            foreach (var term in alternative.Terms)
            {
                CollectCaptureGroups(term, groups);
            }
        }

        private static void CollectCaptureGroups(DisjunctionNode disjunction, SortedSet<int> groups)
        {
            foreach (var alternative in disjunction.Alternatives)
            {
                CollectCaptureGroups(alternative, groups);
            }
        }

        private static void CollectCaptureGroups(TermNode term, SortedSet<int> groups)
        {
            switch (term)
            {
                case GroupNode group:
                    if (group.Kind is GroupKind.Capturing or GroupKind.NamedCapturing)
                    {
                        groups.Add(group.GroupNumber);
                    }

                    CollectCaptureGroups(group.Body, groups);
                    break;
                case QuantifierNode quantifier:
                    CollectCaptureGroups(quantifier.Body, groups);
                    break;
                case AssertionNode assertion when assertion.Body is not null:
                    CollectCaptureGroups(assertion.Body, groups);
                    break;
                case ModifierGroupNode modifierGroup:
                    CollectCaptureGroups(modifierGroup.Body, groups);
                    break;
            }
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

        internal void CollectNamedGroups(DisjunctionNode disjunction)
        {
            foreach (var alternative in disjunction.Alternatives)
            {
                foreach (var term in alternative.Terms)
                {
                    CollectNamedGroups(term);
                }
            }
        }

        private void CollectNamedGroups(TermNode term)
        {
            switch (term)
            {
                case QuantifierNode quantifier:
                    CollectNamedGroups(quantifier.Body);
                    break;
                case AssertionNode assertion when assertion.Body is not null:
                    CollectNamedGroups(assertion.Body);
                    break;
                case GroupNode group:
                    if (group.Kind == GroupKind.NamedCapturing && group.Name is not null)
                    {
                        AddNamedGroup(group.Name, group.GroupNumber);
                    }
                    CollectNamedGroups(group.Body);
                    break;
                case ModifierGroupNode modifierGroup:
                    CollectNamedGroups(modifierGroup.Body);
                    break;
            }
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
                    Emit(RegexOpCode.WordBoundary, WordFoldOperand);
                    break;
                case AssertionKind.NonWordBoundary:
                    Emit(RegexOpCode.NonWordBoundary, WordFoldOperand);
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

            var skipBodyJump = Emit(RegexOpCode.Jump, 0);
            var bodyStart = CurrentPos;
            if (isLookbehind)
            {
                EmitDisjunctionReverse(assertion.Body);
            }
            else
            {
                EmitDisjunction(assertion.Body);
            }
            // Emit Accept so sub-match knows where the body ends
            Emit(RegexOpCode.Accept);
            var assertionPos = CurrentPos;
            PatchJump(skipBodyJump, assertionPos);

            // Store offset back to the body start, for the VM to use as a jump target.
            // The offset includes the Accept so the sub-match can reach it.
            var bodyOffset = bodyStart - assertionPos;

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
            EmitResetGroupsForQuantifierBody(body);
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
            EmitResetGroupsForQuantifierBody(body);
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
            EmitResetGroupsForQuantifierBody(body);
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
                EmitResetGroupsForQuantifierBody(body);
                EmitAtom(body);
            }

            // Then emit optional repetitions (max - min times):
            var optionalCount = max - min;
            for (int i = 0; i < optionalCount; i++)
            {
                var splitPos = Emit(RegexOpCode.Split, 0, 0);
                var bodyStart = CurrentPos;
                EmitResetGroupsForQuantifierBody(body);
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
                EmitResetGroupsForQuantifierBody(body);
                EmitAtom(body);
            }

            EmitStar(body, greedy);
        }

        // ─── Atom compilation ──────────────────────────────

        private void EmitResetGroupsForQuantifierBody(AtomNode body)
        {
            var groups = new SortedSet<int>();
            CollectCaptureGroups(body, groups);
            foreach (var groupNumber in groups)
            {
                Emit(RegexOpCode.ResetGroup, groupNumber);
            }
        }

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
                    if (!_flags.Unicode && !_flags.UnicodeSets && br.GroupNumber > _captureCount)
                    {
                        EmitChar(br.GroupNumber);
                    }
                    else
                    {
                        Emit(RegexOpCode.BackRef, br.GroupNumber, IgnoreCase ? 1 : 0);
                    }
                    break;
                case NamedBackReferenceNode nbr:
                    if (!_namedGroupMap.TryGetValue(nbr.Name, out var groupNumbers))
                    {
                        throw new RegexSyntaxError($"Unknown named capture group '{nbr.Name}'.");
                    }

                    if (groupNumbers.Count == 1)
                    {
                        Emit(RegexOpCode.BackRef, groupNumbers[0], IgnoreCase ? 1 : 0);
                    }
                    else
                    {
                        Emit(RegexOpCode.NamedBackRef, GetNamedBackReferenceNameIndex(nbr.Name), IgnoreCase ? 1 : 0);
                    }
                    break;
                case CharacterClassNode cc:
                    EmitCharacterClass(cc);
                    break;
                case GroupNode g:
                    EmitGroup(g);
                    break;
                case ModifierGroupNode mg:
                    EmitModifierGroup(mg);
                    break;
            }
        }

        private void EmitModifierGroup(ModifierGroupNode mg)
        {
            var (savedIgnoreCase, savedMultiline, savedDotAll) = (_ignoreCase, _multiline, _dotAll);
            foreach (var f in mg.AddFlags)
            {
                if (f == 'i') _ignoreCase = true;
                else if (f == 'm') _multiline = true;
                else if (f == 's') _dotAll = true;
            }

            foreach (var f in mg.RemoveFlags)
            {
                if (f == 'i') _ignoreCase = false;
                else if (f == 'm') _multiline = false;
                else if (f == 's') _dotAll = false;
            }

            EmitDisjunction(mg.Body);
            (_ignoreCase, _multiline, _dotAll) = (savedIgnoreCase, savedMultiline, savedDotAll);
        }

        private void EmitAtomReverse(AtomNode atom)
        {
            switch (atom)
            {
                case LiteralCharNode lit:
                    Emit(RegexOpCode.ReverseChar, lit.Value, b: IgnoreCase ? 1 : 0);
                    break;
                case DotNode:
                    Emit(RegexOpCode.ReverseDot, DotAll ? 1 : 0);
                    break;
                case CharacterEscapeNode esc:
                    Emit(RegexOpCode.ReverseChar, esc.CodePoint, b: IgnoreCase ? 1 : 0);
                    break;
                case ClassEscapeNode ce:
                    EmitClassEscapeReverse(ce.Kind);
                    break;
                case UnicodePropertyNode up:
                    EmitUnicodePropReverse(up);
                    break;
                case BackReferenceNode br:
                    if (!_flags.Unicode && !_flags.UnicodeSets && br.GroupNumber > _captureCount)
                    {
                        Emit(RegexOpCode.ReverseChar, br.GroupNumber, b: IgnoreCase ? 1 : 0);
                    }
                    else
                    {
                        Emit(RegexOpCode.ReverseBackRef, br.GroupNumber, IgnoreCase ? 1 : 0);
                    }
                    break;
                case NamedBackReferenceNode nbr:
                    if (!_namedGroupMap.TryGetValue(nbr.Name, out var groupNumbers))
                    {
                        throw new RegexSyntaxError($"Unknown named capture group '{nbr.Name}'.");
                    }

                    if (groupNumbers.Count == 1)
                    {
                        Emit(RegexOpCode.ReverseBackRef, groupNumbers[0], IgnoreCase ? 1 : 0);
                    }
                    else
                    {
                        Emit(RegexOpCode.ReverseNamedBackRef, GetNamedBackReferenceNameIndex(nbr.Name), IgnoreCase ? 1 : 0);
                    }
                    break;
                case CharacterClassNode cc:
                    EmitCharacterClassReverse(cc);
                    break;
                case GroupNode g:
                    EmitGroupReverse(g);
                    break;
                case ModifierGroupNode mg:
                    EmitModifierGroupReverse(mg);
                    break;
                case AssertionNode assertion:
                    EmitAssertion(assertion);
                    break;
            }
        }

        private void EmitModifierGroupReverse(ModifierGroupNode mg)
        {
            var (savedIgnoreCase, savedMultiline, savedDotAll) = (_ignoreCase, _multiline, _dotAll);
            foreach (var f in mg.AddFlags)
            {
                if (f == 'i') _ignoreCase = true;
                else if (f == 'm') _multiline = true;
                else if (f == 's') _dotAll = true;
            }

            foreach (var f in mg.RemoveFlags)
            {
                if (f == 'i') _ignoreCase = false;
                else if (f == 'm') _multiline = false;
                else if (f == 's') _dotAll = false;
            }

            EmitDisjunctionReverse(mg.Body);
            (_ignoreCase, _multiline, _dotAll) = (savedIgnoreCase, savedMultiline, savedDotAll);
        }

        private void EmitClassEscapeReverse(char kind)
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
            Emit(RegexOpCode.ReverseCharClass, (int)classKind, b: WordFoldOperand);
        }

        private void EmitUnicodePropReverse(UnicodePropertyNode up)
        {
            var body = up.Value is not null
                ? up.Property + "=" + up.Value
                : up.Property;
            var index = _unicodePropertyBodies.Count;
            _unicodePropertyBodies.Add(body);
            var opA = (up.Negated ? 1 : 0) | (IgnoreCase ? 2 : 0);
            Emit(RegexOpCode.ReverseUnicodeProp, opA, index, 0);
        }

        private void EmitGroupReverse(GroupNode group)
        {
            if (group.Kind == GroupKind.Capturing || group.Kind == GroupKind.NamedCapturing)
            {
                var n = group.GroupNumber;
                if (group.Kind == GroupKind.NamedCapturing && group.Name is not null)
                {
                    AddNamedGroup(group.Name, n);
                }

                Emit(RegexOpCode.Save, n * 2 + 1);
            }

            EmitDisjunctionReverse(group.Body);

            if (group.Kind == GroupKind.Capturing || group.Kind == GroupKind.NamedCapturing)
            {
                Emit(RegexOpCode.Save, group.GroupNumber * 2);
            }
        }

        private void EmitCharacterClassReverse(CharacterClassNode cc)
        {
            if (cc.Negated)
            {
                foreach (var item in cc.Items)
                {
                    switch (item)
                    {
                        case ClassLiteralChar lc:
                            Emit(RegexOpCode.ReverseChar, lc.Value, b: IgnoreCase ? 1 : 0, c: 1);
                            break;
                        case ClassRange cr:
                            Emit(RegexOpCode.ReverseCharRange, cr.Start, cr.End, c: 1);
                            if (IgnoreCase && CaseFoldedRange(cr.Start, cr.End) is { } folded)
                            {
                                Emit(RegexOpCode.ReverseCharRange, folded.Start, folded.End, c: 1);
                            }
                            break;
                        case ClassEscape ce:
                            Emit(RegexOpCode.ReverseChar, ce.CodePoint, b: IgnoreCase ? 1 : 0, c: 1);
                            break;
                        case ClassClassEscape cce:
                            var classKind = cce.Kind switch
                            {
                                'd' => CharClassKind.Digit,
                                'D' => CharClassKind.NotDigit,
                                'w' => CharClassKind.Word,
                                'W' => CharClassKind.NotWord,
                                's' => CharClassKind.Space,
                                'S' => CharClassKind.NotSpace,
                                _ => throw new InvalidOperationException($"Unknown class escape: {cce.Kind}")
                            };
                            Emit(RegexOpCode.ReverseCharClass, (int)classKind, b: WordFoldOperand, c: 1);
                            break;
                    }
                }

                Emit(RegexOpCode.ReverseDot, 1);
                return;
            }

            var emitters = new List<Action>();
            foreach (var item in cc.Items)
            {
                switch (item)
                {
                    case ClassLiteralChar lc:
                        emitters.Add(() => Emit(RegexOpCode.ReverseChar, lc.Value, b: IgnoreCase ? 1 : 0));
                        break;
                    case ClassRange cr:
                        emitters.Add(() => Emit(RegexOpCode.ReverseCharRange, cr.Start, cr.End));
                        if (IgnoreCase && CaseFoldedRange(cr.Start, cr.End) is { } folded)
                        {
                            emitters.Add(() => Emit(RegexOpCode.ReverseCharRange, folded.Start, folded.End));
                        }
                        break;
                    case ClassEscape ce:
                        emitters.Add(() => Emit(RegexOpCode.ReverseChar, ce.CodePoint, b: IgnoreCase ? 1 : 0));
                        break;
                    case ClassClassEscape cce:
                        emitters.Add(() => EmitClassEscapeReverse(cce.Kind));
                        break;
                    case ClassUnicodeProperty cup:
                        emitters.Add(() => EmitUnicodePropReverse(new UnicodePropertyNode(cup.Property, cup.Value, cup.Negated)));
                        break;
                }
            }

            if (emitters.Count == 0)
            {
                Emit(RegexOpCode.ReverseCharRange, 1, 0);
                return;
            }

            if (emitters.Count == 1)
            {
                emitters[0]();
                return;
            }

            var splitPositions = new List<int>();
            for (int i = 0; i < emitters.Count - 1; i++)
            {
                splitPositions.Add(ReserveSplit());
            }

            var altStarts = new int[emitters.Count];
            var altJumps = new int[emitters.Count];
            for (int i = 0; i < emitters.Count; i++)
            {
                altStarts[i] = CurrentPos;
                emitters[i]();
                if (i < emitters.Count - 1)
                {
                    altJumps[i] = ReserveJump();
                }
            }

            var endPos = CurrentPos;
            for (int i = 0; i < emitters.Count - 1; i++)
            {
                var nextPos = i + 1 < splitPositions.Count ? splitPositions[i + 1] : altStarts[i + 1];
                PatchSplit(splitPositions[i], altStarts[i], nextPos);
                PatchJump(altJumps[i], endPos);
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
        /// For /i mode, return the case-folded counterpart of an ASCII letter
        /// range, or null when the range has no simple folded twin.
        /// </summary>
        private static (int Start, int End)? CaseFoldedRange(int start, int end)
        {
            if (start is >= 'A' and <= 'Z' && end is >= 'A' and <= 'Z')
                return (start + 32, end + 32);
            if (start is >= 'a' and <= 'z' && end is >= 'a' and <= 'z')
                return (start - 32, end - 32);
            return null;
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
            Emit(RegexOpCode.CharClass, (int)classKind, b: WordFoldOperand);
        }

        private void EmitUnicodeProp(UnicodePropertyNode up)
        {
            // Build the property body string (e.g. "General_Category=Letter" or "Emoji")
            var body = up.Value is not null
                ? up.Property + "=" + up.Value
                : up.Property;
            // Store the body in the program's property table and emit its index.
            // Operand A is a bitfield: bit 0 = negated, bit 1 = case-fold the
            // input before the set test (/i). Operand B = property-table index.
            var index = _unicodePropertyBodies.Count;
            _unicodePropertyBodies.Add(body);
            var opA = (up.Negated ? 1 : 0) | (IgnoreCase ? 2 : 0);
            Emit(RegexOpCode.UnicodeProp, opA, index, 0);
        }

        private void EmitCharacterClass(CharacterClassNode cc)
        {
            // v-flag set operations: if the items contain a ClassSetOpMarker,
            // resolve both sides to code-point sets and emit the result.
            if (_flags.UnicodeSets && cc.Items.Any(i => i is ClassSetOpMarker))
            {
                EmitCharacterClassWithSetOp(cc);
                return;
            }

            if (_flags.UnicodeSets && cc.Items.Any(i => i is ClassNestedSet) && !cc.Items.Any(IsStringPropertyClassItem))
            {
                var resolved = ResolveClassItemsToCodePoints(cc.Items);
                if (cc.Negated)
                {
                    var all = new HashSet<int>();
                    for (var cp = 0; cp <= 0x10FFFF; cp++) all.Add(cp);
                    all.ExceptWith(resolved);
                    resolved = all;
                }

                EmitResolvedCodePointSet(resolved);
                return;
            }

            if (cc.Negated)
            {
                // Negated class [^...]: match any char NOT in the set.
                // Strategy: peek-negate each item (C=1): if any item matches
                // the current char → backtrack; if none match → consume one
                // char. The consume is unconditional (a negated class matches
                // line terminators too).
                foreach (var item in cc.Items)
                {
                    switch (item)
                    {
                        case ClassLiteralChar lc:
                            Emit(RegexOpCode.Char, lc.Value, b: IgnoreCase ? 1 : 0, c: 1);
                            break;
                        case ClassRange cr:
                            Emit(RegexOpCode.CharRange, cr.Start, cr.End, c: 1);
                            if (IgnoreCase && CaseFoldedRange(cr.Start, cr.End) is { } folded)
                            {
                                Emit(RegexOpCode.CharRange, folded.Start, folded.End, c: 1);
                            }
                            break;
                        case ClassEscape ce:
                            Emit(RegexOpCode.Char, ce.CodePoint, b: IgnoreCase ? 1 : 0, c: 1);
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

                Emit(RegexOpCode.Dot, 1);
                return;
            }

            // Positive class [abc...]: a class matches ONE char that is in any
            // item's set, so compile it as an alternation over the items
            // (Split-chained like a disjunction), each alternative consuming
            // the code point.
            var emitters = new List<Action>();
            foreach (var item in cc.Items)
            {
                switch (item)
                {
                    case ClassLiteralChar lc:
                        emitters.Add(() => EmitChar(lc.Value));
                        break;
                    case ClassRange cr:
                        emitters.Add(() => Emit(RegexOpCode.CharRange, cr.Start, cr.End));
                        if (IgnoreCase && CaseFoldedRange(cr.Start, cr.End) is { } folded)
                        {
                            emitters.Add(() => Emit(RegexOpCode.CharRange, folded.Start, folded.End));
                        }
                        break;
                    case ClassEscape ce:
                        emitters.Add(() => EmitChar(ce.CodePoint));
                        break;
                    case ClassClassEscape cce:
                        emitters.Add(() => EmitClassEscape(cce.Kind));
                        break;
                    case ClassUnicodeProperty cup:
                        emitters.Add(() => EmitUnicodeProp(new UnicodePropertyNode(cup.Property, cup.Value, cup.Negated)));
                        break;
                    case ClassNestedSet nested:
                    {
                        var nestedSet = ResolveClassItemsToCodePoints(nested.Items);
                        if (nested.Negated)
                        {
                            var all = new HashSet<int>();
                            for (var cp = 0; cp <= 0x10FFFF; cp++) all.Add(cp);
                            all.ExceptWith(nestedSet);
                            nestedSet = all;
                        }

                        emitters.Add(() => EmitResolvedCodePointSet(nestedSet));
                        break;
                    }
                }
            }

            if (emitters.Count == 0)
            {
                // [] matches nothing: an impossible range always fails.
                Emit(RegexOpCode.CharRange, 1, 0);
                return;
            }

            if (emitters.Count == 1)
            {
                emitters[0]();
                return;
            }

            var splitPositions = new List<int>();
            for (int i = 0; i < emitters.Count - 1; i++)
            {
                splitPositions.Add(ReserveSplit());
            }

            var altStarts = new int[emitters.Count];
            var altJumps = new int[emitters.Count];
            for (int i = 0; i < emitters.Count; i++)
            {
                altStarts[i] = CurrentPos;
                emitters[i]();
                if (i < emitters.Count - 1)
                {
                    altJumps[i] = ReserveJump();
                }
            }

            var endPos = CurrentPos;
            for (int i = 0; i < emitters.Count - 1; i++)
            {
                var nextPos = i + 1 < splitPositions.Count ? splitPositions[i + 1] : altStarts[i + 1];
                PatchSplit(splitPositions[i], altStarts[i], nextPos);
                PatchJump(altJumps[i], endPos);
            }
        }

        // v-flag set operations: resolve both sides to code-point sets,
        // compute the set operation, and emit the resulting character class.
        private void EmitCharacterClassWithSetOp(CharacterClassNode cc)
        {
            // Split items at the ClassSetOpMarker(s).
            var leftItems = new List<ClassItem>();
            var rightItems = new List<ClassItem>();
            int opKind = 0; // 0=Intersection, 1=Difference, 2=SymmetricDifference
            bool foundMarker = false;
            foreach (var item in cc.Items)
            {
                if (item is ClassSetOpMarker marker)
                {
                    foundMarker = true;
                    opKind = marker.OpKind;
                    continue;
                }
                if (!foundMarker) leftItems.Add(item);
                else rightItems.Add(item);
            }
            // Resolve both sides to sorted unique code-point lists.
            var leftCps = ResolveClassItemsToCodePoints(leftItems);
            var rightCps = ResolveClassItemsToCodePoints(rightItems);
            // Compute the set operation result.
            HashSet<int> result;
            switch (opKind)
            {
                case 0: // Intersection: left ∩ right
                    result = new HashSet<int>(leftCps);
                    result.IntersectWith(rightCps);
                    break;
                case 1: // Difference: left \ right
                    result = new HashSet<int>(leftCps);
                    result.ExceptWith(rightCps);
                    break;
                case 2: // SymmetricDifference: (left ∪ right) \ (left ∩ right)
                    result = new HashSet<int>(leftCps);
                    result.SymmetricExceptWith(rightCps);
                    break;
                default:
                    result = new HashSet<int>(leftCps);
                    break;
            }
            if (cc.Negated)
            {
                // Negated set operation: complement the result.
                // This is uncommon; emit as peek-negated items.
                var allCps = new HashSet<int>();
                for (int cp = 0; cp <= 0x10FFFF; cp++) allCps.Add(cp);
                allCps.ExceptWith(result);
                result = allCps;
            }
            EmitResolvedCodePointSet(result);
        }

        private void EmitResolvedCodePointSet(HashSet<int> result)
        {
            var sorted = result.OrderBy(cp => cp).ToList();
            if (sorted.Count == 0)
            {
                Emit(RegexOpCode.CharRange, 1, 0); // impossible range
                return;
            }

            var ranges = new List<(int start, int end)>();
            int rangeStart = sorted[0], rangeEnd = sorted[0];
            for (int i = 1; i < sorted.Count; i++)
            {
                if (sorted[i] == rangeEnd + 1) { rangeEnd = sorted[i]; }
                else { ranges.Add((rangeStart, rangeEnd)); rangeStart = sorted[i]; rangeEnd = sorted[i]; }
            }
            ranges.Add((rangeStart, rangeEnd));
            // Emit as alternation over ranges and single chars.
            if (ranges.Count == 1 && ranges[0].start == ranges[0].end)
            {
                Emit(RegexOpCode.Char, ranges[0].start, b: _flags.IgnoreCase ? 1 : 0);
                return;
            }
            var splitPositions = new List<int>();
            for (int i = 0; i < ranges.Count - 1; i++) splitPositions.Add(ReserveSplit());
            var altStarts = new int[ranges.Count];
            var altJumps = new int[ranges.Count];
            for (int i = 0; i < ranges.Count; i++)
            {
                altStarts[i] = CurrentPos;
                var (s, e) = ranges[i];
                if (s == e) Emit(RegexOpCode.Char, s, b: _flags.IgnoreCase ? 1 : 0);
                else Emit(RegexOpCode.CharRange, s, e);
                if (i < ranges.Count - 1) altJumps[i] = ReserveJump();
            }
            var endPos2 = CurrentPos;
            for (int i = 0; i < ranges.Count - 1; i++)
            {
                var nextPos = i + 1 < splitPositions.Count ? splitPositions[i + 1] : altStarts[i + 1];
                PatchSplit(splitPositions[i], altStarts[i], nextPos);
                PatchJump(altJumps[i], endPos2);
            }
        }

        // Resolve a list of ClassItems to the set of code points they match.
        private static HashSet<int> ResolveClassItemsToCodePoints(List<ClassItem> items)
        {
            var cps = new HashSet<int>();
            foreach (var item in items)
            {
                switch (item)
                {
                    case ClassLiteralChar lc: cps.Add(lc.Value); break;
                    case ClassRange cr: for (int cp = cr.Start; cp <= cr.End; cp++) cps.Add(cp); break;
                    case ClassEscape ce: cps.Add(ce.CodePoint); break;
                    case ClassClassEscape cce: AddClassEscapeCps(cps, cce.Kind); break;
                    case ClassUnicodeProperty cup: AddUnicodePropertyCps(cps, cup); break;
                    case ClassNestedSet nested:
                    {
                        var nestedCps = ResolveClassItemsToCodePoints(nested.Items);
                        if (nested.Negated)
                        {
                            var all = new HashSet<int>();
                            for (var cp = 0; cp <= 0x10FFFF; cp++) all.Add(cp);
                            all.ExceptWith(nestedCps);
                            nestedCps = all;
                        }

                        cps.UnionWith(nestedCps);
                        break;
                    }
                    // String literals and other items are ignored for now.
                }
            }
            return cps;
        }

        private static void AddClassEscapeCps(HashSet<int> cps, char kind)
        {
            switch (kind)
            {
                case 'd': for (int cp = '0'; cp <= '9'; cp++) cps.Add(cp); break;
                case 'D': for (int cp = 0; cp <= 0x10FFFF; cp++) if (cp < '0' || cp > '9') cps.Add(cp); break;
                case 'w': for (int cp = '0'; cp <= '9'; cp++) cps.Add(cp); for (int cp = 'A'; cp <= 'Z'; cp++) cps.Add(cp); for (int cp = 'a'; cp <= 'z'; cp++) cps.Add(cp); cps.Add('_'); break;
                case 'W': for (int cp = 0; cp <= 0x10FFFF; cp++) if (!(cp >= '0' && cp <= '9' || cp >= 'A' && cp <= 'Z' || cp >= 'a' && cp <= 'z' || cp == '_')) cps.Add(cp); break;
                case 's': cps.Add(0x0009); cps.Add(0x000A); cps.Add(0x000B); cps.Add(0x000C); cps.Add(0x000D); cps.Add(0x0020); cps.Add(0x00A0); cps.Add(0x1680); cps.Add(0x2000); cps.Add(0x2001); cps.Add(0x2002); cps.Add(0x2003); cps.Add(0x2004); cps.Add(0x2005); cps.Add(0x2006); cps.Add(0x2007); cps.Add(0x2008); cps.Add(0x2009); cps.Add(0x200A); cps.Add(0x2028); cps.Add(0x2029); cps.Add(0x202F); cps.Add(0x205F); cps.Add(0x3000); cps.Add(0xFEFF); break;
                case 'S': for (int cp = 0; cp <= 0x10FFFF; cp++) cps.Add(cp); cps.Remove(0x0009); cps.Remove(0x000A); cps.Remove(0x000B); cps.Remove(0x000C); cps.Remove(0x000D); cps.Remove(0x0020); break;
            }
        }

        private static void AddUnicodePropertyCps(HashSet<int> cps, ClassUnicodeProperty cup)
        {
            // Query the Unicode property-escape database for all code points.
            // Build the query key from Property and optional Value.
            string body = cup.Value is not null ? $"{cup.Property}={cup.Value}" : cup.Property;
            for (int cp = 0; cp <= 0x10FFFF; cp++)
            {
                if (UnicodePropertyEscapeData.TryHasPropertyCodePoint(body, cp, out var hasProperty) &&
                    hasProperty != cup.Negated)
                    cps.Add(cp);
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
            // C=1 marks peek+negate.
            Emit(RegexOpCode.CharClass, (int)classKind, b: WordFoldOperand, c: 1);
        }

        private void EmitGroup(GroupNode group)
        {
            if (group.Kind == GroupKind.Capturing || group.Kind == GroupKind.NamedCapturing)
            {
                var n = group.GroupNumber;
                if (group.Kind == GroupKind.NamedCapturing && group.Name is not null)
                {
                    AddNamedGroup(group.Name, n);
                }

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

        private static bool IsStringPropertyClassItem(ClassItem item)
        {
            return item switch
            {
                ClassUnicodeProperty cup => IsStringPropertyBody(cup.Value is not null ? $"{cup.Property}={cup.Value}" : cup.Property),
                ClassNestedSet nested => nested.Items.Any(IsStringPropertyClassItem),
                _ => false
            };
        }

        private static bool IsStringPropertyBody(string body)
        {
            return body is "Basic_Emoji" or
                "Emoji_Keycap_Sequence" or
                "RGI_Emoji_Modifier_Sequence" or
                "RGI_Emoji_Flag_Sequence" or
                "RGI_Emoji_Tag_Sequence" or
                "RGI_Emoji_ZWJ_Sequence" or
                "RGI_Emoji";
        }

        private int EmitDisjunctionReverse(DisjunctionNode disjunction)
        {
            if (disjunction.Alternatives.Count == 1)
            {
                return EmitAlternativeReverse(disjunction.Alternatives[0]);
            }

            var altCount = disjunction.Alternatives.Count;
            var altStarts = new int[altCount];
            var altJumps = new int[altCount];
            var splitPositions = new List<int>();
            for (int a = 0; a < altCount - 1; a++)
            {
                splitPositions.Add(ReserveSplit());
            }

            var groupsToReset = CollectCaptureGroups(disjunction);
            for (int a = 0; a < altCount; a++)
            {
                altStarts[a] = CurrentPos;
                foreach (var groupNumber in groupsToReset)
                {
                    Emit(RegexOpCode.ResetGroup, groupNumber);
                }

                EmitAlternativeReverse(disjunction.Alternatives[a]);
                if (a < altCount - 1)
                {
                    altJumps[a] = ReserveJump();
                }
            }

            var endPos = CurrentPos;
            for (int a = 0; a < altCount - 1; a++)
            {
                var nextPos = a + 1 < splitPositions.Count ? splitPositions[a + 1] : altStarts[a + 1];
                PatchSplit(splitPositions[a], altStarts[a], nextPos);
            }

            for (int a = 0; a < altCount - 1; a++)
            {
                PatchJump(altJumps[a], endPos);
            }

            return splitPositions.Count > 0 ? splitPositions[0] : altStarts[0];
        }

        private int EmitAlternativeReverse(AlternativeNode alt)
        {
            var start = CurrentPos;
            for (var i = alt.Terms.Count - 1; i >= 0; i--)
            {
                EmitTermReverse(alt.Terms[i]);
            }

            return start;
        }

        private void EmitTermReverse(TermNode term)
        {
            switch (term)
            {
                case AssertionNode assertion:
                    EmitAssertion(assertion);
                    break;
                case QuantifierNode quantifier:
                    EmitQuantifierReverse(quantifier);
                    break;
                case AtomNode atom:
                    EmitAtomReverse(atom);
                    break;
            }
        }

        private void EmitQuantifierReverse(QuantifierNode q)
        {
            var body = q.Body;
            var isBounded = q.Max != int.MaxValue;

            if (q.Min == 0 && q.Max == int.MaxValue)
            {
                EmitStarReverse(body, q.Greedy);
            }
            else if (q.Min == 1 && q.Max == int.MaxValue)
            {
                EmitPlusReverse(body, q.Greedy);
            }
            else if (q.Min == 0 && q.Max == 1)
            {
                EmitOptionalReverse(body, q.Greedy);
            }
            else if (isBounded)
            {
                for (int i = 0; i < q.Min; i++)
                {
                    EmitAtomReverse(body);
                }

                var optionalCount = q.Max - q.Min;
                for (int i = 0; i < optionalCount; i++)
                {
                    var splitPos = Emit(RegexOpCode.Split, 0, 0);
                    var bodyStart = CurrentPos;
                    EmitAtomReverse(body);
                    var skipPos = CurrentPos;
                    if (q.Greedy)
                        PatchSplit(splitPos, bodyStart, skipPos);
                    else
                        PatchSplit(splitPos, skipPos, bodyStart);
                }
            }
            else
            {
                for (int i = 0; i < q.Min; i++)
                {
                    EmitAtomReverse(body);
                }

                EmitStarReverse(body, q.Greedy);
            }
        }

        private void EmitStarReverse(AtomNode body, bool greedy)
        {
            var loopPos = CurrentPos;
            var splitPos = Emit(RegexOpCode.Split, 0, 0);
            var bodyStart = CurrentPos;
            EmitAtomReverse(body);
            var jumpPos = Emit(RegexOpCode.Jump, 0);
            PatchJump(jumpPos, loopPos);
            var skipPos = CurrentPos;
            if (greedy)
                PatchSplit(splitPos, bodyStart, skipPos);
            else
                PatchSplit(splitPos, skipPos, bodyStart);
        }

        private void EmitPlusReverse(AtomNode body, bool greedy)
        {
            var bodyStart = CurrentPos;
            EmitAtomReverse(body);
            var splitPos = Emit(RegexOpCode.Split, 0, 0);
            var skipPos = CurrentPos;
            if (greedy)
                PatchSplit(splitPos, bodyStart, skipPos);
            else
                PatchSplit(splitPos, skipPos, bodyStart);
        }

        private void EmitOptionalReverse(AtomNode body, bool greedy)
        {
            var splitPos = Emit(RegexOpCode.Split, 0, 0);
            var bodyStart = CurrentPos;
            EmitAtomReverse(body);
            var skipPos = CurrentPos;
            if (greedy)
                PatchSplit(splitPos, bodyStart, skipPos);
            else
                PatchSplit(splitPos, skipPos, bodyStart);
        }

        private void AddNamedGroup(string name, int groupNumber)
        {
            if (!_namedGroupMap.TryGetValue(name, out var groupNumbers))
            {
                groupNumbers = new List<int>();
                _namedGroupMap[name] = groupNumbers;
            }

            if (!groupNumbers.Contains(groupNumber))
            {
                groupNumbers.Add(groupNumber);
            }
        }

        private int GetNamedBackReferenceNameIndex(string name)
        {
            if (_namedBackReferenceNameIndices.TryGetValue(name, out var index))
            {
                return index;
            }

            index = _namedBackReferenceNames.Count;
            _namedBackReferenceNames.Add(name);
            _namedBackReferenceNameIndices[name] = index;
            return index;
        }
    }
}
