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
            return TryEvaluate(
                value, percentReference, fontSize, rootFontSize,
                float.NaN, float.NaN, out result);
        }

        /// <summary>
        /// Evaluates a &lt;length-percentage&gt; with an explicit viewport context.
        /// Viewport-unit dimensions resolve against the supplied nearest-SVG-viewport
        /// dimensions using the same float operation order as the attribute percent
        /// path, keeping CSS and attribute results bit-identical. Without a context
        /// (NaN dimensions) viewport units are rejected.
        /// </summary>
        public static bool TryEvaluate(
            string value,
            float percentReference,
            float fontSize,
            float rootFontSize,
            float viewportWidth,
            float viewportHeight,
            out float result)
        {
            result = 0f;
            if (string.IsNullOrWhiteSpace(value) || value.Length > SvgMarkupParser.MaxAttributeValueChars)
                return false;
            var parser = new Parser(
                value, percentReference, fontSize, rootFontSize, viewportWidth, viewportHeight);
            if (!parser.TryParseExpression(0, out Numeric numeric) ||
                parser.Read().Type != CssTokenType.EOF ||
                (numeric.Kind == NumericKind.Number && numeric.Value != 0d) ||
                !double.IsFinite(numeric.Value))
                return false;
            result = SvgValues.ClampCoord((float)numeric.Value);
            return float.IsFinite(result);
        }

        /// <summary>
        /// Evaluates calc-size(&lt;base&gt;, &lt;calc-sum&gt;) where the base is a sizing
        /// keyword or length-percentage and the identifier 'size' inside the sum
        /// substitutes the resolved base length. Bounded by the same depth and
        /// operation budgets as every other evaluation entry point.
        /// </summary>
        public static bool TryEvaluateCalcSize(
            string value,
            float fillAvailable,
            float fontSize,
            float rootFontSize,
            float viewportWidth,
            float viewportHeight,
            out float result)
        {
            result = 0f;
            if (string.IsNullOrWhiteSpace(value) || value.Length > SvgMarkupParser.MaxAttributeValueChars)
                return false;
            var parser = new Parser(
                value, fillAvailable, fontSize, rootFontSize, viewportWidth, viewportHeight,
                fillAvailable);
            if (!parser.TryParseCalcSize(0, out Numeric numeric) ||
                parser.Read().Type != CssTokenType.EOF ||
                !double.IsFinite(numeric.Value))
                return false;
            result = SvgValues.ClampCoord((float)numeric.Value);
            return float.IsFinite(result);
        }

        internal static bool IsNestedSvgSizingKeyword(string value) =>
            value.Equals("stretch", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("fit-content", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("min-content", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("max-content", StringComparison.OrdinalIgnoreCase);

        private static bool IsCalcSizeBaseKeyword(string value) =>
            IsNestedSvgSizingKeyword(value) ||
            value.Equals("auto", StringComparison.OrdinalIgnoreCase);

        /// <summary>True when any dimension token carries a viewport-relative unit.</summary>
        internal static bool HasViewportUnitDimension(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > SvgMarkupParser.MaxAttributeValueChars)
                return false;
            var tokenizer = new CssTokenizer(value);
            int scanned = 0;
            CssToken token;
            do
            {
                token = tokenizer.Consume();
                if (++scanned > MaxOperations * 4) return false;
                if (token.Type == CssTokenType.Dimension && IsViewportUnit(token.Unit)) return true;
            }
            while (token.Type != CssTokenType.EOF);
            return false;
        }

        private static bool IsViewportUnit(string unit)
        {
            return unit.Length switch
            {
                2 => Eq(unit, "vw") || Eq(unit, "vh") || Eq(unit, "vi") || Eq(unit, "vb"),
                3 => Eq(unit, "cqw") || Eq(unit, "cqh") || Eq(unit, "cqi") || Eq(unit, "cqb"),
                4 => Eq(unit, "vmin") || Eq(unit, "vmax"),
                5 => Eq(unit, "cqmin") || Eq(unit, "cqmax"),
                _ => false
            };

            static bool Eq(string raw, string canonical) =>
                string.Equals(raw, canonical, StringComparison.OrdinalIgnoreCase);
        }

        private enum NumericKind { Number, Length }

        private readonly record struct Numeric(double Value, NumericKind Kind);

        private sealed class Parser
        {
            private readonly CssTokenizer _tokens;
            private readonly float _percentReference;
            private readonly float _fontSize;
            private readonly float _rootFontSize;
            private readonly float _viewportWidth;
            private readonly float _viewportHeight;
            private readonly double _fillAvailable;
            private CssToken _lookahead;
            private bool _hasLookahead;
            private int _operations;
            private double _sizeBase = double.NaN;

            public Parser(
                string value,
                float percentReference,
                float fontSize,
                float rootFontSize,
                float viewportWidth,
                float viewportHeight,
                double fillAvailable = double.NaN)
            {
                _tokens = new CssTokenizer(value);
                _percentReference = percentReference;
                _fontSize = fontSize;
                _rootFontSize = rootFontSize;
                _viewportWidth = viewportWidth;
                _viewportHeight = viewportHeight;
                _fillAvailable = fillAvailable;
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
                    case CssTokenType.Ident
                        when !double.IsNaN(_sizeBase) &&
                             token.Value.Equals("size", StringComparison.OrdinalIgnoreCase):
                        result = new Numeric(_sizeBase, NumericKind.Length);
                        return true;
                    case CssTokenType.LeftParen:
                        return TryParseParenthesized(depth + 1, out result);
                    case CssTokenType.Function:
                        return TryParseFunction(token.Value, depth + 1, out result);
                    default:
                        return false;
                }
            }

            /// <summary>Parses calc-size(base, sum) with 'size' substitution active
            /// only inside the second argument. The base is a sizing keyword or any
            /// bounded length-percentage expression.</summary>
            internal bool TryParseCalcSize(int depth, out Numeric result)
            {
                result = default;
                if (depth > MaxDepth) return false;
                CssToken head = Read();
                if (head.Type != CssTokenType.Function ||
                    !head.Value.Equals("calc-size", StringComparison.OrdinalIgnoreCase))
                    return false;

                double sizeBase;
                CssToken baseToken = Peek();
                if (baseToken.Type == CssTokenType.Ident && IsCalcSizeBaseKeyword(baseToken.Value))
                {
                    Read();
                    sizeBase = _fillAvailable;
                }
                else
                {
                    if (!TryParseValue(depth + 1, out Numeric baseValue) ||
                        baseValue.Kind != NumericKind.Length ||
                        !double.IsFinite(baseValue.Value))
                        return false;
                    sizeBase = baseValue.Value;
                }

                if (Read().Type != CssTokenType.Comma) return false;

                _sizeBase = sizeBase;
                bool ok = TryParseExpression(depth + 1, out result) &&
                          Read().Type == CssTokenType.RightParen;
                _sizeBase = double.NaN;
                return ok && double.IsFinite(result.Value);
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
                if (!double.IsFinite(scale))
                    return TryResolveViewportUnit(value, unit, out result);
                result = new Numeric(value * scale, NumericKind.Length);
                return double.IsFinite(result.Value);
            }

            /// <summary>
            /// Viewport-relative units resolve against the explicit nearest-SVG-viewport
            /// dimensions. Container units fall back to the small viewport (no query
            /// container exists in this engine); vi/vb follow the horizontal-tb inline
            /// and block axes. The float multiply order matches the attribute percent
            /// path so equal declared values produce bit-identical geometry.
            /// </summary>
            private bool TryResolveViewportUnit(double value, string unit, out Numeric result)
            {
                result = default;
                if (!IsViewportUnit(unit)) return false;
                if (float.IsNaN(_viewportWidth) || float.IsNaN(_viewportHeight)) return false;
                float scalar;
                switch (unit.Length)
                {
                    case 2:
                        char axis = Lower(unit[1]);
                        bool inlineAxis = axis == 'w' || axis == 'i';
                        bool extreme = axis == 'n' || axis == 'x';
                        scalar = !extreme
                            ? (inlineAxis ? _viewportWidth : _viewportHeight)
                            : axis == 'n'
                                ? MathF.Min(_viewportWidth, _viewportHeight)
                                : MathF.Max(_viewportWidth, _viewportHeight);
                        break;
                    case 3:
                        char containerAxis = Lower(unit[2]);
                        scalar = containerAxis == 'w' || containerAxis == 'i'
                            ? _viewportWidth
                            : _viewportHeight;
                        break;
                    case 4:
                        scalar = Lower(unit[3]) == 'n'
                            ? MathF.Min(_viewportWidth, _viewportHeight)
                            : MathF.Max(_viewportWidth, _viewportHeight);
                        break;
                    case 5:
                        scalar = Lower(unit[4]) == 'n'
                            ? MathF.Min(_viewportWidth, _viewportHeight)
                            : MathF.Max(_viewportWidth, _viewportHeight);
                        break;
                    default:
                        return false;
                }
                if (!float.IsFinite(scalar)) return false;
                float scaled = (float)value * 0.01f * scalar;
                if (!float.IsFinite(scaled)) return false;
                result = new Numeric((double)scaled, NumericKind.Length);
                return true;
            }

            private static char Lower(char c) => (char)(c | 0x20);

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
