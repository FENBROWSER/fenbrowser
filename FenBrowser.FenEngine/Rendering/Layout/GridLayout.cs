using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Globalization;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Core;

namespace FenBrowser.FenEngine.Rendering.Layout
{
    /// <summary>
    /// CSS Grid layout algorithm implementation.
    /// Computes layout for grid containers according to CSS Grid specification.
    /// </summary>
    public static class GridLayout
    {
        /// <summary>
        /// Compute grid layout for a container and its children.
        /// </summary>
        /// <param name="engine">Layout engine for recursive layout calls</param>
        /// <param name="node">The grid container element</param>
        /// <param name="contentBox">The content box area available for children</param>
        /// <param name="style">Computed styles for the container</param>
        /// <param name="maxChildWidth">Output: maximum width of children</param>
        /// <returns>Total content height after layout</returns>
        public static float Compute(
            ILayoutEngine engine,
            LiteElement node,
            SKRect contentBox,
            CssComputed style,
            out float maxChildWidth)
        {
            var ctx = engine.Context;
            maxChildWidth = 0;
            
            if (node.Children == null || node.Children.Count == 0) return 0;

            // Parse grid-template-columns
            var columnWidths = ParseGridTemplate(style?.GridTemplateColumns, contentBox.Width);
            if (columnWidths.Count == 0)
            {
                // Default: single column
                columnWidths.Add(contentBox.Width);
            }

            // Parse grid-template-areas for named area placement
            var gridAreas = ParseGridTemplateAreas(style?.GridTemplateAreas);

            // Parse gap
            float columnGap = 0, rowGap = 0;
            if (style?.Gap.HasValue == true)
            {
                columnGap = (float)style.Gap.Value;
                rowGap = (float)style.Gap.Value;
            }
            if (style?.ColumnGap.HasValue == true) columnGap = (float)style.ColumnGap.Value;
            if (style?.RowGap.HasValue == true) rowGap = (float)style.RowGap.Value;

            // Collect grid items (excluding absolute/fixed positioned)
            var gridItems = new List<LiteElement>();
            foreach (var c in node.Children)
            {
                CssComputed cStyle = ctx.GetStyle(c);
                string cPos = cStyle?.Position?.ToLowerInvariant();
                if (cPos != "absolute" && cPos != "fixed")
                {
                    gridItems.Add(c);
                }
            }

            if (gridItems.Count == 0) return 0;

            int numColumns = columnWidths.Count;
            int numRows = (int)Math.Ceiling((double)gridItems.Count / numColumns);

            // If we have grid-template-areas, use the row count from the template
            if (gridAreas.Count > 0)
            {
                numRows = gridAreas.Count;
            }

            // Measure all items to determine row heights
            var rowHeights = new float[numRows];
            var itemPlacements = new Dictionary<LiteElement, (int row, int col, int rowSpan, int colSpan)>();

            // First pass: determine placement for items
            int autoPlaceIndex = 0;
            for (int i = 0; i < gridItems.Count; i++)
            {
                var child = gridItems[i];
                CssComputed cStyle = ctx.GetStyle(child);

                string gridArea = cStyle?.GridArea?.Trim();

                if (!string.IsNullOrEmpty(gridArea) && gridAreas.Count > 0)
                {
                    var bounds = FindGridAreaBounds(gridAreas, gridArea);
                    if (bounds.HasValue)
                    {
                        itemPlacements[child] = bounds.Value;
                    }
                    else
                    {
                        int row = autoPlaceIndex / numColumns;
                        int col = autoPlaceIndex % numColumns;
                        itemPlacements[child] = (row, col, 1, 1);
                        autoPlaceIndex++;
                    }
                }
                else
                {
                    int row = autoPlaceIndex / numColumns;
                    int col = autoPlaceIndex % numColumns;
                    itemPlacements[child] = (row, col, 1, 1);
                    autoPlaceIndex++;
                }
            }

            // Measure items
            for (int i = 0; i < gridItems.Count; i++)
            {
                var child = gridItems[i];
                var placement = itemPlacements[child];
                int col = placement.col;

                float cellWidth = placement.colSpan > 1
                    ? columnWidths.Skip(col).Take(placement.colSpan).Sum() + columnGap * (placement.colSpan - 1)
                    : (col < columnWidths.Count ? columnWidths[col] : columnWidths[0]);

                engine.ComputeLayout(child, 0, 0, cellWidth, shrinkToContent: false);

                var childBox = ctx.GetBox(child);
                if (childBox != null)
                {
                    int row = placement.row;
                    if (row < numRows && childBox.MarginBox.Height > rowHeights[row])
                        rowHeights[row] = childBox.MarginBox.Height;
                }
            }

            // Calculate positions
            var columnStarts = new float[numColumns + 1];
            columnStarts[0] = contentBox.Left;
            for (int i = 0; i < numColumns; i++)
            {
                columnStarts[i + 1] = columnStarts[i] + columnWidths[i] + (i < numColumns - 1 ? columnGap : 0);
            }

            var rowStarts = new float[numRows + 1];
            rowStarts[0] = contentBox.Top;
            for (int i = 0; i < numRows; i++)
            {
                rowStarts[i + 1] = rowStarts[i] + rowHeights[i] + (i < numRows - 1 ? rowGap : 0);
            }

            // Position items
            for (int i = 0; i < gridItems.Count; i++)
            {
                var child = gridItems[i];
                var placement = itemPlacements[child];
                int row = placement.row;
                int col = placement.col;

                float x = col < columnStarts.Length - 1 ? columnStarts[col] : columnStarts[0];
                float y = row < rowStarts.Length - 1 ? rowStarts[row] : rowStarts[0];
                float width = placement.colSpan > 1
                    ? columnWidths.Skip(col).Take(placement.colSpan).Sum() + columnGap * (placement.colSpan - 1)
                    : (col < columnWidths.Count ? columnWidths[col] : columnWidths[0]);

                engine.ComputeLayout(child, x, y, width, shrinkToContent: false);
            }

            // Calculate total dimensions
            maxChildWidth = columnStarts[numColumns] - contentBox.Left;
            float totalHeight = rowStarts[numRows] - contentBox.Top;

            return totalHeight;
        }

        /// <summary>
        /// Parse grid-template-columns string into column widths.
        /// </summary>
        public static List<float> ParseGridTemplate(string template, float containerWidth)
        {
            var result = new List<float>();
            if (string.IsNullOrWhiteSpace(template)) return result;

            // Handle repeat()
            template = ExpandRepeat(template, containerWidth);

            var parts = template.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            float totalFr = 0;
            var frIndices = new List<int>();
            float usedWidth = 0;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();

                if (part.EndsWith("fr"))
                {
                    if (float.TryParse(part.Replace("fr", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var fr))
                    {
                        result.Add(0); // Placeholder
                        frIndices.Add(i);
                        totalFr += fr;
                    }
                }
                else if (part.EndsWith("px"))
                {
                    if (float.TryParse(part.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var px))
                    {
                        result.Add(px);
                        usedWidth += px;
                    }
                }
                else if (part.EndsWith("%"))
                {
                    if (float.TryParse(part.Replace("%", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                    {
                        float w = containerWidth * pct / 100;
                        result.Add(w);
                        usedWidth += w;
                    }
                }
                else if (part == "auto")
                {
                    result.Add(100); // Default auto width
                    usedWidth += 100;
                }
                else if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var val))
                {
                    result.Add(val);
                    usedWidth += val;
                }
            }

            // Distribute fr units
            if (totalFr > 0 && frIndices.Count > 0)
            {
                float freeSpace = containerWidth - usedWidth;
                if (freeSpace < 0) freeSpace = 0;

                for (int j = 0; j < frIndices.Count; j++)
                {
                    int idx = frIndices[j];
                    string part = parts[idx].Trim().Replace("fr", "");
                    if (float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var fr))
                    {
                        result[idx] = (fr / totalFr) * freeSpace;
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Expand repeat() function in grid template.
        /// </summary>
        private static string ExpandRepeat(string template, float containerWidth)
        {
            var repeatMatch = Regex.Match(template, @"repeat\s*\(\s*(\d+|auto-fill|auto-fit)\s*,\s*(.+?)\s*\)");
            if (!repeatMatch.Success) return template;

            string countStr = repeatMatch.Groups[1].Value;
            string trackStr = repeatMatch.Groups[2].Value;

            int count = 0;
            if (countStr == "auto-fill" || countStr == "auto-fit")
            {
                // Estimate based on minmax or fixed width
                float minWidth = 100;
                var minmaxMatch = Regex.Match(trackStr, @"minmax\s*\(\s*([^,]+),\s*([^)]+)\s*\)");
                if (minmaxMatch.Success)
                {
                    string minStr = minmaxMatch.Groups[1].Value.Trim();
                    if (minStr.EndsWith("px"))
                    {
                        float.TryParse(minStr.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out minWidth);
                    }
                    trackStr = "1fr";
                }
                count = Math.Max(1, (int)(containerWidth / minWidth));
            }
            else
            {
                int.TryParse(countStr, out count);
            }

            if (count <= 0) count = 1;
            string expanded = string.Join(" ", Enumerable.Repeat(trackStr, count));
            return template.Substring(0, repeatMatch.Index) + expanded + template.Substring(repeatMatch.Index + repeatMatch.Length);
        }

        /// <summary>
        /// Parse grid-template-areas string into row/column area names.
        /// </summary>
        private static List<List<string>> ParseGridTemplateAreas(string areasStr)
        {
            var result = new List<List<string>>();
            if (string.IsNullOrWhiteSpace(areasStr)) return result;

            var matches = Regex.Matches(areasStr, "\"([^\"]+)\"");
            foreach (Match match in matches)
            {
                var row = match.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                result.Add(row);
            }
            return result;
        }

        /// <summary>
        /// Find the bounds of a named grid area.
        /// </summary>
        private static (int row, int col, int rowSpan, int colSpan)? FindGridAreaBounds(List<List<string>> areas, string areaName)
        {
            if (areas.Count == 0) return null;

            int startRow = -1, endRow = -1, startCol = -1, endCol = -1;

            for (int r = 0; r < areas.Count; r++)
            {
                for (int c = 0; c < areas[r].Count; c++)
                {
                    if (areas[r][c] == areaName)
                    {
                        if (startRow == -1) startRow = r;
                        if (startCol == -1 || c < startCol) startCol = c;
                        endRow = r;
                        if (c > endCol) endCol = c;
                    }
                }
            }

            if (startRow == -1) return null;
            return (startRow, startCol, endRow - startRow + 1, endCol - startCol + 1);
        }
    }
}
