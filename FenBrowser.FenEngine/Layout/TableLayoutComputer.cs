using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Handles the complex multi-pass layout algorithm for HTML tables.
    /// Supports fixed and auto layout, colspan, and rowspan.
    /// </summary>
    public static class TableLayoutComputer
    {
        public delegate LayoutMetrics MeasureFunc(Node node, SKSize availableSize, int depth);

        public class TableCellSlot : IDisposable
        {
            public Element Element;
            public int ColumnIndex; // Added to track actual grid position
            public int ColSpan;
            public int RowSpan;
            public float MinWidth;
            public float MaxWidth;
            public float CalculatedWidth;
            public float CalculatedHeight;
            public void Dispose() {}
        }

        public class TableGridState
        {
            public List<List<TableCellSlot>> Rows = new List<List<TableCellSlot>>();
            public int ColumnCount = 0;
            public List<float> ColumnWidths = new List<float>();
            public List<float> RowHeights = new List<float>();
        }

        public static LayoutMetrics Measure(
            Element table,
            SKSize availableSize,
            IReadOnlyDictionary<Node, CssComputed> styles,
            MeasureFunc measureNode,
            int depth,
            out TableGridState state)
        {
            state = new TableGridState();
            if (table == null || table.ChildNodes == null) return new LayoutMetrics();

            // 1. Build Grid Matrix (Handle colspan/rowspan)
            state = BuildGrid(table, styles);
            if (state.Rows.Count == 0 || state.ColumnCount == 0) return new LayoutMetrics();

            // Resolve table style
            var tableStyle = styles.ContainsKey(table) ? styles[table] : null;

            // 2. Measure Column Widths (Step 1 of Table Algorithm)
            MeasureColumns(state, availableSize, tableStyle, styles, measureNode, depth);

            // 3. Measure Row Heights (Step 2 of Table Algorithm)
            MeasureRows(state, styles, measureNode, depth);

            // 4. Calculate Total Dimensions (including border-spacing)
            // Parse border-spacing for Measure phase too
            float spacingX = 2, spacingY = 2;
            if (tableStyle != null)
            {
                if (string.Equals(tableStyle.BorderCollapse, "collapse", StringComparison.OrdinalIgnoreCase))
                {
                    spacingX = 0;
                    spacingY = 0;
                }
                else if (!string.IsNullOrEmpty(tableStyle.BorderSpacing))
                {
                    var parts = tableStyle.BorderSpacing.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                    {
                        spacingX = ParsePx(parts[0]);
                        spacingY = parts.Length >= 2 ? ParsePx(parts[1]) : spacingX;
                    }
                }
            }

            // Table width = sum of columns + spacing between cells + spacing at edges
            float tableWidth = state.ColumnWidths.Sum() + (state.ColumnCount + 1) * spacingX;
            float tableHeight = state.RowHeights.Sum() + (state.Rows.Count + 1) * spacingY;

            return new LayoutMetrics
            {
                ContentHeight = tableHeight,
                ActualHeight = tableHeight,
                MaxChildWidth = tableWidth
            };
        }

        public static void Arrange(
            Element table,
            SKRect bounds,
            TableGridState state,
            IReadOnlyDictionary<Node, CssComputed> styles,
            IDictionary<Node, BoxModel> boxes,
            Action<Node, SKRect, int> arrangeNode,
            int depth)
        {
            if (table == null || state == null || state.Rows.Count == 0) return;

            // Table Box Logic
            var tableStyle = styles.ContainsKey(table) ? styles[table] : null;
            
            float spacingX = 2;
            float spacingY = 2;
            
            if (tableStyle != null)
            {
                // Parse border-spacing
                if (string.Equals(tableStyle.BorderCollapse, "collapse", StringComparison.OrdinalIgnoreCase))
                {
                    spacingX = 0;
                    spacingY = 0;
                }
                else if (!string.IsNullOrEmpty(tableStyle.BorderSpacing))
                {
                    // Parse "5px" or "5px 10px" format
                    var parts = tableStyle.BorderSpacing.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 1)
                    {
                        spacingX = ParsePx(parts[0]);
                        spacingY = parts.Length >= 2 ? ParsePx(parts[1]) : spacingX;
                    }
                }
            }

            float currentY = bounds.Top + spacingY; // Start after top spacing
            
            // Track section bounds (TBODY, THEAD, TFOOT)
            var sectionBounds = new Dictionary<Element, SKRect>();
            var trs = GetRows(table);

            for (int r = 0; r < state.Rows.Count; r++)
            {
                float rowHeight = state.RowHeights[r];
                if (r < trs.Count)
                {
                    var tr = trs[r];
                    // Row box usually wraps cells. Using minimal rect logic here.
                    var trRect = new SKRect(bounds.Left, currentY, bounds.Left + state.ColumnWidths.Sum() + (state.ColumnCount + 1) * spacingX, currentY + rowHeight);
                    boxes[tr] = new BoxModel { BorderBox = trRect, ContentBox = trRect }; 

                    // Update section bounds
                    if (tr.ParentElement is Element parent && parent != table)
                    {
                        if (!sectionBounds.ContainsKey(parent)) sectionBounds[parent] = trRect;
                        else
                        {
                            var old = sectionBounds[parent];
                            sectionBounds[parent] = new SKRect(
                                Math.Min(old.Left, trRect.Left), 
                                Math.Min(old.Top, trRect.Top), 
                                Math.Max(old.Right, trRect.Right), 
                                Math.Max(old.Bottom, trRect.Bottom));
                        }
                    }
                }

                float currentX = bounds.Left + spacingX; // Start after left spacing
                // No specific currentX increment needed inside loop as we calc absolute X per slot
                
                foreach (var slot in state.Rows[r])
                {
                    // Calculate absolute X position based on slot.ColumnIndex
                    float cellX = bounds.Left + spacingX;
                    for (int i = 0; i < slot.ColumnIndex; i++)
                    {
                        cellX += state.ColumnWidths[i] + spacingX;
                    }
                    
                    // Calculate cell width across colspans (plus spacing between spanned cells)
                    float cellWidth = 0;
                    for (int i = 0; i < slot.ColSpan; i++) 
                    {
                        if (slot.ColumnIndex + i < state.ColumnWidths.Count)
                             cellWidth += state.ColumnWidths[slot.ColumnIndex + i];
                        if (i < slot.ColSpan - 1) cellWidth += spacingX; 
                    }

                    // Calculate cell height across rowspans (plus spacing)
                    float cellHeight = 0;
                    for (int i = 0; i < slot.RowSpan; i++) 
                    {
                        cellHeight += state.RowHeights[Math.Min(r + i, state.RowHeights.Count - 1)];
                        if (i < slot.RowSpan - 1) cellHeight += spacingY;
                    }

                    var cellRect = new SKRect(cellX, currentY, cellX + cellWidth, currentY + cellHeight);
                    
                    // Create box for the cell
                    var box = new BoxModel { BorderBox = cellRect, ContentBox = cellRect };
                    boxes[slot.Element] = box;

                    // Arrange cell content
                    arrangeNode(slot.Element, cellRect, depth + 1);
                }
                currentY += state.RowHeights[r] + spacingY;
            }

            // Assign boxes to sections
            foreach(var kvp in sectionBounds)
            {
                boxes[kvp.Key] = new BoxModel { BorderBox = kvp.Value, ContentBox = kvp.Value };
            }
        }

        private static TableGridState BuildGrid(Element table, IReadOnlyDictionary<Node, CssComputed> styles)
        {
            var grid = new TableGridState();
            var trs = GetRows(table);
            
            // Temporary matrix to track occupied slots (row -> col -> occupied)
            var occupied = new Dictionary<int, HashSet<int>>();

            for (int r = 0; r < trs.Count; r++)
            {
                var tr = trs[r];
                if (!occupied.ContainsKey(r)) occupied[r] = new HashSet<int>();
                
                int cOffset = 0;
                var cells = tr.ChildNodes.OfType<Element>().Where(e => e.TagName == "TD" || e.TagName == "TH").ToList();

                if (grid.Rows.Count <= r) grid.Rows.Add(new List<TableCellSlot>());

                foreach (var cell in cells)
                {
                    // Find next free column in this row
                    while (occupied[r].Contains(cOffset)) cOffset++;

                    int colspan = Math.Max(1, ParseInt(cell.GetAttribute("colspan"), 1));
                    int rowspan = Math.Max(1, ParseInt(cell.GetAttribute("rowspan"), 1));

                    var slot = new TableCellSlot 
                    { 
                        Element = cell, 
                        ColumnIndex = cOffset,
                        ColSpan = colspan, 
                        RowSpan = rowspan 
                    };

                    // Mark slots as occupied
                    for (int rr = 0; rr < rowspan; rr++)
                    {
                        int targetRow = r + rr;
                        if (!occupied.ContainsKey(targetRow)) occupied[targetRow] = new HashSet<int>();
                        for (int cc = 0; cc < colspan; cc++)
                        {
                            occupied[targetRow].Add(cOffset + cc);
                        }
                    }

                    grid.Rows[r].Add(slot);
                    grid.ColumnCount = Math.Max(grid.ColumnCount, cOffset + colspan);
                    cOffset += colspan;
                }
            }

            return grid;
        }

        private static void MeasureColumns(TableGridState grid, SKSize availableSize, CssComputed tableStyle, IReadOnlyDictionary<Node, CssComputed> styles, MeasureFunc measureNode, int depth)
        {
            float totalTableWidth = -1;
            bool isFixed = string.Equals(tableStyle?.TableLayout, "fixed", StringComparison.OrdinalIgnoreCase);

            if (tableStyle != null)
            {
                if (tableStyle.Width.HasValue && tableStyle.Width.Value > 0)
                {
                     totalTableWidth = (float)tableStyle.Width.Value;
                }
                else if (tableStyle.WidthPercent.HasValue)
                {
                     totalTableWidth = (float)tableStyle.WidthPercent.Value / 100f * availableSize.Width;
                }
            }

            for (int i = 0; i < grid.ColumnCount; i++) grid.ColumnWidths.Add(0);

            // FIXED LAYOUT PATH
            if (isFixed && totalTableWidth > 0 && grid.Rows.Count > 0)
            {
                 // Use first row to define columns
                 var firstRow = grid.Rows[0];
                 int colIdx = 0;
                 float allocated = 0;
                 int autoCols = 0;
                 // Track which columns are 'auto'
                 var isAutoCol = new bool[grid.ColumnCount];
                 
                 foreach(var slot in firstRow)
                 {
                     var cellStyle = styles.ContainsKey(slot.Element) ? styles[slot.Element] : null;
                     float w = 0;
                     if (cellStyle != null && cellStyle.Width.HasValue) w = (float)cellStyle.Width.Value;
                     
                     if (w > 0)
                     {
                         float perCol = w / slot.ColSpan;
                         for(int k=0; k<slot.ColSpan; k++) 
                         {
                             if (colIdx + k < grid.ColumnCount) 
                             {
                                 grid.ColumnWidths[colIdx + k] = perCol;
                             }
                         }
                         allocated += w;
                     }
                     else
                     {
                         for(int k=0; k<slot.ColSpan; k++) 
                         {
                             if (colIdx + k < grid.ColumnCount) isAutoCol[colIdx+k] = true;
                         }
                         autoCols += slot.ColSpan;
                     }
                     colIdx += slot.ColSpan;
                 }
                 
                 // Distribute remainder to auto columns
                 if (autoCols > 0 && allocated < totalTableWidth)
                 {
                     float remain = totalTableWidth - allocated;
                     float perAuto = remain / autoCols;
                     for(int i=0; i<grid.ColumnCount; i++)
                     {
                         if (isAutoCol[i]) grid.ColumnWidths[i] = perAuto;
                     }
                 }
                 return;
            }

            // AUTO LAYOUT PATH
            
            // Pass 1: Measure Max Content Width
            var spanningCells = new List<(TableCellSlot Slot, int ColIdx)>();
            
            for (int r = 0; r < grid.Rows.Count; r++)
            {
                int colIdx = 0;
                foreach (var slot in grid.Rows[r])
                {
                    // For min-width, we ideally need 'min-content'. Using 0 or minimal as proxy.
                    // For max-width, use infinite available space.
                    var metrics = measureNode(slot.Element, new SKSize(float.PositiveInfinity, float.PositiveInfinity), depth + 1);
                    slot.MaxWidth = metrics.MaxChildWidth;
                    
                    if (slot.ColSpan == 1)
                    {
                        grid.ColumnWidths[colIdx] = Math.Max(grid.ColumnWidths[colIdx], slot.MaxWidth);
                    }
                    else
                    {
                        spanningCells.Add((slot, colIdx));
                    }
                    colIdx += slot.ColSpan;
                }
            }

            // Pass 2: Spanning cells contribution
            foreach (var (slot, startCol) in spanningCells)
            {
                float currentSpanWidth = 0;
                for (int i = 0; i < slot.ColSpan; i++) currentSpanWidth += grid.ColumnWidths[startCol + i];

                if (currentSpanWidth < slot.MaxWidth)
                {
                    float deficit = (slot.MaxWidth + 1.0f) - currentSpanWidth;
                    float addPerCol = deficit / slot.ColSpan;
                    for (int i = 0; i < slot.ColSpan; i++) grid.ColumnWidths[startCol + i] += addPerCol;
                }
            }
            
            // Pass 3: Expansion to full width
            // If table has explicit width OR we are filling available block width (if table is 100% by default? No, table is auto)
            // But if totalTableWidth is set (e.g. style="width:100%"), expand to it.
            
            float currentSum = grid.ColumnWidths.Sum();
            if (totalTableWidth > 0 && currentSum < totalTableWidth)
            {
                float deficit = totalTableWidth - currentSum;
                if (currentSum > 1) // Avoid div by zero
                {
                    // Distribute proportionally
                    for(int i=0; i<grid.ColumnCount; i++)
                    {
                        float ratio = grid.ColumnWidths[i] / currentSum;
                        grid.ColumnWidths[i] += deficit * ratio;
                    }
                }
                else
                {
                    // Distribute evenly
                    float perCol = totalTableWidth / Math.Max(1, grid.ColumnCount);
                    for(int i=0; i<grid.ColumnCount; i++) grid.ColumnWidths[i] = perCol;
                }
            }
        }

        private static void MeasureRows(TableGridState grid, IReadOnlyDictionary<Node, CssComputed> styles, MeasureFunc measureNode, int depth)
        {
            for (int r = 0; r < grid.Rows.Count; r++)
            {
                float maxRowHeight = 0;
                int colIdx = 0;
                foreach (var slot in grid.Rows[r])
                {
                    // Calculate current spanning width
                    float spanWidth = 0;
                    for (int i = 0; i < slot.ColSpan; i++) spanWidth += grid.ColumnWidths[colIdx + i];

                    // Measure height with fixed width
                    var metrics = measureNode(slot.Element, new SKSize(spanWidth, float.PositiveInfinity), depth + 1);
                    slot.CalculatedHeight = metrics.ContentHeight;

                    if (slot.RowSpan == 1)
                    {
                        maxRowHeight = Math.Max(maxRowHeight, slot.CalculatedHeight);
                    }
                    colIdx += slot.ColSpan;
                }
                grid.RowHeights.Add(Math.Max(maxRowHeight, 20)); // Ensure min height
            }
        }

        private static List<Element> GetRows(Element table)
        {
            var trs = new List<Element>();
            foreach (var child in table.ChildNodes.OfType<Element>())
            {
                if (child.TagName == "TR") trs.Add(child);
                else if (child.TagName == "THEAD" || child.TagName == "TBODY" || child.TagName == "TFOOT")
                {
                    trs.AddRange(child.ChildNodes.OfType<Element>().Where(e => e.TagName == "TR"));
                }
            }
            return trs;
        }

        private static int ParseInt(string s, int def)
        {
            if (int.TryParse(s, out int v)) return v;
            return def;
        }

        private static float ParsePx(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            s = s.Trim().ToLowerInvariant();
            if (s.EndsWith("px")) s = s.Substring(0, s.Length - 2);
            if (float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float v)) return v;
            return 0;
        }
    }
}

