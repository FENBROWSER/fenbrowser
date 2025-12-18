using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
// using Avalonia; // Removed
// using FenBrowser.Core.Math;
using FenBrowser.Core; // Namespace moved to Core

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// CSS value parser handling calc(), shorthand expansion, and property value parsing.
    /// </summary>
    public static class CssValueParser
    {
        #region Length Parsing

        /// <summary>
        /// Parse a CSS length value (px, em, rem, %, vh, vw, etc.)
        /// Returns value in pixels, or null if not a valid length.
        /// </summary>
        public static double? ParseLength(string value, double parentFontSize = 16, double viewportWidth = 1920, double viewportHeight = 1080, double rootFontSize = 16)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim().ToLowerInvariant();

            // Handle calc() expressions
            if (value.StartsWith("calc(") && value.EndsWith(")"))
            {
                return EvaluateCalc(value.Substring(5, value.Length - 6), parentFontSize, viewportWidth, viewportHeight, rootFontSize);
            }

            // Auto/inherit - return null (handled by caller)
            if (value == "auto" || value == "inherit" || value == "initial" || value == "unset") return null;

            // Zero
            if (value == "0") return 0;

            // Parse number and unit
            var match = Regex.Match(value, @"^(-?[\d.]+)(px|em|rem|%|vh|vw|vmin|vmax|pt|cm|mm|in|pc|ch|ex)?$");
            if (!match.Success) return null;

            if (!double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                return null;

            string unit = match.Groups[2].Value;

            return unit switch
            {
                "" or "px" => num,
                "em" => num * parentFontSize,
                "rem" => num * rootFontSize,
                "%" => num, // Percentage returned as-is, caller handles context
                "vh" => (num / 100) * viewportHeight,
                "vw" => (num / 100) * viewportWidth,
                "vmin" => (num / 100) * Math.Min(viewportWidth, viewportHeight),
                "vmax" => (num / 100) * Math.Max(viewportWidth, viewportHeight),
                "pt" => num * (96.0 / 72.0), // 1pt = 1/72 inch, 96 DPI
                "cm" => num * (96.0 / 2.54),
                "mm" => num * (96.0 / 25.4),
                "in" => num * 96.0,
                "pc" => num * 16, // 1pc = 12pt = 16px
                "ch" => num * (parentFontSize * 0.5), // Approximate
                "ex" => num * (parentFontSize * 0.5), // Approximate
                _ => num
            };
        }

        /// <summary>
        /// Check if a value is a percentage.
        /// </summary>
        public static bool IsPercentage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            return value.Trim().EndsWith("%");
        }

        /// <summary>
        /// Extract percentage value (without the % sign).
        /// </summary>
        public static double? ParsePercentage(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            if (!value.EndsWith("%")) return null;
            
            if (double.TryParse(value.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
                return result;
            return null;
        }

        #endregion

        #region Calc Expression Evaluation

        /// <summary>
        /// Evaluate a calc() expression, returning the result in pixels.
        /// </summary>
        public static double? EvaluateCalc(string expr, double parentFontSize, double viewportWidth, double viewportHeight, double rootFontSize)
        {
            try
            {
                // Normalize expression
                expr = expr.Trim();
                
                // Simple implementation: replace length values with pixels, then evaluate
                var tokens = TokenizeCalc(expr);
                if (tokens == null) return null;

                // Convert all values to pixels
                var pixelTokens = new List<string>();
                foreach (var token in tokens)
                {
                    if (IsOperator(token))
                    {
                        pixelTokens.Add(token);
                    }
                    else
                    {
                        var px = ParseLength(token, parentFontSize, viewportWidth, viewportHeight, rootFontSize);
                        if (px == null) return null;
                        pixelTokens.Add(px.Value.ToString(CultureInfo.InvariantCulture));
                    }
                }

                // Simple expression evaluation (handles + - * /)
                return EvaluateSimpleExpression(pixelTokens);
            }
            catch
            {
                return null;
            }
        }

        private static List<string> TokenizeCalc(string expr)
        {
            var tokens = new List<string>();
            int i = 0;
            
            while (i < expr.Length)
            {
                char c = expr[i];
                
                if (char.IsWhiteSpace(c)) { i++; continue; }
                
                if (c == '+' || c == '*' || c == '/' || c == '(' || c == ')')
                {
                    tokens.Add(c.ToString());
                    i++;
                    continue;
                }
                
                // Handle minus (could be operator or negative number)
                if (c == '-')
                {
                    if (tokens.Count == 0 || IsOperator(tokens[tokens.Count - 1]) || tokens[tokens.Count - 1] == "(")
                    {
                        // Part of a number
                        var sb = new System.Text.StringBuilder();
                        sb.Append(c);
                        i++;
                        while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '.' || expr[i] == '%'))
                        {
                            sb.Append(expr[i]);
                            i++;
                        }
                        tokens.Add(sb.ToString());
                    }
                    else
                    {
                        tokens.Add("-");
                        i++;
                    }
                    continue;
                }
                
                // Number or length
                if (char.IsDigit(c) || c == '.')
                {
                    var sb = new System.Text.StringBuilder();
                    while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '.' || expr[i] == '%'))
                    {
                        sb.Append(expr[i]);
                        i++;
                    }
                    tokens.Add(sb.ToString());
                    continue;
                }
                
                i++;
            }
            
            return tokens;
        }

        private static bool IsOperator(string s) => s == "+" || s == "-" || s == "*" || s == "/" || s == "(" || s == ")";

        private static double? EvaluateSimpleExpression(List<string> tokens)
        {
            // Simple shunting-yard implementation
            var output = new Stack<double>();
            var operators = new Stack<string>();

            int Precedence(string op) => op == "+" || op == "-" ? 1 : op == "*" || op == "/" ? 2 : 0;

            void ApplyOp()
            {
                if (output.Count < 2 || operators.Count == 0) return;
                double b = output.Pop();
                double a = output.Pop();
                string op = operators.Pop();
                double result = op switch
                {
                    "+" => a + b,
                    "-" => a - b,
                    "*" => a * b,
                    "/" => b != 0 ? a / b : 0,
                    _ => 0
                };
                output.Push(result);
            }

            foreach (var token in tokens)
            {
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
                {
                    output.Push(num);
                }
                else if (token == "(")
                {
                    operators.Push(token);
                }
                else if (token == ")")
                {
                    while (operators.Count > 0 && operators.Peek() != "(") ApplyOp();
                    if (operators.Count > 0) operators.Pop();
                }
                else if (IsOperator(token))
                {
                    while (operators.Count > 0 && operators.Peek() != "(" && Precedence(operators.Peek()) >= Precedence(token))
                        ApplyOp();
                    operators.Push(token);
                }
            }

            while (operators.Count > 0) ApplyOp();

            return output.Count == 1 ? output.Pop() : null;
        }

        #endregion

        #region Shorthand Expansion

        /// <summary>
        /// Expand margin/padding shorthand into individual values.
        /// </summary>
        public static Thickness ParseBoxShorthand(string value, double parentFontSize = 16)
        {
            if (string.IsNullOrWhiteSpace(value)) return new Thickness(0);
            
            var parts = SplitValues(value);
            double[] vals = new double[4];
            
            for (int i = 0; i < Math.Min(parts.Length, 4); i++)
            {
                vals[i] = ParseLength(parts[i], parentFontSize) ?? 0;
            }

            return parts.Length switch
            {
                1 => new Thickness(vals[0]),
                2 => new Thickness(vals[1], vals[0], vals[1], vals[0]), // H V
                3 => new Thickness(vals[1], vals[0], vals[1], vals[2]), // T H B
                _ => new Thickness(vals[3], vals[0], vals[1], vals[2])  // T R B L
            };
        }

        /// <summary>
        /// Expand flex shorthand (flex-grow, flex-shrink, flex-basis).
        /// </summary>
        /// <returns>Tuple of (grow, shrink, basis)</returns>
        public static (double grow, double shrink, string basis) ParseFlexShorthand(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return (0, 1, "auto");
            value = value.Trim().ToLowerInvariant();

            // Common keywords
            if (value == "auto") return (1, 1, "auto");
            if (value == "none") return (0, 0, "auto");
            if (value == "initial") return (0, 1, "auto");

            var parts = SplitValues(value);

            // Single value
            if (parts.Length == 1)
            {
                // If it's a unitless number, it's flex-grow
                if (double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double grow))
                {
                    return (grow, 1, "0%");
                }
                // Otherwise it's flex-basis
                return (1, 1, parts[0]);
            }

            // Two values
            if (parts.Length == 2)
            {
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double g);
                if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
                {
                    return (g, s, "0%");
                }
                return (g, 1, parts[1]);
            }

            // Three values
            if (parts.Length >= 3)
            {
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double g);
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s);
                return (g, s, parts[2]);
            }

            return (0, 1, "auto");
        }

        /// <summary>
        /// Parse border shorthand into width, style, color.
        /// </summary>
        public static (double width, string style, string color) ParseBorderShorthand(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return (0, "none", "currentColor");

            var parts = SplitValues(value);
            double width = 0;
            string style = "none";
            string color = "currentColor";

            foreach (var part in parts)
            {
                var p = part.ToLowerInvariant();
                
                // Check if it's a border style
                if (IsBorderStyle(p))
                {
                    style = p;
                }
                // Check if it's a length
                else if (ParseLength(p) is double w)
                {
                    width = w;
                }
                // Otherwise assume it's a color
                else
                {
                    color = part;
                }
            }

            return (width, style, color);
        }

        private static bool IsBorderStyle(string s) =>
            s == "none" || s == "hidden" || s == "dotted" || s == "dashed" ||
            s == "solid" || s == "double" || s == "groove" || s == "ridge" ||
            s == "inset" || s == "outset";

        #endregion

        #region Utility Methods

        /// <summary>
        /// Split CSS value into parts, respecting parentheses and quotes.
        /// </summary>
        public static string[] SplitValues(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();

            var parts = new List<string>();
            var current = new System.Text.StringBuilder();
            int parenDepth = 0;
            bool inString = false;
            char stringChar = '\0';

            foreach (char c in value)
            {
                if (inString)
                {
                    current.Append(c);
                    if (c == stringChar) inString = false;
                }
                else if (c == '"' || c == '\'')
                {
                    inString = true;
                    stringChar = c;
                    current.Append(c);
                }
                else if (c == '(')
                {
                    parenDepth++;
                    current.Append(c);
                }
                else if (c == ')')
                {
                    parenDepth--;
                    current.Append(c);
                }
                else if (char.IsWhiteSpace(c) && parenDepth == 0)
                {
                    if (current.Length > 0)
                    {
                        parts.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(c);
                }
            }

            if (current.Length > 0) parts.Add(current.ToString());
            return parts.ToArray();
        }

        #endregion
    }
}
