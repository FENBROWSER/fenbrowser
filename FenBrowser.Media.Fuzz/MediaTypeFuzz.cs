using System.Text;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Types;
using Xunit;

namespace FenBrowser.Media.Fuzz;

// Fuzz target for the MIME type parser, the codecs parser and canPlayType.
//
// Properties enforced on every input:
//   * MimeType.Parse, CodecString.Parse and CanPlayType never throw.
//   * A parsed MIME type serializes to a string that parses back to the same serialization.
//   * Type and subtype are ASCII lowercase HTTP tokens; parameter names are lowercase.
//   * canPlayType on an unparseable type is always the empty string.
public sealed class MediaTypeFuzz
{
    private const int Iterations = 12_000;

    private static readonly string[] s_fragments =
    [
        "video", "audio", "application", "/", "webm", "mp4", "ogg", "mpeg", "octet-stream", ";", "=", "\"", "\\",
        " ", "\t", "\r", "\n", ",", "codecs", "CODECS", "charset", "vp8", "vp09.00.10.08", "av01.0.04M.08",
        "avc1.42E01E", "mp4a.40.2", "opus", "flac", "1", ".", "ÿ", "Ā", " ", "\0", "x",
    ];

    private sealed class Container : IDemuxerFactory
    {
        public string Name => "all";
        public IReadOnlyList<string> MimeTypes => [.. MediaContainerType.Known.Keys];
        public int Probe(ReadOnlySpan<byte> header) => 0;
        public IDemuxer Create(IByteSource source, MediaPipelineContext context) => throw new NotSupportedException();
    }

    private sealed class Everything<T>(string name) : IDecoderFactory<T>
        where T : IDisposable
    {
        public string Name => name;
        public bool IsHardwareAccelerated => false;
        public int Priority => 0;
        public DecoderSupport Supports(CodecConfig config) => DecoderSupport.Supported;
        public IMediaDecoder<T> Create(MediaPipelineContext context) => throw new NotSupportedException();
    }

    private static string RandomInput(Random random)
    {
        var sb = new StringBuilder();

        // Half the inputs start with a well-formed essence so the parameter parser gets exercised.
        if (random.Next(2) == 0)
        {
            sb.Append(random.Next(3) switch { 0 => "video", 1 => "Audio", _ => "application" })
              .Append('/')
              .Append(random.Next(3) switch { 0 => "webm", 1 => "MP4", _ => "octet-stream" });
            if (random.Next(3) == 0)
                sb.Append(' ');
            sb.Append(';');
        }

        int pieces = random.Next(0, 16);
        for (int i = 0; i < pieces; i++)
        {
            if (random.Next(4) == 0)
                sb.Append((char)random.Next(0, 0x180));
            else
                sb.Append(s_fragments[random.Next(s_fragments.Length)]);
        }

        return sb.ToString();
    }

    [Theory]
    [InlineData(31)]
    [InlineData(32)]
    public void MimeType_ParseRoundTripsAndNeverThrows(int seed)
    {
        var random = new Random(seed);
        int parsed = 0;
        for (int i = 0; i < Iterations; i++)
        {
            string input = RandomInput(random);
            MimeType? mime;
            try
            {
                mime = MimeType.Parse(input);
            }
            catch (Exception ex)
            {
                Assert.Fail($"Parse threw {ex.GetType().Name} for {Escape(input)}");
                return;
            }

            if (mime is null)
                continue;
            parsed++;

            Assert.True(IsLowerToken(mime.Type) && IsLowerToken(mime.Subtype), $"bad type for {Escape(input)}");
            Assert.All(mime.Parameters.Keys, k => Assert.True(IsLowerToken(k), $"bad parameter name '{k}' for {Escape(input)}"));

            string serialized = mime.ToString();
            var again = MimeType.Parse(serialized);
            Assert.True(again is not null, $"serialization {Escape(serialized)} of {Escape(input)} does not parse");
            Assert.Equal(serialized, again.ToString());
        }

        // The generator must keep producing parseable types, or the round trip is untested.
        Assert.True(parsed > Iterations / 20, $"only {parsed} of {Iterations} inputs parsed");
    }

    [Fact]
    public void CanPlayType_NeverThrowsAndRejectsUnparseable()
    {
        var demuxers = new DemuxerRegistry();
        demuxers.Register(new Container());
        var decoders = new DecoderRegistry();
        decoders.Register(new Everything<VideoFrame>("v"));
        decoders.Register(new Everything<AudioBlock>("a"));
        var support = new MediaTypeSupport(demuxers, decoders);

        var random = new Random(33);
        for (int i = 0; i < Iterations; i++)
        {
            string input = RandomInput(random);
            try
            {
                var answer = support.CanPlayType(input);
                _ = support.KnowsItCannotRender(input);
                if (MimeType.Parse(input) is null)
                    Assert.Equal(CanPlayTypeResult.No, answer);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                Assert.Fail($"CanPlayType threw {ex.GetType().Name} for {Escape(input)}");
            }
        }
    }

    [Fact]
    public void CodecString_NeverThrows()
    {
        var random = new Random(34);
        for (int i = 0; i < Iterations; i++)
        {
            string input = RandomInput(random).Replace(",", "", StringComparison.Ordinal);
            try
            {
                var codec = CodecString.Parse(input);
                if (codec is not null)
                    Assert.Equal(input, codec.Raw);
                _ = CodecString.SplitList(input);
            }
            catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
            {
                Assert.Fail($"CodecString threw {ex.GetType().Name} for {Escape(input)}");
            }
        }
    }

    private static bool IsLowerToken(string s) =>
        s.Length > 0 && s.All(c => c < 0x80 && !char.IsAsciiLetterUpper(c)) && MimeTypeTokenCheck(s);

    private static bool MimeTypeTokenCheck(string s) =>
        s.All(c => char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal));

    private static string Escape(string s) =>
        "\"" + string.Concat(s.Select(c => c is < ' ' or > '~' ? $"\\u{(int)c:X4}" : c.ToString())) + "\"";
}
