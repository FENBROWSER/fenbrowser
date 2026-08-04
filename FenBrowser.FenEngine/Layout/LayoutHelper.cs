using System;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using System.Text;
using SkiaSharp;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.UserAgent;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.FenEngine.Layout
{
    public static class LayoutHelper
    {
        public static string GetTextContent(Node node)
        {
             if (node is Text t) return t.NodeValue ?? "";
             if (node.ChildNodes == null) return "";
             var sb = new StringBuilder();
             foreach (var c in node.ChildNodes) sb.Append(GetTextContent(c));
             return sb.ToString();
        }

        public static string GetRenderableTextContent(Node node)
        {
            if (node == null) return string.Empty;

            if (node is Element element)
            {
                string tag = element.TagName?.ToUpperInvariant() ?? string.Empty;
                if (IsNonRenderableTextTag(tag))
                {
                    return string.Empty;
                }

                if (element.HasAttribute("hidden"))
                {
                    return string.Empty;
                }
            }

            if (node is Text textNode)
            {
                return textNode.NodeValue ?? string.Empty;
            }

            if (node.ChildNodes == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var child in node.ChildNodes)
            {
                sb.Append(GetRenderableTextContent(child));
            }

            return sb.ToString();
        }

        public static string GetRenderableTextContentTrimmed(Node node)
        {
            return GetRenderableTextContent(node).Trim();
        }

        public static bool TryResolveLineHeight(CssComputed style, out float lineHeight)
        {
            lineHeight = 0f;
            if (style?.LineHeight.HasValue != true)
            {
                return false;
            }

            float rawLineHeight = (float)style.LineHeight.Value;
            if (!float.IsFinite(rawLineHeight) || rawLineHeight < 0f)
            {
                return false;
            }

            float fontSize = 16f;
            if (style.FontSize.HasValue && style.FontSize.Value > 0)
            {
                fontSize = (float)style.FontSize.Value;
            }

            lineHeight = rawLineHeight > 0f && rawLineHeight < 3f
                ? rawLineHeight * fontSize
                : rawLineHeight;
            return true;
        }

        public static SKRect CleanRect(SKRect r)
        {
            float l = r.Left, t = r.Top, ri = r.Right, b = r.Bottom;
            if (float.IsNaN(l) || float.IsInfinity(l)) l = 0;
            if (float.IsNaN(t) || float.IsInfinity(t)) t = 0;
            if (float.IsNaN(ri) || float.IsInfinity(ri)) ri = l;
            if (float.IsNaN(b) || float.IsInfinity(b)) b = t;
            return new SKRect(l, t, ri, b);
        }

        public static SKRect NormalizeRect(SKRect rect, SKRect? container = null, bool clampToContainer = false)
        {
            var cleaned = CleanRect(rect);

            float left = cleaned.Left;
            float top = cleaned.Top;
            float right = cleaned.Right;
            float bottom = cleaned.Bottom;

            if (right < left)
            {
                (left, right) = (right, left);
            }

            if (bottom < top)
            {
                (top, bottom) = (bottom, top);
            }

            var normalized = new SKRect(left, top, right, bottom);
            if (!clampToContainer || !container.HasValue)
            {
                return normalized;
            }

            var c = CleanRect(container.Value);
            float cLeft = c.Left;
            float cTop = c.Top;
            float cRight = c.Right;
            float cBottom = c.Bottom;

            if (cRight < cLeft)
            {
                (cLeft, cRight) = (cRight, cLeft);
            }

            if (cBottom < cTop)
            {
                (cTop, cBottom) = (cBottom, cTop);
            }

            float clampedLeft = Math.Clamp(normalized.Left, cLeft, cRight);
            float clampedTop = Math.Clamp(normalized.Top, cTop, cBottom);
            float clampedRight = Math.Clamp(normalized.Right, clampedLeft, cRight);
            float clampedBottom = Math.Clamp(normalized.Bottom, clampedTop, cBottom);

            return new SKRect(clampedLeft, clampedTop, clampedRight, clampedBottom);
        }

        public static bool ShouldHide(Node node, CssComputed style)
        {
            if (node == null) return true;
            string tag = (node as Element)?.TagName?.ToUpperInvariant() ?? "";

            if (style != null && style.Display == "none") return true;
            // Note: visibility:hidden elements MUST still generate boxes and occupy space per CSS spec.
            // They are simply not painted. The paint tree builder handles this correctly.
            if (tag == "HEAD" || 
                tag == "SCRIPT" || 
                tag == "STYLE" || 
                tag == "META" || 
                tag == "TITLE" || 
                tag == "LINK" ||
                tag == "NOSCRIPT" ||
                tag == "TEMPLATE") 
            {
                return true;
            }
            return false;
        }

        public static void ApplyUserAgentStyles(Node node, ref CssComputed style)
        {
             if (style == null) return;
             if (node is Element e)
             {
                 // Reference implementation in UAStyleProvider
                 UAStyleProvider.Apply(e, ref style);
             }
        }

        public static void MeasureInputButtonText(Element node, CssComputed style, ref float width, ref float height)
        {
             string val = node.GetAttribute("value");
             if (string.IsNullOrEmpty(val) && node.TagName == "BUTTON")
             {
                 val = GetRenderableTextContent(node);
             }
             
             if (string.IsNullOrEmpty(val))
             {
                 if (node.TagName == "INPUT") val = "Submit"; 
             }

             if (!string.IsNullOrEmpty(val))
             {
                 var fontSize = style?.FontSize != null ? (float)style.FontSize.Value : 16f;
                 var tf = TextLayoutHelper.ResolveTypeface(style?.FontFamily?.ToString(), val);
                 using (var font = new SKFont(tf, fontSize))
                 {
                      font.MeasureText(val, out var bounds);

                      float w = bounds.Width + 24;
                      if (w > width) width = w;

                      float h = bounds.Height + 10;
                      if (h > height) height = h;
                 }
             }
        }

        private static bool IsNonRenderableTextTag(string tag)
        {
            return tag == "HEAD" ||
                   tag == "SCRIPT" ||
                   tag == "STYLE" ||
                   tag == "META" ||
                   tag == "TITLE" ||
                   tag == "LINK" ||
                   tag == "NOSCRIPT" ||
                   tag == "TEMPLATE";
        }

        public static float EvaluateCssExpression(
            string expression,
            float parentSize,
            float viewportWidth = 0,
            float viewportHeight = 0,
            float fontSize = 16f)
        {
            if (string.IsNullOrEmpty(expression)) return -1;
            expression = expression.Trim().ToLowerInvariant();

            // Intrinsic sizing keywords.
            if (expression == "min-content") return 0;
            if (expression == "max-content" || expression == "fit-content")
            {
                return Math.Max(0, parentSize);
            }

            if (expression.StartsWith("fit-content(") && expression.EndsWith(")"))
            {
                int start = expression.IndexOf('(');
                int end = expression.LastIndexOf(')');
                if (start > -1 && end > start)
                {
                    var inner = expression.Substring(start + 1, end - start - 1).Trim();
                    float limit = EvaluateCssExpression(inner, parentSize, viewportWidth, viewportHeight, fontSize);
                    if (limit < 0)
                    {
                        return Math.Max(0, parentSize);
                    }

                    float maxContent = Math.Max(0, parentSize);
                    return Math.Max(0, Math.Min(maxContent, Math.Max(0, limit)));
                }

                return Math.Max(0, parentSize);
            }

            if (expression.StartsWith("min(") || expression.StartsWith("max(") || expression.StartsWith("clamp("))
            {
                int start = expression.IndexOf('(');
                int end = expression.LastIndexOf(')');
                if (start > -1 && end > start)
                {
                    var inner = expression.Substring(start + 1, end - start - 1);
                    var args = new List<string>();
                    var chunk = new StringBuilder();
                    int depth = 0;

                    foreach (char ch in inner)
                    {
                        if (ch == '(') depth++;
                        else if (ch == ')') depth = Math.Max(0, depth - 1);

                        if (ch == ',' && depth == 0)
                        {
                            args.Add(chunk.ToString().Trim());
                            chunk.Clear();
                            continue;
                        }

                        chunk.Append(ch);
                    }

                    if (chunk.Length > 0)
                    {
                        args.Add(chunk.ToString().Trim());
                    }

                    if (expression.StartsWith("min("))
                    {
                        float minValue = float.PositiveInfinity;
                        foreach (var arg in args)
                        {
                            float parsed = EvaluateCssExpression(arg, parentSize, viewportWidth, viewportHeight, fontSize);
                            if (parsed < 0) continue;
                            if (parsed < minValue) minValue = parsed;
                        }
                        if (!float.IsPositiveInfinity(minValue)) return minValue;
                    }
                    else if (expression.StartsWith("max("))
                    {
                        float maxValue = float.NegativeInfinity;
                        foreach (var arg in args)
                        {
                            float parsed = EvaluateCssExpression(arg, parentSize, viewportWidth, viewportHeight, fontSize);
                            if (parsed < 0) continue;
                            if (parsed > maxValue) maxValue = parsed;
                        }
                        if (!float.IsNegativeInfinity(maxValue)) return maxValue;
                    }
                    else if (expression.StartsWith("clamp(") && args.Count == 3)
                    {
                        float min = EvaluateCssExpression(args[0], parentSize, viewportWidth, viewportHeight, fontSize);
                        float preferred = EvaluateCssExpression(args[1], parentSize, viewportWidth, viewportHeight, fontSize);
                        float max = EvaluateCssExpression(args[2], parentSize, viewportWidth, viewportHeight, fontSize);
                        if (min >= 0 && preferred >= 0 && max >= 0)
                        {
                            return Math.Max(min, Math.Min(preferred, max));
                        }
                    }
                }
            }

            // Simple parser
            if (expression.StartsWith("calc"))
            {
                // Basic calc() support: "calc(100% - 20px)"
                int start = expression.IndexOf('(');
                int end = expression.LastIndexOf(')');
                if (start > -1 && end > start)
                {
                    string inner = expression.Substring(start + 1, end - start - 1);
                    
                    var parts = TokenizeCalcExpression(inner);

                    if (parts.Count == 0 || !TryEvaluateCalcOperand(
                            parts[0], parentSize, viewportWidth, viewportHeight, fontSize, out float firstValue))
                    {
                        return -1f;
                    }

                    // Collapse multiplication/division first, then addition/subtraction.
                    // CSS calc() follows normal arithmetic precedence; evaluating left-to-right
                    // turns expressions such as 100% - 2px * 2 into (100% - 2px) * 2.
                    var values = new List<float> { firstValue };
                    var additiveOperators = new List<string>();

                    for (int i = 1; i < parts.Count; i += 2)
                    {
                        if (i + 1 >= parts.Count)
                        {
                            return -1f;
                        }

                        string op = parts[i].Trim();
                        if (op != "+" && op != "-" && op != "*" && op != "/")
                        {
                            return -1f;
                        }

                        if (!TryEvaluateCalcOperand(
                                parts[i + 1], parentSize, viewportWidth, viewportHeight, fontSize, out float operand))
                        {
                            return -1f;
                        }

                        if (op == "*" || op == "/")
                        {
                            if (op == "/" && Math.Abs(operand) <= float.Epsilon)
                            {
                                return -1f;
                            }

                            int last = values.Count - 1;
                            values[last] = op == "*" ? values[last] * operand : values[last] / operand;
                        }
                        else
                        {
                            additiveOperators.Add(op);
                            values.Add(operand);
                        }
                    }

                    float result = values[0];
                    for (int i = 0; i < additiveOperators.Count; i++)
                    {
                        result = additiveOperators[i] == "+"
                            ? result + values[i + 1]
                            : result - values[i + 1];
                    }

                    return float.IsFinite(result) ? result : -1f;
                }
                return -1; 
            }

            if (expression.EndsWith("px"))
            {
                 if (float.TryParse(expression.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float px)) return px;
            }
            if (expression.EndsWith("rem"))
            {
                 if (float.TryParse(expression.Replace("rem", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float rem)) return rem * 16f;
            }
            if (expression.EndsWith("em"))
            {
                 if (float.TryParse(expression.Replace("em", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float em)) return em * fontSize;
            }
            if (expression.EndsWith("%"))
            {
                 if (float.TryParse(expression.Replace("%", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float pct)) return parentSize * (pct / 100f);
            }
            if (expression.EndsWith("vh"))
            {
                 if (float.TryParse(expression.Replace("vh", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float vh)) return viewportHeight * (vh / 100f);
            }
            if (expression.EndsWith("vw"))
            {
                 if (float.TryParse(expression.Replace("vw", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float vw)) return viewportWidth * (vw / 100f);
            }
             if (float.TryParse(expression, NumberStyles.Float, CultureInfo.InvariantCulture, out float val)) return val;

            return -1;
        }

        private static bool TryEvaluateCalcOperand(
            string operand,
            float parentSize,
            float viewportWidth,
            float viewportHeight,
            float fontSize,
            out float value)
        {
            value = EvaluateCssExpression(operand, parentSize, viewportWidth, viewportHeight, fontSize);
            if (value != -1f)
            {
                return true;
            }

            // -1 is both the public failure sentinel and a valid unitless operand.
            return float.TryParse(
                operand?.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        private static List<string> TokenizeCalcExpression(string expression)
        {
            var tokens = new List<string>();
            if (string.IsNullOrWhiteSpace(expression))
            {
                return tokens;
            }

            var current = new StringBuilder();
            bool previousWasOperator = true;
            for (int i = 0; i < expression.Length; i++)
            {
                char ch = expression[i];
                if (char.IsWhiteSpace(ch))
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                        previousWasOperator = false;
                    }
                    continue;
                }

                bool isOperator = ch == '+' || ch == '-' || ch == '*' || ch == '/';
                bool isSignedNumber = (ch == '+' || ch == '-') &&
                    previousWasOperator &&
                    i + 1 < expression.Length &&
                    (char.IsDigit(expression[i + 1]) || expression[i + 1] == '.');

                if (isOperator && !isSignedNumber)
                {
                    if (current.Length > 0)
                    {
                        tokens.Add(current.ToString());
                        current.Clear();
                    }

                    tokens.Add(ch.ToString());
                    previousWasOperator = true;
                    continue;
                }

                current.Append(ch);
                previousWasOperator = false;
            }

            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
            }

            return tokens;
        }
    }
}


