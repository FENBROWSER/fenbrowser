using System;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering.Css;

internal enum CssPropertyNormalizationResult
{
    UnknownProperty,
    Valid,
    Invalid
}

/// <summary>
/// Validates and serializes values whose CSSOM grammar is implemented by the
/// engine. Unknown properties remain the responsibility of the wider CSS
/// declaration parser.
/// </summary>
internal static class CssStyleDeclarationValueNormalizer
{
    private const int MaxDeclarationValueLength = 64 * 1024;

    private static readonly HashSet<string> PositionGeometryProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "x", "y", "cx", "cy"
    };

    public static CssPropertyNormalizationResult Normalize(
        string property,
        string value,
        out string serialized)
    {
        serialized = string.Empty;
        if (string.IsNullOrWhiteSpace(property))
        {
            return CssPropertyNormalizationResult.UnknownProperty;
        }

        bool allowsNegative;
        bool allowsAuto;
        if (PositionGeometryProperties.Contains(property))
        {
            allowsNegative = true;
            allowsAuto = false;
        }
        else if (property.Equals("r", StringComparison.OrdinalIgnoreCase))
        {
            allowsNegative = false;
            allowsAuto = false;
        }
        else if (property.Equals("rx", StringComparison.OrdinalIgnoreCase) ||
                 property.Equals("ry", StringComparison.OrdinalIgnoreCase))
        {
            allowsNegative = false;
            allowsAuto = true;
        }
        else
        {
            return CssPropertyNormalizationResult.UnknownProperty;
        }

        if (value == null || value.Length > MaxDeclarationValueLength || value.IndexOf('\0') >= 0)
        {
            return CssPropertyNormalizationResult.Invalid;
        }

        var trimmed = value.Trim();
        if (trimmed.Length == 0)
        {
            return CssPropertyNormalizationResult.Valid;
        }

        if (IsCssWideKeyword(trimmed) || (allowsAuto && trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase)))
        {
            serialized = trimmed.ToLowerInvariant();
            return CssPropertyNormalizationResult.Valid;
        }

        var parser = new LengthPercentageParser(trimmed);
        if (!parser.TryParse(out var isLiteral, out var literalValue) ||
            (!allowsNegative && isLiteral && literalValue < 0))
        {
            return CssPropertyNormalizationResult.Invalid;
        }

        serialized = isLiteral && literalValue == 0 && parser.WasUnitlessZero
            ? "0px"
            : trimmed;
        return CssPropertyNormalizationResult.Valid;
    }

    private static bool IsCssWideKeyword(string value) =>
        value.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("revert", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("revert-layer", StringComparison.OrdinalIgnoreCase);

    private sealed class LengthPercentageParser
    {
        private const int MaxNestingDepth = 64;
        private const int MaxTokens = 8 * 1024;

        private readonly CssTokenizer _tokenizer;
        private CssToken _current;
        private int _nestingDepth;
        private int _tokensConsumed;
        private bool _limitExceeded;

        public LengthPercentageParser(string value)
        {
            _tokenizer = new CssTokenizer(value);
            MoveNext();
        }

        public bool WasUnitlessZero { get; private set; }

        public bool TryParse(out bool isLiteral, out double literalValue)
        {
            isLiteral = false;
            literalValue = 0;

            if (_current.Type == CssTokenType.Number && _current.NumericValue == 0)
            {
                WasUnitlessZero = true;
                isLiteral = true;
                MoveNext();
            }
            else if (_current.Type == CssTokenType.Percentage)
            {
                isLiteral = true;
                literalValue = _current.NumericValue;
                MoveNext();
            }
            else if (_current.Type == CssTokenType.Dimension && IsLengthUnit(_current.Unit))
            {
                isLiteral = true;
                literalValue = _current.NumericValue;
                MoveNext();
            }
            else if (_current.Type == CssTokenType.Function && IsMathFunction(_current.Value))
            {
                if (!ParseMathFunction(out var kind) || kind == MathValueKind.Number)
                {
                    return false;
                }
            }
            else if (_current.Type == CssTokenType.Function && IsDeferredSubstitutionFunction(_current.Value))
            {
                if (!ConsumeBalancedFunction())
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            return !_limitExceeded && _current.Type == CssTokenType.EOF;
        }

        private bool ParseMathFunction(out MathValueKind kind)
        {
            kind = MathValueKind.Invalid;
            if (_nestingDepth >= MaxNestingDepth)
            {
                return false;
            }

            _nestingDepth++;
            try
            {
                return ParseMathFunctionCore(out kind);
            }
            finally
            {
                _nestingDepth--;
            }
        }

        private bool ParseMathFunctionCore(out MathValueKind kind)
        {
            kind = MathValueKind.Invalid;
            var function = _current.Value;
            MoveNext();

            int requiredArguments = function.Equals("clamp", StringComparison.OrdinalIgnoreCase) ? 3 : 1;
            int arguments = 0;
            var resultKind = MathValueKind.Invalid;
            do
            {
                if (!ParseSum(out var argumentKind))
                {
                    return false;
                }

                resultKind = arguments == 0
                    ? argumentKind
                    : MergeAdditiveKinds(resultKind, argumentKind);
                if (resultKind == MathValueKind.Invalid)
                {
                    return false;
                }

                arguments++;
                if (_current.Type != CssTokenType.Comma)
                {
                    break;
                }

                MoveNext();
            }
            while (true);

            if (_current.Type != CssTokenType.RightParen)
            {
                return false;
            }

            MoveNext();
            bool validCount = function.Equals("calc", StringComparison.OrdinalIgnoreCase)
                ? arguments == 1
                : function.Equals("clamp", StringComparison.OrdinalIgnoreCase)
                    ? arguments == requiredArguments
                    : arguments >= requiredArguments;
            kind = validCount ? resultKind : MathValueKind.Invalid;
            return validCount;
        }

        private bool ParseSum(out MathValueKind kind)
        {
            kind = MathValueKind.Invalid;
            if (!ParseProduct(out kind))
            {
                return false;
            }

            while (_current.Type == CssTokenType.Delim && (_current.Delimiter == '+' || _current.Delimiter == '-'))
            {
                MoveNext();
                if (!ParseProduct(out var right))
                {
                    return false;
                }

                kind = MergeAdditiveKinds(kind, right);
                if (kind == MathValueKind.Invalid)
                {
                    return false;
                }
            }

            return true;
        }

        private bool ParseProduct(out MathValueKind kind)
        {
            kind = MathValueKind.Invalid;
            if (!ParseTerm(out kind))
            {
                return false;
            }

            while (_current.Type == CssTokenType.Delim && (_current.Delimiter == '*' || _current.Delimiter == '/'))
            {
                var operation = _current.Delimiter;
                MoveNext();
                if (!ParseTerm(out var right))
                {
                    return false;
                }

                kind = MergeMultiplicativeKinds(kind, right, operation);
                if (kind == MathValueKind.Invalid)
                {
                    return false;
                }
            }

            return true;
        }

        private bool ParseTerm(out MathValueKind kind)
        {
            kind = MathValueKind.Invalid;
            if ((_current.Type == CssTokenType.Dimension && IsLengthUnit(_current.Unit)) ||
                _current.Type == CssTokenType.Percentage)
            {
                kind = MathValueKind.LengthPercentage;
                MoveNext();
                return true;
            }

            if (_current.Type == CssTokenType.Number)
            {
                kind = MathValueKind.Number;
                MoveNext();
                return true;
            }

            if (_current.Type == CssTokenType.Function && IsMathFunction(_current.Value))
            {
                return ParseMathFunction(out kind);
            }

            if (_current.Type == CssTokenType.Function && IsDeferredSubstitutionFunction(_current.Value))
            {
                kind = MathValueKind.Deferred;
                return ConsumeBalancedFunction();
            }

            if (_current.Type == CssTokenType.LeftParen)
            {
                return ParseParenthesizedSum(out kind);
            }

            return false;
        }

        private bool ParseParenthesizedSum(out MathValueKind kind)
        {
            kind = MathValueKind.Invalid;
            if (_nestingDepth >= MaxNestingDepth)
            {
                return false;
            }

            _nestingDepth++;
            try
            {
                MoveNext();
                if (!ParseSum(out kind) || _current.Type != CssTokenType.RightParen)
                {
                    return false;
                }

                MoveNext();
                return true;
            }
            finally
            {
                _nestingDepth--;
            }
        }

        private bool ConsumeBalancedFunction()
        {
            int depth = 1;
            MoveNext();
            while (_current.Type != CssTokenType.EOF)
            {
                if (_current.Type == CssTokenType.Function || _current.Type == CssTokenType.LeftParen)
                {
                    if (++depth > MaxNestingDepth)
                    {
                        return false;
                    }
                }
                else if (_current.Type == CssTokenType.RightParen && --depth == 0)
                {
                    MoveNext();
                    return true;
                }

                MoveNext();
            }

            return false;
        }

        private void MoveNext()
        {
            do
            {
                if (++_tokensConsumed > MaxTokens)
                {
                    _limitExceeded = true;
                    _current = new CssToken(CssTokenType.EOF);
                    return;
                }

                _current = _tokenizer.Consume();
            }
            while (_current.Type == CssTokenType.Whitespace || _current.Type == CssTokenType.Comment);
        }

        private static bool IsMathFunction(string value) =>
            value.Equals("calc", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("min", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("max", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("clamp", StringComparison.OrdinalIgnoreCase);

        private static bool IsDeferredSubstitutionFunction(string value) =>
            value.Equals("var", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("env", StringComparison.OrdinalIgnoreCase);

        private static bool IsLengthUnit(string unit) =>
            CssValueParser.ParseUnit(unit) != CssUnit.None;

        private static MathValueKind MergeAdditiveKinds(MathValueKind left, MathValueKind right)
        {
            if (left == MathValueKind.Deferred || right == MathValueKind.Deferred)
            {
                return MathValueKind.Deferred;
            }

            return left == right ? left : MathValueKind.Invalid;
        }

        private static MathValueKind MergeMultiplicativeKinds(
            MathValueKind left,
            MathValueKind right,
            char operation)
        {
            if (left == MathValueKind.Deferred || right == MathValueKind.Deferred)
            {
                return MathValueKind.Deferred;
            }

            if (operation == '/')
            {
                return right == MathValueKind.Number ? left : MathValueKind.Invalid;
            }

            if (left == MathValueKind.Number)
            {
                return right;
            }

            return right == MathValueKind.Number ? left : MathValueKind.Invalid;
        }

        private enum MathValueKind
        {
            Invalid,
            Number,
            LengthPercentage,
            Deferred
        }
    }
}
