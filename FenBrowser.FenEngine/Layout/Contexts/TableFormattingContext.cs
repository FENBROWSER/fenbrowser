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

            string display = box.ComputedStyle?.Display?.Trim().ToLowerInvariant() ?? "table";
            switch (display)
            {
                case "table-row-group":
                case "table-header-group":
                case "table-footer-group":
                    LayoutRowGroup(box, state);
                    return;
                case "table-row":
                    LayoutRow(box, state);
                    return;
                default:
                    LayoutTable(box, state);
                    return;
            }
        }

        private void LayoutTable(LayoutBox tableBox, LayoutState state)
        {
            InitializeBox(tableBox);

            var rows = NormalizeRows(tableBox);
            var style = tableBox.ComputedStyle;
            float specifiedWidth = ResolveSpecifiedWidth(style, state);
            float specifiedHeight = ResolveSpecifiedHeight(style, state);
            float[] columnWidths = MeasureColumnWidths(rows, state);
            float intrinsicWidth = columnWidths.Sum();

            if (specifiedWidth > intrinsicWidth && rows.Count > 0)
            {
                float extra = specifiedWidth - intrinsicWidth;
                float perColumn = extra / Math.Max(1, columnWidths.Length);
                for (int i = 0; i < columnWidths.Length; i++)
                {
                    columnWidths[i] += perColumn;
                }
            }

            float contentWidth = Math.Max(specifiedWidth, intrinsicWidth);
            float currentY = tableBox.Geometry.ContentBox.Top;
            float contentLeft = tableBox.Geometry.ContentBox.Left;
            float maxRight = contentLeft;

            foreach (var row in rows)
            {
                float rowHeight = MeasureRowHeight(row, columnWidths, state);
                float currentX = contentLeft;

                for (int i = 0; i < row.Cells.Count; i++)
                {
                    var cell = row.Cells[i];
                    float cellWidth = i < columnWidths.Length ? columnWidths[i] : 0f;
                    LayoutCell(cell, cellWidth, rowHeight, state);
                    LayoutBoxOps.PositionSubtree(cell, currentX, currentY, CreateChildState(cellWidth, rowHeight, state));

                    if (IsTableCell(cell))
                    {
                        StretchBorderHeight(cell, rowHeight);
                    }

                    maxRight = Math.Max(maxRight, currentX + Math.Max(cellWidth, cell.Geometry.MarginBox.Width));
                    currentX += cellWidth;
                }

                if (row.RowBox != null)
                {
                    InitializeBox(row.RowBox);
                    SetContentSize(row.RowBox, Math.Max(0f, currentX - contentLeft), rowHeight);
                    LayoutBoxOps.PositionSubtree(row.RowBox, contentLeft, currentY, CreateChildState(Math.Max(0f, currentX - contentLeft), rowHeight, state));
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

        private float[] MeasureColumnWidths(List<TableRowModel> rows, LayoutState state)
        {
            int columnCount = rows.Count == 0 ? 0 : rows.Max(r => r.Cells.Count);
            var widths = new float[columnCount];

            foreach (var row in rows)
            {
                for (int i = 0; i < row.Cells.Count; i++)
                {
                    var cell = row.Cells[i];
                    float preferredWidth = MeasurePreferredWidth(cell, state);
                    widths[i] = Math.Max(widths[i], preferredWidth);
                }
            }

            return widths;
        }

        private float MeasureRowHeight(TableRowModel row, float[] columnWidths, LayoutState state)
        {
            float rowHeight = 0f;
            for (int i = 0; i < row.Cells.Count; i++)
            {
                float cellWidth = i < columnWidths.Length ? columnWidths[i] : 0f;
                LayoutCell(row.Cells[i], cellWidth, float.NaN, state);
                rowHeight = Math.Max(rowHeight, row.Cells[i].Geometry.MarginBox.Height);
            }

            return rowHeight;
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
            SyncBoxes(group.Geometry);
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
            SyncBoxes(box.Geometry);
        }

        private static void SetContentSize(LayoutBox box, float width, float height)
        {
            float left = box.Geometry.ContentBox.Left;
            float top = box.Geometry.ContentBox.Top;
            box.Geometry.ContentBox = new SKRect(left, top, left + Math.Max(0f, width), top + Math.Max(0f, height));
            SyncBoxes(box.Geometry);
        }

        private static void SyncBoxes(BoxModel geometry)
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
            string display = box?.ComputedStyle?.Display?.Trim().ToLowerInvariant();
            return display == "table-row-group" ||
                   display == "table-header-group" ||
                   display == "table-footer-group";
        }

        private static bool IsTableRow(LayoutBox box)
        {
            return string.Equals(box?.ComputedStyle?.Display, "table-row", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsTableCell(LayoutBox box)
        {
            return string.Equals(box?.ComputedStyle?.Display, "table-cell", StringComparison.OrdinalIgnoreCase);
        }

        private sealed record TableRowModel(LayoutBox RowBox, List<LayoutBox> Cells);
    }
}
