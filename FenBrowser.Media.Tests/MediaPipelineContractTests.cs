using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Tests;

public class MediaPipelineContractTests
{
    private sealed class FakeDemuxerFactory(string name, int score, params string[] mimeTypes) : IDemuxerFactory
    {
        public string Name => name;
        public IReadOnlyList<string> MimeTypes => mimeTypes;
        public int Probe(ReadOnlySpan<byte> header) => header.IsEmpty ? 0 : score;
        public IDemuxer Create(IByteSource source, MediaPipelineContext context) => throw new NotSupportedException();
    }

    private sealed class FakeDecoder(string name, Func<CodecConfig, Exception?> onConfigure) : IMediaDecoder<VideoFrame>
    {
        public bool Disposed { get; private set; }
        public string Name => name;

        public ValueTask ConfigureAsync(CodecConfig config, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var error = onConfigure(config);
            return error is null ? ValueTask.CompletedTask : ValueTask.FromException(error);
        }

        public ValueTask DecodeAsync(EncodedPacket packet, IDecodeOutput<VideoFrame> output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DrainAsync(IDecodeOutput<VideoFrame> output, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ResetAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeDecoderFactory(
        string name,
        DecoderSupport support,
        bool hardware = false,
        int priority = 0,
        Func<CodecConfig, Exception?>? onConfigure = null) : IDecoderFactory<VideoFrame>
    {
        public List<FakeDecoder> Created { get; } = [];
        public string Name => name;
        public bool IsHardwareAccelerated => hardware;
        public int Priority => priority;

        public DecoderSupport Supports(CodecConfig config) => config.Codec == MediaCodec.Vp9 ? support : DecoderSupport.Unsupported;

        public IMediaDecoder<VideoFrame> Create(MediaPipelineContext context)
        {
            var decoder = new FakeDecoder(name, onConfigure ?? (_ => null));
            Created.Add(decoder);
            return decoder;
        }
    }

    private static readonly CodecConfig Vp9 = CodecConfig.Video(MediaCodec.Vp9, 640, 360);

    [Fact]
    public async Task MemoryByteSource_ReadsRangesAndEnd()
    {
        await using var source = new MemoryByteSource(new byte[] { 1, 2, 3, 4, 5 });
        var buffer = new byte[3];

        Assert.Equal(5, source.Length);
        Assert.Equal(3, await source.ReadAsync(1, buffer, CancellationToken.None));
        Assert.Equal([2, 3, 4], buffer);
        Assert.Equal(1, await source.ReadAsync(4, buffer, CancellationToken.None));
        Assert.Equal(0, await source.ReadAsync(5, buffer, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await source.ReadAsync(-1, buffer, CancellationToken.None));
    }

    private sealed class TrickleSource(byte[] data) : IByteSource
    {
        public long? Length => null;

        public ValueTask<int> ReadAsync(long position, Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (position >= data.Length || destination.IsEmpty)
                return ValueTask.FromResult(0);
            destination.Span[0] = data[position];
            return ValueTask.FromResult(1);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ReadAtLeast_LoopsOverShortReads()
    {
        var source = new TrickleSource([9, 8, 7]);
        var buffer = new byte[5];
        Assert.Equal(3, await source.ReadAtLeastAsync(0, buffer, CancellationToken.None));
        Assert.Equal([9, 8, 7, 0, 0], buffer);
    }

    [Fact]
    public void DemuxerRegistry_ContentBeatsDeclaredType()
    {
        var registry = new DemuxerRegistry();
        registry.Register(new FakeDemuxerFactory("mp4", 10, "video/mp4"));
        registry.Register(new FakeDemuxerFactory("webm", 100, "video/webm"));
        var log = new RecordingMediaLogSink();

        var chosen = registry.Select([1], "video/mp4", MediaPipelineContext.ForTests(log));

        Assert.Equal("webm", chosen!.Name);
        var e = Assert.Single(log.Events);
        Assert.Equal(MediaEventKind.DemuxerChosen, e.Kind);
        Assert.Contains(new KeyValuePair<string, string>("declaredMatches", "false"), e.Fields!);
    }

    [Fact]
    public void DemuxerRegistry_DeclaredTypeBreaksTies()
    {
        var registry = new DemuxerRegistry();
        registry.Register(new FakeDemuxerFactory("ogg", 50, "audio/ogg"));
        registry.Register(new FakeDemuxerFactory("opus", 50, "audio/opus"));

        Assert.Equal("opus", registry.Select([1], "AUDIO/OPUS", MediaPipelineContext.ForTests())!.Name);
        Assert.Equal("ogg", registry.Select([1], null, MediaPipelineContext.ForTests())!.Name);
    }

    [Fact]
    public void DemuxerRegistry_NothingMatches_LogsAndReturnsNull()
    {
        var registry = new DemuxerRegistry();
        registry.Register(new FakeDemuxerFactory("webm", 100, "video/webm"));
        var log = new RecordingMediaLogSink();

        Assert.Null(registry.Select([], "video/webm", MediaPipelineContext.ForTests(log)));
        Assert.Equal(MediaEventKind.SniffResult, Assert.Single(log.Events).Kind);
    }

    private sealed class ThrowingProbeFactory : IDemuxerFactory
    {
        public string Name => "buggy";
        public IReadOnlyList<string> MimeTypes => ["video/mp4"];
        public int Probe(ReadOnlySpan<byte> header) => throw new IndexOutOfRangeException();
        public IDemuxer Create(IByteSource source, MediaPipelineContext context) => throw new NotSupportedException();
    }

    [Fact]
    public void DemuxerRegistry_ThrowingProbeLosesWithoutEndingSelection()
    {
        var registry = new DemuxerRegistry();
        registry.Register(new ThrowingProbeFactory());
        registry.Register(new FakeDemuxerFactory("webm", 40, "video/webm"));
        var log = new RecordingMediaLogSink();

        Assert.Equal("webm", registry.Select([1], "video/mp4", MediaPipelineContext.ForTests(log))!.Name);
        Assert.Equal(MediaLogLevel.Error, log.Events[0].Level);
        Assert.Contains(new KeyValuePair<string, string>("demuxer", "buggy"), log.Events[0].Fields!);
    }

    [Fact]
    public void DemuxerRegistry_RejectsDuplicateNames_AndAnswersMimeQueries()
    {
        var registry = new DemuxerRegistry();
        registry.Register(new FakeDemuxerFactory("webm", 100, "video/webm", "audio/webm"));

        Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeDemuxerFactory("webm", 1)));
        Assert.True(registry.SupportsMimeType("Audio/WebM"));
        Assert.False(registry.SupportsMimeType("video/mp4"));
    }

    [Fact]
    public void DecoderRegistry_OrdersHardwareThenSupportThenPriorityThenRegistration()
    {
        var registry = new DecoderRegistry();
        registry.Register(new FakeDecoderFactory("sw-maybe", DecoderSupport.Maybe, priority: 100));
        registry.Register(new FakeDecoderFactory("sw-a", DecoderSupport.Supported));
        registry.Register(new FakeDecoderFactory("sw-b", DecoderSupport.Supported));
        registry.Register(new FakeDecoderFactory("sw-high", DecoderSupport.Supported, priority: 5));
        registry.Register(new FakeDecoderFactory("hw", DecoderSupport.Maybe, hardware: true));
        registry.Register(new FakeDecoderFactory("never", DecoderSupport.Unsupported));

        var names = registry.GetVideoCandidates(Vp9).Select(f => f.Name);

        Assert.Equal(["hw", "sw-high", "sw-a", "sw-b", "sw-maybe"], names);
        Assert.Equal(DecoderSupport.Supported, registry.GetSupport(Vp9));
        Assert.Equal(DecoderSupport.Unsupported, registry.GetSupport(CodecConfig.Video(MediaCodec.Av1, 1, 1)));
        Assert.Equal(DecoderSupport.Unsupported, registry.GetSupport(CodecConfig.Audio(MediaCodec.Vp9, 48000, 2)));
    }

    [Fact]
    public void DecoderRegistry_KillSwitchAndQuarantine()
    {
        var registry = new DecoderRegistry();
        registry.Register(new FakeDecoderFactory("a", DecoderSupport.Supported));
        registry.Register(new FakeDecoderFactory("b", DecoderSupport.Supported));

        registry.Quarantine("a", "crashed three times");
        Assert.True(registry.IsQuarantined("a"));
        Assert.Equal(["b"], registry.GetVideoCandidates(Vp9).Select(f => f.Name));

        registry.DisableCodec(MediaCodec.Vp9);
        Assert.Empty(registry.GetVideoCandidates(Vp9));
        Assert.Equal(DecoderSupport.Unsupported, registry.GetSupport(Vp9));
    }

    [Fact]
    public void DecoderRegistry_RejectsDuplicateNames()
    {
        var registry = new DecoderRegistry();
        registry.Register(new FakeDecoderFactory("a", DecoderSupport.Supported));
        Assert.Throws<InvalidOperationException>(() => registry.Register(new FakeDecoderFactory("a", DecoderSupport.Maybe)));
    }

    [Fact]
    public async Task DecoderSelector_FallsBackAndDisposesFailures()
    {
        var hw = new FakeDecoderFactory("hw", DecoderSupport.Supported, hardware: true,
            onConfigure: _ => new MediaDecoderException("profile not supported by the GPU"));
        var sw = new FakeDecoderFactory("sw", DecoderSupport.Supported);
        var log = new RecordingMediaLogSink();

        var decoder = await DecoderSelector.SelectAsync([hw, sw], Vp9, MediaPipelineContext.ForTests(log), CancellationToken.None);

        Assert.Equal("sw", decoder!.Name);
        Assert.True(hw.Created.Single().Disposed);
        Assert.False(sw.Created.Single().Disposed);
        Assert.Equal([MediaEventKind.DecoderAttempt, MediaEventKind.DecoderChosen], log.Events.Select(e => e.Kind));
        Assert.Contains(new KeyValuePair<string, string>("reason", nameof(MediaDecoderException)), log.Events[0].Fields!);
    }

    [Fact]
    public async Task DecoderSelector_SurvivesAFactoryThatThrowsOnCreate()
    {
        var broken = new ThrowingFactory();
        var sw = new FakeDecoderFactory("sw", DecoderSupport.Supported);

        var decoder = await DecoderSelector.SelectAsync([broken, sw], Vp9, MediaPipelineContext.ForTests(), CancellationToken.None);

        Assert.Equal("sw", decoder!.Name);
    }

    private sealed class ThrowingFactory : IDecoderFactory<VideoFrame>
    {
        public string Name => "broken";
        public bool IsHardwareAccelerated => false;
        public int Priority => 0;
        public DecoderSupport Supports(CodecConfig config) => DecoderSupport.Supported;
        public IMediaDecoder<VideoFrame> Create(MediaPipelineContext context) => throw new DllNotFoundException("libavcodec");
    }

    [Fact]
    public async Task DecoderSelector_NoneWork_ReturnsNullAndLogsError()
    {
        var bad = new FakeDecoderFactory("bad", DecoderSupport.Supported, onConfigure: _ => new MediaDecoderException("nope"));
        var log = new RecordingMediaLogSink();

        Assert.Null(await DecoderSelector.SelectAsync([bad], Vp9, MediaPipelineContext.ForTests(log), CancellationToken.None));
        Assert.Equal(MediaEventKind.Error, log.Events[^1].Kind);
        Assert.Equal(MediaLogLevel.Error, log.Events[^1].Level);
    }

    [Fact]
    public async Task DecoderSelector_CancellationStopsTheSearch()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var first = new FakeDecoderFactory("first", DecoderSupport.Supported);
        var second = new FakeDecoderFactory("second", DecoderSupport.Supported);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await DecoderSelector.SelectAsync([first, second], Vp9, MediaPipelineContext.ForTests(), cts.Token));

        Assert.True(first.Created.Single().Disposed);
        Assert.Empty(second.Created);
    }
}
