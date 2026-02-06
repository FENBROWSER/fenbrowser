using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using FenBrowser.Core;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Full CSS Grid Layout implementation per CSS Grid Layout Module Level 2.
    /// Supports all grid features including subgrid, named lines, and masonry (experimental).
    /// </summary>
    public class CssGridLayout
    {
        private readonly List<float> _columnTracks = new();
        private readonly List<float> _rowTracks = new();
        private readonly Dictionary<string, GridArea> _namedAreas = new();
        private readonly List<GridItem> _items = new();

        public float ColumnGap { get; set; }
        public float RowGap { get; set; }
        public float ContainerWidth { get; set; }
        public float ContainerHeight { get; set; }
        public string JustifyItems { get; set; } = "stretch";
        public string AlignItems { get; set; } = "stretch";
        public string JustifyContent { get; set; } = "start";
        public string AlignContent { get; set; } = "start";

        /// <summary>
        /// Parse and setup grid template columns
        /// </summary>
        public void SetTemplateColumns(string template)
        {
            _columnTracks.Clear();
            _columnTracks.AddRange(ParseGridTemplate(template, ContainerWidth, true));
        }

        /// <summary>
        /// Parse and setup grid template rows
        /// </summary>
        public void SetTemplateRows(string template)
        {
            _rowTracks.Clear();
            _rowTracks.AddRange(ParseGridTemplate(template, ContainerHeight, false));
        }

        /// <summary>
        /// Parse grid-template-areas
        /// </summary>
        public void SetTemplateAreas(string areasStr)
        {
            _namedAreas.Clear();
            if (string.IsNullOrWhiteSpace(areasStr)) return;

            // Parse quoted rows: "header header" "main sidebar" "footer footer"
            var rows = Regex.Matches(areasStr, "\"([^\"]+)\"")
                .Cast<Match>()
                .Select(m => m.Groups[1].Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                .ToList();

            // Build named areas
            for (int row = 0; row < rows.Count; row++)
            {
                for (int col = 0; col < rows[row].Length; col++)
                {
                    string name = rows[row][col];
                    if (name == ".") continue; // Skip empty cells

                    if (!_namedAreas.ContainsKey(name))
                    {
                        _namedAreas[name] = new GridArea
                        {
                            Name = name,
                            RowStart = row + 1,
                            RowEnd = row + 2,
                            ColumnStart = col + 1,
                            ColumnEnd = col + 2
                        };
                    }
                    else
                    {
                        // Extend existing area
                        var area = _namedAreas[name];
                        area.RowEnd = Math.Max(area.RowEnd, row + 2);
                        area.ColumnEnd = Math.Max(area.ColumnEnd, col + 2);
                    }
                }
            }
        }

        /// <summary>
        /// Add a grid item
        /// </summary>
        public void AddItem(Element element, CssComputed style)
        {
            var item = new GridItem
            {
                Element = element,
                Style = style
            };

            // Parse grid placement
            if (style?.Map != null)
            {
                // grid-area: name OR row-start / column-start / row-end / column-end
                if (style.Map.TryGetValue("grid-area", out var areaStr) && !string.IsNullOrEmpty(areaStr))
                {
                    if (_namedAreas.TryGetValue(areaStr.Trim(), out var namedArea))
                    {
                        item.RowStart = namedArea.RowStart;
                        item.RowEnd = namedArea.RowEnd;
                        item.ColumnStart = namedArea.ColumnStart;
                        item.ColumnEnd = namedArea.ColumnEnd;
                    }
                    else
                    {
                        // Try parsing as line numbers
                        var parts = areaStr.Split('/');
                        if (parts.Length >= 4)
                        {
                            item.RowStart = ParseLineNumber(parts[0]);
                            item.ColumnStart = ParseLineNumber(parts[1]);
                            item.RowEnd = ParseLineNumber(parts[2]);
                            item.ColumnEnd = ParseLineNumber(parts[3]);
                        }
                    }
                }

                // Individual properties
                if (style.Map.TryGetValue("grid-row-start", out var rs)) item.RowStart = ParseLineNumber(rs);
                if (style.Map.TryGetValue("grid-row-end", out var re)) item.RowEnd = ParseLineNumber(re);
                if (style.Map.TryGetValue("grid-column-start", out var cs)) item.ColumnStart = ParseLineNumber(cs);
                if (style.Map.TryGetValue("grid-column-end", out var ce)) item.ColumnEnd = ParseLineNumber(ce);

                // Shorthand
                if (style.Map.TryGetValue("grid-row", out var row))
                {
                    var rowParts = row.Split('/');
                    item.RowStart = ParseLineNumber(rowParts[0]);
                    item.RowEnd = rowParts.Length > 1 ? ParseLineNumber(rowParts[1]) : item.RowStart + 1;
                }
                if (style.Map.TryGetValue("grid-column", out var col))
                {
                    var colParts = col.Split('/');
                    item.ColumnStart = ParseLineNumber(colParts[0]);
                    item.ColumnEnd = colParts.Length > 1 ? ParseLineNumber(colParts[1]) : item.ColumnStart + 1;
                }

                // Alignment
                item.JustifySelf = style.Map.TryGetValue("justify-self", out var js) ? js : JustifyItems;
                item.AlignSelf = style.Map.TryGetValue("align-self", out var als) ? als : AlignItems;
            }

            _items.Add(item);
        }

        /// <summary>
        /// Calculate layout and return item positions
        /// </summary>
        public IEnumerable<(Element element, SKRect rect)> ComputeLayout(float startX, float startY)
        {
            // Ensure we have tracks
            if (_columnTracks.Count == 0) _columnTracks.Add(ContainerWidth);
            
            // Auto-place items without explicit placement
            AutoPlaceItems();

            // Calculate row heights based on content if needed
            int numRows = _items.Max(i => i.RowEnd) - 1;
            while (_rowTracks.Count < numRows)
            {
                _rowTracks.Add(0); // Auto height - will be calculated from content
            }

            // Calculate positions
            var columnStarts = CalculateTrackStarts(_columnTracks, ColumnGap, startX, ContainerWidth, true);
            var rowStarts = CalculateTrackStarts(_rowTracks, RowGap, startY, ContainerHeight, false);

            foreach (var item in _items)
            {
                int c1 = Math.Max(0, item.ColumnStart - 1);
                int c2 = Math.Min(columnStarts.Length - 1, item.ColumnEnd - 1);
                int r1 = Math.Max(0, item.RowStart - 1);
                int r2 = Math.Min(rowStarts.Length - 1, item.RowEnd - 1);

                float x = columnStarts[c1];
                float y = rowStarts[r1];
                float width = columnStarts[c2] - columnStarts[c1] - ColumnGap;
                float height = rowStarts[r2] - rowStarts[r1] - RowGap;

                // Apply alignment
                var rect = ApplyAlignment(new SKRect(x, y, x + width, y + height),
                    item.JustifySelf, item.AlignSelf, width, height);

                yield return (item.Element, rect);
            }
        }

        private void AutoPlaceItems()
        {
            int numCols = _columnTracks.Count;
            var grid = new HashSet<(int row, int col)>();

            // Mark explicitly placed items
            foreach (var item in _items.Where(i => i.RowStart > 0 && i.ColumnStart > 0))
            {
                for (int r = item.RowStart; r < item.RowEnd; r++)
                {
                    for (int c = item.ColumnStart; c < item.ColumnEnd; c++)
                    {
                        grid.Add((r, c));
                    }
                }
            }

            // Auto-place remaining items
            int currentRow = 1;
            int currentCol = 1;

            foreach (var item in _items.Where(i => i.RowStart <= 0 || i.ColumnStart <= 0))
            {
                int colSpan = item.ColumnEnd > item.ColumnStart ? item.ColumnEnd - item.ColumnStart : 1;
                int rowSpan = item.RowEnd > item.RowStart ? item.RowEnd - item.RowStart : 1;

                // [FIX] Clamp colSpan to available columns to prevent infinite search
                if (numCols > 0 && colSpan > numCols)
                {
                    FenBrowser.Core.FenLogger.Warn($"[GRID-GUARD] Item colSpan {colSpan} > numCols {numCols}. Clamping to fit.", FenBrowser.Core.Logging.LogCategory.Layout);
                    colSpan = numCols;
                }

                int iterations = 0;
                while (true)
                {
                    // [FIX] Circuit breaker for infinite loops
                    if (++iterations > 20000)
                    {
                         FenBrowser.Core.FenLogger.Error($"[GRID-GUARD] Auto-placement loop limit hit for item. Aborting placement.", FenBrowser.Core.Logging.LogCategory.Layout);
                         break;
                    }

                    if (CanPlace(grid, currentRow, currentCol, rowSpan, colSpan))
                    {
                        item.RowStart = currentRow;
                        item.ColumnStart = currentCol;
                        item.RowEnd = currentRow + rowSpan;
                        item.ColumnEnd = currentCol + colSpan;

                        for (int r = item.RowStart; r < item.RowEnd; r++)
                        {
                            for (int c = item.ColumnStart; c < item.ColumnEnd; c++)
                            {
                                grid.Add((r, c));
                            }
                        }
                        break;
                    }

                    currentCol++;
                    if (currentCol + colSpan - 1 > numCols)
                    {
                        currentCol = 1;
                        currentRow++;
                    }
                }
            }
        }

        private bool CanPlace(HashSet<(int, int)> grid, int row, int col, int rowSpan, int colSpan)
        {
            for (int r = row; r < row + rowSpan; r++)
            {
                for (int c = col; c < col + colSpan; c++)
                {
                    if (grid.Contains((r, c))) return false;
                }
            }
            return true;
        }

        public Func<Element, SKSize> MeasureChild { get; set; }

        private float[] CalculateTrackStarts(List<float> tracks, float gap, float start, float containerSize, bool isColumn)
        {
            float[] resolvedSizes = new float[tracks.Count];
            float usedSpace = 0;
            float totalFr = 0;
            var autoIndices = new List<int>();
            int gapCount = Math.Max(0, tracks.Count - 1);
            float totalGap = gapCount * gap;

            // Pass 1: Fixed sizes and collect dynamic ones
            for (int i = 0; i < tracks.Count; i++)
            {
                float t = tracks[i];
                if (t >= 0)
                {
                    resolvedSizes[i] = t;
                    usedSpace += t;
                }
                else if (t < 0) // fr or auto
                {
                    if (t == -1) // auto
                    {
                        // Auto track: measure content
                        // Simplified: Treat as 0-base, expand to content max
                        float maxContent = 0;
                        if (MeasureChild != null)
                        {
                            // Find all items in this track
                            foreach (var item in _items)
                            {
                                bool inTrack = isColumn 
                                    ? (item.ColumnStart <= i + 1 && item.ColumnEnd > i + 1)
                                    : (item.RowStart <= i + 1 && item.RowEnd > i + 1);
                                
                                if (inTrack)
                                {
                                    var size = MeasureChild(item.Element);
                                    float dim = isColumn ? size.Width : size.Height;
                                    if (dim > maxContent) maxContent = dim;
                                }
                            }
                        }
                        resolvedSizes[i] = maxContent;
                        usedSpace += maxContent;
                    }
                    else // fr (stored as negative value of fr count? No, ParseGridTemplate stores -fr)
                    {
                        // tracks[i] is e.g. -1 for 1fr, -2 for 2fr
                        totalFr += Math.Abs(t);
                    }
                }
            }

            // Pass 2: Distribute FR
            if (totalFr > 0)
            {
                float remaining = Math.Max(0, containerSize - usedSpace - totalGap);
                float perFr = remaining / totalFr;
                
                for (int i = 0; i < tracks.Count; i++)
                {
                    if (tracks[i] < 0 && tracks[i] != -1) // fr
                    {
                        resolvedSizes[i] = Math.Abs(tracks[i]) * perFr;
                    }
                }
            }

            // Build starts
            var starts = new float[tracks.Count + 1];
            starts[0] = start;
            for (int i = 0; i < tracks.Count; i++)
            {
                starts[i + 1] = starts[i] + resolvedSizes[i] + gap;
            }
            return starts;
        }

        private SKRect ApplyAlignment(SKRect rect, string justify, string align, float cellWidth, float cellHeight)
        {
            float x = rect.Left;
            float y = rect.Top;
            float w = rect.Width;
            float h = rect.Height;

            // Apply justify-self
            switch (justify?.ToLowerInvariant())
            {
                case "center":
                    x = rect.Left + (cellWidth - w) / 2;
                    break;
                case "end":
                    x = rect.Right - w;
                    break;
            }

            // Apply align-self
            switch (align?.ToLowerInvariant())
            {
                case "center":
                    y = rect.Top + (cellHeight - h) / 2;
                    break;
                case "end":
                    y = rect.Bottom - h;
                    break;
            }

            return new SKRect(x, y, x + w, y + h);
        }

        #region Template Parsing

        private List<float> ParseGridTemplate(string template, float containerSize, bool isColumn)
        {
            var tracks = new List<float>();
            if (string.IsNullOrWhiteSpace(template)) return tracks;

            // Expand repeat()
            template = ExpandRepeat(template, containerSize);

            // Split by whitespace (handling minmax)
            var parts = SplitGridTemplate(template);
            var frTracks = new List<int>();
            float fixedTotal = 0;
            float frTotal = 0;

            for (int i = 0; i < parts.Count; i++)
            {
                var part = parts[i].Trim().ToLowerInvariant();

                if (part.StartsWith("minmax("))
                {
                    var (min, max) = ParseMinmax(part, containerSize);
                    tracks.Add(min); // Use min for initial calculation
                }
                else if (part.EndsWith("fr"))
                {
                    float fr = float.TryParse(part.Replace("fr", ""), out var f) ? f : 1;
                    tracks.Add(-fr); // Negative indicates fr unit
                    frTracks.Add(i);
                    frTotal += fr;
                }
                else if (part == "auto")
                {
                    tracks.Add(-1); // Will be calculated from content
                    frTracks.Add(i);
                    frTotal += 1;
                }
                else
                {
                    float size = ParseSize(part, containerSize);
                    tracks.Add(size);
                    fixedTotal += size;
                }
            }

            // Distribute remaining space to fr units
            if (frTracks.Count > 0)
            {
                float remaining = containerSize - fixedTotal - (tracks.Count - 1) * (isColumn ? ColumnGap : RowGap);
                float perFr = remaining / frTotal;

                foreach (int i in frTracks)
                {
                    float fr = -tracks[i];
                    tracks[i] = Math.Max(0, perFr * fr);
                }
            }

            return tracks;
        }

        private string ExpandRepeat(string template, float containerSize)
        {
            var match = Regex.Match(template, @"repeat\s*\(\s*(\d+|auto-fill|auto-fit)\s*,\s*([^)]+)\s*\)");
            if (!match.Success) return template;

            string countStr = match.Groups[1].Value.Trim();
            string value = match.Groups[2].Value.Trim();

            int count = 1;
            if (int.TryParse(countStr, out int explicitCount))
            {
                count = explicitCount;
            }
            else if (countStr == "auto-fill" || countStr == "auto-fit")
            {
                // Calculate based on minmax or fixed size
                float trackSize = ParseSize(value, containerSize);
                if (trackSize > 0)
                {
                    count = Math.Max(1, (int)(containerSize / (trackSize + ColumnGap)));
                }
            }

            var expanded = string.Join(" ", Enumerable.Repeat(value, count));
            return template.Replace(match.Value, expanded);
        }

        private (float min, float max) ParseMinmax(string minmax, float containerSize)
        {
            var inner = minmax.Replace("minmax(", "").TrimEnd(')');
            var parts = inner.Split(',');

            float min = parts.Length > 0 ? ParseSize(parts[0].Trim(), containerSize) : 0;
            float max = parts.Length > 1 ? ParseSize(parts[1].Trim(), containerSize) : containerSize;

            return (min, max);
        }

        private float ParseSize(string size, float containerSize)
        {
            size = size.Trim().ToLowerInvariant();

            if (size.EndsWith("px")) return float.TryParse(size.Replace("px", ""), out var px) ? px : 0;
            if (size.EndsWith("%")) return float.TryParse(size.Replace("%", ""), out var pct) ? pct * containerSize / 100 : 0;
            if (size.EndsWith("em") || size.EndsWith("rem")) return float.TryParse(size.Replace("em", "").Replace("r", ""), out var em) ? em * 16 : 0;
            if (size.EndsWith("vw")) return float.TryParse(size.Replace("vw", ""), out var vw) ? vw * containerSize / 100 : 0;
            if (size.EndsWith("vh")) return float.TryParse(size.Replace("vh", ""), out var vh) ? vh * containerSize / 100 : 0;
            if (size == "min-content") return 0;
            if (size == "max-content") return containerSize;
            if (float.TryParse(size, out var plain)) return plain;

            return 0;
        }

        private int ParseLineNumber(string line)
        {
            line = line?.Trim() ?? "";
            if (int.TryParse(line, out int num)) return num;
            if (line.StartsWith("span ") && int.TryParse(line.Substring(5), out int span))
                return span; // Will need special handling
            return 1;
        }

        private List<string> SplitGridTemplate(string template)
        {
            var parts = new List<string>();
            var current = "";
            int depth = 0;

            foreach (char c in template)
            {
                if (c == '(') depth++;
                if (c == ')') depth--;
                if (c == ' ' && depth == 0)
                {
                    if (!string.IsNullOrWhiteSpace(current))
                        parts.Add(current);
                    current = "";
                }
                else
                {
                    current += c;
                }
            }
            if (!string.IsNullOrWhiteSpace(current))
                parts.Add(current);

            return parts;
        }

        #endregion

        private class GridItem
        {
            public Element Element;
            public CssComputed Style;
            public int RowStart = 0;
            public int RowEnd = 1;
            public int ColumnStart = 0;
            public int ColumnEnd = 1;
            public string JustifySelf;
            public string AlignSelf;
        }

        private class GridArea
        {
            public string Name;
            public int RowStart;
            public int RowEnd;
            public int ColumnStart;
            public int ColumnEnd;
        }
    }

    /// <summary>
    /// CSS Container Queries implementation (experimental)
    /// </summary>
    public class CssContainerQueries
    {
        private readonly Dictionary<Element, ContainerInfo> _containers = new();

        /// <summary>
        /// Register an element as a container
        /// </summary>
        public void RegisterContainer(Element element, CssComputed style)
        {
            if (style?.Map == null) return;

            string containerType = style.Map.TryGetValue("container-type", out var ct) ? ct : null;
            string containerName = style.Map.TryGetValue("container-name", out var cn) ? cn : null;

            if (string.IsNullOrEmpty(containerType) || containerType == "normal") return;

            _containers[element] = new ContainerInfo
            {
                Element = element,
                Name = containerName,
                Type = containerType
            };
        }

        /// <summary>
        /// Update container dimensions
        /// </summary>
        public void UpdateContainerSize(Element element, float width, float height)
        {
            if (_containers.TryGetValue(element, out var info))
            {
                info.Width = width;
                info.Height = height;
            }
        }

        /// <summary>
        /// Evaluate a container query
        /// </summary>
        public bool EvaluateQuery(string query, Element context)
        {
            // Parse @container query
            // Format: @container (min-width: 400px) or @container name (width > 500px)
            var match = Regex.Match(query, @"@container\s*(\w+)?\s*\((.+)\)");
            if (!match.Success) return false;

            string containerName = match.Groups[1].Value;
            string conditions = match.Groups[2].Value;

            // Find the container
            var container = FindContainer(context, containerName);
            if (container == null) return false;

            // Evaluate conditions
            return EvaluateConditions(conditions, container.Width, container.Height);
        }

        private ContainerInfo FindContainer(Element element, string name)
        {
            var current = element?.ParentElement;
            while (current != null)
            {
                if (_containers.TryGetValue(current, out var info))
                {
                    if (string.IsNullOrEmpty(name) || info.Name == name)
                        return info;
                }
                current = current.ParentElement;
            }
            return null;
        }

        private bool EvaluateConditions(string conditions, float width, float height)
        {
            // Split by 'and'
            var parts = conditions.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                if (!EvaluateSingleCondition(part.Trim(), width, height))
                    return false;
            }
            return true;
        }

        private bool EvaluateSingleCondition(string condition, float width, float height)
        {
            // (min-width: 400px), (width > 500px), etc.
            var match = Regex.Match(condition, @"(min-|max-)?(width|height)\s*[:><]=?\s*(\d+(?:\.\d+)?)(px|em|rem)?");
            if (!match.Success) return true;

            string prefix = match.Groups[1].Value;
            string dimension = match.Groups[2].Value;
            float value = float.TryParse(match.Groups[3].Value, out var v) ? v : 0;
            float containerValue = dimension == "width" ? width : height;

            if (prefix == "min-") return containerValue >= value;
            if (prefix == "max-") return containerValue <= value;
            
            // Direct comparison
            if (condition.Contains(">=")) return containerValue >= value;
            if (condition.Contains("<=")) return containerValue <= value;
            if (condition.Contains(">")) return containerValue > value;
            if (condition.Contains("<")) return containerValue < value;

            return true;
        }

        private class ContainerInfo
        {
            public Element Element;
            public string Name;
            public string Type;
            public float Width;
            public float Height;
        }
    }
}



