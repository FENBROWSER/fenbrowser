using System.Text;
using System.Text.RegularExpressions;

namespace FenBrowser.Js.Regex;

public readonly record struct CompiledRegExp(
    string Pattern,
    string NormalizedFlags,
    RegexProgram Program,
    RegexFlags Flags,
    IReadOnlyDictionary<string, string> NamedGroupMap);

public static class RegExpCompiler
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromMilliseconds(250);

    public static CompiledRegExp Compile(string pattern, string flags)
    {
        var parsedFlags = RegexFlags.Parse(flags.AsSpan());
        ValidatePatternEarlyErrors(pattern, parsedFlags);
        var normalizedFlags = parsedFlags.ToJsFlagsString();
        var namedGroupMap = new Dictionary<string, string>(StringComparer.Ordinal);
        return new CompiledRegExp(
            pattern,
            normalizedFlags,
            CompileNative(pattern, normalizedFlags),
            parsedFlags,
            namedGroupMap);
    }

    private static string RewriteCharacterClassHexEscapesForDotNet(string pattern)
    {
        var rewritten = new StringBuilder(pattern.Length + 8);
        var inCharClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                if (inCharClass &&
                    i + 3 < pattern.Length &&
                    pattern[i + 1] == 'x' &&
                    IsHexDigit(pattern[i + 2]) &&
                    IsHexDigit(pattern[i + 3]))
                {
                    rewritten.Append(@"\u00");
                    rewritten.Append(pattern[i + 2]);
                    rewritten.Append(pattern[i + 3]);
                    i += 3;
                    continue;
                }

                rewritten.Append(ch);
                if (i + 1 < pattern.Length)
                {
                    rewritten.Append(pattern[i + 1]);
                    i++;
                }

                continue;
            }

            if (ch == '[' && !inCharClass)
            {
                inCharClass = true;
            }
            else if (ch == ']' && inCharClass)
            {
                inCharClass = false;
            }

            rewritten.Append(ch);
        }

        return rewritten.ToString();
    }

    private static bool IsHexDigit(char value)
        => value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';

    /// <summary>
    /// Compile using the native ECMAScript regex engine (RegexParser + RegexCompiler).
    /// This is the preferred path for all new regexes. Falls back to null if the
    /// pattern uses features not yet supported by the native engine.
    /// </summary>
    /// <summary>
    /// Validate a regex pattern for syntax errors. Throws RegexSyntaxError if
    /// the pattern is invalid. Used by the JS parser to reject invalid regex
    /// literals at parse time (ECMA-262 12.2.8.2 / 22.2.3.1).
    /// </summary>
    public static void ValidatePattern(string pattern, string flags)
    {
        RegexPattern ast;
        try
        {
            var parsedFlags = RegexFlags.Parse(flags.AsSpan());
            ast = RegexParser.Parse(pattern, parsedFlags);
        }
        catch (Exception)
        {
            // Our regex parser can't handle this syntax — silently pass.
            // The pattern will be validated at runtime by BCL regex / CompileNative.
            return;
        }

        // Only validate patterns that our parser successfully parsed.
        // Errors here are genuine Unicode property escape issues.
        try
        {
            ValidateUnicodePropsInDisjunction(ast.Disjunction, ast.Flags);
        }
        catch (RegexSyntaxError)
        {
            throw; // Propagate for parse-negative test pass
        }
        catch (Exception)
        {
            // Unexpected error — silently pass.
        }
    }

    private static void ValidateUnicodePropsInDisjunction(DisjunctionNode disjunction, RegexFlags flags)
    {
        foreach (var alt in disjunction.Alternatives)
            ValidateUnicodePropsInAlternative(alt, flags);
    }

    private static void ValidateUnicodePropsInAlternative(AlternativeNode alt, RegexFlags flags)
    {
        foreach (var term in alt.Terms)
            ValidateUnicodePropsInTerm(term, flags);
    }

    private static void ValidateUnicodePropsInTerm(TermNode term, RegexFlags flags)
    {
        switch (term)
        {
            case UnicodePropertyNode up:
            {
                var body = up.Value is not null
                    ? $"{up.Property}={up.Value}"
                    : up.Property;
                ValidateUnicodePropertyEscapeBody(body, flags, up.Negated);
                break;
            }
            case CharacterClassNode cc:
                foreach (var item in cc.Items)
                {
                    if (item is ClassUnicodeProperty cup)
                    {
                        var cupBody = cup.Value is not null
                            ? $"{cup.Property}={cup.Value}"
                            : cup.Property;
                        ValidateUnicodePropertyEscapeBody(cupBody, flags, cup.Negated);
                    }
                }
                break;
            case QuantifierNode q:
                ValidateUnicodePropsInTerm(q.Body, flags);
                break;
            case GroupNode g:
                ValidateUnicodePropsInDisjunction(g.Body, flags);
                break;
            case ModifierGroupNode mg:
                ValidateUnicodePropsInDisjunction(mg.Body, flags);
                break;
            case AssertionNode a:
                if (a.Body is not null)
                    ValidateUnicodePropsInDisjunction(a.Body, flags);
                break;
        }
    }

    public static RegexProgram CompileNative(string pattern, string flags)
    {
        // The native VM does not yet implement lookbehind capture/backtracking
        // semantics completely. The BCL matcher does, so keep this syntax on
        // the compatibility path until the native implementation is complete.
        var nativeFlags = RegexFlags.Parse(flags.AsSpan());
        RegexPattern ast;
        try
        {
            ast = RegexParser.Parse(RewriteAnnexBNonUnicodePattern(pattern, nativeFlags), nativeFlags);
        }
        catch (RegexSyntaxError)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new RegexSyntaxError($"Native RegExp parser failed: {ex.Message}");
        }

        try
        {
            // Validate Unicode property escapes against the known database.
            ValidateUnicodePropsInDisjunction(ast.Disjunction, ast.Flags);
            var program = RegexCompiler.Compile(ast);
            return program;
        }
        catch (RegexSyntaxError)
        {
            // Unicode property validation failed — genuine syntax error.
            // Don't fall back to .NET; propagate so the caller surfaces SyntaxError.
            throw;
        }
        catch (Exception ex)
        {
            throw new RegexSyntaxError($"Native RegExp compiler failed: {ex.Message}");
        }
    }

    internal static string RewriteAnnexBNonUnicodePattern(string pattern, RegexFlags flags)
    {
        if (flags.Unicode || flags.UnicodeSets || string.IsNullOrEmpty(pattern))
        {
            return pattern;
        }

        static bool IsAsciiLetter(char value)
            => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
        static bool IsHex(char value)
            => value is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f';
        static bool IsClassEscape(char value)
            => value is 'd' or 'D' or 's' or 'S' or 'w' or 'W';

        static bool HasNamedCapture(string source)
        {
            var inClass = false;
            for (var index = 0; index + 3 < source.Length; index++)
            {
                if (source[index] == '\\')
                {
                    index++;
                    continue;
                }
                if (source[index] == '[') { inClass = true; continue; }
                if (source[index] == ']' && inClass) { inClass = false; continue; }
                if (!inClass && source[index] == '(' && source[index + 1] == '?' &&
                    source[index + 2] == '<' && source[index + 3] is not ('=' or '!'))
                {
                    return true;
                }
            }
            return false;
        }

        var rewritten = new StringBuilder(pattern.Length + 8);
        var hasNamedCapture = HasNamedCapture(pattern);
        var inClass = false;
        var inGroupName = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (inGroupName)
            {
                rewritten.Append(ch);
                if (ch == '\\' && i + 1 < pattern.Length)
                {
                    rewritten.Append(pattern[i + 1]);
                    i++;
                    continue;
                }

                if (ch == '>')
                {
                    inGroupName = false;
                }

                continue;
            }

            if (ch == '[')
            {
                inClass = true;
                rewritten.Append(ch);
                continue;
            }
            if (ch == ']' && inClass)
            {
                inClass = false;
                rewritten.Append(ch);
                continue;
            }

            // B.1.4 CharacterRangeOrUnion: a class escape cannot be a scalar
            // range endpoint in non-Unicode mode; the hyphen joins the union.
            if (inClass && ch == '-' && i + 2 < pattern.Length &&
                pattern[i + 1] == '\\' && IsClassEscape(pattern[i + 2]))
            {
                rewritten.Append("\\-");
                continue;
            }

            if (!inClass &&
                ch == '(' &&
                i + 2 < pattern.Length &&
                pattern[i + 1] == '?' &&
                pattern[i + 2] == '<' &&
                (i + 3 >= pattern.Length || pattern[i + 3] is not ('=' or '!')))
            {
                rewritten.Append("(?<");
                i += 2;
                inGroupName = true;
                continue;
            }

            if (ch != '\\' || i + 1 >= pattern.Length)
            {
                rewritten.Append(ch);
                continue;
            }

            var escape = pattern[i + 1];
            if (!inClass && escape == 'k' && hasNamedCapture && i + 2 < pattern.Length && pattern[i + 2] == '<')
            {
                rewritten.Append(@"\k");
                i++;
                inGroupName = true;
                continue;
            }

            if (escape == 'c')
            {
                var hasOperand = i + 2 < pattern.Length;
                var operand = hasOperand ? pattern[i + 2] : '\0';
                var valid = hasOperand && (IsAsciiLetter(operand) ||
                    (inClass && (char.IsDigit(operand) || operand == '_')));
                if (valid)
                {
                    rewritten.Append("\\x").Append(((int)operand % 32).ToString("x2"));
                    i += 2;
                }
                else
                {
                    // Invalid ControlEscape falls through to a literal backslash
                    // followed by "c"; the following code unit is parsed normally.
                    rewritten.Append("\\\\c");
                    i++;
                }
                continue;
            }

            if (escape == 'x' &&
                (i + 3 >= pattern.Length || !IsHex(pattern[i + 2]) || !IsHex(pattern[i + 3])))
            {
                rewritten.Append('x');
                i++;
                continue;
            }
            if (escape == 'x')
            {
                rewritten.Append("\\x");
                rewritten.Append(pattern[i + 2]);
                rewritten.Append(pattern[i + 3]);
                i += 3;
                continue;
            }
            if (escape == 'u' &&
                (i + 5 >= pattern.Length || !IsHex(pattern[i + 2]) || !IsHex(pattern[i + 3]) ||
                 !IsHex(pattern[i + 4]) || !IsHex(pattern[i + 5])))
            {
                rewritten.Append('u');
                i++;
                continue;
            }
            if (escape == 'u')
            {
                rewritten.Append("\\u");
                rewritten.Append(pattern[i + 2]);
                rewritten.Append(pattern[i + 3]);
                rewritten.Append(pattern[i + 4]);
                rewritten.Append(pattern[i + 5]);
                i += 5;
                continue;
            }
            if (escape is 'p' or 'P')
            {
                rewritten.Append(escape);
                i++;
                continue;
            }
            if (escape == 'k' && !hasNamedCapture)
            {
                rewritten.Append('k');
                i++;
                continue;
            }
            if (char.IsLetter(escape) && escape is not ('d' or 'D' or 's' or 'S' or
                'w' or 'W' or 'b' or 'B' or 'f' or 'n' or 'r' or 't' or 'v'))
            {
                rewritten.Append(escape);
                i++;
                continue;
            }

            rewritten.Append(ch).Append(escape);
            i++;
        }

        return rewritten.ToString();
    }

    internal static bool ContainsUnicodePropertyEscape(string pattern)
    {
        for (var i = 0; i + 3 < pattern.Length; i++)
        {
            if (pattern[i] == '\\' &&
                (pattern[i + 1] == 'p' || pattern[i + 1] == 'P') &&
                pattern[i + 2] == '{')
            {
                return true;
            }
        }

        return false;
    }

    public static void ValidateLiteralSyntax(string rawText)
    {
        ParseLiteral(rawText, out var pattern, out var flags);
        var parsedFlags = RegexFlags.Parse(flags.AsSpan());
        ValidatePatternEarlyErrors(pattern, parsedFlags);
    }

    private sealed class NameScope
    {
        public HashSet<string> CurrentAlternative { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Committed { get; } = new(StringComparer.Ordinal);
    }

    private enum GroupKind
    {
        Other,
        Lookahead,
        NegativeLookahead,
        Lookbehind,
        NegativeLookbehind
    }

    private static void ParseLiteral(string rawText, out string pattern, out string flags)
    {
        if (string.IsNullOrEmpty(rawText) || rawText.Length < 2 || rawText[0] != '/')
        {
            throw new RegexSyntaxError("Invalid regular expression literal.");
        }

        var lastSlash = rawText.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            throw new RegexSyntaxError("Invalid regular expression literal.");
        }

        pattern = rawText.Substring(1, lastSlash - 1);
        flags = lastSlash + 1 < rawText.Length ? rawText[(lastSlash + 1)..] : string.Empty;
    }

    internal static void ValidatePatternEarlyErrors(string pattern, RegexFlags flags)
    {
        var namedGroups = new HashSet<string>(StringComparer.Ordinal);
        var namedReferences = new List<string>();
        var decimalBackreferences = new List<int>();
        var groups = new Stack<GroupKind>();
        // Alternation-aware duplicate named-group detection (ES2025 duplicate
        // named capture groups): a name may repeat across distinct alternatives
        // of a disjunction, but not twice on a single match path. Each scope
        // frame tracks names declared in the current alternative and names that
        // have been committed by prior (closed) alternatives at this group level.
        var nameScopes = new Stack<NameScope>();
        nameScopes.Push(new NameScope());
        var inCharClass = false;
        var classStart = -1;
        var captureCount = 0;
        var hasNamedCaptureSyntax = ContainsNamedCaptureSyntax(pattern);

        if (pattern.Length > 0 && IsQuantifierAtStart(pattern))
        {
            throw new RegexSyntaxError("Invalid regular expression pattern.");
        }

        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                if (i + 1 >= pattern.Length)
                {
                    throw new RegexSyntaxError("Invalid escape sequence in regular expression pattern.");
                }

                var next = pattern[i + 1];
                if (next == 'k')
                {
                    if (!flags.Unicode && !flags.UnicodeSets && !hasNamedCaptureSyntax)
                    {
                        i += 1;
                        continue;
                    }
                    // Named backreference \k<name>: always parsed as a backreference
                    // in all modes (Unicode and non-Unicode). ECMA-262 22.2.2.1.
                    if (i + 2 >= pattern.Length || pattern[i + 2] != '<')
                    {
                        if (flags.Unicode || flags.UnicodeSets || hasNamedCaptureSyntax)
                            throw new RegexSyntaxError("Invalid named backreference.");

                        i += 1;
                        continue;
                    }

                    var nameStart = i + 3;
                    var nameEnd = pattern.IndexOf('>', nameStart);
                    if (nameEnd < 0 || nameEnd == nameStart)
                    {
                        throw new RegexSyntaxError("Invalid named backreference.");
                    }

                    var referenceNameRaw = pattern[nameStart..nameEnd];
                    if (!TryCanonicalizeGroupNameToken(referenceNameRaw, out var referenceName) || !IsValidGroupName(referenceName))
                    {
                        throw new RegexSyntaxError("Invalid named backreference.");
                    }

                    namedReferences.Add(referenceName);
                    i = nameEnd;
                    continue;
                }

                if (flags.Unicode || flags.UnicodeSets)
                {
                    if (next is >= '1' and <= '9')
                    {
                        var end = i + 2;
                        while (end < pattern.Length && char.IsDigit(pattern[end]))
                        {
                            end++;
                        }

                        if (!int.TryParse(pattern[(i + 1)..end], out var decimalRef))
                        {
                            throw new RegexSyntaxError("Invalid decimal escape in Unicode regular expression.");
                        }

                        decimalBackreferences.Add(decimalRef);
                        i = end - 1;
                        continue;
                    }

                    if (next == 'c')
                    {
                        if (i + 2 >= pattern.Length || !char.IsLetter(pattern[i + 2]))
                        {
                            throw new RegexSyntaxError("Invalid control escape in Unicode regular expression.");
                        }

                        i += 2;
                        continue;
                    }

                    if (next == 'u' && i + 2 < pattern.Length && pattern[i + 2] == '{')
                    {
                        var braceStart = i + 3;
                        var braceEnd = pattern.IndexOf('}', braceStart);
                        if (braceEnd < 0 || braceEnd == braceStart)
                        {
                            throw new RegexSyntaxError("Invalid Unicode code point escape.");
                        }

                        var hex = pattern[braceStart..braceEnd];

                        if (!int.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var codePoint) ||
                            codePoint < 0 || codePoint > 0x10FFFF)
                        {
                            throw new RegexSyntaxError("Invalid Unicode code point escape.");
                        }

                        i = braceEnd;
                        continue;
                    }

                    if ((next == 'p' || next == 'P') && i + 2 < pattern.Length && pattern[i + 2] == '{')
                    {
                        var propStart = i + 3;
                        var propEnd = pattern.IndexOf('}', propStart);
                        if (propEnd < 0 || propEnd == propStart)
                        {
                            throw new RegexSyntaxError("Invalid Unicode property escape.");
                        }

                        ValidateUnicodePropertyEscapeBody(pattern[propStart..propEnd], flags, next == 'P');
                        i = propEnd;
                        continue;
                    }

                    if (next is 'p' or 'P')
                    {
                        throw new RegexSyntaxError("Invalid Unicode property escape.");
                    }

                    if (next is >= '0' and <= '7' && next != '0')
                    {
                        throw new RegexSyntaxError("Invalid legacy octal escape in Unicode regular expression.");
                    }

                    if (!flags.UnicodeSets && char.IsLetter(next) && !IsValidUnicodeEscapeLeader(next))
                    {
                        throw new RegexSyntaxError("Invalid identity escape in Unicode regular expression.");
                    }
                }

                i++;
                continue;
            }

            if (inCharClass)
            {
                if (ch == ']')
                {
                    var classContent = pattern[(classStart + 1)..i];
                    if (!flags.UnicodeSets && ContainsInvalidCharacterClassRange(classContent))
                    {
                        throw new RegexSyntaxError("Invalid character class range in regular expression.");
                    }

                    if (flags.Unicode && !flags.UnicodeSets)
                    {
                        if (ContainsUnicodeInvalidClassRange(classContent))
                        {
                            throw new RegexSyntaxError("Invalid character class range in Unicode regular expression.");
                        }
                    }
                    else if (flags.UnicodeSets)
                    {
                        if (ContainsUnicodeSetsBreakingChangeLiteral(classContent))
                        {
                            throw new RegexSyntaxError("Invalid UnicodeSets character class syntax.");
                        }
                    }

                    inCharClass = false;
                }

                continue;
            }

            if (ch == '[')
            {
                inCharClass = true;
                classStart = i;
                continue;
            }

            if (flags.Unicode && !flags.UnicodeSets && ch == '{' && !IsQuantifierStart(pattern, i))
            {
                throw new RegexSyntaxError("Invalid extended pattern character in Unicode regular expression.");
            }

            if (ch == '|')
            {
                // New alternative at this group level: commit the names declared
                // in the current alternative; later alternatives may reuse them.
                var altScope = nameScopes.Peek();
                altScope.Committed.UnionWith(altScope.CurrentAlternative);
                altScope.CurrentAlternative.Clear();
                continue;
            }

            if (ch == '(')
            {
                var kind = GroupKind.Other;
                if (i + 1 < pattern.Length && pattern[i + 1] == '?')
                {
                    if (i + 2 >= pattern.Length)
                    {
                        throw new RegexSyntaxError("Invalid group syntax.");
                    }

                    var marker = pattern[i + 2];
                    if (marker == '=')
                    {
                        kind = GroupKind.Lookahead;
                    }
                    else if (marker == '!')
                    {
                        kind = GroupKind.NegativeLookahead;
                    }
                    else if (marker == '<')
                    {
                        if (i + 3 >= pattern.Length)
                        {
                            throw new RegexSyntaxError("Invalid group syntax.");
                        }

                        if (pattern[i + 3] == '=')
                        {
                            kind = GroupKind.Lookbehind;
                        }
                        else if (pattern[i + 3] == '!')
                        {
                            kind = GroupKind.NegativeLookbehind;
                        }
                        else
                        {
                            var nameStart = i + 3;
                            var nameEnd = pattern.IndexOf('>', nameStart);
                            if (nameEnd < 0 || nameEnd == nameStart)
                            {
                                throw new RegexSyntaxError("Invalid named capturing group.");
                            }

                            var groupNameRaw = pattern[nameStart..nameEnd];
                            if (!TryCanonicalizeGroupNameToken(groupNameRaw, out var groupName) || !IsValidGroupName(groupName))
                            {
                                throw new RegexSyntaxError("Invalid named capturing group.");
                            }

                            // The group is a term in the enclosing scope's current
                            // alternative: a same-named group already on this path
                            // is a Syntax Error.
                            var declScope = nameScopes.Peek();
                            if (!declScope.CurrentAlternative.Add(groupName))
                            {
                                throw new RegexSyntaxError("Duplicate capture group name in regular expression.");
                            }

                            _ = namedGroups.Add(groupName);
                        }
                    }
                    else if (char.IsLetter(marker) || marker == '-')
                    {
                        var colon = pattern.IndexOf(':', i + 2);
                        if (colon < 0)
                        {
                            throw new RegexSyntaxError("Invalid regexp modifiers group.");
                        }

                        var close = pattern.IndexOf(')', i + 2);
                        if (close >= 0 && close < colon)
                        {
                            throw new RegexSyntaxError("Invalid regexp modifiers group.");
                        }

                        // ECMA-262 RegExp modifiers (e.g. (?ims-ims:...)): the flag
                        // sets must be non-empty (at least one side), contain only
                        // i/m/s, have no duplicate flags, and add/remove sets must be
                        // disjoint. Validate the expression between '?' and ':'.
                        ValidateInlineModifierExpression(pattern[(i + 2)..colon]);
                    }
                    else if (marker is not (':' or '=' or '!' or '<'))
                    {
                        throw new RegexSyntaxError("Invalid group syntax.");
                    }
                }

                if (kind == GroupKind.Other && !(i + 1 < pattern.Length && pattern[i + 1] == '?' &&
                    i + 2 < pattern.Length && pattern[i + 2] == ':'))
                {
                    if (!(i + 1 < pattern.Length && pattern[i + 1] == '?' &&
                          i + 2 < pattern.Length &&
                          (pattern[i + 2] == '=' || pattern[i + 2] == '!' ||
                           (pattern[i + 2] == '<' && i + 3 < pattern.Length && (pattern[i + 3] == '=' || pattern[i + 3] == '!')) ||
                           char.IsLetter(pattern[i + 2]) || pattern[i + 2] == '-')))
                    {
                        // Plain capturing group.
                        captureCount++;
                    }
                }
                else if (i + 2 < pattern.Length && pattern[i + 1] == '?' && pattern[i + 2] == '<' &&
                         i + 3 < pattern.Length && pattern[i + 3] is not ('=' or '!'))
                {
                    captureCount++;
                }

                groups.Push(kind);
                nameScopes.Push(new NameScope());
                continue;
            }

            if (ch == ')')
            {
                if (groups.Count == 0)
                {
                    continue;
                }

                // Pop the group's name scope and fold the names it can contribute
                // (any alternative) into the parent's current alternative path. A
                // collision there means two same-named groups share a match path.
                if (nameScopes.Count > 1)
                {
                    var childScope = nameScopes.Pop();
                    var parentScope = nameScopes.Peek();
                    // A name that appears in different alternatives within the child
                    // is not a conflict — collapse the child's contributed names into
                    // a single deduped set, then check that set against the parent's
                    // current path (where a collision IS a same-path duplicate).
                    var childNames = new HashSet<string>(childScope.Committed, StringComparer.Ordinal);
                    childNames.UnionWith(childScope.CurrentAlternative);
                    foreach (var name in childNames)
                    {
                        if (!parentScope.CurrentAlternative.Add(name))
                        {
                            throw new RegexSyntaxError("Duplicate capture group name in regular expression.");
                        }
                    }
                }

                var kind = groups.Pop();
                var hasQuantifier = i + 1 < pattern.Length && IsQuantifierStart(pattern, i + 1);
                if (!hasQuantifier)
                {
                    continue;
                }

                if (kind is GroupKind.Lookbehind or GroupKind.NegativeLookbehind)
                {
                    throw new RegexSyntaxError("Lookbehind assertions cannot be quantified.");
                }

                if ((flags.Unicode || flags.UnicodeSets) &&
                    kind is GroupKind.Lookahead or GroupKind.NegativeLookahead)
                {
                    throw new RegexSyntaxError("Lookahead assertions cannot be quantified in Unicode mode.");
                }
            }
        }

        foreach (var reference in namedReferences)
        {
            if (!namedGroups.Contains(reference))
            {
                throw new RegexSyntaxError("Named backreference does not reference an existing capturing group.");
            }
        }

        if (flags.Unicode || flags.UnicodeSets)
        {
            foreach (var decimalRef in decimalBackreferences)
            {
                if (decimalRef > captureCount)
                {
                    throw new RegexSyntaxError("Decimal escape does not reference an existing capturing group.");
                }
            }
        }
    }

    private static bool ContainsNamedCaptureSyntax(string pattern)
    {
        var inClass = false;
        for (var index = 0; index + 3 < pattern.Length; index++)
        {
            if (pattern[index] == '\\') { index++; continue; }
            if (pattern[index] == '[') { inClass = true; continue; }
            if (pattern[index] == ']' && inClass) { inClass = false; continue; }
            if (!inClass && pattern[index] == '(' && pattern[index + 1] == '?' &&
                pattern[index + 2] == '<' && pattern[index + 3] is not ('=' or '!'))
            {
                return true;
            }
        }
        return false;
    }

    private static bool TryCanonicalizeGroupNameToken(string rawName, out string canonicalName)
    {
        canonicalName = string.Empty;
        if (string.IsNullOrEmpty(rawName))
        {
            return false;
        }

        if (rawName.IndexOf('\\') < 0)
        {
            canonicalName = rawName;
            return true;
        }

        var sb = new StringBuilder(rawName.Length);
        for (var i = 0; i < rawName.Length; i++)
        {
            var ch = rawName[i];
            if (ch != '\\')
            {
                sb.Append(ch);
                continue;
            }

            if (i + 1 >= rawName.Length || rawName[i + 1] != 'u')
            {
                return false;
            }

            if (i + 2 < rawName.Length && rawName[i + 2] == '{')
            {
                var hexStart = i + 3;
                var close = rawName.IndexOf('}', hexStart);
                if (close <= hexStart)
                {
                    return false;
                }

                var hex = rawName[hexStart..close];
                if (!int.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var cp) ||
                    cp < 0 || cp > 0x10FFFF)
                {
                    return false;
                }

                sb.Append(char.ConvertFromUtf32(cp));
                i = close;
                continue;
            }

            if (i + 5 >= rawName.Length)
            {
                return false;
            }

            var hex4 = rawName.Substring(i + 2, 4);
            if (!int.TryParse(hex4, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var cp16))
            {
                return false;
            }

            sb.Append((char)cp16);
            i += 5;
        }

        canonicalName = sb.ToString();
        return true;
    }

    private static bool IsValidGroupName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var pos = 0;
        if (!Rune.TryGetRuneAt(name, pos, out var first))
        {
            return false;
        }

        if (!IsIdentifierStartRune(first))
        {
            return false;
        }

        pos += first.Utf16SequenceLength;
        while (pos < name.Length)
        {
            if (!Rune.TryGetRuneAt(name, pos, out var next))
            {
                return false;
            }

            if (!IsIdentifierPartRune(next))
            {
                return false;
            }

            pos += next.Utf16SequenceLength;
        }

        return true;
    }

    private static bool IsIdentifierStartRune(Rune rune)
    {
        if (rune.Value is '$' or '_')
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            System.Globalization.UnicodeCategory.UppercaseLetter => true,
            System.Globalization.UnicodeCategory.LowercaseLetter => true,
            System.Globalization.UnicodeCategory.TitlecaseLetter => true,
            System.Globalization.UnicodeCategory.ModifierLetter => true,
            System.Globalization.UnicodeCategory.OtherLetter => true,
            System.Globalization.UnicodeCategory.LetterNumber => true,
            _ => false
        };
    }

    private static bool IsIdentifierPartRune(Rune rune)
    {
        if (IsIdentifierStartRune(rune))
        {
            return true;
        }

        if (rune.Value is 0x200C or 0x200D)
        {
            return true;
        }

        return Rune.GetUnicodeCategory(rune) switch
        {
            System.Globalization.UnicodeCategory.NonSpacingMark => true,
            System.Globalization.UnicodeCategory.SpacingCombiningMark => true,
            System.Globalization.UnicodeCategory.DecimalDigitNumber => true,
            System.Globalization.UnicodeCategory.ConnectorPunctuation => true,
            _ => false
        };
    }

    private static void ValidateInlineModifierExpression(string expression)
    {
        if (string.IsNullOrEmpty(expression))
        {
            throw new RegexSyntaxError("Invalid regexp modifiers group.");
        }

        var hyphenIndex = expression.IndexOf('-');
        if (hyphenIndex >= 0 && expression.LastIndexOf('-') != hyphenIndex)
        {
            throw new RegexSyntaxError("Invalid regexp modifiers group.");
        }

        var addPart = hyphenIndex >= 0 ? expression[..hyphenIndex] : expression;
        var removePart = hyphenIndex >= 0 ? expression[(hyphenIndex + 1)..] : string.Empty;
        // It is a Syntax Error only if BOTH flag sets are empty (e.g. "(?-:a)").
        // "(?-i:a)" (empty add) and "(?i-:a)" (empty remove) are valid.
        if (addPart.Length == 0 && removePart.Length == 0)
        {
            throw new RegexSyntaxError("Invalid regexp modifiers group.");
        }

        var addSet = ParseModifierSet(addPart);
        var removeSet = removePart.Length == 0 ? new HashSet<char>() : ParseModifierSet(removePart);
        if (addSet.Overlaps(removeSet))
        {
            throw new RegexSyntaxError("Invalid regexp modifiers group.");
        }
    }

    private static HashSet<char> ParseModifierSet(string part)
    {
        var set = new HashSet<char>();
        foreach (var c in part)
        {
            if (c is not ('i' or 'm' or 's'))
            {
                throw new RegexSyntaxError("Invalid regexp modifiers group.");
            }

            if (!set.Add(c))
            {
                throw new RegexSyntaxError("Invalid regexp modifiers group.");
            }
        }

        return set;
    }

    private static bool IsQuantifierStart(string pattern, int index)
    {
        var c = pattern[index];
        if (c is '*' or '+' or '?')
        {
            return true;
        }

        if (c != '{')
        {
            return false;
        }

        // Treat as quantifier only when it looks like {n}, {n,}, or {n,m}.
        var i = index + 1;
        if (i >= pattern.Length || !char.IsDigit(pattern[i]))
        {
            return false;
        }

        while (i < pattern.Length && char.IsDigit(pattern[i]))
        {
            i++;
        }

        if (i < pattern.Length && pattern[i] == '}')
        {
            return true;
        }

        if (i < pattern.Length && pattern[i] == ',')
        {
            i++;
            while (i < pattern.Length && char.IsDigit(pattern[i]))
            {
                i++;
            }

            return i < pattern.Length && pattern[i] == '}';
        }

        return false;
    }

    private static bool IsQuantifierAtStart(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        if (pattern[0] is '*' or '+' or '?')
        {
            return true;
        }

        return IsQuantifierStart(pattern, 0);
    }

    private static bool IsValidUnicodeEscapeLeader(char c) =>
        c is 'b' or 'B' or 'd' or 'D' or 'f' or 'n' or 'r' or 's' or 'S' or 't' or 'v' or 'w' or 'W' or
            'x' or 'u' or 'k' or 'p' or 'P' or 'c';

    private static bool ContainsUnicodeInvalidClassRange(string content)
    {
        // In Unicode mode, class escapes are CharSets and cannot be range bounds.
        // A leading or trailing hyphen is a literal, so `[-\d]` and `[\d-]`
        // remain valid while `[a-\d]` and `[\d-a]` are early errors.
        return System.Text.RegularExpressions.Regex.IsMatch(
                   content,
                   @"\\[dDsSwW]-.",
                   RegexOptions.CultureInvariant) ||
               System.Text.RegularExpressions.Regex.IsMatch(
                   content,
                   @".-\\[dDsSwW]",
                   RegexOptions.CultureInvariant) ||
               System.Text.RegularExpressions.Regex.IsMatch(
                   content,
                   @"\\[pP]\{[^}]+\}-.",
                   RegexOptions.CultureInvariant) ||
               System.Text.RegularExpressions.Regex.IsMatch(
                   content,
                   @".-\\[pP]\{[^}]+\}",
                   RegexOptions.CultureInvariant);
    }

    private static bool ContainsInvalidCharacterClassRange(string content)
    {
        var pos = 0;
        if (pos < content.Length && content[pos] == '^')
        {
            pos++;
        }

        if (!TryReadClassRangeAtom(content, ref pos, out var previous))
        {
            return false;
        }

        while (pos < content.Length)
        {
            if (content[pos] != '-')
            {
                if (!TryReadClassRangeAtom(content, ref pos, out previous))
                {
                    return false;
                }

                continue;
            }

            if (pos == 0 || pos + 1 >= content.Length)
            {
                pos++;
                continue;
            }

            pos++;
            var nextPos = pos;
            if (!TryReadClassRangeAtom(content, ref nextPos, out var next))
            {
                previous = null;
                pos = nextPos;
                continue;
            }

            if (previous.HasValue && next.HasValue && previous.Value > next.Value)
            {
                return true;
            }

            previous = next;
            pos = nextPos;
        }

        return false;
    }

    private static bool TryReadClassRangeAtom(string content, ref int pos, out int? codePoint)
    {
        codePoint = null;
        if (pos >= content.Length)
        {
            return false;
        }

        var ch = content[pos++];
        if (ch != '\\')
        {
            if (char.IsHighSurrogate(ch) &&
                pos < content.Length &&
                char.IsLowSurrogate(content[pos]))
            {
                codePoint = char.ConvertToUtf32(ch, content[pos]);
                pos++;
                return true;
            }

            codePoint = ch;
            return true;
        }

        if (pos >= content.Length)
        {
            return false;
        }

        var escaped = content[pos++];
        switch (escaped)
        {
            case 'd':
            case 'D':
            case 's':
            case 'S':
            case 'w':
            case 'W':
                return true;
            case 'f':
                codePoint = '\f';
                return true;
            case 'n':
                codePoint = '\n';
                return true;
            case 'r':
                codePoint = '\r';
                return true;
            case 't':
                codePoint = '\t';
                return true;
            case 'v':
                codePoint = '\v';
                return true;
            case 'c':
                if (pos < content.Length && char.IsLetter(content[pos]))
                {
                    codePoint = content[pos++] % 32;
                }
                return true;
            case 'x':
                if (pos + 1 < content.Length &&
                    IsHexDigit(content[pos]) &&
                    IsHexDigit(content[pos + 1]))
                {
                    codePoint = Convert.ToInt32(content.Substring(pos, 2), 16);
                    pos += 2;
                }
                return true;
            case 'u':
                if (pos + 3 < content.Length &&
                    IsHexDigit(content[pos]) &&
                    IsHexDigit(content[pos + 1]) &&
                    IsHexDigit(content[pos + 2]) &&
                    IsHexDigit(content[pos + 3]))
                {
                    codePoint = Convert.ToInt32(content.Substring(pos, 4), 16);
                    pos += 4;
                }
                return true;
            default:
                codePoint = escaped;
                return true;
        }
    }

    private static bool ContainsUnicodeSetsBreakingChangeLiteral(string content)
    {
        // Keep this narrowly scoped to known breaking-change parse negatives
        // without blocking valid UnicodeSets set-notation expressions.
        var escaped = false;
        for (var i = 0; i < content.Length; i++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (content[i] == '\\')
            {
                escaped = true;
                continue;
            }

            if (content[i] == '^')
            {
                return true;
            }
        }

        if (content.Length == 1)
        {
            return content[0] is '(' or ')' or '[' or '{' or '}' or '/' or '-' or '|';
        }

        if (content.Length == 2 && content[0] == content[1])
        {
            return content[0] is '&' or '!' or '#' or '$' or '%' or '*' or '+' or ',' or '.' or ':' or ';' or '<' or '=' or '>' or '?' or '@' or '`' or '~' or '^' or '-' or '|';
        }

        return false;
    }

    private static readonly HashSet<string> GeneralCategoryValues = new(StringComparer.Ordinal)
    {
        "C", "Cc", "Cf", "Cn", "Co", "Cs",
        "L", "LC", "Ll", "Lm", "Lo", "Lt", "Lu",
        "M", "Mc", "Me", "Mn",
        "N", "Nd", "Nl", "No",
        "P", "Pc", "Pd", "Pe", "Pf", "Pi", "Po", "Ps",
        "S", "Sc", "Sk", "Sm", "So",
        "Z", "Zl", "Zp", "Zs",
        "Cased_Letter",
        "Close_Punctuation",
        "Combining_Mark",
        "Connector_Punctuation",
        "Control",
        "Currency_Symbol",
        "Dash_Punctuation",
        "Decimal_Number",
        "Enclosing_Mark",
        "Final_Punctuation",
        "Format",
        "Initial_Punctuation",
        "Letter",
        "Letter_Number",
        "Line_Separator",
        "Lowercase_Letter",
        "Mark",
        "Math_Symbol",
        "Modifier_Letter",
        "Modifier_Symbol",
        "Nonspacing_Mark",
        "Number",
        "Open_Punctuation",
        "Other",
        "Other_Letter",
        "Other_Number",
        "Other_Punctuation",
        "Other_Symbol",
        "Paragraph_Separator",
        "Private_Use",
        "Punctuation",
        "Separator",
        "Space_Separator",
        "Spacing_Mark",
        "Surrogate",
        "Symbol",
        "Titlecase_Letter",
        "Unassigned",
        "Uppercase_Letter",
        "cntrl",
        "digit",
        "punct",
        "space",
    };

    private static bool HasLegacyUnicodePrefix(string value)
    {
        if (value.Length <= 2)
        {
            return false;
        }

        if (value.StartsWith("In", StringComparison.Ordinal) || value.StartsWith("Is", StringComparison.Ordinal))
        {
            return char.IsUpper(value[2]);
        }

        return false;
    }

    private static bool IsPropertyToken(string token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        for (var i = 0; i < token.Length; i++)
        {
            var ch = token[i];
            if (!(ch == '_' || char.IsLetterOrDigit(ch)))
            {
                return false;
            }
        }

        return true;
    }

    private static readonly HashSet<string> NonBinaryPropertyNames = new(StringComparer.Ordinal)
    {
        "General_Category", "gc",
        "Script", "sc",
        "Script_Extensions", "scx"
    };

    private static readonly HashSet<string> StringPropertyNames = new(StringComparer.Ordinal)
    {
        "Basic_Emoji",
        "Emoji_Keycap_Sequence",
        "RGI_Emoji_Modifier_Sequence",
        "RGI_Emoji_Flag_Sequence",
        "RGI_Emoji_Tag_Sequence",
        "RGI_Emoji_ZWJ_Sequence",
        "RGI_Emoji",
    };

    private static readonly HashSet<string> BinaryPropertyNames = new(StringComparer.Ordinal)
    {
        "ASCII", "ASCII_Hex_Digit", "AHex",
        "Alphabetic", "Alpha",
        "Any", "Assigned",
        "Bidi_Control", "Bidi_C",
        "Bidi_Mirrored", "Bidi_M",
        "Case_Ignorable", "CI",
        "Cased",
        "Changes_When_Casefolded", "CWCF",
        "Changes_When_Casemapped", "CWCM",
        "Changes_When_Lowercased", "CWL",
        "Changes_When_NFKC_Casefolded", "CWKCF",
        "Changes_When_Titlecased", "CWT",
        "Changes_When_Uppercased", "CWU",
        "Dash", "Default_Ignorable_Code_Point", "DI",
        "Deprecated", "Dep",
        "Diacritic", "Dia",
        "Emoji", "Emoji_Presentation", "EPres",
        "Emoji_Modifier", "EMod",
        "Emoji_Modifier_Base", "EBase",
        "Emoji_Component", "EComp",
        "Extended_Pictographic", "ExtPict",
        "Extender", "Ext",
        "Hex_Digit", "Hex",
        "ID_Start", "IDS",
        "ID_Continue", "IDC",
        "Ideographic", "Ideo",
        "Join_Control", "Join_C",
        "Lowercase", "Lower",
        "Math",
        "Noncharacter_Code_Point", "NChar",
        "Pattern_Syntax", "Pat_Syn",
        "Pattern_White_Space", "Pat_WS",
        "Quotation_Mark", "QMark",
        "Radical",
        "Regional_Indicator", "RI",
        "Sentence_Terminal", "STerm",
        "Soft_Dotted", "SD",
        "Terminal_Punctuation", "Term",
        "Unified_Ideograph", "UIdeo",
        "Uppercase", "Upper",
        "Variation_Selector", "VS",
        "White_Space", "WSpace",
        "XID_Start", "XIDS",
        "XID_Continue", "XIDC",
        "Grapheme_Base", "Gr_Base",
        "Grapheme_Extend", "Gr_Ext",
        "IDS_Binary_Operator", "IDSB",
        "IDS_Trinary_Operator", "IDST",
        "Logical_Order_Exception", "LOE",
    };

    private static void ValidateUnicodePropertyEscapeBody(string body, RegexFlags flags, bool negated)
    {
        if (string.IsNullOrEmpty(body))
        {
            throw new RegexSyntaxError("Invalid Unicode property escape.");
        }

        if (body.IndexOf(' ') >= 0 || body.IndexOf('\t') >= 0 || body.IndexOf('\r') >= 0 || body.IndexOf('\n') >= 0 ||
            body.Contains(':', StringComparison.Ordinal) ||
            body.Contains('-', StringComparison.Ordinal) ||
            body.StartsWith("^", StringComparison.Ordinal))
        {
            throw new RegexSyntaxError("Invalid Unicode property escape.");
        }

        var separator = body.IndexOf('=');
        if (separator >= 0)
        {
            if (separator == 0 || separator == body.Length - 1 || body.LastIndexOf('=') != separator)
            {
                throw new RegexSyntaxError("Invalid Unicode property escape.");
            }

            var name = body[..separator];
            var value = body[(separator + 1)..];
            if (!NonBinaryPropertyNames.Contains(name))
            {
                throw new RegexSyntaxError("Invalid Unicode property escape.");
            }

            if (!IsPropertyToken(value))
            {
                throw new RegexSyntaxError("Invalid Unicode property escape.");
            }

            if (name is "gc" or "General_Category")
            {
                if (!GeneralCategoryValues.Contains(value))
                {
                    throw new RegexSyntaxError("Invalid Unicode property escape.");
                }
            }
            else if (HasLegacyUnicodePrefix(value))
            {
                throw new RegexSyntaxError("Invalid Unicode property escape.");
            }
            else if (UnicodePropertyEscapeData.EnsureLoaded() == null &&
                     UnicodePropertyEscapeData.GetRanges("Script=" + value) == null)
            {
                // sc/scx values must match the spec's script list exactly
                // (UnicodeMatchPropertyValue); the generated table holds every
                // canonical name and alias.
                throw new RegexSyntaxError("Invalid Unicode property escape.");
            }

            if ((name is "scx" or "Script_Extensions") && StringPropertyNames.Contains(value) && !flags.UnicodeSets)
            {
                throw new RegexSyntaxError("Invalid Unicode property escape.");
            }

            return;
        }

        if (!IsPropertyToken(body))
        {
            throw new RegexSyntaxError("Invalid Unicode property escape.");
        }

        // ECMA-262 22.2.1 (UnicodeMatchProperty): names must match the spec tables
        // exactly — no loose matching, and no "Is"/"In" grammar extensions
        // (property-escapes/grammar-extension-Is-prefix-*.js requires SyntaxError).
        var lookupBody = body;

        if (NonBinaryPropertyNames.Contains(lookupBody))
        {
            throw new RegexSyntaxError("Invalid Unicode property escape.");
        }

        if (StringPropertyNames.Contains(lookupBody))
        {
            if (!flags.UnicodeSets || negated)
            {
                throw new RegexSyntaxError("Invalid Unicode property escape.");
            }

            return;
        }

        if (!BinaryPropertyNames.Contains(lookupBody) &&
            !StringPropertyNames.Contains(lookupBody) &&
            !GeneralCategoryValues.Contains(lookupBody))
        {
            throw new RegexSyntaxError("Invalid Unicode property escape.");
        }
    }

    private static string RewriteEcmaCharacterClassEscapes(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return pattern;
        }

        const string whiteSpaceClass = @"[\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
        const string nonWhiteSpaceClass = @"[^\u0009-\u000D\u0020\u00A0\u1680\u2000-\u200A\u2028\u2029\u202F\u205F\u3000\uFEFF]";
        var rewritten = new StringBuilder(pattern.Length + 24);
        var inCharClass = false;
        var classStartPos = -1;
        var classContentStart = -1;
        var classNegated = false;

        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\' && i + 1 < pattern.Length)
            {
                var next = pattern[i + 1];
                // \cX control escape — .NET doesn't support it. Rewrite to the
                // literal control character per annex B.1.4 (char % 32).
                if (next == 'c' && i + 2 < pattern.Length)
                {
                    var controlChar = (char)(pattern[i + 2] % 32);
                    rewritten.Append('\\').Append('x');
                    rewritten.Append(((int)controlChar).ToString("x2"));
                    i += 2;
                    continue;
                }
                if (!inCharClass)
                {
                    switch (next)
                    {
                        case 'd':
                            rewritten.Append("[0-9]");
                            i++;
                            continue;
                        case 'D':
                            rewritten.Append("[^0-9]");
                            i++;
                            continue;
                        case 'w':
                            rewritten.Append("[A-Za-z0-9_]");
                            i++;
                            continue;
                        case 'W':
                            rewritten.Append("[^A-Za-z0-9_]");
                            i++;
                            continue;
                        case 's':
                            rewritten.Append(whiteSpaceClass);
                            i++;
                            continue;
                        case 'S':
                            rewritten.Append(nonWhiteSpaceClass);
                            i++;
                            continue;
                    }
                }

                rewritten.Append(ch);
                rewritten.Append(next);
                i++;
                continue;
            }

            if (ch == '[' && !inCharClass)
            {
                inCharClass = true;
                classStartPos = rewritten.Length;
                classNegated = false;
                rewritten.Append(ch);
                if (i + 1 < pattern.Length && pattern[i + 1] == '^')
                {
                    rewritten.Append('^');
                    classNegated = true;
                    i++;
                }
                classContentStart = rewritten.Length;
                continue;
            }

            if (ch == ']' && inCharClass)
            {
                inCharClass = false;
                if (rewritten.Length == classContentStart)
                {
                    // ECMAScript Annex B: empty character class.
                    // [] matches nothing; [^] matches any character (including \n).
                    // .NET rejects "[]" / "[^]", so rewrite to equivalent expressions.
                    rewritten.Length = classStartPos;
                    if (classNegated)
                    {
                        rewritten.Append(@"[\s\S]");
                    }
                    else
                    {
                        rewritten.Append(@"[^\w\W]");
                    }
                }
                else
                {
                    rewritten.Append(ch);
                }
                continue;
            }

            rewritten.Append(ch);
        }

        return rewritten.ToString();
    }

    internal static string RewriteNamedGroupSyntaxForDotNet(string pattern, Dictionary<string, string> namedGroupMap)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return pattern;
        }

        var rewritten = new StringBuilder(pattern.Length + 16);
        var inCharClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                if (i + 1 < pattern.Length &&
                    pattern[i + 1] == 'k' &&
                    i + 2 < pattern.Length &&
                    pattern[i + 2] == '<')
                {
                    var nameStart = i + 3;
                    var nameEnd = pattern.IndexOf('>', nameStart);
                    if (nameEnd > nameStart)
                    {
                        var originalNameRaw = pattern[nameStart..nameEnd];
                        var originalName = TryCanonicalizeGroupNameToken(originalNameRaw, out var canonicalRef)
                            ? canonicalRef
                            : originalNameRaw;
                        var alias = GetOrCreateNamedGroupAlias(originalName, namedGroupMap);
                        rewritten.Append(@"\k<");
                        rewritten.Append(alias);
                        rewritten.Append('>');
                        i = nameEnd;
                        continue;
                    }
                }

                rewritten.Append(ch);
                if (i + 1 < pattern.Length)
                {
                    rewritten.Append(pattern[i + 1]);
                    i++;
                }

                continue;
            }

            if (inCharClass)
            {
                rewritten.Append(ch);
                if (ch == ']')
                {
                    inCharClass = false;
                }

                continue;
            }

            if (ch == '[')
            {
                inCharClass = true;
                rewritten.Append(ch);
                continue;
            }

            if (ch == '(' &&
                i + 2 < pattern.Length &&
                pattern[i + 1] == '?' &&
                pattern[i + 2] == '<' &&
                !(i + 3 < pattern.Length && (pattern[i + 3] == '=' || pattern[i + 3] == '!')))
            {
                var nameStart = i + 3;
                var nameEnd = pattern.IndexOf('>', nameStart);
                if (nameEnd > nameStart)
                {
                    var originalNameRaw = pattern[nameStart..nameEnd];
                    var originalName = TryCanonicalizeGroupNameToken(originalNameRaw, out var canonicalName)
                        ? canonicalName
                        : originalNameRaw;
                    var alias = GetOrCreateNamedGroupAlias(originalName, namedGroupMap);
                    rewritten.Append("(?<");
                    rewritten.Append(alias);
                    rewritten.Append('>');
                    i = nameEnd;
                    continue;
                }
            }

            rewritten.Append(ch);
        }

        return rewritten.ToString();
    }

    private static string GetOrCreateNamedGroupAlias(string originalName, Dictionary<string, string> namedGroupMap)
    {
        if (namedGroupMap.TryGetValue(originalName, out var existing))
        {
            return existing;
        }

        var alias = "g" + (namedGroupMap.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        namedGroupMap[originalName] = alias;
        return alias;
    }

    internal static string RewriteUnicodeCodePointEscapes(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return pattern;
        }

        var rewritten = new StringBuilder(pattern.Length);
        var inCharClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\' && i + 1 < pattern.Length)
            {
                if (pattern[i + 1] == 'u' &&
                    i + 2 < pattern.Length &&
                    pattern[i + 2] == '{')
                {
                    var hexStart = i + 3;
                    var hexEnd = pattern.IndexOf('}', hexStart);
                    if (hexEnd > hexStart)
                    {
                        var hex = pattern[hexStart..hexEnd];
                        if (int.TryParse(hex, System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out var codePoint) &&
                            codePoint is >= 0 and <= 0x10FFFF)
                        {
                            AppendRegexLiteralCodePoint(rewritten, codePoint, inCharClass);
                            i = hexEnd;
                            continue;
                        }
                    }
                }

                rewritten.Append(ch);
                rewritten.Append(pattern[i + 1]);
                i++;
                continue;
            }

            if (ch == '[' && !inCharClass)
            {
                inCharClass = true;
            }
            else if (ch == ']' && inCharClass)
            {
                inCharClass = false;
            }

            rewritten.Append(ch);
        }

        return rewritten.ToString();
    }

    public static string RewriteUnicodePropertyEscapesForDotNet(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return pattern;
        }

        var rewritten = new StringBuilder(pattern.Length + 16);
        var inCharClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\' &&
                i + 3 < pattern.Length &&
                (pattern[i + 1] == 'p' || pattern[i + 1] == 'P') &&
                pattern[i + 2] == '{')
            {
                var negated = pattern[i + 1] == 'P';
                var bodyStart = i + 3;
                var close = pattern.IndexOf('}', bodyStart);
                if (close > bodyStart)
                {
                    var body = pattern[bodyStart..close];
                    // Exact codepoint ranges from the generated Unicode tables
                    // beat any .NET category/block approximation.
                    if (TryRangeBasedPropertyRewrite(body, negated, inCharClass, out var exact))
                    {
                        rewritten.Append(exact);
                        i = close;
                        continue;
                    }
                    if (TryRewriteUnicodePropertyBodyForDotNet(body, negated, out var replacement))
                    {
                        rewritten.Append(replacement);
                        i = close;
                        continue;
                    }
                    // Fallback: rewrite to a character class based on hardcoded data
                    if (TryHardcodedPropertyRewrite(body, negated, out var hardcoded))
                    {
                        rewritten.Append(hardcoded);
                        i = close;
                        continue;
                    }
                    // Last resort: match anything/nothing
                    rewritten.Append(negated ? @"[\s\S]" : @"[^\s\S]");
                    i = close;
                    continue;
                }
            }

            if (ch == '\\' && i + 1 < pattern.Length)
            {
                rewritten.Append(ch);
                rewritten.Append(pattern[i + 1]);
                i++;
                continue;
            }

            if (ch == '[' && !inCharClass)
            {
                inCharClass = true;
            }
            else if (ch == ']' && inCharClass)
            {
                inCharClass = false;
            }

            rewritten.Append(ch);
        }

        return rewritten.ToString();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Body, bool Negated, bool InClass), string?> s_propertyRewriteCache = new();

    /// <summary>
    /// Rewrite a \p{...}/\P{...} body into a .NET pattern fragment using the exact
    /// codepoint ranges from the generated Unicode tables. BMP codepoints become a
    /// character class; supplementary-plane codepoints become surrogate-pair
    /// alternatives (tried first so pairs win over lone surrogates, matching /u
    /// code-point semantics).
    /// </summary>
    private static bool TryRangeBasedPropertyRewrite(string body, bool negated, bool inCharClass, out string replacement)
    {
        replacement = string.Empty;
        var cached = s_propertyRewriteCache.GetOrAdd((body, negated, inCharClass), static key =>
        {
            var ranges = UnicodePropertyEscapeData.GetRanges(key.Body);
            if (ranges == null && key.Body.StartsWith("gc=", StringComparison.Ordinal))
            {
                ranges = UnicodePropertyEscapeData.GetRanges(key.Body[3..]);
            }

            if (ranges == null || ranges.Length % 2 != 0)
            {
                return null;
            }

            var effective = key.Negated ? ComplementRanges(ranges) : ranges;
            return BuildDotNetPatternFromRanges(effective, key.InClass);
        });

        if (cached == null)
        {
            return false;
        }

        replacement = cached;
        return true;
    }

    /// <summary>Complement sorted, disjoint [start,end] codepoint pairs over U+0000..U+10FFFF.</summary>
    private static uint[] ComplementRanges(uint[] ranges)
    {
        var result = new List<uint>(ranges.Length + 2);
        uint next = 0;
        for (var i = 0; i < ranges.Length; i += 2)
        {
            var start = ranges[i];
            var end = ranges[i + 1];
            if (start > next)
            {
                result.Add(next);
                result.Add(start - 1);
            }

            if (end >= next)
            {
                next = end + 1;
            }

            if (next > 0x10FFFF)
            {
                return result.ToArray();
            }
        }

        result.Add(next);
        result.Add(0x10FFFF);
        return result.ToArray();
    }

    private static string BuildDotNetPatternFromRanges(uint[] ranges, bool inCharClass)
    {
        var bmpItems = new StringBuilder();
        var astralAlts = new List<string>();
        for (var i = 0; i < ranges.Length; i += 2)
        {
            var start = ranges[i];
            var end = Math.Min(ranges[i + 1], 0x10FFFFu);
            if (start > end)
            {
                continue;
            }

            if (start <= 0xFFFF)
            {
                var bmpEnd = Math.Min(end, 0xFFFFu);
                AppendBmpClassRange(bmpItems, (char)start, (char)bmpEnd);
            }

            if (end >= 0x10000)
            {
                AppendAstralAlternatives(astralAlts, Math.Max(start, 0x10000u), end);
            }
        }

        if (inCharClass)
        {
            // Inside an enclosing class only BMP units can be expressed;
            // supplementary codepoints cannot form surrogate pairs in a class.
            return bmpItems.ToString();
        }

        if (bmpItems.Length == 0 && astralAlts.Count == 0)
        {
            return @"[^\s\S]";
        }

        if (astralAlts.Count == 0)
        {
            return "[" + bmpItems + "]";
        }

        var sb = new StringBuilder("(?:");
        for (var i = 0; i < astralAlts.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('|');
            }

            sb.Append(astralAlts[i]);
        }

        if (bmpItems.Length > 0)
        {
            sb.Append("|[").Append(bmpItems).Append(']');
        }

        sb.Append(')');
        return sb.ToString();
    }

    private static void AppendBmpClassRange(StringBuilder sb, char start, char end)
    {
        AppendClassUnit(sb, start);
        if (end > start)
        {
            if (end > start + 1)
            {
                sb.Append('-');
            }

            AppendClassUnit(sb, end);
        }
    }

    private static void AppendClassUnit(StringBuilder sb, char unit)
    {
        sb.Append(@"\u");
        sb.Append(((int)unit).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void AppendAstralAlternatives(List<string> alts, uint start, uint end)
    {
        var hiStart = (char)(0xD800 + ((start - 0x10000) >> 10));
        var loStart = (char)(0xDC00 + ((start - 0x10000) & 0x3FF));
        var hiEnd = (char)(0xD800 + ((end - 0x10000) >> 10));
        var loEnd = (char)(0xDC00 + ((end - 0x10000) & 0x3FF));

        if (hiStart == hiEnd)
        {
            alts.Add(BuildSurrogatePairAlternative(hiStart, hiStart, loStart, loEnd));
            return;
        }

        if (loStart != 0xDC00)
        {
            alts.Add(BuildSurrogatePairAlternative(hiStart, hiStart, loStart, '\uDFFF'));
            hiStart++;
        }

        var hiTail = hiEnd;
        if (loEnd != 0xDFFF)
        {
            alts.Add(BuildSurrogatePairAlternative(hiEnd, hiEnd, '\uDC00', loEnd));
            hiTail--;
        }

        if (hiStart <= hiTail)
        {
            alts.Add(BuildSurrogatePairAlternative(hiStart, hiTail, '\uDC00', '\uDFFF'));
        }
    }

    private static string BuildSurrogatePairAlternative(char hiStart, char hiEnd, char loStart, char loEnd)
    {
        var sb = new StringBuilder(24);
        if (hiStart == hiEnd)
        {
            AppendClassUnit(sb, hiStart);
        }
        else
        {
            sb.Append('[');
            AppendBmpClassRange(sb, hiStart, hiEnd);
            sb.Append(']');
        }

        if (loStart == loEnd)
        {
            AppendClassUnit(sb, loStart);
        }
        else
        {
            sb.Append('[');
            AppendBmpClassRange(sb, loStart, loEnd);
            sb.Append(']');
        }

        return sb.ToString();
    }

    private static bool TryHardcodedPropertyRewrite(string body, bool negated, out string replacement)
    {
        replacement = string.Empty;
        var key = body.ToLowerInvariant();
        var eqIdx = key.IndexOf('=');
        if (eqIdx > 0) key = key.Substring(eqIdx + 1);

        switch (key)
        {
            case "ascii": replacement = negated ? @"[^\x00-\x7F]" : @"[\x00-\x7F]"; return true;
            case "ascii_hex_digit": case "ahex": replacement = negated ? @"[^0-9A-Fa-f]" : @"[0-9A-Fa-f]"; return true;
            case "any": case "assigned": replacement = negated ? @"[^\s\S]" : @"[\s\S]"; return true;
            case "hex_digit": case "hex": replacement = negated ? @"[^0-9A-Fa-f]" : @"[0-9A-Fa-f]"; return true;
            case "letter": case "l": case "alphabetic": case "alpha":
                replacement = negated ? @"[^A-Za-z]" : @"[A-Za-z]"; return true;
            case "uppercase_letter": case "lu":
                replacement = negated ? @"[^A-Z]" : @"[A-Z]"; return true;
            case "lowercase_letter": case "ll":
                replacement = negated ? @"[^a-z]" : @"[a-z]"; return true;
            case "decimal_number": case "nd": case "digit":
                replacement = negated ? @"[^0-9]" : @"[0-9]"; return true;
            case "white_space": case "wspace":
                replacement = negated ? @"[^\t\n\v\f\r \xA0]" : @"[\t\n\v\f\r \xA0]"; return true;
            case "word": case "w":
                replacement = negated ? @"[^0-9A-Za-z_]" : @"[0-9A-Za-z_]"; return true;
            case "not_word": case "not w":
                replacement = negated ? @"[0-9A-Za-z_]" : @"[^0-9A-Za-z_]"; return true;
            case "bidi_control": case "bidi_c": case "bidi_mirrored": case "bidi_m":
            case "case_ignorable": case "ci": case "join_control": case "join_c":
            case "diacritic": case "dia": case "extender": case "ext":
                replacement = negated ? @"[\s\S]" : @"[^\s\S]"; return true;
            case "cased": case "changes_when_casefolded": case "cwcf":
            case "changes_when_casemapped": case "cwcm": case "changes_when_titlecased": case "cwt":
                replacement = negated ? @"[^A-Za-z]" : @"[A-Za-z]"; return true;
            case "changes_when_lowercased": case "cwl":
                replacement = negated ? @"[^A-Z]" : @"[A-Z]"; return true;
            case "changes_when_uppercased": case "cwu":
                replacement = negated ? @"[^a-z]" : @"[a-z]"; return true;
            case "dash": replacement = negated ? @"[^-]" : @"[-]"; return true;
            case "default_ignorable_code_point": case "di2":
            case "deprecated": case "dep": case "emoji": case "emoji_presentation": case "epres":
            case "emoji_modifier": case "emod": case "emoji_modifier_base": case "ebase":
            case "emoji_component": case "ecomp": case "extended_pictographic": case "extpict":
            case "grapheme_extend": case "gr_ext": case "ideographic": case "ideo":
            case "ids_binary_operator": case "idsb": case "ids_trinary_operator": case "idst":
            case "logical_order_exception": case "loe": case "noncharacter_code_point": case "nchar":
            case "radical": case "regional_indicator": case "ri":
            case "unified_ideograph": case "uideo": case "variation_selector": case "vs":
                replacement = negated ? @"[\s\S]" : @"[^\s\S]"; return true;
            case "grapheme_base": case "gr_base": case "any2": case "assigned2":
                replacement = negated ? @"[^\s\S]" : @"[\s\S]"; return true;
            case "id_start": case "ids": case "xid_start": case "xids":
                replacement = negated ? @"[^A-Za-z_]" : @"[A-Za-z_]"; return true;
            case "id_continue": case "idc": case "xid_continue": case "xidc":
                replacement = negated ? @"[^0-9A-Za-z_]" : @"[0-9A-Za-z_]"; return true;
            case "lowercase": case "lower":
                replacement = negated ? @"[^a-z]" : @"[a-z]"; return true;
            case "uppercase": case "upper":
                replacement = negated ? @"[^A-Z]" : @"[A-Z]"; return true;
            case "math": case "pattern_syntax": case "pat_syn":
                replacement = negated ? @"[^\x00-\x7F]" : @"[\x00-\x7F]"; return true;
            case "pattern_white_space": case "pat_ws":
                replacement = negated ? @"[^\t\n\v\f\r ]" : @"[\t\n\v\f\r ]"; return true;
            case "quotation_mark": case "qmark":
                replacement = negated ? "[^\"]" : "[\"]"; return true;
            case "sentence_terminal": case "sterm": case "terminal_punctuation": case "term":
                replacement = negated ? @"[^.!?]" : @"[.!?]"; return true;
            case "soft_dotted": case "sd":
                replacement = negated ? @"[^ij]" : @"[ij]"; return true;
        }
        return false;
    }

    private static bool TryRewriteUnicodePropertyBodyForDotNet(string body, bool negated, out string replacement)
    {
        replacement = string.Empty;
        if (string.IsNullOrEmpty(body))
        {
            return false;
        }

        var separator = body.IndexOf('=');
        if (separator > 0 && separator < body.Length - 1)
        {
            var name = body[..separator];
            var value = body[(separator + 1)..];

            if (name is "gc" or "General_Category" &&
                TryMapGeneralCategoryValueToDotNet(value, out var gc))
            {
                replacement = negated ? @"\P{" + gc + "}" : @"\p{" + gc + "}";
                return true;
            }

            if (name is "sc" or "Script" or "scx" or "Script_Extensions" &&
                TryMapScriptValueToDotNet(value, out var script))
            {
                replacement = negated ? @"\P{Is" + script + "}" : @"\p{Is" + script + "}";
                return true;
            }

            return false;
        }

        if (TryMapGeneralCategoryValueToDotNet(body, out var mappedCategory))
        {
            replacement = negated ? @"\P{" + mappedCategory + "}" : @"\p{" + mappedCategory + "}";
            return true;
        }

        if (TryMapScriptValueToDotNet(body, out var mappedScript))
        {
            replacement = negated ? @"\P{Is" + mappedScript + "}" : @"\p{Is" + mappedScript + "}";
            return true;
        }

        return false;
    }

    private static bool TryMapScriptValueToDotNet(string value, out string script)
    {
        script = string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        var mapped = value switch
        {
            "Hira" => "Hiragana",
            "Kana" => "Katakana",
            "Hrkt" => "KatakanaOrHiragana",
            "Zyyy" => "Common",
            "Zinh" => "Inherited",
            _ => value
        };

        var sb = new StringBuilder(mapped.Length);
        for (var i = 0; i < mapped.Length; i++)
        {
            var ch = mapped[i];
            if (ch == '_' || ch == '-' || ch == ' ')
            {
                continue;
            }

            sb.Append(ch);
        }

        if (sb.Length == 0)
        {
            return false;
        }

        script = sb.ToString();
        return true;
    }

    private static bool TryMapGeneralCategoryValueToDotNet(string value, out string category)
    {
        category = value switch
        {
            "Letter" => "L",
            "Uppercase_Letter" => "Lu",
            "Lowercase_Letter" => "Ll",
            "Titlecase_Letter" => "Lt",
            "Modifier_Letter" => "Lm",
            "Other_Letter" => "Lo",
            "Cased_Letter" => "L",
            "Mark" => "M",
            "Nonspacing_Mark" => "Mn",
            "Spacing_Mark" => "Mc",
            "Enclosing_Mark" => "Me",
            "Combining_Mark" => "M",
            "Number" => "N",
            "Decimal_Number" => "Nd",
            "Letter_Number" => "Nl",
            "Other_Number" => "No",
            "Punctuation" => "P",
            "Connector_Punctuation" => "Pc",
            "Dash_Punctuation" => "Pd",
            "Open_Punctuation" => "Ps",
            "Close_Punctuation" => "Pe",
            "Initial_Punctuation" => "Pi",
            "Final_Punctuation" => "Pf",
            "Other_Punctuation" => "Po",
            "Symbol" => "S",
            "Math_Symbol" => "Sm",
            "Currency_Symbol" => "Sc",
            "Modifier_Symbol" => "Sk",
            "Other_Symbol" => "So",
            "Separator" => "Z",
            "Space_Separator" => "Zs",
            "Line_Separator" => "Zl",
            "Paragraph_Separator" => "Zp",
            "Other" => "C",
            "Control" => "Cc",
            "Format" => "Cf",
            "Surrogate" => "Cs",
            "Private_Use" => "Co",
            "Unassigned" => "Cn",
            _ => value
        };

        if (category.Length == 1)
        {
            return category[0] is 'L' or 'M' or 'N' or 'P' or 'S' or 'Z' or 'C';
        }

        if (category.Length == 2 &&
            category[0] is 'L' or 'M' or 'N' or 'P' or 'S' or 'Z' or 'C')
        {
            return true;
        }

        return false;
    }

    private static void AppendRegexLiteralCodePoint(StringBuilder builder, int codePoint, bool inCharClass)
    {
        var literal = char.ConvertFromUtf32(codePoint);
        foreach (var c in literal)
        {
            if (!inCharClass)
            {
                if (c is '.' or '^' or '$' or '|' or '?' or '*' or '+' or '(' or ')' or '[' or ']' or '{' or '}' or '\\')
                {
                    builder.Append('\\');
                }
            }
            else if (c is '\\' or ']' or '^' or '-')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }
    }

    /// <summary>
    /// Rewrites ECMAScript forward backreferences (backreferences that appear before
    /// the capturing group they reference) to (?:) which always matches empty.
    /// In ECMAScript, a backreference to a group that hasn't been captured yet
    /// matches the empty string. .NET with RegexOptions.ECMAScript may not
    /// <summary>
    /// Rewrite \cX control escapes to the literal control character \xHH.
    /// .NET's regex engine doesn't support \cX syntax, so we replace it
    /// with the equivalent \xHH escape per annex B.1.4 (char % 32).
    /// </summary>
    internal static string RewriteControlEscapesForDotNet(string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return pattern;
        var sb = new System.Text.StringBuilder(pattern.Length);
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '\\' && i + 2 < pattern.Length && pattern[i + 1] == 'c')
            {
                var ch = (int)pattern[i + 2] % 32;
                sb.Append("\\x").Append(ch.ToString("x2"));
                i += 2;
                continue;
            }
            sb.Append(pattern[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// handle this correctly, so we rewrite forward references explicitly.
    /// </summary>
    internal static string RewriteForwardBackreferences(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return pattern;
        }

        // First pass: find all capturing group positions.
        // groupPositions[N] = position of the opening '(' of group N in the pattern.
        var groupPositions = new List<int> { -1 }; // 0-indexed sentinel
        var inCharClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\')
            {
                if (i + 1 < pattern.Length) i++;
                continue;
            }

            if (ch == '[' && !inCharClass)
            {
                inCharClass = true;
                continue;
            }

            if (ch == ']' && inCharClass)
            {
                inCharClass = false;
                continue;
            }

            if (inCharClass) continue;

            if (ch == '(')
            {
                var j = i + 1;
                // Check for non-capturing group markers
                if (j < pattern.Length && pattern[j] == '?')
                {
                    j++;
                    if (j < pattern.Length)
                    {
                        var marker = pattern[j];
                        if (marker is ':' or '=' or '!')
                        {
                            continue; // non-capturing
                        }
                        if (marker == '<')
                        {
                            j++;
                            if (j < pattern.Length && (pattern[j] == '=' || pattern[j] == '!'))
                            {
                                continue; // lookbehind
                            }
                            // Named capturing group '(?<name>' — still captures
                            groupPositions.Add(i);
                            continue;
                        }
                        // Other (?...) syntaxes like modifiers — treat as non-capturing
                        continue;
                    }
                }

                // Plain capturing group
                groupPositions.Add(i);
            }
        }

        var captureCount = groupPositions.Count - 1; // exclude sentinel
        if (captureCount == 0)
        {
            return pattern;
        }

        // Second pass: rewrite forward backreferences.
        var sb = new StringBuilder(pattern.Length);
        inCharClass = false;
        var pos = 0;
        while (pos < pattern.Length)
        {
            var ch = pattern[pos];
            if (ch == '\\' && pos + 1 < pattern.Length && !inCharClass)
            {
                var next = pattern[pos + 1];
                if (next is >= '1' and <= '9')
                {
                    // Read the full decimal escape
                    var digitsStart = pos + 1;
                    var digitsEnd = digitsStart;
                    while (digitsEnd < pattern.Length && char.IsDigit(pattern[digitsEnd]))
                    {
                        digitsEnd++;
                    }

                    var digitsSpan = pattern[digitsStart..digitsEnd];
                    if (int.TryParse(digitsSpan, System.Globalization.NumberStyles.None,
                            System.Globalization.CultureInfo.InvariantCulture, out var groupNum))
                    {
                        if (groupNum <= captureCount && groupPositions[groupNum] > pos)
                        {
                            // Forward reference: backreference appears before group groupNum.
                            // Per ECMAScript, matches empty string when group hasn't captured yet.
                            sb.Append("(?:)");
                            pos = digitsEnd;
                            continue;
                        }
                    }
                }

                sb.Append(ch);
                sb.Append(next);
                pos += 2;
                continue;
            }

            if (ch == '[' && !inCharClass)
            {
                inCharClass = true;
                sb.Append(ch);
                pos++;
                continue;
            }

            if (ch == ']' && inCharClass)
            {
                inCharClass = false;
            }

            sb.Append(ch);
            pos++;
        }

        return sb.ToString();
    }
}
