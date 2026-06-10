// ECMA-262 §22.2 — Backtracking regex VM.
//
// Executes a compiled RegexProgram against an input string.
// Uses a backtracking stack to implement NFA semantics.
// All matching operates on code points (not UTF-16 chars) for /u and /v correctness.

using System.Text;
using FenBrowser.Js.Runtime; // for FenLogger

namespace FenBrowser.Js.Regex;

public sealed class RegexVM
{
    private readonly RegexProgram _program;
    private string _input = string.Empty;
    private int[] _codePoints = Array.Empty<int>();
    private int[] _charOffsets = Array.Empty<int>(); // codePoint → char offset mapping
    private int _cpLen;

    // Backtracking stack
    private Stack<ThreadState>? _stack;

    // Backtrack limit to prevent exponential blowup (DoS protection).
    // The effective limit grows with input length (see ExecuteFrom).
    private const int MaxBacktracks = 50000;
    private int _backtrackCount;
    private int _maxBacktracks = MaxBacktracks;

    public RegexVM(RegexProgram program)
    {
        _program = program;
    }

    // ─── Public API ───────────────────────────────────────

    /// <summary>
    /// Execute the regex against the input string, starting at <paramref name="startIndex"/> (char offset).
    /// Returns the first match found, or a failed result.
    /// </summary>
    public RegexMatchResult Execute(string input, int startIndex = 0)
    {
        input ??= string.Empty;
        // An empty input is still a valid match target (e.g. /a*/, /^$/ match ""),
        // so only bail out when startIndex is genuinely past the end.
        if (startIndex > input.Length)
            return RegexMatchResult.Empty(input);

        SetInput(input);
        var startCp = CharOffsetToCodePoint(startIndex);
        if (startCp < 0 || startCp > _cpLen) startCp = _cpLen;

        return ExecuteFrom(startCp);
    }

    /// <summary>
    /// Execute the regex starting at code-point position <paramref name="startCp"/>.
    /// </summary>
    private RegexMatchResult ExecuteFrom(int startCp)
    {
        var flags = _program.Flags;
        var captureSlots = 2 * (_program.CaptureCount + 1);

        // Try each starting position from startCp to end.
        // Per ECMAScript, the regex is not anchored unless it starts with ^.
        // Budget scales with input: unwinding a failed greedy loop and probing
        // every start position are both O(input) legitimate work; the cap only
        // exists to stop exponential blowup.
        _backtrackCount = 0;
        _maxBacktracks = MaxBacktracks + 4 * _cpLen;
        for (int tryCp = startCp; tryCp <= _cpLen; tryCp++)
        {
            var stack = new Stack<ThreadState>(64);
            _stack = stack;

            var initialState = new ThreadState
            {
                PC = 0,
                CP = tryCp,
                Captures = AllocateCaptures(captureSlots)
            };
            initialState.Captures[0] = _charOffsets[tryCp]; // full match start
            stack.Push(initialState);

        while (stack.Count > 0)
        {
            if (++_backtrackCount > _maxBacktracks)
            {
                _stack = null;
                return RegexMatchResult.Empty(_input);
            }
            var state = stack.Pop();
            var pc = state.PC;
            var cp = state.CP;
            var captures = state.Captures;

            while (true)
            {
                if (pc >= _program.Instructions.Length)
                    break; // reached end → no accept → backtrack

                var ins = _program.Instructions[pc];
                bool matched;

                switch (ins.OpCode)
                {
                    case RegexOpCode.Char:
                        if (ins.B == 1) // case-insensitive
                            matched = cp < _cpLen && CaseInsensitiveEqual(_codePoints[cp], ins.A);
                        else
                            matched = cp < _cpLen && _codePoints[cp] == ins.A;
                        if (ins.C == 1) { matched = !matched; } // peek+negate for [^...]
                        if (matched) { if (ins.C == 0) cp++; pc++; }
                        else { pc = -1; }
                        break;

                    case RegexOpCode.CharRange:
                        matched = cp < _cpLen && _codePoints[cp] >= ins.A && _codePoints[cp] <= ins.B;
                        if (ins.C == 1) { matched = !matched; }
                        if (matched) { if (ins.C == 0) cp++; pc++; }
                        else { pc = -1; }
                        break;

                    case RegexOpCode.CharClass:
                        matched = cp < _cpLen && MatchCharClass(_codePoints[cp], (CharClassKind)ins.A);
                        if (ins.C == 1) { matched = !matched; }
                        if (matched) { if (ins.C == 0) cp++; pc++; }
                        else { pc = -1; }
                        break;

                    case RegexOpCode.Dot:
                        matched = cp < _cpLen && MatchDot(_codePoints[cp], ins.A == 1);
                        if (matched) { cp++; pc++; }
                        else { pc = -1; }
                        break;

                    case RegexOpCode.UnicodeProp:
                        matched = cp < _cpLen && MatchUnicodeProperty(_codePoints[cp], ins.B, ins.A != 0);
                        if (ins.C == 1) { matched = !matched; }
                        if (matched) { if (ins.C == 0) cp++; pc++; }
                        else { pc = -1; }
                        break;

                    case RegexOpCode.Jump:
                        pc += ins.A;
                        break;

                    case RegexOpCode.Split:
                        // Push only the alternative; continue path 1 inline so
                        // forward progress never consumes backtrack budget
                        // (a greedy loop over N chars is N splits — treating
                        // those as backtracks made long inputs silently fail).
                        PushState(stack, pc + ins.B, cp, captures); // path 2 (on backtrack)
                        pc += ins.A;                                // path 1
                        break;

                    case RegexOpCode.Accept:
                        captures[1] = _charOffsets[cp]; // full match end
                        _stack = null;
                        return BuildResult(true, captures);

                    case RegexOpCode.Bol:
                        matched = cp == 0 || (ins.A == 1 && IsLineStart(cp));
                        if (matched) pc++;
                        else { pc = -1; }
                        break;

                    case RegexOpCode.Eol:
                        matched = cp >= _cpLen || (ins.A == 1 && IsLineEnd(cp));
                        if (matched) pc++;
                        else { pc = -1; }
                        break;

                    case RegexOpCode.WordBoundary:
                        matched = IsWordBoundary(cp);
                        if (matched) pc++;
                        else { pc = -1; }
                        break;

                    case RegexOpCode.NonWordBoundary:
                        matched = !IsWordBoundary(cp);
                        if (matched) pc++;
                        else { pc = -1; }
                        break;

                    case RegexOpCode.Save:
                        captures[ins.A] = _charOffsets[cp];
                        pc++;
                        break;

                    case RegexOpCode.BackRef:
                        {
                            // captures[] store char offsets; cp is a code point index.
                            // Convert to char offset, match, then convert back.
                            var charPos = CodePointToCharOffset(cp);
                            var result = TryMatchBackRefAtChar(charPos, captures, ins.A, ins.B == 1);
                            if (result < 0) { pc = -1; }
                            else
                            {
                                var charLen = GetBackRefLength(captures, ins.A);
                                cp = CharOffsetToCodePoint(charPos + charLen);
                                pc++;
                            }
                        }
                        break;

                    case RegexOpCode.Lookahead:
                    case RegexOpCode.NegLookahead:
                        {
                            var resultPc = HandleLookaround(stack, cp, captures, ins, pc);
                            if (resultPc < 0) { pc = -1; }
                            else pc = resultPc;
                        }
                        break;

                    case RegexOpCode.Lookbehind:
                    case RegexOpCode.NegLookbehind:
                        {
                            var isNegBehind = ins.OpCode == RegexOpCode.NegLookbehind;
                            var bodyStartPc = pc + ins.A;
                            var behindResult = TryLookbehind(bodyStartPc, cp, captures, isNegBehind);
                            if (behindResult) pc++;
                            else { pc = -1; }
                        }
                        break;

                    default:
                        pc++;
                        break;
                }

                if (pc < 0)
                    goto backtrack;
            }

        backtrack:
            continue; // pop next state from stack
        }
        // End of tryCp loop — if this position failed, try the next one
        }

        _stack = null;
        return RegexMatchResult.Empty(_input);
    }

    // ─── Backtracking helpers ─────────────────────────────

    private static int[] AllocateCaptures(int slots)
    {
        var captures = new int[slots];
        for (int i = 0; i < slots; i++) captures[i] = -1;
        return captures;
    }

    private static void PushState(Stack<ThreadState> stack, int pc, int cp, int[] captures)
    {
        var copy = new int[captures.Length];
        Array.Copy(captures, copy, captures.Length);
        stack.Push(new ThreadState { PC = pc, CP = cp, Captures = copy });
    }

    private struct ThreadState
    {
        public int PC;
        public int CP;
        public int[] Captures;
    }

    // ─── Input conversion ─────────────────────────────────

    private void SetInput(string input)
    {
        _input = input;

        // Pre-compute code points array
        var cpList = new List<int>(input.Length);
        var offsetList = new List<int>(input.Length);

        for (int i = 0; i < input.Length; i++)
        {
            offsetList.Add(i);
            if (char.IsHighSurrogate(input[i]) && i + 1 < input.Length &&
                char.IsLowSurrogate(input[i + 1]))
            {
                cpList.Add(char.ConvertToUtf32(input[i], input[i + 1]));
                i++; // skip low surrogate
            }
            else
            {
                cpList.Add(input[i]);
            }
        }

        // Add sentinel at end
        offsetList.Add(input.Length);
        _codePoints = cpList.ToArray();
        _charOffsets = offsetList.ToArray();
        _cpLen = cpList.Count;
    }

    private int CharOffsetToCodePoint(int charOffset)
    {
        // Binary search in _charOffsets to find the code point index.
        // Actually, a linear scan is fine for typical input sizes.
        for (int cp = 0; cp < _cpLen; cp++)
        {
            if (_charOffsets[cp] >= charOffset)
                return cp;
        }
        return _cpLen;
    }

    private int CodePointToCharOffset(int cp)
    {
        if (cp < 0) return 0;
        if (cp >= _cpLen) return _input.Length;
        return _charOffsets[cp];
    }

    // ─── Character matching ───────────────────────────────

    private static bool MatchDot(int cp, bool dotAll)
    {
        if (dotAll) return true; // . matches everything with /s
        // . does not match line terminators: \n \r
        return cp is not ('\n' or '\r' or 0x2028 or 0x2029);
    }

    private static bool MatchCharClass(int cp, CharClassKind kind)
    {
        return kind switch
        {
            CharClassKind.Digit => cp is >= '0' and <= '9',
            CharClassKind.NotDigit => cp is < '0' or > '9',
            CharClassKind.Word => IsWordChar(cp),
            CharClassKind.NotWord => !IsWordChar(cp),
            CharClassKind.Space => IsSpaceChar(cp),
            CharClassKind.NotSpace => !IsSpaceChar(cp),
            _ => false
        };
    }

    private static bool IsWordChar(int cp)
    {
        return cp is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_';
    }

    /// <summary>
    /// Case-insensitive code point comparison (ECMA-262 §22.2.2.1.2).
    /// Without /u: ASCII-only case folding. With /u: full Unicode simple case folding.
    /// </summary>
    private bool CaseInsensitiveEqual(int cp, int target)
    {
        if (cp == target) return true;
        // ASCII fast path (always active)
        if (cp is >= 'A' and <= 'Z' && cp + 32 == target) return true;
        if (cp is >= 'a' and <= 'z' && cp - 32 == target) return true;
        // Unicode case folding only with /u or /v flag
        if (!_program.Flags.Unicode && !_program.Flags.UnicodeSets)
            return false;
        // Full Unicode simple case folding via Rune
        try
        {
            var lowerCp = Rune.ToLowerInvariant(new Rune((uint)cp));
            var lowerTarget = Rune.ToLowerInvariant(new Rune((uint)target));
            return lowerCp == lowerTarget;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSpaceChar(int cp)
    {
        return cp switch
        {
            '\t' or '\n' or '\v' or '\f' or '\r' or ' ' or
            0x00A0 or 0x1680 or 0x2000 or 0x2001 or 0x2002 or 0x2003 or
            0x2004 or 0x2005 or 0x2006 or 0x2007 or 0x2008 or 0x2009 or
            0x200A or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or
            0xFEFF => true,
            _ => false
        };
    }

    // ─── Unicode property matching ────────────────────────

    private bool MatchUnicodeProperty(int cp, int propIndex, bool negated)
    {
        var bodies = _program.UnicodePropertyBodies;
        if (bodies is null || propIndex < 0 || propIndex >= bodies.Length)
            return negated;

        // Resolve the body's ranges once per program; per-character dictionary
        // lookups dominated matching time on long inputs.
        var cache = _program.ResolvedPropertyRanges ??= new uint[]?[bodies.Length];
        var ranges = cache[propIndex] ??= UnicodePropertyEscapeData.ResolveRanges(bodies[propIndex]);
        if (ranges.Length > 0)
        {
            var inRanges = UnicodePropertyEscapeData.IsInRanges(cp, ranges);
            return negated ? !inRanges : inRanges;
        }

        var body = bodies[propIndex];
        if (UnicodePropertyEscapeData.TryHasPropertyCodePoint(body, cp, out var hasProperty))
            return negated ? !hasProperty : hasProperty;

        return negated; // unknown → \p{} matches nothing, \P{} matches everything
    }

    // ─── Position assertions ──────────────────────────────

    private bool IsLineStart(int cp)
    {
        if (cp == 0) return true;
        var prev = _codePoints[cp - 1];
        return prev is '\n' or '\r' or 0x2028 or 0x2029;
    }

    private bool IsLineEnd(int cp)
    {
        if (cp >= _cpLen) return true;
        return _codePoints[cp] is '\n' or '\r' or 0x2028 or 0x2029;
    }

    private bool IsWordBoundary(int cp)
    {
        var prevIsWord = cp > 0 && cp <= _cpLen && IsWordChar(_codePoints[cp - 1]);
        var nextIsWord = cp < _cpLen && IsWordChar(_codePoints[cp]);
        return prevIsWord != nextIsWord;
    }

    // ─── Backreferences ───────────────────────────────────

    private int TryMatchBackRefAtChar(int charPos, int[] captures, int groupNum, bool ignoreCase)
    {
        var slot = groupNum * 2;
        if (slot + 1 >= captures.Length)
            return -1;

        var start = captures[slot];
        var end = captures[slot + 1];

        if (start < 0 || end < 0)
        {
            // Group hasn't participated — matches empty (ECMAScript §21.2.2.9)
            return 0; // success, no advance needed
        }

        var length = end - start;
        var matchEnd = charPos + length;
        if (matchEnd > _input.Length)
            return -1;

        var capturedText = _input.Substring(start, length);
        var targetText = _input.Substring(charPos, length);

        bool matches = ignoreCase
            ? string.Equals(capturedText, targetText, StringComparison.OrdinalIgnoreCase)
            : string.Equals(capturedText, targetText, StringComparison.Ordinal);

        return matches ? 0 : -1; // 0 = success (caller advances by length)
    }

    private int GetBackRefLength(int[] captures, int groupNum)
    {
        var slot = groupNum * 2;
        if (slot + 1 >= captures.Length) return 0;
        var start = captures[slot];
        var end = captures[slot + 1];
        if (start < 0 || end < 0) return 0;
        return end - start;
    }

    // ─── Lookaround ───────────────────────────────────────

    private int HandleLookaround(Stack<ThreadState> stack, int cp, int[] captures,
        RegexInstruction ins, int pc)
    {
        var isNegative = ins.OpCode is RegexOpCode.NegLookahead or RegexOpCode.NegLookbehind;

        // Run the body subprogram from the lookaround's saved offset
        var bodyStartPc = pc + ins.A; // ins.A = body offset (negative relative offset)

        if (bodyStartPc < 0 || bodyStartPc >= _program.Instructions.Length)
            return pc + 1; // invalid — skip

        // Execute the body in a sub-match at the current position
        var endCp = ExecuteSubMatch(bodyStartPc, cp, captures);
        var bodyMatch = endCp >= 0;
        var assertionPassed = isNegative ? !bodyMatch : bodyMatch;

        return assertionPassed ? pc + 1 : -1;
    }

    /// <summary>
    /// Try lookbehind: run the body starting at each position from cp backwards.
    /// Success if the body matches and ends exactly at cp.
    /// </summary>
    private bool TryLookbehind(int bodyStartPc, int targetCp, int[] captures, bool isNegative)
    {
        // Try each starting position from targetCp backwards.
        // For typical fixed-width lookbehinds, we find the match quickly.
        // Limit search to avoid pathological performance.
        var searchLimit = Math.Min(targetCp, 4096); // ECMA-262 has no explicit limit; 4096 is generous
        for (int startCp = targetCp; startCp >= targetCp - searchLimit && startCp >= 0; startCp--)
        {
            var endCp = ExecuteSubMatch(bodyStartPc, startCp, captures);
            if (endCp == targetCp)
            {
                // Body matched and ended exactly at targetCp
                return !isNegative; // positive succeeds, negative fails
            }
        }

        // No match ending at targetCp found
        return isNegative; // positive fails, negative succeeds
    }

    /// <summary>
    /// Execute a sub-program (for lookaround bodies). Returns the ending code point
    /// if the sub-match succeeds, or -1 if it fails.
    /// </summary>
    private int ExecuteSubMatch(int startPc, int startCp, int[] captures)
    {
        // Execute a sub-program using its own backtracking stack.
        var subStack = new Stack<ThreadState>(16);
        var initialState = new ThreadState
        {
            PC = startPc,
            CP = startCp,
            Captures = AllocateCaptures(captures.Length)
        };
        subStack.Push(initialState);

        while (subStack.Count > 0)
        {
            var state = subStack.Pop();
            var pc = state.PC;
            var cp = state.CP;
            var caps = state.Captures;

            while (true)
            {
                if (pc >= _program.Instructions.Length)
                    goto subBacktrack;

                var ins = _program.Instructions[pc];
                bool matched;

                switch (ins.OpCode)
                {
                    case RegexOpCode.Char:
                        if (ins.B == 1)
                            matched = cp < _cpLen && CaseInsensitiveEqual(_codePoints[cp], ins.A);
                        else
                            matched = cp < _cpLen && _codePoints[cp] == ins.A;
                        if (ins.C == 1) { matched = !matched; }
                        if (matched) { if (ins.C == 0) cp++; pc++; }
                        else { pc = -1; }
                        break;
                    case RegexOpCode.CharRange:
                        matched = cp < _cpLen && _codePoints[cp] >= ins.A && _codePoints[cp] <= ins.B;
                        if (ins.C == 1) { matched = !matched; }
                        if (matched) { if (ins.C == 0) cp++; pc++; }
                        else { pc = -1; }
                        break;
                    case RegexOpCode.CharClass:
                        matched = cp < _cpLen && MatchCharClass(_codePoints[cp], (CharClassKind)ins.A);
                        if (ins.C == 1) { matched = !matched; }
                        if (matched) { if (ins.C == 0) cp++; pc++; }
                        else { pc = -1; }
                        break;
                    case RegexOpCode.UnicodeProp:
                        matched = cp < _cpLen && MatchUnicodeProperty(_codePoints[cp], ins.B, ins.A != 0);
                        if (ins.C == 1) { matched = !matched; }
                        if (matched) { if (ins.C == 0) cp++; pc++; }
                        else { pc = -1; }
                        break;
                    case RegexOpCode.Dot:
                        matched = cp < _cpLen && MatchDot(_codePoints[cp], ins.A == 1);
                        if (matched) { cp++; pc++; } else { pc = -1; }
                        break;
                    case RegexOpCode.Jump:
                        pc += ins.A;
                        break;
                    case RegexOpCode.Split:
                        PushState(subStack, pc + ins.B, cp, caps); // alternative (on backtrack)
                        pc += ins.A;                               // path 1 continues inline
                        break;
                    case RegexOpCode.Accept:
                        return cp; // success — return ending code point
                    case RegexOpCode.Bol:
                    case RegexOpCode.Eol:
                        matched = cp == startCp;
                        if (matched) pc++; else { pc = -1; }
                        break;
                    case RegexOpCode.Save:
                        caps[ins.A] = _charOffsets[cp];
                        pc++;
                        break;
                    case RegexOpCode.BackRef:
                        {
                            var charPos2 = CodePointToCharOffset(cp);
                            var result2 = TryMatchBackRefAtChar(charPos2, caps, ins.A, ins.B == 1);
                            if (result2 < 0) { pc = -1; }
                            else
                            {
                                var charLen2 = GetBackRefLength(caps, ins.A);
                                cp = CharOffsetToCodePoint(charPos2 + charLen2);
                                pc++;
                            }
                        }
                        break;
                    default:
                        pc++;
                        break;
                }

                if (pc < 0)
                    goto subBacktrack;
            }

        subBacktrack:
            continue;
        }

        return -1; // failure
    }

    // ─── Result construction ──────────────────────────────

    private RegexMatchResult BuildResult(bool success, int[] captures)
    {
        if (!success)
            return RegexMatchResult.Empty(_input);

        var start = captures[0];
        var end = captures[1];
        var length = end >= start ? end - start : 0;

        return new RegexMatchResult(
            success: true,
            index: start,
            length: length,
            input: _input,
            captures: captures,
            namedGroups: _program.NamedGroupMap);
    }
}
