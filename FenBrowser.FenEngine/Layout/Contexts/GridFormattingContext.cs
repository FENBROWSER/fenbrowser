using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.Core.Css;
using FenBrowser.FenEngine.Layout.Tree;
using SkiaSharp;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Layout.Contexts
{
    /// <summary>
    /// Implements CSS Grid Layout (Level 1).
    /// Handles simplified track sizing (fr, px, auto) and auto-placement.
    /// </summary>
    public class GridFormattingContext : FormattingContext
    {
        private static GridFormattingContext _instance;
        public static GridFormattingContext Instance => _instance ??= new GridFormattingContext();

        public override void Layout(LayoutBox box, LayoutState state)
        {
            if (!(box is BlockBox container)) return;

            // 1. Resolve Container Dimensions
            LayoutBoxOps.ComputeBoxModelFromContent(container, 
                state.AvailableSize.Width, 
                Math.Max(0, state.AvailableSize.Height)); // Placeholder

            // 2. Parse Grid Definition
            var style = container.ComputedStyle;
            var colDefinitions = ParseTrackList(style.Map.ContainsKey("grid-template-columns") ? style.Map["grid-template-columns"] : "none");
            var rowDefinitions = ParseTrackList(style.Map.ContainsKey("grid-template-rows") ? style.Map["grid-template-rows"] : "none");

            // Implicit grid support
            var autoFlow = style.Map.ContainsKey("grid-auto-flow") ? style.Map["grid-auto-flow"] : "row";

            // 3. Place Items into Cells (Auto-Placement Algorithm)
            var items = container.Children.Where(c => c.ComputedStyle?.Display?.Contains("none") != true).ToList();
            var placement = new Dictionary<LayoutBox, GridArea>();
            int cursorRow = 1, cursorCol = 1;

            if (colDefinitions.Count == 0) colDefinitions.Add(new GridTrack(1, GridUnit.Fr)); // Default 1 col
            int maxCols = colDefinitions.Count; // Simplified: explicit columns only for now

            foreach (var item in items)
            {
                // Simple auto-placement: Next available cell
                // Todo: Read grid-column-start/end
                placement[item] = new GridArea(cursorRow, cursorCol, 1, 1);
                
                cursorCol++;
                if (cursorCol > maxCols)
                {
                    cursorCol = 1;
                    cursorRow++;
                }
            }

            // 4. Size Tracks (The Hard Part)
            // Simplified: Treat all 'fr' as equal shares of remaining space
            // Treat 'auto' as content-based (requires measuring items)
            float totalFixedWidth = colDefinitions.Where(t => t.Unit == GridUnit.Px).Sum(t => t.Value);
            float availableSpace = container.Geometry.ContentBox.Width - totalFixedWidth;
            float totalFr = colDefinitions.Where(t => t.Unit == GridUnit.Fr).Sum(t => t.Value);
            
            var colWidths = new List<float>();
            foreach (var track in colDefinitions)
            {
                if (track.Unit == GridUnit.Px) colWidths.Add(track.Value);
                else if (track.Unit == GridUnit.Fr) colWidths.Add(availableSpace * (track.Value / totalFr));
                else colWidths.Add(0); // Auto not fully supported yet in this snippet
            }

            // Rows - usually auto-sized based on content height
            // We need to measure row heights based on items in them
            var rowHeights = new Dictionary<int, float>();
            foreach (var kvp in placement)
            {
                var item = kvp.Key;
                var area = kvp.Value;
                
                // Measure item with fixed width
                float targetWidth = colWidths[area.ColStart - 1]; // Simplified 1-cell span
                
                // Layout Item
                var childState = state.Clone();
                childState.AvailableSize = new SKSize(targetWidth, state.AvailableSize.Height); // Unconstrained height
                FormattingContext.Resolve(item).Layout(item, childState); // Recursive layout
                
                float h = item.Geometry.MarginBox.Height;
                if (!rowHeights.ContainsKey(area.RowStart) || h > rowHeights[area.RowStart])
                    rowHeights[area.RowStart] = h;
            }

            // 5. Final Geometry Calculation
            float gapRow = ParseGap(style, "row-gap");
            float gapCol = ParseGap(style, "column-gap");

            foreach (var kvp in placement)
            {
                var item = kvp.Key;
                var area = kvp.Value;
                
                float x = 0;
                for (int i = 0; i < area.ColStart - 1; i++) x += colWidths[i] + gapCol;
                
                float y = 0;
                for (int i = 0; i < area.RowStart - 1; i++) y += (rowHeights.ContainsKey(i+1) ? rowHeights[i+1] : 0) + gapRow; // 1-based index issues?

                LayoutBoxOps.SetPosition(item, x, y);
                // Resize explicitly to fill cell?
                // grid-stretch behavior...
                var targetW = colWidths[area.ColStart - 1];
                var targetH = rowHeights[area.RowStart];
                LayoutBoxOps.ComputeBoxModelFromContent(item, targetW, targetH); 
            }
            
            // Set Container Height
            float finalH = 0;
            foreach(var h in rowHeights.Values) finalH += h + gapRow;
             if (rowHeights.Count > 0) finalH -= gapRow; // Remove last gap
            
            LayoutBoxOps.ComputeBoxModelFromContent(container, container.Geometry.ContentBox.Width, finalH);
        }

        private float ParseGap(CssComputed style, string prop)
        {
            if (style.Map.ContainsKey(prop) && float.TryParse(style.Map[prop].Replace("px",""), out float val)) return val;
            return 0;
        }

        private List<GridTrack> ParseTrackList(string val)
        {
            var list = new List<GridTrack>();
            if (val == "none") return list;
            
            var parts = val.Split(' ');
            foreach (var p in parts)
            {
                if (p.EndsWith("fr") && float.TryParse(p.Replace("fr",""), out float fr)) 
                    list.Add(new GridTrack(fr, GridUnit.Fr));
                else if (p.EndsWith("px") && float.TryParse(p.Replace("px",""), out float px)) 
                    list.Add(new GridTrack(px, GridUnit.Px));
                else if (p == "auto") 
                    list.Add(new GridTrack(0, GridUnit.Auto));
            }
            return list;
        }

        private struct GridArea { 
            public int RowStart, ColStart, RowSpan, ColSpan; 
            public GridArea(int r, int c, int rs, int cs) { RowStart=r; ColStart=c; RowSpan=rs; ColSpan=cs; }
        }

        private struct GridTrack {
            public float Value;
            public GridUnit Unit;
            public GridTrack(float v, GridUnit u) { Value = v; Unit = u; }
        }

        private enum GridUnit { Px, Fr, Auto, Percent }
    }
}
