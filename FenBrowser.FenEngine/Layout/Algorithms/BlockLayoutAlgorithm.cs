using System;
using System.Linq;
using System.Collections.Generic;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core;
using SkiaSharp;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Layout.Coordinates; 
using FenBrowser.Core.Logging;
using FenBrowser.Core.Deadlines;

namespace FenBrowser.FenEngine.Layout.Algorithms
{
    public class BlockLayoutAlgorithm : ILayoutAlgorithm
    {
        public LayoutMetrics Measure(LayoutContext context)
        {
            var element = context.Node as Element;
            var fallbackNode = context.FallbackNode;

            // PSEUDO-ELEMENT AWARENESS
            var childrenSource = LayoutHelpers.GetChildrenWithPseudos(element, fallbackNode, context.Computer).ToList();
            if (childrenSource == null || childrenSource.Count == 0) return new LayoutMetrics();

            // IFC CHECK: Pure IFC handling (Optimization)
            bool hasBlock = false;
            foreach (var c in childrenSource)
            {
                 if (c is Text t && string.IsNullOrWhiteSpace(t.Data)) continue;
                 if (!LayoutHelpers.IsInlineLevel(c, context.Computer)) 
                 {
                     hasBlock = true; 
                     break;
                 }
            }

            if (!hasBlock && element != null)
            {
                // Pure Inline Context - Delegate completely
                FenLogger.Debug($"[LAYOUT-TRACE] PURE IFC shortcut taken for {element.TagName} (ID={element.GetAttribute("id")}). Delegating to InlineLayoutComputer. AvailWidth={context.AvailableSize.Width}");
                return context.Computer.MeasureInlineContextInternal(element, context.AvailableSize, context.Depth); 
            }

            // BFC Logic with Mixed Content (Run-based)
            string writingMode = context.Style?.WritingMode ?? "horizontal-tb";
            LogicalSize logicalAvailable = WritingModeConverter.ToLogical(context.AvailableSize, writingMode);

            float logicalCurBlock = 0;
            float logicalMaxInline = 0;
            float currentFloatBlockSize = 0;
            float floatInlineCursor = 0;
            
            // Padding/Border adjustment for child constraint
            float inlineOffset = 0;
            if (context.Style != null) 
            {
                 var logPadding = WritingModeConverter.ToLogicalMargin(context.Style.Padding, writingMode);
                 var logBorder = WritingModeConverter.ToLogicalMargin(context.Style.BorderThickness, writingMode);
                 inlineOffset = logPadding.InlineStart + logPadding.InlineEnd + logBorder.InlineStart + logBorder.InlineEnd;
            }
            float logicalAvailableInline = Math.Max(0, logicalAvailable.Inline - inlineOffset);

            if (context.Style != null) LayoutHelpers.ApplyContainerWidthConstraints(context.Style, writingMode, inlineOffset, ref logicalAvailableInline);
            
            var className = element?.GetAttribute("class");
            if (element != null && element.TagName == "DIV")
            {
                FenLogger.Info($"[STYLE-DEBUG] Element {element.TagName}.{className}: Width={context.Style?.Width}, MaxWidth={context.Style?.MaxWidth}, TextAlign={context.Style?.TextAlign}, AvailableInline={logicalAvailableInline}", LogCategory.Layout);
            }

            bool parentPreventCollapse = (context.Style?.Padding.Top ?? 0) > 0 || (context.Style?.BorderThickness.Top ?? 0) > 0;
            
            MarginPair currentMarginGroup = new MarginPair();
            MarginPair bubbledMarginTop = new MarginPair();
            bool inInitialMarginGroup = !parentPreventCollapse;

            List<Node> currentInlineRun = new List<Node>();

            void FlushInlineRun()
            {
                if (currentInlineRun.Count == 0) return;

                // 1. Measure Run
                // FIX: Guard against Infinite Width for Inline Context if needed? 
                // InlineLayoutComputer works better with finite width, but handles wrapping.
                var result = InlineLayoutComputer.Compute(
                    element, 
                    new SKSize(WritingModeConverter.ToPhysical(new LogicalSize(logicalAvailableInline, logicalAvailable.Block), writingMode).Width, float.PositiveInfinity),
                    context.Computer.GetStyleInternal,
                    context.Computer.MeasureNodePublic,
                    context.Depth + 1,
                    context.Exclusions,
                    currentInlineRun
                );

                // 2. Determine if this run HAS content that prevents collapsing
                if (result.Metrics.ContentHeight > 0.01f || result.ElementRects.Count > 0)
                {
                    // This run separates margins above and below
                    if (inInitialMarginGroup)
                    {
                        bubbledMarginTop = currentMarginGroup;
                        inInitialMarginGroup = false;
                    }
                    else
                    {
                        logicalCurBlock += currentMarginGroup.Collapsed;
                    }

                    // 3. Add Height
                    logicalCurBlock += result.Metrics.ContentHeight;
                    logicalMaxInline = Math.Max(logicalMaxInline, result.Metrics.MaxChildWidth);

                    // 4. Reset margin group
                    currentMarginGroup = new MarginPair();
                }
                
                // Clear Run
                currentInlineRun.Clear();
            }

            foreach (var child in childrenSource)
            {
                context.Deadline?.Check();

                var childStyle = context.Computer.GetStyleInternal(child);
                if (LayoutHelpers.ShouldHide(child, childStyle)) continue;
                // Skip empty text nodes if they are direct children of BFC (but usually they should be part of inline run?)
                // If it's whitespace only, and we are in BFC mode, it usually collapses unless PRE.
                // But if we are building runs, we should include it? 
                // Existing logic skipped them. Let's skip pure whitespace at BFC level to avoid empty runs.
                if (child is Text txt && string.IsNullOrWhiteSpace(txt.Data)) continue;

                string pos = childStyle?.Position?.ToLowerInvariant();
                bool isAbs = pos == "absolute" || pos == "fixed";

                if (isAbs)
                {
                    // Absolutes don't break runs, but also don't belong to runs usually? 
                    // They are 0-sized in flow. 
                    // Existing logic measured them.
                    var absMetrics = context.Computer.MeasureNodePublic(child, context.AvailableSize, context.Depth + 1);
                    context.Computer.SetDesiredSize(child, new SKSize(absMetrics.MaxChildWidth, absMetrics.ContentHeight));
                    continue;
                }

                bool isFloat = childStyle?.Float?.ToLowerInvariant() == "left" || childStyle?.Float?.ToLowerInvariant() == "right";
                bool isInlineLevel = LayoutHelpers.IsInlineLevel(child, context.Computer) && !isFloat;

                if (child is Element tel && tel.TagName == "A")
                {
                     FenLogger.Debug($"[LAYOUT-TRACE] Found <A> tag. IsInline={isInlineLevel} Float={childStyle?.Float} Display={childStyle?.Display} Pos={pos}");
                }

                if (isInlineLevel)
                {
                    currentInlineRun.Add(child);
                    continue;
                }

                // Block-level or Float encountered -> Flush previous run
                if (child is Element tel2 && tel2.TagName == "A")
                {
                     FenLogger.Debug($"[LAYOUT-TRACE] Flushing run due to <A> tag being block/float? RunCount={currentInlineRun.Count}");
                }
                FlushInlineRun();

                LayoutMetrics childMetrics;
                LogicalSize childLogSize;
                
                var childMargin = childStyle?.Margin ?? new Thickness(0);
                LogicalMargin logicalMargin = WritingModeConverter.ToLogicalMargin(childMargin, writingMode);

                float childInlineConstraint = logicalAvailableInline;
                var physicalConstraint = WritingModeConverter.ToPhysical(new LogicalSize(childInlineConstraint, logicalAvailable.Block), writingMode);
                
                childMetrics = context.Computer.MeasureNodePublic(child, physicalConstraint, context.Depth + 1);
                
                var childPhysSize = new SKSize(childMetrics.MaxChildWidth, childMetrics.ContentHeight);
                childLogSize = WritingModeConverter.ToLogical(childPhysSize, writingMode);

                if (isFloat)
                {
                    // Float Logic (Existing, but cleaned up)
                    // Floats are technically siblings to the anonymous block if they are between runs.
                    // Or they are part of the IFC if they are inside the run.
                    // If we are here, 'isFloat' is true. 'FlushInlineRun' happened.
                    // So this float stands alone between runs.
                    
                    logicalCurBlock += currentMarginGroup.Collapsed;
                    currentMarginGroup = new MarginPair();
                    inInitialMarginGroup = false;

                    float fullChildInline = childLogSize.Inline + logicalMargin.InlineStart + logicalMargin.InlineEnd;
                    float fullChildBlock = childLogSize.Block + logicalMargin.BlockStart + logicalMargin.BlockEnd;
                    
                    if (floatInlineCursor + fullChildInline > logicalAvailableInline && floatInlineCursor > 0)
                    {
                        logicalCurBlock += currentFloatBlockSize;
                        floatInlineCursor = 0;
                        currentFloatBlockSize = 0;
                    }
                    
                    // Register Float Exclusion
                    if (context.Exclusions != null)
                    {
                        // Simplified Float Placement (Block-Level Float)
                        float floatX = 0;
                        // FIX: Guard for Right Float with Infinite Width
                        if (childStyle?.Float?.ToLowerInvariant() == "right") 
                        {
                            if (float.IsInfinity(logicalAvailableInline)) 
                                floatX = floatInlineCursor; 
                            else
                                floatX = logicalAvailableInline - floatInlineCursor - fullChildInline;
                        }
                        else
                        {
                            floatX = floatInlineCursor;
                        }
                        
                        // FIX: Ensure floatX is finite
                        if (float.IsNaN(floatX) || float.IsInfinity(floatX)) floatX = 0;
                                     
                        var floatRect = new SKRect(floatX, logicalCurBlock, floatX + fullChildInline, logicalCurBlock + fullChildBlock);
                        bool isLeft = childStyle?.Float?.ToLowerInvariant() != "right";
                        context.Exclusions.Add(FloatExclusion.CreateFromStyle(floatRect, isLeft, childStyle));
                    }

                    floatInlineCursor += fullChildInline;
                    currentFloatBlockSize = Math.Max(currentFloatBlockSize, fullChildBlock);
                    logicalMaxInline = Math.Max(logicalMaxInline, floatInlineCursor);
                }
                else
                {

                    // Regular Block Child
                    if (floatInlineCursor > 0)
                    {
                        logicalCurBlock += currentFloatBlockSize;
                        floatInlineCursor = 0;
                        currentFloatBlockSize = 0;
                    }
                    
                    // CLEARANCE CHECK
                     if (context.Exclusions != null && childStyle != null && !string.IsNullOrEmpty(childStyle.Clear) && childStyle.Clear != "none")
                    {
                         float clearY = 0;
                         bool needsClearance = false;
                         string clear = childStyle.Clear.ToLowerInvariant();
                         
                         foreach(var exc in context.Exclusions)
                         {
                             bool relevant = (clear == "both") || (clear == "left" && exc.IsLeft) || (clear == "right" && !exc.IsLeft);
                             if (relevant)
                             {
                                 if (exc.FloatingRect.Bottom > clearY) 
                                 {
                                     clearY = exc.FloatingRect.Bottom;
                                     needsClearance = true;
                                 }
                             }
                         }
                         
                         // Only apply if needed
                         if (needsClearance && clearY > logicalCurBlock)
                         {
                             logicalCurBlock += currentMarginGroup.Collapsed;
                             currentMarginGroup = new MarginPair();
                             inInitialMarginGroup = false;
                             
                             if (clearY > logicalCurBlock) logicalCurBlock = clearY;
                         }
                    }

                    logicalMaxInline = Math.Max(logicalMaxInline, childLogSize.Inline);

                    // Margin Collapsing with Child
                    // 1. Combine our current accumulated bottom margin with child's top margin
                    currentMarginGroup.Combine(childMetrics.MarginTop); 
                    currentMarginGroup.Combine(logicalMargin.BlockStart);

                    // 2. Decide if we collapse THROUGH this child (child is empty block)
                    if (MarginCollapseComputer.ShouldCollapseThrough(childStyle, childLogSize.Block))
                    {
                         currentMarginGroup.Combine(childMetrics.MarginBottom);
                         currentMarginGroup.Combine(logicalMargin.BlockEnd);
                    }
                    else
                    {
                         // Child has height/border/padding, so it breaks the margin collapse chain.
                         // We must Place the child now.
                         
                         if (inInitialMarginGroup)
                         {
                              // If we are at the top of the container, the top margin bubbles up to parent.
                              // So we don't add it to CurBlock.
                              bubbledMarginTop = currentMarginGroup;
                              inInitialMarginGroup = false;
                              // But wait, if we don't add it, where does the child sit?
                              // It sits at 0 (relative to content box).
                         }
                         else
                         {
                              // Not in initial group -> we must apply the collapsed margin between previous sibling and this child.
                              logicalCurBlock += currentMarginGroup.Collapsed;
                         }
                         
                         // Advance for Child Height
                         logicalCurBlock += childLogSize.Block;
                         
                         // Start new margin group for bottom
                         currentMarginGroup = new MarginPair();
                         currentMarginGroup.Combine(childMetrics.MarginBottom);
                         currentMarginGroup.Combine(logicalMargin.BlockEnd);
                    }
                }
            }

            // Flush failing/trailing run
            FlushInlineRun();
            
            // Final Margin handling
            MarginPair bubbledMarginBottom = new MarginPair();
            bool parentPreventCollapseBottom = (context.Style?.Padding.Bottom ?? 0) > 0 || (context.Style?.BorderThickness.Bottom ?? 0) > 0 || context.Style?.Height.HasValue == true;

            if (parentPreventCollapseBottom)
            {
                 logicalCurBlock += currentMarginGroup.Collapsed;
            }
            else
            {
                 if (inInitialMarginGroup) 
                 {
                      bubbledMarginTop = currentMarginGroup;
                      bubbledMarginBottom = bubbledMarginTop;
                 }
                 else
                 {
                      bubbledMarginBottom = currentMarginGroup;
                 }
            }

            logicalCurBlock += currentFloatBlockSize;

            var finalLogSize = new LogicalSize(logicalMaxInline, logicalCurBlock);
            var finalPhysSize = WritingModeConverter.ToPhysical(finalLogSize, writingMode);

            return new LayoutMetrics {
                ContentHeight = finalPhysSize.Height,
                ActualHeight = finalPhysSize.Height,
                MaxChildWidth = finalPhysSize.Width,
                MarginTop = bubbledMarginTop.Collapsed,
                MarginBottom = bubbledMarginBottom.Collapsed,
                MarginTopPos = bubbledMarginTop.Positive,
                MarginTopNeg = bubbledMarginTop.Negative,
                MarginBottomPos = bubbledMarginBottom.Positive,
                MarginBottomNeg = bubbledMarginBottom.Negative
            };
        }

        public void Arrange(LayoutContext context, SKRect finalRect)
        {
            // FIX: Guard against NaN input for Arrange
            if (float.IsNaN(finalRect.Left) || float.IsNaN(finalRect.Top) || float.IsNaN(finalRect.Width) || float.IsNaN(finalRect.Height))
            {
                FenLogger.Error($"[BLOCK-ARRANGE-ERROR] Received NaN finalRect for {context.Node?.TagName}: {finalRect}");
                return;
            }

            FenLogger.Error($"[BLOCK-ARRANGE-START] Node={context.Node?.NodeName} children={(context.Node?.Children?.Length ?? 0)} finalRect={finalRect}");
            // RE-IMPLEMENTATION of Arrange logic to support Anonymous Blocks
            // We must replicate the exact flow logic from Measure to position elements correctly.
            var element = context.Node as Element;
            var fallbackNode = context.FallbackNode;

            var childrenSource = LayoutHelpers.GetChildrenWithPseudos(element, fallbackNode, context.Computer).ToList();
            if (childrenSource == null || childrenSource.Count == 0) return;

             // Pure IFC Handling
            bool hasBlock = false;
            foreach (var c in childrenSource)
            {
                 if (c is Text t && string.IsNullOrWhiteSpace(t.Data)) continue;
                 if (!LayoutHelpers.IsInlineLevel(c, context.Computer)) 
                 {
                     hasBlock = true; 
                     break;
                 }
            }
            if (!hasBlock && element != null)
            {
                 // Guard for null element before delegating to IFC arrange
                 if (element == null) return;
                 context.Computer.ArrangeBlockInternalInternal(element, finalRect, context.Depth, fallbackNode);
                 return;
            }

            // BFC Logic
            string writingMode = context.Style?.WritingMode ?? "horizontal-tb";
            LogicalSize logicalSize = WritingModeConverter.ToLogical(finalRect.Size, writingMode);
            
            float logicalCurBlock = 0;
            float currentFloatBlockSize = 0;
            float floatInlineCursor = 0;
            
            float inlineOffset = 0;
            if (context.Style != null) 
            {
                  var logPadding = WritingModeConverter.ToLogicalMargin(context.Style.Padding, writingMode);
                  var logBorder = WritingModeConverter.ToLogicalMargin(context.Style.BorderThickness, writingMode);
                  
                  // FIX: Do NOT add inlineOffset here. finalRect is ContentBox. 
                  // Subtracting padding/border from ContentBox size reduces available width incorrectly (causing wrap).
                  inlineOffset = 0; 
                  
                  // inlineOffset = logPadding.InlineStart + logPadding.InlineEnd + logBorder.InlineStart + logBorder.InlineEnd;
                  
                  // logicalCurBlock starts at 0 relative to the provided finalRect content-box coords.
                  // We don't add padding here because finalRect is already the ContentBox.
            }
            // Note: In Measure, logicalCurBlock started at 0 (content-relative). 
            // In Arrange, we assume finalRect includes padding/border area?? 
            // Usually 'finalRect' passed to Arrange is the Border Box.
            
            // Wait, BlockLayoutAlgorithm.Measure returned 'ContentHeight'.
            // Arrange receives the Border Box.
            // So we must offset by Top Padding/Border to map 'logicalCurBlock=0' to reality.
            
            // Available width for children (Logical)
            float logicalAvailableInline = logicalSize.Inline - inlineOffset;
            if (context.Style != null) LayoutHelpers.ApplyContainerWidthConstraints(context.Style, writingMode, inlineOffset, ref logicalAvailableInline);


            bool parentPreventCollapse = (context.Style?.Padding.Top ?? 0) > 0 || (context.Style?.BorderThickness.Top ?? 0) > 0;
            MarginPair currentMarginGroup = new MarginPair();
            bool inInitialMarginGroup = !parentPreventCollapse;

            List<Node> currentInlineRun = new List<Node>();

            void FlushInlineRun()
            {
                if (currentInlineRun.Count == 0) return;

                // 1. Compute Layout for Run
                 var result = InlineLayoutComputer.Compute(
                    element, 
                    new SKSize(WritingModeConverter.ToPhysical(new LogicalSize(logicalAvailableInline, float.PositiveInfinity), writingMode).Width, float.PositiveInfinity),
                    context.Computer.GetStyleInternal,
                    context.Computer.MeasureNodePublic,
                    context.Depth + 1,
                    context.Exclusions,
                    currentInlineRun
                );

                // 2. Determine if this run HAS content that prevents collapsing
                if (result.Metrics.ContentHeight > 0.01f || result.ElementRects.Count > 0)
                {
                    if (inInitialMarginGroup)
                    {
                        inInitialMarginGroup = false;
                    }
                    else
                    {
                        float oldVal = logicalCurBlock;
                        logicalCurBlock += currentMarginGroup.Collapsed;
                        // FenLogger.Log($"[ICB-OVERLAP] Child {i} ({child.NodeName}) Advancing CurBlock by collapsed margin {currentMarginGroup.Collapsed}: {oldVal} -> {logicalCurBlock}", LogCategory.Layout);
                    }
                    
                    float startInline = 0;
                     if (context.Style != null) 
                     {
                         var logPadding = WritingModeConverter.ToLogicalMargin(context.Style.Padding, writingMode);
                         var logBorder = WritingModeConverter.ToLogicalMargin(context.Style.BorderThickness, writingMode);
                         // FIX: Do NOT add padding to startInline. finalRect is ContentBox.
                         // Adding padding shifts content right (Double Padding).
                         startInline = 0;
                         // startInline = logPadding.InlineStart + logBorder.InlineStart;
                     }

                    float blockOffset = logicalCurBlock;
                    
                    // Elements
                    foreach(var kvp in result.ElementRects)
                    {
                         var node = kvp.Key;
                         var localRect = kvp.Value;
                         float absX = finalRect.Left + startInline + localRect.Left;
                         float absY = finalRect.Top + blockOffset + localRect.Top;
                         var absRect = new SKRect(absX, absY, absX + localRect.Width, absY + localRect.Height);
                         context.Computer.Arrange(node, absRect);
                    }
                    
                    // Text Lines
                    foreach(var kvp in result.TextLines)
                    {
                        var node = kvp.Key;
                        var lines = kvp.Value.Select(l => {
                            var ol = l; 
                            var origin = ol.Origin;
                            // Fix: Origin should be relative to the container (finalRect), NOT absolute.
                            // NewPaintTreeBuilder adds box.ContentBox.Left/Top (which is finalRect.Left/Top).
                            ol.Origin = new SKPoint(
                                startInline + origin.X,
                                blockOffset + origin.Y
                            );
                            return ol;
                        }).ToList();
                        context.Computer.RegisterTextLines(node, lines);
                        
                        // Fix: Ensure BoxModel is created for text node so renderer can find it
                        FenBrowser.Core.FenLogger.Info($"[ARRANGE-TEXT-DEBUG] Node={node} FinalRect={finalRect} BlockOffset={blockOffset} LineCount={lines.Count} FirstLineY={(lines.Count>0?lines[0].Origin.Y:-999)}", FenBrowser.Core.Logging.LogCategory.Layout);
                        context.Computer.ArrangeText(node, finalRect); // finalRect is ContentBox
                    }

                    // Advance
                    logicalCurBlock += result.Metrics.ContentHeight;
                    currentMarginGroup = new MarginPair();
                }
                
                currentInlineRun.Clear();
            }

            for (int i = 0; i < childrenSource.Count; i++) // Added loop index 'i'
            {
                 var child = childrenSource[i]; // Get child using index
                 var childStyle = context.Computer.GetStyleInternal(child);
                 if (LayoutHelpers.ShouldHide(child, childStyle)) continue;
                 if (child is Text txt && string.IsNullOrWhiteSpace(txt.Data)) continue;

                 string pos = childStyle?.Position?.ToLowerInvariant();
                 bool isAbs = pos == "absolute" || pos == "fixed";

                 if (isAbs)
                 {
                      context.Computer.Arrange(child, SKRect.Empty); 
                      continue;
                 }

                 bool isFloat = childStyle?.Float?.ToLowerInvariant() == "left" || childStyle?.Float?.ToLowerInvariant() == "right";
                 bool isInlineLevel = LayoutHelpers.IsInlineLevel(child, context.Computer) && !isFloat;
                
                 if (child is Element tel3 && tel3.TagName == "A")
                {
                     FenLogger.Debug($"[ARRANGE-TRACE] Found <A> tag. IsInline={isInlineLevel}");
                }

                 if (isInlineLevel)
                 {
                      currentInlineRun.Add(child);
                      continue;
                 }

                 FlushInlineRun();

                 // Block/Float Child Logic
                 var childMargin = childStyle?.Margin ?? new Thickness(0);
                 LogicalMargin logicalMargin = WritingModeConverter.ToLogicalMargin(childMargin, writingMode);
                 
                 // Re-fetch size (from cache hopefully)
                 // removed incorrect GetBox call
                 // Note: MinimalLayoutComputer stores Measure result? No, relies on "ArrangeBlockInternal" re-measuring?
                 // MinimalLayoutComputer's ArrangeBlockInternal doesn't re-measure explicitly, 
                 // but relies on Measure being called before.
                 // We need the size we computed in Measure.
                 // MinimalLayoutComputer uses _desiredSizes? Line 316.
                 // But BlockLayoutAlgorithm calls context.Computer.MeasureNodePublic which might cache.
                 // Let's assume Measure was called and we can proceed.
                 // Wait, we need the EXACT size layout used.
                 // For now, re-measure to be safe/consistent with this algorithm style.
                 
                 float childInlineConstraint = logicalAvailableInline;
                 var physicalConstraint = WritingModeConverter.ToPhysical(new LogicalSize(childInlineConstraint, logicalSize.Block), writingMode);
                 var childMetrics = context.Computer.MeasureNodePublic(child, physicalConstraint, context.Depth + 1);
                 
                 var childLogSize = WritingModeConverter.ToLogical(new SKSize(childMetrics.MaxChildWidth, childMetrics.ContentHeight), writingMode);

                 if (isFloat)
                 {
                      logicalCurBlock += currentMarginGroup.Collapsed;
                      currentMarginGroup = new MarginPair();
                      inInitialMarginGroup = false;

                      float fullChildInline = childLogSize.Inline + logicalMargin.InlineStart + logicalMargin.InlineEnd;
                      float fullChildBlock = childLogSize.Block + logicalMargin.BlockStart + logicalMargin.BlockEnd;

                       if (floatInlineCursor + fullChildInline > logicalAvailableInline && floatInlineCursor > 0)
                        {
                            logicalCurBlock += currentFloatBlockSize;
                            floatInlineCursor = 0;
                            currentFloatBlockSize = 0;
                        }

                        // Placement
                        // Need float X relative to border box
                        float startInline = 0;
                         if (context.Style != null) 
                        {
                             var logPadding = WritingModeConverter.ToLogicalMargin(context.Style.Padding, writingMode);
                             var logBorder = WritingModeConverter.ToLogicalMargin(context.Style.BorderThickness, writingMode);
                             startInline = logPadding.InlineStart + logBorder.InlineStart;
                        }

                        float floatX = (childStyle?.Float?.ToLowerInvariant() == "right") 
                                     ? (logicalAvailableInline - floatInlineCursor - fullChildInline) 
                                     : floatInlineCursor;
                        
                        // Abs X = finalRect.Left + startInline + floatX + MarginLeft (logic)
                        // Note: floatX logic above assumed content box relative.
                        // For 'Left': floatX = floatInlineCursor.
                        // If floatInlineCursor = 0, floatX = 0.
                        // AbsX = finalRect.Left + startInline + 0 + MarginLeft.
                        
                        float absY = finalRect.Top + logicalCurBlock + logicalMargin.BlockStart; // Top margin
                        float absX = finalRect.Left + startInline + floatX + logicalMargin.InlineStart;

                        var finalChildRect = new SKRect(absX, absY, absX + childLogSize.Inline, absY + childLogSize.Block);
                        context.Computer.Arrange(child, finalChildRect);

                        floatInlineCursor += fullChildInline;
                        currentFloatBlockSize = Math.Max(currentFloatBlockSize, fullChildBlock);

                 }
                 else
                 {
                      if (floatInlineCursor > 0)
                        {
                            logicalCurBlock += currentFloatBlockSize;
                            floatInlineCursor = 0;
                            currentFloatBlockSize = 0;
                        }

                       // Clearance
                       if (context.Exclusions != null && childStyle != null && !string.IsNullOrEmpty(childStyle.Clear) && childStyle.Clear != "none")
                       {
                            float clearY = 0;
                            bool needsClearance = false;
                            string clear = childStyle.Clear.ToLowerInvariant();
                             foreach(var exc in context.Exclusions)
                             {
                                 bool relevant = (clear == "both") || (clear == "left" && exc.IsLeft) || (clear == "right" && !exc.IsLeft);
                                 if (relevant && exc.FloatingRect.Bottom > clearY) { clearY = exc.FloatingRect.Bottom; needsClearance = true; }
                             }
                             if (needsClearance && clearY > logicalCurBlock)
                             {
                                 logicalCurBlock += currentMarginGroup.Collapsed;
                                 currentMarginGroup = new MarginPair();
                                 inInitialMarginGroup = false;
                                 if (clearY > logicalCurBlock) logicalCurBlock = clearY;
                             }
                       }

                       // Margin collapse with children
                       currentMarginGroup.Combine(childMetrics.MarginTop);
                       currentMarginGroup.Combine(logicalMargin.BlockStart);

                       if (MarginCollapseComputer.ShouldCollapseThrough(childStyle, childLogSize.Block))
                       {
                           currentMarginGroup.Combine(childMetrics.MarginBottom);
                           currentMarginGroup.Combine(logicalMargin.BlockEnd);
                       }
                       else
                       {
                            // Place child now.
                            // Only non-initial groups advance by the collapsed inter-sibling margin.
                            if (inInitialMarginGroup)
                            {
                                inInitialMarginGroup = false;
                            }
                            else
                            {
                                logicalCurBlock += currentMarginGroup.Collapsed;
                            }

                             float startInline = 0;
                            // finalRect is already content-box coordinates; no extra padding/border offset here.

                            float absX = finalRect.Left + startInline + logicalMargin.InlineStart;
                            float absY = finalRect.Top + logicalCurBlock; 

                           var finalChildRect = new SKRect(absX, absY, absX + childLogSize.Inline, absY + childLogSize.Block);
                           context.Computer.Arrange(child, finalChildRect);
                           
                           // Advance CurBlock past the element
                           logicalCurBlock += childLogSize.Block;
                           
                           // Start new margin group for bottom
                           currentMarginGroup = new MarginPair();
                           currentMarginGroup.Combine(childMetrics.MarginBottom);
                           currentMarginGroup.Combine(logicalMargin.BlockEnd);
                       }
                 }
            }

            FlushInlineRun();
        }
    }
}

