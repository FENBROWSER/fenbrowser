using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    /// <summary>
    /// Minimal table formatting support for the active box-tree pipeline.
    /// Handles display:table containers, row groups, rows, and direct anonymous-row cases.
    /// </summary>
    public sealed class TableFormattingContext : FormattingContext
    {
        private static TableFormattingContext _instance;
        public static TableFormattingContext Instance => _instance ??= new TableFormattingContext();

        protected override void LayoutCore(LayoutBox box, LayoutState state)
        {
            if (box == null)
            {
                return;
            }

            LayoutBoxOps.ResetSubtreeToOrigin(box);

            string display = box.ComputedStyle?.Display;
            if (!string.IsNullOrEmpty(display))
            {
                if (display.Equals("table-row-group", StringComparison.OrdinalIgnoreCase) ||
                    display.Equals("table-header-group", StringComparison.OrdinalIgnoreCase) ||
                    display.Equals("table-footer-group", StringComparison.OrdinalIgnoreCase))
                {
                    LayoutRowGroup(box, state);
                    return;
                }
                if (display.Equals("table-row", StringComparison.OrdinalIgnoreCase))
                {
                    LayoutRow(box, state);
                    return;
                }
            }
            LayoutTable(box, state);
        }

        private void LayoutTable(LayoutBox tableBox, LayoutState state)
        {
            InitializeBox(tableBox);

            var rows = NormalizeRows(tableBox);
            var grid = BuildSlotGrid(rows);
            var style = tableBox.ComputedStyle;
            float specifiedWidth = ResolveSpecifiedWidth(style, state);
            float specifiedHeight = ResolveSpecifiedHeight(style, state);
            float[] columnWidths = MeasureColumnWidths(grid, state);
            float intrinsicWidth = columnWidths.Sum();

            if (specifiedWidth > intrinsicWidth && columnWidths.Length > 0)
            {
                float extra = specifiedWidth - intrinsicWidth;
                float perColumn = extra / columnWidths.Length;
                for (int i = 0; i < columnWidths.Length; i++)
                {
                    columnWidths[i] += perColumn;
                }
            }

            float[] rowHeights = MeasureRowHeights(grid, columnWidths, state);
            float contentWidth = Math.Max(specifiedWidth, intrinsicWidth);
            float currentY = tableBox.Geometry.ContentBox.Top;
            float contentLeft = tableBox.Geometry.ContentBox.Left;
            float maxRight = contentLeft;

            for (int rowIdx = 0; rowIdx < grid.Count; rowIdx++)
            {
                var row = grid[rowIdx];
                float rowHeight = rowHeights[rowIdx];

                foreach (var slot in row.Slots)
                {
                    float cellWidth = SumColumnWidths(columnWidths, slot.ColumnIndex, slot.ColSpan);
                    float spannedRowHeight = SumRowHeights(rowHeights, rowIdx, slot.RowSpan);

                    // PERF: MeasureRowHeights already laid out single-row cells with the
                    // final cellWidth — re-laying them out here would do the same work twice.
                    // For rowspanning cells, the spannedRowHeight may differ from the probe
                    // height so we re-run layout. Either way, position the subtree at the
                    // final (cellX, currentY) and let StretchBorderHeight handle vertical
                    // stretching to match the spanned row height.
                    if (slot.RowSpan > 1)
                    {
                        LayoutCell(slot.Cell, cellWidth, spannedRowHeight, state);
                    }
                    float cellX = contentLeft + SumColumnWidths(columnWidths, 0, slot.ColumnIndex);
                    LayoutBoxOps.PositionSubtree(slot.Cell, cellX, currentY, CreateChildState(cellWidth, spannedRowHeight, state));

                    if (IsTableCell(slot.Cell))
                    {
                        StretchBorderHeight(slot.Cell, spannedRowHeight);
                    }

                    maxRight = Math.Max(maxRight, cellX + Math.Max(cellWidth, slot.Cell.Geometry.MarginBox.Width));
                }

                if (row.RowBox != null)
                {
                    float rowWidth = columnWidths.Sum();
                    InitializeBox(row.RowBox);
                    SetContentSize(row.RowBox, rowWidth, rowHeight);
                    LayoutBoxOps.PositionSubtree(row.RowBox, contentLeft, currentY, CreateChildState(rowWidth, rowHeight, state));
                }

                currentY += rowHeight;
            }

            float measuredWidth = Math.Max(contentWidth, Math.Max(0f, maxRight - contentLeft));
            float measuredHeight = Math.Max(specifiedHeight, Math.Max(0f, currentY - tableBox.Geometry.ContentBox.Top));
            SetContentSize(tableBox, measuredWidth, measuredHeight);

            foreach (var group in tableBox.Children.Where(IsRowGroup))
            {
                UpdateGroupBounds(group);
            }
        }

        private static float SumColumnWidths(float[] columnWidths, int start, int count)
        {
            float sum = 0f;
            int end = Math.Min(columnWidths.Length, start + count);
            for (int i = Math.Max(0, start); i < end; i++)
            {
                sum += columnWidths[i];
            }
            return sum;
        }

        private static float SumRowHeights(float[] rowHeights, int start, int count)
        {
            float sum = 0f;
            int end = Math.Min(rowHeights.Length, start + count);
            for (int i = Math.Max(0, start); i < end; i++)
            {
                sum += rowHeights[i];
            }
            return sum;
        }

        private static List<TableGridRow> BuildSlotGrid(List<TableRowModel> rows)
        {
            // Occupancy[(rowIdx, colIdx)] = true if a previously-placed cell still
            // occupies that slot (via rowspan).
            var occupied = new HashSet<(int row, int col)>();
            var grid = new List<TableGridRow>();
            for (int r = 0; r < rows.Count; r++)
            {
                var slots = new List<TableSlot>();
                int col = 0;
                foreach (var cell in rows[r].Cells)
                {
                    int colspan = Math.Max(1, GetSpanAttribute(cell, "colspan"));
                    int rowspan = Math.Max(1, GetSpanAttribute(cell, "rowspan"));

                    // Skip columns already claimed by a previous row's rowspan.
                    while (occupied.Contains((r, col))) col++;

                    slots.Add(new TableSlot(cell, col, colspan, rowspan));
                    for (int rr = r; rr < r + rowspan; rr++)
                    {
                        for (int cc = col; cc < col + colspan; cc++)
                        {
                            occupied.Add((rr, cc));
                        }
                    }
                    col += colspan;
                }
                grid.Add(new TableGridRow(rows[r].RowBox, slots));
            }
            return grid;
        }

        private static int GetSpanAttribute(LayoutBox cell, string attribute)
        {
            if (cell?.SourceNode is not FenBrowser.Core.Dom.V2.Element element)
            {
                return 1;
            }
            string raw = element.GetAttribute(attribute);
            if (string.IsNullOrWhiteSpace(raw)) return 1;
            return int.TryParse(raw.Trim(), out int value) && value >= 1 ? value : 1;
        }

        private void LayoutRowGroup(LayoutBox groupBox, LayoutState state)
        {
            InitializeBox(groupBox);
            float currentY = groupBox.Geometry.ContentBox.Top;
            float left = groupBox.Geometry.ContentBox.Left;
            float maxWidth = 0f;

            foreach (var child in groupBox.Children.Where(IsTableRow))
            {
                FormattingContext.Resolve(child).Layout(child, state);
                LayoutBoxOps.PositionSubtree(child, left, currentY, state);
                currentY += child.Geometry.MarginBox.Height;
                maxWidth = Math.Max(maxWidth, child.Geometry.MarginBox.Width);
            }

            SetContentSize(groupBox, maxWidth, Math.Max(0f, currentY - groupBox.Geometry.ContentBox.Top));
        }

        private void LayoutRow(LayoutBox rowBox, LayoutState state)
        {
            InitializeBox(rowBox);
            float currentX = rowBox.Geometry.ContentBox.Left;
            float top = rowBox.Geometry.ContentBox.Top;
            float rowHeight = 0f;

            foreach (var child in rowBox.Children)
            {
                FormattingContext.Resolve(child).Layout(child, state);
                LayoutBoxOps.PositionSubtree(child, currentX, top, state);
                currentX += child.Geometry.MarginBox.Width;
                rowHeight = Math.Max(rowHeight, child.Geometry.MarginBox.Height);
            }

            SetContentSize(rowBox, Math.Max(0f, currentX - rowBox.Geometry.ContentBox.Left), rowHeight);
        }

        private float[] MeasureColumnWidths(List<TableGridRow> grid, LayoutState state)
        {
            int columnCount = grid.Count == 0
                ? 0
                : grid.Max(r => r.Slots.Count == 0 ? 0 : r.Slots.Max(s => s.ColumnIndex + s.ColSpan));
            var widths = new float[columnCount];

            // First pass: non-spanning cells set their column width directly.
            foreach (var row in grid)
            {
                foreach (var slot in row.Slots)
                {
                    if (slot.ColSpan != 1) continue;
                    float preferredWidth = MeasurePreferredWidth(slot.Cell, state);
                    widths[slot.ColumnIndex] = Math.Max(widths[slot.ColumnIndex], preferredWidth);
                }
            }

            // Second pass: spanning cells distribute additional width to their
            // spanned columns proportionally if existing widths don't add up.
            foreach (var row in grid)
            {
                foreach (var slot in row.Slots)
                {
                    if (slot.ColSpan <= 1) continue;
                    float preferred = MeasurePreferredWidth(slot.Cell, state);
                    float existing = SumColumnWidths(widths, slot.ColumnIndex, slot.ColSpan);
                    if (preferred > existing && slot.ColSpan > 0)
                    {
                        float extra = (preferred - existing) / slot.ColSpan;
                        for (int i = slot.ColumnIndex; i < slot.ColumnIndex + slot.ColSpan && i < widths.Length; i++)
                        {
                            widths[i] += extra;
                        }
                    }
                }
            }

            return widths;
        }

        private float[] MeasureRowHeights(List<TableGridRow> grid, float[] columnWidths, LayoutState state)
        {
            var heights = new float[grid.Count];
            // First pass: single-row cells.
            for (int r = 0; r < grid.Count; r++)
            {
                foreach (var slot in grid[r].Slots)
                {
                    if (slot.RowSpan != 1) continue;
                    float cellWidth = SumColumnWidths(columnWidths, slot.ColumnIndex, slot.ColSpan);
                    LayoutCell(slot.Cell, cellWidth, float.NaN, state);
                    heights[r] = Math.Max(heights[r], slot.Cell.Geometry.MarginBox.Height);
                }
            }
            // Second pass: rowspanning cells must fit across their spanned rows.
            for (int r = 0; r < grid.Count; r++)
            {
                foreach (var slot in grid[r].Slots)
                {
                    if (slot.RowSpan <= 1) continue;
                    float cellWidth = SumColumnWidths(columnWidths, slot.ColumnIndex, slot.ColSpan);
                    LayoutCell(slot.Cell, cellWidth, float.NaN, state);
                    float needed = slot.Cell.Geometry.MarginBox.Height;
                    float existing = SumRowHeights(heights, r, slot.RowSpan);
                    if (needed > existing && slot.RowSpan > 0)
                    {
                        float extra = (needed - existing) / slot.RowSpan;
                        for (int i = r; i < r + slot.RowSpan && i < heights.Length; i++)
                        {
                            heights[i] += extra;
                        }
                    }
                }
            }
            return heights;
        }

        private void LayoutCell(LayoutBox cell, float cellWidth, float rowHeight, LayoutState state)
        {
            var style = cell.ComputedStyle;
            bool empty = cell.Children.Count == 0 &&
                         !(cell is TextLayoutBox) &&
                         !(cell.SourceNode is FenBrowser.Core.Dom.V2.Text);

            if (empty && !HasSpecifiedInlineSize(style) && !HasSpecifiedBlockSize(style))
            {
                InitializeBox(cell);
                SetContentSize(cell, 0f, 0f);
                return;
            }

            var childState = CreateChildState(cellWidth, rowHeight, state);
            FormattingContext.Resolve(cell).Layout(cell, childState);
        }

        private static float MeasurePreferredWidth(LayoutBox cell, LayoutState state)
        {
            var style = cell.ComputedStyle;
            float specifiedWidth = ResolveSpecifiedWidth(style, state);
            if (specifiedWidth > 0f)
            {
                return specifiedWidth;
            }

            bool empty = cell.Children.Count == 0 &&
                         !(cell is TextLayoutBox) &&
                         !(cell.SourceNode is FenBrowser.Core.Dom.V2.Text);
            if (empty)
            {
                return 0f;
            }

            var probeState = CreateChildState(float.PositiveInfinity, float.PositiveInfinity, state);
            FormattingContext.Resolve(cell).Layout(cell, probeState);
            return Math.Max(0f, cell.Geometry.MarginBox.Width);
        }

        private static List<TableRowModel> NormalizeRows(LayoutBox tableBox)
        {
            var rows = new List<TableRowModel>();
            var anonymousCells = new List<LayoutBox>();

            foreach (var child in tableBox.Children)
            {
                if (IsRowGroup(child))
                {
                    FlushAnonymousRow(rows, anonymousCells);
                    foreach (var row in child.Children.Where(IsTableRow))
                    {
                        rows.Add(new TableRowModel(row, row.Children.ToList()));
                    }
                    continue;
                }

                if (IsTableRow(child))
                {
                    FlushAnonymousRow(rows, anonymousCells);
                    rows.Add(new TableRowModel(child, child.Children.ToList()));
                    continue;
                }

                anonymousCells.Add(child);
            }

            FlushAnonymousRow(rows, anonymousCells);
            return rows;
        }

        private static void FlushAnonymousRow(List<TableRowModel> rows, List<LayoutBox> anonymousCells)
        {
            if (anonymousCells.Count == 0)
            {
                return;
            }

            rows.Add(new TableRowModel(null, anonymousCells.ToList()));
            anonymousCells.Clear();
        }

        private static void UpdateGroupBounds(LayoutBox group)
        {
            var rows = group.Children.Where(IsTableRow).ToList();
            if (rows.Count == 0)
            {
                return;
            }

            float left = rows.Min(r => r.Geometry.MarginBox.Left);
            float top = rows.Min(r => r.Geometry.MarginBox.Top);
            float right = rows.Max(r => r.Geometry.MarginBox.Right);
            float bottom = rows.Max(r => r.Geometry.MarginBox.Bottom);

            InitializeBox(group);
            group.Geometry.ContentBox = new SKRect(left, top, right, bottom);
            LayoutBoxOps.SyncBoxes(group.Geometry);
        }

        private static void StretchBorderHeight(LayoutBox cell, float borderHeight)
        {
            if (!float.IsFinite(borderHeight) || borderHeight <= 0f)
            {
                return;
            }

            float extras = (float)(cell.Geometry.Padding.Top + cell.Geometry.Padding.Bottom +
                                   cell.Geometry.Border.Top + cell.Geometry.Border.Bottom);
            float targetContentHeight = Math.Max(0f, borderHeight - extras);
            if (targetContentHeight <= cell.Geometry.ContentBox.Height + 0.5f)
            {
                return;
            }

            SetContentSize(cell, cell.Geometry.ContentBox.Width, targetContentHeight);
        }

        private static void InitializeBox(LayoutBox box)
        {
            var style = box.ComputedStyle ?? new CssComputed();
            box.Geometry.Padding = style.Padding;
            box.Geometry.Border = style.BorderThickness;
            box.Geometry.Margin = style.Margin;

            float left = (float)(style.Margin.Left + style.BorderThickness.Left + style.Padding.Left);
            float top = (float)(style.Margin.Top + style.BorderThickness.Top + style.Padding.Top);
            box.Geometry.ContentBox = new SKRect(left, top, left, top);
            box.Geometry.Lines = null;
            LayoutBoxOps.SyncBoxes(box.Geometry);
        }

        private static void SetContentSize(LayoutBox box, float width, float height)
        {
            float left = box.Geometry.ContentBox.Left;
            float top = box.Geometry.ContentBox.Top;
            box.Geometry.ContentBox = new SKRect(left, top, left + Math.Max(0f, width), top + Math.Max(0f, height));
            LayoutBoxOps.SyncBoxes(box.Geometry);
        }

        private static LayoutState CreateChildState(float width, float height, LayoutState parentState)
        {
            float resolvedWidth = width;
            if (!float.IsFinite(resolvedWidth) || resolvedWidth < 0f)
            {
                resolvedWidth = width;
            }

            float resolvedHeight = height;
            if (!float.IsFinite(resolvedHeight) || resolvedHeight <= 0f)
            {
                resolvedHeight = parentState.ContainingBlockHeight > 0f
                    ? parentState.ContainingBlockHeight
                    : parentState.ViewportHeight;
            }

            return new LayoutState(
                new SKSize(width, resolvedHeight),
                float.IsFinite(resolvedWidth) && resolvedWidth > 0f ? resolvedWidth : parentState.ContainingBlockWidth,
                resolvedHeight,
                parentState.ViewportWidth,
                parentState.ViewportHeight,
                parentState.Deadline);
        }

        private static float ResolveSpecifiedWidth(CssComputed style, LayoutState state)
        {
            if (style == null)
            {
                return 0f;
            }

            if (style.Width.HasValue)
            {
                return (float)style.Width.Value;
            }

            if (style.WidthPercent.HasValue)
            {
                float cbWidth = state.ContainingBlockWidth > 0f ? state.ContainingBlockWidth : state.ViewportWidth;
                return (float)(style.WidthPercent.Value / 100d * cbWidth);
            }

            return 0f;
        }

        private static float ResolveSpecifiedHeight(CssComputed style, LayoutState state)
        {
            if (style == null)
            {
                return 0f;
            }

            if (style.Height.HasValue)
            {
                return (float)style.Height.Value;
            }

            if (style.HeightPercent.HasValue)
            {
                float cbHeight = state.ContainingBlockHeight > 0f ? state.ContainingBlockHeight : state.ViewportHeight;
                return (float)(style.HeightPercent.Value / 100d * cbHeight);
            }

            return 0f;
        }

        private static bool HasSpecifiedInlineSize(CssComputed style) =>
            style?.Width.HasValue == true || style?.WidthPercent.HasValue == true;

        private static bool HasSpecifiedBlockSize(CssComputed style) =>
            style?.Height.HasValue == true || style?.HeightPercent.HasValue == true;

        private static bool IsRowGroup(LayoutBox box)
        {
            string display = box?.ComputedStyle?.Display;
            if (!string.IsNullOrEmpty(display))
            {
                // Strings from the CSS pipeline are already trimmed lowercase; fall
                // back to OrdinalIgnoreCase rather than allocating Trim+ToLowerInvariant.
                if (display.Equals("table-row-group", StringComparison.OrdinalIgnoreCase) ||
                    display.Equals("table-header-group", StringComparison.OrdinalIgnoreCase) ||
                    display.Equals("table-footer-group", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            string tag = (box?.SourceNode as FenBrowser.Core.Dom.V2.Element)?.TagName;
            return string.Equals(tag, "TBODY", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "THEAD", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "TFOOT", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTableRow(LayoutBox box)
        {
            if (string.Equals(box?.ComputedStyle?.Display, "table-row", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string tag = (box?.SourceNode as FenBrowser.Core.Dom.V2.Element)?.TagName;
            return string.Equals(tag, "TR", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTableCell(LayoutBox box)
        {
            if (string.Equals(box?.ComputedStyle?.Display, "table-cell", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            string tag = (box?.SourceNode as FenBrowser.Core.Dom.V2.Element)?.TagName;
            return string.Equals(tag, "TD", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(tag, "TH", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record TableRowModel(LayoutBox RowBox, List<LayoutBox> Cells);

        private sealed record TableSlot(LayoutBox Cell, int ColumnIndex, int ColSpan, int RowSpan);

        private sealed record TableGridRow(LayoutBox RowBox, List<TableSlot> Slots);
    }
}
