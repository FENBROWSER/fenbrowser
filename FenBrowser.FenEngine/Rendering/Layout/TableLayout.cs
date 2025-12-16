using SkiaSharp;
using System;
using System.Collections.Generic;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Core;

namespace FenBrowser.FenEngine.Rendering.Layout
{
    /// <summary>
    /// HTML Table layout algorithm implementation.
    /// Computes layout for table elements with row/colspan support.
    /// </summary>
    public static class TableLayout
    {
        /// <summary>
        /// Grid cell data for table layout.
        /// </summary>
        public class TableGridCell
        {
            public LiteElement Element { get; set; }
            public int Row { get; set; }
            public int Col { get; set; }
            public int RowSpan { get; set; } = 1;
            public int ColSpan { get; set; } = 1;
        }

        /// <summary>
        /// Compute table layout for a table element and its rows/cells.
        /// </summary>
        /// <param name="engine">Layout engine for recursive layout calls</param>
        /// <param name="node">The table element</param>
        /// <param name="contentBox">The content box area available</param>
        /// <param name="style">Computed styles for the table</param>
        /// <param name="maxChildWidth">Output: maximum width of table content</param>
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
            float startY = contentBox.Top;

            // 1. Collect Rows and Cells
            var rows = new List<List<LiteElement>>();

            void CollectRows(LiteElement parent)
            {
                if (parent.Children == null) return;
                foreach (var c in parent.Children)
                {
                    string t = c.Tag?.ToUpperInvariant();
                    if (t == "TR")
                    {
                        var cells = new List<LiteElement>();
                        if (c.Children != null)
                        {
                            foreach (var cell in c.Children)
                            {
                                string ct = cell.Tag?.ToUpperInvariant();
                                if (ct == "TD" || ct == "TH") cells.Add(cell);
                            }
                        }
                        rows.Add(cells);
                    }
                    else if (t == "THEAD" || t == "TBODY" || t == "TFOOT")
                    {
                        CollectRows(c);
                    }
                }
            }
            CollectRows(node);

            if (rows.Count == 0) return 0;

            // 2. Build Grid Map (handle rowspan/colspan)
            var occupied = new HashSet<(int, int)>();
            var cellData = new List<TableGridCell>();

            int maxCols = 0;
            int currentRowIndex = 0;

            foreach (var row in rows)
            {
                int currentColIndex = 0;
                foreach (var cell in row)
                {
                    // Find next available slot
                    while (occupied.Contains((currentRowIndex, currentColIndex)))
                    {
                        currentColIndex++;
                    }

                    // Parse span attributes
                    int rowspan = 1;
                    int colspan = 1;
                    if (cell.Attr != null)
                    {
                        if (cell.Attr.TryGetValue("rowspan", out var rs)) int.TryParse(rs, out rowspan);
                        if (cell.Attr.TryGetValue("colspan", out var cs)) int.TryParse(cs, out colspan);
                    }
                    if (rowspan < 1) rowspan = 1;
                    if (colspan < 1) colspan = 1;

                    // Mark occupied cells
                    for (int r = 0; r < rowspan; r++)
                    {
                        for (int c = 0; c < colspan; c++)
                        {
                            occupied.Add((currentRowIndex + r, currentColIndex + c));
                        }
                    }

                    cellData.Add(new TableGridCell
                    {
                        Element = cell,
                        Row = currentRowIndex,
                        Col = currentColIndex,
                        RowSpan = rowspan,
                        ColSpan = colspan
                    });

                    if (currentColIndex + colspan > maxCols) maxCols = currentColIndex + colspan;
                    currentColIndex += colspan;
                }
                currentRowIndex++;
            }

            // 3. Measure Column Widths (intrinsic sizing)
            float[] colWidths = new float[maxCols];

            // First pass: Measure single-column cells
            foreach (var cd in cellData)
            {
                if (cd.ColSpan == 1)
                {
                    engine.ComputeLayout(cd.Element, 0, 0, 10000, shrinkToContent: true);
                    var box = ctx.GetBox(cd.Element);
                    if (box != null && box.MarginBox.Width > colWidths[cd.Col])
                    {
                        colWidths[cd.Col] = box.MarginBox.Width;
                    }
                }
            }

            // Ensure minimum width
            for (int i = 0; i < maxCols; i++)
            {
                if (colWidths[i] < 10) colWidths[i] = 10;
            }

            // 4. Calculate Column X Positions
            float[] colX = new float[maxCols + 1];
            float cx = contentBox.Left;
            for (int i = 0; i < maxCols; i++)
            {
                colX[i] = cx;
                cx += colWidths[i];
            }
            colX[maxCols] = cx;

            if (cx - contentBox.Left > maxChildWidth) maxChildWidth = cx - contentBox.Left;

            // 5. Layout Cells and Calculate Row Heights
            float[] rowHeights = new float[currentRowIndex];
            float[] rowY = new float[currentRowIndex + 1];

            foreach (var cd in cellData)
            {
                float w = colX[cd.Col + cd.ColSpan] - colX[cd.Col];
                engine.ComputeLayout(cd.Element, 0, 0, w, shrinkToContent: false);

                var box = ctx.GetBox(cd.Element);
                if (box != null)
                {
                    if (cd.RowSpan == 1)
                    {
                        if (box.MarginBox.Height > rowHeights[cd.Row])
                            rowHeights[cd.Row] = box.MarginBox.Height;
                    }
                }
            }

            // Ensure minimum row height
            for (int i = 0; i < currentRowIndex; i++)
            {
                if (rowHeights[i] < 16) rowHeights[i] = 16;
            }

            // Calculate row Y positions
            float ry = startY;
            for (int i = 0; i < currentRowIndex; i++)
            {
                rowY[i] = ry;
                ry += rowHeights[i];
            }
            rowY[currentRowIndex] = ry;

            // 6. Position Cells at Final Locations
            foreach (var cd in cellData)
            {
                float x = colX[cd.Col];
                float y = rowY[cd.Row];
                float w = colX[cd.Col + cd.ColSpan] - colX[cd.Col];
                float h = rowY[cd.Row + cd.RowSpan] - rowY[cd.Row];

                engine.ComputeLayout(cd.Element, x, y, w, shrinkToContent: false);
            }

            return rowY[currentRowIndex] - startY;
        }
    }
}
