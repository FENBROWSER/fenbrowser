// SpecRef: CSS Grid Layout Module Level 1, track sizing and placement
// CapabilityId: LAYOUT-GRID-TRACKS-01
// Determinism: strict
// FallbackPolicy: spec-defined
using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// CSS Grid Layout Computer - Phase 1 Implementation
    /// Supports: grid-template-columns/rows, fr/px/auto units, gap, explicit placement
    /// </summary>
    /// <summary>
    /// CSS Grid Layout Computer - Phase 2 Implementation
    /// Supports: Auto-placement (sparse/dense), implicit tracks, robust collision handling.
    /// </summary>
    /// Supports: Auto-placement (sparse/dense), implicit tracks, robust collision handling.
    /// </summary>
    public static partial class GridLayoutComputer
    {
        /// <summary>
        /// Represents a single grid track (column or row)
        /// </summary>
        /// <summary>
        /// Represents a single grid track (column or row)
        /// </summary>
        public class GridTrack
        {
            // Resolved Layout Values
            public float BaseSize { get; set; }      // The calculated size (used size)
            public float GrowthLimit { get; set; }   // The maximum size the track can grow to
            
            // Sizing Constraints (Parsed)
            public GridTrackSize MinLimit { get; set; }
            public GridTrackSize MaxLimit { get; set; }

            public float FlexFactor => MaxLimit.IsFlex ? MaxLimit.Value : 0;
            public bool IsAuto => MaxLimit.IsAuto || MinLimit.IsAuto;
            
            // Content Sizing (for auto/min-content/max-content)
            public float MinContent { get; set; }    
            public float MaxContent { get; set; }
            public AutoRepeatMode AutoRepeatMode { get; set; } = AutoRepeatMode.None;
        }

        public struct GridTrackSize
        {
            public float Value;
            public GridUnitType Type;
            public float FitContentLimit; // For fit-content(limit)
            public bool FitContentIsPercent;

            public static GridTrackSize Auto => new GridTrackSize { Type = GridUnitType.Auto };
            public static GridTrackSize MinContent => new GridTrackSize { Type = GridUnitType.MinContent };
            public static GridTrackSize MaxContent => new GridTrackSize { Type = GridUnitType.MaxContent };
            public static GridTrackSize FromPx(float px) => new GridTrackSize { Value = px, Type = GridUnitType.Px };
            public static GridTrackSize FromFr(float fr) => new GridTrackSize { Value = fr, Type = GridUnitType.Fr };
            public static GridTrackSize FromPercent(float pct) => new GridTrackSize { Value = pct, Type = GridUnitType.Percent };
            public static GridTrackSize FromFitContent(float limit, bool isPercent = false) => new GridTrackSize { Value = limit, Type = GridUnitType.FitContent, FitContentLimit = limit, FitContentIsPercent = isPercent };
            
            public bool IsAuto => Type == GridUnitType.Auto;
            public bool IsFlex => Type == GridUnitType.Fr;
            public bool IsPx => Type == GridUnitType.Px;
            public bool IsPercent => Type == GridUnitType.Percent;
            public bool IsContent => Type == GridUnitType.MinContent || Type == GridUnitType.MaxContent || Type == GridUnitType.FitContent;
        }

        public enum GridUnitType { Px, Fr, Percent, Auto, MinContent, MaxContent, FitContent }
        public enum AutoRepeatMode { None, Fill, Fit }

        /// <summary>
        /// Represents a grid item's position
        /// </summary>
        public class GridItemPosition
        {
            public int ColumnStart { get; set; }
            public int ColumnEnd { get; set; }
            public int RowStart { get; set; }
            public int RowEnd { get; set; }
            public int ColumnSpan => ColumnEnd - ColumnStart;
            public int RowSpan => RowEnd - RowStart;

            public override string ToString() => $"Col {ColumnStart}-{ColumnEnd}, Row {RowStart}-{RowEnd}";
        }

        private class GridOccupancyMap
        {
            private readonly HashSet<(int, int)> _occupied = new HashSet<(int, int)>();

            public void Mark(int colStart, int colEnd, int rowStart, int rowEnd)
            {
                for (int c = colStart; c < colEnd; c++)
                {
                    for (int r = rowStart; r < rowEnd; r++)
                    {
                        _occupied.Add((c, r));
                    }
                }
            }

            public bool IsOccupied(int colStart, int colEnd, int rowStart, int rowEnd)
            {
                for (int c = colStart; c < colEnd; c++)
                {
                    for (int r = rowStart; r < rowEnd; r++)
                    {
                        if (_occupied.Contains((c, r))) return true;
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// Global placement calculation used by both Measure and Arrange
        /// </summary>
        private static (Dictionary<Node, GridItemPosition> Positions, int RowCount, int ColCount) ComputePlacements(
            List<Node> items,
            IReadOnlyDictionary<Node, CssComputed> styles, 
            int explicitColCount, 
            int explicitRowCount,
            string autoFlow,
            Dictionary<string, NamedArea> areas,
            IReadOnlyDictionary<string, int>? columnLineNames = null,
            IReadOnlyDictionary<string, int>? rowLineNames = null) // "row", "column", "row dense", "column dense"
        {
            var positions = new Dictionary<Node, GridItemPosition>();
            var map = new GridOccupancyMap();
            
            bool isDense = autoFlow.Contains("dense");
            bool isColumnFlow = autoFlow.Contains("column");

            // Keep the parsed position so auto items are not parsed again during
            // the placement pass.
            List<RawGridPosition>? pendingAuto = null;
            
            // State for auto-placement cursor
            int cursorRow = 1;
            int cursorCol = 1;

            int maxRow = explicitRowCount;
            int maxCol = explicitColCount;

            // Helper to expand grid bounds
            void UpdateBounds(GridItemPosition p)
            {
                maxRow = Math.Max(maxRow, p.RowEnd - 1);
                maxCol = Math.Max(maxCol, p.ColumnEnd - 1);
            }

            int RowFlowColumnLimit(int colSpan)
            {
                int limit = Math.Max(explicitColCount, maxCol);
                limit = Math.Max(limit, 1);
                return Math.Max(limit, colSpan);
            }

            int ColumnFlowRowLimit(int rowSpan)
            {
                int limit = Math.Max(explicitRowCount, maxRow);
                limit = Math.Max(limit, 1);
                return Math.Max(limit, rowSpan);
            }

            foreach (var item in items)
            {
                var style = styles.TryGetValue(item, out var s) ? s : null;
                var rawPos = DetermineGridPosition(style, item, areas, columnLineNames, rowLineNames);
                
                // If fully explicit
                if (rawPos.HasExplicitCol && rawPos.HasExplicitRow)
                {
                    var pos = FinalizePosition(rawPos);
                    positions[item] = pos;
                    map.Mark(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd);
                    UpdateBounds(pos);
                }
                else
                {
                    pendingAuto ??= new List<RawGridPosition>(items.Count - positions.Count);
                    pendingAuto.Add(rawPos);
                }
            }

            if (pendingAuto is null)
            {
                return (positions, maxRow, maxCol);
            }

            // Iterate pending items
            foreach (var rawPos in pendingAuto)
            {
                var item = rawPos.Node;

                if (isDense)
                {
                    cursorRow = 1;
                    cursorCol = 1;
                }
                
                var pos = new GridItemPosition();

                if (!isColumnFlow) // Row Flow
                {
                    if (rawPos.HasExplicitRow)
                    {
                        // Explicit Row, Auto Column
                        int r = rawPos.RowStart;
                        pos.RowStart = r;
                        pos.RowEnd = rawPos.RowEnd ?? (r + rawPos.RowSpan);

                        // Reset cursor col if we can't continue on same row naturally
                        // Actually, for "explicit row" items, we should start at 1?
                        // "Set the column position to the start line... iterate"
                        int c = 1; 
                        // If dense, start at 1. If sparse? 
                        // Spec: "place the item in the earliest... that does not overlap".
                        // Logic simplified matches dense behavior for constrained items essentially.
                        
                        while(true)
                        {
                            pos.ColumnStart = c;
                            pos.ColumnEnd = c + rawPos.ColSpan;
                             if (!map.IsOccupied(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd))
                            {
                                positions[item] = pos;
                                map.Mark(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd);
                                UpdateBounds(pos);
                                break;
                            }
                            c++;
                            if (c > 10000) break;
                        }
                    }
                    else if (rawPos.HasExplicitCol)
                    {
                        // Explicit Col, Auto Row (Automatic placement but fixed column)
                        int c = rawPos.ColStart;
                        pos.ColumnStart = c;
                        pos.ColumnEnd = rawPos.ColEnd ?? (c + rawPos.ColSpan);

                        int r = isDense ? 1 : cursorRow; // Start search from cursor row
                        while (true)
                        {
                             pos.RowStart = r;
                             pos.RowEnd = r + rawPos.RowSpan;
                             if (!map.IsOccupied(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd))
                             {
                                 positions[item] = pos;
                                 map.Mark(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd);
                                 UpdateBounds(pos);
                                 // We don't necessarily update cursor unless it affects main flow?
                                 // Actually for fixed-minor items, we mostly ignore cursor impact on pure auto?
                                 break;
                             }
                             r++;
                             if (r > 10000) break;
                        }
                    }
                    else
                    {
                        // Fully Auto (Row Flow)
                        while (true)
                        {
                            pos.RowStart = cursorRow;
                            pos.RowEnd = cursorRow + rawPos.RowSpan;
                            pos.ColumnStart = cursorCol;
                            pos.ColumnEnd = cursorCol + rawPos.ColSpan;

                            // Check collision
                            bool fits = !map.IsOccupied(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd);
                            int limit = RowFlowColumnLimit(rawPos.ColSpan);
                            bool overflow = pos.ColumnEnd - 1 > limit;

                            if (fits && !overflow)
                            {
                                positions[item] = pos;
                                map.Mark(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd);
                                UpdateBounds(pos);
                                if (!isDense) cursorCol = pos.ColumnEnd;
                                break;
                            }
                            
                            cursorCol++;
                            if (overflow)
                            {
                                cursorCol = 1;
                                cursorRow++;
                            }
                            if (cursorRow > 10000) break;
                        }
                    }
                }
                else // Column Flow
                {
                     if (rawPos.HasExplicitCol)
                    {
                        // Explicit Column, Auto Row
                        int c = rawPos.ColStart;
                        pos.ColumnStart = c;
                        pos.ColumnEnd = rawPos.ColEnd ?? (c + rawPos.ColSpan);
                        
                        int r = 1;
                        while(true)
                        {
                            pos.RowStart = r;
                            pos.RowEnd = r + rawPos.RowSpan;
                             if (!map.IsOccupied(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd))
                            {
                                positions[item] = pos;
                                map.Mark(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd);
                                UpdateBounds(pos);
                                break;
                            }
                            r++;
                            if (r > 10000) break;
                        }
                    }
                    else if (rawPos.HasExplicitRow)
                    {
                        // Explicit Row, Auto Col
                        int r = rawPos.RowStart;
                        pos.RowStart = r;
                        pos.RowEnd = rawPos.RowEnd ?? (r + rawPos.RowSpan);

                        int c = isDense ? 1 : cursorCol;
                        while(true)
                        {
                             pos.ColumnStart = c;
                             pos.ColumnEnd = c + rawPos.ColSpan;
                             if (!map.IsOccupied(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd))
                             {
                                 positions[item] = pos;
                                 map.Mark(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd);
                                 UpdateBounds(pos);
                                 break;
                             }
                             c++;
                             if (c > 10000) break;
                        }
                    }
                    else
                    {
                        // Fully Auto (Col Flow)
                        while (true)
                        {
                            pos.ColumnStart = cursorCol;
                            pos.ColumnEnd = cursorCol + rawPos.ColSpan;
                            pos.RowStart = cursorRow;
                            pos.RowEnd = cursorRow + rawPos.RowSpan;

                            bool fits = !map.IsOccupied(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd);
                            int limit = ColumnFlowRowLimit(rawPos.RowSpan);
                            bool overflow = pos.RowEnd - 1 > limit;

                            if (fits && !overflow)
                            {
                                positions[item] = pos;
                                map.Mark(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd);
                                UpdateBounds(pos);
                                if (!isDense) cursorRow = pos.RowEnd;
                                break;
                            }

                            cursorRow++;
                            if (overflow)
                            {
                                cursorRow = 1;
                                cursorCol++;
                            }
                            if (cursorCol > 10000) break;
                        }
                    }
                }
            }

            return (positions, maxRow, maxCol);
        }

        private struct RawGridPosition {
            public int RowStart; 
            public int? RowEnd; // null = auto/span
            public int RowSpan;
            public int ColStart;
            public int? ColEnd;
            public int ColSpan;
            public bool HasExplicitRow;
            public bool HasExplicitCol;
            public Node Node;
        }

        private static RawGridPosition DetermineGridPosition(
            CssComputed style,
            Node node,
            Dictionary<string, NamedArea> areas,
            IReadOnlyDictionary<string, int>? columnLineNames,
            IReadOnlyDictionary<string, int>? rowLineNames)
        {
            style ??= new CssComputed();
            var p = new RawGridPosition { Node = node, RowSpan = 1, ColSpan = 1 };

            // 1. Check for named area ("grid-area: header")
            if (!string.IsNullOrEmpty(style.GridArea))
            {
                if (areas != null && areas.TryGetValue(style.GridArea, out var area))
                {
                    p.RowStart = area.RowStart;
                    p.RowEnd = area.RowEnd;
                    p.ColStart = area.ColStart;
                    p.ColEnd = area.ColEnd;
                    p.HasExplicitRow = true;
                    p.HasExplicitCol = true;
                    // If named area found, return immediately as it overrides specific row/col props
                    return p;
                }
                
                // 2. Check for numeric shorthand ("grid-area: 1 / 1 / 2 / 2")
                var parts = style.GridArea.Split('/');
                if (parts.Length > 0)
                {
                    // r-start / c-start / r-end / c-end
                    if (int.TryParse(parts[0].Trim(), out int rs)) { p.RowStart = rs; p.HasExplicitRow = true; }
                    
                    if (parts.Length >= 2)
                        if (int.TryParse(parts[1].Trim(), out int cs)) { p.ColStart = cs; p.HasExplicitCol = true; }

                    if (parts.Length >= 3)
                        if (int.TryParse(parts[2].Trim(), out int re)) { p.RowEnd = re; }
                    
                    if (parts.Length >= 4)
                        if (int.TryParse(parts[3].Trim(), out int ce)) { p.ColEnd = ce; }

                    if (p.HasExplicitRow || p.HasExplicitCol) return p;
                }
            }

            // 3. Fallback to specific properties
            // Row
            if (TryResolveGridLine(style.GridRowStart, rowLineNames, preferStart: true, out int rsNamed)) { p.RowStart = rsNamed; p.HasExplicitRow = true; }
            else if (int.TryParse(style.GridRowStart, out int rsProp)) { p.RowStart = rsProp; p.HasExplicitRow = true; }
            else p.RowStart = 1;

            if (TryResolveGridLine(style.GridRowEnd, rowLineNames, preferStart: false, out int reNamed)) { p.RowEnd = reNamed; }
            else if (int.TryParse(style.GridRowEnd, out int reProp)) { p.RowEnd = reProp; }
            else if (TryParseSpan(style.GridRowEnd, out int rspan)) { p.RowSpan = rspan; }
            else if (p.HasExplicitRow && TryResolveGridLine(style.GridRowStart, rowLineNames, preferStart: false, out int reFromStart)) { p.RowEnd = reFromStart; }
            
            // Col
            if (TryResolveGridLine(style.GridColumnStart, columnLineNames, preferStart: true, out int csNamed)) { p.ColStart = csNamed; p.HasExplicitCol = true; }
            else if (int.TryParse(style.GridColumnStart, out int csProp)) { p.ColStart = csProp; p.HasExplicitCol = true; }
            else p.ColStart = 1;

            if (TryResolveGridLine(style.GridColumnEnd, columnLineNames, preferStart: false, out int ceNamed)) { p.ColEnd = ceNamed; }
            else if (int.TryParse(style.GridColumnEnd, out int ceProp)) { p.ColEnd = ceProp; }
            else if (TryParseSpan(style.GridColumnEnd, out int cspan)) { p.ColSpan = cspan; }
            else if (p.HasExplicitCol && TryResolveGridLine(style.GridColumnStart, columnLineNames, preferStart: false, out int ceFromStart)) { p.ColEnd = ceFromStart; }
            
            return p;
        }

        private static bool TryResolveGridLine(
            string? value,
            IReadOnlyDictionary<string, int>? lineNames,
            bool preferStart,
            out int line)
        {
            line = 0;
            if (string.IsNullOrWhiteSpace(value) || lineNames == null || lineNames.Count == 0)
            {
                return false;
            }

            var name = value.Trim();
            if (lineNames.TryGetValue(name, out line))
            {
                return true;
            }

            var suffixed = name.EndsWith("-start", StringComparison.OrdinalIgnoreCase) ||
                           name.EndsWith("-end", StringComparison.OrdinalIgnoreCase)
                ? name
                : name + (preferStart ? "-start" : "-end");
            return lineNames.TryGetValue(suffixed, out line);
        }

        private static bool TryParseSpan(string val, out int span)
        {
            span = 1;
            if (string.IsNullOrWhiteSpace(val)) return false;
            val = val.ToLowerInvariant();
            if (val.StartsWith("span"))
            {
                if (int.TryParse(val.Replace("span", "").Trim(), out int s)) span = s;
                return true;
            }
            return false;
        }

        private static GridItemPosition FinalizePosition(RawGridPosition r)
        {
            var p = new GridItemPosition();
            p.RowStart = r.RowStart;
            p.RowEnd = r.RowEnd ?? (r.RowStart + r.RowSpan);
            p.ColumnStart = r.ColStart;
            p.ColumnEnd = r.ColEnd ?? (r.ColStart + r.ColSpan);
            return p;
        }

        private static GridItemPosition ResolveItemPosition(RawGridPosition r, int autoCol, int autoRow)
        {
            var p = new GridItemPosition();
            // Start is explicit if present, else auto
            p.ColumnStart = r.HasExplicitCol ? r.ColStart : autoCol;
            p.RowStart = r.HasExplicitRow ? r.RowStart : autoRow;
            
            p.ColumnEnd = r.ColEnd ?? (p.ColumnStart + r.ColSpan);
            p.RowEnd = r.RowEnd ?? (p.RowStart + r.RowSpan);
            return p;
        }

        private static GridTrack CloneTrack(GridTrack t)
        {
            return new GridTrack
            {
                BaseSize = t.BaseSize,
                MinLimit = t.MinLimit,
                MaxLimit = t.MaxLimit,
                MinContent = t.MinContent,
                MaxContent = t.MaxContent,
                GrowthLimit = t.GrowthLimit,
                AutoRepeatMode = t.AutoRepeatMode
            };
        }

        private static void CollapseTrailingAutoFitTracks(List<GridTrack> tracks, int usedTrackCount)
        {
            if (tracks == null || tracks.Count == 0)
            {
                return;
            }

            int minRetained = Math.Max(1, usedTrackCount);
            while (tracks.Count > minRetained)
            {
                if (tracks[tracks.Count - 1].AutoRepeatMode != AutoRepeatMode.Fit)
                {
                    break;
                }

                tracks.RemoveAt(tracks.Count - 1);
            }
        }

        /// <summary>
        /// Measure a grid container and its children
        /// </summary>
        public static LayoutMetrics Measure(
            Element container,
            SKSize availableSize,
            IReadOnlyDictionary<Node, CssComputed> styles,
            int depth,
            Func<Node, SKSize, int, LayoutMetrics> measureNode,
            IEnumerable<Node> childrenSource = null,
            GridSubgridContext subgridContext = null)
        {
            if (container == null || container.ChildNodes == null)
                return new LayoutMetrics();

            var style = styles.TryGetValue(container, out var s) ? s : null;
            if (style == null) return new LayoutMetrics();

            bool columnsSubgrid = subgridContext?.SubgridColumns == true && subgridContext.HasColumnLines;
            bool rowsSubgrid = subgridContext?.SubgridRows == true && subgridContext.HasRowLines;

            // Parse grid template
            var columnTracks = columnsSubgrid
                ? BuildSubgridTracks(subgridContext.ColumnLines)
                : ParseTracks(style.GridTemplateColumns, availableSize.Width, (float)(style.ColumnGap ?? style.Gap ?? 0));
            var rowTracks = rowsSubgrid
                ? BuildSubgridTracks(subgridContext.RowLines)
                : ParseTracks(style.GridTemplateRows, availableSize.Height, (float)(style.RowGap ?? style.Gap ?? 0));
            var columnLineNames = columnsSubgrid
                ? MergeSubgridLineNames(ParseTrackLineNames(style.GridTemplateColumns, availableSize.Width, (float)(style.ColumnGap ?? style.Gap ?? 0)), subgridContext.InheritedLineNames)
                : ParseTrackLineNames(style.GridTemplateColumns, availableSize.Width, (float)(style.ColumnGap ?? style.Gap ?? 0));
            var rowLineNames = rowsSubgrid
                ? MergeSubgridLineNames(ParseTrackLineNames(style.GridTemplateRows, availableSize.Height, (float)(style.RowGap ?? style.Gap ?? 0)), subgridContext.InheritedLineNames)
                : ParseTrackLineNames(style.GridTemplateRows, availableSize.Height, (float)(style.RowGap ?? style.Gap ?? 0));
            
            // Parse areas (Phase 3)
            var areas = ParseGridTemplateAreas(style.GridTemplateAreas);

            int columnTracksOriginalCount = columnTracks.Count;
            int rowTracksOriginalCount = rowTracks.Count;

            // Get gap values
            float columnGap = (float)(style.ColumnGap ?? style.Gap ?? 0);
            float rowGap = (float)(style.RowGap ?? style.Gap ?? 0);

            // Get grid items. CSS Grid generates anonymous grid items for
            // non-whitespace text runs, so keep text nodes that survived box-tree
            // construction; otherwise label text in form-control grids is dropped.
            var source = childrenSource ?? container.ChildNodes;
            var items = source
                .Where(IsGridItemNode)
                .ToList();

            // Parse auto tracks
            var autoColTracks = ParseTracks(style.GridAutoColumns, availableSize.Width, (float)(style.ColumnGap ?? style.Gap ?? 0));
            var autoRowTracks = ParseTracks(style.GridAutoRows, availableSize.Height, (float)(style.RowGap ?? style.Gap ?? 0));
            if (autoColTracks.Count == 0) autoColTracks.Add(new GridTrack { MinLimit = GridTrackSize.Auto, MaxLimit = GridTrackSize.Auto });
            if (autoRowTracks.Count == 0) autoRowTracks.Add(new GridTrack { MinLimit = GridTrackSize.Auto, MaxLimit = GridTrackSize.Auto });
            
            string autoFlow = style.GridAutoFlow?.ToLowerInvariant() ?? "row";
            
            // Compute Layout
            var placement = ComputePlacements(items, styles, columnTracks.Count, rowTracks.Count, autoFlow, areas, columnLineNames, rowLineNames);
            var positions = placement.Positions;
            int usedColCount = positions.Count > 0 ? positions.Values.Max(p => p.ColumnEnd - 1) : 0;
            int usedRowCount = positions.Count > 0 ? positions.Values.Max(p => p.RowEnd - 1) : 0;

            // auto-fit collapses unused trailing explicit repeat tracks.
            CollapseTrailingAutoFitTracks(columnTracks, usedColCount);
            CollapseTrailingAutoFitTracks(rowTracks, usedRowCount);

            int requiredColCount = Math.Max(usedColCount, columnTracks.Count);
            int requiredRowCount = Math.Max(usedRowCount, rowTracks.Count);

            // Fill implicit tracks
            while (columnTracks.Count < requiredColCount) {
                // Cycle through auto patterns
                var pattern = autoColTracks[(columnTracks.Count - columnTracksOriginalCount) % autoColTracks.Count];
                columnTracks.Add(CloneTrack(pattern));
            }
            while (rowTracks.Count < requiredRowCount) {
                var pattern = autoRowTracks[(rowTracks.Count - rowTracksOriginalCount) % autoRowTracks.Count];
                rowTracks.Add(CloneTrack(pattern));
            }

            // Resolve intrinsic sizes for columns (Auto, MinContent, MaxContent, FitContent)
            // This sets BaseSize based on content. Subgrid tracks are already
            // resolved by the parent grid and must not be re-measured.
            if (!columnsSubgrid)
            {
                MeasureTracksIntrinsic(columnTracks, items, positions, styles, true, depth, measureNode);
            }

            if (!rowsSubgrid)
            {
                MeasureTracksIntrinsic(rowTracks, items, positions, styles, false, depth, measureNode);
            }
            
            // Resolve flexible tracks (fr units)
            if (!columnsSubgrid)
            {
                ResolveFlexibleTracks(columnTracks, availableSize.Width, columnGap);
            }
            
            // Measure rows
            if (!rowsSubgrid)
            {
                MeasureAutoRowHeights(rowTracks, columnTracks, items, positions, styles, columnGap, depth, measureNode);
                if (HasDefiniteBlockSize(style, availableSize.Height))
                {
                    ResolveFlexibleTracks(rowTracks, availableSize.Height, rowGap);
                }
            }

            // Calculate total dimensions. Subgrid tracks are fixed and their
            // line deltas already include the parent grid's gaps, so no gap
            // term is added for subgrid axes.
            float effectiveColumnGapTotal = columnsSubgrid ? 0f : columnGap;
            float effectiveRowGapTotal = rowsSubgrid ? 0f : rowGap;
            float totalWidth = columnTracks.Sum(t => t.BaseSize) + Math.Max(0, columnTracks.Count - 1) * effectiveColumnGapTotal;
            float totalHeight = rowTracks.Sum(t => t.BaseSize) + Math.Max(0, rowTracks.Count - 1) * effectiveRowGapTotal;

            // Calculate Intrinsic Dimensions
            float minIntrinsicWidth = columnTracks.Sum(t => t.BaseSize) + Math.Max(0, columnTracks.Count - 1) * effectiveColumnGapTotal;
            float maxIntrinsicWidth = columnTracks.Sum(t => float.IsInfinity(t.GrowthLimit) ? t.BaseSize : t.GrowthLimit) + Math.Max(0, columnTracks.Count - 1) * effectiveColumnGapTotal;

            System.Diagnostics.Debug.WriteLine($"[CSS-GRID] Measured: {columnTracks.Count}x{rowTracks.Count}, size={totalWidth}x{totalHeight}");

            return new LayoutMetrics
            {
                ContentHeight = totalHeight,
                ActualHeight = totalHeight,
                MaxChildWidth = totalWidth,
                MinContentWidth = minIntrinsicWidth,
                MaxContentWidth = maxIntrinsicWidth
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
            Action<Node, SKRect, int> arrangeChild,
            Func<Node, SKSize, int, LayoutMetrics> measureNode,
            IEnumerable<Node> childrenSource = null,
            GridSubgridContext subgridContext = null,
            Action<Node, SKRect, int, GridSubgridContext> arrangeChildWithContext = null)
        {
            if (container == null || container.ChildNodes == null) return;

            var style = styles.TryGetValue(container, out var s) ? s : null;
            if (style == null) return;

            bool columnsSubgrid = subgridContext?.SubgridColumns == true && subgridContext.HasColumnLines;
            bool rowsSubgrid = subgridContext?.SubgridRows == true && subgridContext.HasRowLines;

            // Parse areas (Phase 3)
            var areas = ParseGridTemplateAreas(style.GridTemplateAreas);

            float columnGap = (float)(style.ColumnGap ?? style.Gap ?? 0);
            float rowGap = (float)(style.RowGap ?? style.Gap ?? 0);

            // Parse grid template
            var columnTracks = columnsSubgrid
                ? BuildSubgridTracks(subgridContext.ColumnLines)
                : ParseTracks(style.GridTemplateColumns, bounds.Width, columnGap);
            var rowTracks = rowsSubgrid
                ? BuildSubgridTracks(subgridContext.RowLines)
                : ParseTracks(style.GridTemplateRows, bounds.Height, rowGap);
            var columnLineNames = columnsSubgrid
                ? MergeSubgridLineNames(ParseTrackLineNames(style.GridTemplateColumns, bounds.Width, columnGap), subgridContext.InheritedLineNames)
                : ParseTrackLineNames(style.GridTemplateColumns, bounds.Width, columnGap);
            var rowLineNames = rowsSubgrid
                ? MergeSubgridLineNames(ParseTrackLineNames(style.GridTemplateRows, bounds.Height, rowGap), subgridContext.InheritedLineNames)
                : ParseTrackLineNames(style.GridTemplateRows, bounds.Height, rowGap);

            int columnTracksOriginalCount = columnTracks.Count;
            int rowTracksOriginalCount = rowTracks.Count;



            var source = childrenSource ?? container.ChildNodes;
            var items = source
                .Where(IsGridItemNode)
                .ToList();

            // Parse auto tracks
            var autoColTracks = ParseTracks(style.GridAutoColumns, bounds.Width, columnGap);
            var autoRowTracks = ParseTracks(style.GridAutoRows, bounds.Height, rowGap);
            if (autoColTracks.Count == 0) autoColTracks.Add(new GridTrack { MinLimit = GridTrackSize.Auto, MaxLimit = GridTrackSize.Auto });
            if (autoRowTracks.Count == 0) autoRowTracks.Add(new GridTrack { MinLimit = GridTrackSize.Auto, MaxLimit = GridTrackSize.Auto });

            string autoFlow = style.GridAutoFlow?.ToLowerInvariant() ?? "row";
            
            // Compute Layout
            var placement = ComputePlacements(items, styles, columnTracks.Count, rowTracks.Count, autoFlow, areas, columnLineNames, rowLineNames);
            var positions = placement.Positions;
            int usedColCount = positions.Count > 0 ? positions.Values.Max(p => p.ColumnEnd - 1) : 0;
            int usedRowCount = positions.Count > 0 ? positions.Values.Max(p => p.RowEnd - 1) : 0;

            // auto-fit collapses unused trailing explicit repeat tracks.
            CollapseTrailingAutoFitTracks(columnTracks, usedColCount);
            CollapseTrailingAutoFitTracks(rowTracks, usedRowCount);

            int requiredColCount = Math.Max(usedColCount, columnTracks.Count);
            int requiredRowCount = Math.Max(usedRowCount, rowTracks.Count);

            // Fill implicit tracks
            while (columnTracks.Count < requiredColCount) {
                int index = (columnTracks.Count - columnTracksOriginalCount) % autoColTracks.Count;
                if (index < 0) index = 0;
                var pattern = autoColTracks[index];
                columnTracks.Add(CloneTrack(pattern));
            }
            while (rowTracks.Count < requiredRowCount) {
                int index = (rowTracks.Count - rowTracksOriginalCount) % autoRowTracks.Count;
                if(index < 0) index = 0;
                var pattern = autoRowTracks[index];
                rowTracks.Add(CloneTrack(pattern));
            }

            // Resolve intrinsic sizes for columns (Auto, MinContent, MaxContent, FitContent)
            // This sets BaseSize based on content. Subgrid axes are fixed by the
            // parent grid's resolved lines and are never re-measured here.
            if (!columnsSubgrid)
            {
                MeasureTracksIntrinsic(columnTracks, items, positions, styles, true, depth, measureNode);
            }

            if (!rowsSubgrid)
            {
                MeasureTracksIntrinsic(rowTracks, items, positions, styles, false, depth, measureNode);
            }

            // Resolve column flex tracks BEFORE measuring row heights so items are
            // measured at their correct column widths (matching the Measure pass).
            if (!columnsSubgrid)
            {
                ResolveFlexibleTracks(columnTracks, bounds.Width, columnGap);
            }

            // Ensure arrange pass uses the same content-derived auto-row sizing
            // as measure pass before flex/stretch resolution.
            if (!rowsSubgrid)
            {
                MeasureAutoRowHeights(rowTracks, columnTracks, items, positions, styles, columnGap, depth, measureNode);
            }

            // Resolve row Track Sizes (only when block size is definite)
            if (!rowsSubgrid && HasDefiniteBlockSize(style, bounds.Height))
            {
                ResolveFlexibleTracks(rowTracks, bounds.Height, rowGap);
            }

            // Compute effective gaps for justify/align content
            float effectiveColumnGap = columnsSubgrid ? 0f : columnGap;
            float effectiveRowGap = rowsSubgrid ? 0f : rowGap;
            float contentXOffset = 0;
            float contentYOffset = 0;

            // Base totals with original gaps
            float baseGridWidth = columnTracks.Sum(t => t.BaseSize) + Math.Max(0, columnTracks.Count - 1) * effectiveColumnGap;
            float baseGridHeight = rowTracks.Sum(t => t.BaseSize) + Math.Max(0, rowTracks.Count - 1) * effectiveRowGap;

            // JustifyContent (Horizontal Track Alignment). Not applicable to a
            // subgrid axis: its lines are inherited from the parent grid.
            if (!columnsSubgrid && !string.IsNullOrEmpty(style.JustifyContent) && columnTracks.Count > 0)
            {
                string jc = style.JustifyContent.ToLowerInvariant();
                float freeW = bounds.Width - baseGridWidth;
                if (freeW > 0)
                {
                    if (jc == "center") contentXOffset = freeW / 2;
                    else if (jc == "end" || jc == "flex-end") contentXOffset = freeW;
                    else if (jc == "space-between" && columnTracks.Count > 1)
                    {
                        effectiveColumnGap = columnGap + freeW / (columnTracks.Count - 1);
                    }
                    else if (jc == "space-around" && columnTracks.Count > 0)
                    {
                        float extraPer = freeW / columnTracks.Count;
                        effectiveColumnGap = columnGap + extraPer;
                        contentXOffset = extraPer / 2;
                    }
                }
            }

            // AlignContent (Vertical Track Alignment). Not applicable to a
            // subgrid axis: its lines are inherited from the parent grid.
            if (!rowsSubgrid && !string.IsNullOrEmpty(style.AlignContent) && rowTracks.Count > 0)
            {
                string ac = style.AlignContent.ToLowerInvariant();
                float freeH = bounds.Height - baseGridHeight;
                if (freeH > 0)
                {
                    if (ac == "center") contentYOffset = freeH / 2;
                    else if (ac == "end" || ac == "flex-end") contentYOffset = freeH;
                    else if (ac == "space-between" && rowTracks.Count > 1)
                    {
                        effectiveRowGap = rowGap + freeH / (rowTracks.Count - 1);
                    }
                    else if (ac == "space-around" && rowTracks.Count > 0)
                    {
                        float extraPer = freeH / rowTracks.Count;
                        effectiveRowGap = rowGap + extraPer;
                        contentYOffset = extraPer / 2;
                    }
                }
            }

            // Compute Start Positions using effective gaps. Subgrid axes reuse
            // the parent grid's resolved line positions verbatim.
            float[] colStarts;
            if (columnsSubgrid)
            {
                colStarts = subgridContext.ColumnLines;
            }
            else
            {
                colStarts = new float[columnTracks.Count + 1];
                float cx = contentXOffset;
                for (int i = 0; i < columnTracks.Count; i++)
                {
                    colStarts[i] = cx;
                    cx += columnTracks[i].BaseSize + effectiveColumnGap;
                }
                colStarts[columnTracks.Count] = cx;
            }

            float[] rowStarts;
            if (rowsSubgrid)
            {
                rowStarts = subgridContext.RowLines;
            }
            else
            {
                rowStarts = new float[rowTracks.Count + 1];
                float cy = contentYOffset;
                for (int i = 0; i < rowTracks.Count; i++)
                {
                    rowStarts[i] = cy;
                    cy += rowTracks[i].BaseSize + effectiveRowGap;
                }
                rowStarts[rowTracks.Count] = cy;
            }

            // Arrange Item
            var baselinePlans = new List<GridBaselineAlignmentPlan>();
            var rowBaselineTargets = new Dictionary<int, float>();
            foreach (var kv in positions)
            {
                var item = kv.Key;
                var pos = kv.Value;

                int c1 = pos.ColumnStart - 1;
                int c2 = pos.ColumnEnd - 1;
                int r1 = pos.RowStart - 1;
                int r2 = pos.RowEnd - 1;

                if (c1 < 0) c1 = 0;
                if (r1 < 0) r1 = 0;
                
                // Track start/end coords
                float trackX = colStarts[c1];
                float trackY = rowStarts[r1];
                
                // Track width/height (spanning logic)
                float trackW = (c2 < colStarts.Length ? colStarts[c2] : colStarts.Last()) - trackX;
                float trackH = (r2 < rowStarts.Length ? rowStarts[r2] : rowStarts.Last()) - trackY;

                // Adjust for gaps consumed in span
                // colStarts[c2] includes Gap[c2-1].
                // If c2 > c1, we spanned (c2-c1) tracks and (c2-c1-1) internal gaps.
                // The distance (Starts[c2] - Starts[c1]) includes Gap[c2-1].
                // If we end exactly at c2, we occupy cols c1..c2-1.
                // Starts[c2] is (Base[c2-1] + Gap[c2-1]) + Starts[c2-1].
                // So the delta includes the gap AFTER the last track. Use subtract logic to get "cell content width" (excluding final gap).
                // Subgrid axes inherit the parent's lines wholesale, so their
                // line deltas already include every gap; no subtraction applies.
                if (!columnsSubgrid && c2 > c1) trackW -= columnGap;
                if (!rowsSubgrid && r2 > r1) trackH -= rowGap;
                
                if (trackW < 0) trackW = 0;
                if (trackH < 0) trackH = 0;

                // --- Phase 3: Item Alignment (JustifyItems/AlignItems/Self) ---
                var itemStyle = styles.TryGetValue(item, out var resolvedItemStyle)
                    ? resolvedItemStyle
                    : new CssComputed();
                string justify = itemStyle.JustifySelf ?? style.JustifyItems ?? "stretch";
                string align = itemStyle.AlignSelf ?? style.AlignItems ?? "stretch";

                float itemW = trackW;
                float itemH = trackH;
                
                // If not stretching, we need intrinsic size.
                // Explicit sizes win. Otherwise use measured intrinsic size so justify/align
                // can position content-sized items instead of leaving them track-stretched.
                bool hasExplicitW = itemStyle.Width.HasValue;
                bool hasExplicitH = itemStyle.Height.HasValue;
                
                if (justify != "stretch" && hasExplicitW) itemW = (float)itemStyle.Width.Value;
                if (align != "stretch" && hasExplicitH) itemH = (float)itemStyle.Height.Value;

                bool needsIntrinsicW = justify != "stretch" && !hasExplicitW;
                bool needsIntrinsicH = align != "stretch" && !hasExplicitH;
                if (needsIntrinsicW || needsIntrinsicH)
                {
                    var intrinsic = measureNode(item, new SKSize(float.PositiveInfinity, float.PositiveInfinity), depth + 1);
                    float intrinsicWidth = intrinsic.MaxContentWidth > 0
                        ? intrinsic.MaxContentWidth
                        : intrinsic.MaxChildWidth;
                    if (needsIntrinsicW && intrinsicWidth > 0)
                    {
                        itemW = Math.Min(trackW, intrinsicWidth);
                    }

                    if (needsIntrinsicH && intrinsic.ContentHeight > 0)
                    {
                        itemH = Math.Min(trackH, intrinsic.ContentHeight);
                    }
                }

                // Track starts already include content alignment offsets.
                // Adding them again here double-shifts items for align/justify-content.
                float cellX = trackX;
                float cellY = trackY;
                
                if (justify == "center") cellX += (trackW - itemW) / 2;
                else if (justify == "end" || justify == "right" || justify == "flex-end") cellX += (trackW - itemW);
                
                bool alignBaseline = IsBaselineAlignment(align);
                if (align == "center") cellY += (trackH - itemH) / 2;
                else if (align == "end" || align == "bottom" || align == "flex-end") cellY += (trackH - itemH);

                var itemRect = new SKRect(cellX, cellY, cellX + itemW, cellY + itemH);

                if (arrangeChildWithContext != null)
                {
                    var itemSubgrid = TryBuildSubgridContext(item, itemStyle, colStarts, rowStarts, c1, c2, r1, r2, itemRect, columnLineNames, rowLineNames);
                    if (itemSubgrid != null)
                    {
                        arrangeChildWithContext(item, itemRect, depth + 1, itemSubgrid);
                    }
                    else
                    {
                        arrangeChild(item, itemRect, depth + 1);
                    }
                }
                else
                {
                    arrangeChild(item, itemRect, depth + 1);
                }

                if (alignBaseline)
                {
                    float baselineOffset = ResolveGridItemBaselineOffset(item, boxes, itemRect);
                    if (baselineOffset > 0f)
                    {
                        float baselineAbsoluteY = itemRect.Top + baselineOffset;
                        if (!rowBaselineTargets.TryGetValue(r1, out float currentBaseline) || baselineAbsoluteY > currentBaseline)
                        {
                            rowBaselineTargets[r1] = baselineAbsoluteY;
                        }

                        baselinePlans.Add(new GridBaselineAlignmentPlan(item, r1, trackY, trackH, itemRect, baselineOffset));
                    }
                }
            }

            // Align baseline-participating items after all per-row baselines are known.
            foreach (var plan in baselinePlans)
            {
                if (!rowBaselineTargets.TryGetValue(plan.RowIndex, out float targetBaseline))
                {
                    continue;
                }

                float targetTop = targetBaseline - plan.BaselineOffset;
                float maxTopWithinTrack = plan.TrackTop + Math.Max(0f, plan.TrackHeight - plan.ItemRect.Height);
                if (targetTop > maxTopWithinTrack)
                {
                    targetTop = maxTopWithinTrack;
                }

                if (targetTop < plan.TrackTop)
                {
                    targetTop = plan.TrackTop;
                }

                if (Math.Abs(targetTop - plan.ItemRect.Top) <= 0.1f)
                {
                    continue;
                }

                var adjustedRect = new SKRect(
                    plan.ItemRect.Left,
                    targetTop,
                    plan.ItemRect.Right,
                    targetTop + plan.ItemRect.Height);
                arrangeChild(plan.Item, adjustedRect, depth + 1);
            }
        }

        // --- Helpers identical to Phase 1 but included for completeness ---

        private readonly record struct GridBaselineAlignmentPlan(
            Node Item,
            int RowIndex,
            float TrackTop,
            float TrackHeight,
            SKRect ItemRect,
            float BaselineOffset);

        private static bool IsBaselineAlignment(string align)
        {
            if (string.IsNullOrWhiteSpace(align))
            {
                return false;
            }

            var normalized = align.Trim().ToLowerInvariant();
            return normalized == "baseline" ||
                   normalized == "first baseline" ||
                   normalized == "first-baseline" ||
                   normalized == "last baseline" ||
                   normalized == "last-baseline";
        }

        private static float ResolveGridItemBaselineOffset(
            Node item,
            IDictionary<Node, BoxModel> boxes,
            SKRect itemRect)
        {
            if (boxes != null && boxes.TryGetValue(item, out var geometry))
            {
                if (TryResolveBaselineOffsetFromMarginTop(geometry, out float baseline))
                {
                    return baseline;
                }
            }

            float fallback = itemRect.Height;
            return float.IsFinite(fallback) && fallback > 0f ? fallback : 0f;
        }

        private static bool TryResolveBaselineOffsetFromMarginTop(BoxModel geometry, out float baselineOffset)
        {
            baselineOffset = 0f;
            if (geometry == null)
            {
                return false;
            }

            float marginToContent = geometry.ContentBox.Top - geometry.MarginBox.Top;
            if (!float.IsFinite(marginToContent) || marginToContent < 0f)
            {
                marginToContent = 0f;
            }

            if (geometry.Lines != null && geometry.Lines.Count > 0)
            {
                var line = geometry.Lines[0];
                float lineBaseline = marginToContent + line.Origin.Y + line.Baseline;
                if (float.IsFinite(lineBaseline) && lineBaseline > 0f)
                {
                    baselineOffset = lineBaseline;
                    return true;
                }
            }

            float baseline = geometry.Baseline;
            if (!float.IsFinite(baseline) || baseline <= 0f)
            {
                baseline = geometry.Ascent;
            }

            if (!float.IsFinite(baseline) || baseline <= 0f)
            {
                return false;
            }

            baselineOffset = marginToContent + baseline;
            if (!float.IsFinite(baselineOffset) || baselineOffset <= 0f)
            {
                return false;
            }

            float marginHeight = geometry.MarginBox.Height;
            if (float.IsFinite(marginHeight) && marginHeight > 0f)
            {
                baselineOffset = Math.Min(marginHeight, baselineOffset);
            }

            return baselineOffset > 0f;
        }

        private static bool IsGridItemNode(Node node)
        {
            if (node == null)
            {
                return false;
            }

            if (node is Element)
            {
                return true;
            }

            if (node is Text text)
            {
                return !Contexts.TextWhitespaceClassifier.IsCollapsibleWhitespaceOnly(text.Data ?? string.Empty);
            }

            return false;
        }

        /// <summary>
        /// Builds the subgrid context for a grid item whose template declares
        /// <c>subgrid</c> on one or both axes. The child grid inherits the
        /// parent's resolved line positions for the tracks the item spans,
        /// expressed relative to the item's content-box origin so the child's
        /// own <c>colStarts/rowStarts</c> match the parent's lines exactly.
        /// </summary>
        private static GridSubgridContext TryBuildSubgridContext(
            Node item,
            CssComputed itemStyle,
            float[] colStarts,
            float[] rowStarts,
            int c1,
            int c2,
            int r1,
            int r2,
            SKRect itemRect,
            Dictionary<string, int> parentColumnLineNames,
            Dictionary<string, int> parentRowLineNames)
        {
            bool columnsSubgrid = IsSubgridTemplate(itemStyle?.GridTemplateColumns);
            bool rowsSubgrid = IsSubgridTemplate(itemStyle?.GridTemplateRows);
            if (!columnsSubgrid && !rowsSubgrid)
            {
                return null;
            }

            // Subgrid only applies to a grid container; a non-grid item with a
            // subgrid template keeps its regular block layout.
            string display = itemStyle?.Display;
            bool isGridContainer = !string.IsNullOrWhiteSpace(display) &&
                                   display.Contains("grid", StringComparison.OrdinalIgnoreCase);
            if (!isGridContainer)
            {
                return null;
            }

            var context = new GridSubgridContext
            {
                SubgridColumns = columnsSubgrid,
                SubgridRows = rowsSubgrid
            };

            // The item's content box origin sits inside the cell at the item's
            // border+padding+margin chrome, so parent lines are shifted by the
            // chrome to land on the child's content-box coordinates.
            var margin = itemStyle?.Margin ?? default;
            var border = itemStyle?.BorderThickness ?? default;
            var padding = itemStyle?.Padding ?? default;
            float chromeLeft = (float)(margin.Left + border.Left + padding.Left);
            float chromeTop = (float)(margin.Top + border.Top + padding.Top);

            if (columnsSubgrid)
            {
                int span = Math.Max(1, c2 - c1);
                var lines = new float[span + 1];
                for (int i = 0; i <= span; i++)
                {
                    int lineIndex = Math.Min(c1 + i, colStarts.Length - 1);
                    lines[i] = colStarts[lineIndex] - itemRect.Left - chromeLeft;
                }
                context.ColumnLines = lines;

                // Inherit parent line names that fall inside the spanned range,
                // remapped to child-local 1-based line numbers.
                var inherited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (parentColumnLineNames != null)
                {
                    foreach (var name in parentColumnLineNames)
                    {
                        if (name.Value > c1 && name.Value <= c1 + span + 1)
                        {
                            inherited[name.Key] = name.Value - c1;
                        }
                    }
                }
                context.InheritedLineNames = inherited;
            }

            if (rowsSubgrid)
            {
                int span = Math.Max(1, r2 - r1);
                var lines = new float[span + 1];
                for (int i = 0; i <= span; i++)
                {
                    int lineIndex = Math.Min(r1 + i, rowStarts.Length - 1);
                    lines[i] = rowStarts[lineIndex] - itemRect.Top - chromeTop;
                }
                context.RowLines = lines;

                var inherited = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (parentRowLineNames != null)
                {
                    foreach (var name in parentRowLineNames)
                    {
                        if (name.Value > r1 && name.Value <= r1 + span + 1)
                        {
                            inherited[name.Key] = name.Value - r1;
                        }
                    }
                }
                context.InheritedLineNames = context.InheritedLineNames ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var name in inherited)
                {
                    if (!context.InheritedLineNames.ContainsKey(name.Key))
                    {
                        context.InheritedLineNames[name.Key] = name.Value;
                    }
                }
            }

            return context;
        }



        private static void ResolveFlexibleTracks(List<GridTrack> tracks, float availableSpace, float gap)
        {
            if (tracks.Count == 0) return;
            if (float.IsNaN(availableSpace) || float.IsInfinity(availableSpace) || availableSpace <= 0)
            {
                return;
            }

            foreach (var track in tracks)
            {
                if (track.MaxLimit.IsPx && !track.MaxLimit.IsFlex && track.BaseSize < track.MaxLimit.Value)
                {
                    track.BaseSize = Math.Max(track.BaseSize, track.MaxLimit.Value);
                }
            }

            float usedSpace = tracks
                .Where(static track => !track.MaxLimit.IsFlex)
                .Sum(static track => Math.Max(0f, track.BaseSize));

            float gapSpace = Math.Max(0, tracks.Count - 1) * gap;
            float freeSpace = Math.Max(0, availableSpace - usedSpace - gapSpace);
            var flexTracks = tracks.Where(static track => track.MaxLimit.IsFlex).ToList();
            float totalFlex = flexTracks.Sum(static track => track.FlexFactor);

            if (totalFlex > 0)
            {
                float totalFloor = flexTracks.Sum(static track => Math.Max(0f, track.BaseSize));
                if (totalFloor >= freeSpace)
                {
                    foreach (var track in flexTracks)
                    {
                        track.BaseSize = Math.Max(0f, track.BaseSize);
                    }
                }
                else
                {
                    var unfrozen = new List<GridTrack>(flexTracks);
                    float remainingSpace = freeSpace;
                    while (unfrozen.Count > 0)
                    {
                        float remainingFlex = unfrozen.Sum(static track => track.FlexFactor);
                        if (remainingFlex <= 0f)
                        {
                            break;
                        }

                        float frUnit = remainingSpace / remainingFlex;
                        var belowFloor = unfrozen
                            .Where(track => track.BaseSize > track.FlexFactor * frUnit)
                            .ToList();
                        if (belowFloor.Count == 0)
                        {
                            foreach (var track in unfrozen)
                            {
                                track.BaseSize = track.FlexFactor * frUnit;
                            }
                            break;
                        }

                        foreach (var track in belowFloor)
                        {
                            remainingSpace = Math.Max(0f, remainingSpace - track.BaseSize);
                            unfrozen.Remove(track);
                        }
                    }
                }
            }
            
            // Distribute to Auto / MinMax(fixed, fixed) if space remains? 
            // For now, assume minmax(fixed, fixed) stays at min unless we implement specific expansion.
            // Auto tracks handling:
            var autoTracks = tracks.Where(t => t.IsAuto && !t.MaxLimit.IsFlex).ToList();
            if (autoTracks.Count > 0 && totalFlex == 0)
            {
                // If no flex tracks, distribute remaining space to auto tracks?
                // Or if they have content, they use content size.
                // If empty/auto, we might expand them.
                float remainingSpace = Math.Max(0, freeSpace); // But freeSpace was calc based on Min. 
                // Any space left is truly free.
                float share = remainingSpace / autoTracks.Count;
                foreach(var t in autoTracks) 
                {
                    // If t has MaxLimit (e.g. minmax(10, 50)), clamp it.
                    float newSize = t.BaseSize + share;
                    if (t.MaxLimit.IsPx) newSize = Math.Min(newSize, t.MaxLimit.Value);
                    else if (t.MaxLimit.Type == GridUnitType.FitContent) newSize = Math.Min(newSize, t.MaxLimit.FitContentLimit);
                    t.BaseSize = Math.Max(t.BaseSize, newSize);
                }
            }
            
            foreach (var t in tracks) if (t.BaseSize < 0) t.BaseSize = 0;
        }

        private static void MeasureAutoRowHeights(
            List<GridTrack> rowTracks,
            List<GridTrack> columnTracks,
            List<Node> items,
            Dictionary<Node, GridItemPosition> positions,
            IReadOnlyDictionary<Node, CssComputed> styles,
            float columnGap,
            int depth,
            Func<Node, SKSize, int, LayoutMetrics> measureNode)
        {
            var rowMaxHeights = new Dictionary<int, float>();
            foreach (var item in items)
            {
                if (!positions.ContainsKey(item)) continue;
                var pos = positions[item];
                var itemStyle = styles.TryGetValue(item, out var ist) ? ist : null;

                // Calculate width to measure against
                float itemWidth = 0;
                for (int c = pos.ColumnStart - 1; c < pos.ColumnEnd - 1 && c < columnTracks.Count; c++)
                {
                    itemWidth += columnTracks[c].BaseSize;
                    if (c < pos.ColumnEnd - 2) itemWidth += columnGap;
                }

                var metrics = measureNode(item, new SKSize(itemWidth, float.PositiveInfinity), depth + 1);
                float itemHeight = metrics.ContentHeight;

                int spanRows = Math.Max(1, pos.RowEnd - pos.RowStart);
                float heightPerRow = itemHeight / spanRows;

                for (int r = pos.RowStart; r < pos.RowEnd; r++)
                {
                    // Map is 0-indexed internally for tracks, but positions are 1-based?
                    // pos.RowStart=1 => rowTracks index 0.
                    // We need to map to track index.
                    int trackIndex = r - 1;
                    if (trackIndex >= 0)
                    {
                        if (!rowMaxHeights.ContainsKey(trackIndex)) rowMaxHeights[trackIndex] = 0;
                        rowMaxHeights[trackIndex] = Math.Max(rowMaxHeights[trackIndex], heightPerRow);
                    }
                }
            }

            for (int i = 0; i < rowTracks.Count; i++)
            {
                if (rowTracks[i].IsAuto && rowMaxHeights.TryGetValue(i, out float h))
                {
                    rowTracks[i].BaseSize = Math.Max(rowTracks[i].BaseSize, h);
                }
                if (rowTracks[i].IsAuto && rowTracks[i].BaseSize == 0) rowTracks[i].BaseSize = 40;
            }
        }

        private static void MeasureTracksIntrinsic(
            List<GridTrack> tracks,
            List<Node> items,
            Dictionary<Node, GridItemPosition> positions,
            IReadOnlyDictionary<Node, CssComputed> styles,
            bool isColumn,
            int depth,
            Func<Node, SKSize, int, LayoutMetrics> measureNode)
        {
            // Simple heuristic for intrinsic sizing:
            // Iterate all tracks. If track is content-sized (Auto, MinContent, MaxContent, FitContent),
            // find items that fall into this track (exclusively or primarily).
            // Measure those items. Update Track.BaseSize.
            
            // Only process if there are any content tracks
            bool hasContentTracks = false;
            foreach (var t in tracks)
            {
                bool isMinContent = t.MinLimit.Type == GridUnitType.MinContent || t.MinLimit.Type == GridUnitType.MaxContent || t.MinLimit.Type == GridUnitType.FitContent;
                bool isMaxContent = t.MaxLimit.Type == GridUnitType.MinContent || t.MaxLimit.Type == GridUnitType.MaxContent || t.MaxLimit.Type == GridUnitType.FitContent;
                
                if (t.IsAuto || isMinContent || isMaxContent || t.MaxLimit.IsFlex)
                {
                    hasContentTracks = true;
                    break;
                }
            }
            if (!hasContentTracks) return;

            // First Pass: 1-track spanning items
            foreach (var item in items)
            {
                if (!positions.TryGetValue(item, out var pos)) continue;
                int spanStart = isColumn ? pos.ColumnStart : pos.RowStart;
                int spanCount = isColumn ? pos.ColumnSpan : pos.RowSpan;
                if (spanCount != 1) continue;
                
                int trackIndex = spanStart - 1;
                if (trackIndex < 0 || trackIndex >= tracks.Count) continue;
                var track = tracks[trackIndex];
                if (!styles.TryGetValue(item, out var style)) continue;

                // Measure item accurately
                var metrics = measureNode(item, new SKSize(float.PositiveInfinity, float.PositiveInfinity), depth + 1);
                
                float minSize = isColumn
                    ? ResolveGridItemMinimumContribution(style, metrics.MinContentWidth)
                    : metrics.ContentHeight;
                float maxSize = isColumn ? metrics.MaxContentWidth : metrics.ContentHeight;

                UpdateTrackSizes(track, minSize, maxSize);
            }

            // Second Pass: Multi-track spanning items
            var spanningItems = items.Where(i => positions.ContainsKey(i) && (isColumn ? positions[i].ColumnSpan : positions[i].RowSpan) > 1)
                                    .OrderBy(i => isColumn ? positions[i].ColumnSpan : positions[i].RowSpan);

            foreach (var item in spanningItems)
            {
                var pos = positions[item];
                int start = (isColumn ? pos.ColumnStart : pos.RowStart) - 1;
                int span = isColumn ? pos.ColumnSpan : pos.RowSpan;
                if (start < 0 || start + span > tracks.Count) continue;

                var spannedTracks = tracks.GetRange(start, span);
                if (!styles.TryGetValue(item, out var style)) continue;

                // Measure multi-track item
                var metrics = measureNode(item, new SKSize(float.PositiveInfinity, float.PositiveInfinity), depth + 1);

                float minSize = isColumn
                    ? ResolveGridItemMinimumContribution(style, metrics.MinContentWidth)
                    : metrics.ContentHeight;
                float maxSize = isColumn ? metrics.MaxContentWidth : metrics.ContentHeight;

                // Distribute minSize
                DistributeExtraSpace(spannedTracks, minSize, true);
                // Distribute maxSize
                DistributeExtraSpace(spannedTracks, maxSize, false);
            }
        }

        private static void UpdateTrackSizes(GridTrack track, float minSize, float maxSize)
        {
            if (track.MaxLimit.IsFlex)
            {
                if (track.MinLimit.Type == GridUnitType.Auto || track.MinLimit.Type == GridUnitType.MinContent)
                {
                    track.BaseSize = Math.Max(track.BaseSize, minSize);
                }
                else if (track.MinLimit.Type == GridUnitType.MaxContent)
                {
                    track.BaseSize = Math.Max(track.BaseSize, maxSize);
                }
                else if (track.MinLimit.IsPx)
                {
                    track.BaseSize = Math.Max(track.BaseSize, track.MinLimit.Value);
                }

                track.GrowthLimit = Math.Max(track.GrowthLimit, maxSize);
                return;
            }

            if (track.MinLimit.Type == GridUnitType.MinContent || track.MinLimit.Type == GridUnitType.Auto)
                track.BaseSize = Math.Max(track.BaseSize, minSize);
            else if (track.MinLimit.Type == GridUnitType.MaxContent)
                track.BaseSize = Math.Max(track.BaseSize, maxSize);

            if (track.MaxLimit.Type == GridUnitType.MaxContent || track.MaxLimit.Type == GridUnitType.Auto)
                track.GrowthLimit = Math.Max(track.GrowthLimit, maxSize);
        }

        private static float ResolveGridItemMinimumContribution(CssComputed style, float minContentWidth)
        {
            if (style == null)
            {
                return Math.Max(0f, minContentWidth);
            }

            if (style.MinWidth.HasValue)
            {
                return Math.Max(0f, (float)style.MinWidth.Value);
            }

            string overflow = (style.OverflowX ?? style.Overflow)?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(overflow) && overflow != "visible")
            {
                return 0f;
            }

            return Math.Max(0f, minContentWidth);
        }

        private static void DistributeExtraSpace(List<GridTrack> tracks, float requiredSpace, bool isMin)
        {
            float currentSum = tracks.Sum(t => isMin ? t.BaseSize : (float.IsInfinity(t.GrowthLimit) ? t.BaseSize : t.GrowthLimit));
            float extra = requiredSpace - currentSum;
            if (extra <= 0) return;

            // Simple distribution: evenly among content-sized tracks
            var growable = tracks.Where(t => t.IsAuto || t.MinLimit.Type == GridUnitType.MinContent || t.MinLimit.Type == GridUnitType.MaxContent || t.MaxLimit.IsFlex).ToList();
            if (growable.Count == 0) growable = tracks; // Fallback

            float share = extra / growable.Count;
            foreach (var t in growable)
            {
                if (isMin) t.BaseSize += share;
                else if (!float.IsInfinity(t.GrowthLimit)) t.GrowthLimit += share;
                else t.BaseSize += share; // Growth limit infinite means base size is the constraint for now
            }
        }

        private static bool HasDefiniteBlockSize(CssComputed style, float availableHeight)
        {
            if (style == null)
            {
                return false;
            }

            if (style.Height.HasValue)
            {
                return true;
            }

            return style.HeightPercent.HasValue &&
                   !float.IsNaN(availableHeight) &&
                   !float.IsInfinity(availableHeight) &&
                   availableHeight > 0f;
        }
    }
}
