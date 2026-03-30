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
    [Flags]
    public enum RenderFrameInvalidationReason
    {
        None = 0,
        Unknown = 1 << 0,
        Navigation = 1 << 1,
        Dom = 1 << 2,
        Style = 1 << 3,
        Layout = 1 << 4,
        Paint = 1 << 5,
        Viewport = 1 << 6,
        Scroll = 1 << 7,
        Animation = 1 << 8,
        Input = 1 << 9,
        Overlay = 1 << 10,
        Timer = 1 << 11,
        Diagnostics = 1 << 12,
        ProcessIsolation = 1 << 13,
        HostRequest = 1 << 14
    }

    public enum RenderFrameRasterMode
    {
        None = 0,
        PreservedBaseFrame = 1,
        Damage = 2,
        Full = 3
    }

    public sealed class RenderFrameTelemetry
    {
        public long FrameSequence { get; init; }

        public string Url { get; init; }

        public string RequestedBy { get; init; }

        public RenderFrameInvalidationReason InvalidationReason { get; init; }

        public RenderFrameRasterMode RasterMode { get; init; }

        public int DomNodeCount { get; init; }

        public int BoxCount { get; init; }

        public int PaintNodeCount { get; init; }

        public int OverlayCount { get; init; }

        public int DamageRegionCount { get; init; }

        public bool LayoutUpdated { get; init; }

        public bool PaintTreeRebuilt { get; init; }

        public bool BaseFrameSeeded { get; init; }

        public bool WatchdogTriggered { get; init; }

        public string WatchdogReason { get; init; }

        public double LayoutDurationMs { get; init; }

        public double PaintDurationMs { get; init; }

        public double RasterDurationMs { get; init; }

        public double TotalDurationMs { get; init; }

        public float DamageAreaRatio { get; init; }
    }

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

        public RenderFrameInvalidationReason InvalidationReason { get; set; } = RenderFrameInvalidationReason.Unknown;

        public string RequestedBy { get; set; } = "unspecified";

        public bool EmitVerificationReport { get; set; } = true;
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

        public RenderFrameInvalidationReason InvalidationReason { get; init; }

        public string RequestedBy { get; init; }

        public RenderFrameRasterMode RasterMode { get; init; }

        public RenderFrameTelemetry Telemetry { get; init; }
    }
}
