using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
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
            ValidateUnicodeBidi(element);
            ValidateTextWritingMode(element);
            bool rightToLeft = ResolveTextDirection(element, inherited.RightToLeft);
            UnicodeBidi bidi = ResolveUnicodeBidi(element, inherited.Bidi);
            BidiFrame frame = ResolveBidiFrame(element, inherited.Frame, rightToLeft, bidi);
            float fontSizeAdjust = ResolveFontSizeAdjust(element, inherited.FontSizeAdjust);
            return new TextStyle(
                string.IsNullOrWhiteSpace(family) ? inherited.Family : family,
                fontSize,
                fontSizeAdjust,
                ParseFontWeight(weightRaw, inherited.Weight),
                ParseFontSlant(styleRaw, inherited.Slant),
                ParseTextAnchor(anchorRaw, inherited.Anchor),
                letterSpacing,
                ResolvePreserveWhitespace(element, xmlSpace, inherited.PreserveWhitespace),
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
        /// Whether the run keeps its spaces. A white-space declaration wins over the
        /// deprecated xml:space (SVG 2 §11.9 white-space processing): pre, pre-wrap and
        /// break-spaces preserve spaces, the other values collapse them. Newlines stay
        /// spaces either way because the text lays out on one line; the cascade only
        /// admits white-space under single-line text layout.
        /// </summary>
        private static bool ResolvePreserveWhitespace(SvgElement element, string xmlSpace, bool inherited)
        {
            string whiteSpace = element.GetCascadedPresentationProperty("white-space")?.Trim();
            if (!string.IsNullOrEmpty(whiteSpace) &&
                !whiteSpace.Equals("inherit", StringComparison.OrdinalIgnoreCase) &&
                !whiteSpace.Equals("unset", StringComparison.OrdinalIgnoreCase))
            {
                return whiteSpace.Equals("pre", StringComparison.OrdinalIgnoreCase) ||
                       whiteSpace.Equals("pre-wrap", StringComparison.OrdinalIgnoreCase) ||
                       whiteSpace.Equals("break-spaces", StringComparison.OrdinalIgnoreCase);
            }
            return string.IsNullOrWhiteSpace(xmlSpace)
                ? inherited
                : xmlSpace.Trim().Equals("preserve", StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// The x-height ratio the element asks its runs to be sized to, as a plain
        /// number, and zero when the property is absent or names a keyword that
        /// adjusts nothing. The ratio rescales the used size only, so it is
        /// inherited like <c>font-size</c> and never feeds back into the length
        /// resolution that produced the computed size. Every other value is
        /// reported: a ratio the pass cannot read, or one that asks for a size no
        /// browser would settle on, is not painted from a guess.
        /// <para>
        /// A ratio is refused when the same element also declares a length in a
        /// font-relative unit. Whether such a length resolves against the computed
        /// or the adjusted size is the one question the used-size model does not
        /// settle, so a run whose spacing would differ between the two readings is
        /// reported instead of being painted from one of them.
        /// </para>
        /// </summary>
        private float ResolveFontSizeAdjust(SvgElement element, float inherited)
        {
            string raw = element.GetPresentationProperty("font-size-adjust");
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            var keyword = raw.AsSpan().Trim();
            if (IsCssWideInheritedKeyword(keyword)) return inherited;
            if (IsCssWideInitialKeyword(keyword)) return 0f;
            if (keyword.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                keyword.Equals("from-font", StringComparison.OrdinalIgnoreCase))
            {
                return 0f;
            }
            if (!SvgValues.TryParseNumber(keyword, out float ratio) || !(ratio > 0f) ||
                !SvgValues.IsFinite(ratio))
            {
                _report.RequireFallback("SVG font-size-adjust value requires compatibility fallback");
                return inherited;
            }
            if (DeclaresFontRelativeLength(element))
            {
                _report.RequireFallback(
                    "SVG font-size-adjust beside a font-relative length requires compatibility fallback");
                return inherited;
            }
            return ratio;
        }

        /// <summary>
        /// True when the element declares a length the text pass resolves against
        /// the run's own font size, in a unit that moves when the used size moves.
        /// A list longer than the value budget counts as a match: the list is
        /// refused on its own, and this only decides whether a second capability
        /// may be claimed on the same element.
        /// </summary>
        private static bool DeclaresFontRelativeLength(SvgElement element) =>
            DeclaresFontRelativeUnit(element.GetPresentationProperty("letter-spacing")) ||
            DeclaresFontRelativeUnit(element.GetPresentationProperty("word-spacing")) ||
            DeclaresFontRelativeUnit(element.GetPresentationProperty("baseline-shift")) ||
            DeclaresFontRelativeUnit(element.GetPresentationProperty("textLength")) ||
            DeclaresFontRelativeUnit(element.GetAttribute("x")) ||
            DeclaresFontRelativeUnit(element.GetAttribute("y")) ||
            DeclaresFontRelativeUnit(element.GetAttribute("dx")) ||
            DeclaresFontRelativeUnit(element.GetAttribute("dy")) ||
            DeclaresFontRelativeUnit(element.GetAttribute("startOffset"));

        private static bool DeclaresFontRelativeUnit(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var tokenizer = SvgValues.CreateTokenizer(raw.AsSpan().Trim());
            int inspected = 0;
            while (tokenizer.Next(out var token))
            {
                if (++inspected > MaxTextPositionListValues) return true;
                if (SvgValues.TryParseLength(token, out _, out var unit) &&
                    unit is SvgValues.SvgUnit.Em or SvgValues.SvgUnit.Ex or SvgValues.SvgUnit.Ch)
                {
                    return true;
                }
            }
            return false;
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

        private static bool IsCssWideInheritedKeyword(ReadOnlySpan<char> value) =>
            value.Equals("inherit", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("unset", StringComparison.OrdinalIgnoreCase);

        private static bool IsCssWideInitialKeyword(ReadOnlySpan<char> value) =>
            value.Equals("initial", StringComparison.OrdinalIgnoreCase) ||
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

        private void ValidateUnicodeBidi(SvgElement element)
        {
            string raw = element.GetPresentationProperty("unicode-bidi");
            if (string.IsNullOrWhiteSpace(raw)) return;
            if (!SvgFeatureSupport.IsUnimplementedUnicodeBidi(raw)) return;
            _report.RequireFallback(
                "SVG unicode-bidi isolating run sequence requires compatibility fallback");
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
            if (IsCssWideInheritedKeyword(keyword)) return inherited;
            if (IsCssWideInitialKeyword(keyword)) return false;
            if (keyword.Equals("rtl", StringComparison.OrdinalIgnoreCase)) return true;
            if (keyword.Equals("ltr", StringComparison.OrdinalIgnoreCase)) return false;
            return inherited;
        }

        private UnicodeBidi ResolveUnicodeBidi(SvgElement element, UnicodeBidi inherited)
        {
            string raw = element.GetPresentationProperty("unicode-bidi");
            if (string.IsNullOrWhiteSpace(raw)) return inherited;
            var keyword = raw.AsSpan().Trim();
            if (IsCssWideInheritedKeyword(keyword)) return inherited;
            if (IsCssWideInitialKeyword(keyword)) return UnicodeBidi.Normal;
            if (keyword.Equals("normal", StringComparison.OrdinalIgnoreCase)) return UnicodeBidi.Normal;
            if (keyword.Equals("plaintext", StringComparison.OrdinalIgnoreCase)) return UnicodeBidi.Plaintext;
            if (keyword.Equals("embed", StringComparison.OrdinalIgnoreCase)) return UnicodeBidi.Embed;
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
            bool embed = keyword.Equals("embed", StringComparison.OrdinalIgnoreCase);
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

        /// <summary>
        /// The used font size a requested x-height ratio implies. The face's own
        /// x-height ratio is measured at one em, so the answer does not depend on
        /// the size being adjusted and cannot be skewed by the size the metrics
        /// were sampled at. A face that reports no x-height is refused rather than
        /// assumed: the normalized metrics carry a substituted x-height, and
        /// sizing a run from a ratio the font never declared is a guess.
        /// </summary>
        private static bool TryResolveAdjustedFontSize(
            SKTypeface typeface,
            float fontSize,
            float ratio,
            out float adjusted)
        {
            adjusted = 0f;
            if (!TryReadFaceXHeightRatio(typeface, out float faceRatio)) return false;

            float value = fontSize * (ratio / faceRatio);
            if (!SvgValues.IsFinite(value) || value < MinFontSize || value > MaxFontSize) return false;
            adjusted = value;
            return true;
        }

        private static bool TryReadFaceXHeightRatio(SKTypeface typeface, out float ratio)
        {
            ratio = 0f;
            if (typeface == null) return false;
            try
            {
                using var probe = new SKFont(typeface, 1f);
                float xHeight = probe.Metrics.XHeight;
                if (!(xHeight > 0f) || !SvgValues.IsFinite(xHeight)) return false;
                ratio = xHeight;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool SharesFaceXHeightRatio(SKTypeface first, SKTypeface second) =>
            TryReadFaceXHeightRatio(first, out float firstRatio) &&
            TryReadFaceXHeightRatio(second, out float secondRatio) &&
            MathF.Abs(firstRatio - secondRatio) <= 0.0001f;

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
    }
}
