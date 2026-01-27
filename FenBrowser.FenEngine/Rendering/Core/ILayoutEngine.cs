using FenBrowser.Core;
using SkiaSharp;
using System.Collections.Generic;

using FenBrowser.FenEngine.Layout;

namespace FenBrowser.FenEngine.Rendering.Core
{
    public interface ILayoutEngine
    {
        void PerformLayout(LayoutContext context, FenBrowser.Core.Deadlines.FrameDeadline deadline);
    }
}
