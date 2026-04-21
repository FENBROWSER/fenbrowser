// CssFunctions.cs - CSS Math Functions (calc, min, max, clamp, etc.)
// Extracted from CssLoader.cs for modularity
// Part of CssLoader partial class

using System;
using System.Collections.Generic;
using System.Text;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Css;

namespace FenBrowser.FenEngine.Rendering
{
    public static partial class CssLoader
    {
        #region CSS Math Functions

        /// <summary>
        /// Parse CSS calc() expressions like calc(100% - 40px), calc(100vh - 60px), etc.
        /// Supports: +, -, *, / operators and px, em, rem, %, vw, vh, vmin, vmax units
        /// </summary>
        private static bool TryParseCalc(string s, out double px, double emBase = 16.0, double percentBase = 0)
        {
            px = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            var sl = s.Trim().ToLowerInvariant();
            if (!sl.StartsWith("calc(") || !sl.EndsWith(")")) return false;
            
            var expr = s.Substring(5, s.Length - 6).Trim();
            if (string.IsNullOrWhiteSpace(expr)) return false;

            try
            {
                px = EvaluateCalcExpression(expr, emBase, percentBase);
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Parse CSS min() function: min(value1, value2, ...)
        /// Returns the smallest of the provided values
        /// </summary>
        private static bool TryParseMin(string s, out double px, double emBase = 16.0, double percentBase = 0)
        {
            px = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            var sl = s.Trim().ToLowerInvariant();
            if (!sl.StartsWith("min(") || !sl.EndsWith(")")) return false;
            
            var inner = s.Substring(4, s.Length - 5).Trim();
            if (string.IsNullOrWhiteSpace(inner)) return false;

            try
            {
                var args = SplitCssFunctionArgs(inner);
                if (args.Count == 0) return false;
                
                double minVal = double.MaxValue;
                foreach (var arg in args)
                {
                    double val = EvaluateCssValue(arg.Trim(), emBase, percentBase);
                    if (val < minVal) minVal = val;
                }
                px = minVal;
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Parse CSS max() function: max(value1, value2, ...)
        /// Returns the largest of the provided values
        /// </summary>
        private static bool TryParseMax(string s, out double px, double emBase = 16.0, double percentBase = 0)
        {
            px = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            var sl = s.Trim().ToLowerInvariant();
            if (!sl.StartsWith("max(") || !sl.EndsWith(")")) return false;
            
            var inner = s.Substring(4, s.Length - 5).Trim();
            if (string.IsNullOrWhiteSpace(inner)) return false;

            try
            {
                var args = SplitCssFunctionArgs(inner);
                if (args.Count == 0) return false;
                
                double maxVal = double.MinValue;
                foreach (var arg in args)
                {
                    double val = EvaluateCssValue(arg.Trim(), emBase, percentBase);
                    if (val > maxVal) maxVal = val;
                }
                px = maxVal;
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Parse CSS clamp() function: clamp(min, preferred, max)
        /// Clamps the preferred value between min and max
        /// </summary>
        private static bool TryParseClamp(string s, out double px, double emBase = 16.0, double percentBase = 0)
        {
            px = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;

            var sl = s.Trim().ToLowerInvariant();
            if (!sl.StartsWith("clamp(") || !sl.EndsWith(")")) return false;
            
            var inner = s.Substring(6, s.Length - 7).Trim();
            if (string.IsNullOrWhiteSpace(inner)) return false;

            try
            {
                var args = SplitCssFunctionArgs(inner);
                if (args.Count != 3) return false;
                
                double minVal = EvaluateCssValue(args[0].Trim(), emBase, percentBase);
                double preferred = EvaluateCssValue(args[1].Trim(), emBase, percentBase);
                double maxVal = EvaluateCssValue(args[2].Trim(), emBase, percentBase);
                
                px = Math.Max(minVal, Math.Min(preferred, maxVal));
                return true;
            }
            catch
            {
                return false;
            }
        }
        
        /// <summary>
        /// Split CSS function arguments, handling nested functions
        /// </summary>
        private static List<string> SplitCssFunctionArgs(string inner)
        {
            var args = new List<string>();
            var current = new StringBuilder();
            int depth = 0;
            
            foreach (char c in inner)
            {
                if (c == '(') depth++;
                else if (c == ')') depth--;
                
                if (c == ',' && depth == 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
            
            if (current.Length > 0)
                args.Add(current.ToString());
                
            return args;
        }
        
        /// <summary>
        /// Evaluate a CSS value that may contain functions like calc(), min(), max(), clamp(), env()
        /// </summary>
        private static double EvaluateCssValue(string value, double emBase = 16.0, double percentBase = 0)
        {
            if (string.IsNullOrWhiteSpace(value)) return 0;
            
            value = value.Trim();
            var lower = value.ToLowerInvariant();
            
            // Try CSS math functions
            if (lower.StartsWith("calc(") && TryParseCalc(value, out double calcVal, emBase, percentBase))
                return calcVal;
            if (lower.StartsWith("min(") && TryParseMin(value, out double minVal, emBase, percentBase))
                return minVal;
            if (lower.StartsWith("max(") && TryParseMax(value, out double maxVal, emBase, percentBase))
                return maxVal;
            if (lower.StartsWith("clamp(") && TryParseClamp(value, out double clampVal, emBase, percentBase))
                return clampVal;
            
            // env() function
            if (lower.StartsWith("env(") && TryParseEnv(value, out double envVal))
                return envVal;
            
            // var() CSS custom properties
            if (lower.StartsWith("var("))
            {
                var resolved = ResolveVarValue(value, emBase, percentBase);
                if (resolved != null && TryPx(resolved, out double varPx, emBase, percentBase))
                    return varPx;
                if (resolved != null && resolved != value)
                    return EvaluateCssValue(resolved, emBase, percentBase);
                return 0;
            }
            
            // CSS Values Level 4 math functions
            if (lower.StartsWith("abs(") && TryParseMathFunc(value, "abs", out double absVal, emBase, percentBase))
                return Math.Abs(absVal);
            if (lower.StartsWith("sign(") && TryParseMathFunc(value, "sign", out double signVal, emBase, percentBase))
                return Math.Sign(signVal);
            if (lower.StartsWith("round(") && TryParseRound(value, out double roundVal, emBase, percentBase))
                return roundVal;
            if (lower.StartsWith("mod(") && TryParseMod(value, out double modVal, emBase, percentBase))
                return modVal;
            if (lower.StartsWith("rem(") && TryParseRem(value, out double remVal, emBase, percentBase))
                return remVal;
            if (lower.StartsWith("pow(") && TryParsePow(value, out double powVal, emBase, percentBase))
                return powVal;
            if (lower.StartsWith("sqrt(") && TryParseMathFunc(value, "sqrt", out double sqrtVal, emBase, percentBase))
                return Math.Sqrt(sqrtVal);
            if (lower.StartsWith("log(") && TryParseMathFunc(value, "log", out double logVal, emBase, percentBase))
                return Math.Log(logVal);
            if (lower.StartsWith("exp(") && TryParseMathFunc(value, "exp", out double expVal, emBase, percentBase))
                return Math.Exp(expVal);
            
            // CSS Trigonometric functions
            if (lower.StartsWith("sin(") && TryParseMathFunc(value, "sin", out double sinVal, emBase, percentBase))
                return Math.Sin(sinVal * Math.PI / 180.0);
            if (lower.StartsWith("cos(") && TryParseMathFunc(value, "cos", out double cosVal, emBase, percentBase))
                return Math.Cos(cosVal * Math.PI / 180.0);
            if (lower.StartsWith("tan(") && TryParseMathFunc(value, "tan", out double tanVal, emBase, percentBase))
                return Math.Tan(tanVal * Math.PI / 180.0);
            if (lower.StartsWith("asin(") && TryParseMathFunc(value, "asin", out double asinVal, emBase, percentBase))
                return Math.Asin(asinVal) * 180.0 / Math.PI;
            if (lower.StartsWith("acos(") && TryParseMathFunc(value, "acos", out double acosVal, emBase, percentBase))
                return Math.Acos(acosVal) * 180.0 / Math.PI;
            if (lower.StartsWith("atan(") && TryParseMathFunc(value, "atan", out double atanVal, emBase, percentBase))
                return Math.Atan(atanVal) * 180.0 / Math.PI;
            if (lower.StartsWith("atan2(") && TryParseAtan2(value, out double atan2Val, emBase, percentBase))
                return atan2Val;
            if (lower.StartsWith("hypot(") && TryParseHypot(value, out double hypotVal, emBase, percentBase))
                return hypotVal;
            
            // Try simple value with units
            return ParseCalcValue(value, emBase, percentBase);
        }

        /// <summary>
        /// Parse a single-argument math function like abs(), sign(), sqrt(), etc.
        /// </summary>
        private static bool TryParseMathFunc(string value, string funcName, out double result, double emBase = 16.0, double percentBase = 0)
        {
            result = 0;
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            
            var inner = value.Substring(open + 1, close - open - 1).Trim();
            result = EvaluateCssValue(inner, emBase, percentBase);
            return true;
        }

        /// <summary>
        /// Parse round() function: round(strategy?, value, interval?)
        /// Strategies: nearest (default), up, down, to-zero
        /// </summary>
        private static bool TryParseRound(string value, out double result, double emBase = 16.0, double percentBase = 0)
        {
            result = 0;
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            
            var inner = value.Substring(open + 1, close - open - 1).Trim();
            var parts = SplitCssFunctionArgs(inner);
            
            if (parts.Count == 0) return false;
            
            string strategy = "nearest";
            double val, interval = 1;
            
            if (parts.Count >= 3)
            {
                strategy = parts[0].Trim().ToLowerInvariant();
                val = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
                interval = EvaluateCssValue(parts[2].Trim(), emBase, percentBase);
            }
            else if (parts.Count == 2)
            {
                val = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
                interval = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
            }
            else
            {
                val = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
            }
            
            if (interval == 0) interval = 1;
            
            switch (strategy)
            {
                case "up":
                    result = Math.Ceiling(val / interval) * interval;
                    break;
                case "down":
                    result = Math.Floor(val / interval) * interval;
                    break;
                case "to-zero":
                    result = Math.Truncate(val / interval) * interval;
                    break;
                default:
                    result = Math.Round(val / interval) * interval;
                    break;
            }
            return true;
        }

        /// <summary>
        /// Parse mod() function: mod(dividend, divisor) - like % but always positive
        /// </summary>
        private static bool TryParseMod(string value, out double result, double emBase = 16.0, double percentBase = 0)
        {
            result = 0;
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            
            var inner = value.Substring(open + 1, close - open - 1).Trim();
            var parts = SplitCssFunctionArgs(inner);
            
            if (parts.Count < 2) return false;
            
            double dividend = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
            double divisor = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
            
            if (divisor == 0) return false;
            
            result = dividend - divisor * Math.Floor(dividend / divisor);
            return true;
        }

        /// <summary>
        /// Parse rem() function: rem(dividend, divisor) - like % with sign of dividend
        /// </summary>
        private static bool TryParseRem(string value, out double result, double emBase = 16.0, double percentBase = 0)
        {
            result = 0;
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            
            var inner = value.Substring(open + 1, close - open - 1).Trim();
            var parts = SplitCssFunctionArgs(inner);
            
            if (parts.Count < 2) return false;
            
            double dividend = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
            double divisor = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
            
            if (divisor == 0) return false;
            
            result = dividend % divisor;
            return true;
        }

        /// <summary>
        /// Parse pow() function: pow(base, exponent)
        /// </summary>
        private static bool TryParsePow(string value, out double result, double emBase = 16.0, double percentBase = 0)
        {
            result = 0;
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            
            var inner = value.Substring(open + 1, close - open - 1).Trim();
            var parts = SplitCssFunctionArgs(inner);
            
            if (parts.Count < 2) return false;
            
            double baseVal = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
            double exponent = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
            
            result = Math.Pow(baseVal, exponent);
            return true;
        }

        /// <summary>
        /// Parse atan2() function: atan2(y, x) - returns angle in degrees
        /// </summary>
        private static bool TryParseAtan2(string value, out double result, double emBase = 16.0, double percentBase = 0)
        {
            result = 0;
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            
            var inner = value.Substring(open + 1, close - open - 1).Trim();
            var parts = SplitCssFunctionArgs(inner);
            
            if (parts.Count < 2) return false;
            
            double y = EvaluateCssValue(parts[0].Trim(), emBase, percentBase);
            double x = EvaluateCssValue(parts[1].Trim(), emBase, percentBase);
            
            result = Math.Atan2(y, x) * 180.0 / Math.PI;
            return true;
        }

        /// <summary>
        /// Parse hypot() function: hypot(value1, value2, ...) - hypotenuse of values
        /// </summary>
        private static bool TryParseHypot(string value, out double result, double emBase = 16.0, double percentBase = 0)
        {
            result = 0;
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            
            var inner = value.Substring(open + 1, close - open - 1).Trim();
            var parts = SplitCssFunctionArgs(inner);
            
            if (parts.Count == 0) return false;
            
            double sumOfSquares = 0;
            foreach (var part in parts)
            {
                double v = EvaluateCssValue(part.Trim(), emBase, percentBase);
                sumOfSquares += v * v;
            }
            
            result = Math.Sqrt(sumOfSquares);
            return true;
        }

        /// <summary>
        /// Parse env() function for environment variables like safe-area-inset-*
        /// </summary>
        private static bool TryParseEnv(string value, out double result)
        {
            result = 0;
            
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return false;
            
            var inner = value.Substring(open + 1, close - open - 1).Trim();
            var parts = SplitCssFunctionArgs(inner);
            if (parts.Count == 0) return false;
            
            string varName = parts[0].Trim().ToLowerInvariant();
            
            switch (varName)
            {
                case "safe-area-inset-top":
                case "safe-area-inset-bottom":
                case "safe-area-inset-left":
                case "safe-area-inset-right":
                    result = 0;
                    return true;
                    
                case "titlebar-area-x":
                case "titlebar-area-y":
                case "titlebar-area-width":
                case "titlebar-area-height":
                    result = 0;
                    return true;
                    
                case "keyboard-inset-top":
                case "keyboard-inset-bottom":
                case "keyboard-inset-left":
                case "keyboard-inset-right":
                case "keyboard-inset-width":
                case "keyboard-inset-height":
                    result = 0;
                    return true;
                    
                default:
                    if (parts.Count > 1)
                    {
                        if (TryPx(parts[1].Trim(), out result))
                            return true;
                    }
                    return false;
            }
        }

        #endregion

        #region CSS Variable Resolution

        /// <summary>
        /// Resolve CSS var() custom property value
        /// Syntax: var(--property-name) or var(--property-name, fallback)
        /// </summary>
        private static string ResolveVarValue(string value, double emBase = 16.0, double percentBase = 0, int depth = 0)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (depth > 10) return null;
            
            var lower = value.Trim().ToLowerInvariant();
            if (!lower.StartsWith("var(")) return value;
            
            int open = value.IndexOf('(');
            int close = value.LastIndexOf(')');
            if (open < 0 || close <= open) return null;
            
            var inner = value.Substring(open + 1, close - open - 1).Trim();
            
            string varName = null;
            string fallback = null;
            
            int commaPos = FindFallbackComma(inner);
            if (commaPos > 0)
            {
                varName = inner.Substring(0, commaPos).Trim();
                fallback = inner.Substring(commaPos + 1).Trim();
            }
            else
            {
                varName = inner.Trim();
            }
            
            if (!varName.StartsWith("--")) return fallback ?? null;
            
            string resolved = null;
            lock (_customProperties)
            {
                _customProperties.TryGetValue(varName, out resolved);
            }
            
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                if (resolved.ToLowerInvariant().Contains("var("))
                {
                    return ResolveVarValue(resolved, emBase, percentBase, depth + 1);
                }
                return resolved;
            }
            
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                if (fallback.ToLowerInvariant().Contains("var("))
                {
                    return ResolveVarValue(fallback, emBase, percentBase, depth + 1);
                }
                return fallback;
            }
            
            if (DEBUG_FILE_LOGGING && depth == 0)
                EngineLogCompat.Debug($"[CSS-VAR] {varName} NOT FOUND and NO FALLBACK", LogCategory.Layout);
            return null;
        }
        
        /// <summary>
        /// Find the comma separating var name from fallback, accounting for nested parentheses
        /// </summary>
        private static int FindFallbackComma(string inner)
        {
            int depth = 0;
            for (int i = 0; i < inner.Length; i++)
            {
                char c = inner[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (c == ',' && depth == 0)
                    return i;
            }
            return -1;
        }

        #endregion

        #region Calc Expression Evaluation

        /// <summary>
        /// Evaluate a calc expression supporting +, -, *, / with proper operator precedence
        /// </summary>
        private static double EvaluateCalcExpression(string expr, double emBase, double percentBase)
        {
            var tokens = TokenizeCalcExpression(expr);
            if (tokens.Count == 0) return 0;

            var output = new Stack<double>();
            var operators = new Stack<char>();

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                if (IsCalcOperator(token))
                {
                    char op = token[0];
                    while (operators.Count > 0 && ShouldPopOperator(operators.Peek(), op))
                    {
                        ApplyOperator(output, operators.Pop());
                    }
                    operators.Push(op);
                }
                else
                {
                    double val = ParseCalcValue(token, emBase, percentBase);
                    output.Push(val);
                }
            }

            while (operators.Count > 0)
            {
                ApplyOperator(output, operators.Pop());
            }

            return output.Count > 0 ? output.Pop() : 0;
        }

        private static List<string> TokenizeCalcExpression(string expr)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();

            for (int i = 0; i < expr.Length; i++)
            {
                char c = expr[i];

                if (c == ' ' || c == '\t')
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    continue;
                }

                if ((c == '+' || c == '-') && current.Length > 0 && !char.IsDigit(expr[Math.Max(0, i - 1)]))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    tokens.Add(c.ToString());
                    continue;
                }
                else if ((c == '+' || c == '-') && current.Length == 0)
                {
                    if (tokens.Count > 0 && !IsCalcOperator(tokens[tokens.Count - 1]))
                    {
                        tokens.Add(c.ToString());
                        continue;
                    }
                    current.Append(c);
                    continue;
                }
                else if (c == '*' || c == '/')
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }
                    tokens.Add(c.ToString());
                    continue;
                }

                current.Append(c);
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }
            return tokens;
        }

        private static double ParseCalcValue(string token, double emBase, double percentBase)
        {
            token = token.Trim().ToLowerInvariant();
            double v;
            double vpWidth = CssParser.MediaViewportWidth ?? 1920.0;
            double vpHeight = CssParser.MediaViewportHeight ?? 1080.0;

            if (token.EndsWith("rem"))
            {
                if (TryDouble(token.Substring(0, token.Length - 3), out v)) return v * 16.0;
            }
            else if (token.EndsWith("em"))
            {
                if (TryDouble(token.Substring(0, token.Length - 2), out v)) return v * emBase;
            }
            else if (token.EndsWith("vw"))
            {
                if (TryDouble(token.Substring(0, token.Length - 2), out v)) return v * vpWidth / 100.0;
            }
            else if (token.EndsWith("vh"))
            {
                if (TryDouble(token.Substring(0, token.Length - 2), out v)) return v * vpHeight / 100.0;
            }
            else if (token.EndsWith("vmin"))
            {
                if (TryDouble(token.Substring(0, token.Length - 4), out v)) return v * Math.Min(vpWidth, vpHeight) / 100.0;
            }
            else if (token.EndsWith("vmax"))
            {
                if (TryDouble(token.Substring(0, token.Length - 4), out v)) return v * Math.Max(vpWidth, vpHeight) / 100.0;
            }
            else if (token.EndsWith("%"))
            {
                if (TryDouble(token.Substring(0, token.Length - 1), out v)) return v * percentBase / 100.0;
            }
            else
            {
                if (TryDouble(token, out v)) return v;
            }
            return 0;
        }

        private static bool IsCalcOperator(string token)
        {
            return token == "+" || token == "-" || token == "*" || token == "/";
        }

        private static bool ShouldPopOperator(char stackTop, char newOp)
        {
             int p1 = GetPriority(stackTop);
             int p2 = GetPriority(newOp);
             return p1 >= p2;
        }

        private static int GetPriority(char op)
        {
            if (op == '*' || op == '/') return 2;
            if (op == '+' || op == '-') return 1;
            return 0;
        }

        private static void ApplyOperator(Stack<double> output, char op)
        {
            if (output.Count < 2) return;
            double right = output.Pop();
            double left = output.Pop();
            double res = 0;
            switch(op)
            {
                case '+': res = left + right; break;
                case '-': res = left - right; break;
                case '*': res = left * right; break;
                case '/': res = right != 0 ? left / right : 0; break;
            }
            output.Push(res);
        }

        #endregion
    }
}
