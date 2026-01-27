using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using FenBrowser.Core.Css;
using FenBrowser.Core;
using FenBrowser.Core.Dom;
using FenBrowser.FenEngine.Layout; 

namespace FenBrowser.FenEngine.Layout.Contexts
{
    public class InlineFormattingContext : FormattingContext
    {
        private static InlineFormattingContext _instance;
        public static InlineFormattingContext Instance => _instance ??= new InlineFormattingContext();

        // Line Box Structure
        private class LineBox
        {
            public float Width = 0;
            public float Height = 0;
            public float Baseline = 0;
            public List<LayoutBox> Items = new List<LayoutBox>();
        }

        public override void Layout(LayoutBox box, LayoutState state)
        {
            ResolveContextWidth(box, state);
            
            float contentLimit = box.Geometry.ContentBox.Width;
            // Robustness: Handle unconstrained width (shrink-to-fit root)
            if (float.IsInfinity(contentLimit)) contentLimit = state.ViewportWidth;
            if (float.IsNaN(contentLimit)) contentLimit = 800f; // Safe fallback

            float startX = (float)box.Geometry.Padding.Left + (float)box.Geometry.Border.Left;
            float startY = (float)box.Geometry.Padding.Top + (float)box.Geometry.Border.Top;

            var lines = new List<LineBox>();
            var currentLine = new LineBox();
            lines.Add(currentLine);

            float curX = 0;

            foreach (var child in box.Children)
            {
                state.Deadline?.Check();
                if (child.IsOutOfFlow) continue;

                if (child is TextLayoutBox textBox)
                {
                    string fullText = (textBox.SourceNode as Text)?.Data ?? "";
                    if (string.IsNullOrEmpty(fullText)) continue;

                    // WORD FLOW
                    int startIdx = 0;
                    while (startIdx < fullText.Length)
                    {
                        // Find next word break
                        int nextSpace = fullText.IndexOf(' ', startIdx);
                        int endIdx = (nextSpace == -1) ? fullText.Length : nextSpace + 1;
                        string word = fullText.Substring(startIdx, endIdx - startIdx);
                        float wordWidth = MeasureString(word, textBox.ComputedStyle).Width;

                        if (curX + wordWidth > contentLimit && curX > 0)
                        {
                            currentLine = new LineBox();
                            lines.Add(currentLine);
                            curX = 0;
                        }

                        // Add to current line
                        // Check if last item was a fragment for the same node
                        var lastItem = currentLine.Items.LastOrDefault() as TextLayoutBox;
                        if (lastItem != null && lastItem.SourceNode == textBox.SourceNode && 
                            lastItem.ComputedStyle == textBox.ComputedStyle &&
                            (lastItem.StartOffset + lastItem.Length == startIdx))
                        {
                            // Append to last fragment on this line
                            lastItem.Length += word.Length;
                            // Update width
                            float newW = lastItem.Geometry.ContentBox.Width + wordWidth;
                            lastItem.Geometry.ContentBox = new SKRect(lastItem.Geometry.ContentBox.Left, 0, lastItem.Geometry.ContentBox.Left + newW, lastItem.Geometry.ContentBox.Height);
                            currentLine.Width += wordWidth;
                            curX += wordWidth;
                        }
                        else
                        {
                            // Create new fragment box
                            var frag = new TextLayoutBox(textBox.SourceNode as Text, textBox.ComputedStyle);
                            frag.StartOffset = startIdx;
                            frag.Length = word.Length;
                            frag.Geometry = new BoxModel();
                            float h = MeasureString("H", textBox.ComputedStyle).Height;
                            frag.Geometry.ContentBox = new SKRect(curX, 0, curX + wordWidth, h);
                            SyncBoxes(frag.Geometry);

                            currentLine.Items.Add(frag);
                            currentLine.Width = curX + wordWidth;
                            currentLine.Height = Math.Max(currentLine.Height, h);
                            curX += wordWidth;
                        }

                        startIdx = endIdx;
                    }
                }
                else
                {
                    // Atomic Inline
                    SKSize childSize = MeasureInlineChild(child);
                    if (curX + childSize.Width > contentLimit && curX > 0)
                    {
                        currentLine = new LineBox();
                        lines.Add(currentLine);
                        curX = 0;
                    }

                    if (child.Geometry == null) child.Geometry = new BoxModel();
                    child.Geometry.ContentBox = new SKRect(curX, 0, curX + childSize.Width, childSize.Height);
                    SyncBoxes(child.Geometry);

                    currentLine.Items.Add(child);
                    currentLine.Width = curX + childSize.Width;
                    currentLine.Height = Math.Max(currentLine.Height, childSize.Height);
                    curX += childSize.Width;
                }
            }

            // SYNC CHILDREN: Replace existing children with flattened fragments
            box.Children.Clear();
            foreach (var line in lines)
            {
                foreach (var item in line.Items)
                {
                    box.Children.Add(item);
                }
            }

            // POSITION LINES
            float curY = startY;
            var textAlign = box.ComputedStyle?.TextAlign ?? SKTextAlign.Left;

            // Robustness: If no lines or empty lines, ensure we take up space if we are an atomic inline 
            if (lines.Count == 1 && lines[0].Items.Count == 0)
            {
                SKSize selfSize = MeasureInlineChild(box);
                if (selfSize.Height > 0) curY += selfSize.Height;
            }

            foreach (var line in lines)
            {
                float xOffset = 0;
                if (textAlign == SKTextAlign.Center) xOffset = (contentLimit - line.Width) / 2f;
                else if (textAlign == SKTextAlign.Right) xOffset = (contentLimit - line.Width);

                foreach (var item in line.Items)
                {
                    // Shift to final position
                    float finalX = startX + xOffset + item.Geometry.ContentBox.Left;
                    float finalY = curY; 
                    
                    LayoutBoxOps.SetPosition(item, finalX, finalY);
                }
                curY += line.Height;
            }

            // Final Height
            float finalH = curY + (float)box.Geometry.Padding.Bottom + (float)box.Geometry.Border.Bottom;
            float finalContentHeight = Math.Max(0f, curY - startY);
            
            // SHRINK-TO-FIT: If width was auto and available was infinity, we shrink to widest line
            float finalContentWidth = box.Geometry.ContentBox.Width;
            if (box.ComputedStyle != null && !box.ComputedStyle.Width.HasValue && float.IsInfinity(state.AvailableSize.Width))
            {
                float maxLW = 0;
                foreach (var line in lines) maxLW = Math.Max(maxLW, line.Width);
                finalContentWidth = maxLW;
            }

            if (box.ComputedStyle != null && box.ComputedStyle.Height.HasValue)
                finalContentHeight = (float)box.ComputedStyle.Height.Value;

            box.Geometry.ContentBox = new SKRect(
                box.Geometry.ContentBox.Left,
                box.Geometry.ContentBox.Top,
                box.Geometry.ContentBox.Left + finalContentWidth,
                box.Geometry.ContentBox.Top + finalContentHeight
            );
            
            SyncBoxes(box.Geometry);
        }

        private SKSize MeasureInlineChild(LayoutBox child)
        {
            if (child is TextLayoutBox textBox)
            {
                return MeasureString(textBox.TextContent, textBox.ComputedStyle);
            }
            else if (child is InlineBox inlineBox)
            {
                // Check if it's an atomic inline (inline-block etc)
                string display = inlineBox.ComputedStyle?.Display?.ToLowerInvariant() ?? "inline";
                if (display != "inline")
                {
                    // For atomic inlines, we should ideally call their own layout
                    // but for now, we'll return its computed size or content size.
                    float cw = (float)(inlineBox.ComputedStyle?.Width ?? 0);
                    float ch = (float)(inlineBox.ComputedStyle?.Height ?? 20);
                    
                    if (cw > 0) return new SKSize(cw, ch);
                }

                // Normal inline (span) - aggregate children
                float w = 0;
                float h = 0;
                foreach (var c in inlineBox.Children)
                {
                    var sz = MeasureInlineChild(c);
                    w += sz.Width;
                    h = Math.Max(h, sz.Height);
                }
                return new SKSize(w, Math.Max(h, 20f));
            }
            
            // Handle IMG, INPUT, SVG or other elements that result in generic LayoutBox
            if (child.SourceNode is FenBrowser.Core.Dom.Element el)
            {
                string t = el.TagName?.ToUpperInvariant();
                if (t == "IMG" || t == "INPUT" || t == "SVG" || t == "BUTTON")
                {
                    float w = (float)(child.ComputedStyle?.Width ?? 0);
                    float h = (float)(child.ComputedStyle?.Height ?? 0);
                    
                    // Fallbacks if not specified
                    if (w <= 0) 
                    {
                        if (t == "INPUT") w = 150;
                        else if (t == "SVG") w = 24;
                        else if (t == "BUTTON") w = 60;
                        else w = 20;
                    }
                    if (h <= 0)
                    {
                        if (t == "INPUT") h = 24;
                        else if (t == "SVG") h = 24;
                        else if (t == "BUTTON") h = 24;
                        else h = 20;
                    }
                    return new SKSize(w, h);
                }
            }

            return new SKSize(10,10); // Minimal fallback for unknown items
        }

        private SKSize MeasureString(string text, CssComputed style)
        {
             float fs = 16f;
             if (style?.FontSize != null) fs = (float)style.FontSize.Value;
             if (fs < 5) fs = 16f;

             float charWidth = fs * 0.5f;
             if (style?.FontWeight >= 700) charWidth *= 1.15f;
             
             return new SKSize(text.Length * charWidth, fs * 1.25f);
        }


        private void ResolveContextWidth(LayoutBox box, LayoutState state)
        {
            float available = state.AvailableSize.Width;
            float finalW = available; 
            
            var p = box.ComputedStyle?.Padding ?? new Thickness();
            var b = box.ComputedStyle?.BorderThickness ?? new Thickness();
            var m = box.ComputedStyle?.Margin ?? new Thickness();

            float used = (float)(p.Left + p.Right + b.Left + b.Right + m.Left + m.Right);
            finalW = Math.Max(0f, available - used);

            if (box.ComputedStyle != null && box.ComputedStyle.Width.HasValue)
            {
                finalW = (float)box.ComputedStyle.Width.Value;
            }

            float left = box.Geometry.ContentBox.Left;
            float top = box.Geometry.ContentBox.Top;
            box.Geometry.ContentBox = new SKRect(left, top, left + finalW, top);
            
            box.Geometry.Padding = p;
            box.Geometry.Border = b;
            box.Geometry.Margin = m;
            
            SyncBoxes(box.Geometry);
        }
        
        private void SyncBoxes(BoxModel geometry)
        {
             var cb = geometry.ContentBox;
            var p = geometry.Padding;
            var b = geometry.Border;
            var m = geometry.Margin;
            
            geometry.PaddingBox = new SKRect(
                cb.Left - (float)p.Left,
                cb.Top - (float)p.Top,
                cb.Right + (float)p.Right,
                cb.Bottom + (float)p.Bottom);
                
            geometry.BorderBox = new SKRect(
                geometry.PaddingBox.Left - (float)b.Left,
                geometry.PaddingBox.Top - (float)b.Top,
                geometry.PaddingBox.Right + (float)b.Right,
                geometry.PaddingBox.Bottom + (float)b.Bottom);
                
            geometry.MarginBox = new SKRect(
                geometry.BorderBox.Left - (float)m.Left,
                geometry.BorderBox.Top - (float)m.Top,
                geometry.BorderBox.Right + (float)m.Right,
                geometry.BorderBox.Bottom + (float)m.Bottom);
        }
    }
}
