// SpecRef: CSS2.1 Appendix E (painting order), CSS Compositing and Blending Level 1
// CapabilityId: PAINT-DAMAGE-TELEMETRY-01
// Determinism: strict — deterministic trace logging; same inputs → same log shape
// FallbackPolicy: log-only, never affects rasterization correctness

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.Core;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering.Core;
using SkiaSharp;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Immutable snapshot of per-frame damage/invalidation diagnostics.
    /// Captures what changed, why, and the spatial extent — for post-mortem
    /// analysis in the field without reproducing the user scenario.
    ///
    /// Design constraints (inherited from Chromium's cc::LayerTreeHost):
    ///   - Zero-alloc on the hot path: all data captured as struct fields / scoped arrays.
    ///   - No references to DOM, PaintTree, or mutable engine state.
    ///   - Safe to queue and drain asynchronously from a telemetry thread.
    /// </summary>
    public readonly record struct DamageFrameTelemetry
    {
        /// <summary>UTC timestamp when the frame telemetry was captured.</summary>
        public DateTimeOffset Timestamp { get; init; }

        /// <summary>Frame sequence number (monotonic, host-relative).</summary>
        public ulong FrameNumber { get; init; }

        /// <summary>Viewport dimensions for this frame.</summary>
        public SKRect Viewport { get; init; }

        /// <summary>Why the frame was produced (layout, style, scroll, animation, etc.)</summary>
        public RenderFrameInvalidationReason InvalidationReason { get; init; }

        /// <summary>Whether damage rasterization was used for this frame.</summary>
        public bool UsedDamageRasterization { get; init; }

        /// <summary>Ratio of damaged area to viewport area (0.0 – 1.0).</summary>
        public float DamageAreaRatio { get; init; }

        /// <summary>Number of raw damage regions before normalization.</summary>
        public int RawDamageRegionCount { get; init; }

        /// <summary>Number of normalized damage regions after merging/clamping.</summary>
        public int NormalizedDamageRegionCount { get; init; }

        /// <summary>Whether scroll damage contributed to the frame.</summary>
        public bool ScrollDamageContributed { get; init; }

        /// <summary>Whether tree-diff damage (paint-tree delta) contributed.</summary>
        public bool TreeDiffDamageContributed { get; init; }

        /// <summary>Whether the paint tree was rebuilt this frame.</summary>
        public bool PaintTreeRebuilt { get; init; }

        /// <summary>Whether the layout was recomputed this frame.</summary>
        public bool LayoutRecomputed { get; init; }

        /// <summary>Stability controller forced-repaint state at the time of capture.</summary>
        public bool ForcedPaintRebuildActive { get; init; }

        /// <summary>EMA-based frame-budget suppression active at the time of capture.</summary>
        public bool BudgetSuppressionActive { get; init; }

        /// <summary>Explicit full-repaint override (e.g. interaction state change).</summary>
        public bool FullRepaintOverride { get; init; }

        /// <summary>
        /// Returns a compact single-line description suitable for structured logging.
        /// Format is column-oriented for easy grep/awk/ ingestion.
        /// </summary>
        public string ToTraceLine()
        {
            var sb = new StringBuilder(256);
            sb.Append("[DAMAGE-TRACE] ");
            sb.Append("frame=").Append(FrameNumber).Append(' ');
            sb.Append("ts=").Append(Timestamp.ToUnixTimeMilliseconds()).Append(' ');
            sb.Append("viewport=").Append(Viewport.Width).Append('x').Append(Viewport.Height).Append(' ');
            sb.Append("reason=").Append(InvalidationReason).Append(' ');
            sb.Append("damage_used=").Append(UsedDamageRasterization).Append(' ');
            sb.Append("damage_ratio=").Append(DamageAreaRatio.ToString("F4")).Append(' ');
            sb.Append("raw_regions=").Append(RawDamageRegionCount).Append(' ');
            sb.Append("norm_regions=").Append(NormalizedDamageRegionCount).Append(' ');
            sb.Append("scroll_damage=").Append(ScrollDamageContributed).Append(' ');
            sb.Append("tree_diff=").Append(TreeDiffDamageContributed).Append(' ');
            sb.Append("rebuilt=").Append(PaintTreeRebuilt).Append(' ');
            sb.Append("layout=").Append(LayoutRecomputed).Append(' ');
            sb.Append("forced=").Append(ForcedPaintRebuildActive).Append(' ');
            sb.Append("suppressed=").Append(BudgetSuppressionActive).Append(' ');
            sb.Append("full_override=").Append(FullRepaintOverride);
            return sb.ToString();
        }

        /// <summary>
        /// Emits a complete telemetry record to the logging system at the appropriate level.
        /// </summary>
        public void EmitToLog()
        {
            // Always emit at Trace level so field analysis can aggregate.
            // Emit at Debug level when damage rasterization is active (unusual/interesting state).
            var line = ToTraceLine();
            if (UsedDamageRasterization || ForcedPaintRebuildActive)
            {
                EngineLogCompat.Debug(line, LogCategory.Paint);
            }
            else
            {
                EngineLogCompat.Debug(line, LogCategory.Paint);
            }
        }
    }
}
