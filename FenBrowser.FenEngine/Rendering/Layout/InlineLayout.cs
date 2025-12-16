using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Layout
{
    /// <summary>
    /// Inline layout engine for text flow and inline elements.
    /// Handles line breaking, word wrapping, and inline formatting contexts.
    /// </summary>
    public static class InlineLayout
    {
        /// <summary>
        /// Compute layout for inline content within a block container.
        /// </summary>
        public static float Compute(
            ILayoutEngine engine,
            LiteElement container,
            SKRect contentBox,
            CssComputed containerStyle,
            out float maxInlineWidth)
        {
            maxInlineWidth = 0;
            if (container.Children == null || container.Children.Count == 0)
                return 0;

            var ctx = engine.Context;
            float lineHeight = GetLineHeight(containerStyle);
            float currentX = contentBox.Left;
            float currentY = contentBox.Top;
            float lineMaxHeight = lineHeight;
            float maxWidth = contentBox.Width;

            var currentLine = new List<InlineBox>();
            float currentLineWidth = 0;

            foreach (var child in container.Children)
            {
                if (child.IsText)
                {
                    // Text node - wrap into inline boxes
                    var textBoxes = CreateTextBoxes(child, containerStyle, maxWidth - currentLineWidth, engine);
                    foreach (var textBox in textBoxes)
                    {
                        if (currentLineWidth + textBox.Width > maxWidth && currentLine.Count > 0)
                        {
                            // Wrap to next line
                            PositionLine(currentLine, contentBox.Left, currentY, containerStyle);
                            currentY += lineMaxHeight;
                            currentLine.Clear();
                            currentLineWidth = 0;
                            lineMaxHeight = lineHeight;
                        }

                        currentLine.Add(textBox);
                        currentLineWidth += textBox.Width;
                        lineMaxHeight = Math.Max(lineMaxHeight, textBox.Height);
                        maxInlineWidth = Math.Max(maxInlineWidth, currentLineWidth);
                    }
                }
                else
                {
                    // Inline element
                    CssComputed childStyle = null;
                    ctx.Styles?.TryGetValue(child, out childStyle);

                    var display = childStyle?.Display?.ToLowerInvariant() ?? "inline";
                    if (display == "none") continue;

                    if (display == "inline" || display == "inline-block")
                    {
                        var inlineBox = CreateInlineElementBox(child, childStyle, engine);

                        if (currentLineWidth + inlineBox.Width > maxWidth && currentLine.Count > 0)
                        {
                            PositionLine(currentLine, contentBox.Left, currentY, containerStyle);
                            currentY += lineMaxHeight;
                            currentLine.Clear();
                            currentLineWidth = 0;
                            lineMaxHeight = lineHeight;
                        }

                        currentLine.Add(inlineBox);
                        currentLineWidth += inlineBox.Width;
                        lineMaxHeight = Math.Max(lineMaxHeight, inlineBox.Height);
                        maxInlineWidth = Math.Max(maxInlineWidth, currentLineWidth);
                    }
                    else
                    {
                        // Block element breaks inline flow
                        if (currentLine.Count > 0)
                        {
                            PositionLine(currentLine, contentBox.Left, currentY, containerStyle);
                            currentY += lineMaxHeight;
                            currentLine.Clear();
                            currentLineWidth = 0;
                            lineMaxHeight = lineHeight;
                        }

                        // Layout as block
                        engine.ComputeLayout(child, contentBox.Left, currentY, contentBox.Width, false, 0);
                        if (ctx.Boxes.TryGetValue(child, out var blockBox))
                        {
                            currentY = blockBox.MarginBox.Bottom;
                        }
                    }
                }
            }

            // Position remaining line
            if (currentLine.Count > 0)
            {
                PositionLine(currentLine, contentBox.Left, currentY, containerStyle);
                currentY += lineMaxHeight;
            }

            return currentY - contentBox.Top;
        }

        /// <summary>
        /// Create text boxes for a text node with word wrapping.
        /// </summary>
        private static List<InlineBox> CreateTextBoxes(LiteElement textNode, CssComputed style, float availableWidth, ILayoutEngine engine)
        {
            var boxes = new List<InlineBox>();
            string text = textNode.Text ?? "";
            if (string.IsNullOrEmpty(text)) return boxes;

            // Split by whitespace for word wrapping
            var words = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            float fontSize = (float)(style?.FontSize ?? 16);

            using var paint = new SKPaint
            {
                TextSize = fontSize,
                IsAntialias = true
            };

            float spaceWidth = paint.MeasureText(" ");

            foreach (var word in words)
            {
                float wordWidth = paint.MeasureText(word);
                var metrics = paint.FontMetrics;

                boxes.Add(new InlineBox
                {
                    Element = textNode,
                    Text = word,
                    Width = wordWidth + spaceWidth,
                    Height = metrics.Descent - metrics.Ascent,
                    Baseline = -metrics.Ascent
                });
            }

            return boxes;
        }

        /// <summary>
        /// Create inline box for an inline element.
        /// </summary>
        private static InlineBox CreateInlineElementBox(LiteElement element, CssComputed style, ILayoutEngine engine)
        {
            float width = (float)(style?.Width ?? 0);
            float height = (float)(style?.Height ?? 20);

            // Measure content if no explicit size
            if (width == 0)
            {
                width = 100; // Default estimate
            }

            return new InlineBox
            {
                Element = element,
                Width = width,
                Height = height,
                IsElement = true
            };
        }

        /// <summary>
        /// Position boxes on a line with text alignment.
        /// </summary>
        private static void PositionLine(List<InlineBox> line, float startX, float y, CssComputed style)
        {
            if (line.Count == 0) return;

            float x = startX;
            float maxHeight = 0;
            foreach (var box in line)
            {
                maxHeight = Math.Max(maxHeight, box.Height);
            }

            // Apply vertical alignment
            foreach (var box in line)
            {
                box.X = x;
                box.Y = y + (maxHeight - box.Height); // Bottom align by default
                x += box.Width;
            }
        }

        private static float GetLineHeight(CssComputed style)
        {
            if (style?.LineHeight.HasValue == true)
            {
                return (float)style.LineHeight.Value;
            }
            float fontSize = (float)(style?.FontSize ?? 16);
            return fontSize * 1.2f; // Default line height multiplier
        }
    }

    /// <summary>
    /// Represents an inline box (text run or inline element).
    /// </summary>
    public class InlineBox
    {
        public LiteElement Element { get; set; }
        public string Text { get; set; }
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float Baseline { get; set; }
        public bool IsElement { get; set; }

        public SKRect Bounds => new SKRect(X, Y, X + Width, Y + Height);
    }
}
