using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        // ------------------------------------------------------- foreignObject

        private const string SvgNamespaceUri = "http://www.w3.org/2000/svg";
        private const int MaxForeignObjectSubtreeNodes = 4096;

        /// <summary>
        /// Element names a foreignObject subtree may contain without naming a
        /// specific blocker. The set mirrors the draw dispatch above - graphics
        /// elements, containers, and referenced-only or metadata content - but it is
        /// a diagnostic whitelist, not a renderable set: none of it is painted inside
        /// a foreignObject, because the whole viewport is refused for want of box
        /// layout. Anything outside it - XHTML, a foreign namespace, unknown SVG,
        /// 'animation' - lets the refusal name the actual element rather than
        /// falling back to the generic reason.
        /// </summary>
        private static readonly HashSet<string> ForeignObjectKnownNames = new(StringComparer.Ordinal)
        {
            "g", "a", "view", "svg", "foreignObject", "switch", "use",
            "path", "rect", "circle", "ellipse", "line", "polyline", "polygon",
            "text", "tspan", "textPath", "image",
            "defs", "style", "title", "desc", "metadata", "link", "meta",
            "h:link", "h:meta", "html:link", "html:meta",
            "script", "h:script", "html:script",
            "symbol", "marker", "pattern", "linearGradient", "radialGradient", "stop",
            "filter", "mask", "clipPath", "cursor", "color-profile",
            "font", "font-face", "font-face-src", "font-face-uri", "font-face-name",
            "font-face-format", "glyph", "missing-glyph", "hkern", "vkern",
            "altGlyphDef", "altGlyphItem", "glyphRef", "altGlyphRef",
            "mesh", "meshgradient", "meshrow", "meshpatch",
            "animate", "animateColor", "animateMotion", "animateTransform", "set"
        };

        /// <summary>
        /// Decides a foreignObject. A foreignObject establishes a viewport from its
        /// x/y/width/height geometry, and the walk resolves that geometry here so the
        /// only case it certifies is the one it can prove: a viewport with no area.
        /// An empty clip admits nothing no matter what the subtree holds, so the
        /// document needs no reason code.
        ///
        /// A viewport with area is refused, and the refusal is structural rather than
        /// a missing feature. A foreignObject's content is laid out as a foreign
        /// namespace: element content needs XHTML box layout, and a bare text node
        /// becomes an anonymous block inside the viewport. This engine has no box
        /// layout for either, so no subtree can be painted and Success would report a
        /// frame a browser does not produce. The refusal therefore does not rest on
        /// what the bounded parse happened to record: it holds for a viewport with
        /// area whether or not the tree faithfully carries the content.
        ///
        /// TryDescribeForeignObjectBlocker only refines the diagnostic by naming the
        /// first missing capability it can see. A clean scan is not a licence to
        /// paint, and a named blocker is not what makes the refusal sound.
        /// The canvas and inherited style are part of the uniform draw-handler
        /// contract the dispatch calls every case through; the certified case paints
        /// nothing and the refused case never reaches the canvas.
        ///
        /// This decision governs only the foreignObjects the walk is reached for, so
        /// it is not a substitute for the parse-time gate in
        /// SvgFeatureSupport.FallbackElements. Two skip paths a foreignObject can
        /// still be lost on are budgets that warn rather than refuse - the render
        /// depth budget in DrawElement and the use-chain budget in DrawUse - so any
        /// removal of that gate has to close those first. The switch branch list and
        /// the conditional processing gate no longer lose one: DrawSwitch offers
        /// 'foreignObject' as a branch, and a requiredExtensions branch this
        /// renderer cannot answer is refused with a reason instead of being dropped.
        /// </summary>

        private void DrawForeignObject(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext outer,
            InheritedStyle inherited)
        {
            ResolveGeometryFontContext(el, out float fontSize, out float rootFontSize);
            if (!TryResolveForeignObjectExtent(
                    el, "width", outer, fontSize, rootFontSize, out float w) ||
                !TryResolveForeignObjectExtent(
                    el, "height", outer, fontSize, rootFontSize, out float h))
            {
                return;
            }
            if (!(w > 0f) || !(h > 0f)) return;

            if (!TryDescribeForeignObjectBlocker(el, out string blocker))
            {
                _report.RequireFallback(
                    "SVG foreignObject content requires XHTML box layout");
                return;
            }
            _report.RequireFallback($"SVG foreignObject content {blocker} requires compatibility fallback");
        }

        /// <summary>
        /// Resolves a foreignObject width/height. An absent, 'auto' or 'none'
        /// value is zero rather than an error, per the geometry property definition.
        /// Every other value must resolve: an extent this engine cannot compute
        /// would silently become a zero viewport, turning content the browser paints
        /// into a certified non-rendering subtree, so it refuses the document instead.
        /// </summary>
        private bool TryResolveForeignObjectExtent(
            SvgElement element,
            string property,
            ViewportContext viewport,
            float fontSize,
            float rootFontSize,
            out float value)
        {
            value = 0f;
            string raw = element.GetPresentationProperty(property);
            if (string.IsNullOrWhiteSpace(raw)) return true;
            string trimmed = raw.Trim();
            if (trimmed.Equals("auto", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            float percentReference = property == "width"
                ? viewport.Width
                : viewport.Height;
            if (TryResolveGeometryLength(
                    element, property, viewport, percentReference,
                    fontSize, rootFontSize, out value))
            {
                return true;
            }
            _report.RequireFallback(
                $"SVG foreignObject geometry property '{property}: {trimmed}' requires compatibility fallback");
            return false;
        }

        /// <summary>
        /// Names the first reason the subtree cannot be painted, so the refusal says
        /// which feature is missing rather than reporting every case identically.
        /// </summary>
        private bool TryDescribeForeignObjectBlocker(SvgElement root, out string blocker)
        {
            blocker = null;
            var pending = new Stack<SvgElement>();
            pending.Push(root);
            int inspected = 0;
            while (pending.Count != 0)
            {
                CheckTime();
                SvgElement current = pending.Pop();
                if (++inspected > MaxForeignObjectSubtreeNodes)
                {
                    blocker = "exceeding the first-party render budget";
                    return true;
                }
                if (IsSvgTestSuiteMetadata(current)) continue;
                if (IsXhtmlForeignElement(current) ||
                    (current.NamespaceUri != null &&
                     !string.Equals(current.NamespaceUri, SvgNamespaceUri, StringComparison.Ordinal)))
                {
                    blocker =
                        $"'{SvgDiagnosticText.Identifier(current.Name)}' needs XHTML box layout";
                    return true;
                }
                if (!ForeignObjectKnownNames.Contains(current.Name))
                {
                    blocker =
                        $"'{SvgDiagnosticText.Identifier(current.Name)}' is not renderable here";
                    return true;
                }
                var children = current.Children;
                for (int i = 0; i < children.Count; i++) pending.Push(children[i]);
            }
            return false;
        }

        /// <summary>
        /// Draws a switch. A switch renders exactly one branch: the first direct
        /// child whose conditional processing attributes all evaluate to true.
        ///
        /// The branch list is the set of children the draw dispatch has a
        /// decision for - the graphics elements, the container elements, and
        /// foreignObject. It is not the set the engine happens to be able to
        /// paint: foreignObject is a container element, so a browser may select
        /// it, and leaving it out made the switch skip past a branch a browser
        /// renders and paint the next sibling instead. That is a wrong frame
        /// reported as a clean render. Offering it costs nothing this walk did
        /// not already do for a foreignObject reached any other way, because
        /// DrawForeignObject decides the branch and records the reason it cannot
        /// be painted, and the switch has already committed to the branch by
        /// then, so no later sibling stands in for it.
        ///
        /// Elements outside the list are the same non-rendering kinds the draw
        /// dispatch declines everywhere else - referenced-only content, metadata,
        /// animation - and are handled by that same rule, not re-decided here.
        ///
        /// A branch whose conditional processing cannot be decided stops the
        /// scan outright. The switch already recorded the reason, and painting
        /// the next sibling would be the fail-open this engine exists to avoid.
        /// </summary>
        private void DrawSwitch(SvgElement el, SKCanvas canvas, ViewportContext viewport, InheritedStyle inherited)
        {
            SvgElement selected = null;
            var children = el.Children;
            for (int i = 0; i < children.Count; i++)
            {
                CheckDeadline();
                var child = children[i];
                var verdict = EvaluateConditionalProcessing(child);
                if (verdict == ConditionalVerdict.Undecidable) return;
                if (verdict != ConditionalVerdict.Selected) continue;
                switch (child.Name)
                {
                    case "title":
                    case "desc":
                    case "metadata":
                        continue;
                    case "g":
                    case "a":
                    case "svg":
                    case "switch":
                    case "use":
                    case "foreignObject":
                    case "path":
                    case "rect":
                    case "circle":
                    case "ellipse":
                    case "line":
                    case "polyline":
                    case "polygon":
                    case "text":
                    case "image":
                        selected = child;
                        break;
                    default:
                        continue;
                }
                if (selected != null) break;
            }
            if (selected == null) return;

            using var scope = new CanvasState(canvas);
            ApplyElementTransform(el, canvas, viewport, inherited);
            ApplyClipPath(el, canvas, viewport, inherited);
            var next = inherited.ResolveOverrides(el, _report);
            DrawWithEffects(el, canvas, viewport, () =>
            {
                bool layered = TryBeginGroupOpacity(el, canvas, out var layerPaint);
                try
                {
                    DrawElement(selected, canvas, viewport, next);
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
            }, next);
        }

        /// <summary>
        /// The three conditional processing attributes, and the one rule they
        /// share. requiredExtensions, requiredFeatures and systemLanguage each
        /// hold a list of identifiers from an identifier namespace the
        /// specification owns: an SVG extension identifier, an SVG feature
        /// string, a language tag. An identifier from that namespace asks a
        /// question about the user agent, and this renderer cannot answer it -
        /// it declares no extension, it declines a feature, it has no user
        /// language. Taking the next sibling for an unanswered question paints
        /// a branch no browser picks and reports it as a clean render, so an
        /// unanswerable list is refused with the identifier that made it
        /// unanswerable.
        ///
        /// An identifier outside the namespace is a different question with a
        /// settled answer: no user agent can be shown to implement an extension
        /// the specification does not define, recognize a feature string that is
        /// not in the feature namespace, or prefer a language tag that is not
        /// well formed. The list is false for every user agent, so the branch is
        /// rejected and the walk continues. That is the case a document relies
        /// on when its fallback branch is the correct frame, and it stays
        /// decidable.
        ///
        /// An empty list is false for every user agent as well - the SVG 1.1
        /// conditional processing section and the SVG 1.1 test suite both
        /// evaluate an empty requiredExtensions, requiredFeatures or
        /// systemLanguage to false - so it is rejected rather than refused, and
        /// an element that carries only an empty list still admits the frame a
        /// browser paints.
        /// </summary>
        private enum ConditionalVerdict
        {
            Rejected,
            Selected,
            Undecidable
        }

        private bool PassesConditionalProcessing(SvgElement element)
        {
            return EvaluateConditionalProcessing(element) == ConditionalVerdict.Selected;
        }

        private ConditionalVerdict EvaluateConditionalProcessing(SvgElement element)
        {
            var extensions = EvaluateRequiredExtensions(element);
            if (extensions != ConditionalVerdict.Selected) return extensions;
            var features = EvaluateRequiredFeatures(element);
            if (features != ConditionalVerdict.Selected) return features;
            return EvaluateSystemLanguage(element);
        }

        /// <summary>
        /// requiredExtensions is satisfied only when every listed extension is
        /// supported, so one identifier no user agent can be shown to implement
        /// settles the whole list as false. A list made only of identifiers the
        /// specification defines is a question about this user agent that this
        /// renderer has to refuse: a browser implements both defined
        /// extensions, so it selects the branch, and this renderer cannot render
        /// the branch a browser selects.
        /// </summary>
        private ConditionalVerdict EvaluateRequiredExtensions(SvgElement element)
        {
            string required = element.GetAttribute("requiredExtensions");
            if (required == null) return ConditionalVerdict.Selected;
            var tokenizer = SvgValues.CreateTokenizer(required.AsSpan());
            bool any = false;
            string defined = null;
            while (tokenizer.Next(out var token))
            {
                any = true;
                if (!IsSpecifiedExtensionIdentifier(token)) return ConditionalVerdict.Rejected;
                if (defined == null) defined = token.ToString();
            }
            if (!any || defined == null) return ConditionalVerdict.Rejected;
            _report.RequireFallback(
                $"SVG conditional processing attribute 'requiredExtensions' requires compatibility fallback " +
                $"for unimplemented extension '{defined}'");
            return ConditionalVerdict.Undecidable;
        }

        /// <summary>
        /// requiredFeatures is satisfied only when every listed feature is
        /// supported, so an unrecognized feature string settles the whole list
        /// as false. A feature string from the SVG feature namespace the engine
        /// does not implement is refused rather than rejected, because a browser
        /// implements features this renderer does not and would select the
        /// branch.
        /// </summary>
        private ConditionalVerdict EvaluateRequiredFeatures(SvgElement element)
        {
            string required = element.GetAttribute("requiredFeatures");
            if (required == null) return ConditionalVerdict.Selected;
            var tokenizer = SvgValues.CreateTokenizer(required.AsSpan());
            bool any = false;
            string unimplemented = null;
            while (tokenizer.Next(out var token))
            {
                any = true;
                if (!TryGetSpecifiedFeature(token, out string feature)) return ConditionalVerdict.Rejected;
                if (IsSupportedRequiredFeature(feature)) continue;
                if (unimplemented == null) unimplemented = token.ToString();
            }
            if (!any) return ConditionalVerdict.Rejected;
            if (unimplemented == null) return ConditionalVerdict.Selected;
            _report.RequireFallback(
                $"SVG conditional processing attribute 'requiredFeatures' requires compatibility fallback " +
                $"for unimplemented feature '{unimplemented}'");
            return ConditionalVerdict.Undecidable;
        }

        /// <summary>
        /// systemLanguage is satisfied when any listed tag matches a user
        /// language, so a well formed tag is a question about a user preference
        /// this renderer does not have and is refused. Tags that are not well
        /// formed match no user language, which settles the list as false for
        /// every user agent.
        /// </summary>
        private ConditionalVerdict EvaluateSystemLanguage(SvgElement element)
        {
            string required = element.GetAttribute("systemLanguage");
            if (required == null) return ConditionalVerdict.Selected;
            var tokenizer = SvgValues.CreateTokenizer(required.AsSpan());
            bool any = false;
            string tag = null;
            while (tokenizer.Next(out var token))
            {
                any = true;
                if (!IsWellFormedLanguageTag(token)) continue;
                if (tag == null) tag = token.ToString();
            }
            if (!any || tag == null) return ConditionalVerdict.Rejected;
            _report.RequireFallback(
                $"SVG conditional processing attribute 'systemLanguage' requires compatibility fallback " +
                $"for language '{tag}' this renderer has no user language for");
            return ConditionalVerdict.Undecidable;
        }

        /// <summary>
        /// The extension identifiers the SVG specification defines. Both are
        /// implemented by shipping browsers, so a branch that requires one is a
        /// branch a browser selects.
        /// </summary>
        private static bool IsSpecifiedExtensionIdentifier(ReadOnlySpan<char> token)
        {
            string value = token.ToString();
            return value.Equals("http://www.w3.org/1999/xhtml", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("http://www.w3.org/1999/xlink", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// A tag no user agent can hold in its preferred language list: a
        /// non-empty run of ASCII alphanumerics and hyphens with no empty
        /// subtag. The test is deliberately permissive, because a real tag
        /// rejected here would let the walk paint a fallback branch a browser
        /// does not paint; a malformed tag accepted here only costs a refusal
        /// the walk could have decided.
        /// </summary>
        private static bool IsWellFormedLanguageTag(ReadOnlySpan<char> token)
        {
            if (token.IsEmpty) return false;
            if (token[0] == '-' || token[token.Length - 1] == '-') return false;
            bool afterHyphen = false;
            for (int i = 0; i < token.Length; i++)
            {
                char c = token[i];
                if (c == '-')
                {
                    if (afterHyphen) return false;
                    afterHyphen = true;
                    continue;
                }
                bool alpha = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
                bool digit = c >= '0' && c <= '9';
                if (!alpha && !digit) return false;
                afterHyphen = false;
            }
            return true;
        }

        private static bool TryGetSpecifiedFeature(ReadOnlySpan<char> token, out string feature)
        {
            string value = token.ToString();
            const string svg11 = "http://www.w3.org/TR/SVG11/feature#";
            const string svg11Https = "https://www.w3.org/TR/SVG11/feature#";
            const string svg2 = "http://www.w3.org/TR/SVG2/feature#";
            const string svg2Https = "https://www.w3.org/TR/SVG2/feature#";
            feature = null;
            if (value.StartsWith(svg11, StringComparison.OrdinalIgnoreCase))
                feature = value.Substring(svg11.Length);
            else if (value.StartsWith(svg11Https, StringComparison.OrdinalIgnoreCase))
                feature = value.Substring(svg11Https.Length);
            else if (value.StartsWith(svg2, StringComparison.OrdinalIgnoreCase))
                feature = value.Substring(svg2.Length);
            else if (value.StartsWith(svg2Https, StringComparison.OrdinalIgnoreCase))
                feature = value.Substring(svg2Https.Length);
            return feature != null;
        }

        private static bool IsSupportedRequiredFeature(string feature)
        {
            return feature.Equals("SVG", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Structure", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("BasicStructure", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("ContainerAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("ConditionalProcessing", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Image", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Style", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("ViewportAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Shape", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("BasicText", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Text", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("PaintAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("BasicPaintAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("GraphicsAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("BasicGraphicsAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("OpacityAttribute", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Gradient", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Pattern", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Clip", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("BasicClip", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Mask", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Filter", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("Marker", StringComparison.OrdinalIgnoreCase) ||
                feature.Equals("XlinkAttribute", StringComparison.OrdinalIgnoreCase);
        }
    }
}
