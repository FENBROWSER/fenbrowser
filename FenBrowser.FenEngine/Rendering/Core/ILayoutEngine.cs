using FenBrowser.Core;
using SkiaSharp;
using System.Collections.Generic;

using FenBrowser.FenEngine.Layout;

namespace FenBrowser.FenEngine.Rendering.Core
{
    public interface ILayoutEngine
    {
        void PerformLayout(LayoutContext context, FenBrowser.Core.Deadlines.FrameDeadline deadline);

        /// <summary>
        /// Returns the bounding rect for the given element in viewport coordinates,
        /// or null if the element has no layout box.
        /// </summary>
        SkiaSharp.SKRect? GetBoxForNode(FenBrowser.Core.Dom.V2.Element element) => null;
    }
}
