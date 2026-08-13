using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FenBrowser.FenEngine.Layout
{
    public static partial class GridLayoutComputer
    {
        private class NamedArea
        {
            public string Name;
            public int RowStart;
            public int RowEnd;
            public int ColStart;
            public int ColEnd;
        }

        private sealed class AreaBounds
        {
            public int MinRow = int.MaxValue;
            public int MaxRow = int.MinValue;
            public int MinCol = int.MaxValue;
            public int MaxCol = int.MinValue;
            public int CellCount;
        }

        private static Dictionary<string, NamedArea> ParseGridTemplateAreas(string areasDef)
        {
            var empty = new Dictionary<string, NamedArea>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(areasDef)) return empty;

            // Each quoted string is one grid row. CSS requires every row to contain
            // the same number of cells and every named area to form one rectangle.
            var matches = Regex.Matches(areasDef, "\"[^\"]+\"|'[^']+'");
            if (matches.Count == 0) return empty;

            var rows = new List<string[]>(matches.Count);
            var expectedColumns = -1;
            foreach (Match match in matches)
            {
                var rowString = match.Value.Trim('"', '\'');
                var cells = rowString.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (cells.Length == 0)
                {
                    return empty;
                }

                if (expectedColumns < 0)
                {
                    expectedColumns = cells.Length;
                }
                else if (cells.Length != expectedColumns)
                {
                    // The entire grid-template-areas value is invalid when row widths
                    // differ. Returning no named areas is safer than fabricating tracks.
                    return empty;
                }

                rows.Add(cells);
            }

            var boundsByName = new Dictionary<string, AreaBounds>(StringComparer.Ordinal);
            for (var row = 0; row < rows.Count; row++)
            {
                for (var col = 0; col < rows[row].Length; col++)
                {
                    var name = rows[row][col];
                    if (IsNullCellToken(name))
                    {
                        continue;
                    }

                    if (!boundsByName.TryGetValue(name, out var bounds))
                    {
                        bounds = new AreaBounds();
                        boundsByName[name] = bounds;
                    }

                    bounds.MinRow = Math.Min(bounds.MinRow, row);
                    bounds.MaxRow = Math.Max(bounds.MaxRow, row);
                    bounds.MinCol = Math.Min(bounds.MinCol, col);
                    bounds.MaxCol = Math.Max(bounds.MaxCol, col);
                    bounds.CellCount++;
                }
            }

            var result = new Dictionary<string, NamedArea>(StringComparer.Ordinal);
            foreach (var pair in boundsByName)
            {
                var name = pair.Key;
                var bounds = pair.Value;
                var rectangleCellCount =
                    (bounds.MaxRow - bounds.MinRow + 1) *
                    (bounds.MaxCol - bounds.MinCol + 1);

                if (rectangleCellCount != bounds.CellCount)
                {
                    return empty;
                }

                // Count equality is necessary but check the rectangle explicitly as
                // well; this keeps the invariant obvious if the parser changes later.
                for (var row = bounds.MinRow; row <= bounds.MaxRow; row++)
                {
                    for (var col = bounds.MinCol; col <= bounds.MaxCol; col++)
                    {
                        if (!string.Equals(rows[row][col], name, StringComparison.Ordinal))
                        {
                            return empty;
                        }
                    }
                }

                result[name] = new NamedArea
                {
                    Name = name,
                    RowStart = bounds.MinRow + 1,
                    RowEnd = bounds.MaxRow + 2,
                    ColStart = bounds.MinCol + 1,
                    ColEnd = bounds.MaxCol + 2
                };
            }

            return result;
        }

        private static bool IsNullCellToken(string token)
        {
            if (string.IsNullOrEmpty(token)) return false;
            for (var i = 0; i < token.Length; i++)
            {
                if (token[i] != '.') return false;
            }
            return true;
        }
    }
}
