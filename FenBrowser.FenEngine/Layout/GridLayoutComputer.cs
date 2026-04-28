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
        private static (Dictionary<Element, GridItemPosition> Positions, int RowCount, int ColCount) ComputePlacements(
            List<Element> items, 
            IReadOnlyDictionary<Node, CssComputed> styles, 
            int explicitColCount, 
            int explicitRowCount,
            string autoFlow,
            Dictionary<string, NamedArea> areas) // "row", "column", "row dense", "column dense"
        {
            var positions = new Dictionary<Element, GridItemPosition>();
            var map = new GridOccupancyMap();
            
            bool isDense = autoFlow.Contains("dense");
            bool isColumnFlow = autoFlow.Contains("column");

            // Temporary list for auto items
            var pendingAuto = new List<Element>();
            
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

            foreach (var item in items)
            {
                var style = styles.TryGetValue(item, out var s) ? s : null;
                var rawPos = DetermineGridPosition(style, item, areas);
                
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
                    pendingAuto.Add(item);
                }
            }

            // Iterate pending items
            foreach (var item in pendingAuto)
            {
                var style = styles.TryGetValue(item, out var s) ? s : null;
                var rawPos = DetermineGridPosition(style, item, areas);

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
                        pos.RowEnd = r + rawPos.RowSpan;

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
                        pos.ColumnEnd = c + rawPos.ColSpan;

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
                            int limit = explicitColCount > 0 ? explicitColCount : int.MaxValue;
                            bool overflow = pos.ColumnStart > limit;

                            if (fits && (!overflow || explicitColCount == 0))
                            {
                                positions[item] = pos;
                                map.Mark(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd);
                                UpdateBounds(pos);
                                if (!isDense) cursorCol = pos.ColumnEnd;
                                break;
                            }
                            
                            cursorCol++;
                            if (overflow && explicitColCount > 0)
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
                        pos.ColumnEnd = c + rawPos.ColSpan;
                        
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
                        pos.RowEnd = r + rawPos.RowSpan;

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
                            int limit = explicitRowCount > 0 ? explicitRowCount : int.MaxValue;
                            bool overflow = pos.RowStart > limit;

                            if (fits && (!overflow || explicitRowCount == 0))
                            {
                                positions[item] = pos;
                                map.Mark(pos.ColumnStart, pos.ColumnEnd, pos.RowStart, pos.RowEnd);
                                UpdateBounds(pos);
                                if (!isDense) cursorRow = pos.RowEnd;
                                break;
                            }

                            cursorRow++;
                            if (overflow && explicitRowCount > 0)
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

        private class RawGridPosition {
            public int RowStart; 
            public int? RowEnd; // null = auto/span
            public int RowSpan = 1;
            public int ColStart;
            public int? ColEnd;
            public int ColSpan = 1;
            public bool HasExplicitRow;
            public bool HasExplicitCol;
            public Element Element;
        }

        private static RawGridPosition DetermineGridPosition(CssComputed style, Element el, Dictionary<string, NamedArea> areas)
        {
            var p = new RawGridPosition { Element = el };

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
            if (int.TryParse(style.GridRowStart, out int rsProp)) { p.RowStart = rsProp; p.HasExplicitRow = true; }
            else p.RowStart = 1;

            if (int.TryParse(style.GridRowEnd, out int reProp)) { p.RowEnd = reProp; }
            else if (TryParseSpan(style.GridRowEnd, out int rspan)) { p.RowSpan = rspan; }
            
            // Col
            if (int.TryParse(style.GridColumnStart, out int csProp)) { p.ColStart = csProp; p.HasExplicitCol = true; }
            else p.ColStart = 1;

            if (int.TryParse(style.GridColumnEnd, out int ceProp)) { p.ColEnd = ceProp; }
            else if (TryParseSpan(style.GridColumnEnd, out int cspan)) { p.ColSpan = cspan; }
            
            return p;
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
            IEnumerable<Node> childrenSource = null)
        {
            if (container == null || container.ChildNodes == null)
                return new LayoutMetrics();

            var style = styles.TryGetValue(container, out var s) ? s : null;
            if (style == null) return new LayoutMetrics();

            // Parse grid template
            var columnTracks = ParseTracks(style.GridTemplateColumns, availableSize.Width, (float)(style.ColumnGap ?? style.Gap ?? 0));
            var rowTracks = ParseTracks(style.GridTemplateRows, availableSize.Height, (float)(style.RowGap ?? style.Gap ?? 0));
            
            // Parse areas (Phase 3)
            var areas = ParseGridTemplateAreas(style.GridTemplateAreas);

            int columnTracksOriginalCount = columnTracks.Count;
            int rowTracksOriginalCount = rowTracks.Count;

            // Get gap values
            float columnGap = (float)(style.ColumnGap ?? style.Gap ?? 0);
            float rowGap = (float)(style.RowGap ?? style.Gap ?? 0);

            // Get grid items (exclude text nodes and hidden elements)
            var source = childrenSource ?? container.ChildNodes;
            var items = source
                .Where(c => !c.IsText() && c is Element)
                .Cast<Element>()
                .ToList();

            // If no explicit columns defined, create one auto column per item
            if (columnTracks.Count == 0)
            {
                // Wait, if no template is defined, we rely on implicit.
                // But auto-placement logic handles adding implicit.
                // We should start empty?
                // Phase 1 had logic to force 1 col per item if empty.
                // Standard: Start empty.
            }
            
            // Parse auto tracks
            var autoColTracks = ParseTracks(style.GridAutoColumns, availableSize.Width, (float)(style.ColumnGap ?? style.Gap ?? 0));
            var autoRowTracks = ParseTracks(style.GridAutoRows, availableSize.Height, (float)(style.RowGap ?? style.Gap ?? 0));
            if (autoColTracks.Count == 0) autoColTracks.Add(new GridTrack { MinLimit = GridTrackSize.Auto, MaxLimit = GridTrackSize.Auto });
            if (autoRowTracks.Count == 0) autoRowTracks.Add(new GridTrack { MinLimit = GridTrackSize.Auto, MaxLimit = GridTrackSize.Auto });
            
            string autoFlow = style.GridAutoFlow?.ToLowerInvariant() ?? "row";
            
            // Compute Layout
            var placement = ComputePlacements(items, styles, columnTracks.Count, rowTracks.Count, autoFlow, areas);
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
            // This sets BaseSize based on content
            MeasureTracksIntrinsic(columnTracks, items, positions, styles, true, depth, measureNode);
            MeasureTracksIntrinsic(rowTracks, items, positions, styles, false, depth, measureNode);
            
            // Resolve flexible tracks (fr units)
            ResolveFlexibleTracks(columnTracks, availableSize.Width, columnGap);
            
            // Measure rows
            MeasureAutoRowHeights(rowTracks, columnTracks, items, positions, styles, columnGap, depth, measureNode);
            if (HasDefiniteBlockSize(style, availableSize.Height))
            {
                ResolveFlexibleTracks(rowTracks, availableSize.Height, rowGap);
            }

            // Calculate total dimensions
            float totalWidth = columnTracks.Sum(t => t.BaseSize) + Math.Max(0, columnTracks.Count - 1) * columnGap;
            float totalHeight = rowTracks.Sum(t => t.BaseSize) + Math.Max(0, rowTracks.Count - 1) * rowGap;

            // Calculate Intrinsic Dimensions
            float minIntrinsicWidth = columnTracks.Sum(t => t.BaseSize) + Math.Max(0, columnTracks.Count - 1) * columnGap;
            float maxIntrinsicWidth = columnTracks.Sum(t => float.IsInfinity(t.GrowthLimit) ? t.BaseSize : t.GrowthLimit) + Math.Max(0, columnTracks.Count - 1) * columnGap;

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
            IEnumerable<Node> childrenSource = null)
        {
            if (container == null || container.ChildNodes == null) return;

            var style = styles.TryGetValue(container, out var s) ? s : null;
            if (style == null) return;

            // Parse areas (Phase 3)
            var areas = ParseGridTemplateAreas(style.GridTemplateAreas);

            float columnGap = (float)(style.ColumnGap ?? style.Gap ?? 0);
            float rowGap = (float)(style.RowGap ?? style.Gap ?? 0);

            // Parse grid template
            var columnTracks = ParseTracks(style.GridTemplateColumns, bounds.Width, columnGap);
            var rowTracks = ParseTracks(style.GridTemplateRows, bounds.Height, rowGap);

            int columnTracksOriginalCount = columnTracks.Count;
            int rowTracksOriginalCount = rowTracks.Count;



            var source = childrenSource ?? container.ChildNodes;
            var items = source
                .Where(c => !c.IsText() && c is Element)
                .Cast<Element>()
                .ToList();

            // Parse auto tracks
            var autoColTracks = ParseTracks(style.GridAutoColumns, bounds.Width, columnGap);
            var autoRowTracks = ParseTracks(style.GridAutoRows, bounds.Height, rowGap);
            if (autoColTracks.Count == 0) autoColTracks.Add(new GridTrack { MinLimit = GridTrackSize.Auto, MaxLimit = GridTrackSize.Auto });
            if (autoRowTracks.Count == 0) autoRowTracks.Add(new GridTrack { MinLimit = GridTrackSize.Auto, MaxLimit = GridTrackSize.Auto });

            string autoFlow = style.GridAutoFlow?.ToLowerInvariant() ?? "row";
            
            // Compute Layout
            var placement = ComputePlacements(items, styles, columnTracks.Count, rowTracks.Count, autoFlow, areas);
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
            // This sets BaseSize based on content
            MeasureTracksIntrinsic(columnTracks, items, positions, styles, true, depth, measureNode);
            MeasureTracksIntrinsic(rowTracks, items, positions, styles, false, depth, measureNode);

            // Ensure arrange pass uses the same content-derived auto-row sizing
            // as measure pass before flex/stretch resolution.
            MeasureAutoRowHeights(rowTracks, columnTracks, items, positions, styles, columnGap, depth, measureNode);

            // Resolve Track Sizes
            ResolveFlexibleTracks(columnTracks, bounds.Width, columnGap);
            if (HasDefiniteBlockSize(style, bounds.Height))
            {
                ResolveFlexibleTracks(rowTracks, bounds.Height, rowGap);
            }

            // Compute effective gaps for justify/align content
            float effectiveColumnGap = columnGap;
            float effectiveRowGap = rowGap;
            float contentXOffset = 0;
            float contentYOffset = 0;

            // Base totals with original gaps
            float baseGridWidth = columnTracks.Sum(t => t.BaseSize) + Math.Max(0, columnTracks.Count - 1) * columnGap;
            float baseGridHeight = rowTracks.Sum(t => t.BaseSize) + Math.Max(0, rowTracks.Count - 1) * rowGap;

            // JustifyContent (Horizontal Track Alignment)
            if (!string.IsNullOrEmpty(style.JustifyContent) && columnTracks.Count > 0)
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

            // AlignContent (Vertical Track Alignment)
            if (!string.IsNullOrEmpty(style.AlignContent) && rowTracks.Count > 0)
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

            // Compute Start Positions using effective gaps
            float[] colStarts = new float[columnTracks.Count + 1];
            float cx = contentXOffset;
            for (int i = 0; i < columnTracks.Count; i++)
            {
                colStarts[i] = cx;
                cx += columnTracks[i].BaseSize + effectiveColumnGap;
            }
            colStarts[columnTracks.Count] = cx;

            float[] rowStarts = new float[rowTracks.Count + 1];
            float cy = contentYOffset;
            for (int i = 0; i < rowTracks.Count; i++)
            {
                rowStarts[i] = cy;
                cy += rowTracks[i].BaseSize + effectiveRowGap;
            }
            rowStarts[rowTracks.Count] = cy;

            // Arrange Item
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
                if (c2 > c1) trackW -= columnGap;
                if (r2 > r1) trackH -= rowGap;
                
                if (trackW < 0) trackW = 0;
                if (trackH < 0) trackH = 0;

                // --- Phase 3: Item Alignment (JustifyItems/AlignItems/Self) ---
                var itemStyle = styles[item];
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
                    if (needsIntrinsicW && intrinsic.MaxChildWidth > 0)
                    {
                        itemW = Math.Min(trackW, intrinsic.MaxChildWidth);
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
                
                if (align == "center") cellY += (trackH - itemH) / 2;
                else if (align == "end" || align == "bottom" || align == "flex-end") cellY += (trackH - itemH);

                var itemRect = new SKRect(cellX, cellY, cellX + itemW, cellY + itemH);
                arrangeChild(item, itemRect, depth + 1);
            }
        }

        // --- Helpers identical to Phase 1 but included for completeness ---



        private static void ResolveFlexibleTracks(List<GridTrack> tracks, float availableSpace, float gap)
        {
            if (tracks.Count == 0) return;
            if (float.IsNaN(availableSpace) || float.IsInfinity(availableSpace) || availableSpace <= 0)
            {
                return;
            }

            // Calculate used space by FIXED tracks (non-flex)
            float usedSpace = 0;
            foreach (var t in tracks)
            {
                // If track is flex, it doesn't contribute to usedSpace (it consumes freeSpace)
                if (t.MaxLimit.IsFlex) continue;

                if (t.MinLimit.IsPx) usedSpace += t.MinLimit.Value;
                else if (t.BaseSize > 0) usedSpace += t.BaseSize; // Fallback
            }

            float gapSpace = Math.Max(0, tracks.Count - 1) * gap;
            float freeSpace = Math.Max(0, availableSpace - usedSpace - gapSpace);
            float totalFlex = tracks.Sum(t => t.FlexFactor);

            // Distribute free space to Flex tracks
            if (totalFlex > 0)
            {
                float frUnit = freeSpace / totalFlex;
                foreach (var track in tracks)
                {
                    if (track.MaxLimit.IsFlex)
                    {
                        float size = track.FlexFactor * frUnit;
                        
                        // Clamp to MinLimit
                        if (track.MinLimit.IsPx) size = Math.Max(size, track.MinLimit.Value);
                        
                        // Clamp to MaxLimit (if Px) - e.g. minmax(10, 100px) is not flex. 
                        // If minmax(100px, 1fr), Max is Flex.
                        // If minmax(100px, 200px), it is not flex.
                        
                        track.BaseSize = size;
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
            List<Element> items,
            Dictionary<Element, GridItemPosition> positions,
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
            List<Element> items,
            Dictionary<Element, GridItemPosition> positions,
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
                
                float minSize = isColumn ? metrics.MinContentWidth : metrics.ContentHeight;
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

                float minSize = isColumn ? metrics.MinContentWidth : metrics.ContentHeight;
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
                track.BaseSize = Math.Max(track.BaseSize, minSize);
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
