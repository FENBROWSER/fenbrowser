using System;
using System.Collections.Generic;
using FenBrowser.Core.Css;
using FenBrowser.Core.Dom.V2;
using FenBrowser.FenEngine.Layout;
using FenBrowser.FenEngine.Rendering;
using FenBrowser.FenEngine.Rendering.Interaction;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering.Core
{
    /// <summary>
    /// Finalized render-frame contract: DOM/styles enter here, layout/paint/damage artifacts come out.
    /// </summary>
    public interface IRenderFramePipeline
    {
        RenderFrameResult RenderFrame(RenderFrameRequest request);

        BoxModel GetElementBox(Node node);

        ScrollManager ScrollManager { get; }

        List<InputOverlayData> CurrentOverlays { get; }

        IReadOnlyList<SKRect> LastDamageRegions { get; }

        LayoutResult LastLayout { get; }

        RenderContext CreateRenderContext();
    }

    public sealed class RenderFrameRequest
    {
        public Node Root { get; set; }

        public SKCanvas Canvas { get; set; }

        public Dictionary<Node, CssComputed> Styles { get; set; } = new Dictionary<Node, CssComputed>();

        public SKRect Viewport { get; set; }

        public string BaseUrl { get; set; }

        public Action<SKSize, List<InputOverlayData>> OnLayoutUpdated { get; set; }

        public SKSize? SeparateLayoutViewport { get; set; }

        public bool HasBaseFrame { get; set; }
    }

    public sealed class RenderFrameResult
    {
        public LayoutResult Layout { get; init; }

        public ImmutablePaintTree PaintTree { get; init; }

        public IReadOnlyList<SKRect> DamageRegions { get; init; } = Array.Empty<SKRect>();

        public IReadOnlyList<InputOverlayData> Overlays { get; init; } = Array.Empty<InputOverlayData>();

        public bool WatchdogTriggered { get; init; }

        public string WatchdogReason { get; init; }

        public bool UsedDamageRasterization { get; init; }

        public float DamageAreaRatio { get; init; }
    }
}
