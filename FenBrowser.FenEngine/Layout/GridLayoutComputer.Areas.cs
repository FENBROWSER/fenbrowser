using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;

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

        private static Dictionary<string, NamedArea> ParseGridTemplateAreas(string areasDef)
        {
            var map = new Dictionary<string, NamedArea>();
            if (string.IsNullOrWhiteSpace(areasDef)) return map;

            // Split into rows (strings in quotes)
            var matches = Regex.Matches(areasDef, "\"[^\"]+\"|'[^']+'");
            if (matches.Count == 0) return map;

            int rowIndex = 1;
            foreach (Match match in matches)
            {
                var rowString = match.Value.Trim('"', '\'');
                var cells = rowString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                
                int colIndex = 1;
                foreach (var cellName in cells)
                {
                    if (cellName == ".") 
                    {
                        colIndex++;
                        continue;
                    }

                    if (!map.TryGetValue(cellName, out var area))
                    {
                        area = new NamedArea 
                        { 
                            Name = cellName, 
                            RowStart = rowIndex, 
                            RowEnd = rowIndex + 1, 
                            ColStart = colIndex, 
                            ColEnd = colIndex + 1 
                        };
                        map[cellName] = area;
                    }
                    else
                    {
                        // Extend existing area
                        // Assume rectangular (CSS Grid spec requires rectangular areas)
                        area.RowEnd = Math.Max(area.RowEnd, rowIndex + 1);
                        area.ColEnd = Math.Max(area.ColEnd, colIndex + 1);
                    }
                    colIndex++;
                }
                rowIndex++;
            }
            return map;
        }
    }
}

