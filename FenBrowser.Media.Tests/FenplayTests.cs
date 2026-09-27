using System.Text.Json;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Shell;

namespace FenBrowser.Media.Tests;

public class FenplayTests
{
    private sealed class WebMProbe : IDemuxerFactory
    {
        public string Name => "webm";
        public IReadOnlyList<string> MimeTypes => ["video/webm", "audio/webm"];
        public int Probe(ReadOnlySpan<byte> header) => header.StartsWith((ReadOnlySpan<byte>)[0x1A, 0x45, 0xDF, 0xA3]) ? 100 : 0;
        public IDemuxer Create(IByteSource source, MediaPipelineContext context) => throw new NotSupportedException();
    }

    private static async Task<(int Exit, string Out, string Err)> Run(DemuxerRegistry registry, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exit = await new Fenplay(output, error, registry).RunAsync(args, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    private static string FixturePath(string file) => Path.Combine(MediaFixtures.Directory, file);

    [Fact]
    public async Task NoArguments_PrintsUsageAndFails()
    {
        var (exit, output, _) = await Run(new DemuxerRegistry());
        Assert.Equal(Fenplay.ExitUsage, exit);
        Assert.Contains("fenplay probe", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownCommand_IsAUsageError()
    {
        var (exit, _, error) = await Run(new DemuxerRegistry(), "transcode");
        Assert.Equal(Fenplay.ExitUsage, exit);
        Assert.Contains("unknown command", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_ChoosesARegisteredDemuxer()
    {
        var registry = new DemuxerRegistry();
        registry.Register(new WebMProbe());

        var (exit, output, _) = await Run(registry, "probe", FixturePath("pattern_vp9.webm"), "--json");

        Assert.Equal(Fenplay.ExitOk, exit);
        var report = JsonSerializer.Deserialize<Fenplay.ProbeReport>(output)!;
        Assert.Equal("pattern_vp9.webm", report.File);
        Assert.Equal("video/webm", report.Sniffed);
        Assert.Equal("webm", report.Demuxer);
        Assert.Equal(MediaFixtures.Get("pattern_vp9.webm").Bytes, report.Bytes);
    }

    [Fact]
    public async Task Probe_WithoutADemuxer_ReportsAndFails()
    {
        var (exit, output, error) = await Run(new DemuxerRegistry(), "probe", FixturePath("sine_opus.ogg"), "--log");

        Assert.Equal(Fenplay.ExitInputError, exit);
        Assert.Contains("sniffed:   application/ogg", output, StringComparison.Ordinal);
        Assert.Contains("no demuxers registered yet", output, StringComparison.Ordinal);
        Assert.Contains("SniffResult", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_MissingFile_IsAnInputError()
    {
        var (exit, _, error) = await Run(new DemuxerRegistry(), "probe", Path.Combine(MediaFixtures.Directory, "missing.webm"));
        Assert.Equal(Fenplay.ExitInputError, exit);
        Assert.Contains("cannot read", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Probe_BadArguments_IsAUsageError()
    {
        var (exit, _, _) = await Run(new DemuxerRegistry(), "probe");
        Assert.Equal(Fenplay.ExitUsage, exit);
    }

    [Fact]
    public async Task Limits_PrintsJson()
    {
        var (exit, output, _) = await Run(new DemuxerRegistry(), "limits");
        Assert.Equal(Fenplay.ExitOk, exit);
        using var doc = JsonDocument.Parse(output);
        Assert.Equal(8192, doc.RootElement.GetProperty(nameof(MediaLimits.MaxVideoWidth)).GetInt32());
    }
}
