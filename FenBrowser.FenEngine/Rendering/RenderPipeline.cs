using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Threading.Tasks;
using FenBrowser.Core;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Orchestrates the full rendering pipeline: Parsing -> Style -> Layout -> Painting.
    /// </summary>
    public static class RenderPipeline
    {
        public static async Task<Control> RenderHtmlAsync(string html, Uri baseUri, Size viewportSize)
        {
            // 1. Parse HTML
            var parser = new HtmlLiteParser(html);
            var domRoot = parser.Parse();

            // 2. Load CSS
            // For now, we ignore external CSS fetching to keep it simple.
            var styles = await CssLoader.ComputeAsync(domRoot, baseUri, async (url) => 
            {
                // Placeholder: In a real app, fetch content from 'url'
                return await Task.FromResult(""); 
            });

            // 3. Build Render Tree
            var renderRoot = RenderTreeBuilder.Build(domRoot, styles);

            // 4. Layout
            LayoutEngine.PerformLayout(renderRoot, viewportSize);

            // 5. Paint
            var visualRoot = Painter.Paint(renderRoot);

            return visualRoot;
        }
    }
}
