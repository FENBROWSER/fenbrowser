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
            if (node is Element e && e.HasAttribute("hidden")) return true;
            if (style != null && style.Display == "none") return true;
             // Check details/summary
             // Logic simplified for refactor
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
