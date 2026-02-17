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
             if (node.Children == null) return "";
             var sb = new StringBuilder();
             foreach (var c in node.Children) sb.Append(GetTextContent(c));
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

            if (node.Children == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var child in node.Children)
            {
                sb.Append(GetRenderableTextContent(child));
            }

            return sb.ToString();
        }

        public static string GetRenderableTextContentTrimmed(Node node)
        {
            return GetRenderableTextContent(node).Trim();
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
                 using (var paint = new SKPaint())
                 {
                      paint.TextSize = style?.FontSize != null ? (float)style.FontSize.Value : 16f; 
                      var tf = TextLayoutHelper.ResolveTypeface(style?.FontFamily?.ToString(), val);
                      paint.Typeface = tf;
                      
                      var bounds = new SKRect();
                      paint.MeasureText(val, ref bounds);
                      
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

        public static float EvaluateCssExpression(string expression, float parentSize, float viewportWidth = 0, float viewportHeight = 0)
        {
            if (string.IsNullOrEmpty(expression)) return -1;
            expression = expression.Trim().ToLowerInvariant();

            // Simple parser
            if (expression.StartsWith("calc"))
            {
                // Basic calc() support: "calc(100% - 20px)"
                int start = expression.IndexOf('(');
                int end = expression.LastIndexOf(')');
                if (start > -1 && end > start)
                {
                    string inner = expression.Substring(start + 1, end - start - 1);
                    
                    // Very simple parser for "A op B"
                    // Supports: 100% - 20px, 50vh - 10px
                    // Does NOT support complex nesting yet
                    
                    var parts = inner.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    
                    // Simple accumulation
                    // ex: 100% - 20px
                    // stack: [val]
                    // op: -
                    
                    float currentVal = 0;
                    string currentOp = "+";
                    
                    bool first = true;
                    
                    for (int i=0; i<parts.Length; i++)
                    {
                        string p = parts[i].Trim();
                        if (p == "+" || p == "-" || p == "*" || p == "/")
                        {
                            currentOp = p;
                        }
                        else
                        {
                            float calcVal = EvaluateCssExpression(p, parentSize, viewportWidth, viewportHeight);
                            if (calcVal != -1)
                            {
                                if (first) 
                                {
                                    currentVal = calcVal;
                                    first = false;
                                }
                                else
                                {
                                    switch (currentOp)
                                    {
                                        case "+": currentVal += calcVal; break;
                                        case "-": currentVal -= calcVal; break;
                                        case "*": currentVal *= calcVal; break;
                                        case "/": if (calcVal != 0) currentVal /= calcVal; break;
                                    }
                                }
                            }
                        }
                    }
                    return currentVal;
                }
                return -1; 
            }

            if (expression.EndsWith("px"))
            {
                 if (float.TryParse(expression.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out float px)) return px;
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
    }
}


