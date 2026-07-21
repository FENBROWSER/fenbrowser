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

    /// <summary>
    /// Phase 1: authoritative animation invalidation result. Combines the property-
    /// specific update kind with the corresponding DOM dirty flags. Composite-only
    /// properties carry no DOM invalidation — they must not mark elements PaintDirty.
    /// </summary>
    public readonly record struct AnimationInvalidationResult(
        AnimationUpdateKind UpdateKind,
        InvalidationKind DomInvalidation,
        IReadOnlyList<string> ChangedProperties);

    /// <summary>
    /// Phase 2: the execution path taken for a composite animation frame. Surfaces
    /// whether the element was promoted to a composited layer, fell back to localized
    /// paint, or required a full paint rebuild.
    /// </summary>
    public enum CompositeAnimationExecutionPath
    {
        None,
        CachedLayerComposite,
        PromotedDuringFrame,
        LocalizedPaintFallback,
        FullPaintFallback
    }

    /// <summary>
    /// Phase 10: the concrete decision the frame watchdog took for a late frame.
    /// Replaces a bare boolean so late-frame policy can be reasoned about and tested.
    /// </summary>
    public enum RenderFrameWatchdogAction
    {
        None = 0,
        PreservedPreviousFrame = 1,
        DroppedObsoleteAnimationFrame = 2,
        ScheduledFollowup = 3,
        ForcedFreshRaster = 4,
        AbortedInvalidFrame = 5
    }

    /// <summary>
    /// Phase 4: classifier describing why this frame did or did not rebuild the
    /// paint tree. Replaces the bare boolean: a frame may legitimately be presentable
    /// without rebuilding the paint tree when no DOM/layout/style change happened
    /// (compositor-only frames, repair tails, etc.). Telemetry surfaces the
    /// classifier so multi-tab and animation-heavy workloads can prove that
    /// hundreds of animation frames do not produce hundreds of paint-tree rebuilds.
    ///</summary>
    public enum PaintTreeRebuildReason
    {
        None = 0,
        FirstFrame = 1,
        Navigation = 2,
        RootChanged = 3,
        LayoutChanged = 4,
        StructuralPaintChange = 5,
        StyleChange = 6,
        PaintOnly = 7,
        ImageCacheChange = 8,
        StabilityForced = 9,
        DiagnosticForce = 10
    }

    public sealed class RenderFrameTelemetry
    {
        public long FrameSequence { get; init; }

        public string Url { get; init; }

        public string RequestedBy { get; init; }

        public RenderFrameInvalidationReason InvalidationReason { get; init; }

        public RenderFrameRasterMode RasterMode { get; init; }

        public int DomNodeCount { get; init; }

        public int ElementNodeCount { get; init; }

        public int TextNodeCount { get; init; }

        public int AttributeCount { get; init; }

        public int BoxCount { get; init; }

        public int PaintNodeCount { get; init; }

        public int OverlayCount { get; init; }

        public int DamageRegionCount { get; init; }

        public int CompositedLayerCount { get; init; }

        public int PromotedLayerCount { get; init; }

        public bool UsedIncrementalLayout { get; init; }

        public int IncrementalLayoutRootCount { get; init; }

        public bool LayoutUpdated { get; init; }

        public bool PaintTreeRebuilt { get; init; }

        /// <summary>
        /// Phase 4: classifies the cause of any paint-tree rebuild — when
        /// <see cref="PaintTreeRebuilt"/> is true, this tells callers WHY; when
        /// false, this tells callers that the prior paint tree was intentionally
        /// preserved (compositor-only update, scroll-driven re-layerization, etc.).
        ///</summary>
        public PaintTreeRebuildReason PaintTreeRebuildReason { get; init; }

        public bool BaseFrameSeeded { get; init; }

        public bool WatchdogTriggered { get; init; }

        public string WatchdogReason { get; init; }

        public RenderFrameWatchdogAction WatchdogAction { get; init; }

        public double LayoutDurationMs { get; init; }

        public double PaintDurationMs { get; init; }

        public double RasterDurationMs { get; init; }

        public long LayoutAllocatedBytes { get; init; }

        public long PaintAllocatedBytes { get; init; }

        public long RasterAllocatedBytes { get; init; }

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

        // Phase 9: verification (debug screenshot capture + content-verifier
        // registration) is OFF by default. Normal browsing frames must never
        // capture a screenshot; it is enabled only for explicit visual test /
        // screenshot / WPT / debug requests.
        public bool EmitVerificationReport { get; set; } = false;

        public bool CollectAllocationTelemetry { get; set; }
    }

    public sealed class RenderFrameResult
    {
        public LayoutResult Layout { get; init; }

        public ImmutablePaintTree PaintTree { get; init; }

        public IReadOnlyList<SKRect> DamageRegions { get; init; } = Array.Empty<SKRect>();

        public IReadOnlyList<InputOverlayData> Overlays { get; init; } = Array.Empty<InputOverlayData>();

        public bool WatchdogTriggered { get; init; }

        public string WatchdogReason { get; init; }

        public RenderFrameWatchdogAction WatchdogAction { get; init; }

        public bool UsedDamageRasterization { get; init; }

        public float DamageAreaRatio { get; init; }

        public RenderFrameInvalidationReason InvalidationReason { get; init; }

        public string RequestedBy { get; init; }

        public RenderFrameRasterMode RasterMode { get; init; }

        public RenderFrameTelemetry Telemetry { get; init; }
    }
}
