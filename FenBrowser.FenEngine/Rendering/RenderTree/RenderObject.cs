using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using System;
using SkiaSharp;
using System.Collections.Generic;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// The base class for every node in the Render Tree.
    /// This separates the "Data" (Style/DOM) from the "UI" (XAML).
    /// </summary>
    public abstract class RenderObject
    {
        // Tree Structure
        public RenderObject Parent { get; set; }
        public List<RenderObject> Children { get; } = new List<RenderObject>();

        // Data Source
        public Element Node { get; set; }
        public CssComputed Style { get; set; }

        // Layout Results (Calculated by Layout Engine)
        // X, Y are relative to the Page (Absolute Positioning)
        public SKRect Bounds { get; set; }
        
        // Box Model (Calculated)
        public Thickness Margin { get; set; }
        public Thickness Padding { get; set; }
        public Thickness Border { get; set; }
        
        // Computed Box Rects for HitTesting
        public SKRect BorderBox => Bounds;
        public SKRect MarginBox => new SKRect(
            Bounds.Left - (float)Margin.Left,
            Bounds.Top - (float)Margin.Top,
            Bounds.Right + (float)Margin.Right,
            Bounds.Bottom + (float)Margin.Bottom);

        public void AddChild(RenderObject child)
        {
            child.Parent = this;
            Children.Add(child);
        }

        /// <summary>
        /// Calculates the size and position of this object and its children.
        /// </summary>
        /// <param name="availableSize">The space given by the parent.</param>
        public abstract void Layout(SKSize availableSize);
    }
}



