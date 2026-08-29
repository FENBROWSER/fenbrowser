// =============================================================================
// ContainingBlockResolver.cs
// CSS 2.1 Containing Block Determination
// 
// SPEC REFERENCE: CSS 2.1 §10.1 - Definition of "containing block"
//                 https://www.w3.org/TR/CSS21/visudet.html#containing-block-details
// 
// CONTAINING BLOCK RULES:
//   1. Root element: Initial containing block (viewport)
//   2. Static/relative: Nearest block container ancestor
//   3. Absolute: Nearest positioned ancestor (or ICB if none)
//   4. Fixed: Viewport (unless a transform/filter/perspective ancestor exists)
// 
// STATUS: Resolution lives in LayoutPositioningLogic (production). This file
//         keeps only the ContainingBlock geometry struct shared with
//         AbsolutePositionSolver.
// =============================================================================

using FenBrowser.Core.Dom.V2;
using SkiaSharp;

namespace FenBrowser.FenEngine.Layout
{
    /// <summary>
    /// Represents a containing block with its dimensions and position.
    /// </summary>
    public struct ContainingBlock
    {
        public Node Node;
        public float Width;
        public float Height;
        public float X;
        public float Y;
        public bool IsInitial;
        public SKRect PaddingBox { get; set; }

        public SKRect ContentBox => new SKRect(X, Y, X + Width, Y + Height);

        public override string ToString()
        {
            var name = IsInitial ? "ICB" : ((Node as Element)?.TagName ?? "Node");
            return $"CB[{name} {Width}x{Height} at ({X},{Y})]";
        }
    }

}
