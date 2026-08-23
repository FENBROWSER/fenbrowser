using System;
using FenBrowser.FenEngine.Rendering.Css;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// Stateless, bounded CSS math evaluator for SVG geometry
    /// &lt;length-percentage&gt; values. All context is explicit; no document,
    /// media-query, viewport, or process-global CSS state is consulted.
    /// </summary>
    internal static class SvgCssLengthEvaluator
    {
        private const int MaxDepth = 16;
        private const int MaxOperations = 128;

        public static bool TryEvaluate(
            string value,
            float percentReference,
            float fontSize,
            float rootFontSize,
            out float result)
        {
            result = 0f;
            if (string.IsNullOrWhiteSpace(value) || value.Length > SvgMarkupParser.MaxAttributeValueChars)
                return false;
            var parser = new Parser(value, percentReference, fontSize, rootFontSize);
            if (!parser.TryParseExpression(0, out Numeric numeric) ||
                parser.Read().Type != CssTokenType.EOF ||
                (numeric.Kind == NumericKind.Number && numeric.Value != 0d) ||
                !double.IsFinite(numeric.Value))
                return false;
            result = SvgValues.ClampCoord((float)numeric.Value);
            return float.IsFinite(result);
        }

        private enum NumericKind { Number, Length }

        private readonly record struct Numeric(double Value, NumericKind Kind);

        private sealed class Parser
        {
            private readonly CssTokenizer _tokens;
            private readonly float _percentReference;
            private readonly float _fontSize;
            private readonly float _rootFontSize;
            private CssToken _lookahead;
            private bool _hasLookahead;
            private int _operations;

            public Parser(string value, float percentReference, float fontSize, float rootFontSize)
            {
                _tokens = new CssTokenizer(value);
                _percentReference = percentReference;
                _fontSize = fontSize;
                _rootFontSize = rootFontSize;
            }

            public bool TryParseExpression(int depth, out Numeric result)
            {
                result = default;
                if (depth > MaxDepth || !TryParseProduct(depth, out result)) return false;
                while (true)
                {
                    CssToken token = Peek();
                    if (token.Type != CssTokenType.Delim || token.Delimiter is not ('+' or '-'))
                        return true;
                    Read();
                    if (++_operations > MaxOperations || !TryParseProduct(depth, out Numeric right) ||
                        result.Kind != right.Kind)
                        return false;
                    result = new Numeric(
                        token.Delimiter == '+' ? result.Value + right.Value : result.Value - right.Value,
                        result.Kind);
                }
            }

            private bool TryParseProduct(int depth, out Numeric result)
            {
                if (!TryParseValue(depth, out result)) return false;
                while (true)
                {
                    CssToken token = Peek();
                    if (token.Type != CssTokenType.Delim || token.Delimiter is not ('*' or '/'))
                        return true;
                    Read();
                    if (++_operations > MaxOperations || !TryParseValue(depth, out Numeric right))
                        return false;
                    if (token.Delimiter == '*')
                    {
                        if (result.Kind == NumericKind.Length && right.Kind == NumericKind.Length) return false;
                        NumericKind kind = result.Kind == NumericKind.Length || right.Kind == NumericKind.Length
                            ? NumericKind.Length
                            : NumericKind.Number;
                        result = new Numeric(result.Value * right.Value, kind);
                    }
                    else
                    {
                        if (right.Kind != NumericKind.Number || right.Value == 0d) return false;
                        result = new Numeric(result.Value / right.Value, result.Kind);
                    }
                    if (!double.IsFinite(result.Value)) return false;
                }
            }

            private bool TryParseValue(int depth, out Numeric result)
            {
                result = default;
                CssToken token = Read();
                switch (token.Type)
                {
                    case CssTokenType.Number:
                        result = new Numeric(token.NumericValue, NumericKind.Number);
                        return double.IsFinite(result.Value);
                    case CssTokenType.Percentage:
                        result = new Numeric(
                            token.NumericValue * _percentReference / 100d,
                            NumericKind.Length);
                        return double.IsFinite(result.Value);
                    case CssTokenType.Dimension:
                        return TryResolveDimension(token.NumericValue, token.Unit, out result);
                    case CssTokenType.LeftParen:
                        return TryParseParenthesized(depth + 1, out result);
                    case CssTokenType.Function:
                        return TryParseFunction(token.Value, depth + 1, out result);
                    default:
                        return false;
                }
            }

            private bool TryParseParenthesized(int depth, out Numeric result)
            {
                result = default;
                return depth <= MaxDepth && TryParseExpression(depth, out result) &&
                       Read().Type == CssTokenType.RightParen;
            }

            private bool TryParseFunction(string name, int depth, out Numeric result)
            {
                result = default;
                if (depth > MaxDepth || string.IsNullOrEmpty(name)) return false;
                if (name.Equals("calc", StringComparison.OrdinalIgnoreCase))
                    return TryParseExpression(depth, out result) && Read().Type == CssTokenType.RightParen;
                if (name.Equals("min", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("max", StringComparison.OrdinalIgnoreCase))
                    return TryParseMinMax(name[1] == 'i' || name[1] == 'I', depth, out result);
                if (name.Equals("clamp", StringComparison.OrdinalIgnoreCase))
                    return TryParseClamp(depth, out result);
                return false;
            }

            private bool TryParseMinMax(bool minimum, int depth, out Numeric result)
            {
                result = default;
                if (!TryParseExpression(depth, out result)) return false;
                int arguments = 1;
                while (Peek().Type == CssTokenType.Comma)
                {
                    Read();
                    if (++arguments > MaxOperations || !TryParseExpression(depth, out Numeric candidate) ||
                        candidate.Kind != result.Kind)
                        return false;
                    result = minimum
                        ? (candidate.Value < result.Value ? candidate : result)
                        : (candidate.Value > result.Value ? candidate : result);
                }
                return Read().Type == CssTokenType.RightParen;
            }

            private bool TryParseClamp(int depth, out Numeric result)
            {
                result = default;
                if (!TryParseExpression(depth, out Numeric minimum) || Read().Type != CssTokenType.Comma ||
                    !TryParseExpression(depth, out Numeric preferred) || Read().Type != CssTokenType.Comma ||
                    !TryParseExpression(depth, out Numeric maximum) || Read().Type != CssTokenType.RightParen ||
                    minimum.Kind != preferred.Kind || preferred.Kind != maximum.Kind)
                    return false;
                result = new Numeric(
                    Math.Max(minimum.Value, Math.Min(preferred.Value, maximum.Value)),
                    preferred.Kind);
                return true;
            }

            private bool TryResolveDimension(double value, string unit, out Numeric result)
            {
                result = default;
                if (!double.IsFinite(value) || string.IsNullOrWhiteSpace(unit)) return false;
                double scale = unit.ToLowerInvariant() switch
                {
                    "px" => 1d,
                    "pt" => 96d / 72d,
                    "pc" => 16d,
                    "mm" => 96d / 25.4d,
                    "cm" => 96d / 2.54d,
                    "in" => 96d,
                    "q" => 96d / 101.6d,
                    "em" => _fontSize,
                    "ex" or "ch" => _fontSize * 0.5d,
                    "rem" => _rootFontSize,
                    _ => double.NaN
                };
                if (!double.IsFinite(scale)) return false;
                result = new Numeric(value * scale, NumericKind.Length);
                return double.IsFinite(result.Value);
            }

            public CssToken Read()
            {
                if (_hasLookahead)
                {
                    _hasLookahead = false;
                    return _lookahead;
                }
                CssToken token;
                do { token = _tokens.Consume(); } while (token.Type == CssTokenType.Whitespace);
                return token;
            }

            private CssToken Peek()
            {
                if (!_hasLookahead)
                {
                    _lookahead = Read();
                    _hasLookahead = true;
                }
                return _lookahead;
            }
        }
    }
}
