// =============================================================================
// InlineRunDebugger.cs
// Inline Text Run Debugging Tool
// 
// PURPOSE: Log detailed metrics for inline text runs to detect overlapping/spacing issues
// USAGE: InlineRunDebugger.LogRun(...) during inline layout computation
// OUTPUT: Console logs + CSV export showing glyph count, advance, font metrics
// =============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Debugging tool for inline formatting context text runs.
    /// Logs glyph count, total advance width, and font metrics to detect spacing issues.
    /// </summary>
    public static class InlineRunDebugger
    {
        private static List<InlineRunMetrics> _runs = new List<InlineRunMetrics>();
        private static bool _enabled = false;

        public struct InlineRunMetrics
        {
            public string Text;
            public int GlyphCount;
            public float TotalAdvance;
            public float FontSize;
            public string FontFamily;
            public float LineHeight;
            public float BaselineOffset;
            public float ExpectedX;
            public float ActualX;
            public string NodePath;  // For DOM correlation
        }

        /// <summary>
        /// Enable/disable inline run debugging.
        /// </summary>
        public static bool Enabled
        {
            get => _enabled;
            set => _enabled = value;
        }

        /// <summary>
        /// Log a text run during inline layout.
        /// </summary>
        public static void LogRun(
            string text,
            int glyphCount,
            float totalAdvance,
            float fontSize,
            string fontFamily,
            float lineHeight,
            float baselineOffset,
            float expectedX,
            float actualX,
            Node node)
        {
            if (!_enabled) return;

            var metrics = new InlineRunMetrics
            {
                Text = text?.Length > 50 ? text.Substring(0, 50) + "..." : text,
                GlyphCount = glyphCount,
                TotalAdvance = totalAdvance,
                FontSize = fontSize,
                FontFamily = fontFamily ?? "default",
                LineHeight = lineHeight,
                BaselineOffset = baselineOffset,
                ExpectedX = expectedX,
                ActualX = actualX,
                NodePath = GetNodePath(node)
            };

            _runs.Add(metrics);

            // Console log for immediate debugging
            global::FenBrowser.Core.EngineLogCompat.Debug(
                $"[InlineRun] \"{metrics.Text}\" | Glyphs:{glyphCount} | Advance:{totalAdvance:F2}px | " +
                $"Font:{fontFamily}/{fontSize:F1}px | X:{actualX:F2} (expected:{expectedX:F2})",
                LogCategory.Text
            );
        }

        /// <summary>
        /// Log a run with automatically calculated glyph count (character count as fallback).
        /// </summary>
        public static void LogRun(
            string text,
            float totalAdvance,
            float fontSize,
            string fontFamily,
            float lineHeight,
            float baselineOffset,
            float x,
            Node node)
        {
            LogRun(text, text?.Length ?? 0, totalAdvance, fontSize, fontFamily, lineHeight, baselineOffset, x, x, node);
        }

        /// <summary>
        /// Detect overlapping runs (when advance doesn't match expected positioning).
        /// </summary>
        public static List<(InlineRunMetrics run1, InlineRunMetrics run2, float overlapAmount)> DetectOverlaps()
        {
            var overlaps = new List<(InlineRunMetrics, InlineRunMetrics, float)>();

            for (int i = 0; i < _runs.Count - 1; i++)
            {
                var current = _runs[i];
                var next = _runs[i + 1];

                // Expected next run X = current X + current advance
                float expectedNextX = current.ActualX + current.TotalAdvance;
                float actualNextX = next.ActualX;

                // Overlap if next starts before current ends
                if (actualNextX < expectedNextX)
                {
                    float overlap = expectedNextX - actualNextX;
                    overlaps.Add((current, next, overlap));

                    global::FenBrowser.Core.EngineLogCompat.Warn(
                        $"[InlineRun] OVERLAP DETECTED: \"{current.Text}\" → \"{next.Text}\" | " +
                        $"Overlap: {overlap:F2}px | Expected X: {expectedNextX:F2}, Actual: {actualNextX:F2}",
                        LogCategory.Text
                    );
                }
            }

            return overlaps;
        }

        /// <summary>
        /// Export run data to CSV for analysis.
        /// </summary>
        public static void ExportToCsv(string path)
        {
            try
            {
                var sb = new StringBuilder();
                
                // Header
                sb.AppendLine("Text,GlyphCount,TotalAdvance,FontSize,FontFamily,LineHeight,BaselineOffset,ExpectedX,ActualX,AdvanceGap,NodePath");

                // Data rows
                for (int i = 0; i < _runs.Count; i++)
                {
                    var run = _runs[i];
                    float gap = (i < _runs.Count - 1) 
                        ? (_runs[i + 1].ActualX - (run.ActualX + run.TotalAdvance))
                        : 0;

                    sb.AppendLine($"\"{run.Text.Replace("\"", "\"\"")}\"," +
                                  $"{run.GlyphCount}," +
                                  $"{run.TotalAdvance:F2}," +
                                  $"{run.FontSize:F2}," +
                                  $"\"{run.FontFamily}\"," +
                                  $"{run.LineHeight:F2}," +
                                  $"{run.BaselineOffset:F2}," +
                                  $"{run.ExpectedX:F2}," +
                                  $"{run.ActualX:F2}," +
                                  $"{gap:F2}," +
                                  $"\"{run.NodePath}\"");
                }

                File.WriteAllText(path, sb.ToString());
                global::FenBrowser.Core.EngineLogCompat.Log($"[InlineRun] Exported {_runs.Count} runs to: {path}", LogCategory.Text);
            }
            catch (Exception ex)
            {
                global::FenBrowser.Core.EngineLogCompat.Error($"[InlineRun] CSV export failed: {ex.Message}", LogCategory.Text);
            }
        }

        /// <summary>
        /// Generate summary statistics.
        /// </summary>
        public static string GetSummary()
        {
            if (_runs.Count == 0)
                return "[InlineRun] No runs recorded.";

            float totalAdvance = 0;
            int totalGlyphs = 0;
            float maxAdvance = 0;
            float minAdvance = float.MaxValue;

            foreach (var run in _runs)
            {
                totalAdvance += run.TotalAdvance;
                totalGlyphs += run.GlyphCount;
                maxAdvance = Math.Max(maxAdvance, run.TotalAdvance);
                minAdvance = Math.Min(minAdvance, run.TotalAdvance);
            }

            float avgAdvance = totalAdvance / _runs.Count;
            float avgGlyphsPerRun = (float)totalGlyphs / _runs.Count;

            var overlaps = DetectOverlaps();

            return $"[InlineRun] Summary:\n" +
                   $"  Total Runs: {_runs.Count}\n" +
                   $"  Total Glyphs: {totalGlyphs}\n" +
                   $"  Total Advance: {totalAdvance:F2}px\n" +
                   $"  Avg Advance/Run: {avgAdvance:F2}px\n" +
                   $"  Avg Glyphs/Run: {avgGlyphsPerRun:F1}\n" +
                   $"  Min Advance: {minAdvance:F2}px\n" +
                   $"  Max Advance: {maxAdvance:F2}px\n" +
                   $"  Overlaps Detected: {overlaps.Count}";
        }

        /// <summary>
        /// Clear recorded runs.
        /// </summary>
        public static void Clear()
        {
            _runs.Clear();
            global::FenBrowser.Core.EngineLogCompat.Debug("[InlineRun] Cleared run history", LogCategory.Text);
        }

        /// <summary>
        /// Get count of recorded runs.
        /// </summary>
        public static int RunCount => _runs.Count;

        // ========================================================================
        // INTERNAL: Helpers
        // ========================================================================

        private static string GetNodePath(Node node)
        {
            if (node == null) return "null";

            var parts = new List<string>();
            var current = node;

            while (current != null && parts.Count < 10)  // Limit depth
            {
                string part;
                if (current is Element elem)
                {
                    part = elem.TagName ?? "ELEMENT";
                    var id = elem.GetAttribute("id");
                    if (!string.IsNullOrEmpty(id))
                        part += $"#{id}";
                }
                else if (current is Text)
                {
                    part = "#text";
                }
                else
                {
                    part = current.NodeName ?? "NODE";
                }

                parts.Insert(0, part);
                current = current.ParentNode;
            }

            return string.Join(" > ", parts);
        }
    }
}

