using System;
using System.Linq;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Css;
using FenBrowser.Core; // Added for Thickness
using SkiaSharp;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Layout.Coordinates; 
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Layout; // Added

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

            // IFC CHECK
            bool useIFC = false;
            bool hasBlock = false;
            bool hasInline = false;
            
            foreach (var c in childrenSource)
            {
                 if (c is Text t && string.IsNullOrWhiteSpace(t.Data)) continue;
                 
                 bool isInline = LayoutHelpers.IsInlineLevel(c, context.Computer);
                 if (isInline) hasInline = true;
                 else hasBlock = true;
            }
            
            useIFC = hasInline && !hasBlock;

            if (useIFC && element != null)
            {
                return context.Computer.MeasureInlineContextInternal(element, context.AvailableSize, context.Depth); 
            }

            // BFC Logic
            string writingMode = context.Style?.WritingMode ?? "horizontal-tb";
            LogicalSize logicalAvailable = WritingModeConverter.ToLogical(context.AvailableSize, writingMode);

            float logicalCurBlock = 0;
            float logicalMaxInline = 0;
            float currentFloatBlockSize = 0;
            float floatInlineCursor = 0;
            bool first = true;
             
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

            float internalBlockMarginStart = 0;
            float internalBlockMarginEnd = 0;
            
            float previousMarginBottom = 0;
            // Fix nullable comparison
            bool parentPreventCollapse = (context.Style?.Padding.Top ?? 0) > 0 || (context.Style?.BorderThickness.Top ?? 0) > 0;
            
            foreach (var child in childrenSource)
            {
                var childStyle = context.Computer.GetStyleInternal(child);
                if (LayoutHelpers.ShouldHide(child, childStyle)) continue;
                if (child is Text txt && string.IsNullOrWhiteSpace(txt.Data)) continue;

                // Position check
                string pos = childStyle?.Position?.ToLowerInvariant();
                bool isAbs = pos == "absolute" || pos == "fixed";

                if (isAbs)
                {
                     var absMetrics = context.Computer.MeasureNodePublic(child, context.AvailableSize, context.Depth + 1);
                     context.Computer.SetDesiredSize(child, new SKSize(absMetrics.MaxChildWidth, absMetrics.ContentHeight));
                     continue;
                }

                LayoutMetrics childMetrics;
                LogicalSize childLogSize;
                
                var childMargin = childStyle?.Margin ?? new Thickness(0);
                LogicalMargin logicalMargin = WritingModeConverter.ToLogicalMargin(childMargin, writingMode);

                // MeasureNode handles margin subtraction for block elements (auto-width behavior).
                // Passing available width without margins ensures we don't double-subtract.
                float childInlineConstraint = logicalAvailableInline; // Math.Max(0, logicalAvailableInline - logicalMargin.InlineSum);
                
                var physicalConstraint = WritingModeConverter.ToPhysical(new LogicalSize(childInlineConstraint, logicalAvailable.Block), writingMode);
                
                childMetrics = context.Computer.MeasureNodePublic(child, physicalConstraint, context.Depth + 1);
                
                var childPhysSize = new SKSize(childMetrics.MaxChildWidth, childMetrics.ContentHeight);
                childLogSize = WritingModeConverter.ToLogical(childPhysSize, writingMode);

                bool isFloat = childStyle?.Float?.ToLowerInvariant() == "left"; 
                if (isFloat)
                {
                    float fullChildInline = childLogSize.Inline + logicalMargin.InlineStart + logicalMargin.InlineEnd;
                    float fullChildBlock = childLogSize.Block + logicalMargin.BlockStart + logicalMargin.BlockEnd;
                    
                    if (floatInlineCursor + fullChildInline > logicalAvailableInline && floatInlineCursor > 0)
                    {
                        logicalCurBlock += currentFloatBlockSize;
                        floatInlineCursor = 0;
                        currentFloatBlockSize = 0;
                    }
                    floatInlineCursor += fullChildInline;
                    currentFloatBlockSize = Math.Max(currentFloatBlockSize, fullChildBlock);
                    logicalMaxInline = Math.Max(logicalMaxInline, floatInlineCursor);
                }
                else
                {
                    if (floatInlineCursor > 0)
                    {
                        logicalCurBlock += currentFloatBlockSize;
                        floatInlineCursor = 0;
                        currentFloatBlockSize = 0;
                        first = true; 
                    }

                    logicalMaxInline = Math.Max(logicalMaxInline, childLogSize.Inline);

                    float childMT = logicalMargin.BlockStart;
                    float childMB = logicalMargin.BlockEnd;

                    if (first)
                    {
                        if (parentPreventCollapse)
                        {
                            logicalCurBlock += childMT;
                             internalBlockMarginStart = 0; 
                        }
                        else
                        {
                             internalBlockMarginStart = Math.Max(internalBlockMarginStart, childMT);
                        }
                        first = false;
                    }
                    else
                    {
                        float collapsedMargin = Math.Max(previousMarginBottom, childMT);
                        logicalCurBlock += collapsedMargin;
                    }
                    
                    logicalCurBlock += childLogSize.Block;
                    previousMarginBottom = childMB;
                }
            }
            
            if (!parentPreventCollapse)
            {
                internalBlockMarginEnd = previousMarginBottom;
            }
            else
            {
                logicalCurBlock += previousMarginBottom;
            }

            logicalCurBlock += currentFloatBlockSize;

            var finalLogSize = new LogicalSize(logicalMaxInline, logicalCurBlock);
            var finalPhysSize = WritingModeConverter.ToPhysical(finalLogSize, writingMode);

            return new LayoutMetrics {
                ContentHeight = finalPhysSize.Height,
                ActualHeight = finalPhysSize.Height,
                MaxChildWidth = finalPhysSize.Width,
                MarginTop = internalBlockMarginStart,
                MarginBottom = internalBlockMarginEnd
            };
        }

        public void Arrange(LayoutContext context, SKRect finalRect)
        {
             context.Computer.ArrangeBlockInternalInternal(context.Node as Element, finalRect, context.Depth, context.FallbackNode);
        }
    }
}
