using System.Collections.Generic;
using System.Diagnostics;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// First-party SVG rendering pipeline: parsed tree -> styled draw -> SKPicture.
    ///
    /// Stage order follows engine convention:
    /// 1. source/DOM (sandboxed parser)
    /// 2. geometry (viewport, viewBox, transforms, shapes, paths)
    /// 3. paint (fills, strokes, gradients, group opacity)
    /// 4. raster preparation happens in <see cref="Adapters.FenSvgRenderer"/>.
    ///
    /// Threading: one engine instance PER RENDER CALL. Nothing static is mutated,
    /// so concurrent renders are safe.
    /// </summary>
    internal sealed partial class SvgRenderEngine
    {
        private readonly SvgParsedDocument _doc;
        private readonly SvgParseReport _report;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly long _deadlineMs;
        private int _elementsVisited;

        /// <summary>Independent render-side recursion guard (S2/F2): spans the
        /// element tree AND use/symbol expansion, which multiply parser depth.</summary>
        private int _depth;

        /// <summary>Live SaveLayer count (S1/F1): each layer allocates a
        /// full-viewport surface; unbounded nesting is a memory bomb that the
        /// raster caps cannot see.</summary>
        private int _activeLayers;

        /// <summary>Per-render paint-server memoization (P0.3): stops arrays and
        /// userSpace shaders are rebuilt once per (server, currentColor) instead
        /// of once per referencing shape. Lifetime == this engine instance.</summary>
        internal Dictionary<SvgElement, CachedGradient> ShaderCache =
            new Dictionary<SvgElement, CachedGradient>();

        private const int TimeCheckMask = 0x3F; // check every 64 elements
        private const float DefaultFontSize = 16f;
        private const int MaxRenderDepth = 256;   // independent of parser budget
        private const int MaxActiveLayers = 8;    // full-viewport surface cap

        private SvgRenderEngine(SvgParsedDocument doc, SvgRenderLimits limits)
        {
            _doc = doc;
            _report = doc.Report;
            _deadlineMs = limits.MaxRenderTimeMs > 0 ? limits.MaxRenderTimeMs : long.MaxValue;
        }

        public static bool TryRender(
            string source,
            SvgRenderLimits limits,
            out SKPicture picture,
            out float width,
            out float height,
            out string error, out int warningCount)
        {
            picture = null;
            width = 0f;
            height = 0f;
            error = null;
            warningCount = 0;

            if (!SvgMarkupParser.TryParse(source, limits, out var doc, out var fatalReason))
            {
                error = fatalReason;
                return false;
            }

            var engine = new SvgRenderEngine(doc, limits);
            engine.ConfigureImageBudgets(limits.MaxDecodedImagePixels, limits.MaxRasterWidth);
            try
            {
                engine.RenderRoot(out picture, out width, out height);
                warningCount = engine._report.Warnings.Count;
                return true;
            }
            catch (SvgTimeBudgetExceededException)
            {
                picture = null;
                error = $"SVG render exceeded time limit ({limits.MaxRenderTimeMs}ms, size={source.Length / 1024}KB)";
                return false;
            }
            catch (SvgSandboxViolationException ex)
            {
                // Budget violations surface with their bare parity message, not
                // the generic "SVG render error:" prefix (F10).
                picture = null;
                error = ex.Message;
                return false;
            }
        }

        private void CheckTime()
        {
            if ((_elementsVisited & TimeCheckMask) == 0 && _clock.ElapsedMilliseconds > _deadlineMs)
            {
                throw new SvgTimeBudgetExceededException();
            }
        }

        // -------------------------------------------------------------- viewport

        private void RenderRoot(out SKPicture picture, out float width, out float height)
        {
            var root = _doc.Root;

            width = ResolveViewportLength(root.GetAttribute("width"), 0f);
            height = ResolveViewportLength(root.GetAttribute("height"), 0f);

            bool hasViewBox = TryParseViewBox(
                root.GetAttribute("viewBox"),
                out float vbX, out float vbY, out float vbW, out float vbH);

            // Intrinsic sizing: explicit px-ish lengths win; otherwise derive from
            // viewBox (legacy adapter parity); otherwise CSS replaced-element
            // default 300x150 keeps output deterministic.
            bool sizeComplete = width > 0f && height > 0f;
            if (!sizeComplete && hasViewBox)
            {
                if (width <= 0f) width = vbW;
                if (height <= 0f) height = vbH;
            }
            if (width <= 0f || !SvgValues.IsFinite(width)) width = 300f;
            if (height <= 0f || !SvgValues.IsFinite(height)) height = 150f;

            // Absolute upper bound independent of caller limits: a viewport this
            // large can never pass raster admission below anyway.
            width = System.Math.Min(width, 32767f);
            height = System.Math.Min(height, 32767f);

            using var recorder = new SKPictureRecorder();
            var canvas = recorder.BeginRecording(new SKRect(0f, 0f, width, height));

            var viewport = new ViewportContext(width, height);
            var inherited = new InheritedStyle();

            // The root <svg> participates in inheritance (e.g. fill="blue" on the
            // root cascades to children) - legacy adapter behavior preserved.
            var rootStyle = inherited.ResolveOverrides(root, _report);

            using (new CanvasState(canvas))
            {
                // Clip FIRST, under the identity CTM, so the clip is the raw
                // device viewport. Clipping after the viewBox transform would
                // transform the clip rectangle too (e.g. a large-negative
                // vbY would push the clip entirely off-content).
                canvas.ClipRect(new SKRect(0f, 0f, width, height));
                ApplyViewportTransform(canvas, viewport, hasViewBox, vbX, vbY, vbW, vbH, root.GetAttribute("preserveAspectRatio"));
                if (!rootStyle.Visibility)
                {
                    picture = recorder.EndRecording();
                    return;
                }
                DrawChildren(root, canvas, viewport, rootStyle);
            }

            picture = recorder.EndRecording();
        }

    }

    internal sealed class SvgTimeBudgetExceededException : System.Exception
    {
    }
}
