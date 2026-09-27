using System;
using System.Collections.Generic;
using System.Globalization;
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
            ApplyParagraphDirection(runs, chunks, state);
            ApplyTextLengthAdjustments(runs, state.LengthAdjustments);
            ApplyTextAnchors(runs, chunks, _chunkLevels);
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
                    chunks.Add(TextChunk.At(textStyle, state.X));
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
                    if (tokenizer.Next(out _))
                    {
                        _report.RequireFallback(
                            "per-glyph SVG text rotation keyword must be the only value");
                        return null;
                    }
                    return null;
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

            return new TextLengthAdjustment(0, 0, targets, glyphScale, -1);
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
                adjustment with
                {
                    StartRun = startRun,
                    EndRun = runs.Count,
                    Chunk = state.CurrentChunk
                });
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

            if (textStyle.RightToLeft || textStyle.Frame != null ||
                ContainsBidirectionalText(element.TextContent))
            {
                _report.RequireFallback(
                    "bidirectional SVG text on textPath requires compatibility fallback");
                return;
            }
            if (state.CurrentChunk >= 0) state.PoisonedChunk = state.CurrentChunk;

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

            var typeface = SvgTypefaceResolver.Resolve(
                textStyle.Family, PaintedCharacters(text), textStyle.Language,
                textStyle.Weight, textStyle.Slant);
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
            run.LogicalText = text;
            run.Frame = textStyle.Frame;
            run.PerCharacterPositioned = positions.HasPerCharacterLists;
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

                TextDecoration underOrOver = run.Decorations &
                    (TextDecoration.Underline | TextDecoration.Overline);
                if (underOrOver != TextDecoration.None)
                    PaintTextDecorations(canvas, run, metrics, fillPaint, strokePaint, underOrOver);

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

                if ((run.Decorations & TextDecoration.LineThrough) != 0)
                {
                    PaintTextDecorations(
                        canvas, run, metrics, fillPaint, strokePaint, TextDecoration.LineThrough);
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
            SKPaint fillPaint,
            SKPaint strokePaint,
            TextDecoration bands)
        {
            for (int i = 0; i < 3; i++)
            {
                SKPaint paint = run.PaintStyle.PaintOrder.At(i) switch
                {
                    PaintPhase.Fill => fillPaint,
                    PaintPhase.Stroke => strokePaint,
                    _ => null
                };
                if (paint == null) continue;
                PaintDecorationBands(canvas, run, metrics, paint, bands);
            }
        }

        private void PaintDecorationBands(
            SKCanvas canvas,
            TextPaintRun run,
            SKFontMetrics metrics,
            SKPaint paint,
            TextDecoration bands)
        {
            float fontSize = run.GlyphRun.FontSize;
            float defaultThickness = Math.Max(1f, fontSize / 16f);
            bool rotated = run.GlyphRotations != null && run.PathTransforms != null;

            if ((bands & TextDecoration.Underline) != 0)
            {
                float thickness = metrics.UnderlineThickness ?? defaultThickness;
                if (!(thickness > 0f) || !SvgValues.IsFinite(thickness)) thickness = defaultThickness;
                float y = metrics.UnderlinePosition ?? fontSize * 0.1f;
                PaintDecorationBand(canvas, run, paint, y, thickness, rotated);
            }
            if ((bands & TextDecoration.Overline) != 0)
            {
                float thickness = metrics.UnderlineThickness ?? defaultThickness;
                if (!(thickness > 0f) || !SvgValues.IsFinite(thickness)) thickness = defaultThickness;
                PaintDecorationBand(canvas, run, paint, metrics.Ascent, thickness, rotated);
            }
            if ((bands & TextDecoration.LineThrough) != 0)
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

        private void ApplyTextAnchors(
            List<TextPaintRun> runs,
            List<TextChunk> chunks,
            IReadOnlyList<int> paragraphLevels)
        {
            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                TextChunk chunk = chunks[chunkIndex];
                float right = chunk.StartX;
                for (int i = 0; i < runs.Count; i++)
                {
                    if (runs[i].Chunk == chunkIndex) right = Math.Max(right, runs[i].X + runs[i].AdvanceExtent);
                }
                float width = Math.Max(0f, right - chunk.StartX);
                // The start and end of a text chunk follow the base direction the
                // paragraph resolved, so a right-to-left chunk anchors its start
                // edge on the right and its end edge on the left.
                TextAnchor anchor = chunk.Anchor;
                int level = chunkIndex < paragraphLevels.Count ? paragraphLevels[chunkIndex] : 0;
                if ((level & 1) == 1)
                {
                    anchor = anchor switch
                    {
                        TextAnchor.Start => TextAnchor.End,
                        TextAnchor.End => TextAnchor.Start,
                        _ => anchor
                    };
                }
                float shift = anchor == TextAnchor.Middle ? -width / 2f : anchor == TextAnchor.End ? -width : 0f;
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
            ValidateTextWritingMode(element);
            bool rightToLeft = ResolveTextDirection(element, inherited.RightToLeft);
            UnicodeBidi bidi = ResolveUnicodeBidi(element, inherited.Bidi);
            BidiFrame frame = ResolveBidiFrame(element, inherited.Frame, rightToLeft, bidi);
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
                ResolveLanguage(element, inherited.Language),
                rightToLeft,
                bidi,
                frame);
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
            if (keyword.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("revert", StringComparison.OrdinalIgnoreCase))
            {
                return TextDecoration.None;
            }
            if (keyword.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("unset", StringComparison.OrdinalIgnoreCase))
            {
                return inherited;
            }

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
        /// Reports a <c>direction</c> value the paragraph resolver cannot read.
        /// The value is inherited from any ancestor, so the bounded parse, which
        /// only inspects <c>text</c> and <c>tspan</c> attributes, cannot decide it
        /// at attribute sight; the text layout pass is the stage that resolves it.
        /// </summary>
        private void ValidateTextDirection(SvgElement element)
        {
            string raw = element.GetPresentationProperty("direction");
            if (string.IsNullOrWhiteSpace(raw)) return;
            var keyword = raw.AsSpan().Trim();
            if (IsCssWideKeyword(keyword)) return;
            if (keyword.Equals("ltr", StringComparison.OrdinalIgnoreCase)) return;
            if (keyword.Equals("rtl", StringComparison.OrdinalIgnoreCase)) return;
            _report.RequireFallback("SVG text direction value requires compatibility fallback");
        }

        /// <summary>
        /// Reports a vertical writing mode inherited from any ancestor. The
        /// bounded parse only inspects <c>text</c> and <c>tspan</c> attributes, so
        /// a <c>writing-mode</c> on an enclosing container reaches the text pass
        /// unfiltered. Vertical runs are laid out by a separate subsystem, so
        /// they are reported instead of painted horizontally.
        /// </summary>
        private void ValidateTextWritingMode(SvgElement element)
        {
            string raw = element.GetPresentationProperty("writing-mode");
            if (string.IsNullOrWhiteSpace(raw)) return;
            var keyword = raw.AsSpan().Trim();
            if (IsCssWideKeyword(keyword)) return;
            if (keyword.Equals("horizontal-tb", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("lr", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("lr-tb", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            _report.RequireFallback("SVG vertical writing mode requires compatibility fallback");
        }

        private bool ResolveTextDirection(SvgElement element, bool inherited)
        {
            string raw = element.GetPresentationProperty("direction");
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            var keyword = raw.AsSpan().Trim();
            if (IsCssWideKeyword(keyword)) return inherited;
            if (keyword.Equals("rtl", StringComparison.OrdinalIgnoreCase)) return true;
            if (keyword.Equals("ltr", StringComparison.OrdinalIgnoreCase)) return false;
            return inherited;
        }

        private UnicodeBidi ResolveUnicodeBidi(SvgElement element, UnicodeBidi inherited)
        {
            string raw = element.GetPresentationProperty("unicode-bidi");
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            var keyword = raw.AsSpan().Trim();
            if (IsCssWideKeyword(keyword)) return inherited;
            if (keyword.Equals("normal", StringComparison.OrdinalIgnoreCase)) return UnicodeBidi.Normal;
            if (keyword.Equals("plaintext", StringComparison.OrdinalIgnoreCase)) return UnicodeBidi.Plaintext;
            if (keyword.Equals("embed", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("inline", StringComparison.OrdinalIgnoreCase))
            {
                return UnicodeBidi.Embed;
            }
            if (keyword.Equals("bidi-override", StringComparison.OrdinalIgnoreCase)) return UnicodeBidi.Override;
            return inherited;
        }

        /// <summary>
        /// Builds the embedding frame an element contributes to the paragraph.
        /// Only <c>embed</c> and <c>bidi-override</c> open one; every other value
        /// passes the inherited frame through unchanged, so a plain left-to-right
        /// document allocates no frame at all. <c>isolate</c> deliberately opens
        /// no frame: the isolating run sequence rules are a separate stage this
        /// pass does not compute, and a frame would silently claim to.
        /// </summary>
        private BidiFrame ResolveBidiFrame(
            SvgElement element,
            BidiFrame inherited,
            bool rightToLeft,
            UnicodeBidi bidi)
        {
            if (bidi is not (UnicodeBidi.Embed or UnicodeBidi.Override)) return inherited;
            string raw = element.GetPresentationProperty("unicode-bidi");
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            var keyword = raw.AsSpan().Trim();
            bool embed = keyword.Equals("embed", StringComparison.OrdinalIgnoreCase) ||
                         keyword.Equals("inline", StringComparison.OrdinalIgnoreCase);
            bool over = keyword.Equals("bidi-override", StringComparison.OrdinalIgnoreCase);
            if (!embed && !over) return inherited;
            return new BidiFrame(inherited, rightToLeft, over);
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

        private const int MaxBidiParagraphChars = 1024;
        private const int MaxBidiEmbeddingDepth = 125;

        private enum UnicodeBidi : byte { Normal, Embed, Plaintext, Override }

        /// <summary>
        /// One element's contribution to the paragraph's embedding stack. A
        /// <c>unicode-bidi</c> embedding or override opens a frame whose parent
        /// is the frame it inherits, so the paragraph pass can rebuild the stack
        /// the run was authored under. A document that never opens a frame keeps
        /// a null style frame and skips the stack entirely.
        /// </summary>
        private sealed class BidiFrame
        {
            public BidiFrame(BidiFrame parent, bool rightToLeft, bool over)
            {
                Parent = parent;
                RightToLeft = rightToLeft;
                Override = over;
            }

            public BidiFrame Parent { get; }
            public bool RightToLeft { get; }
            public bool Override { get; }
            public int Depth => Parent == null ? 1 : Parent.Depth + 1;
        }

        private enum BidiClass : byte
        {
            LeftToRight,
            RightToLeft,
            ArabicLetter,
            ArabicNumber,
            EuropeanNumber,
            EuropeanSeparator,
            EuropeanTerminator,
            CommonSeparator,
            Neutral,
            Whitespace,
            NonSpacingMark,
            BoundaryNeutral,
            EmbeddingLeft,
            EmbeddingRight,
            OverrideLeft,
            OverrideRight,
            PopFormatting,
            IsolateInitiator,
            IsolateTerminator
        }

        /// <summary>
        /// Resolves the visual order of one text chunk, the unit a paragraph is
        /// laid out in. Runs are shaped in logical order, so the bidirectional
        /// algorithm runs here over the concatenated logical characters of the
        /// chunk and the resulting level run is cut back into the painted runs:
        /// a run is split wherever its characters resolve to different levels or
        /// to different strong directions, and the pieces are emitted in visual
        /// order from the chunk's start edge.
        /// <para>
        /// The shaper behind this path chooses its own direction per buffer from
        /// the first strong character, so a level run is laid out correctly only
        /// when that guess agrees with the resolved level. A run the guess
        /// disagrees with is mirrored by reversing its glyph run, which is what a
        /// browser does when it hands the buffer the other direction. A run that
        /// has to be mirrored is mirrored as a whole glyph sequence, so no
        /// character-to-glyph correspondence is needed; a run that has to be
        /// split does need it, because the split points are character positions.
        /// </para>
        /// </summary>
        private readonly List<int> _chunkLevels = new List<int>();

        private void ApplyParagraphDirection(
            List<TextPaintRun> runs,
            List<TextChunk> chunks,
            TextLayoutState state)
        {
            List<int> levels = _chunkLevels;
            levels.Clear();
            for (int chunkIndex = 0; chunkIndex < chunks.Count; chunkIndex++)
            {
                CheckDeadline();
                levels.Add(ResolveChunkParagraph(runs, chunks, state, chunkIndex));
            }
        }

        private readonly List<TextPaintRun> _chunkRuns = new List<TextPaintRun>();
        private readonly List<BidiSegment> _chunkSegments = new List<BidiSegment>();
        private readonly List<TextPaintRun> _chunkVisual = new List<TextPaintRun>();

        private int ResolveChunkParagraph(
            List<TextPaintRun> runs,
            List<TextChunk> chunks,
            TextLayoutState state,
            int chunkIndex)
        {
            TextChunk chunk = chunks[chunkIndex];
            List<TextPaintRun> members = _chunkRuns;
            members.Clear();
            for (int i = 0; i < runs.Count; i++)
            {
                if (runs[i].Chunk == chunkIndex) members.Add(runs[i]);
            }
            if (members.Count == 0) return 0;

            int total = 0;
            bool framed = false;
            for (int i = 0; i < members.Count; i++)
            {
                total += members[i].LogicalText?.Length ?? 0;
                if (members[i].Frame != null) framed = true;
            }
            if (total == 0) return 0;

            byte paragraphLevel = chunk.Bidi == UnicodeBidi.Plaintext
                ? ResolvePlaintextLevel(members)
                : (byte)(chunk.RightToLeft ? 1 : 0);

            if (!framed && paragraphLevel == 0 && !ContainsBidirectionalText(members))
                return paragraphLevel;

            if (total > MaxBidiParagraphChars)
            {
                _report.RequireFallback("bidirectional SVG text requires compatibility fallback");
                return paragraphLevel;
            }

            if (!TryResolveParagraphLevels(
                    members, paragraphLevel,
                    out byte[] levels, out int[] origin, out int[] owner, out int[] runStart,
                    out bool[] shaperRight, out int kept))
            {
                _report.RequireFallback("bidirectional SVG text requires compatibility fallback");
                return paragraphLevel;
            }

            int[] order = VisualOrder(levels, paragraphLevel);
            bool identity = kept == total;
            for (int i = 0; i < kept && identity; i++)
            {
                // A chunk is already laid out when the resolved order is the
                // logical one, no character is dropped from it, and every
                // character sits at a level that agrees with the direction the
                // shaper chose for the run it came from.
                if (order[i] != i) identity = false;
                else if (shaperRight[owner[i]] != ((levels[i] & 1) == 1)) identity = false;
            }
            if (identity) return paragraphLevel;

            if (state.PoisonedChunk == chunkIndex)
            {
                _report.RequireFallback(
                    "bidirectional SVG text adjacent to a textPath requires compatibility fallback");
                return paragraphLevel;
            }
            for (int i = 0; i < members.Count; i++)
            {
                if (members[i].GlyphRotations != null || members[i].PerCharacterPositioned)
                {
                    _report.RequireFallback(
                        "bidirectional SVG text cannot be combined with per-glyph positioning");
                    return paragraphLevel;
                }
            }
            for (int i = 0; i < state.LengthAdjustments.Count; i++)
            {
                if (state.LengthAdjustments[i].Chunk != chunkIndex) continue;
                _report.RequireFallback(
                    "bidirectional SVG text with textLength requires compatibility fallback");
                return paragraphLevel;
            }

            BuildVisualSegments(members, levels, order, origin, owner, runStart, _chunkSegments);
            List<TextPaintRun> visual = _chunkVisual;
            visual.Clear();
            float cursor = chunk.StartX;
            for (int i = 0; i < _chunkSegments.Count; i++)
            {
                BidiSegment segment = _chunkSegments[i];
                TextPaintRun source = members[segment.Run];
                if (segment.Text.Length == source.LogicalText.Length && !segment.Reversed)
                {
                    source.X = cursor;
                    cursor += source.AdvanceExtent;
                    visual.Add(source);
                    continue;
                }
                TextPaintRun piece = SliceRun(source, segment, cursor);
                if (piece == null)
                {
                    _report.RequireFallback(
                        "bidirectional SVG text run cannot be split at a character boundary");
                    return paragraphLevel;
                }
                visual.Add(piece);
                cursor += piece.AdvanceExtent;
            }
            for (int i = 0; i < members.Count; i++) runs.Remove(members[i]);
            for (int i = 0; i < visual.Count; i++) runs.Add(visual[i]);
            return paragraphLevel;
        }

        private static bool ContainsBidirectionalText(string text)
        {
            if (text == null) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is (>= '\u0590' and <= '\u08FF') or (>= '\uFB1D' and <= '\uFDFF') or
                        (>= '\uFE70' and <= '\uFEFE') or '\u200E' or '\u200F' or '\u061C' or
                        (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069'))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// The characters a run actually paints. The explicit directional
        /// controls carry no glyph, so a typeface is chosen for the run without
        /// them; a run made of nothing else keeps its own text so the resolver
        /// still sees something to answer with.
        /// </summary>
        private static string PaintedCharacters(string text)
        {
            int controls = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')) controls++;
            }
            if (controls == 0 || controls == text.Length) return text;
            var builder = new StringBuilder(text.Length - controls);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069')) continue;
                builder.Append(c);
            }
            return builder.ToString();
        }

        private static bool ContainsBidirectionalText(List<TextPaintRun> members)
        {
            for (int i = 0; i < members.Count; i++)
            {
                string text = members[i].LogicalText;
                if (text == null) continue;
                for (int j = 0; j < text.Length; j++)
                {
                    char c = text[j];
                    if (c is (>= '\u0590' and <= '\u08FF') or (>= '\uFB1D' and <= '\uFDFF') or
                            (>= '\uFE70' and <= '\uFEFE') or '\u200E' or '\u200F' or '\u061C' or
                            (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069'))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static byte ResolvePlaintextLevel(List<TextPaintRun> members)
        {
            for (int i = 0; i < members.Count; i++)
            {
                string text = members[i].LogicalText;
                if (text == null) continue;
                for (int j = 0; j < text.Length; j++)
                {
                    if (!TryClassifyBidi(text[j], out BidiClass type)) continue;
                    if (type == BidiClass.LeftToRight) return 0;
                    if (type is BidiClass.RightToLeft or BidiClass.ArabicLetter) return 1;
                }
            }
            return 0;
        }

        /// <summary>
        /// One contiguous piece of the visual line: a character range of one
        /// source run, in the order the algorithm placed it. The range is
        /// contiguous in the run's logical text, so a piece is shaped from that
        /// substring rather than cut out of the run the shaper already produced,
        /// which is the granularity a browser shapes a directional run at.
        /// </summary>
        private readonly struct BidiSegment
        {
            public BidiSegment(int run, string text, bool reversed, bool levelOdd)
            {
                Run = run;
                Text = text;
                Reversed = reversed;
                LevelOdd = levelOdd;
            }

            public int Run { get; }
            public string Text { get; }
            public bool Reversed { get; }
            public bool LevelOdd { get; }
        }

        /// <summary>
        /// Cuts the resolved level run back into the painted runs. The order the
        /// algorithm produces is already visual, so each piece is read straight
        /// out of it. A piece is cut where the resolved level changes, where the
        /// source run changes, and where the level run stops walking its source
        /// characters monotonically. The piece keeps the characters the
        /// algorithm kept, in the order the source run authored them, so an
        /// explicit control between two of them is not read back as text.
        /// </summary>
        private void BuildVisualSegments(
            List<TextPaintRun> members,
            byte[] levels,
            int[] order,
            int[] origin,
            int[] owner,
            int[] runStart,
            List<BidiSegment> segments)
        {
            segments.Clear();
            int count = levels.Length;
            if (count == 0) return;
            string[] sources = new string[members.Count];
            for (int run = 0; run < members.Count; run++) sources[run] = members[run].LogicalText;
            var picked = new List<int>();
            int position = 0;
            while (position < count)
            {
                int first = order[position];
                byte level = levels[first];
                bool reversed = (level & 1) == 1;
                int run = owner[first];
                picked.Clear();
                picked.Add(origin[first]);
                int end = position + 1;
                while (end < count)
                {
                    int next = order[end];
                    if (levels[next] != level) break;
                    if (owner[next] != run) break;
                    int previous = order[end - 1];
                    if (origin[next] - origin[previous] != (reversed ? -1 : 1)) break;
                    picked.Add(origin[next]);
                    end++;
                }
                picked.Sort();
                string source = sources[run];
                int baseIndex = runStart[run];
                var builder = new StringBuilder(picked.Count);
                for (int i = 0; i < picked.Count; i++) builder.Append(source[picked[i] - baseIndex]);
                segments.Add(new BidiSegment(run, builder.ToString(), reversed, reversed));
                position = end;
            }
        }

        /// <summary>
        /// UAX 9 rule L2 over the resolved levels: from the highest level down to
        /// the lowest odd level, reverse every contiguous range at or above it.
        /// The paragraph embedding level counts as a level on the line even when
        /// no character resolves to it, which is what lets a left-to-right island
        /// inside a right-to-left paragraph move to the other side of its
        /// neighbours.
        /// </summary>
        private static int[] VisualOrder(byte[] levels, byte paragraphLevel)
        {
            int count = levels.Length;
            var order = new int[count];
            int highest = 0;
            int lowestOdd = (paragraphLevel & 1) == 1 ? paragraphLevel : int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                order[i] = i;
                if (levels[i] > highest) highest = levels[i];
                if ((levels[i] & 1) == 1 && levels[i] < lowestOdd) lowestOdd = levels[i];
            }
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
            return order;
        }

        /// <summary>
        /// Produces the run that paints one piece, in visual order, from the
        /// chunk cursor. The advances are re-accumulated from the source
        /// positions rather than copied from the stored advance, so the letter
        /// and word spacing already folded into those positions survives the
        /// <summary>
        /// Produces the run that paints one piece, in visual order, from the
        /// chunk cursor. The piece is shaped from its own characters, which is
        /// the granularity a browser shapes a directional run at, so the advance
        /// the run carries is the one the visual order implies. The shaper
        /// derives its direction from the first strong character of the buffer
        /// it is given, and a piece whose level disagrees with that direction is
        /// permuted: the shaper lays a right-to-left buffer out with the advances
        /// its characters have in logical order and reverses the result, so
        /// reversing the glyph sequence is the same operation the shaper would
        /// have done for the other direction.
        /// </summary>
        private TextPaintRun SliceRun(
            TextPaintRun source,
            BidiSegment segment,
            float cursor)
        {
            GlyphRun original = source.GlyphRun;
            if (original?.Typeface == null || segment.Text == null) return null;
            if (segment.Text.Length == 0) return null;

            GlyphRun shaped = SkiaFontService.ShapeWithTypeface(
                segment.Text, original.Typeface, original.FontSize);
            if (shaped?.Glyphs == null || shaped.Glyphs.Length == 0) return null;
            if (ShapedRightToLeft(segment.Text) != segment.LevelOdd) PermuteGlyphs(shaped);

            return new TextPaintRun(
                source.Element, source.PaintStyle, shaped, source.Viewport, cursor, source.Y,
                source.Chunk, source.ApplyOwnOpacity, source.ContainerOpacity, source.Decorations)
            {
                Frame = source.Frame,
                LogicalText = source.LogicalText
            };
        }

        /// <summary>
        /// Reverses a shaped glyph run in place, keeping every glyph its own
        /// advance and mirroring the positions so the run still measures the
        /// same.
        /// </summary>
        private static void PermuteGlyphs(GlyphRun run)
        {
            PositionedGlyph[] glyphs = run.Glyphs;
            int count = glyphs.Length;
            var reversed = new PositionedGlyph[count];
            for (int i = 0; i < count; i++)
            {
                int at = count - 1 - i;
                reversed[i] = glyphs[at];
                reversed[i].X = run.Width - glyphs[at].X - RunAdvance(run, at);
            }
            run.Glyphs = reversed;
        }

        /// </summary>
        private static float RunAdvance(GlyphRun glyphs, int index)
        {
            float next = index + 1 < glyphs.Glyphs.Length
                ? glyphs.Glyphs[index + 1].X
                : glyphs.Width;
            float advance = next - glyphs.Glyphs[index].X;
            return SvgValues.IsFinite(advance) && advance > 0f ? advance : 0f;
        }

        private static byte NextEmbeddingLevel(byte current, bool rightToLeft)
        {
            for (int candidate = current + 1; candidate <= MaxBidiEmbeddingDepth; candidate++)
            {
                if (((candidate & 1) == 1) == rightToLeft) return (byte)candidate;
            }
            return MaxBidiEmbeddingDepth;
        }

        private const byte MaxIsolateDepth = 125;

        private bool TryResolveParagraphLevels(
            List<TextPaintRun> members,
            byte paragraphLevel,
            out byte[] levels,
            out int[] origin,
            out int[] owner,
            out int[] runStart,
            out bool[] shaperRight,
            out int keptCount)
        {
            levels = null;
            origin = null;
            owner = null;
            runStart = null;
            shaperRight = null;
            keptCount = 0;
            int count = 0;
            for (int i = 0; i < members.Count; i++) count += members[i].LogicalText?.Length ?? 0;
            if (count == 0) return false;

            var text = new char[count];
            var runOf = new int[count];
            var starts = new int[members.Count];
            int cursor = 0;
            for (int run = 0; run < members.Count; run++)
            {
                string logical = members[run].LogicalText;
                starts[run] = cursor;
                if (logical == null) continue;
                for (int i = 0; i < logical.Length; i++)
                {
                    text[cursor + i] = logical[i];
                    runOf[cursor + i] = run;
                }
                cursor += logical.Length;
            }

            var types = new BidiClass[count];
            var resolved = new byte[count];
            var keptOrigin = new int[count];
            var keptOwner = new int[count];
            var stackLevel = new byte[MaxBidiEmbeddingDepth + 1];
            var stackOverride = new BidiClass[MaxBidiEmbeddingDepth + 1];
            RebuildEmbeddingStack(null, paragraphLevel, stackLevel, stackOverride, out int depth, out int overflow);
            int currentRun = 0;
            BidiFrame currentFrame = null;
            BidiClass paragraphDirection = (paragraphLevel & 1) == 1
                ? BidiClass.RightToLeft
                : BidiClass.LeftToRight;
            int kept = 0;

            for (int i = 0; i < count; i++)
            {
                if (runOf[i] != currentRun)
                {
                    currentRun = runOf[i];
                    BidiFrame frame = members[currentRun].Frame;
                    // Only an element that opens or closes an embedding resets
                    // the stack; a run boundary inside the same embedding keeps
                    // the explicit controls the text content opened.
                    if (!ReferenceEquals(frame, currentFrame))
                    {
                        currentFrame = frame;
                        RebuildEmbeddingStack(
                            frame, paragraphLevel,
                            stackLevel, stackOverride, out depth, out overflow);
                    }
                }
                if (!TryClassifyBidi(text[i], out BidiClass type)) return false;
                if (type is BidiClass.IsolateInitiator or BidiClass.IsolateTerminator) return false;

                if (type is BidiClass.EmbeddingLeft or BidiClass.EmbeddingRight or
                    BidiClass.OverrideLeft or BidiClass.OverrideRight)
                {
                    bool rightToLeft = type is BidiClass.EmbeddingRight or BidiClass.OverrideRight;
                    bool over = type is BidiClass.OverrideLeft or BidiClass.OverrideRight;
                    if (depth < MaxBidiEmbeddingDepth)
                    {
                        depth++;
                        stackLevel[depth] = NextEmbeddingLevel(stackLevel[depth - 1], rightToLeft);
                        stackOverride[depth] = over
                            ? (rightToLeft ? BidiClass.RightToLeft : BidiClass.LeftToRight)
                            : BidiClass.Neutral;
                    }
                    else
                    {
                        overflow++;
                    }
                    continue;
                }
                if (type == BidiClass.PopFormatting)
                {
                    if (overflow > 0) overflow--;
                    else if (depth > 0) depth--;
                    continue;
                }
                if (type == BidiClass.NonSpacingMark)
                {
                    type = kept > 0 ? types[kept - 1] : paragraphDirection;
                }
                types[kept] = stackOverride[depth] != BidiClass.Neutral
                    ? stackOverride[depth]
                    : type;
                resolved[kept] = stackLevel[depth];
                keptOrigin[kept] = i;
                keptOwner[kept] = runOf[i];
                kept++;
            }
            if (kept == 0) return false;

            ResolveWeakTypes(types, kept);
            ResolveNeutralTypes(types, resolved, kept, paragraphDirection, paragraphLevel);
            ResolveImplicitLevels(types, resolved, kept);
            ResetTrailingWhitespace(types, resolved, kept, paragraphLevel);

            levels = new byte[kept];
            origin = new int[kept];
            owner = new int[kept];
            runStart = starts;
            shaperRight = new bool[members.Count];
            for (int i = 0; i < kept; i++)
            {
                levels[i] = resolved[i];
                origin[i] = keptOrigin[i];
                owner[i] = keptOwner[i];
            }
            for (int run = 0; run < members.Count; run++)
            {
                shaperRight[run] = ShapedRightToLeft(members[run].LogicalText);
            }
            keptCount = kept;
            return true;
        }

        /// <summary>
        /// The direction the shaper chose for a run. The shaper behind this path
        /// derives it from the first strong character of the buffer it is given,
        /// so the paragraph reproduces the same choice and only mirrors a piece
        /// whose resolved level disagrees with it.
        /// </summary>
        private static bool ShapedRightToLeft(string text)
        {
            if (text == null) return false;
            for (int i = 0; i < text.Length; i++)
            {
                if (!TryClassifyBidi(text[i], out BidiClass type)) continue;
                if (type is BidiClass.LeftToRight) return false;
                if (type is BidiClass.RightToLeft or BidiClass.ArabicLetter) return true;
            }
            return false;
        }

        /// <summary>
        /// Rebuilds the element embedding stack for a run. Every
        /// <c>unicode-bidi</c> embedding or override an element opens is replayed
        /// as the explicit embedding the algorithm would have seen, so a run
        /// authored inside a nested element starts from the same stack the
        /// document declared.
        /// </summary>
        private static void RebuildEmbeddingStack(
            BidiFrame frame,
            byte paragraphLevel,
            byte[] stackLevel,
            BidiClass[] stackOverride,
            out int depth,
            out int overflow)
        {
            var chain = new BidiFrame[MaxBidiEmbeddingDepth + 1];
            int length = 0;
            for (BidiFrame at = frame; at != null && length < MaxBidiEmbeddingDepth; at = at.Parent)
            {
                chain[length++] = at;
            }
            depth = 0;
            overflow = 0;
            stackLevel[0] = paragraphLevel;
            stackOverride[0] = BidiClass.Neutral;
            for (int i = length - 1; i >= 0; i--)
            {
                BidiFrame at = chain[i];
                if (depth < MaxBidiEmbeddingDepth)
                {
                    depth++;
                    stackLevel[depth] = NextEmbeddingLevel(stackLevel[depth - 1], at.RightToLeft);
                    stackOverride[depth] = at.Override
                        ? (at.RightToLeft ? BidiClass.RightToLeft : BidiClass.LeftToRight)
                        : BidiClass.Neutral;
                }
                else
                {
                    overflow++;
                }
            }
        }

        /// <summary>
        /// UAX 9 rules W2 to W7.
        /// </summary>
        private static void ResolveWeakTypes(BidiClass[] types, int count)
        {
            BidiClass lastStrong = BidiClass.Neutral;
            for (int i = 0; i < count; i++)
            {
                if (types[i] is BidiClass.LeftToRight or BidiClass.RightToLeft or BidiClass.ArabicLetter)
                {
                    lastStrong = types[i];
                }
                else if (types[i] == BidiClass.EuropeanNumber && lastStrong == BidiClass.ArabicLetter)
                {
                    types[i] = BidiClass.ArabicNumber;
                }
            }

            for (int i = 1; i + 1 < count; i++)
            {
                if (types[i] != BidiClass.EuropeanSeparator && types[i] != BidiClass.CommonSeparator)
                {
                    continue;
                }
                bool european = types[i - 1] == BidiClass.EuropeanNumber &&
                                types[i + 1] == BidiClass.EuropeanNumber;
                bool arabic = types[i - 1] == BidiClass.ArabicNumber &&
                              types[i + 1] == BidiClass.ArabicNumber;
                if (european) types[i] = BidiClass.EuropeanNumber;
                else if (arabic) types[i] = BidiClass.ArabicNumber;
            }

            int position = 0;
            while (position < count)
            {
                if (types[position] != BidiClass.EuropeanTerminator) { position++; continue; }
                int start = position;
                while (position < count && types[position] == BidiClass.EuropeanTerminator) position++;
                bool joins = (start > 0 && types[start - 1] == BidiClass.EuropeanNumber) ||
                             (position < count && types[position] == BidiClass.EuropeanNumber);
                if (!joins) continue;
                for (int i = start; i < position; i++) types[i] = BidiClass.EuropeanNumber;
            }

            position = 0;
            while (position < count)
            {
                if (types[position] != BidiClass.EuropeanTerminator &&
                    types[position] != BidiClass.EuropeanSeparator)
                {
                    position++;
                    continue;
                }
                int start = position;
                while (position < count &&
                       (types[position] == BidiClass.EuropeanTerminator ||
                        types[position] == BidiClass.EuropeanSeparator))
                {
                    position++;
                }
                bool adjacent = (start > 0 && types[start - 1] == BidiClass.EuropeanNumber) ||
                                (position < count && types[position] == BidiClass.EuropeanNumber);
                if (!adjacent) continue;
                for (int i = start; i < position; i++) types[i] = BidiClass.EuropeanNumber;
            }

            for (int i = 0; i < count; i++)
            {
                if (types[i] == BidiClass.ArabicLetter) types[i] = BidiClass.RightToLeft;
                else if (types[i] is BidiClass.EuropeanSeparator or BidiClass.CommonSeparator or
                         BidiClass.EuropeanTerminator)
                {
                    types[i] = BidiClass.Neutral;
                }
            }

            lastStrong = BidiClass.Neutral;
            for (int i = 0; i < count; i++)
            {
                if (types[i] is BidiClass.LeftToRight or BidiClass.RightToLeft)
                {
                    lastStrong = types[i];
                }
                else if (types[i] == BidiClass.EuropeanNumber && lastStrong == BidiClass.LeftToRight)
                {
                    types[i] = BidiClass.LeftToRight;
                }
            }
        }

        /// <summary>
        /// UAX 9 rules N1 and N2, resolved per run of a constant embedding level.
        /// A run has one embedding direction, so the neutrals at its edges take
        /// that direction whenever the strong text on their other side does not
        /// already agree. The direction a run inherits from its neighbour is the
        /// parity of the higher of the neighbour's embedding level and the
        /// paragraph level, which is rule X10; without it a left-to-right island
        /// inside a right-to-left embedding would drag the neutrals around it to
        /// the wrong side of the line.
        /// </summary>
        private static void ResolveNeutralTypes(
            BidiClass[] types,
            byte[] levels,
            int count,
            BidiClass paragraphDirection,
            byte paragraphLevel)
        {
            int runStart = 0;
            while (runStart < count)
            {
                int runEnd = runStart + 1;
                while (runEnd < count && levels[runEnd] == levels[runStart]) runEnd++;
                BidiClass runDirection = (levels[runStart] & 1) == 1
                    ? BidiClass.RightToLeft
                    : BidiClass.LeftToRight;
                BidiClass start = runStart == 0
                    ? paragraphDirection
                    : BoundaryDirection(levels[runStart - 1], paragraphLevel);
                BidiClass end = runEnd == count
                    ? paragraphDirection
                    : BoundaryDirection(levels[runEnd], paragraphLevel);
                ResolveNeutralRun(types, levels, runStart, runEnd, start, end, runDirection);
                runStart = runEnd;
            }
        }

        /// <summary>
        /// UAX 9 rule X10: the direction a run inherits across a level boundary
        /// is the parity of the higher of the neighbour's embedding level and the
        /// paragraph embedding level.
        /// </summary>
        private static BidiClass BoundaryDirection(byte neighbourLevel, byte paragraphLevel)
        {
            byte level = neighbourLevel > paragraphLevel ? neighbourLevel : paragraphLevel;
            return (level & 1) == 1 ? BidiClass.RightToLeft : BidiClass.LeftToRight;
        }

        private static void ResolveNeutralRun(
            BidiClass[] types,
            byte[] levels,
            int runStart,
            int runEnd,
            BidiClass start,
            BidiClass end,
            BidiClass runDirection)
        {
            int position = runStart;
            while (position < runEnd)
            {
                if (!IsNeutral(types[position])) { position++; continue; }
                int first = position;
                while (position < runEnd && IsNeutral(types[position])) position++;
                BidiClass before = first == runStart
                    ? start
                    : StrongOf(types[first - 1]);
                if (before == BidiClass.Neutral) before = start;
                BidiClass after = position == runEnd ? end : StrongOf(types[position]);
                if (after == BidiClass.Neutral) after = end;
                BidiClass resolved = before == after ? before : runDirection;
                for (int i = first; i < position; i++) types[i] = resolved;
            }
        }

        private static BidiClass StrongOf(BidiClass type) => type switch
        {
            BidiClass.LeftToRight => BidiClass.LeftToRight,
            BidiClass.RightToLeft => BidiClass.RightToLeft,
            BidiClass.EuropeanNumber or BidiClass.ArabicNumber => BidiClass.RightToLeft,
            _ => BidiClass.Neutral
        };

        private static bool IsNeutral(BidiClass type) =>
            type is BidiClass.Neutral or BidiClass.Whitespace;

        /// <summary>
        /// UAX 9 rules I1 and I2.
        /// </summary>
        private static void ResolveImplicitLevels(BidiClass[] types, byte[] levels, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if ((levels[i] & 1) == 0)
                {
                    if (types[i] == BidiClass.RightToLeft) levels[i]++;
                    else if (types[i] is BidiClass.EuropeanNumber or BidiClass.ArabicNumber) levels[i] += 2;
                }
                else if (types[i] is BidiClass.LeftToRight or BidiClass.EuropeanNumber or
                         BidiClass.ArabicNumber)
                {
                    levels[i]++;
                }
            }
        }

        /// <summary>
        /// UAX 9 rule L1: whitespace at the end of the line returns to the
        /// paragraph embedding level.
        /// </summary>
        private static void ResetTrailingWhitespace(
            BidiClass[] types,
            byte[] levels,
            int count,
            byte paragraphLevel)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                if (types[i] != BidiClass.Whitespace) break;
                levels[i] = paragraphLevel;
            }
        }

        /// <summary>
        /// Classifies one character for the bidirectional algorithm. Every
        /// character a bidi-relevant paragraph can carry is classified here;
        /// anything the tables below do not positively identify, which includes
        /// every code point outside the basic multilingual plane and every
        /// paired bracket, is refused so the paragraph is reported instead of
        /// laid out from a guessed class.
        /// </summary>
        private static bool TryClassifyBidi(char c, out BidiClass type)
        {
            type = BidiClass.Neutral;
            switch (c)
            {
                case '\u202A': type = BidiClass.EmbeddingLeft; return true;
                case '\u202B': type = BidiClass.EmbeddingRight; return true;
                case '\u202D': type = BidiClass.OverrideLeft; return true;
                case '\u202E': type = BidiClass.OverrideRight; return true;
                case '\u202C': type = BidiClass.PopFormatting; return true;
                case '\u2066':
                case '\u2067':
                case '\u2068': type = BidiClass.IsolateInitiator; return true;
                case '\u2069': type = BidiClass.IsolateTerminator; return true;
                case '\u200E': type = BidiClass.LeftToRight; return true;
                case '\u200F': type = BidiClass.RightToLeft; return true;
                case '\u061C': type = BidiClass.ArabicLetter; return true;
            }

            if (c > '\uFFFD') return false;
            if (IsPairedBracket(c)) return false;
            if (IsArabicNumber(c)) { type = BidiClass.ArabicNumber; return true; }
            if (IsRightToLeftBlock(c))
            {
                // A point or a haraka inside a right-to-left word takes the type
                // of the character it follows, so the marks the block mixes into
                // its letters are recognised before the letters are.
                switch (CharUnicodeInfo.GetUnicodeCategory(c))
                {
                    case UnicodeCategory.NonSpacingMark:
                    case UnicodeCategory.EnclosingMark:
                    case UnicodeCategory.SpacingCombiningMark:
                        type = BidiClass.NonSpacingMark;
                        return true;
                }
                if (IsArabicBlock(c)) { type = BidiClass.ArabicLetter; return true; }
                if (IsHebrewBlock(c)) { type = BidiClass.RightToLeft; return true; }
                return false;
            }

            if (c is >= '0' and <= '9') { type = BidiClass.EuropeanNumber; return true; }
            if (c is '+' or '-') { type = BidiClass.EuropeanSeparator; return true; }
            if (c is '#' or '$') { type = BidiClass.EuropeanTerminator; return true; }
            if (c is ',' or '.' or ':' or '/' or '\u00A0') { type = BidiClass.CommonSeparator; return true; }
            if (c is ' ' or '\t' or '\n' or '\r' or '\u000B' or '\u000C' or '\u0085' or
                      '\u2028' or '\u2029' or '\u2000' or '\u2001' or '\u2002' or '\u2003' or
                      '\u2004' or '\u2005' or '\u2006' or '\u2007' or '\u2008' or '\u2009' or
                      '\u200A' or '\u205F' or '\u3000')
            {
                type = BidiClass.Whitespace;
                return true;
            }
            if (c is '\u00B2' or '\u00B3' or '\u00B9' or '\u2070' or '\u2074' or '\u2075' or
                      '\u2076' or '\u2077' or '\u2078' or '\u2079' or '\u2080' or '\u2081' or
                      '\u2082' or '\u2083' or '\u2084' or '\u2085' or '\u2086' or '\u2087' or
                      '\u2088' or '\u2089')
            {
                type = BidiClass.EuropeanNumber;
                return true;
            }
            if (c is >= '\uFF10' and <= '\uFF19') { type = BidiClass.EuropeanNumber; return true; }
            if (c is >= '\u06F0' and <= '\u06F9') { type = BidiClass.EuropeanNumber; return true; }
            if (c is '\u00AD' or '\uFEFF' or '\u2060' or '\u180E' or '\u200B' or '\u200C' or '\u200D')
            {
                type = BidiClass.BoundaryNeutral;
                return true;
            }

            switch (CharUnicodeInfo.GetUnicodeCategory(c))
            {
                case UnicodeCategory.UppercaseLetter:
                case UnicodeCategory.LowercaseLetter:
                case UnicodeCategory.TitlecaseLetter:
                case UnicodeCategory.ModifierLetter:
                case UnicodeCategory.OtherLetter:
                    type = BidiClass.LeftToRight;
                    return true;
                case UnicodeCategory.NonSpacingMark:
                case UnicodeCategory.EnclosingMark:
                case UnicodeCategory.SpacingCombiningMark:
                    type = BidiClass.NonSpacingMark;
                    return true;
                case UnicodeCategory.DecimalDigitNumber:
                    type = BidiClass.LeftToRight;
                    return true;
                default:
                    type = BidiClass.Neutral;
                    return true;
            }
        }

        private static bool IsPairedBracket(char c) =>
            c is '(' or ')' or '[' or ']' or '{' or '}' or '\u27E6' or '\u27E7' or
                  '\u27E8' or '\u27E9' or '\u27EA' or '\u27EB' or '\u27EC' or '\u27ED' or
                  '\u27EE' or '\u27EF' or '\u2983' or '\u2984' or '\u2985' or '\u2986' or
                  '\u2987' or '\u2988' or '\u2989' or '\u298A' or '\u298B' or '\u298C' or
                  '\u298D' or '\u298E' or '\u298F' or '\u2990' or '\u2991' or '\u2992' or
                  '\u2993' or '\u2994' or '\u2995' or '\u2996' or '\u2997' or '\u2998' or
                  '\u29D8' or '\u29D9' or '\u29DA' or '\u29DB' or '\u2E22' or '\u2E23' or
                  '\u2E24' or '\u2E25' or '\u2E26' or '\u2E27' or '\u2E28' or
                  '\u2045' or '\u2046' or '\u207D' or '\u207E' or '\u208D' or '\u208E' or
                  '\u3008' or '\u3009' or '\u300A' or '\u300B' or '\u300C' or '\u300D' or
                  '\u3010' or '\u3011' or '\u3014' or '\u3015' or '\u3016' or '\u3017' or
                  '\u3018' or '\u3019' or '\u301A' or '\uFE59' or '\uFE5A' or '\uFE5B' or
                  '\uFE5C' or '\uFE5D' or '\uFF08' or '\uFF09' or '\uFF3B' or '\uFF3D' or
                  '\uFF5B' or '\uFF5D' or '\uFF5F' or '\uFF62';

        private static bool IsArabicNumber(char c) =>
            c is (>= '\u0600' and <= '\u0605') or (>= '\u0660' and <= '\u0669') or
                  '\u066B' or '\u066C' or '\u06DD' or '\u08E2';

        private static bool IsRightToLeftBlock(char c) =>
            c is (>= '\u0590' and <= '\u08FF') or (>= '\uFB1D' and <= '\uFDFF') or
                  (>= '\uFE70' and <= '\uFEFE');

        private static bool IsHebrewBlock(char c) =>
            c is (>= '\u05D0' and <= '\u05F4') or (>= '\uFB1D' and <= '\uFB4F');

        private static bool IsArabicBlock(char c) =>
            c is (>= '\u0600' and <= '\u06FF') or (>= '\u0750' and <= '\u077F') or
                  (>= '\u08A0' and <= '\u08FF') or (>= '\uFB50' and <= '\uFDFF') or
                  (>= '\uFE70' and <= '\uFEFC');

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
            public int PoisonedChunk = -1;
            public readonly List<TextLengthAdjustment> LengthAdjustments = new List<TextLengthAdjustment>();
        }

        private readonly record struct TextLengthAdjustment(
            int StartRun,
            int EndRun,
            float[] Targets,
            bool GlyphScale,
            int Chunk);

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
            string Language,
            bool RightToLeft,
            UnicodeBidi Bidi,
            BidiFrame Frame)
        {
            public static TextStyle Default => new(null, DefaultFontSize, 400, SKFontStyleSlant.Upright, TextAnchor.Start, 0f, false, BaselineKind.Alphabetic, 0f, 0f, TextDecoration.None, null, false, UnicodeBidi.Normal, null);
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
            public string LogicalText { get; set; }
            public BidiFrame Frame { get; set; }
            public bool PerCharacterPositioned { get; set; }

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

        private readonly record struct TextChunk(
            float StartX,
            TextAnchor Anchor,
            bool RightToLeft,
            UnicodeBidi Bidi,
            BidiFrame Frame)
        {
            public static TextChunk At(TextStyle style, float startX) =>
                new(startX, style.Anchor, style.RightToLeft, style.Bidi, style.Frame);
        }
    }
}
