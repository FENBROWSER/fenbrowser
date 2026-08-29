using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Parsing;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Core;
using FenBrowser.FenEngine.Scripting;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Layout;

/// <summary>
/// The live reCAPTCHA anchor frame renders its checkbox as a position:absolute
/// element with auto insets inside a position:relative inline-block span. The
/// span's subtree is repeatedly reset and shift-placed by inline probe passes,
/// which used to destroy the resolved offset and paint the checkbox at the
/// iframe's top-left corner (overlapping the widget border).
/// </summary>
public sealed class IframeAbsoluteStaticPositionTests
{
    // Mirrors the live anchor structure: table/table-cell wrappers around a
    // relative inline-block span containing the abspos checkbox border.
    private const string FrameHtml = """
        <!doctype html>
        <html>
        <head><style>
        html,body{margin:0}
        .rc-anchor{width:300px;height:74px;background:#f9f9f9;border:1px solid #d3d3d3}
        .rc-anchor-content{display:inline-block;position:relative;width:206px;height:74px}
        .rc-inline-block{display:inline-block;height:100%}
        .rc-anchor-center-container{display:table;height:100%}
        .rc-anchor-center-item{display:table-cell;vertical-align:middle}
        .rc-anchor-checkbox{margin:0 12px 2px 12px}
        .goog-inline-block{position:relative;display:inline-block}
        .recaptcha-checkbox{border:none;font-size:1px;height:28px;margin:4px;width:28px;overflow:visible;vertical-align:text-bottom}
        .recaptcha-checkbox-border{background-color:#fff;border:2px solid #444746;font-size:1px;height:24px;position:absolute;width:24px;z-index:1}
        </style></head>
        <body>
        <div class="rc-anchor">
          <div class="rc-anchor-content">
            <div class="rc-inline-block">
              <div class="rc-anchor-center-container">
                <div class="rc-anchor-center-item rc-anchor-checkbox">
                  <span class="recaptcha-checkbox goog-inline-block"><div class="recaptcha-checkbox-border"></div></span>
                </div>
              </div>
            </div>
          </div>
        </div>
        </body>
        </html>
        """;

    private const string ParentHtml = """
        <!doctype html>
        <html><body style="margin:0">
        <div style="width:304px;height:78px"><iframe id="challenge" width="304" height="78"></iframe></div>
        </body></html>
        """;

    [Fact]
    public async Task AbsposAutoOffsets_InRelativeInlineBlock_Iframe_SubdocumentUseStaticPosition()
    {
        var (bitmap, borderRect, frameRect) = await RenderReproAsync();
        using (bitmap)
        {
            // The abspos checkbox border must sit inside the frame at its
            // static position — not collapsed onto the frame origin, and not
            // sticking out over the widget border (negative offsets).
            Assert.True(borderRect.Left >= frameRect.Left + 1f,
                $"abspos border left {borderRect.Left} collapsed to/past the frame origin {frameRect.Left}");
            Assert.True(borderRect.Top >= frameRect.Top,
                $"abspos border top {borderRect.Top} stuck out above the frame origin {frameRect.Top}");
            Assert.True(borderRect.Right <= frameRect.Right && borderRect.Bottom <= frameRect.Bottom,
                $"abspos border {borderRect} escapes the frame {frameRect}");

            // Paint-level verification: the 2px #444746 checkbox outline must
            // be painted at the resolved position, not at the frame corner.
            var darkPixels = FindPixels(bitmap, new SKColor(68, 71, 70), tolerance: 24);
            Assert.NotEmpty(darkPixels);
            var minX = darkPixels.Min(p => p.X);
            var minY = darkPixels.Min(p => p.Y);
            Assert.True(minX >= borderRect.Left - 2f,
                $"checkbox outline painted at x={minX}, expected near {borderRect.Left}");
            Assert.True(minY >= borderRect.Top - 2f,
                $"checkbox outline painted at y={minY}, expected near {borderRect.Top}");
        }
    }

    private static async Task<(SKBitmap bitmap, SKRect borderRect, SKRect frameRect)> RenderReproAsync()
    {
        var parentUri = new Uri("https://parent.example.test/search");
        using var host = new BrowserHost();
        var renderer = new SkiaDomRenderer();
        host.EnableJavaScript = true;
        host.SetActiveRenderer(renderer);
        host.Engine.SetExternalRenderer(renderer);

        await host.Engine.RenderAsync(
            ParentHtml,
            parentUri,
            _ => Task.FromResult(string.Empty),
            _ => Task.FromResult<Stream>(null),
            _ => { },
            640,
            480,
            forceJavascript: true);

        var root = Assert.IsType<Element>(host.Engine.GetActiveDom());
        var iframe = Assert.IsType<Element>(root.OwnerDocument.GetElementById("challenge"));
        while (iframe.FirstChild != null)
        {
            iframe.RemoveChild(iframe.FirstChild);
        }

        var frameUri = new Uri("https://www.google.com/recaptcha/enterprise/anchor");
        var frameDocument = new HtmlParser(FrameHtml, frameUri).Parse();
        iframe.AppendChild(frameDocument);
        var scriptEngine = Assert.IsType<FenJsBrowserScriptEngine>(host.Engine.ScriptEngine);
        await scriptEngine.SetSubdocumentDomAsync(frameDocument.DocumentElement, frameUri);
        await host.Engine.RecascadeAsync();
        await host.FlushPendingLayoutAsync();

        var bitmap = new SKBitmap(640, 480);
        using var canvas = new SKCanvas(bitmap);
        renderer.RenderFrame(new RenderFrameRequest
        {
            Root = root,
            Canvas = canvas,
            Styles = host.ComputedStyles,
            Viewport = new SKRect(0, 0, 640, 480),
            BaseUrl = "https://parent.example.test/search",
            InvalidationReason = RenderFrameInvalidationReason.Navigation,
            RequestedBy = nameof(IframeAbsoluteStaticPositionTests),
            EmitVerificationReport = false
        });
        canvas.Flush();

        var border = Assert.IsType<Element>(frameDocument.QuerySelector(".recaptcha-checkbox-border"));
        var holder = Assert.IsType<Element>(frameDocument.QuerySelector(".rc-anchor-center-item"));
        Assert.Equal("middle", holder.GetComputedStyle()?.VerticalAlign);
        var borderBox = renderer.GetElementBox(border);
        Assert.NotNull(borderBox);
        Assert.NotNull(renderer.GetElementBox(iframe));
        var holderBox = renderer.GetElementBox(holder);
        Assert.NotNull(holderBox);
        float expectedTop = holderBox.ContentBox.Top + (holderBox.ContentBox.Height - borderBox.BorderBox.Height) / 2f;
        Assert.Equal(expectedTop, borderBox.BorderBox.Top, 1f);
        return (bitmap, borderBox.BorderBox, renderer.GetElementBox(iframe).BorderBox);
    }

    private static List<SKPointI> FindPixels(SKBitmap bitmap, SKColor target, byte tolerance)
    {
        var hits = new List<SKPointI>();
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.Red - target.Red) <= tolerance &&
                    Math.Abs(pixel.Green - target.Green) <= tolerance &&
                    Math.Abs(pixel.Blue - target.Blue) <= tolerance)
                {
                    hits.Add(new SKPointI(x, y));
                }
            }
        }

        return hits;
    }
}
