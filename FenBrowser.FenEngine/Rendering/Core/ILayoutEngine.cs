using FenBrowser.Core;
using SkiaSharp;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering.Core
{
    public interface ILayoutEngine
    {
        RenderContext Context { get; }
    }
}
