using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using SkiaSharp;
using FenBrowser.FenEngine.Layout;

namespace FenBrowser.FenEngine.Rendering.Css
{
    /// <summary>
    /// Implements the CSS Flexbox Layout Algorithm.
    /// Handles main/cross axis sizing, wrapping, alignment, and gap support.
    /// </summary>
    public static class CssFlexLayout
    {
        // Helper to track flex item state during algorithm
        public class FlexItem
        {
            public Node Node;
            public CssComputed Style;
            public LayoutMetrics IntrinsicMetrics; // Result of first measure
            
            public float FlexBaseSize;       // 'flex-basis' resolved
            public float TargetMainSize;     // Final size after grow/shrink
            public float HypotheticalMainSize; 
            
            public float MinMain;
            public float MaxMain;
            public float MinCross;
            public float MaxCross;
            
            public float FlexGrow;
            public float FlexShrink;
            public int Order; // CSS order property
            
            public bool Frozen; // If true, size is fixed

            public FlexItem(Node node, CssComputed style)
            {
                Node = node;
                Style = style;
                
                // Initialize defaults
                MinMain = 0;
                MaxMain = float.MaxValue;
                MinCross = 0;
                MaxCross = float.MaxValue;
            }
        }

        public class FlexLine
        {
            public List<FlexItem> Items = new List<FlexItem>();
            public float MainSize { get; set; }  // Sum of items + gaps
            public float CrossSize { get; set; } // Max cross size of items
            
            public float TotalFlexGrow { get; set; }
            public float TotalFlexShrink { get; set; }
            
            public float CrossStart { get; set; } // Relative to content box
            public float Baseline { get; set; } // Max Ascent for baseline alignment
        }

        public static LayoutMetrics Measure(
            Element container, 
            SKSize availableSize, 
            Func<Node, SKSize, int, bool, LayoutMetrics> measureChild,
            Func<Node, CssComputed> getStyle,
            Func<Node, CssComputed, bool> shouldHide,
            int depth,
            IEnumerable<Node> childrenSource = null)
        {
            var style = getStyle(container);
            // ... (keep existing setup logic) ...
            bool isRow = !(style?.FlexDirection?.ToLowerInvariant().Contains("column") ?? false);
            bool isWrap = (style?.FlexWrap?.ToLowerInvariant() ?? "nowrap") != "nowrap";

            // ... (keep existing gaps/children logic) ...
            float rowGap = (float)(style?.RowGap ?? style?.Gap ?? 0);
            float colGap = (float)(style?.ColumnGap ?? style?.Gap ?? 0);
            float mainGap = isRow ? colGap : rowGap;
            float crossGap = isRow ? rowGap : colGap;

            var source = childrenSource ?? container.ChildNodes;
            var children = source.Where(c => 
            {
                // Whitespace-only text nodes must not become flex items. If they do,
                // they can consume large main-axis space and shift real content off-screen.
                if (c is Text t)
                {
                    return !string.IsNullOrWhiteSpace(t.Data);
                }
                return !shouldHide(c, getStyle(c));
            }).ToList();

            if (children.Count == 0) return new LayoutMetrics();
            
            // Sort children by CSS order property (stable sort preserves source order for equal values)
            children = children.OrderBy(c => getStyle(c)?.Order ?? 0).ToList();
            
            // ... (keep mainAvailable logic) ...
            float mainAvailable = isRow ? availableSize.Width : availableSize.Height;
            if (float.IsInfinity(mainAvailable)) mainAvailable = float.MaxValue;
            
            string alignItems = style?.AlignItems?.ToLowerInvariant() ?? "stretch";

            if (container.TagName == "DIV" && children.Any(c => c is Element e && e.TagName == "A"))
            {
                 FenLogger.Error($"[FLEX-TRACE-V2] Container={container.TagName} Class={container.GetAttribute("class")} IsRow={isRow} IsWrap={isWrap} MainAvail={mainAvailable} Width={availableSize.Width}");
            }

            // 1. Generate Flex Items
            var items = new List<FlexItem>();
            int itemCountGuard = 0;
            foreach (var child in children)
            {
                if (itemCountGuard++ > 10000) { FenLogger.Log("[CssFlexLayout] Item limit reached, truncating", LogCategory.Layout); break; }
                var cStyle = getStyle(child);
                var item = new FlexItem(child, cStyle);
                
                // ... (keep margin logic) ...
                var margin = cStyle?.Margin ?? new Thickness(0);
                float mMain = isRow ? (float)(margin.Left + margin.Right) : (float)(margin.Top + margin.Bottom);
                float mCross = isRow ? (float)(margin.Top + margin.Bottom) : (float)(margin.Left + margin.Right);

                float basis = float.NaN;
                if (cStyle?.FlexBasis.HasValue == true && !double.IsNaN(cStyle.FlexBasis.Value))
                    basis = (float)cStyle.FlexBasis.Value;
                
                if (!float.IsNaN(basis))
                {
                    item.FlexBaseSize = basis;
                    var constraints = availableSize;
                     if (isRow) { constraints.Width = basis; } else { constraints.Height = basis; }
                    item.IntrinsicMetrics = measureChild(child, constraints, depth + 1, false);
                }
                else
                {
                    // "Content" basis
                    var constraints = availableSize;
                    bool shrink = false;
                    
                    bool hasExplicitMainSize = HasExplicitMainSize(cStyle, isRow);

                    if (isRow) 
                    {
                        // Cross-size probing for row flex items must remain intrinsic unless
                        // the item has an explicitly definite cross size. Passing a finite
                        // viewport-height constraint here leaks viewport math into auto/percent
                        // heights under indefinite parents.
                        bool hasDefiniteCrossSize =
                            cStyle?.Height.HasValue == true ||
                            !string.IsNullOrEmpty(cStyle?.HeightExpression);
                        if (!hasDefiniteCrossSize)
                        {
                            constraints.Height = float.PositiveInfinity;
                        }

                        if (hasExplicitMainSize &&
                            !float.IsInfinity(availableSize.Width) &&
                            !float.IsNaN(availableSize.Width) &&
                            availableSize.Width > 0)
                        {
                            constraints.Width = availableSize.Width;
                        }
                        else
                        {
                            constraints.Width = float.PositiveInfinity;
                            // Row implies we want the intrinsic content width (shrink-to-fit)
                            // otherwise block-level items expand to fill viewport width (e.g. 1920px) which breaks the row.
                            shrink = true;
                        }
                    }
                    else 
                    {
                        if (!(hasExplicitMainSize &&
                              !float.IsInfinity(availableSize.Height) &&
                              !float.IsNaN(availableSize.Height) &&
                              availableSize.Height > 0))
                        {
                            constraints.Height = float.PositiveInfinity;
                        }

                        // Cross-axis (width) must be constrained to container width
                        if (!float.IsInfinity(availableSize.Width) && availableSize.Width > 0)
                        {
                            constraints.Width = availableSize.Width;
                            
                            // CRITICAL FIX: Block-level items in column flex should NOT expand 
                            // if alignment is not 'stretch'.
                            string itemAlign = cStyle?.AlignSelf?.ToLowerInvariant();
                            bool isStretch = (itemAlign == "stretch") || (string.IsNullOrEmpty(itemAlign) && alignItems == "stretch");
                            if (itemAlign == "auto" || string.IsNullOrEmpty(itemAlign)) isStretch = (alignItems == "stretch");
                            
                            if (!isStretch)
                            {
                                shrink = true; // Tell MeasureNode to NOT force-expand block width
                            }
                        }
                    }
                    
                    if (container.TagName == "DIV" || container.TagName == "HEADER" || container.TagName == "NAV")
                    {
                         // Logging removed
                    }

                    item.IntrinsicMetrics = measureChild(child, constraints, depth + 1, shrink);
                    item.FlexBaseSize = isRow ? item.IntrinsicMetrics.MaxChildWidth : item.IntrinsicMetrics.ContentHeight;
                }
                
                // ... rest of method

                
                item.FlexGrow = (float)(cStyle?.FlexGrow ?? 0);
                item.FlexShrink = (float)(cStyle?.FlexShrink ?? 1); // Default shrink is 1
                
                // Resolve Min/Max Constraints
                if (cStyle != null)
                {
                    double? min = isRow ? cStyle.MinWidth : cStyle.MinHeight;
                    double? max = isRow ? cStyle.MaxWidth : cStyle.MaxHeight;
                    
                    if (min.HasValue && !double.IsNaN(min.Value)) item.MinMain = (float)min.Value;
                    if (max.HasValue && !double.IsNaN(max.Value)) item.MaxMain = (float)max.Value;
                    
                    // Cross Axis Constraints
                    double? minC = isRow ? cStyle.MinHeight : cStyle.MinWidth;
                    double? maxC = isRow ? cStyle.MaxHeight : cStyle.MaxWidth;
                    if (minC.HasValue && !double.IsNaN(minC.Value)) item.MinCross = (float)minC.Value;
                    if (maxC.HasValue && !double.IsNaN(maxC.Value)) item.MaxCross = (float)maxC.Value;
                    
                    // Handle 'min-width: auto' (flexbox default)
                    // If min-width is auto (null/NaN logic usually, but here checked via MinWidth nullity or explicit 'auto' logic if parsed)
                    // For now, assuming default null means auto.
                    // "auto" means "min-content" if overflow is visible.
                    bool isMinAuto = !min.HasValue; 
                    bool overflowVisible = (isRow ? cStyle.OverflowX : cStyle.OverflowY) == "visible" || (cStyle.Overflow == "visible");
                    bool hasContainerRelativeMainSize = HasContainerRelativeMainSize(cStyle, isRow);
                    
                    if (isMinAuto && overflowVisible)
                    {
                        // Resolve auto limit to min-content size
                        // We already measured intrinsic metrics
                        // min-content is essentially content size without wrapping? 
                        // Simplified: use measured content size as min-basis
                        float contentSize = isRow ? item.IntrinsicMetrics.MaxChildWidth : item.IntrinsicMetrics.ContentHeight;

                        // Container-relative main sizes such as width:100% should still be allowed
                        // to shrink within the flex line instead of pinning the auto minimum to the
                        // resolved container width from the probe measurement.
                        if (!hasContainerRelativeMainSize)
                        {
                            item.MinMain = contentSize;
                        }
                        
                        // BUT: If width is fixed, min-width:auto doesn't override it.
                        // logic: used size = max(min, min(max, clamp(size)))
                    }
                }

                item.HypotheticalMainSize = Math.Max(item.MinMain, Math.Min(item.MaxMain, item.FlexBaseSize));
                items.Add(item);
            }

            // 2. Line Generation (Wrapping)
            var lines = new List<FlexLine>();
            var currentLine = new FlexLine();
            lines.Add(currentLine);
            
            float currentMainUsed = 0;
            int lineCountGuard = 0;
            
            foreach (var item in items)
            {
                if (lineCountGuard++ > 5000) break;
                var margin = item.Style?.Margin ?? new Thickness(0);
                float mMain = isRow ? (float)(margin.Left + margin.Right) : (float)(margin.Top + margin.Bottom);
                float mCross = isRow ? (float)(margin.Top + margin.Bottom) : (float)(margin.Left + margin.Right);
                
                float outerMain = item.FlexBaseSize + mMain;
                
                if (isWrap && currentLine.Items.Count > 0 && (currentMainUsed + outerMain > mainAvailable))
                {
                    // Wrap
                    currentLine.MainSize = currentMainUsed - mainGap;
                    lines.Add(currentLine = new FlexLine());
                    currentMainUsed = 0;
                }
                
                currentLine.Items.Add(item);
                
                // Track line specific grow/shrink
                currentLine.TotalFlexGrow += item.FlexGrow;
                currentLine.TotalFlexShrink += item.FlexShrink;
                
                float cross = isRow ? item.IntrinsicMetrics.ContentHeight : item.IntrinsicMetrics.MaxChildWidth;
                currentLine.CrossSize = Math.Max(currentLine.CrossSize, cross + mCross);
                
                currentMainUsed += outerMain + mainGap;
            }
            currentLine.MainSize = Math.Max(0, currentMainUsed - mainGap);

            // 3. Resolve Flexible Lengths (Calculate Target Sizes)
            // For Measure phase, we just need the hypothetical container size.
            // But if we grow, the container grows (if it wasn't fixed).
            // Actually, if availableSize is fixed, container is that size. 
            // If availableSize is infinite (shrink-to-fit container), then we sum the *flexed* sizes?
            // Spec says: If container has indefinite main size, use max-content. 
            // Max-content involves flex lines.
            
            // Simplified Resolution for Measure Phase:
            // Just accumulate line stats.
            
            float containerMainSize = 0;
            float containerCrossSize = 0;
            
            foreach (var line in lines)
            {
                // Resolve Flexing for this line
                float freeSpace = mainAvailable - line.MainSize;
                
                // If mainAvailable is infinite, freeSpace is infinite -> usually means no growing?
                // Or rather, we just report the content size (line.MainSize).
                float resolvedLineMain = line.MainSize;
                
                if (!float.IsInfinity(mainAvailable))
                {
                    // Apply Grow/Shrink logic to see if line expands to fill container
                    if (freeSpace > 0 && line.TotalFlexGrow > 0)
                    {
                        resolvedLineMain = mainAvailable; // Expands to fill
                    }
                    else if (freeSpace < 0 && line.TotalFlexShrink > 0)
                    {
                         resolvedLineMain = mainAvailable; // Shrinks to fit
                    }
                }
                else
                {
                    // Indefinite container: children don't grow.
                    resolvedLineMain = line.MainSize;
                }
                
                containerMainSize = Math.Max(containerMainSize, resolvedLineMain);
                containerCrossSize += line.CrossSize;
            }

            if (lines.Count > 1) containerCrossSize += crossGap * (lines.Count - 1);

            return new LayoutMetrics
            {
                MaxChildWidth = isRow ? containerMainSize : containerCrossSize,
                ContentHeight = isRow ? containerCrossSize : containerMainSize
            };
        }


        public static void Arrange(
            Element container, 
            SKRect contentBox, 
            Action<Node, SKRect, int> arrangeChild,
            Func<Node, CssComputed> getStyle,
             Func<Node, SKSize> getDesiredSize, 
            Func<Node, CssComputed, bool> shouldHide,
            int depth,
            IEnumerable<Node> childrenSource = null)
        {
            var style = getStyle(container);
            bool isRow = !(style?.FlexDirection?.ToLowerInvariant().Contains("column") ?? false);
            string flexWrap = style?.FlexWrap?.ToLowerInvariant() ?? "nowrap";
            bool isWrap = flexWrap != "nowrap";
            bool isWrapReverse = flexWrap == "wrap-reverse";
            
            float rowGap = (float)(style?.RowGap ?? style?.Gap ?? 0);
            float colGap = (float)(style?.ColumnGap ?? style?.Gap ?? 0);
            float mainGap = isRow ? colGap : rowGap;
            float crossGap = isRow ? rowGap : colGap;

            string justify = style?.JustifyContent?.ToLowerInvariant() ?? "flex-start";
            string alignContent = style?.AlignContent?.ToLowerInvariant() ?? "stretch";
            string alignItems = style?.AlignItems?.ToLowerInvariant() ?? "stretch";
            
            // DEBUG: Log flex container dimensions
            if (container.TagName == "BODY" || container.TagName == "HTML")
            {
                FenLogger.Debug($"[FLEX-ARRANGE] <{container.TagName}> contentBox={contentBox} isRow={isRow} Justify={justify} AlignItems={alignItems}", LogCategory.Layout);
            }
            
            // Spec compliance check
            SpecComplianceLogger.LogContentBox(container.TagName ?? "unknown", container.Id ?? "", contentBox.Width, contentBox.Height);

            var source = childrenSource ?? container.ChildNodes;
            var children = source.Where(c => 
            {
                // Match Measure() behavior: ignore whitespace-only text nodes in flex item list.
                if (c is Text t)
                {
                    return !string.IsNullOrWhiteSpace(t.Data);
                }
                return !shouldHide(c, getStyle(c));
            }).ToList();
            if (children.Count == 0) return;
            
            // Sort children by CSS order property (stable sort preserves source order for equal values)
            children = children.OrderBy(c => getStyle(c)?.Order ?? 0).ToList();

            float mainAvailable = isRow ? contentBox.Width : contentBox.Height;
             if (float.IsInfinity(mainAvailable) || float.IsNaN(mainAvailable))
            {
                mainAvailable = isRow ? 1920f : 1080f; 
            }
            
            // FIX for collapsed container
            float calculatedContentMain = 0;
            if (mainAvailable < 10f && children.Count > 0)
            {
                foreach (var child in children)
                {
                    var s = getDesiredSize(child);
                    var cStyle = getStyle(child);
                    var margin = cStyle?.Margin ?? new Thickness(0);
                    float mMain = isRow ? (float)(margin.Left + margin.Right) : (float)(margin.Top + margin.Bottom);
                    float childMainSize = isRow ? s.Width : s.Height;
                    calculatedContentMain += childMainSize + mMain + mainGap;
                }
                mainAvailable = calculatedContentMain > 0 ? calculatedContentMain : mainAvailable;
            }
            
            // 1. RE-Generate Flex Items
            var items = new List<FlexItem>();
            int arrangeItemGuard = 0;
            foreach(var child in children)
            {
                if (arrangeItemGuard++ > 10000) break;
                var s = getDesiredSize(child); 
                var cStyle = getStyle(child);
                var item = new FlexItem(child, cStyle);
                
                float basis = float.NaN;
                if (cStyle?.FlexBasis.HasValue == true && !double.IsNaN(cStyle.FlexBasis.Value))
                     basis = (float)cStyle.FlexBasis.Value;

                item.FlexBaseSize = !float.IsNaN(basis) ? basis : (isRow ? s.Width : s.Height);
                item.FlexGrow = (float)(cStyle?.FlexGrow ?? 0);
                item.FlexShrink = (float)(cStyle?.FlexShrink ?? 1);

                // Re-calculate Min/Max for Arrange phase (Ensure identical logic)
                 if (cStyle != null)
                {
                    double? min = isRow ? cStyle.MinWidth : cStyle.MinHeight;
                    double? max = isRow ? cStyle.MaxWidth : cStyle.MaxHeight;
                    
                    if (min.HasValue && !double.IsNaN(min.Value)) item.MinMain = (float)min.Value;
                    if (max.HasValue && !double.IsNaN(max.Value)) item.MaxMain = (float)max.Value;

                    // Cross Axis Constraints
                    double? minC = isRow ? cStyle.MinHeight : cStyle.MinWidth;
                    double? maxC = isRow ? cStyle.MaxHeight : cStyle.MaxWidth;
                    if (minC.HasValue && !double.IsNaN(minC.Value)) item.MinCross = (float)minC.Value;
                    if (maxC.HasValue && !double.IsNaN(maxC.Value)) item.MaxCross = (float)maxC.Value;
                    
                     bool isMinAuto = !min.HasValue; 
                    bool overflowVisible = (isRow ? cStyle.OverflowX : cStyle.OverflowY) == "visible" || (cStyle.Overflow == "visible");
                    bool hasContainerRelativeMainSize = HasContainerRelativeMainSize(cStyle, isRow);
                    
                    if (isMinAuto && overflowVisible)
                    {
                         // Use DesiredSize which is result of Measure
                         float contentSize = isRow ? s.Width : s.Height;
                         if (!hasContainerRelativeMainSize)
                         {
                             item.MinMain = contentSize;
                         }
                    }
                }

                item.HypotheticalMainSize = Math.Max(item.MinMain, Math.Min(item.MaxMain, item.FlexBaseSize));
                item.IntrinsicMetrics = new LayoutMetrics { MaxChildWidth = s.Width, ContentHeight = s.Height };
                
                items.Add(item);
            }
            
            // 2. Wrap
            var lines = new List<FlexLine>();
            var currentLine = new FlexLine();
            lines.Add(currentLine);
            float currentMainUsed = 0;
            
            foreach (var item in items)
            {
                 var margin = item.Style?.Margin ?? new Thickness(0);
                 float mMain = isRow ? (float)(margin.Left + margin.Right) : (float)(margin.Top + margin.Bottom);
                 float mCross = isRow ? (float)(margin.Top + margin.Bottom) : (float)(margin.Left + margin.Right);
                 
                 float outerMain = item.FlexBaseSize + mMain;
                 
                  if (isWrap && currentLine.Items.Count > 0 && (currentMainUsed + outerMain > mainAvailable))
                {
                    currentLine.MainSize = currentMainUsed - mainGap;
                    lines.Add(currentLine = new FlexLine());
                    currentMainUsed = 0;
                }
                
                currentLine.Items.Add(item);
                currentLine.TotalFlexGrow += item.FlexGrow;
                currentLine.TotalFlexShrink += item.FlexShrink;
                
                 float cross = isRow ? item.IntrinsicMetrics.ContentHeight : item.IntrinsicMetrics.MaxChildWidth;
                currentLine.CrossSize = Math.Max(currentLine.CrossSize, cross + mCross);
                
                currentMainUsed += outerMain + mainGap;
            }
             currentLine.MainSize = Math.Max(0, currentMainUsed - mainGap);
             
             // 3. Resolve Target Sizes
            foreach (var line in lines)
            {
                 float freeSpace = mainAvailable - line.MainSize;
                 
                 if (freeSpace > 0 && line.TotalFlexGrow > 0)
                 {
                      foreach(var item in line.Items)
                      {
                          if (item.FlexGrow > 0)
                          {
                              float share = (item.FlexGrow / line.TotalFlexGrow) * freeSpace;
                              item.TargetMainSize = item.FlexBaseSize + share;
                          }
                          else item.TargetMainSize = item.FlexBaseSize;
                      }
                      line.MainSize = mainAvailable; 
                 }
                 else if (freeSpace < 0 && line.TotalFlexShrink > 0)
                 {
                        // Simplified shrink
                        float totalWeightedShrink = 0;
                        foreach(var item in line.Items) totalWeightedShrink += item.FlexShrink * item.FlexBaseSize;
                        
                        if (totalWeightedShrink > 0) {
                            foreach(var item in line.Items) {
                                float ratio = (item.FlexShrink * item.FlexBaseSize) / totalWeightedShrink;
                                float shrinkAmount = Math.Abs(freeSpace) * ratio;
                                item.TargetMainSize = Math.Max(item.MinMain, item.FlexBaseSize - shrinkAmount);
                            }
                             line.MainSize = mainAvailable; 
                        } else {
                             foreach(var item in line.Items) item.TargetMainSize = item.FlexBaseSize;
                        }
                 }
                 else
                 {
                      foreach(var item in line.Items) 
                     {
                         item.TargetMainSize = Math.Max(item.MinMain, Math.Min(item.MaxMain, item.FlexBaseSize));
                     }
                 }
            }
            
            // Final Pass: Ensure min/max violations are resolved (simplified resolve loop)
            foreach (var line in lines)
            {
               foreach(var item in line.Items)
               {
                   item.TargetMainSize = Math.Max(item.MinMain, Math.Min(item.MaxMain, item.TargetMainSize));
               }
            }
            
            // 4. Cross Alignment
             float totalCrossSize = lines.Sum(l => l.CrossSize) + (crossGap * (lines.Count - 1));
             float crossFreeSpace = (isRow ? contentBox.Height : contentBox.Width) - totalCrossSize;
             
             if (lines.Count == 1 && crossFreeSpace > 0)
             {
                 lines[0].CrossSize += crossFreeSpace;
                 totalCrossSize += crossFreeSpace;
                 crossFreeSpace = 0;
             }
             
             float crossStartOffset = 0;
             float lineGap = crossGap;
              if (crossFreeSpace > 0 && lines.Count > 0)
            {
                switch (alignContent)
                {
                    case "center": crossStartOffset = crossFreeSpace / 2; break;
                    case "flex-end": crossStartOffset = crossFreeSpace; break;
                    case "space-between": if (lines.Count > 1) lineGap += crossFreeSpace / (lines.Count - 1); break;
                    case "space-around": 
                        if (lines.Count > 0) { float p = crossFreeSpace / lines.Count; crossStartOffset = p / 2; lineGap += p; } break;
                    case "stretch":
                         float add = crossFreeSpace / lines.Count;
                         foreach(var line in lines) line.CrossSize += add;
                         break;
                }
            }

            float crossPos = (isRow ? contentBox.Top : contentBox.Left) + crossStartOffset;
            
            // 5. Final Arrangement Loop
            if (isWrapReverse) lines.Reverse();

            foreach (var line in lines)
            {
                float lineCrossSize = line.CrossSize;
                
                // Calculate Baseline for this line if needed
                if (alignItems == "baseline" || line.Items.Any(i => (i.Style?.AlignSelf?.ToLowerInvariant()) == "baseline"))
                {
                    float maxAscent = 0;
                    foreach(var item in line.Items)
                    {
                        float childCrossSize = isRow
                            ? item.IntrinsicMetrics.ContentHeight
                            : item.IntrinsicMetrics.MaxChildWidth;
                        float ascent = ResolveFlexItemBaselineOffset(item, childCrossSize);
                        var m = item.Style?.Margin; 
                        if (m!=null) ascent += (float)(isRow ? m.Value.Top : m.Value.Left);
                        
                        maxAscent = Math.Max(maxAscent, ascent);
                    }
                    line.Baseline = maxAscent;
                }

                float freeMain = mainAvailable - line.MainSize;
                if (float.IsInfinity(freeMain) || float.IsNaN(freeMain)) freeMain = 0;
                
                float startOffset = 0;
                float itemGap = mainGap;

                if (freeMain > 0)
                {
                     if (container.TagName == "BODY")
                        FenLogger.Debug($"[FLEX-JUSTIFY] BODY Line={line.MainSize} Avail={mainAvailable} Free={freeMain} Justify={justify} StartOffset={startOffset}", LogCategory.Layout);

                     switch (justify)
                    {
                        case "center": startOffset = freeMain / 2; break;
                        case "flex-end": startOffset = freeMain; break;
                        case "space-between": if (line.Items.Count > 1) itemGap += freeMain / (line.Items.Count - 1); break;
                        case "space-around": 
                             if (line.Items.Count > 0) { float p = freeMain / line.Items.Count; startOffset = p / 2; itemGap += p; } break;
                        case "space-evenly":
                             if (line.Items.Count > 0) { float p = freeMain / (line.Items.Count + 1); startOffset = p; itemGap += p; } break;
                    }
                }
                else if (container.TagName == "BODY")
                {
                    FenLogger.Debug($"[FLEX-JUSTIFY-FAIL] BODY freeMain={freeMain} (Avail={mainAvailable} - Line={line.MainSize})", LogCategory.Layout);
                }
                
                float totalFlexGrow = 0;
                int autoMarginCount = 0;
                foreach(var item in line.Items)
                {
                    totalFlexGrow += item.FlexGrow;
                    var s = item.Style;
                    if (isRow) {
                        if (s?.MarginLeftAuto == true) autoMarginCount++;
                        if (s?.MarginRightAuto == true) autoMarginCount++;
                    } else {
                        if (s?.MarginTopAuto == true) autoMarginCount++;
                        if (s?.MarginBottomAuto == true) autoMarginCount++;
                    }
                }

                bool hasFlexGrow = totalFlexGrow > 0 && freeMain > 0;
                bool hasAutoMargins = autoMarginCount > 0 && freeMain > 0 && !hasFlexGrow;
                
                float growUnit = hasFlexGrow ? freeMain / totalFlexGrow : 0;
                float autoMarginUnit = hasAutoMargins ? freeMain / autoMarginCount : 0;
                
                if (hasFlexGrow || hasAutoMargins) 
                {
                    startOffset = 0;
                    itemGap = mainGap; 
                }

                float currentMainPos = (isRow ? contentBox.Left : contentBox.Top) + startOffset;

                foreach (var item in line.Items)
                {
                     var cStyle = item.Style;
                     var margin = cStyle?.Margin ?? new Thickness(0);
                     float mt = (float)margin.Top, mb = (float)margin.Bottom, ml = (float)margin.Left, mr = (float)margin.Right;
                     float childCrossMargins = isRow ? (mt + mb) : (ml + mr);
                     
                     float finalMain = item.TargetMainSize;
                     float childCrossSize = isRow ? item.IntrinsicMetrics.ContentHeight : item.IntrinsicMetrics.MaxChildWidth;
                     
                     // Align Items
                     float crossAvailable = lineCrossSize - childCrossMargins - childCrossSize;
                     float crossOffset = 0;
                     
                     string align = alignItems;
                     var alignSelf = cStyle?.AlignSelf?.ToLowerInvariant();
                     if (!string.IsNullOrEmpty(alignSelf) && alignSelf != "auto")
                     {
                         align = alignSelf;
                     }
                     
                     

                     
                     if (align == "center") crossOffset = crossAvailable / 2;
                     else if (align == "flex-end") crossOffset = crossAvailable;
                     else if (align == "baseline")
                     {
                         float itemAscent = ResolveFlexItemBaselineOffset(item, childCrossSize);
                         crossOffset = line.Baseline - itemAscent - mt;
                     }
                     
                     float finalCross = childCrossSize;
                     
                     // Helper for Cross-Axis Auto Margins
                     float crossAxisFreeSpace = lineCrossSize - childCrossSize - (isRow ? (mt + mb) : (ml + mr));
                     
                     // Detect Cross-Axis Auto Margins
                     bool crossAutoStart = isRow ? cStyle?.MarginTopAuto == true : cStyle?.MarginLeftAuto == true;
                     bool crossAutoEnd = isRow ? cStyle?.MarginBottomAuto == true : cStyle?.MarginRightAuto == true;
                     
                     // DIAGNOSTIC LOG for Alignment (Moved here to fix build error)
                     if (align == "center" || isRow==false) 
                     {
                          FenLogger.Debug($"[FLEX-ALIGN] Item={item.Node.NodeName} Align={align} " +
                                       $"CrossAvailable={crossAvailable} (Line={lineCrossSize} - Child={childCrossSize} - Margins={childCrossMargins}) " +
                                       $"AutoStart={crossAutoStart} AutoEnd={crossAutoEnd}", LogCategory.Layout);
                     }
                     
                     if (crossAutoStart && crossAutoEnd)
                     {
                         // split space
                         float space = Math.Max(0, crossAxisFreeSpace);
                         if (isRow) { mt += space / 2; mb += space / 2; }
                         else       { ml += space / 2; mr += space / 2; }
                         // disable align-items behavior (margin auto takes precedence)
                         crossOffset = 0; 
                         align = "auto-margin"; // override to skip alignment logic
                         finalCross = childCrossSize; // disable stretch
                     }
                     else if (crossAutoStart)
                     {
                         // push to end
                         float space = Math.Max(0, crossAxisFreeSpace);
                         if (isRow) mt += space;
                         else       ml += space;
                         crossOffset = 0;
                         align = "auto-margin";
                         finalCross = childCrossSize;
                     }
                     else if (crossAutoEnd)
                     {
                         // push to start (space goes to end margin)
                         float space = Math.Max(0, crossAxisFreeSpace);
                         if (isRow) mb += space;
                         else       mr += space;
                         crossOffset = 0; // standard start alignment
                         align = "auto-margin";
                         finalCross = childCrossSize;
                     }

                     if (align == "stretch" && crossAvailable > 0) 
                     {
                         FenLogger.Info($"[FLEX-STRETCH] Item stretch: childCross={childCrossSize:F1} crossAvail={crossAvailable:F1} lineCross={lineCrossSize:F1} -> finalCross={childCrossSize + crossAvailable:F1}", LogCategory.Layout);
                         finalCross += crossAvailable;
                     }
                     
                     // Enforce Cross-Axis Constraints
                     finalCross = Math.Max(item.MinCross, Math.Min(item.MaxCross, finalCross));
                     
                     float x, y, w, h;
                     
                     float extraLeft=0, extraTop=0;
                     if (hasAutoMargins) {
                         // ... existing Main Axis logic ...
                         if (isRow && cStyle?.MarginLeftAuto==true) extraLeft = autoMarginUnit;
                         if (!isRow && cStyle?.MarginTopAuto==true) extraTop = autoMarginUnit;
                     }

                     if (isRow)
                     {
                         x = currentMainPos + ml + extraLeft;
                         y = crossPos + mt + crossOffset;
                         w = finalMain;
                         h = finalCross;
                     }
                     else
                     {
                          x = crossPos + ml + crossOffset;
                          y = currentMainPos + mt + extraTop;
                          // CRITICAL FIX: Clamp width to container's cross-axis size
                          // For column flex, cross-axis is width. Ensure it doesn't exceed content box.
                          float maxCrossWidth = Math.Max(0, contentBox.Width - ml - mr);
                          w = Math.Min(finalCross, maxCrossWidth);
                          h = finalMain;
                     }
                     
                     arrangeChild(item.Node, new SKRect(x, y, x + w, y + h), depth + 1);
                     
                     float extraMainMargin = 0;
                     if (hasAutoMargins) {
                         if (isRow && cStyle?.MarginRightAuto==true) extraMainMargin = autoMarginUnit;
                         if (!isRow && cStyle?.MarginBottomAuto==true) extraMainMargin = autoMarginUnit;
                     }

                     float itemSize = finalMain + (isRow ? (ml + mr) : (mt + mb)) + extraLeft + extraMainMargin;
                     currentMainPos += itemSize + itemGap;
                }
                
                crossPos += lineCrossSize + lineGap;
            }
        }

        private static float ResolveFlexItemBaselineOffset(FlexItem item, float childCrossSize)
        {
            float usedCross = float.IsFinite(childCrossSize) && childCrossSize > 0f ? childCrossSize : 0f;
            if (usedCross <= 0f)
            {
                return 0f;
            }

            // Direct text nodes use measured baseline or text fallback.
            if (item.Node is Text textNode)
            {
                if (!string.IsNullOrWhiteSpace(textNode.Data))
                {
                    float textBaseline = item.IntrinsicMetrics.Baseline;
                    if (!float.IsFinite(textBaseline) || textBaseline <= 0f)
                    {
                        textBaseline = usedCross * 0.8f;
                    }

                    return Math.Clamp(textBaseline, 0f, usedCross);
                }

                return usedCross;
            }

            // For element-backed flex items, prefer measured baseline only for explicit inline-level formatting.
            string display = item.Style?.Display?.Trim().ToLowerInvariant() ?? string.Empty;
            bool inlineLevel =
                display == "inline" ||
                display == "inline-block" ||
                display == "inline-flex" ||
                display == "inline-grid" ||
                display == "inline-table";

            if (inlineLevel)
            {
                float measuredBaseline = item.IntrinsicMetrics.Baseline;
                if (float.IsFinite(measuredBaseline) && measuredBaseline > 0f)
                {
                    return Math.Clamp(measuredBaseline, 0f, usedCross);
                }
            }

            // Block/replaced element fallback: baseline at lower border edge.
            return usedCross;
        }

        private static bool HasExplicitMainSize(CssComputed style, bool isRow)
        {
            if (style == null)
            {
                return false;
            }

            return isRow
                ? style.Width.HasValue || style.WidthPercent.HasValue || !string.IsNullOrEmpty(style.WidthExpression)
                : style.Height.HasValue || style.HeightPercent.HasValue || !string.IsNullOrEmpty(style.HeightExpression);
        }

        private static bool HasContainerRelativeMainSize(CssComputed style, bool isRow)
        {
            if (style == null)
            {
                return false;
            }

            return isRow
                ? style.WidthPercent.HasValue || !string.IsNullOrEmpty(style.WidthExpression)
                : style.HeightPercent.HasValue || !string.IsNullOrEmpty(style.HeightExpression);
        }

    }
}
