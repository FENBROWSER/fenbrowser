using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Represents an area that inline content should be excluded from (avoid).
    /// Used for Floats and CSS Shapes.
    /// </summary>
    public class FloatExclusion
    {
        public SKRect FloatingRect { get; set; }
        public bool IsLeft { get; set; } // True = Float Left, False = Float Right
        
        // Shape Definition
        public enum ShapeType { Box, Circle }
        public ShapeType Type { get; set; } = ShapeType.Box;
        
        // Circle Params (Relative to FloatingRect)
        public SKPoint CircleCenter { get; set; }
        public float CircleRadius { get; set; }

        public FloatExclusion(SKRect rect, bool isLeft)
        {
            FloatingRect = rect;
            IsLeft = isLeft;
        }

        public static FloatExclusion CreateCircle(SKRect rect, bool isLeft, float cx, float cy, float r)
        {
            return new FloatExclusion(rect, isLeft)
            {
                Type = ShapeType.Circle,
                CircleCenter = new SKPoint(cx, cy),
                CircleRadius = r
            };
        }

        /// <summary>
        /// Calculates the occupied width range at a specific Y vertical range.
        /// Returns null if this exclusion does not intersect the Y range.
        /// </summary>
        public (float Left, float Right)? GetOccupiedRange(float y, float height)
        {
            // 1. Box Check (Optimization & Fallback)
            // If we are completely outside the reference box, no exclusion.
            if (y >= FloatingRect.Bottom || (y + height) <= FloatingRect.Top)
                return null;

            if (Type == ShapeType.Box)
            {
                return (FloatingRect.Left, FloatingRect.Right);
            }
            else if (Type == ShapeType.Circle)
            {
                // Circle Intersection Logic
                // We need to find the chord width at the given Y range.
                // Conservative approach: use the widest part of the circle within [y, y+height].
                
                // Effective Y relative to center
                // We want the range of X values where the circle exists for any Y inside [y, y+height].
                // Which Y in the range is closest to the center (gives max width)?
                
                float circleTop = CircleCenter.Y - CircleRadius;
                float circleBottom = CircleCenter.Y + CircleRadius;
                
                // Overlap of band [y, y+h] and circle Vertical Range
                float overlapTop = Math.Max(y, circleTop);
                float overlapBottom = Math.Min(y + height, circleBottom);
                
                if (overlapTop >= overlapBottom) return null; // No overlap with circle body
                
                // Find Y closest to center to maximize width (most conservative exclusion)
                float closestY = CircleCenter.Y;
                if (overlapBottom < CircleCenter.Y) closestY = overlapBottom;
                else if (overlapTop > CircleCenter.Y) closestY = overlapTop;
                
                // Calculate half-chord width at closestY
                float dy = Math.Abs(closestY - CircleCenter.Y);
                if (dy >= CircleRadius) return null; // Should be covered by overlap check, but safety
                
                float dx = (float)Math.Sqrt(CircleRadius * CircleRadius - dy * dy);
                
                float left = CircleCenter.X - dx;
                float right = CircleCenter.X + dx;
                
                // Clamp to Float Box (shape-outside is clipped to margin box usually)
                left = Math.Max(left, FloatingRect.Left);
                right = Math.Min(right, FloatingRect.Right);
                
                if (left >= right) return null;
                
                return (left, right);
            }
            
            return (FloatingRect.Left, FloatingRect.Right);
        }
        public static FloatExclusion CreateFromStyle(SKRect rect, bool isLeft, FenBrowser.Core.Css.CssComputed style)
        {
            // Default to Box
            if (string.IsNullOrEmpty(style?.ShapeOutside) || style.ShapeOutside.ToLowerInvariant() == "none")
            {
                return new FloatExclusion(rect, isLeft);
            }

            string shape = style.ShapeOutside.Trim().ToLowerInvariant();

            // Support: circle(radius), circle(radius at x y)
            if (shape.StartsWith("circle"))
            {
                try
                {
                    // Parse content inside parens
                    int start = shape.IndexOf('(');
                    int end = shape.LastIndexOf(')');
                    if (start > -1 && end > start)
                    {
                         string content = shape.Substring(start + 1, end - start - 1).Trim();
                         
                         // Defaults
                         float cx = rect.MidX;
                         float cy = rect.MidY;
                         float r = Math.Min(rect.Width, rect.Height) / 2.0f; // Default closest-side

                         if (!string.IsNullOrEmpty(content))
                         {
                             // Split by "at"
                             var parts = content.Split(new[] { "at" }, StringSplitOptions.RemoveEmptyEntries);
                             string radiusPart = parts[0].Trim();
                             
                             // Parse Radius
                             if (!string.IsNullOrEmpty(radiusPart) && radiusPart != "closest-side")
                             {
                                 if (radiusPart.EndsWith("%") && float.TryParse(radiusPart.TrimEnd('%'), out float pct))
                                 {
                                     // Reference size for radius is usually sqrt(w*w + h*h)/sqrt(2)
                                     float refSize = (float)Math.Sqrt(rect.Width*rect.Width + rect.Height*rect.Height) / 1.414f;
                                     r = refSize * (pct / 100f);
                                 }
                                 else if (radiusPart.EndsWith("px") && float.TryParse(radiusPart.TrimEnd('p','x'), out float px))
                                 {
                                     r = px;
                                 }
                                 else if (float.TryParse(radiusPart, out float val))
                                 {
                                     r = val;
                                 }
                             }

                             // Parse Center
                             if (parts.Length > 1)
                             {
                                 string centerPart = parts[1].Trim();
                                 var coords = centerPart.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                                 if (coords.Length >= 1) cx = ParseLength(coords[0], rect.Left, rect.Width);
                                 if (coords.Length >= 2) cy = ParseLength(coords[1], rect.Top, rect.Height);
                             }
                         }

                         return CreateCircle(rect, isLeft, cx, cy, r);
                    }
                }
                catch 
                {
                    // Fallback to box on parse error
                }
            }

            return new FloatExclusion(rect, isLeft);
        }
        
        private static float ParseLength(string val, float start, float size)
        {
            if (val.EndsWith("%") && float.TryParse(val.TrimEnd('%'), out float pct))
            {
                return start + size * (pct / 100f);
            }
            if (val.EndsWith("px") && float.TryParse(val.TrimEnd('p','x'), out float px))
            {
                // Absolute coordinate? Assuming relative to box origin if just px?
                // Spec says position center. Center is relative to the box.
                // So "10px" means 10px from Left.
                return start + px;
            }
            if (val == "center") return start + size / 2;
            if (val == "left" || val == "top") return start;
            if (val == "right" || val == "bottom") return start + size;
            
            return start + size / 2; // Fallback
        }
    }
}
