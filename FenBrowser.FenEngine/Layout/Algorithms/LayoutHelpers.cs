using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Layout.Coordinates;

namespace FenBrowser.FenEngine.Layout.Algorithms
{
    public static class LayoutHelpers
    {
        public static IEnumerable<Node> GetChildrenWithPseudos(Element element, Node fallbackNode, MinimalLayoutComputer computer)
        {
            if (element == null) 
            {
               if (fallbackNode != null) yield return fallbackNode;
               yield break;
            }
            // In a real refactor, use computer.GetChildrenWithPseudos(element) if exposed, or copy logic.
            // Assuming we expose it.
            foreach (var child in computer.GetChildrenWithPseudosInternal(element, fallbackNode))
            {
                yield return child;
            }
        }

        public static bool IsInlineLevel(Node node, MinimalLayoutComputer computer)
        {
            return computer.IsInlineLevelInternal(node);
        }

        public static bool ShouldHide(Node node, CssComputed style)
        {
            if (node is Element e)
            {
                if (e.HasAttribute("hidden")) return true;
                
                // Hide metadata/invisible tags - their children (including text) should not be rendered
                string tag = e.TagName?.ToLowerInvariant() ?? "";
                if (tag == "head" || tag == "script" || tag == "style" || tag == "template" || 
                    tag == "link" || tag == "meta" || tag == "title" || tag == "noscript")
                    return true;
            }
            else if (node is Text)
            {
                // Hide text nodes that are children of hidden elements
                var parent = node.Parent as Element;
                if (parent != null)
                {
                    string parentTag = parent.TagName?.ToLowerInvariant() ?? "";
                    if (parentTag == "head" || parentTag == "script" || parentTag == "style" || 
                        parentTag == "template" || parentTag == "noscript")
                        return true;
                }
            }
            
            if (style != null && style.Display == "none") return true;
            
            return false;
        }

        public static void ApplyContainerWidthConstraints(CssComputed style, string writingMode, float inlineOffset, ref float logicalAvailableInline)
        {
             bool isBorderBox = style.BoxSizing == "border-box";
             if (style.Width.HasValue)
             {
                  float contentW = (float)style.Width.Value;
                  if (isBorderBox) contentW -= inlineOffset;
                  logicalAvailableInline = contentW;
             }
             if (style.MaxWidth.HasValue)
             {
                  float contentMax = (float)style.MaxWidth.Value;
                  if (isBorderBox) contentMax -= inlineOffset;
                  if (!float.IsInfinity(logicalAvailableInline) && contentMax < logicalAvailableInline)
                  {
                      logicalAvailableInline = contentMax;
                  }
                  else if (float.IsInfinity(logicalAvailableInline))
                  {
                      logicalAvailableInline = contentMax;
                  }
             }
        }
    }
}
