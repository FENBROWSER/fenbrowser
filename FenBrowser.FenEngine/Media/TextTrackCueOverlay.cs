using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Adapters;
using SkiaSharp;

namespace FenBrowser.FenEngine.Media
{
    /// <summary>One cue to show over a video: its text and the WebVTT settings that place it.</summary>
    /// <param name="Text">The cue text with markup removed; line feeds are line breaks.</param>
    /// <param name="Line">The line setting, or null for "auto".</param>
    /// <param name="Position">The position setting, or null for "auto".</param>
    public sealed record TextTrackCueDisplay(
        string Text,
        string Vertical,
        bool SnapToLines,
        double? Line,
        string LineAlign,
        double? Position,
        string PositionAlign,
        double Size,
        string Align);

    /// <summary>A laid-out line of a cue: where its background box and text go.</summary>
    public readonly record struct TextTrackCueLine(string Text, SKRect Box, SKPoint Baseline);

    /// <summary>
    /// The showing cues of every video element, written by the text track model on the
    /// script thread and read by paint. The layout itself (<see cref="Layout"/>) is a
    /// reduced form of WebVTT §7.3 "apply WebVTT cue settings": the font is 5% of the
    /// viewport height, cues wrap inside a box <c>size</c>% of the viewport width, are
    /// anchored by <c>position</c>/<c>align</c>, and snap-to-lines cues stack upward from
    /// the bottom (line -1) or downward from the top for non-negative lines; percentage
    /// lines place the box directly. Vertical writing modes fall back to horizontal.
    /// </summary>
    public static class TextTrackCueOverlay
    {
        private static readonly ConditionalWeakTable<Element, IReadOnlyList<TextTrackCueDisplay>> s_cues = new();

        public const string FontFamily = "sans-serif";

        public static IReadOnlyList<TextTrackCueDisplay> Get(Element element)
        {
            if (element != null && s_cues.TryGetValue(element, out var cues))
                return cues;
            return Array.Empty<TextTrackCueDisplay>();
        }

        public static void Update(Element element, IReadOnlyList<TextTrackCueDisplay> cues)
        {
            if (element == null)
                return;
            if (cues == null || cues.Count == 0)
                s_cues.Remove(element);
            else
                s_cues.AddOrUpdate(element, cues);
        }

        /// <summary>Lays the cues out inside <paramref name="viewport"/> (the video's content box).</summary>
        public static List<TextTrackCueLine> Layout(IReadOnlyList<TextTrackCueDisplay> cues, SKRect viewport, ITextMeasurer measurer, out float fontSize)
        {
            fontSize = Math.Max(8f, viewport.Height * 0.05f);
            var lines = new List<TextTrackCueLine>();
            if (cues == null || cues.Count == 0 || viewport.Width <= 0 || viewport.Height <= 0)
                return lines;

            float lineHeight = measurer.GetLineHeight(FontFamily, fontSize);
            if (lineHeight <= 0)
                lineHeight = fontSize * 1.2f;
            float padding = fontSize * 0.15f;
            int totalLines = (int)Math.Floor(viewport.Height / lineHeight);
            if (totalLines <= 0)
                return lines;

            // Snap-to-lines cues that stack from the bottom: each takes the rows above the
            // previous one so cues never overlap (§7.3 step 18, the "auto" placement).
            var occupied = new bool[totalLines];

            foreach (var cue in cues)
            {
                double size = double.IsFinite(cue.Size) ? Math.Clamp(cue.Size, 0, 100) : 100;
                float boxWidth = (float)(viewport.Width * size / 100.0);
                var wrapped = Wrap(cue.Text, boxWidth - 2 * padding, measurer, fontSize);
                int rows = wrapped.Count;
                if (rows == 0)
                    continue;

                // Horizontal placement: position picks the anchor point, align/positionAlign
                // pick which edge of the box sits on it (§7.3 steps 6–9).
                double position = cue.Position ?? DefaultPosition(cue.Align);
                float anchorX = (float)(viewport.Left + viewport.Width * position / 100.0);
                string positionAlign = cue.PositionAlign == "auto" ? DefaultPositionAlign(cue.Align) : cue.PositionAlign;
                float boxLeft = positionAlign switch
                {
                    "line-left" => anchorX,
                    "line-right" => anchorX - boxWidth,
                    _ => anchorX - boxWidth / 2,
                };
                boxLeft = Math.Clamp(boxLeft, viewport.Left, Math.Max(viewport.Left, viewport.Right - boxWidth));

                // Vertical placement.
                float boxTop;
                if (!cue.SnapToLines && cue.Line is { } percent)
                {
                    float y = (float)(viewport.Top + viewport.Height * Math.Clamp(percent, 0, 100) / 100.0);
                    float boxHeight = rows * lineHeight;
                    boxTop = cue.LineAlign switch
                    {
                        "center" => y - boxHeight / 2,
                        "end" => y - boxHeight,
                        _ => y,
                    };
                    boxTop = Math.Clamp(boxTop, viewport.Top, Math.Max(viewport.Top, viewport.Bottom - boxHeight));
                }
                else
                {
                    int row;
                    double line = cue.Line ?? -1;
                    if (line < 0)
                    {
                        // Counted from the bottom: -1 is the last row. Move up past rows
                        // other cues took.
                        row = totalLines + (int)Math.Round(line) - rows + 1;
                        row = Math.Clamp(row, 0, Math.Max(0, totalLines - rows));
                        while (row > 0 && Overlaps(occupied, row, rows))
                            row--;
                    }
                    else
                    {
                        row = Math.Clamp((int)Math.Round(line), 0, Math.Max(0, totalLines - rows));
                        while (row + rows < totalLines && Overlaps(occupied, row, rows))
                            row++;
                    }

                    for (int r = row; r < Math.Min(totalLines, row + rows); r++)
                        occupied[r] = true;
                    boxTop = viewport.Top + row * lineHeight;
                }

                for (int i = 0; i < rows; i++)
                {
                    string text = wrapped[i];
                    float width = measurer.MeasureWidth(text, FontFamily, fontSize);
                    float left = cue.Align switch
                    {
                        "start" or "left" => boxLeft + padding,
                        "end" or "right" => boxLeft + boxWidth - padding - width,
                        _ => boxLeft + (boxWidth - width) / 2,
                    };
                    float top = boxTop + i * lineHeight;
                    var box = new SKRect(left - padding, top, left + width + padding, top + lineHeight);
                    // The baseline sits at roughly 80% of the line box for Latin text.
                    lines.Add(new TextTrackCueLine(text, box, new SKPoint(left, top + lineHeight * 0.8f)));
                }
            }

            return lines;
        }

        private static bool Overlaps(bool[] occupied, int row, int rows)
        {
            for (int r = row; r < Math.Min(occupied.Length, row + rows); r++)
            {
                if (occupied[r])
                    return true;
            }

            return false;
        }

        private static double DefaultPosition(string align) => align switch
        {
            "start" or "left" => 0,
            "end" or "right" => 100,
            _ => 50,
        };

        private static string DefaultPositionAlign(string align) => align switch
        {
            "start" or "left" => "line-left",
            "end" or "right" => "line-right",
            _ => "center",
        };

        /// <summary>Greedy word wrap of each source line into <paramref name="maxWidth"/>.</summary>
        private static List<string> Wrap(string text, float maxWidth, ITextMeasurer measurer, float fontSize)
        {
            var result = new List<string>();
            foreach (var source in (text ?? string.Empty).Split('\n'))
            {
                var words = source.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0)
                {
                    if (source.Length == 0 && result.Count > 0)
                        result.Add(string.Empty);
                    continue;
                }

                string current = words[0];
                for (int i = 1; i < words.Length; i++)
                {
                    string candidate = current + " " + words[i];
                    if (measurer.MeasureWidth(candidate, FontFamily, fontSize) <= maxWidth)
                    {
                        current = candidate;
                    }
                    else
                    {
                        result.Add(current);
                        current = words[i];
                    }
                }

                result.Add(current);
            }

            return result;
        }
    }
}
