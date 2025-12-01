using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Collections.Generic;

namespace FenBrowser.FenEngine.Rendering
{
    public static class LayoutEngine
    {
        public static void PerformLayout(RenderObject root, Size viewportSize)
        {
            if (root == null) return;

            // 1. Reset
            // (In future: invalidate tree)

            // 2. Layout Pass
            // We pass the viewport size as the initial constraint
            root.Layout(viewportSize);
        }
    }
}
