using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace FenBrowser.FenEngine.Rendering
{
    /// <summary>
    /// Turns a WOFF 1.0 or WOFF2 web font into the plain sfnt (TrueType/OpenType) bytes
    /// the platform font manager understands. Skia's Windows backend is DirectWrite, which
    /// reads neither container, so every woff2 @font-face on the web fell back to a system
    /// font (github.com's Mona Sans among them).
    ///
    /// Untrusted input: every read is bounds-checked, the reconstructed font is capped at
    /// <see cref="MaxSfntBytes"/>, and any structural violation the specs require a decoder
    /// to reject makes <see cref="TryDecode"/> return false rather than throw. Validation
    /// follows the WOFF2 decoder conformance requirements (W3C WOFF2 §§4-5) as exercised by
    /// the WPT css/WOFF2 fixtures.
    /// </summary>
    public static class WebFontDecoder
    {
        public const int MaxSfntBytes = 64 * 1024 * 1024;

        private const uint Woff1Signature = 0x774F4646; // 'wOFF'
        private const uint Woff2Signature = 0x774F4632; // 'wOF2'
        private const uint TtcfFlavor = 0x74746366;     // 'ttcf'

        private const uint TagGlyf = 0x676C7966;
        private const uint TagLoca = 0x6C6F6361;
        private const uint TagHmtx = 0x686D7478;
        private const uint TagHead = 0x68656164;
        private const uint TagHhea = 0x68686561;
        private const uint TagMaxp = 0x6D617870;

        // WOFF2 §5.1 known table tags, indexed by the directory entry's 6-bit tag index.
        private static readonly string[] KnownTags =
        {
            "cmap", "head", "hhea", "hmtx", "maxp", "name", "OS/2", "post", "cvt ", "fpgm", "glyf", "loca", "prep",
            "CFF ", "VORG", "EBDT", "EBLC", "gasp", "hdmx", "kern", "LTSH", "PCLT", "VDMX", "vhea", "vmtx", "BASE",
            "GDEF", "GPOS", "GSUB", "EBSC", "JSTF", "MATH", "CBDT", "CBLC", "COLR", "CPAL", "SVG ", "sbix", "acnt",
            "avar", "bdat", "bloc", "bsln", "cvar", "fdsc", "feat", "fmtx", "fvar", "gvar", "hsty", "just", "lcar",
            "mort", "morx", "opbd", "prop", "trak", "Zapf", "Silf", "Glat", "Gloc", "Feat", "Sill",
        };

        /// <summary>True when the bytes start with a WOFF or WOFF2 signature.</summary>
        public static bool IsWebFontContainer(ReadOnlySpan<byte> data)
        {
            if (data.Length < 4) return false;
            uint signature = ReadU32(data, 0);
            return signature == Woff1Signature || signature == Woff2Signature;
        }

        /// <summary>
        /// Decodes a WOFF or WOFF2 font to sfnt bytes. Returns false for anything malformed.
        /// </summary>
        public static bool TryDecode(byte[] data, out byte[] sfnt)
        {
            sfnt = null;
            if (data == null || data.Length < 4) return false;
            try
            {
                uint signature = ReadU32(data, 0);
                sfnt = signature switch
                {
                    Woff2Signature => DecodeWoff2(data),
                    Woff1Signature => DecodeWoff1(data),
                    _ => null,
                };
            }
            catch (FontFormatException)
            {
                sfnt = null;
            }
            catch (InvalidDataException)
            {
                sfnt = null; // zlib stream corruption
            }

            return sfnt != null;
        }

        // ---------------------------------------------------------------- WOFF 1.0

        private sealed class Woff1Entry
        {
            public uint Tag;
            public uint Offset;
            public uint CompLength;
            public uint OrigLength;
        }

        private static byte[] DecodeWoff1(byte[] data)
        {
            // WOFF 1.0 §4 header: 44 bytes.
            Require(data.Length >= 44);
            uint flavor = ReadU32(data, 4);
            uint length = ReadU32(data, 8);
            int numTables = ReadU16(data, 12);
            Require(length == (uint)data.Length && numTables > 0 && flavor != TtcfFlavor);

            long directoryEnd = 44L + numTables * 20L;
            Require(directoryEnd <= data.Length);

            var entries = new List<Woff1Entry>(numTables);
            var tags = new HashSet<uint>();
            long total = 0;
            for (int i = 0; i < numTables; i++)
            {
                int at = 44 + i * 20;
                var entry = new Woff1Entry
                {
                    Tag = ReadU32(data, at),
                    Offset = ReadU32(data, at + 4),
                    CompLength = ReadU32(data, at + 8),
                    OrigLength = ReadU32(data, at + 12),
                };
                Require(tags.Add(entry.Tag));
                Require(entry.Offset >= directoryEnd && (long)entry.Offset + entry.CompLength <= data.Length);
                Require(entry.CompLength <= entry.OrigLength);
                total += Pad4(entry.OrigLength);
                Require(total <= MaxSfntBytes);
                entries.Add(entry);
            }

            var tables = new Dictionary<uint, byte[]>();
            foreach (var entry in entries)
            {
                byte[] table;
                if (entry.CompLength == entry.OrigLength)
                {
                    table = new byte[entry.OrigLength];
                    Buffer.BlockCopy(data, (int)entry.Offset, table, 0, (int)entry.OrigLength);
                }
                else
                {
                    table = new byte[entry.OrigLength];
                    using var input = new MemoryStream(data, (int)entry.Offset, (int)entry.CompLength, writable: false);
                    using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                    int read = ReadFully(zlib, table);
                    Require(read == table.Length && zlib.ReadByte() == -1);
                }

                tables[entry.Tag] = table;
            }

            return BuildSfnt(flavor, tables);
        }

        // ---------------------------------------------------------------- WOFF2

        private sealed class Woff2Entry
        {
            public uint Tag;
            public int Transform;
            public uint OrigLength;
            public uint TransformLength;
            public bool IsTransformed;
            public int StreamOffset;
            public int StreamLength;
        }

        private static byte[] DecodeWoff2(byte[] data)
        {
            // WOFF2 §3 header: 48 bytes.
            Require(data.Length >= 48);
            uint flavor = ReadU32(data, 4);
            uint length = ReadU32(data, 8);
            int numTables = ReadU16(data, 12);
            uint totalCompressedSize = ReadU32(data, 20);
            uint metaOffset = ReadU32(data, 28);
            uint metaLength = ReadU32(data, 32);
            uint privOffset = ReadU32(data, 40);
            uint privLength = ReadU32(data, 44);
            Require(length == (uint)data.Length && numTables > 0);
            Require(flavor != TtcfFlavor); // collections are not supported

            // §4 table directory.
            var reader = new Reader(data, 48, data.Length);
            var entries = new List<Woff2Entry>(numTables);
            var byTag = new Dictionary<uint, Woff2Entry>();
            long streamTotal = 0;
            for (int i = 0; i < numTables; i++)
            {
                int flags = reader.U8();
                int tagIndex = flags & 0x3F;
                uint tag = tagIndex == 0x3F ? reader.U32() : TagFromString(KnownTags[tagIndex]);
                int transform = (flags >> 6) & 0x03;
                uint origLength = reader.Base128();

                bool isGlyfOrLoca = tag == TagGlyf || tag == TagLoca;
                bool transformed;
                if (isGlyfOrLoca)
                {
                    Require(transform == 0 || transform == 3);
                    transformed = transform == 0;
                }
                else if (tag == TagHmtx)
                {
                    Require(transform == 0 || transform == 1);
                    transformed = transform == 1;
                }
                else
                {
                    Require(transform == 0);
                    transformed = false;
                }

                uint transformLength = transformed ? reader.Base128() : origLength;
                if (tag == TagLoca && transformed)
                {
                    Require(transformLength == 0); // loca is rebuilt from glyf
                }

                var entry = new Woff2Entry
                {
                    Tag = tag,
                    Transform = transform,
                    OrigLength = origLength,
                    TransformLength = transformLength,
                    IsTransformed = transformed,
                    StreamOffset = (int)streamTotal,
                    StreamLength = (int)transformLength,
                };
                streamTotal += transformLength;
                Require(streamTotal <= MaxSfntBytes && origLength <= MaxSfntBytes);
                Require(byTag.TryAdd(tag, entry));
                entries.Add(entry);
            }

            // glyf and loca come as a pair, both transformed or both not.
            bool hasGlyf = byTag.TryGetValue(TagGlyf, out var glyfEntry);
            bool hasLoca = byTag.TryGetValue(TagLoca, out var locaEntry);
            Require(hasGlyf == hasLoca);
            if (hasGlyf)
            {
                Require(glyfEntry.IsTransformed == locaEntry.IsTransformed);
            }

            // §4-§6 block layout: the compressed stream follows the directory directly and
            // metadata/private data follow on 4-byte boundaries with nothing in between.
            long compressedOffset = reader.Position;
            long cursor = compressedOffset + totalCompressedSize;
            Require(cursor <= data.Length);
            cursor = Pad4(cursor);
            if (metaOffset != 0)
            {
                Require(cursor == metaOffset && (long)metaOffset + metaLength <= data.Length);
                cursor = Pad4((long)metaOffset + metaLength);
            }

            if (privOffset != 0)
            {
                Require(cursor == privOffset && (long)privOffset + privLength <= data.Length);
                cursor = Pad4((long)privOffset + privLength);
            }

            Require(cursor == Pad4(data.Length));

            // §5.4 one Brotli stream holding every table, exactly as long as their sum.
            var stream = new byte[streamTotal];
            if (streamTotal > 0)
            {
                Require(BrotliDecoder.TryDecompress(
                    new ReadOnlySpan<byte>(data, (int)compressedOffset, (int)totalCompressedSize),
                    stream,
                    out int written));
                Require(written == stream.Length);
            }
            else
            {
                Require(totalCompressedSize > 0 && BrotliDecoder.TryDecompress(
                    new ReadOnlySpan<byte>(data, (int)compressedOffset, (int)totalCompressedSize),
                    Span<byte>.Empty,
                    out int emptyWritten) && emptyWritten == 0);
            }

            var tables = new Dictionary<uint, byte[]>();
            foreach (var entry in entries)
            {
                if (entry.Tag == TagLoca && entry.IsTransformed) continue;
                if (entry.IsTransformed) continue; // glyf and hmtx are rebuilt below
                var table = new byte[entry.StreamLength];
                Buffer.BlockCopy(stream, entry.StreamOffset, table, 0, entry.StreamLength);
                tables[entry.Tag] = table;
            }

            short[] xMins = null;
            if (hasGlyf && glyfEntry.IsTransformed)
            {
                var (glyf, loca, indexFormat, mins) = ReconstructGlyf(stream, glyfEntry.StreamOffset, glyfEntry.StreamLength);
                Require(loca.Length == locaEntry.OrigLength);
                tables[TagGlyf] = glyf;
                tables[TagLoca] = loca;
                xMins = mins;

                // head.indexToLocFormat must describe the loca we just wrote.
                if (tables.TryGetValue(TagHead, out var head) && head.Length >= 54)
                {
                    head[50] = 0;
                    head[51] = (byte)indexFormat;
                }
            }

            if (byTag.TryGetValue(TagHmtx, out var hmtxEntry) && hmtxEntry.IsTransformed)
            {
                Require(xMins != null); // lsb values are derived from the transformed glyf
                tables[TagHmtx] = ReconstructHmtx(stream, hmtxEntry, tables, xMins);
            }

            return BuildSfnt(flavor, tables);
        }

        // WOFF2 §5.1 transformed glyf -> glyf + loca.
        private static (byte[] Glyf, byte[] Loca, int IndexFormat, short[] XMins) ReconstructGlyf(byte[] stream, int offset, int length)
        {
            var header = new Reader(stream, offset, offset + length);
            header.U16(); // reserved
            int optionFlags = header.U16();
            int numGlyphs = header.U16();
            int indexFormat = header.U16();
            Require(indexFormat <= 1);

            var sizes = new uint[7];
            for (int i = 0; i < sizes.Length; i++) sizes[i] = header.U32();

            long cursor = header.Position;
            var substreams = new Reader[7];
            for (int i = 0; i < sizes.Length; i++)
            {
                Require(cursor + sizes[i] <= offset + length);
                substreams[i] = new Reader(stream, (int)cursor, (int)(cursor + sizes[i]));
                cursor += sizes[i];
            }

            Reader overlapBitmap = null;
            if ((optionFlags & 1) != 0)
            {
                int overlapBytes = (numGlyphs + 7) >> 3;
                Require(cursor + overlapBytes <= offset + length);
                overlapBitmap = new Reader(stream, (int)cursor, (int)(cursor + overlapBytes));
                cursor += overlapBytes;
            }

            Require(cursor == offset + length); // nothing may trail the substreams

            var nContourStream = substreams[0];
            var nPointsStream = substreams[1];
            var flagStream = substreams[2];
            var glyphStream = substreams[3];
            var compositeStream = substreams[4];
            var bboxStream = substreams[5];
            var instructionStream = substreams[6];

            int bboxBitmapLength = ((numGlyphs + 31) >> 5) << 2;
            Require(bboxStream.Remaining >= bboxBitmapLength);
            int bboxBitmapStart = bboxStream.Position;
            bboxStream.Skip(bboxBitmapLength);

            using var glyf = new MemoryStream();
            var loca = new uint[numGlyphs + 1];
            var xMins = new short[numGlyphs];
            var buffer = new GlyphWriter();

            for (int glyph = 0; glyph < numGlyphs; glyph++)
            {
                bool hasBbox = (stream[bboxBitmapStart + (glyph >> 3)] & (0x80 >> (glyph & 7))) != 0;
                bool overlap = overlapBitmap != null &&
                               (stream[overlapBitmap.Start + (glyph >> 3)] & (0x80 >> (glyph & 7))) != 0;
                short nContours = (short)nContourStream.U16();
                buffer.Reset();

                if (nContours == 0)
                {
                    Require(!hasBbox); // an empty glyph has no bounding box
                }
                else if (nContours > 0)
                {
                    WriteSimpleGlyph(buffer, nContours, hasBbox, overlap, nPointsStream, flagStream, glyphStream,
                        bboxStream, instructionStream, out xMins[glyph]);
                }
                else
                {
                    Require(nContours == -1 && hasBbox); // composites always carry a bbox
                    WriteCompositeGlyph(buffer, bboxStream, compositeStream, glyphStream, instructionStream, out xMins[glyph]);
                }

                loca[glyph] = (uint)glyf.Length;
                if (buffer.Length > 0)
                {
                    glyf.Write(buffer.Bytes, 0, buffer.Length);
                    while ((glyf.Length & 3) != 0) glyf.WriteByte(0);
                }

                Require(glyf.Length <= MaxSfntBytes);
            }

            loca[numGlyphs] = (uint)glyf.Length;

            byte[] locaBytes;
            if (indexFormat == 0)
            {
                Require(glyf.Length / 2 <= ushort.MaxValue);
                locaBytes = new byte[(numGlyphs + 1) * 2];
                for (int i = 0; i <= numGlyphs; i++) WriteU16(locaBytes, i * 2, (int)(loca[i] / 2));
            }
            else
            {
                locaBytes = new byte[(numGlyphs + 1) * 4];
                for (int i = 0; i <= numGlyphs; i++) WriteU32(locaBytes, i * 4, loca[i]);
            }

            return (glyf.ToArray(), locaBytes, indexFormat, xMins);
        }

        private static void WriteSimpleGlyph(
            GlyphWriter output,
            int nContours,
            bool hasBbox,
            bool overlap,
            Reader nPointsStream,
            Reader flagStream,
            Reader glyphStream,
            Reader bboxStream,
            Reader instructionStream,
            out short xMin)
        {
            var endPoints = new int[nContours];
            int totalPoints = 0;
            for (int c = 0; c < nContours; c++)
            {
                totalPoints += nPointsStream.Read255UShort();
                Require(totalPoints <= ushort.MaxValue);
                endPoints[c] = totalPoints - 1;
            }

            var xs = new int[totalPoints];
            var ys = new int[totalPoints];
            var onCurve = new bool[totalPoints];
            int x = 0, y = 0;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
            for (int p = 0; p < totalPoints; p++)
            {
                int flag = flagStream.U8();
                onCurve[p] = (flag & 0x80) == 0;
                DecodeTriplet(flag & 0x7F, glyphStream, out int dx, out int dy);
                x += dx;
                y += dy;
                Require(x >= short.MinValue && x <= short.MaxValue && y >= short.MinValue && y <= short.MaxValue);
                xs[p] = x;
                ys[p] = y;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }

            int instructionLength = glyphStream.Read255UShort();
            var instructions = instructionStream.Bytes(instructionLength);

            if (hasBbox)
            {
                minX = (short)bboxStream.U16(); minY = (short)bboxStream.U16();
                maxX = (short)bboxStream.U16(); maxY = (short)bboxStream.U16();
            }
            else if (totalPoints == 0)
            {
                minX = minY = maxX = maxY = 0;
            }

            xMin = (short)minX;
            output.I16(nContours);
            output.I16(minX); output.I16(minY); output.I16(maxX); output.I16(maxY);
            foreach (int end in endPoints) output.I16(end);
            output.I16(instructionLength);
            output.Raw(instructions);

            // Flags: on-curve bit, and OVERLAP_SIMPLE on the first point when marked.
            // Coordinates are written as full 16-bit deltas - larger than a hand-packed
            // glyph but exactly equivalent, and trivially correct.
            for (int p = 0; p < totalPoints; p++)
            {
                int flags = onCurve[p] ? 0x01 : 0x00;
                if (p == 0 && overlap) flags |= 0x40;
                output.U8(flags);
            }

            int prev = 0;
            for (int p = 0; p < totalPoints; p++) { output.I16(xs[p] - prev); prev = xs[p]; }
            prev = 0;
            for (int p = 0; p < totalPoints; p++) { output.I16(ys[p] - prev); prev = ys[p]; }
        }

        private static void WriteCompositeGlyph(
            GlyphWriter output,
            Reader bboxStream,
            Reader compositeStream,
            Reader glyphStream,
            Reader instructionStream,
            out short xMin)
        {
            int minX = (short)bboxStream.U16(), minY = (short)bboxStream.U16();
            int maxX = (short)bboxStream.U16(), maxY = (short)bboxStream.U16();
            xMin = (short)minX;

            int start = compositeStream.Position;
            bool haveInstructions = false;
            bool more;
            do
            {
                int flags = compositeStream.U16();
                compositeStream.Skip(2); // glyphIndex
                int argBytes = (flags & 0x0001) != 0 ? 4 : 2;
                if ((flags & 0x0008) != 0) argBytes += 2;        // WE_HAVE_A_SCALE
                else if ((flags & 0x0040) != 0) argBytes += 4;   // WE_HAVE_AN_X_AND_Y_SCALE
                else if ((flags & 0x0080) != 0) argBytes += 8;   // WE_HAVE_A_TWO_BY_TWO
                compositeStream.Skip(argBytes);
                haveInstructions |= (flags & 0x0100) != 0;
                more = (flags & 0x0020) != 0;
            }
            while (more);

            output.I16(-1);
            output.I16(minX); output.I16(minY); output.I16(maxX); output.I16(maxY);
            output.Raw(compositeStream.Slice(start, compositeStream.Position - start));
            if (haveInstructions)
            {
                int instructionLength = glyphStream.Read255UShort();
                output.I16(instructionLength);
                output.Raw(instructionStream.Bytes(instructionLength));
            }
        }

        // WOFF2 §5.2 triplet encoding of one point's (dx, dy).
        private static void DecodeTriplet(int flag, Reader glyphStream, out int dx, out int dy)
        {
            static int WithSign(int f, int value) => (f & 1) != 0 ? value : -value;

            if (flag < 10)
            {
                int b0 = glyphStream.U8();
                dx = 0;
                dy = WithSign(flag, ((flag & 14) << 7) + b0);
            }
            else if (flag < 20)
            {
                int b0 = glyphStream.U8();
                dx = WithSign(flag, (((flag - 10) & 14) << 7) + b0);
                dy = 0;
            }
            else if (flag < 84)
            {
                int b0 = flag - 20;
                int b1 = glyphStream.U8();
                dx = WithSign(flag, 1 + (b0 & 0x30) + (b1 >> 4));
                dy = WithSign(flag >> 1, 1 + ((b0 & 0x0C) << 2) + (b1 & 0x0F));
            }
            else if (flag < 120)
            {
                int b0 = flag - 84;
                int in0 = glyphStream.U8(), in1 = glyphStream.U8();
                dx = WithSign(flag, 1 + ((b0 / 12) << 8) + in0);
                dy = WithSign(flag >> 1, 1 + (((b0 % 12) >> 2) << 8) + in1);
            }
            else if (flag < 124)
            {
                int in0 = glyphStream.U8(), in1 = glyphStream.U8(), in2 = glyphStream.U8();
                dx = WithSign(flag, (in0 << 4) + (in1 >> 4));
                dy = WithSign(flag >> 1, ((in1 & 0x0F) << 8) + in2);
            }
            else
            {
                int in0 = glyphStream.U8(), in1 = glyphStream.U8(), in2 = glyphStream.U8(), in3 = glyphStream.U8();
                dx = WithSign(flag, (in0 << 8) + in1);
                dy = WithSign(flag >> 1, (in2 << 8) + in3);
            }
        }

        // WOFF2 §5.3 transformed hmtx.
        private static byte[] ReconstructHmtx(byte[] stream, Woff2Entry entry, Dictionary<uint, byte[]> tables, short[] xMins)
        {
            Require(tables.TryGetValue(TagHhea, out var hhea) && hhea.Length >= 36);
            Require(tables.TryGetValue(TagMaxp, out var maxp) && maxp.Length >= 6);
            int numberOfHMetrics = ReadU16(hhea, 34);
            int numGlyphs = ReadU16(maxp, 4);
            Require(numberOfHMetrics >= 1 && numberOfHMetrics <= numGlyphs && numGlyphs <= xMins.Length);

            var reader = new Reader(stream, entry.StreamOffset, entry.StreamOffset + entry.StreamLength);
            int flags = reader.U8();
            Require((flags & 0xFC) == 0 && (flags & 0x03) != 0);
            bool lsbOmitted = (flags & 1) != 0;
            bool leftSideBearingOmitted = (flags & 2) != 0;

            var advances = new int[numberOfHMetrics];
            for (int i = 0; i < numberOfHMetrics; i++) advances[i] = reader.U16();

            var lsbs = new int[numGlyphs];
            for (int i = 0; i < numberOfHMetrics; i++) lsbs[i] = lsbOmitted ? xMins[i] : (short)reader.U16();
            for (int i = numberOfHMetrics; i < numGlyphs; i++) lsbs[i] = leftSideBearingOmitted ? xMins[i] : (short)reader.U16();
            Require(reader.Remaining == 0);

            // The size follows from hhea and maxp; a transformed table's origLength is
            // only a hint (the reference decoder writes what it computes).
            var hmtx = new byte[numberOfHMetrics * 4 + (numGlyphs - numberOfHMetrics) * 2];
            int at = 0;
            for (int i = 0; i < numberOfHMetrics; i++)
            {
                WriteU16(hmtx, at, advances[i]);
                WriteU16(hmtx, at + 2, lsbs[i] & 0xFFFF);
                at += 4;
            }

            for (int i = numberOfHMetrics; i < numGlyphs; i++)
            {
                WriteU16(hmtx, at, lsbs[i] & 0xFFFF);
                at += 2;
            }

            return hmtx;
        }

        // ---------------------------------------------------------------- sfnt output

        private static byte[] BuildSfnt(uint flavor, Dictionary<uint, byte[]> tables)
        {
            var tags = new List<uint>(tables.Keys);
            tags.Sort();
            int numTables = tags.Count;
            long size = 12 + 16L * numTables;
            foreach (var tag in tags) size += Pad4(tables[tag].Length);
            Require(size <= MaxSfntBytes);

            var output = new byte[size];
            int entrySelector = 0;
            while ((2 << entrySelector) <= numTables) entrySelector++;
            int searchRange = (1 << entrySelector) * 16;
            WriteU32(output, 0, flavor);
            WriteU16(output, 4, numTables);
            WriteU16(output, 6, searchRange);
            WriteU16(output, 8, entrySelector);
            WriteU16(output, 10, numTables * 16 - searchRange);

            int offset = 12 + 16 * numTables;
            int headOffset = -1;
            for (int i = 0; i < numTables; i++)
            {
                uint tag = tags[i];
                var table = tables[tag];
                if (tag == TagHead && table.Length >= 12)
                {
                    WriteU32(table, 8, 0); // checkSumAdjustment is recomputed below
                    headOffset = offset;
                }

                Buffer.BlockCopy(table, 0, output, offset, table.Length);
                int record = 12 + i * 16;
                WriteU32(output, record, tag);
                WriteU32(output, record + 4, Checksum(output, offset, table.Length));
                WriteU32(output, record + 8, (uint)offset);
                WriteU32(output, record + 12, (uint)table.Length);
                offset += (int)Pad4(table.Length);
            }

            if (headOffset >= 0)
            {
                WriteU32(output, headOffset + 8, unchecked(0xB1B0AFBA - Checksum(output, 0, output.Length)));
            }

            return output;
        }

        private static uint Checksum(byte[] data, int offset, int length)
        {
            uint sum = 0;
            int end = offset + (int)Pad4(length);
            for (int i = offset; i < end; i += 4)
            {
                uint word = 0;
                for (int b = 0; b < 4; b++)
                {
                    word <<= 8;
                    if (i + b < data.Length && i + b < offset + length) word |= data[i + b];
                }

                sum = unchecked(sum + word);
            }

            return sum;
        }

        // ---------------------------------------------------------------- primitives

        private sealed class FontFormatException : Exception
        {
        }

        private static void Require(bool condition)
        {
            if (!condition) throw new FontFormatException();
        }

        private static long Pad4(long value) => (value + 3) & ~3L;

        private static uint TagFromString(string tag) =>
            ((uint)tag[0] << 24) | ((uint)tag[1] << 16) | ((uint)tag[2] << 8) | tag[3];

        private static int ReadU16(ReadOnlySpan<byte> data, int at) => (data[at] << 8) | data[at + 1];

        private static uint ReadU32(ReadOnlySpan<byte> data, int at) =>
            ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

        private static void WriteU16(byte[] data, int at, int value)
        {
            data[at] = (byte)(value >> 8);
            data[at + 1] = (byte)value;
        }

        private static void WriteU32(byte[] data, int at, uint value)
        {
            data[at] = (byte)(value >> 24);
            data[at + 1] = (byte)(value >> 16);
            data[at + 2] = (byte)(value >> 8);
            data[at + 3] = (byte)value;
        }

        private static int ReadFully(Stream stream, byte[] buffer)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int read = stream.Read(buffer, total, buffer.Length - total);
                if (read <= 0) break;
                total += read;
            }

            return total;
        }

        /// <summary>A bounds-checked big-endian cursor over [start, end) of a buffer.</summary>
        private sealed class Reader
        {
            private readonly byte[] _data;
            private readonly int _end;

            public Reader(byte[] data, int start, int end)
            {
                Require(start >= 0 && end <= data.Length && start <= end);
                _data = data;
                Start = start;
                Position = start;
                _end = end;
            }

            public int Start { get; }
            public int Position { get; private set; }
            public int Remaining => _end - Position;

            public void Skip(int count)
            {
                Require(count >= 0 && count <= Remaining);
                Position += count;
            }

            public int U8()
            {
                Require(Remaining >= 1);
                return _data[Position++];
            }

            public int U16()
            {
                Require(Remaining >= 2);
                int value = (_data[Position] << 8) | _data[Position + 1];
                Position += 2;
                return value;
            }

            public uint U32()
            {
                Require(Remaining >= 4);
                uint value = ReadU32(_data, Position);
                Position += 4;
                return value;
            }

            public byte[] Bytes(int count)
            {
                Require(count >= 0 && count <= Remaining);
                var result = new byte[count];
                Buffer.BlockCopy(_data, Position, result, 0, count);
                Position += count;
                return result;
            }

            public byte[] Slice(int start, int count)
            {
                var result = new byte[count];
                Buffer.BlockCopy(_data, start, result, 0, count);
                return result;
            }

            // WOFF2 §3.2 UIntBase128: at most five bytes, no leading zero byte, fits 32 bits.
            public uint Base128()
            {
                uint accumulator = 0;
                for (int i = 0; i < 5; i++)
                {
                    int b = U8();
                    Require(!(i == 0 && b == 0x80));
                    Require((accumulator & 0xFE000000) == 0);
                    accumulator = (accumulator << 7) | (uint)(b & 0x7F);
                    if ((b & 0x80) == 0) return accumulator;
                }

                throw new FontFormatException();
            }

            // WOFF2 §3.1 255UInt16.
            public int Read255UShort()
            {
                int code = U8();
                return code switch
                {
                    253 => U16(),
                    254 => U8() + 506,
                    255 => U8() + 253,
                    _ => code,
                };
            }
        }

        private sealed class GlyphWriter
        {
            public byte[] Bytes = new byte[256];
            public int Length;

            public void Reset() => Length = 0;

            private void Ensure(int extra)
            {
                Require((long)Length + extra <= MaxSfntBytes);
                if (Length + extra <= Bytes.Length) return;
                Array.Resize(ref Bytes, Math.Max(Bytes.Length * 2, Length + extra));
            }

            public void U8(int value)
            {
                Ensure(1);
                Bytes[Length++] = (byte)value;
            }

            public void I16(int value)
            {
                Ensure(2);
                Bytes[Length++] = (byte)(value >> 8);
                Bytes[Length++] = (byte)value;
            }

            public void Raw(byte[] data)
            {
                Ensure(data.Length);
                Buffer.BlockCopy(data, 0, Bytes, Length, data.Length);
                Length += data.Length;
            }
        }
    }
}
