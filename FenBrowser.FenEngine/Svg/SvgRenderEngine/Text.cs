using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FenBrowser.FenEngine.Typography;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        private const int MaxTextRenderCharsPerElement = 4096;
        private const int MaxTextGlyphsPerDocument = 16384;
        private const int MaxTextPositionListValues = 4096;
        private const int MaxTextDecorationTokens = 4;
        private const int MaxStyledSheetScanElements = 4096;
        private const int MaxStyledSheetsPerDocument = 256;
        private const int MaxStyledSheetChars = 512 * 1024;
        private int _documentTextGlyphCount;
        private int _documentStyleSheetsInspected;
        private int _documentStyleSheetCharsInspected;
        private bool? _documentStylesFirstLetter;
        private bool? _documentStylesByLanguage;

        private void DrawTextElement(SvgElement el, SKCanvas canvas, ViewportContext viewport, InheritedStyle inherited)
        {
            if (IsInsideExplicitlyRotatedMarker(el))
            {
                _report.RequireFallback(
                    "SVG text inside an explicitly rotated marker requires compatibility fallback");
                return;
            }
            var state = new TextLayoutState();
            var runs = new List<TextPaintRun>();
            var chunks = new List<TextChunk>();
            RequireFirstLetterSupport();
            RequireLanguageStyleSupport();
            LayoutTextElement(el, viewport, inherited, ResolveAncestorTextStyle(el), state, runs, chunks, true);
            ApplyTextLengthAdjustments(runs, state.LengthAdjustments);
            ApplyTextAnchors(runs, chunks);
            ApplyTextRotations(runs);
            if (runs.Count == 0) return;

            bool layered = TryBeginGroupOpacity(el, canvas, out var layerPaint);
            try
            {
                foreach (var run in runs)
                {
                    CheckDeadline();
                    PaintGlyphRun(canvas, run);
                }
            }
            finally
            {
                if (layered)
                {
                    canvas.Restore();
                    _activeLayers--;
                    layerPaint.Dispose();
                }
            }
        }

        private void LayoutTextElement(
            SvgElement element,
            ViewportContext viewport,
            InheritedStyle inheritedPaint,
            TextStyle inheritedText,
            TextLayoutState state,
            List<TextPaintRun> runs,
            List<TextChunk> chunks,
            bool isRoot)
        {
            CheckDeadline();
            if (IsDisplayNone(element)) return;
            if (!isRoot &&
                (!string.IsNullOrWhiteSpace(element.GetAttribute("transform")) ||
                 !string.IsNullOrWhiteSpace(element.GetPresentationProperty("clip-path"))))
            {
                _report.RequireFallback("transformed or clipped tspan requires compatibility fallback");
            }
            if ((element.TextContent?.Length ?? 0) > MaxTextRenderCharsPerElement)
            {
                _report.RequireFallback("SVG text exceeds first-party render length budget");
                return;
            }

            var paintStyle = inheritedPaint.ResolveOverrides(element, _report);
            if (!paintStyle.Visibility) return;
            var textStyle = ResolveTextStyle(element, inheritedText);
            RotationScope rotation = PushRotationScope(element, state);
            try
            {
                bool hasX = TryResolveTextLengthList(element.GetAttribute("x"), viewport.Width, textStyle.FontSize, out var xList);
                bool hasY = TryResolveTextLengthList(element.GetAttribute("y"), viewport.Height, textStyle.FontSize, out var yList);
                bool hasDx = TryResolveTextLengthList(element.GetAttribute("dx"), viewport.Width, textStyle.FontSize, out var dxList);
                bool hasDy = TryResolveTextLengthList(element.GetAttribute("dy"), viewport.Height, textStyle.FontSize, out var dyList);
                bool hasAbsolute = hasX || hasY;

                if (hasX) state.X = FirstPosition(xList, state.X);
                else if (hasDx) state.X += FirstPosition(dxList, 0f);
                if (hasY) state.Y = FirstPosition(yList, state.Y);
                else if (hasDy) state.Y += FirstPosition(dyList, 0f);

                if (isRoot || hasAbsolute || state.CurrentChunk < 0)
                {
                    state.CurrentChunk = chunks.Count;
                    chunks.Add(new TextChunk(state.X, textStyle.Anchor));
                }

                // A declared textLength governs the horizontal layout of the whole
                // element, so it takes precedence over a per-character x list; the
                // list still opens the chunk at its first value.
                TextLengthAdjustment? lengthAdjust = ResolveTextLengthDeclaration(element, textStyle, viewport);
                if (lengthAdjust.HasValue) xList = null;

                var positions = new TextPositionLists(xList, yList, dxList, dyList);
                int startRun = runs.Count;
                bool applyOwnOpacity = !isRoot;

                if (element.Content.Count == 0 && !string.IsNullOrEmpty(element.TextContent))
                {
                    ShapeTextPart(
                        element.TextContent, element, paintStyle, textStyle, viewport,
                        state, runs, applyOwnOpacity, positions);
                    RegisterTextLength(lengthAdjust, runs, startRun, state);
                    return;
                }

                foreach (var part in element.Content)
                {
                    if (part.IsText)
                    {
                        ShapeTextPart(
                            part.Text, element, paintStyle, textStyle, viewport,
                            state, runs, applyOwnOpacity, positions);
                    }
                    else if (part.Element?.Name == "tspan")
                    {
                        LayoutTextElement(part.Element, viewport, paintStyle, textStyle, state, runs, chunks, false);
                    }
                    else if (part.Element?.Name == "textPath")
                    {
                        LayoutTextPathElement(part.Element, viewport, paintStyle, textStyle, state, runs);
                    }
                    else if (part.Element?.Name == "a")
                    {
                        LayoutTextAnchorElement(part.Element, viewport, paintStyle, textStyle, state, runs, chunks);
                    }
                    else if (part.Element != null && !PaintableInTextContent(part.Element))
                    {
                        continue;
                    }
                    else if (part.Element != null)
                    {
                        _report.RequireFallback("unknown element in SVG text content requires compatibility fallback");
                    }
                }

                RegisterTextLength(lengthAdjust, runs, startRun, state);
            }
            finally
            {
                state.RotationList = rotation.List;
                state.RotationIndex = rotation.Index;
            }
        }

        private void LayoutTextAnchorElement(
            SvgElement element,
            ViewportContext viewport,
            InheritedStyle inheritedPaint,
            TextStyle inheritedText,
            TextLayoutState state,
            List<TextPaintRun> runs,
            List<TextChunk> chunks)
        {
            CheckDeadline();
            if (IsDisplayNone(element)) return;
            if (!string.IsNullOrWhiteSpace(element.GetAttribute("transform")) ||
                !string.IsNullOrWhiteSpace(element.GetPresentationProperty("clip-path")))
            {
                _report.RequireFallback("transformed or clipped tspan requires compatibility fallback");
            }
            if ((element.TextContent?.Length ?? 0) > MaxTextRenderCharsPerElement)
            {
                _report.RequireFallback("SVG text exceeds first-party render length budget");
                return;
            }
            var paintStyle = inheritedPaint.ResolveOverrides(element, _report);
            if (!paintStyle.Visibility) return;
            var textStyle = ResolveTextStyle(element, inheritedText);

            float previous = state.ContainerOpacity;
            state.ContainerOpacity = previous * ReadClampedOpacity(element, "opacity", 1f);
            RotationScope rotation = PushRotationScope(element, state);
            try
            {
                foreach (var part in element.Content)
                {
                    if (part.IsText)
                    {
                        ShapeTextPart(
                            part.Text, element, paintStyle, textStyle, viewport,
                            state, runs, applyOwnOpacity: false, TextPositionLists.None);
                    }
                    else if (part.Element?.Name == "tspan")
                    {
                        LayoutTextElement(part.Element, viewport, paintStyle, textStyle, state, runs, chunks, false);
                    }
                    else if (part.Element?.Name == "textPath")
                    {
                        LayoutTextPathElement(part.Element, viewport, paintStyle, textStyle, state, runs);
                    }
                    else if (part.Element?.Name == "a")
                    {
                        LayoutTextAnchorElement(part.Element, viewport, paintStyle, textStyle, state, runs, chunks);
                    }
                    else if (part.Element != null && PaintableInTextContent(part.Element))
                    {
                        _report.RequireFallback(
                            "unknown element in SVG text content requires compatibility fallback");
                    }
                }
            }
            finally
            {
                state.ContainerOpacity = previous;
                state.RotationList = rotation.List;
                state.RotationIndex = rotation.Index;
            }
        }

        /// <summary>
        /// True when an element nested in text content would paint something the
        /// text layout pass cannot honour. Definition, descriptive and other
        /// never-rendered containers are inert and are skipped; anything that
        /// produces geometry in the render tree is routed to the fallback instead
        /// of being silently dropped.
        /// </summary>
        private static bool PaintableInTextContent(SvgElement element) =>
            element.Name is "rect" or "circle" or "ellipse" or "line" or "polyline" or
                "polygon" or "path" or "image" or "use" or "text" or "switch" or
                "marker" or "foreignObject" or "video" or "audio" or "canvas" or "iframe";

        /// <summary>
        /// True when the run sits inside a <c>marker</c> that asks for an
        /// explicit rotation of its own content. The marker angle and the glyph
        /// angle are two independent rotations about two different points, and
        /// the single pass cannot establish that composition against an oracle,
        /// so such a run is reported rather than painted at a composed position
        /// the engine has not verified. A marker that follows the path tangent
        /// (<c>auto</c>, the default) lays its content out in the same
        /// orientation the shapes already use, so it stays admitted.
        /// </summary>
        private static bool IsInsideExplicitlyRotatedMarker(SvgElement element)
        {
            for (var ancestor = element?.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                if (ancestor.Name != "marker") continue;
                string orient = ancestor.GetAttribute("orient");
                if (string.IsNullOrWhiteSpace(orient)) return false;
                var keyword = orient.AsSpan().Trim();
                return !(IsCssWideKeyword(keyword) ||
                         keyword.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                         keyword.Equals("0", StringComparison.OrdinalIgnoreCase) ||
                         keyword.Equals("0deg", StringComparison.OrdinalIgnoreCase) ||
                         keyword.Equals("0turn", StringComparison.OrdinalIgnoreCase) ||
                         keyword.Equals("0rad", StringComparison.OrdinalIgnoreCase) ||
                         keyword.Equals("0grad", StringComparison.OrdinalIgnoreCase));
            }
            return false;
        }

        private float[] TryResolveRotationList(SvgElement element)
        {
            string raw = element.GetPresentationProperty("rotate");
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan().Trim());
            var resolved = new List<float>();
            while (tokenizer.Next(out var token))
            {
                if (resolved.Count >= MaxTextPositionListValues)
                {
                    _report.RequireFallback(
                        "per-glyph SVG text rotation list exceeds the first-party value budget");
                    return null;
                }
                if (IsCssWideKeyword(token) ||
                    token.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                    token.Equals("normal", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!SvgValues.TryParseNumber(token, out float degrees) || !SvgValues.IsFinite(degrees))
                {
                    _report.RequireFallback("per-glyph SVG text rotation requires compatibility fallback");
                    return null;
                }
                resolved.Add(SvgValues.ClampCoord(degrees));
            }
            return resolved.Count == 0 ? null : resolved.ToArray();
        }

        /// <summary>
        /// Binds the element's own <c>rotate</c> list to the character cursor for
        /// the duration of its subtree. A descendant that declares no list keeps
        /// the ancestor's list and its running index, so a nested element that
        /// contributes characters is addressed by the list declared above it, and
        /// the ancestor resumes at the index it had reached before descending.
        /// </summary>
        private RotationScope PushRotationScope(SvgElement element, TextLayoutState state)
        {
            var scope = new RotationScope(state.RotationList, state.RotationIndex);
            float[] declared = TryResolveRotationList(element);
            if (declared != null)
            {
                state.RotationList = declared;
                state.RotationIndex = 0;
            }
            return scope;
        }

        private readonly record struct RotationScope(float[] List, int Index);

        /// <summary>
        /// A <c>::first-letter</c> rule paints the first letter of the first
        /// formatted line, so unlike <c>::before</c> or <c>::after</c> it is not
        /// inert in a static render. The first-party cascade cannot match a
        /// pseudo-element, so a document that styles one would otherwise paint
        /// its whole run in the inherited fill and report success. The guard is
        /// read once per document: a run under a styled <c>::first-letter</c>
        /// cannot be painted faithfully, so the document is routed to the
        /// compatibility fallback instead of drawing the wrong pixels.
        /// </summary>
        private void RequireFirstLetterSupport()
        {
            if (DocumentStylesFirstLetter())
            {
                _report.RequireFallback(
                    "SVG first-letter pseudo-element styling requires compatibility fallback");
            }
        }

        private bool DocumentStylesFirstLetter()
        {
            if (_documentStylesFirstLetter.HasValue) return _documentStylesFirstLetter.Value;
            _documentStylesFirstLetter = false;
            if (_doc?.Root == null) return false;

            var styles = new Stack<SvgElement>();
            styles.Push(_doc.Root);
            int scanned = 0;
            while (styles.Count > 0 && scanned < MaxStyledSheetScanElements)
            {
                if ((scanned & 0x3F) == 0) CheckDeadline();
                scanned++;
                var element = styles.Pop();
                if (element.Name == "style" && MentionsFirstLetter(element.TextContent))
                {
                    _documentStylesFirstLetter = true;
                    return true;
                }
                for (int i = element.Children.Count - 1; i >= 0; i--)
                    styles.Push(element.Children[i]);
            }
            return false;
        }

        private bool MentionsFirstLetter(string sheet)
        {
            if (!TryAccountStyleSheet(sheet)) return false;

            int index = 0;
            while ((index = sheet.IndexOf("first-letter", index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                // A pseudo-element can only be spelled with a leading colon, so the
                // character before the name is what distinguishes a selector from
                // any other mention of the same text.
                for (int i = index - 1; i >= 0; i--)
                {
                    if (char.IsWhiteSpace(sheet[i])) continue;
                    if (sheet[i] == ':') return true;
                    break;
                }
                index += "first-letter".Length;
            }
            return false;
        }

        /// <summary>
        /// A <c>:lang()</c> rule paints a run from the language tag on the
        /// element or an ancestor, so the paint a static render produces is only
        /// final while the DOM is. A document that both styles text by language
        /// and carries a script has not reached that state here: the first-party
        /// renderer never executes the script, so it cannot observe the language
        /// the script installs and would paint the run in the inherited fill while
        /// reporting success. The guard is read once per document, and only the
        /// conjunction is routed to the compatibility fallback: a document with a
        /// language rule and no script has a paint the cascade really does decide,
        /// and a scripted document with no language rule never depends on the
        /// missing mutation.
        /// </summary>
        private void RequireLanguageStyleSupport()
        {
            if (DocumentStylesByLanguage())
            {
                _report.RequireFallback(
                    "language-styled SVG text under a scripted DOM requires compatibility fallback");
            }
        }

        private bool DocumentStylesByLanguage()
        {
            if (_documentStylesByLanguage.HasValue) return _documentStylesByLanguage.Value;
            _documentStylesByLanguage = false;
            if (_doc?.Root == null) return false;

            bool script = false;
            bool language = false;
            var pending = new Stack<SvgElement>();
            pending.Push(_doc.Root);
            int scanned = 0;
            while (pending.Count > 0 && scanned < MaxStyledSheetScanElements)
            {
                if ((scanned & 0x3F) == 0) CheckDeadline();
                scanned++;
                var element = pending.Pop();
                if (IsScriptElement(element.Name))
                {
                    script = true;
                }
                else if (element.Name == "style" && MentionsLanguageSelector(element.TextContent))
                {
                    language = true;
                }
                if (script && language)
                {
                    _documentStylesByLanguage = true;
                    return true;
                }
                for (int i = element.Children.Count - 1; i >= 0; i--)
                    pending.Push(element.Children[i]);
            }
            return false;
        }

        private static bool IsScriptElement(string name) =>
            name.Equals("script", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(":script", StringComparison.OrdinalIgnoreCase);

        private bool MentionsLanguageSelector(string sheet)
        {
            if (!TryAccountStyleSheet(sheet)) return false;

            int index = 0;
            while ((index = sheet.IndexOf("lang(", index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                // A functional pseudo-class can only be spelled with a leading
                // colon, so the character before the name is what distinguishes a
                // selector from any other mention of the same text.
                for (int i = index - 1; i >= 0; i--)
                {
                    if (char.IsWhiteSpace(sheet[i])) continue;
                    if (sheet[i] == ':') return true;
                    break;
                }
                index += "lang(".Length;
            }
            return false;
        }

        /// <summary>
        /// Charges one sheet against the per-document style budget, shared by
        /// every document-level sheet guard so a document cannot buy unbounded
        /// scanning by naming the same sheet twice.
        /// </summary>
        private bool TryAccountStyleSheet(string sheet)
        {
            if (string.IsNullOrWhiteSpace(sheet)) return false;
            if (++_documentStyleSheetsInspected > MaxStyledSheetsPerDocument) return false;
            if (_documentStyleSheetCharsInspected + sheet.Length > MaxStyledSheetChars) return false;
            _documentStyleSheetCharsInspected += sheet.Length;
            return true;
        }

        private TextLengthAdjustment? ResolveTextLengthDeclaration(
            SvgElement element,
            TextStyle textStyle,
            ViewportContext viewport)
        {
            CheckDeadline();
            string lengthRaw = element.GetPresentationProperty("textLength");
            if (string.IsNullOrWhiteSpace(lengthRaw)) return null;

            if (!TryResolveTextLengthTargets(lengthRaw, textStyle.FontSize, viewport, out float[] targets))
            {
                _report.RequireFallback("SVG textLength value requires compatibility fallback");
                return null;
            }

            bool glyphScale = false;
            string adjustRaw = element.GetPresentationProperty("lengthAdjust");
            if (!string.IsNullOrWhiteSpace(adjustRaw))
            {
                if (!SvgFeatureSupport.IsSupportedLengthAdjust(adjustRaw))
                {
                    _report.RequireFallback("SVG lengthAdjust value requires compatibility fallback");
                    return null;
                }
                glyphScale = adjustRaw.AsSpan().Trim()
                    .Equals("spacingAndGlyphs", StringComparison.OrdinalIgnoreCase);
            }

            return new TextLengthAdjustment(0, 0, targets, glyphScale);
        }

        private void RegisterTextLength(
            TextLengthAdjustment? declaration,
            List<TextPaintRun> runs,
            int startRun,
            TextLayoutState state)
        {
            if (!declaration.HasValue) return;
            if (runs.Count <= startRun) return;
            var adjustment = declaration.Value;
            state.LengthAdjustments.Add(
                adjustment with { StartRun = startRun, EndRun = runs.Count });
        }

        private static bool TryResolveTextLengthTargets(
            string raw,
            float fontSize,
            ViewportContext viewport,
            out float[] targets)
        {
            targets = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            double squared = (double)viewport.Width * viewport.Width +
                             (double)viewport.Height * viewport.Height;
            float diagonal = SvgValues.IsFinite((float)squared) && squared > 0d
                ? (float)Math.Sqrt(squared)
                : 1f;

            var values = new List<float>();
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan().Trim());
            while (tokenizer.Next(out var token))
            {
                if (values.Count >= MaxTextPositionListValues) return false;
                if (!SvgValues.TryParseLength(token, out float parsed, out var unit)) return false;
                float resolved = SvgValues.ResolveUnits(parsed, unit, fontSize, diagonal);
                if (!SvgValues.IsFinite(resolved) || resolved < 0f) return false;
                values.Add(resolved);
            }
            if (values.Count == 0) return false;
            targets = values.ToArray();
            return true;
        }

        private void ApplyTextLengthAdjustments(List<TextPaintRun> runs, List<TextLengthAdjustment> adjustments)
        {
            for (int i = 0; i < adjustments.Count; i++)
            {
                CheckDeadline();
                ApplyTextLengthAdjustment(runs, adjustments[i]);
            }
        }

        private void ApplyTextLengthAdjustment(List<TextPaintRun> runs, TextLengthAdjustment adjustment)
        {
            int start = adjustment.StartRun;
            int end = adjustment.EndRun;
            if (end <= start || end > runs.Count) return;

            int glyphCount = 0;
            float natural = 0f;
            for (int k = start; k < end; k++)
            {
                if (runs[k].PathTransforms != null)
                {
                    _report.RequireFallback("SVG textLength on textPath requires compatibility fallback");
                    return;
                }
                glyphCount += runs[k].GlyphRun.Count;
                natural += runs[k].AdvanceExtent;
            }
            if (glyphCount == 0) return;

            float[] targets = adjustment.Targets;
            if (targets.Length > 1 && targets.Length != glyphCount)
            {
                _report.RequireFallback("SVG textLength list requires compatibility fallback");
                return;
            }

            float target = adjustment.GlyphScale || targets.Length == 1
                ? targets[0]
                : targets.Sum();
            if (!SvgValues.IsFinite(target) || target < 0f)
            {
                _report.RequireFallback("SVG textLength value requires compatibility fallback");
                return;
            }

            if (adjustment.GlyphScale)
            {
                if (!SvgValues.IsFinite(natural) || !(natural > 0f))
                {
                    if (target > 0f)
                    {
                        _report.RequireFallback(
                            "SVG textLength cannot be applied to a zero-extent run");
                    }
                    return;
                }
                float scale = target / natural;
                if (!SvgValues.IsFinite(scale) || MathF.Abs(scale - 1f) <= 0.0001f) return;
                for (int k = start; k < end; k++)
                {
                    TextPaintRun run = runs[k];
                    if (run.HorizontalScale != 1f)
                    {
                        _report.RequireFallback("SVG textLength on textPath requires compatibility fallback");
                        return;
                    }
                    // The glyph outlines are scaled at paint time about the run
                    // origin, so the positioned glyphs stay in natural units and
                    // only the layout advance is scaled here.
                    run.AdvanceScale = scale;
                    run.HorizontalScale = scale;
                }
                return;
            }

            float delta = target - natural;
            if (!SvgValues.IsFinite(delta) || MathF.Abs(delta) <= 0.0001f) return;
            if (glyphCount == 1)
            {
                PositionedGlyph[] only = runs[start].GlyphRun.Glyphs;
                if (only.Length == 1) only[0].X += delta * 0.5f;
                return;
            }

            float perGap = delta / (glyphCount - 1);
            if (!SvgValues.IsFinite(perGap)) return;
            float offset = 0f;
            int seen = 0;
            for (int k = start; k < end; k++)
            {
                if ((seen & 255) == 0) CheckDeadline();
                PositionedGlyph[] glyphs = runs[k].GlyphRun.Glyphs;
                for (int i = 0; i < glyphs.Length; i++)
                {
                    if (seen > 0) offset += perGap;
                    glyphs[i].X += offset;
                    seen++;
                }
            }
        }

        private void LayoutTextPathElement(
            SvgElement element,
            ViewportContext viewport,
            InheritedStyle inheritedPaint,
            TextStyle inheritedText,
            TextLayoutState state,
            List<TextPaintRun> runs)
        {
            if (IsDisplayNone(element)) return;
            if (TryResolveRotationList(element) != null)
            {
                _report.RequireFallback(
                    "per-glyph SVG text rotation on textPath requires compatibility fallback");
                return;
            }
            if ((element.TextContent?.Length ?? 0) > MaxTextRenderCharsPerElement)
            {
                _report.RequireFallback("SVG text exceeds first-party render length budget");
                return;
            }

            using var geometry = ResolveTextPathGeometry(element, viewport, out SvgElement pathOwner);
            if (geometry == null) return;

            if (!string.IsNullOrWhiteSpace(element.GetAttribute("method")) ||
                !string.IsNullOrWhiteSpace(element.GetAttribute("spacing")))
            {
                _report.RequireFallback("advanced SVG textPath layout requires compatibility fallback");
                return;
            }
            foreach (var part in element.Content)
            {
                if (part.IsText) continue;
                if (part.Element?.Name is not ("tspan" or "a"))
                {
                    _report.RequireFallback("nested SVG textPath content requires compatibility fallback");
                    return;
                }
            }

            bool reverse = element.GetAttribute("side") is { } side &&
                           side.Trim().Equals("right", StringComparison.OrdinalIgnoreCase);

            using var measure = new SKPathMeasure(geometry, false);
            float pathLength = measure.Length;
            if (!(pathLength > 0f) || !float.IsFinite(pathLength)) return;

            var paintStyle = inheritedPaint.ResolveOverrides(element, _report);
            if (!paintStyle.Visibility) return;
            var textStyle = ResolveTextStyle(element, inheritedText);

            int firstRun = runs.Count;
            float originX = state.X;
            if (element.Content.Count > 0)
            {
                var ownedChunks = new List<TextChunk>();
                foreach (var part in element.Content)
                {
                    if (part.IsText)
                    {
                        ShapeTextPart(
                            part.Text, element, paintStyle, textStyle, viewport,
                            state, runs, applyOwnOpacity: true, TextPositionLists.None);
                    }
                    else if (part.Element?.Name == "tspan")
                    {
                        LayoutTextElement(
                            part.Element, viewport, paintStyle, textStyle, state, runs, ownedChunks, false);
                    }
                    else
                    {
                        LayoutTextAnchorElement(
                            part.Element, viewport, paintStyle, textStyle, state, runs, ownedChunks);
                    }
                }
            }

            if (runs.Count == firstRun) return;

            for (int i = firstRun; i < runs.Count; i++)
            {
                if (runs[i].GlyphRotations == null) continue;
                _report.RequireFallback(
                    "per-glyph SVG text rotation on textPath requires compatibility fallback");
                return;
            }

            float offset = ResolveTextPathOffset(element, pathOwner, pathLength, textStyle.FontSize);
            if (!float.IsFinite(offset))
            {
                runs.RemoveRange(firstRun, runs.Count - firstRun);
                return;
            }

            float totalExtent = 0f;
            for (int i = firstRun; i < runs.Count; i++) totalExtent += runs[i].AdvanceExtent;
            offset += textStyle.Anchor == TextAnchor.Middle
                ? -totalExtent / 2f
                : textStyle.Anchor == TextAnchor.End ? -totalExtent : 0f;
            for (int k = firstRun; k < runs.Count; k++)
            {
                TextPaintRun run = runs[k];
                float runOrigin = run.X - originX;
                var glyphIds = new List<ushort>(run.GlyphRun.Count);
                var transforms = new List<SKRotationScaleMatrix>(run.GlyphRun.Count);
                for (int i = 0; i < run.GlyphRun.Count; i++)
                {
                    if ((i & 255) == 0) CheckDeadline();
                    PositionedGlyph glyph = run.GlyphRun.Glyphs[i];
                    float advance = Math.Max(0f, glyph.AdvanceX);
                    float centerDistance = offset + runOrigin + glyph.X + advance / 2f;
                    if (centerDistance < 0f || centerDistance > pathLength) continue;
                    float sampled = reverse ? pathLength - centerDistance : centerDistance;
                    if (sampled < 0f || sampled > pathLength) continue;
                    if (!measure.GetPositionAndTangent(sampled, out SKPoint position, out SKPoint tangent))
                        continue;
                    float degrees = MathF.Atan2(tangent.Y, tangent.X) * (180f / MathF.PI);
                    if (reverse) degrees += 180f;
                    glyphIds.Add(glyph.GlyphId);
                    transforms.Add(SKRotationScaleMatrix.CreateDegrees(
                        1f, degrees, position.X, position.Y, advance / 2f, -glyph.Y));
                }
                if (glyphIds.Count == 0) continue;
                run.PathGlyphIds = glyphIds.ToArray();
                run.PathTransforms = transforms.ToArray();
                run.PathBounds = geometry.Bounds;
                run.Chunk = -1;
            }

            for (int k = runs.Count - 1; k >= firstRun; k--)
            {
                if (runs[k].PathTransforms == null) runs.RemoveAt(k);
            }
        }

        /// <summary>
        /// Resolves the geometry a textPath lays its glyphs on. The SVG 2
        /// <c>path</c> attribute wins over <c>href</c> and is authored directly in
        /// the user space of the textPath; path data that yields no geometry at all
        /// is an error, so the textPath falls back to its <c>href</c> target. A
        /// referenced shape contributes its own geometry plus its own transform,
        /// never an ancestor's.
        /// </summary>
        private SKPath ResolveTextPathGeometry(SvgElement element, ViewportContext viewport, out SvgElement owner)
        {
            owner = null;
            string inlinePath = element.GetAttribute("path");
            if (!string.IsNullOrWhiteSpace(inlinePath))
            {
                string data = inlinePath.Trim();
                if (SvgFeatureSupport.HasExternalUrlReference(data))
                {
                    _report.RejectResource("textPath external reference rejected by SVG resource policy");
                    return null;
                }
                if (SvgPathParser.TryBuildPath(data.AsSpan(), out SKPath parsed, _report, CheckTime) &&
                    !parsed.IsEmpty)
                {
                    return parsed;
                }
                parsed?.Dispose();
            }

            string href = element.GetAttribute("href") ?? element.GetLookup("xlink:href");
            if (string.IsNullOrWhiteSpace(href)) return null;
            if (!SvgValues.TryParseLocalReference(href, out string id))
            {
                _report.RejectResource("textPath external reference rejected by SVG resource policy");
                return null;
            }
            if (!_doc.ElementsById.TryGetValue(id, out SvgElement target))
                return null;
            if (target.Name is not ("path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon"))
            {
                _report.RequireFallback("SVG textPath target geometry requires compatibility fallback");
                return null;
            }

            owner = target;
            string transformRaw = target.GetPresentationProperty("transform");
            SKPath geometry = BuildGeometry(target, viewport);
            if (geometry == null) return null;
            if (string.IsNullOrWhiteSpace(transformRaw)) return geometry;

            if (!SvgValues.TryParseTransformList(transformRaw.AsSpan(), out SKMatrix matrix) ||
                !SvgValues.IsFinite(matrix))
            {
                geometry.Dispose();
                _report.RequireFallback("transformed SVG textPath target requires compatibility fallback");
                return null;
            }
            geometry.Transform(matrix);
            return geometry;
        }

        /// <summary>
        /// Resolves the distance along the path where the first glyph of a textPath
        /// is anchored. A declared <c>pathLength</c> on the target rescales every
        /// startOffset, and it is the basis a percentage is a fraction of, so a
        /// percentage resolves against the declared length when the target declares
        /// one and against the measured length otherwise. The scaling rule that a
        /// <c>pathLength</c> of zero is a factor of infinity applies to both forms: a
        /// zero offset stays zero and any other offset leaves the path entirely.
        /// </summary>
        private static float ResolveTextPathOffset(
            SvgElement textPath,
            SvgElement target,
            float actualLength,
            float fontSize)
        {
            string raw = textPath.GetAttribute("startOffset");
            if (string.IsNullOrWhiteSpace(raw)) return 0f;
            raw = raw.Trim();

            bool percentage = raw.EndsWith("%", StringComparison.Ordinal);
            ReadOnlySpan<char> number = percentage ? raw.AsSpan(0, raw.Length - 1) : raw.AsSpan();
            if (!SvgValues.TryParseLength(number, out float declaredOffset, out var offsetUnit)) return 0f;

            string declaredPathLengthRaw = target?.GetPresentationProperty("path-length") ??
                                          target?.GetAttribute("pathLength");
            bool hasDeclaredPathLength = SvgValues.TryParseLength(
                declaredPathLengthRaw.AsSpan(), out float declaredPathLength, out var pathUnit);
            if (hasDeclaredPathLength)
                declaredPathLength = SvgValues.ResolveUnits(declaredPathLength, pathUnit, fontSize, actualLength);

            if (percentage)
                declaredOffset = declaredOffset * 0.01f * (hasDeclaredPathLength ? declaredPathLength : actualLength);
            else
                declaredOffset = SvgValues.ResolveUnits(declaredOffset, offsetUnit, fontSize, actualLength);

            if (!hasDeclaredPathLength) return declaredOffset;
            if (declaredPathLength == 0f)
                return declaredOffset == 0f ? 0f : MathF.CopySign(float.PositiveInfinity, declaredOffset);
            if (!(declaredPathLength > 0f) || !float.IsFinite(declaredPathLength)) return declaredOffset;
            return declaredOffset * (actualLength / declaredPathLength);
        }

        private TextPaintRun ShapeTextPart(
            string rawText,
            SvgElement element,
            InheritedStyle paintStyle,
            TextStyle textStyle,
            ViewportContext viewport,
            TextLayoutState state,
            List<TextPaintRun> runs,
            bool applyOwnOpacity,
            TextPositionLists positions)
        {
            string text = NormalizeText(rawText, textStyle.PreserveWhitespace, state);
            if (text.Length == 0) return null;

            bool rotated = state.RotationList != null;
            if (!TryResolveVisualOrder(
                    text,
                    positions.HasPerCharacterLists || rotated,
                    textStyle.WordSpacing,
                    out string visualOrder))
            {
                _report.RequireFallback("bidirectional SVG text requires compatibility fallback");
                return null;
            }
            if (visualOrder != null) text = visualOrder;
            if (text.Length == 0) return null;

            var typeface = SvgTypefaceResolver.Resolve(
                textStyle.Family, text, textStyle.Language, textStyle.Weight, textStyle.Slant);
            if (typeface == null)
            {
                _report.RequireFallback("SVG text has no available typeface");
                return null;
            }

            var glyphRun = SkiaFontService.ShapeWithTypeface(text, typeface, textStyle.FontSize);
            if (glyphRun.Count == 0)
            {
                _report.RequireFallback("SVG text shaping produced no glyphs");
                return null;
            }
            if (!glyphRun.WasShaped && ContainsComplexText(text))
            {
                _report.RequireFallback("complex SVG text requires an available HarfBuzz shaper");
                return null;
            }
            if (_documentTextGlyphCount + glyphRun.Count > MaxTextGlyphsPerDocument)
            {
                _report.RequireFallback("SVG text exceeds first-party glyph budget");
                return null;
            }

            if (textStyle.LetterSpacing != 0f && glyphRun.Count > 1)
            {
                for (int i = 1; i < glyphRun.Glyphs.Length; i++) glyphRun.Glyphs[i].X += textStyle.LetterSpacing * i;
                glyphRun.Width += textStyle.LetterSpacing * (glyphRun.Count - 1);
            }

            ApplyWordSpacing(glyphRun, text, textStyle.WordSpacing);

            float[] rotations = null;
            if (rotated)
            {
                if (glyphRun.Count != text.Length)
                {
                    _report.RequireFallback("per-glyph SVG text rotation requires compatibility fallback");
                    return null;
                }
                rotations = ConsumeRotations(state.RotationList, ref state.RotationIndex, glyphRun.Count);
            }

            float runX = state.X;
            float runY = state.Y;
            if (positions.HasPerCharacterLists)
            {
                ApplyPerCharacterPositions(glyphRun, text, runX, runY, positions, out float nextX, out float nextY);
                state.X = nextX;
                state.Y = nextY;
            }
            else
            {
                state.X += glyphRun.Width;
            }

            float baselineOffset = -textStyle.BaselineShift;
            if (textStyle.Baseline != BaselineKind.Alphabetic)
            {
                baselineOffset += ResolveBaselineShift(textStyle.Baseline, glyphRun.Metrics);
            }

            var run = new TextPaintRun(
                element, paintStyle, glyphRun, viewport, runX, runY + baselineOffset,
                state.CurrentChunk, applyOwnOpacity, state.ContainerOpacity, textStyle.Decorations);
            run.GlyphRotations = rotations;
            runs.Add(run);
            _documentTextGlyphCount += glyphRun.Count;
            state.GlyphCount += glyphRun.Count;
            state.HasRenderedText = true;
            return run;
        }

        /// <summary>
        /// Takes the next <paramref name="count"/> angles from the current
        /// <c>rotate</c> list, advancing the cursor once per character. A list
        /// shorter than the run keeps its final angle for the remaining
        /// characters, which is the propagation rule SVG 1.1 defines for both
        /// trailing characters and trailing list entries. Returns null when no
        /// angle in the run is non-zero, so an inert list never reaches the
        /// per-glyph transform path.
        /// </summary>
        private static float[] ConsumeRotations(float[] list, ref int index, int count)
        {
            int last = list.Length - 1;
            bool any = false;
            for (int i = 0; i < count; i++)
            {
                int at = index + i;
                if (MathF.Abs(list[at > last ? last : at]) > 0.0001f)
                {
                    any = true;
                    break;
                }
            }
            if (!any)
            {
                index += count;
                return null;
            }
            var rotations = new float[count];
            for (int i = 0; i < count; i++)
            {
                int at = index + i;
                rotations[i] = list[at > last ? last : at];
            }
            index += count;
            return rotations;
        }

        private void ApplyWordSpacing(GlyphRun glyphRun, string text, float wordSpacing)
        {
            if (wordSpacing == 0f || text.IndexOf(' ') < 0) return;
            if (glyphRun.Count != text.Length)
            {
                _report.RequireFallback("shaped SVG word-spacing requires compatibility fallback");
                return;
            }
            float extra = 0f;
            for (int i = 0; i < glyphRun.Count; i++)
            {
                if (text[i] == ' ') extra += wordSpacing;
                if (extra != 0f) glyphRun.Glyphs[i].X += extra;
            }
            glyphRun.Width += extra;
        }

        private void ApplyPerCharacterPositions(
            GlyphRun glyphRun,
            string text,
            float runX,
            float runY,
            TextPositionLists positions,
            out float nextX,
            out float nextY)
        {
            int count = glyphRun.Count;
            if (count != text.Length)
            {
                _report.RequireFallback("per-glyph SVG text positioning requires compatibility fallback");
                nextX = runX + glyphRun.Width;
                nextY = runY;
                return;
            }

            float[] xs = positions.X;
            float[] ys = positions.Y;
            float[] dxs = positions.Dx;
            float[] dys = positions.Dy;

            float originX = runX;
            float originY = runY;
            for (int i = 0; i < count; i++)
            {
                if ((i & 255) == 0) CheckDeadline();
                float shiftX = dxs != null && i < dxs.Length ? dxs[i] : 0f;
                float shiftY = dys != null && i < dys.Length ? dys[i] : 0f;

                if (xs != null && i < xs.Length)
                {
                    originX = xs[i] + shiftX;
                    glyphRun.Glyphs[i].X = originX - runX;
                }
                else if (dxs != null)
                {
                    glyphRun.Glyphs[i].X += shiftX;
                    originX += glyphRun.Glyphs[i].X;
                }
                else
                {
                    originX += glyphRun.Glyphs[i].X;
                }

                if (ys != null && i < ys.Length)
                {
                    originY = ys[i] + shiftY;
                    glyphRun.Glyphs[i].Y = originY - runY;
                }
                else if (dys != null)
                {
                    glyphRun.Glyphs[i].Y += shiftY;
                    originY += glyphRun.Glyphs[i].Y;
                }
                else
                {
                    originY += glyphRun.Glyphs[i].Y;
                }
            }

            nextX = originX + Math.Max(0f, glyphRun.Glyphs[count - 1].AdvanceX);
            nextY = originY;
        }

        /// <summary>
        /// Baseline table for the horizontal writing mode, in user units, positive
        /// downwards. The metrics are the engine's normalized (positive-up ascent,
        /// positive-down descent) values, so a before-edge baseline sits one ascent
        /// below the resolved y and an after-edge baseline one descent above it.
        /// </summary>
        private static float ResolveBaselineShift(BaselineKind baseline, NormalizedFontMetrics metrics)
        {
            float ascent = SvgValues.IsFinite(metrics.Ascent) ? metrics.Ascent : 0f;
            float descent = SvgValues.IsFinite(metrics.Descent) ? metrics.Descent : 0f;
            float xHeight = SvgValues.IsFinite(metrics.XHeight) ? metrics.XHeight : 0f;
            switch (baseline)
            {
                case BaselineKind.BeforeEdge: return ascent;
                case BaselineKind.AfterEdge: return -descent;
                case BaselineKind.Central: return (ascent - descent) * 0.5f;
                case BaselineKind.Middle: return xHeight * 0.5f;
                default: return 0f;
            }
        }

        private void PaintGlyphRun(SKCanvas canvas, TextPaintRun run)
        {
            SKPaint containerLayer = null;
            bool containerLayered = false;
            SKPaint layerPaint = null;
            bool layered = false;
            try
            {
                if (run.ContainerOpacity < 1f && TryBeginOpacityLayer(run.ContainerOpacity, canvas, out containerLayer))
                {
                    containerLayered = true;
                }
                if (run.ApplyOwnOpacity) layered = TryBeginGroupOpacity(run.Element, canvas, out layerPaint);

                using var font = new SKFont(run.GlyphRun.Typeface, run.GlyphRun.FontSize)
                {
                    Edging = SKFontEdging.Antialias,
                    Subpixel = true
                };
                using var blobBuilder = new SKTextBlobBuilder();
                if (run.PathTransforms != null)
                {
                    var pathRun = blobBuilder.AllocateRotationScaleRun(font, run.PathGlyphIds.Length);
                    for (int i = 0; i < run.PathGlyphIds.Length; i++)
                    {
                        pathRun.Glyphs[i] = run.PathGlyphIds[i];
                        pathRun.Positions[i] = run.PathTransforms[i];
                    }
                }
                else
                {
                    var positioned = blobBuilder.AllocatePositionedRun(font, run.GlyphRun.Count);
                    for (int i = 0; i < run.GlyphRun.Count; i++)
                    {
                        positioned.Glyphs[i] = run.GlyphRun.Glyphs[i].GlyphId;
                        positioned.Positions[i] = new SKPoint(run.X + run.GlyphRun.Glyphs[i].X, run.Y + run.GlyphRun.Glyphs[i].Y);
                    }
                }
                using var blob = blobBuilder.Build();
                if (blob == null) return;

                var metrics = font.Metrics;
                SKRect objectBounds = blob.Bounds;
                if (!float.IsFinite(objectBounds.Left) || !float.IsFinite(objectBounds.Top) ||
                    !float.IsFinite(objectBounds.Right) || !float.IsFinite(objectBounds.Bottom) ||
                    objectBounds.Width <= 0f || objectBounds.Height <= 0f)
                {
                    objectBounds = run.PathTransforms == null
                        ? new SKRect(run.X, run.Y + metrics.Ascent,
                            run.X + Math.Max(1f, run.NaturalExtent), run.Y + metrics.Descent)
                        : ResolvePathTextBounds(run, font);
                }
                using var boundsBuilder = new SKPathBuilder();
                boundsBuilder.AddRect(objectBounds);
                using var boundsPath = boundsBuilder.Detach();
                using var fillPaint = ApplyInheritedFillOpacity(
                    BuildFillPaint(run.Element, run.PaintStyle, boundsPath, run.Viewport),
                    run.PaintStyle);
                using var strokePaint = ApplyInheritedStrokeOpacity(
                    BuildStrokePaint(run.Element, run.PaintStyle, boundsPath, run.Viewport),
                    run.PaintStyle);

                bool scaled = false;
                if (run.HorizontalScale != 1f)
                {
                    canvas.Save();
                    canvas.Translate(run.X, 0f);
                    canvas.Scale(run.HorizontalScale, 1f);
                    canvas.Translate(-run.X, 0f);
                    scaled = true;
                }

                if (run.Decorations != TextDecoration.None)
                    PaintTextDecorations(canvas, run, metrics, fillPaint ?? strokePaint);

                for (int i = 0; i < 3; i++)
                {
                    switch (run.PaintStyle.PaintOrder.At(i))
                    {
                        case PaintPhase.Fill when fillPaint != null:
                            canvas.DrawText(blob, 0f, 0f, fillPaint);
                            break;
                        case PaintPhase.Stroke when strokePaint != null:
                            canvas.DrawText(blob, 0f, 0f, strokePaint);
                            break;
                    }
                }

                if (scaled) canvas.Restore();
            }
            finally
            {
                if (layered)
                {
                    canvas.Restore();
                    _activeLayers--;
                    layerPaint.Dispose();
                }
                if (containerLayered)
                {
                    canvas.Restore();
                    _activeLayers--;
                    containerLayer.Dispose();
                }
            }
        }

        private bool TryBeginOpacityLayer(float opacity, SKCanvas canvas, out SKPaint layerPaint)
        {
            if (opacity >= 1f)
            {
                layerPaint = null;
                return false;
            }
            if (_activeLayers >= _maxActiveLayers)
            {
                _report.RequireFallback(
                    "layer budget exceeded; compatibility fallback required for isolated opacity");
                layerPaint = null;
                return false;
            }
            layerPaint = new SKPaint
            {
                Color = SKColors.Black.WithAlpha((byte)Math.Clamp((int)(opacity * 255f), 0, 255))
            };
            canvas.SaveLayer(layerPaint);
            _activeLayers++;
            return true;
        }

        private void PaintTextDecorations(
            SKCanvas canvas,
            TextPaintRun run,
            SKFontMetrics metrics,
            SKPaint paint)
        {
            if (paint == null) return;
            float fontSize = run.GlyphRun.FontSize;
            float defaultThickness = Math.Max(1f, fontSize / 16f);
            bool rotated = run.GlyphRotations != null && run.PathTransforms != null;

            if ((run.Decorations & TextDecoration.Underline) != 0)
            {
                float thickness = metrics.UnderlineThickness ?? defaultThickness;
                if (!(thickness > 0f) || !SvgValues.IsFinite(thickness)) thickness = defaultThickness;
                float y = metrics.UnderlinePosition ?? fontSize * 0.1f;
                PaintDecorationBand(canvas, run, paint, y, thickness, rotated);
            }
            if ((run.Decorations & TextDecoration.Overline) != 0)
            {
                float thickness = metrics.UnderlineThickness ?? defaultThickness;
                if (!(thickness > 0f) || !SvgValues.IsFinite(thickness)) thickness = defaultThickness;
                PaintDecorationBand(canvas, run, paint, metrics.Ascent, thickness, rotated);
            }
            if ((run.Decorations & TextDecoration.LineThrough) != 0)
            {
                float thickness = metrics.StrikeoutThickness ?? defaultThickness;
                if (!(thickness > 0f) || !SvgValues.IsFinite(thickness)) thickness = defaultThickness;
                float y = metrics.StrikeoutPosition ?? -fontSize * 0.25f;
                PaintDecorationBand(canvas, run, paint, y, thickness, rotated);
            }
        }

        /// <summary>
        /// Paints one decoration band at <paramref name="offset"/> from the
        /// baseline. Drawn inside the run's own canvas state, so the natural
        /// extent is the correct span even when lengthAdjust scaled the
        /// outlines. A rotated run has no single span to cover, so the band is
        /// emitted per glyph and turned by that glyph's own rotation.
        /// </summary>
        private void PaintDecorationBand(
            SKCanvas canvas,
            TextPaintRun run,
            SKPaint paint,
            float offset,
            float thickness,
            bool rotated)
        {
            if (!SvgValues.IsFinite(offset) || !SvgValues.IsFinite(thickness)) return;
            if (rotated)
            {
                for (int i = 0; i < run.PathTransforms.Length && i < run.GlyphRun.Count; i++)
                {
                    if ((i & 255) == 0) CheckDeadline();
                    float advance = Math.Max(0f, run.GlyphRun.Glyphs[i].AdvanceX);
                    if (!(advance > 0f) || !SvgValues.IsFinite(advance)) continue;
                    using var bandBuilder = new SKPathBuilder();
                    bandBuilder.AddRect(new SKRect(0f, offset, advance, offset + thickness));
                    using var band = bandBuilder.Detach();
                    band.Transform(run.PathTransforms[i].ToMatrix());
                    canvas.DrawPath(band, paint);
                }
                return;
            }
            float width = run.NaturalExtent;
            if (!(width > 0f) || !SvgValues.IsFinite(width)) return;
            canvas.DrawRect(run.X, run.Y + offset, run.X + width, run.Y + offset + thickness, paint);
        }

        private SKRect ResolvePathTextBounds(TextPaintRun run, SKFont font)
        {
            if (run.PathTransforms == null || run.PathTransforms.Length == 0)
                return run.PathBounds;

            bool hasBounds = false;
            SKRect bounds = default;
            for (int i = 0; i < run.PathTransforms.Length; i++)
            {
                if ((i & 255) == 0) CheckDeadline();
                using var glyphPath = font.GetGlyphPath(run.PathGlyphIds[i]);
                if (glyphPath == null || glyphPath.IsEmpty) continue;
                using var transformed = new SKPath();
                glyphPath.Transform(run.PathTransforms[i].ToMatrix(), transformed);
                SKRect glyphBounds = transformed.TightBounds;
                if (!float.IsFinite(glyphBounds.Left) || !float.IsFinite(glyphBounds.Top) ||
                    !float.IsFinite(glyphBounds.Right) || !float.IsFinite(glyphBounds.Bottom) ||
                    glyphBounds.Width <= 0f || glyphBounds.Height <= 0f)
                {
                    continue;
                }
                bounds = hasBounds ? SKRect.Union(bounds, glyphBounds) : glyphBounds;
                hasBounds = true;
            }
            return hasBounds ? bounds : run.PathBounds;
        }

        /// <summary>
        /// Turns the per-glyph angles captured during layout into the rotation
        /// transforms the paint pass already uses for path-positioned glyphs.
        /// Each glyph is turned about its own origin on the baseline, which is
        /// the point the SVG rotation is defined around, and the advance is left
        /// untouched because a rotation does not change how far the run travels.
        /// The pass runs after the anchor and textLength passes so the baked
        /// origin is the final one.
        /// </summary>
        private void ApplyTextRotations(List<TextPaintRun> runs)
        {
            for (int k = 0; k < runs.Count; k++)
            {
                CheckDeadline();
                TextPaintRun run = runs[k];
                float[] rotations = run.GlyphRotations;
                if (rotations == null || run.PathTransforms != null) continue;

                int count = Math.Min(rotations.Length, run.GlyphRun.Count);
                if (count == 0) continue;
                var glyphIds = new ushort[count];
                var transforms = new SKRotationScaleMatrix[count];
                for (int i = 0; i < count; i++)
                {
                    PositionedGlyph glyph = run.GlyphRun.Glyphs[i];
                    glyphIds[i] = glyph.GlyphId;
                    transforms[i] = SKRotationScaleMatrix.CreateDegrees(
                        1f, rotations[i],
                        run.X + glyph.X, run.Y + glyph.Y,
                        0f, 0f);
                }
                run.PathGlyphIds = glyphIds;
                run.PathTransforms = transforms;
                float ascent = SvgValues.IsFinite(run.GlyphRun.Metrics.Ascent)
                    ? run.GlyphRun.Metrics.Ascent : 0f;
                float descent = SvgValues.IsFinite(run.GlyphRun.Metrics.Descent)
                    ? run.GlyphRun.Metrics.Descent : 0f;
                run.PathBounds = new SKRect(
                    run.X, run.Y - ascent,
                    run.X + run.NaturalExtent, run.Y + descent);
            }
        }

        private static void ApplyTextAnchors(List<TextPaintRun> runs, List<TextChunk> chunks)
        {
            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                var chunk = chunks[chunkIndex];
                float right = chunk.StartX;
                for (int i = 0; i < runs.Count; i++)
                {
                    if (runs[i].Chunk == chunkIndex) right = Math.Max(right, runs[i].X + runs[i].AdvanceExtent);
                }
                float width = Math.Max(0f, right - chunk.StartX);
                float shift = chunk.Anchor == TextAnchor.Middle ? -width / 2f : chunk.Anchor == TextAnchor.End ? -width : 0f;
                if (shift == 0f) continue;
                for (int i = 0; i < runs.Count; i++)
                {
                    if (runs[i].Chunk == chunkIndex) runs[i].X += shift;
                }
            }
        }

        private TextStyle ResolveTextStyle(SvgElement element, TextStyle inherited)
        {
            string family = element.GetPresentationProperty("font-family");
            string sizeRaw = element.GetPresentationProperty("font-size");
            string weightRaw = element.GetPresentationProperty("font-weight");
            string styleRaw = element.GetPresentationProperty("font-style");
            string anchorRaw = element.GetPresentationProperty("text-anchor");
            string spacingRaw = element.GetPresentationProperty("letter-spacing");
            string xmlSpace = element.GetAttribute("xml:space");

            float fontSize = ResolveFontSize(sizeRaw, inherited.FontSize, _report);
            float letterSpacing = inherited.LetterSpacing;
            if (!string.IsNullOrWhiteSpace(spacingRaw) && !spacingRaw.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase) &&
                SvgValues.TryParseLength(spacingRaw.AsSpan().Trim(), out float spacing, out var spacingUnit))
            {
                letterSpacing = SvgValues.ClampCoord(SvgValues.ResolveUnits(spacing, spacingUnit, fontSize, fontSize));
            }

            ValidateTextDirection(element);
            return new TextStyle(
                string.IsNullOrWhiteSpace(family) ? inherited.Family : family,
                fontSize,
                ParseFontWeight(weightRaw, inherited.Weight),
                ParseFontSlant(styleRaw, inherited.Slant),
                ParseTextAnchor(anchorRaw, inherited.Anchor),
                letterSpacing,
                string.IsNullOrWhiteSpace(xmlSpace) ? inherited.PreserveWhitespace : xmlSpace.Trim().Equals("preserve", StringComparison.OrdinalIgnoreCase),
                ResolveBaselineKind(element, inherited.Baseline),
                ResolveBaselineShiftValue(element, inherited.BaselineShift, fontSize),
                ResolveWordSpacing(element, inherited.WordSpacing, fontSize),
                ResolveTextDecoration(element, inherited.Decorations),
                ResolveLanguage(element, inherited.Language));
        }

        /// <summary>
        /// The language that selects a typeface for the run. It is inherited
        /// like any other text property, and the HTML <c>lang</c> attribute wins
        /// over <c>xml:lang</c> on the same element, matching the order the
        /// cascade uses for <c>:lang()</c> matching.
        /// </summary>
        private static string ResolveLanguage(SvgElement element, string inherited)
        {
            string language = element.GetAttribute("lang");
            if (string.IsNullOrWhiteSpace(language)) language = element.GetAttribute("xml:lang");
            if (string.IsNullOrWhiteSpace(language)) return inherited;
            return language.Trim();
        }

        private TextStyle ResolveAncestorTextStyle(SvgElement element)
        {
            var ancestors = new Stack<SvgElement>();
            for (var ancestor = element.Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                ancestors.Push(ancestor);
            }
            var style = TextStyle.Default;
            while (ancestors.Count > 0)
            {
                style = ResolveTextStyle(ancestors.Pop(), style);
            }
            return style;
        }

        private BaselineKind ResolveBaselineKind(SvgElement element, BaselineKind inherited)
        {
            string raw = element.GetPresentationProperty("dominant-baseline");
            if (string.IsNullOrWhiteSpace(raw)) raw = element.GetPresentationProperty("alignment-baseline");
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            var keyword = raw.AsSpan().Trim();
            if (IsCssWideKeyword(keyword)) return BaselineKind.Alphabetic;
            if (!SvgFeatureSupport.IsSupportedDominantBaseline(raw))
            {
                _report.RequireFallback("SVG dominant-baseline value requires compatibility fallback");
                return inherited;
            }
            return ParseBaselineKeyword(keyword);
        }

        private static BaselineKind ParseBaselineKeyword(ReadOnlySpan<char> keyword)
        {
            if (keyword.Equals("central", StringComparison.OrdinalIgnoreCase)) return BaselineKind.Central;
            if (keyword.Equals("middle", StringComparison.OrdinalIgnoreCase)) return BaselineKind.Middle;
            if (keyword.Equals("text-before-edge", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("before-edge", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("text-top", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("hanging", StringComparison.OrdinalIgnoreCase)) return BaselineKind.BeforeEdge;
            if (keyword.Equals("text-after-edge", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("after-edge", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("text-bottom", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("ideographic", StringComparison.OrdinalIgnoreCase)) return BaselineKind.AfterEdge;
            return BaselineKind.Alphabetic;
        }

        private float ResolveBaselineShiftValue(SvgElement element, float inherited, float fontSize)
        {
            string raw = element.GetPresentationProperty("baseline-shift");
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            var keyword = raw.AsSpan().Trim();
            if (IsCssWideKeyword(keyword)) return 0f;
            if (keyword.Equals("baseline", StringComparison.OrdinalIgnoreCase)) return 0f;
            if (keyword.Equals("sub", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("super", StringComparison.OrdinalIgnoreCase) ||
                !SvgValues.TryParseLength(keyword, out float parsed, out var unit))
            {
                _report.RequireFallback("SVG baseline-shift value requires compatibility fallback");
                return inherited;
            }
            float resolved = SvgValues.ResolveUnits(parsed, unit, fontSize, fontSize);
            if (!SvgValues.IsFinite(resolved))
            {
                _report.RequireFallback("SVG baseline-shift value requires compatibility fallback");
                return inherited;
            }
            return SvgValues.ClampCoord(resolved);
        }

        private float ResolveWordSpacing(SvgElement element, float inherited, float fontSize)
        {
            string raw = element.GetPresentationProperty("word-spacing");
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            var keyword = raw.AsSpan().Trim();
            if (keyword.Equals("normal", StringComparison.OrdinalIgnoreCase)) return 0f;
            if (IsCssWideKeyword(keyword)) return 0f;
            if (!SvgValues.TryParseLength(keyword, out float parsed, out var unit))
            {
                _report.RequireFallback("SVG word-spacing value requires compatibility fallback");
                return inherited;
            }
            float resolved = SvgValues.ResolveUnits(parsed, unit, fontSize, fontSize);
            if (!SvgValues.IsFinite(resolved))
            {
                _report.RequireFallback("SVG word-spacing value requires compatibility fallback");
                return inherited;
            }
            return SvgValues.ClampCoord(resolved);
        }

        private TextDecoration ResolveTextDecoration(SvgElement element, TextDecoration inherited)
        {
            string raw = element.GetPresentationProperty("text-decoration");
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            var keyword = raw.AsSpan().Trim();
            if (IsCssWideKeyword(keyword)) return TextDecoration.None;

            var flags = TextDecoration.None;
            var tokenizer = SvgValues.CreateTokenizer(keyword);
            int inspected = 0;
            while (tokenizer.Next(out var token))
            {
                if (++inspected > MaxTextDecorationTokens)
                {
                    _report.RequireFallback("SVG text-decoration value requires compatibility fallback");
                    return inherited;
                }
                if (token.Equals("none", StringComparison.OrdinalIgnoreCase)) { flags = TextDecoration.None; continue; }
                if (token.Equals("underline", StringComparison.OrdinalIgnoreCase)) { flags |= TextDecoration.Underline; continue; }
                if (token.Equals("overline", StringComparison.OrdinalIgnoreCase)) { flags |= TextDecoration.Overline; continue; }
                if (token.Equals("line-through", StringComparison.OrdinalIgnoreCase)) { flags |= TextDecoration.LineThrough; continue; }
                _report.RequireFallback("SVG text-decoration value requires compatibility fallback");
                return inherited;
            }
            return flags;
        }

        private static bool IsCssWideKeyword(ReadOnlySpan<char> value) =>
            value.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unset", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("revert", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// The first-party paragraph is laid out with a left-to-right base direction,
        /// which is the initial value of <c>direction</c>. A right-to-left base
        /// direction on any element in the inherited chain reverses the whole
        /// paragraph, so it is reported here where the direction is resolved rather
        /// than at attribute sight: the parse only inspects <c>text</c> and
        /// <c>tspan</c>, while <c>direction</c> is inherited from any ancestor.
        /// </summary>
        private void ValidateTextDirection(SvgElement element)
        {
            string raw = element.GetPresentationProperty("direction");
            if (string.IsNullOrWhiteSpace(raw)) return;
            var keyword = raw.AsSpan().Trim();
            if (IsCssWideKeyword(keyword)) return;
            if (keyword.Equals("ltr", StringComparison.OrdinalIgnoreCase)) return;
            _report.RequireFallback("SVG text direction requires compatibility fallback");
        }

        private bool TryResolveTextLengthList(string raw, float percentReference, float fontSize, out float[] values)
        {
            values = null;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var resolved = new List<float>();
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan().Trim());
            while (tokenizer.Next(out var token))
            {
                if (resolved.Count >= MaxTextPositionListValues)
                {
                    _report.RequireFallback(
                        "per-glyph SVG text positioning list exceeds the first-party value budget");
                    return false;
                }
                if (!SvgValues.TryParseLength(token, out float parsed, out var unit)) return false;
                float value = SvgValues.ResolveUnits(parsed, unit, fontSize, percentReference);
                if (!SvgValues.IsFinite(value)) return false;
                resolved.Add(SvgValues.ClampCoord(value));
            }
            if (resolved.Count == 0) return false;
            values = resolved.ToArray();
            return true;
        }

        private static float FirstPosition(float[] list, float fallback) =>
            list != null && list.Length > 0 ? list[0] : fallback;

        private const float MaxFontSize = 4096f;
        private const float MinFontSize = 0.01f;

        private static float ResolveFontSize(string raw, float inherited, SvgParseReport report)
        {
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            ReadOnlySpan<char> value = raw.AsSpan().Trim();
            if (value.IsEmpty) return inherited;

            if (value.Equals("inherit", System.StringComparison.OrdinalIgnoreCase) ||
                value.Equals("revert", System.StringComparison.OrdinalIgnoreCase) ||
                value.Equals("unset", System.StringComparison.OrdinalIgnoreCase))
            {
                return inherited;
            }
            if (value.Equals("initial", System.StringComparison.OrdinalIgnoreCase) ||
                value.Equals("medium", System.StringComparison.OrdinalIgnoreCase))
            {
                return BoundedFontSize(DefaultFontSize);
            }
            if (value.Equals("smaller", System.StringComparison.OrdinalIgnoreCase))
            {
                return BoundedFontSize(inherited / 1.2f);
            }
            if (value.Equals("larger", System.StringComparison.OrdinalIgnoreCase))
            {
                return BoundedFontSize(inherited * 1.2f);
            }

            float absolute = AbsoluteFontSizeKeyword(value);
            if (absolute > 0f) return BoundedFontSize(absolute);

            if (SvgValues.TryParseLength(value, out float parsed, out var unit))
            {
                float resolved = SvgValues.ResolveUnits(parsed, unit, inherited, inherited);
                return float.IsFinite(resolved) && resolved > 0f
                    ? BoundedFontSize(resolved)
                    : inherited;
            }

            report?.RequireFallback("SVG font-size value requires compatibility fallback");
            return inherited;
        }

        private static float AbsoluteFontSizeKeyword(ReadOnlySpan<char> value)
        {
            float ratio;
            if (value.Equals("xx-small", System.StringComparison.OrdinalIgnoreCase)) ratio = 9f / 16f;
            else if (value.Equals("x-small", System.StringComparison.OrdinalIgnoreCase)) ratio = 10f / 16f;
            else if (value.Equals("small", System.StringComparison.OrdinalIgnoreCase)) ratio = 13f / 16f;
            else if (value.Equals("large", System.StringComparison.OrdinalIgnoreCase)) ratio = 18f / 16f;
            else if (value.Equals("x-large", System.StringComparison.OrdinalIgnoreCase)) ratio = 24f / 16f;
            else if (value.Equals("xx-large", System.StringComparison.OrdinalIgnoreCase)) ratio = 32f / 16f;
            else return 0f;
            return DefaultFontSize * ratio;
        }

        private static float BoundedFontSize(float value)
        {
            if (!float.IsFinite(value)) return DefaultFontSize;
            if (value < MinFontSize) return MinFontSize;
            return value > MaxFontSize ? MaxFontSize : value;
        }

        private static string NormalizeText(string raw, bool preserve, TextLayoutState state)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if (preserve)
            {
                state.PendingSpace = false;
                return raw.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            }
            var result = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                if (IsXmlWhitespace(c))
                {
                    if (state.HasRenderedText || result.Length > 0) state.PendingSpace = true;
                }
                else
                {
                    if (state.PendingSpace) { result.Append(' '); state.PendingSpace = false; }
                    result.Append(c);
                }
            }
            return result.ToString();
        }

        private static bool IsXmlWhitespace(char value) =>
            value is ' ' or '\t' or '\r' or '\n';

        private static bool ContainsComplexText(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] > 0x7f) return true;
            }
            return false;
        }

        /// <summary>
        /// Resolves the visual character order of one text run.
        ///
        /// The first-party subset lays a paragraph out left to right. That is the
        /// identity order for the overwhelming majority of content, so a run with no
        /// explicit directional control and no right-to-left script is returned
        /// unchanged and never reaches the resolver. Any other run is resolved with
        /// the Unicode bidirectional algorithm restricted to the classes the subset
        /// can honour: an explicit left-to-right base direction, no isolate
        /// initiators, no bracket pairs, and no right-to-left script characters,
        /// because the shaper behind the first-party path can only produce
        /// left-to-right glyph runs. A run outside that subset reports false and is
        /// routed to the compatibility fallback instead of painting a paragraph in
        /// an order the engine cannot compute.
        ///
        /// <paramref name="visualOrder"/> is null when the run is already in visual
        /// order and carries no explicit formatting character, which is the case for
        /// every run that never reaches this resolver.
        /// </summary>
        private static bool TryResolveVisualOrder(
            string text,
            bool perCharacterPositions,
            float wordSpacing,
            out string visualOrder)
        {
            visualOrder = null;
            if (!ContainsBidiRelevantText(text)) return true;
            if (perCharacterPositions || wordSpacing != 0f) return false;
            if (text.Length > MaxBidiParagraphChars) return false;

            int count = text.Length;
            var types = new BidiType[count];
            var levels = new byte[count];
            var origin = new int[count];
            var stackLevel = new byte[MaxBidiEmbeddingDepth + 1];
            var stackOverride = new BidiType[MaxBidiEmbeddingDepth + 1];
            int depth = 0;
            int overflow = 0;
            int kept = 0;

            for (int i = 0; i < count; i++)
            {
                if (!TryClassifyBidi(text[i], out BidiType type)) return false;
                if (type is BidiType.Isolate or BidiType.IsolateTerminator) return false;

                // UAX 9 rules X1 to X8.
                if (type is BidiType.EmbeddingLeft or BidiType.EmbeddingRight or
                    BidiType.OverrideLeft or BidiType.OverrideRight)
                {
                    bool rightToLeft = type is BidiType.EmbeddingRight or BidiType.OverrideRight;
                    bool overridden = type is BidiType.OverrideLeft or BidiType.OverrideRight;
                    if (depth < MaxBidiEmbeddingDepth)
                    {
                        depth++;
                        stackLevel[depth] = NextEmbeddingLevel(stackLevel[depth - 1], rightToLeft);
                        stackOverride[depth] = overridden
                            ? (rightToLeft ? BidiType.RightToLeft : BidiType.LeftToRight)
                            : BidiType.Neutral;
                    }
                    else
                    {
                        overflow++;
                    }
                    continue;
                }
                if (type == BidiType.PopFormatting)
                {
                    if (overflow > 0) overflow--;
                    else if (depth > 0) depth--;
                    continue;
                }

                if (type is BidiType.RightToLeft or BidiType.RightToLeftArabic) return false;
                types[kept] = stackOverride[depth] != BidiType.Neutral ? stackOverride[depth] : type;
                levels[kept] = stackLevel[depth];
                origin[kept] = i;
                kept++;
            }
            if (kept == 0)
            {
                visualOrder = string.Empty;
                return true;
            }

            Array.Resize(ref types, kept);
            Array.Resize(ref levels, kept);
            Array.Resize(ref origin, kept);
            ResolveWeakTypes(types, levels, kept);
            ResolveNeutralTypes(types, levels, kept);
            ResolveImplicitLevels(types, levels, kept);
            ReorderVisually(text, origin, types, levels, kept, out visualOrder);
            return true;
        }

        private const int MaxBidiParagraphChars = 1024;
        private const int MaxBidiEmbeddingDepth = 125;

        private static bool ContainsBidiRelevantText(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (IsBidiControl(c) || IsRightToLeftScript(c)) return true;
            }
            return false;
        }

        /// <summary>
        /// True for the bidirectional formatting characters and directional marks,
        /// which carry no glyphs but do decide the order of a paragraph.
        /// </summary>
        private static bool IsBidiControl(char c) =>
            c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069') or
                '\u200E' or '\u200F' or '\u061C';

        /// <summary>
        /// True for the code point blocks the bidirectional algorithm classifies as
        /// strong right-to-left or Arabic-letter.
        /// </summary>
        private static bool IsRightToLeftScript(char c) =>
            c is (>= '\u0590' and <= '\u08FF') or (>= '\uFB1D' and <= '\uFDFF') or
                (>= '\uFE70' and <= '\uFEFE');

        private static bool TryClassifyBidi(char c, out BidiType type)
        {
            type = BidiType.Neutral;
            switch (c)
            {
                case '\u202A': type = BidiType.EmbeddingLeft; return true;
                case '\u202B': type = BidiType.EmbeddingRight; return true;
                case '\u202D': type = BidiType.OverrideLeft; return true;
                case '\u202E': type = BidiType.OverrideRight; return true;
                case '\u202C': type = BidiType.PopFormatting; return true;
                case '\u2066':
                case '\u2067':
                case '\u2068': type = BidiType.Isolate; return true;
                case '\u2069': type = BidiType.IsolateTerminator; return true;
                case '\u200E': type = BidiType.LeftToRight; return true;
                case '\u200F': type = BidiType.RightToLeft; return true;
                case '\u061C': type = BidiType.RightToLeftArabic; return true;
                default: break;
            }

            // Paired brackets need the bracket resolution pass, and anything outside
            // the ranges the first-party shaper is exercised on is left to the
            // compatibility fallback.
            if (c is '(' or ')' or '[' or ']' or '{' or '}' ||
                IsRightToLeftScript(c) || c > '\uFFFE')
            {
                return false;
            }

            if (c is >= '0' and <= '9') { type = BidiType.Number; return true; }
            if (c is '+' or '-') { type = BidiType.NumberSeparator; return true; }
            if (c is '#' or '$') { type = BidiType.NumberTerminator; return true; }
            if (c is ',' or '.' or ':' or '/') { type = BidiType.CommonSeparator; return true; }
            if (c is ' ' or '\t' or '\u00A0') { type = BidiType.Whitespace; return true; }
            type = char.IsLetter(c) ? BidiType.LeftToRight : BidiType.Neutral;
            return true;
        }

        private static byte NextEmbeddingLevel(byte current, bool rightToLeft)
        {
            for (int candidate = current + 1; candidate <= MaxBidiEmbeddingDepth; candidate++)
            {
                if (((candidate & 1) == 1) == rightToLeft) return (byte)candidate;
            }
            return MaxBidiEmbeddingDepth;
        }

        /// <summary>
        /// UAX 9 rules W4 to W7. The supported subset has no strong right-to-left
        /// character, so a European number only ever becomes left-to-right.
        /// </summary>
        private static void ResolveWeakTypes(BidiType[] types, byte[] levels, int count)
        {
            for (int i = 1; i + 1 < count; i++)
            {
                if (types[i] != BidiType.CommonSeparator) continue;
                if (types[i - 1] == BidiType.Number && types[i + 1] == BidiType.Number)
                    types[i] = BidiType.Number;
            }

            int position = 0;
            while (position < count)
            {
                if (types[position] != BidiType.NumberTerminator) { position++; continue; }
                int start = position;
                while (position < count && types[position] == BidiType.NumberTerminator) position++;
                bool joinsNumber = (start > 0 && types[start - 1] == BidiType.Number) ||
                                   (position < count && types[position] == BidiType.Number);
                if (!joinsNumber) continue;
                for (int i = start; i < position; i++) types[i] = BidiType.Number;
            }

            for (int i = 0; i < count; i++)
            {
                if (types[i] is BidiType.NumberSeparator or BidiType.NumberTerminator or
                    BidiType.CommonSeparator)
                {
                    types[i] = BidiType.Neutral;
                }
            }

            int lastStrong = -1;
            for (int i = 0; i < count; i++)
            {
                if (types[i] == BidiType.LeftToRight)
                {
                    lastStrong = i;
                }
                else if (types[i] == BidiType.Number && lastStrong >= 0)
                {
                    types[i] = BidiType.LeftToRight;
                }
            }
        }

        /// <summary>
        /// UAX 9 rules N1 and N2. A neutral run between two strongs of the same
        /// direction takes it; anything else takes the embedding direction.
        /// </summary>
        private static void ResolveNeutralTypes(BidiType[] types, byte[] levels, int count)
        {
            int position = 0;
            while (position < count)
            {
                if (!IsNeutral(types[position])) { position++; continue; }
                int start = position;
                while (position < count && IsNeutral(types[position])) position++;
                BidiType before = start > 0 ? types[start - 1] : BidiType.Neutral;
                BidiType after = position < count ? types[position] : BidiType.Neutral;
                if (before != BidiType.LeftToRight && before != BidiType.RightToLeft)
                    before = BidiType.Neutral;
                if (after != BidiType.LeftToRight && after != BidiType.RightToLeft)
                    after = BidiType.Neutral;
                for (int i = start; i < position; i++)
                {
                    if (before != BidiType.Neutral && before == after) types[i] = before;
                    else types[i] = (levels[i] & 1) == 1 ? BidiType.RightToLeft : BidiType.LeftToRight;
                }
            }
        }

        private static bool IsNeutral(BidiType type) =>
            type is BidiType.Neutral or BidiType.Whitespace;

        /// <summary>
        /// UAX 9 rules I1 and I2: a left-to-right character or European number inside
        /// a right-to-left embedding is raised to the next even level.
        /// </summary>
        private static void ResolveImplicitLevels(BidiType[] types, byte[] levels, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if ((levels[i] & 1) == 0) continue;
                if (types[i] is BidiType.LeftToRight or BidiType.Number) levels[i]++;
            }
        }

        /// <summary>
        /// UAX 9 rule L1: whitespace at the end of the line returns to the paragraph
        /// embedding level, which the first-party subset fixes at left-to-right.
        /// </summary>
        private static void ResetTrailingWhitespace(BidiType[] types, byte[] levels, int count)
        {
            for (int i = count - 1; i >= 0 && types[i] == BidiType.Whitespace; i--) levels[i] = 0;
        }

        /// <summary>
        /// UAX 9 rules L2 and X9: reverses every contiguous run at or above each
        /// level from the highest one down to the lowest odd one, then rebuilds the
        /// run without the explicit formatting characters.
        /// </summary>
        private static void ReorderVisually(
            string text,
            int[] origin,
            BidiType[] types,
            byte[] levels,
            int count,
            out string visualOrder)
        {
            visualOrder = null;
            ResetTrailingWhitespace(types, levels, count);

            int highest = 0;
            int lowestOdd = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                if (levels[i] > highest) highest = levels[i];
                if ((levels[i] & 1) == 1 && levels[i] < lowestOdd) lowestOdd = levels[i];
            }
            if (lowestOdd == int.MaxValue) lowestOdd = highest + 1;

            var order = new int[count];
            for (int i = 0; i < count; i++) order[i] = i;
            for (int level = highest; level >= lowestOdd; level--)
            {
                int start = 0;
                while (start < count)
                {
                    if (levels[order[start]] < level) { start++; continue; }
                    int end = start;
                    while (end < count && levels[order[end]] >= level) end++;
                    Array.Reverse(order, start, end - start);
                    start = end;
                }
            }

            bool reordered = false;
            for (int i = 0; i < count; i++)
            {
                if (order[i] == i) continue;
                reordered = true;
                break;
            }
            if (!reordered && count == text.Length) return;

            var builder = new StringBuilder(count);
            for (int i = 0; i < count; i++) builder.Append(text[origin[order[i]]]);
            visualOrder = builder.ToString();
        }

        private enum BidiType : byte
        {
            LeftToRight,
            RightToLeft,
            RightToLeftArabic,
            Number,
            NumberSeparator,
            NumberTerminator,
            CommonSeparator,
            Neutral,
            Whitespace,
            EmbeddingLeft,
            EmbeddingRight,
            OverrideLeft,
            OverrideRight,
            PopFormatting,
            Isolate,
            IsolateTerminator
        }

        private static int ParseFontWeight(string raw, int inherited)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase)) return inherited;
            if (raw.Trim().Equals("bold", StringComparison.OrdinalIgnoreCase)) return 700;
            if (raw.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase)) return 400;
            if (raw.Trim().Equals("bolder", StringComparison.OrdinalIgnoreCase)) return Math.Min(900, inherited + 300);
            if (raw.Trim().Equals("lighter", StringComparison.OrdinalIgnoreCase)) return Math.Max(100, inherited - 300);
            return int.TryParse(raw, out int weight) ? Math.Clamp(weight, 100, 900) : inherited;
        }

        private static SKFontStyleSlant ParseFontSlant(string raw, SKFontStyleSlant inherited)
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("inherit", StringComparison.OrdinalIgnoreCase)) return inherited;
            if (raw.Trim().Equals("italic", StringComparison.OrdinalIgnoreCase)) return SKFontStyleSlant.Italic;
            if (raw.Trim().Equals("oblique", StringComparison.OrdinalIgnoreCase)) return SKFontStyleSlant.Oblique;
            return raw.Trim().Equals("normal", StringComparison.OrdinalIgnoreCase) ? SKFontStyleSlant.Upright : inherited;
        }

        private static TextAnchor ParseTextAnchor(string raw, TextAnchor inherited)
        {
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            return raw.Trim().ToLowerInvariant() switch
            {
                "start" => TextAnchor.Start,
                "middle" => TextAnchor.Middle,
                "end" => TextAnchor.End,
                _ => inherited
            };
        }

        private enum TextAnchor { Start, Middle, End }

        private enum BaselineKind { Alphabetic, BeforeEdge, AfterEdge, Central, Middle }

        [Flags]
        private enum TextDecoration { None = 0, Underline = 1, Overline = 2, LineThrough = 4 }

        private sealed class TextLayoutState
        {
            public float X;
            public float Y;
            public float ContainerOpacity = 1f;
            public float[] RotationList;
            public int RotationIndex;
            public int CurrentChunk = -1;
            public int GlyphCount;
            public bool HasRenderedText;
            public bool PendingSpace;
            public readonly List<TextLengthAdjustment> LengthAdjustments = new List<TextLengthAdjustment>();
        }

        private readonly record struct TextLengthAdjustment(int StartRun, int EndRun, float[] Targets, bool GlyphScale);

        private readonly struct TextPositionLists
        {
            public readonly float[] X;
            public readonly float[] Y;
            public readonly float[] Dx;
            public readonly float[] Dy;

            public TextPositionLists(float[] x, float[] y, float[] dx, float[] dy)
            {
                X = x;
                Y = y;
                Dx = dx;
                Dy = dy;
            }

            public static TextPositionLists None => default;

            /// <summary>
            /// True only when a position attribute carries more than one value.
            /// Single-valued positions are the ordinary absolute-placement case
            /// and never need per-character addressing, so complex shaping keeps
            /// its natural run geometry.
            /// </summary>
            public bool HasPerCharacterLists =>
                (X?.Length ?? 0) > 1 || (Y?.Length ?? 0) > 1 ||
                (Dx?.Length ?? 0) > 1 || (Dy?.Length ?? 0) > 1;
        }

        private readonly record struct TextStyle(
            string Family,
            float FontSize,
            int Weight,
            SKFontStyleSlant Slant,
            TextAnchor Anchor,
            float LetterSpacing,
            bool PreserveWhitespace,
            BaselineKind Baseline,
            float BaselineShift,
            float WordSpacing,
            TextDecoration Decorations,
            string Language)
        {
            public static TextStyle Default => new(null, DefaultFontSize, 400, SKFontStyleSlant.Upright, TextAnchor.Start, 0f, false, BaselineKind.Alphabetic, 0f, 0f, TextDecoration.None, null);
        }

        private sealed class TextPaintRun
        {
            public TextPaintRun(
                SvgElement element,
                InheritedStyle paintStyle,
                GlyphRun glyphRun,
                ViewportContext viewport,
                float x,
                float y,
                int chunk,
                bool applyOwnOpacity,
                float containerOpacity,
                TextDecoration decorations)
            {
                Element = element;
                PaintStyle = paintStyle;
                GlyphRun = glyphRun;
                Viewport = viewport;
                X = x;
                Y = y;
                Chunk = chunk;
                ApplyOwnOpacity = applyOwnOpacity;
                ContainerOpacity = containerOpacity;
                Decorations = decorations;
            }
            public SvgElement Element { get; }
            public InheritedStyle PaintStyle { get; }
            public GlyphRun GlyphRun { get; }
            public ViewportContext Viewport { get; }
            public float X { get; set; }
            public float Y { get; }
            public int Chunk { get; set; }
            public bool ApplyOwnOpacity { get; }
            public float ContainerOpacity { get; }
            public TextDecoration Decorations { get; }
            public float[] GlyphRotations { get; set; }
            public ushort[] PathGlyphIds { get; set; }
            public SKRotationScaleMatrix[] PathTransforms { get; set; }
            public SKRect PathBounds { get; set; }
            public float HorizontalScale { get; set; } = 1f;
            public float AdvanceScale { get; set; } = 1f;

            public float NaturalExtent
            {
                get
                {
                    PositionedGlyph[] glyphs = GlyphRun?.Glyphs;
                    if (glyphs == null || glyphs.Length == 0) return 0f;
                    PositionedGlyph last = glyphs[glyphs.Length - 1];
                    return last.X + Math.Max(0f, last.AdvanceX);
                }
            }

            public float AdvanceExtent => NaturalExtent * AdvanceScale;
        }

        private readonly record struct TextChunk(float StartX, TextAnchor Anchor);
    }
}
