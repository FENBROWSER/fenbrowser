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
        /// chunk cursor. The piece is shaped from its own characters, which is
        /// the granularity a browser shapes a directional run at, so the advance
        /// the run carries is the one the visual order implies. A piece is also
        /// the granularity a browser resolves a typeface at, because fallback is
        /// per character: a level run cannot be shaped in the face the whole
        /// source run resolved to when its own characters live in a narrower
        /// face, or the piece paints glyphs and an advance the browser never
        /// asks for. The shaper derives its direction from the first strong
        /// character of the buffer it is given, and a piece whose level
        /// disagrees with that direction is permuted: the shaper lays a
        /// right-to-left buffer out with the advances its characters have in
        /// logical order and reverses the result, so reversing the glyph
        /// sequence is the same operation the shaper would have done for the
        /// other direction.
        /// </summary>
        private TextPaintRun SliceRun(
            TextPaintRun source,
            BidiSegment segment,
            float cursor)
        {
            GlyphRun original = source.GlyphRun;
            if (original?.Typeface == null || segment.Text == null) return null;
            if (segment.Text.Length == 0) return null;

            // A piece is a subset of the source run's characters, so the face the
            // source run resolved to always covers it; the source face is the
            // answer only when the piece resolves to nothing of its own.
            SKTypeface typeface = source.Font.ResolveTypeface(segment.Text) ?? original.Typeface;
            if (source.FontSizeAdjust != 0f && !ReferenceEquals(typeface, original.Typeface) &&
                !SharesFaceXHeightRatio(typeface, original.Typeface))
            {
                // A piece the face fallback moves to is sized from that face's own
                // x-height, and the source run is already shaped at the size the
                // first face implied. One piece cannot be rescaled into the other
                // without reshaping the source, so the run is reported instead.
                _report.RequireFallback(
                    "SVG font-size-adjust across a typeface change requires compatibility fallback");
                return null;
            }
            GlyphRun shaped = SkiaFontService.ShapeWithTypeface(
                segment.Text, typeface, original.FontSize);
            if (shaped?.Glyphs == null || shaped.Glyphs.Length == 0) return null;
            if (ShapedRightToLeft(segment.Text) != segment.LevelOdd) PermuteGlyphs(shaped);

            return new TextPaintRun(
                source.Element, source.PaintStyle, shaped, source.Viewport, cursor, source.Y,
                source.Chunk, source.ApplyOwnOpacity, source.ContainerOpacity, source.Decorations)
            {
                Font = source.Font,
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
    }
}
