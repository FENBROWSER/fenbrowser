using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using FenBrowser.Core.Network;
using FenBrowser.FenEngine.Rendering;
using SkiaSharp;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// WOFF/WOFF2 unwrapping in front of Skia. The fixtures are the WPT css/WOFF2 decoder
/// conformance fonts (TestData/Fonts/Woff2); each row's expectation is what that test
/// requires of a conforming user agent - "P" tests must load, "F" tests must be rejected.
/// </summary>
[Collection("Synthetic CAPTCHA")]
public sealed class WebFontDecoderTests
{
    private static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", "Fonts", "Woff2", name + ".woff2"));

    [Theory]
    [InlineData("blocks-extraneous-data-001", false)]
    [InlineData("blocks-extraneous-data-002", false)]
    [InlineData("blocks-extraneous-data-003", false)]
    [InlineData("blocks-extraneous-data-004", false)]
    [InlineData("blocks-extraneous-data-005", false)]
    [InlineData("blocks-extraneous-data-006", false)]
    [InlineData("blocks-extraneous-data-007", false)]
    [InlineData("blocks-extraneous-data-008", false)]
    [InlineData("blocks-overlap-001", false)]
    [InlineData("blocks-overlap-002", false)]
    [InlineData("blocks-overlap-003", false)]
    [InlineData("datatypes-alt-255uint16-001", true)]
    [InlineData("datatypes-invalid-base128-001", false)]
    [InlineData("datatypes-invalid-base128-002", false)]
    [InlineData("datatypes-invalid-base128-003", false)]
    [InlineData("directory-knowntags-001", true)]
    [InlineData("directory-mismatched-tables-001", false)]
    [InlineData("header-length-001", false)]
    [InlineData("header-length-002", false)]
    [InlineData("header-numTables-001", false)]
    [InlineData("header-reserved-001", true)]
    [InlineData("header-signature-001", false)]
    [InlineData("header-totalsfntsize-001", true)]
    [InlineData("header-totalsfntsize-002", true)]
    [InlineData("metadata-noeffect-001", true)]
    [InlineData("metadata-noeffect-002", true)]
    [InlineData("metadatadisplay-encoding-001", true)]
    [InlineData("metadatadisplay-schema-credit-001", true)]
    [InlineData("privatedata-noeffect-001", true)]
    [InlineData("privatedata-noeffect-002", true)]
    [InlineData("tabledata-bad-origlength-loca-001", false)]
    [InlineData("tabledata-bad-origlength-loca-002", false)]
    [InlineData("tabledata-brotli-001", false)]
    [InlineData("tabledata-decompressed-length-001", false)]
    [InlineData("tabledata-decompressed-length-002", false)]
    [InlineData("tabledata-decompressed-length-003", false)]
    [InlineData("tabledata-decompressed-length-004", false)]
    [InlineData("tabledata-extraneous-data-001", false)]
    [InlineData("tabledata-glyf-bbox-001", true)]
    [InlineData("tabledata-glyf-bbox-002", false)]
    [InlineData("tabledata-glyf-bbox-003", false)]
    [InlineData("tabledata-glyf-origlength-001", true)]
    [InlineData("tabledata-glyf-origlength-002", true)]
    [InlineData("tabledata-glyf-origlength-003", true)]
    [InlineData("tabledata-non-zero-loca-001", false)]
    [InlineData("tabledata-recontruct-loca-001", true)]
    [InlineData("tabledata-transform-bad-flag-001", false)]
    [InlineData("tabledata-transform-bad-flag-002", false)]
    [InlineData("tabledata-transform-hmtx-001", true)]
    [InlineData("tabledata-transform-hmtx-002", true)]
    [InlineData("tabledata-transform-hmtx-003", false)]
    [InlineData("tabledata-transform-hmtx-004", false)]
    [InlineData("valid-001", true)]
    [InlineData("valid-002", true)]
    [InlineData("valid-003", true)]
    [InlineData("valid-004", true)]
    [InlineData("valid-005", true)]
    [InlineData("valid-006", true)]
    [InlineData("valid-007", true)]
    [InlineData("valid-008", true)]
    public void Woff2ConformanceFixture_IsAcceptedOrRejectedAsTheSpecRequires(string fixture, bool accept)
    {
        bool decoded = WebFontDecoder.TryDecode(Fixture(fixture), out var sfnt);

        Assert.Equal(accept, decoded);
        if (accept)
        {
            // The platform font manager must take what we produce.
            using var stream = new MemoryStream(sfnt, writable: false);
            using var typeface = SKTypeface.FromStream(stream);
            Assert.NotNull(typeface);
            Assert.True(typeface.GlyphCount > 0);
        }
    }

    [Fact]
    public void RealWorldVariableFont_WithTransformedGlyf_DecodesToEveryGlyph()
    {
        // github.com's Mona Sans Mono: 746 glyphs, transformed glyf/loca, gvar/fvar.
        Assert.True(WebFontDecoder.TryDecode(Fixture("MonaSansMonoVF"), out var sfnt));
        using var stream = new MemoryStream(sfnt, writable: false);
        using var typeface = SKTypeface.FromStream(stream);
        Assert.NotNull(typeface);
        Assert.Equal(746, typeface.GlyphCount);
        Assert.Equal("Mona Sans Mono VF", typeface.FamilyName);

        using var font = new SKFont(typeface, 32);
        Assert.True(font.MeasureText("Mona") > 0);
        Assert.NotEqual(0, font.GetGlyphs("M")[0]);
    }

    [Fact]
    public void Woff1Font_WithZlibAndStoredTables_RoundTripsToTheSameTables()
    {
        // WOFF 1.0 §5: a table is stored as-is when compression does not shrink it and as a
        // zlib stream otherwise. Wrap a known font both ways and expect identical tables back.
        Assert.True(WebFontDecoder.TryDecode(Fixture("valid-005"), out var sfnt));
        var original = ReadTables(sfnt);
        var woff = BuildWoff1(sfnt, original);

        Assert.True(WebFontDecoder.IsWebFontContainer(woff));
        Assert.True(WebFontDecoder.TryDecode(woff, out var roundTripped));
        var decoded = ReadTables(roundTripped);
        Assert.Equal(original.Keys, decoded.Keys);
        foreach (var (tag, bytes) in original)
        {
            if (tag == "head") continue; // checkSumAdjustment is recomputed
            Assert.Equal(bytes, decoded[tag]);
        }

        // A WOFF whose declared length is not the file's is rejected (WOFF 1.0 §4).
        var truncated = woff.AsSpan(0, woff.Length - 4).ToArray();
        Assert.False(WebFontDecoder.TryDecode(truncated, out _));
    }

    [Fact]
    public void PlainSfntAndGarbage_AreNotWebFontContainers()
    {
        Assert.True(WebFontDecoder.TryDecode(Fixture("valid-005"), out var sfnt));
        Assert.False(WebFontDecoder.IsWebFontContainer(sfnt));
        Assert.False(WebFontDecoder.TryDecode(sfnt, out _));
        Assert.False(WebFontDecoder.TryDecode(new byte[] { 0x77, 0x4F, 0x46 }, out _));
        Assert.False(WebFontDecoder.TryDecode(null, out _));
    }

    [Fact]
    public void MutatedFonts_NeverThrowOrRunAway()
    {
        // Security: the decoder parses hostile bytes from any origin. 10,000 deterministic
        // mutations of valid WOFF2 and WOFF inputs (bit flips, byte overwrites with boundary
        // values, truncation, insertion, 32-bit field rewrites) must each end in a clean
        // accept or reject, and any accepted output must stay within the size cap.
        var seeds = new List<byte[]>
        {
            Fixture("valid-005"),
            Fixture("valid-001"),
            Fixture("tabledata-transform-hmtx-001"),
            Fixture("tabledata-glyf-bbox-001"),
        };
        Assert.True(WebFontDecoder.TryDecode(seeds[0], out var sfnt));
        seeds.Add(BuildWoff1(sfnt, ReadTables(sfnt)));

        var random = new Random(20260929);
        var clock = Stopwatch.StartNew();
        int accepted = 0;
        for (int iteration = 0; iteration < 10_000; iteration++)
        {
            var input = Mutate(seeds[iteration % seeds.Count], random);
            var started = clock.Elapsed;
            bool ok = WebFontDecoder.TryDecode(input, out var output);
            Assert.True(clock.Elapsed - started < TimeSpan.FromSeconds(2), $"iteration {iteration} ran away");
            if (ok)
            {
                accepted++;
                Assert.True(output.Length <= WebFontDecoder.MaxSfntBytes);
            }
        }

        Assert.InRange(accepted, 1, 9_999);
    }

    [Fact]
    public async Task Woff2FontFace_LoadsThroughTheFontRegistry()
    {
        var previousFetcher = FontRegistry.FetchDetailedAsync;
        FontRegistry.Clear();
        try
        {
            FontRegistry.FetchDetailedAsync = uri => Task.FromResult(new BinaryFetchResult
            {
                FinalUri = uri,
                Body = Fixture("MonaSansMonoVF"),
            });

            FontRegistry.ParseAndRegister(
                "font-family: 'Woff2 Probe'; src: url('probe.woff2') format('woff2');",
                new Uri("https://fonts.example.test/"));
            await FontRegistry.LoadPendingFontsAsync();

            var typeface = FontRegistry.TryResolve("Woff2 Probe");
            Assert.NotNull(typeface);
            Assert.Equal(746, typeface.GlyphCount);
        }
        finally
        {
            FontRegistry.FetchDetailedAsync = previousFetcher;
            FontRegistry.Clear();
        }
    }

    private static byte[] Mutate(byte[] seed, Random random)
    {
        var data = (byte[])seed.Clone();
        int edits = 1 + random.Next(4);
        for (int e = 0; e < edits && data.Length > 0; e++)
        {
            int at = random.Next(data.Length);
            switch (random.Next(6))
            {
                case 0:
                    data[at] ^= (byte)(1 << random.Next(8));
                    break;
                case 1:
                    data[at] = (byte)(random.Next(4) switch { 0 => 0x00, 1 => 0xFF, 2 => 0x7F, _ => 0x80 });
                    break;
                case 2:
                    Array.Resize(ref data, Math.Max(1, at));
                    break;
                case 3:
                {
                    var grown = new byte[data.Length + 1 + random.Next(8)];
                    Buffer.BlockCopy(data, 0, grown, 0, at);
                    random.NextBytes(grown.AsSpan(at, grown.Length - data.Length));
                    Buffer.BlockCopy(data, at, grown, at + grown.Length - data.Length, data.Length - at);
                    data = grown;
                    break;
                }
                case 4 when at + 4 <= data.Length:
                {
                    uint value = random.Next(3) switch { 0 => 0u, 1 => uint.MaxValue, _ => (uint)random.Next() };
                    data[at] = (byte)(value >> 24);
                    data[at + 1] = (byte)(value >> 16);
                    data[at + 2] = (byte)(value >> 8);
                    data[at + 3] = (byte)value;
                    break;
                }
                default:
                    data[at] = (byte)random.Next(256);
                    break;
            }
        }

        return data;
    }

    private static SortedDictionary<string, byte[]> ReadTables(byte[] sfnt)
    {
        var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        int numTables = (sfnt[4] << 8) | sfnt[5];
        for (int i = 0; i < numTables; i++)
        {
            int record = 12 + i * 16;
            string tag = System.Text.Encoding.ASCII.GetString(sfnt, record, 4);
            int offset = (int)ReadU32(sfnt, record + 8);
            int length = (int)ReadU32(sfnt, record + 12);
            tables[tag] = sfnt.AsSpan(offset, length).ToArray();
        }

        return tables;
    }

    private static byte[] BuildWoff1(byte[] sfnt, SortedDictionary<string, byte[]> tables)
    {
        var bodies = new List<(string Tag, byte[] Stored, int OrigLength)>();
        bool compress = false;
        foreach (var (tag, bytes) in tables)
        {
            // Alternate so both the zlib and the stored path are exercised.
            compress = !compress;
            byte[] stored = bytes;
            if (compress)
            {
                using var buffer = new MemoryStream();
                using (var zlib = new ZLibStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                {
                    zlib.Write(bytes);
                }

                if (buffer.Length < bytes.Length) stored = buffer.ToArray();
            }

            bodies.Add((tag, stored, bytes.Length));
        }

        int directoryEnd = 44 + bodies.Count * 20;
        int length = directoryEnd;
        foreach (var body in bodies) length += (body.Stored.Length + 3) & ~3;
        var woff = new byte[length];
        WriteU32(woff, 0, 0x774F4646);
        Buffer.BlockCopy(sfnt, 0, woff, 4, 4); // flavor
        WriteU32(woff, 8, (uint)length);
        woff[12] = (byte)(bodies.Count >> 8);
        woff[13] = (byte)bodies.Count;
        WriteU32(woff, 16, (uint)sfnt.Length);

        int offset = directoryEnd;
        for (int i = 0; i < bodies.Count; i++)
        {
            int entry = 44 + i * 20;
            var (tag, stored, origLength) = bodies[i];
            System.Text.Encoding.ASCII.GetBytes(tag, 0, 4, woff, entry);
            WriteU32(woff, entry + 4, (uint)offset);
            WriteU32(woff, entry + 8, (uint)stored.Length);
            WriteU32(woff, entry + 12, (uint)origLength);
            Buffer.BlockCopy(stored, 0, woff, offset, stored.Length);
            offset += (stored.Length + 3) & ~3;
        }

        return woff;
    }

    private static uint ReadU32(byte[] data, int at) =>
        ((uint)data[at] << 24) | ((uint)data[at + 1] << 16) | ((uint)data[at + 2] << 8) | data[at + 3];

    private static void WriteU32(byte[] data, int at, uint value)
    {
        data[at] = (byte)(value >> 24);
        data[at + 1] = (byte)(value >> 16);
        data[at + 2] = (byte)(value >> 8);
        data[at + 3] = (byte)value;
    }
}
