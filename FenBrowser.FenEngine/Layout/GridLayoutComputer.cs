using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// CSS Grid Layout Computer - Phase 1 Implementation
    /// Supports: grid-template-columns/rows, fr/px/auto units, gap, explicit placement
    /// </summary>
    public static class GridLayoutComputer
    {
        /// <summary>
        /// Represents a single grid track (column or row)
        /// </summary>
        public class GridTrack
        {
            public float BaseSize { get; set; }      // Resolved size in pixels
            public float FlexFactor { get; set; }    // fr value (0 if not flexible)
            public bool IsAuto { get; set; }         // true if "auto"
            public float MinContent { get; set; }    // Minimum content size
            public float MaxContent { get; set; }    // Maximum content size
        }

        /// <summary>
        /// Represents a grid item's position
        /// </summary>
        public class GridItemPosition
        {
            public int ColumnStart { get; set; } = 1;
            public int ColumnEnd { get; set; } = 2;
            public int RowStart { get; set; } = 1;
            public int RowEnd { get; set; } = 2;
            public int ColumnSpan => ColumnEnd - ColumnStart;
            public int RowSpan => RowEnd - RowStart;
        }

        /// <summary>
        /// Measure a grid container and its children
        /// </summary>
        public static LayoutMetrics Measure(
            Element container,
            SKSize availableSize,
            IReadOnlyDictionary<Node, CssComputed> styles,
            int depth)
        {
            if (container == null || container.Children == null)
                return new LayoutMetrics();

            var style = styles.TryGetValue(container, out var s) ? s : null;
            if (style == null) return new LayoutMetrics();

            // Parse grid template
            var columnTracks = ParseTracks(style.GridTemplateColumns, availableSize.Width);
            var rowTracks = ParseTracks(style.GridTemplateRows, availableSize.Height);

            // Get gap values
            float columnGap = (float)(style.ColumnGap ?? style.Gap ?? 0);
            float rowGap = (float)(style.RowGap ?? style.Gap ?? 0);

            // Get grid items (exclude text nodes and hidden elements)
            var items = container.Children
                .Where(c => !c.IsText && c is Element)
                .Cast<Element>()
                .ToList();

            // If no explicit columns defined, create one auto column per item
            if (columnTracks.Count == 0)
            {
                int cols = Math.Max(1, items.Count);
                for (int i = 0; i < cols; i++)
                    columnTracks.Add(new GridTrack { IsAuto = true });
            }

            // Determine positions for all items
            var positions = new Dictionary<Element, GridItemPosition>();
            int autoRow = 1;
            int autoCol = 1;
            
            foreach (var item in items)
            {
                var itemStyle = styles.TryGetValue(item, out var ist) ? ist : null;
                var pos = ResolveItemPosition(itemStyle, autoCol, autoRow, columnTracks.Count);
                positions[item] = pos;

                // Advance auto-placement cursor
                autoCol++;
                if (autoCol > columnTracks.Count)
                {
                    autoCol = 1;
                    autoRow++;
                }
            }

            // Calculate required row count (Clamped to 10,000 to prevent freeze)
            int rowCount = Math.Max(rowTracks.Count, positions.Values.Max(p => p.RowEnd) - 1);
            if (rowCount > 10000) 
            {
                global::FenBrowser.Core.FenLogger.Warn($"[CSS-GRID] Clamping excessive row count {rowCount} to 10000", LogCategory.Layout);
                rowCount = 10000;
            }
            
            // Ensure we have enough row tracks
            while (rowTracks.Count < rowCount)
            {
                rowTracks.Add(new GridTrack { IsAuto = true });
            }

            // Resolve flexible tracks (fr units)
            ResolveFlexibleTracks(columnTracks, availableSize.Width, columnGap);
            
            // For rows, we need to measure content first for auto rows
            MeasureAutoRowHeights(rowTracks, columnTracks, items, positions, styles, columnGap, depth);
            ResolveFlexibleTracks(rowTracks, availableSize.Height, rowGap);

            // Calculate total dimensions
            float totalWidth = columnTracks.Sum(t => t.BaseSize) + Math.Max(0, columnTracks.Count - 1) * columnGap;
            float totalHeight = rowTracks.Sum(t => t.BaseSize) + Math.Max(0, rowTracks.Count - 1) * rowGap;

            System.Diagnostics.Debug.WriteLine($"[CSS-GRID] Measured grid: {columnTracks.Count} cols x {rowCount} rows, size={totalWidth}x{totalHeight}");

            return new LayoutMetrics
            {
                ContentHeight = totalHeight,
                ActualHeight = totalHeight,
                MaxChildWidth = totalWidth
            };
        }

        /// <summary>
        /// Arrange grid items within the container bounds
        /// </summary>
        public static void Arrange(
            Element container,
            SKRect bounds,
            IReadOnlyDictionary<Node, CssComputed> styles,
            IDictionary<Node, BoxModel> boxes,
            int depth,
            Action<Node, SKRect, int> arrangeChild)
        {
            if (container == null || container.Children == null) return;

            var style = styles.TryGetValue(container, out var s) ? s : null;
            if (style == null) return;

            // Parse grid template
            var columnTracks = ParseTracks(style.GridTemplateColumns, bounds.Width);
            var rowTracks = ParseTracks(style.GridTemplateRows, bounds.Height);

            float columnGap = (float)(style.ColumnGap ?? style.Gap ?? 0);
            float rowGap = (float)(style.RowGap ?? style.Gap ?? 0);

            var items = container.Children
                .Where(c => !c.IsText && c is Element)
                .Cast<Element>()
                .ToList();

            if (columnTracks.Count == 0)
            {
                int cols = Math.Max(1, items.Count);
                for (int i = 0; i < cols; i++)
                    columnTracks.Add(new GridTrack { IsAuto = true });
            }

            // Determine positions
            var positions = new Dictionary<Element, GridItemPosition>();
            int autoRow = 1;
            int autoCol = 1;

            foreach (var item in items)
            {
                var itemStyle = styles.TryGetValue(item, out var ist) ? ist : null;
                var pos = ResolveItemPosition(itemStyle, autoCol, autoRow, columnTracks.Count);
                positions[item] = pos;

                autoCol++;
                if (autoCol > columnTracks.Count)
                {
                    autoCol = 1;
                    autoRow++;
                }
            }

            int rowCount = Math.Max(rowTracks.Count, positions.Values.Max(p => p.RowEnd) - 1);
            while (rowTracks.Count < rowCount)
                rowTracks.Add(new GridTrack { IsAuto = true });

            // Resolve track sizes
            ResolveFlexibleTracks(columnTracks, bounds.Width - Math.Max(0, columnTracks.Count - 1) * columnGap, columnGap);
            MeasureAutoRowHeights(rowTracks, columnTracks, items, positions, styles, columnGap, depth);
            ResolveFlexibleTracks(rowTracks, bounds.Height - Math.Max(0, rowTracks.Count - 1) * rowGap, rowGap);

            // Calculate track start positions
            var colStarts = new float[columnTracks.Count + 1];
            colStarts[0] = bounds.Left;
            for (int i = 0; i < columnTracks.Count; i++)
                colStarts[i + 1] = colStarts[i] + columnTracks[i].BaseSize + (i < columnTracks.Count - 1 ? columnGap : 0);

            var rowStarts = new float[rowTracks.Count + 1];
            rowStarts[0] = bounds.Top;
            for (int i = 0; i < rowTracks.Count; i++)
                rowStarts[i + 1] = rowStarts[i] + rowTracks[i].BaseSize + (i < rowTracks.Count - 1 ? rowGap : 0);

            // Arrange each item
            foreach (var item in items)
            {
                var pos = positions[item];
                int c1 = Math.Max(0, pos.ColumnStart - 1);
                int c2 = Math.Min(columnTracks.Count, pos.ColumnEnd - 1);
                int r1 = Math.Max(0, pos.RowStart - 1);
                int r2 = Math.Min(rowTracks.Count, pos.RowEnd - 1);

                float x = colStarts[c1];
                float y = rowStarts[r1];
                float w = colStarts[c2] - x - (c2 > c1 + 1 ? 0 : 0); // Account for gaps in span
                float h = rowStarts[r2] - y;

                // Subtract trailing gap for multi-span items
                if (pos.ColumnSpan > 1 && c2 < columnTracks.Count)
                    w = colStarts[c2] - x;
                if (pos.RowSpan > 1 && r2 < rowTracks.Count)
                    h = rowStarts[r2] - y;

                var itemRect = new SKRect(x, y, x + w, y + h);
                
                System.Diagnostics.Debug.WriteLine($"[CSS-GRID] Arrange item at grid[{pos.ColumnStart},{pos.RowStart}] -> rect={itemRect}");
                
                arrangeChild(item, itemRect, depth + 1);
            }
        }

        /// <summary>
        /// Parse track definitions (e.g., "100px 1fr auto 200px")
        /// </summary>
        private static List<GridTrack> ParseTracks(string template, float containerSize)
        {
            var tracks = new List<GridTrack>();
            if (string.IsNullOrWhiteSpace(template)) return tracks;

            var parts = template.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var track = new GridTrack();
                string val = part.Trim().ToLowerInvariant();

                if (val == "auto")
                {
                    track.IsAuto = true;
                    track.BaseSize = 0; // Will be resolved based on content
                }
                else if (val.EndsWith("fr"))
                {
                    if (float.TryParse(val.Replace("fr", ""), out float fr))
                        track.FlexFactor = fr;
                    else
                        track.FlexFactor = 1;
                }
                else if (val.EndsWith("px"))
                {
                    if (float.TryParse(val.Replace("px", ""), out float px))
                        track.BaseSize = px;
                }
                else if (val.EndsWith("%"))
                {
                    if (float.TryParse(val.Replace("%", ""), out float pct))
                        track.BaseSize = containerSize * pct / 100f;
                }
                else if (float.TryParse(val, out float num))
                {
                    track.BaseSize = num;
                }

                tracks.Add(track);
            }

            return tracks;
        }

        /// <summary>
        /// Resolve item grid position from CSS properties
        /// </summary>
        private static GridItemPosition ResolveItemPosition(CssComputed style, int autoCol, int autoRow, int colCount)
        {
            var pos = new GridItemPosition
            {
                ColumnStart = autoCol,
                ColumnEnd = autoCol + 1,
                RowStart = autoRow,
                RowEnd = autoRow + 1
            };

            if (style == null) return pos;

            // Parse grid-column-start
            if (!string.IsNullOrEmpty(style.GridColumnStart))
            {
                if (int.TryParse(style.GridColumnStart, out int cs))
                    pos.ColumnStart = cs;
            }

            // Parse grid-column-end or span
            if (!string.IsNullOrEmpty(style.GridColumnEnd))
            {
                var gce = style.GridColumnEnd.Trim().ToLowerInvariant();
                if (gce.StartsWith("span"))
                {
                    if (int.TryParse(gce.Replace("span", "").Trim(), out int span))
                        pos.ColumnEnd = pos.ColumnStart + span;
                }
                else if (int.TryParse(gce, out int ce))
                {
                    pos.ColumnEnd = ce;
                }
            }
            else
            {
                pos.ColumnEnd = pos.ColumnStart + 1;
            }

            // Parse grid-row-start
            if (!string.IsNullOrEmpty(style.GridRowStart))
            {
                if (int.TryParse(style.GridRowStart, out int rs))
                    pos.RowStart = rs;
            }

            // Parse grid-row-end or span
            if (!string.IsNullOrEmpty(style.GridRowEnd))
            {
                var gre = style.GridRowEnd.Trim().ToLowerInvariant();
                if (gre.StartsWith("span"))
                {
                    if (int.TryParse(gre.Replace("span", "").Trim(), out int span))
                        pos.RowEnd = pos.RowStart + span;
                }
                else if (int.TryParse(gre, out int re))
                {
                    pos.RowEnd = re;
                }
            }
            else
            {
                pos.RowEnd = pos.RowStart + 1;
            }

            return pos;
        }

        /// <summary>
        /// Distribute available space among flexible (fr) tracks
        /// </summary>
        private static void ResolveFlexibleTracks(List<GridTrack> tracks, float availableSpace, float gap)
        {
            if (tracks.Count == 0) return;

            // Calculate used space by fixed tracks
            float usedSpace = tracks.Where(t => t.FlexFactor == 0 && !t.IsAuto).Sum(t => t.BaseSize);
            float gapSpace = Math.Max(0, tracks.Count - 1) * gap;
            float freeSpace = Math.Max(0, availableSpace - usedSpace - gapSpace);

            // Calculate total flex factor
            float totalFlex = tracks.Sum(t => t.FlexFactor);

            if (totalFlex > 0)
            {
                float frUnit = freeSpace / totalFlex;
                foreach (var track in tracks)
                {
                    if (track.FlexFactor > 0)
                        track.BaseSize = track.FlexFactor * frUnit;
                }
            }

            // Resolve auto tracks (distribute remaining space equally)
            var autoTracks = tracks.Where(t => t.IsAuto && t.FlexFactor == 0 && t.BaseSize == 0).ToList();
            if (autoTracks.Count > 0 && totalFlex == 0)
            {
                float remainingSpace = freeSpace / autoTracks.Count;
                foreach (var track in autoTracks)
                    track.BaseSize = Math.Max(track.BaseSize, remainingSpace);
            }

            // Ensure minimum size
            foreach (var track in tracks)
            {
                if (track.BaseSize < 0) track.BaseSize = 0;
            }
        }

        /// <summary>
        /// Measure content to determine auto row heights
        /// </summary>
        private static void MeasureAutoRowHeights(
            List<GridTrack> rowTracks,
            List<GridTrack> columnTracks,
            List<Element> items,
            Dictionary<Element, GridItemPosition> positions,
            IReadOnlyDictionary<Node, CssComputed> styles,
            float columnGap,
            int depth)
        {
            // Group items by row
            var rowMaxHeights = new Dictionary<int, float>();

            foreach (var item in items)
            {
                var pos = positions[item];
                var itemStyle = styles.TryGetValue(item, out var ist) ? ist : null;

                // Estimate item height (use explicit height or default)
                float itemHeight = 50; // Default fallback
                if (itemStyle?.Height != null)
                    itemHeight = (float)itemStyle.Height.Value;
                else if (itemStyle?.MinHeight != null)
                    itemHeight = (float)itemStyle.MinHeight.Value;

                // Distribute height across spanned rows
                int spanRows = pos.RowEnd - pos.RowStart;
                float heightPerRow = itemHeight / spanRows;

                for (int r = pos.RowStart; r < pos.RowEnd; r++)
                {
                    if (!rowMaxHeights.ContainsKey(r))
                        rowMaxHeights[r] = 0;
                    rowMaxHeights[r] = Math.Max(rowMaxHeights[r], heightPerRow);
                }
            }

            // Apply measured heights to auto rows
            for (int i = 0; i < rowTracks.Count; i++)
            {
                if (rowTracks[i].IsAuto && rowMaxHeights.TryGetValue(i + 1, out float h))
                {
                    rowTracks[i].BaseSize = Math.Max(rowTracks[i].BaseSize, h);
                }
                
                // Ensure auto rows have minimum height
                if (rowTracks[i].IsAuto && rowTracks[i].BaseSize == 0)
                    rowTracks[i].BaseSize = 40; // Minimum auto row height
            }
        }
    }
}
