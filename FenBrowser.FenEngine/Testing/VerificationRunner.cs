using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using SkiaSharp;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Css;
using FenBrowser.Core;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.FenEngine.Testing
{
    public static class VerificationRunner
    {
        public static async Task GenerateSnapshot(string htmlPath, string outputPath)
        {
            Console.WriteLine($"[Verify] Loading {htmlPath}...");
            string html = await File.ReadAllTextAsync(htmlPath);

            // 0. Setup Viewport
            float viewportW = 800;
            float viewportH = 600;

            // 1. Parse HTML
            var tokenizer = new FenBrowser.FenEngine.HTML.HtmlTokenizer(html);
            var builder = new FenBrowser.FenEngine.HTML.HtmlTreeBuilder(tokenizer);
            var doc = builder.Build();

            // 2. Compute Style
            Console.WriteLine("[Verify] Computing styles...");
            var baseUri = new Uri("file://" + htmlPath.Replace("\\", "/"));
            var cssResult = await CssLoader.ComputeWithResultAsync(doc.DocumentElement, baseUri, null, viewportW, viewportH);
            var styles = cssResult.Computed;

            // 3. Compute Layout
            Console.WriteLine("[Verify] Computing layout...");
            var layoutEngine = new LayoutEngine(styles, viewportW, viewportH);
            var layoutRoot = layoutEngine.ComputeLayout(doc, 0, 0, viewportW, false, viewportH, false);

            // 4. Paint
            Console.WriteLine("[Verify] Painting...");
            
            // Build Paint Tree
            // NewPaintTreeBuilder.Build expects IReadOnlyDictionary<Node, BoxModel>
            // layoutEngine.AllBoxes returns IEnumerable<KeyValuePair<...>>
            var boxes = layoutEngine.AllBoxes.ToDictionary(k => k.Key, v => v.Value);
            
            var paintTree = NewPaintTreeBuilder.Build(doc, boxes, styles, viewportW, viewportH, null);

            // Render
            using var surface = SKSurface.Create(new SKImageInfo((int)viewportW, (int)viewportH));
            var canvas = surface.Canvas;
            
            var paintRenderer = new SkiaRenderer();
            paintRenderer.Render(canvas, paintTree, new SKRect(0, 0, viewportW, viewportH));

            // 5. Save
            Console.WriteLine($"[Verify] Saving to {outputPath}...");
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.OpenWrite(outputPath);
            data.SaveTo(stream);
            Console.WriteLine("[Verify] Done.");
        }
    }
}

