using System.Globalization;
using System.Text.Json;
using FenBrowser.Media.Audio;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Sniffing;
using FenBrowser.Media.Video;

namespace FenBrowser.Media.Shell;

/// <summary>
/// The <c>fenplay</c> commands. Output goes to the given writers so tests can drive it.
/// </summary>
public sealed class Fenplay
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;
    public const int ExitInputError = 3;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private readonly TextWriter _out;
    private readonly TextWriter _err;
    private readonly DemuxerRegistry _demuxers;
    private readonly DecoderRegistry _decoders;

    public Fenplay(TextWriter output, TextWriter error, DemuxerRegistry demuxers, DecoderRegistry? decoders = null)
    {
        _out = output;
        _err = error;
        _demuxers = demuxers;
        _decoders = decoders ?? new DecoderRegistry();
    }

    public const string Usage = """
        fenplay - FenBrowser media engine tool

        usage:
          fenplay probe <file> [--json] [--log]   sniff a file and choose a demuxer
          fenplay play <file> [--null-sink] [--rate R] [--seek S] [--log]
                                                  play a file on the default device (or the
                                                  null sink) to its end and print metrics
          fenplay bench <file>                    decode every picture as fast as possible and
                                                  report decode and colour-conversion rates
          fenplay limits                          print the default media limits
          fenplay help                            show this text

        Exit codes: 0 ok, 2 usage error, 3 unreadable or unsupported input.
        """;

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0 || args[0] is "help" or "-h" or "--help")
        {
            await _out.WriteLineAsync(Usage).ConfigureAwait(false);
            return args.Count == 0 ? ExitUsage : ExitOk;
        }

        switch (args[0])
        {
            case "probe":
                return await ProbeAsync(args.Skip(1).ToList(), cancellationToken).ConfigureAwait(false);
            case "play":
                return await PlayAsync(args.Skip(1).ToList(), cancellationToken).ConfigureAwait(false);
            case "bench":
                return await BenchAsync(args.Skip(1).ToList(), cancellationToken).ConfigureAwait(false);
            case "limits":
                await _out.WriteLineAsync(JsonSerializer.Serialize(MediaLimits.Default, s_json)).ConfigureAwait(false);
                return ExitOk;
            default:
                await _err.WriteLineAsync($"fenplay: unknown command '{args[0]}'. Try 'fenplay help'.").ConfigureAwait(false);
                return ExitUsage;
        }
    }

    private async Task<int> ProbeAsync(List<string> args, CancellationToken cancellationToken)
    {
        bool json = args.Remove("--json");
        bool log = args.Remove("--log");
        if (args.Count != 1 || args[0].StartsWith('-'))
        {
            await _err.WriteLineAsync("usage: fenplay probe <file> [--json] [--log]").ConfigureAwait(false);
            return ExitUsage;
        }

        string path = args[0];
        byte[] header;
        long length;
        try
        {
            await using var file = File.OpenRead(path);
            length = file.Length;
            header = new byte[(int)Math.Min(length, MediaSniffer.ResourceHeaderLength)];
            await file.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _err.WriteLineAsync($"fenplay: cannot read '{path}': {ex.Message}").ConfigureAwait(false);
            return ExitInputError;
        }

        IMediaLogSink sink = log ? new TextMediaLogSink(_err) : NullMediaLogSink.Instance;
        var context = new MediaPipelineContext(PlayerId.Next(), MediaLimits.Default, sink);
        string? sniffed = MediaSniffer.Sniff(header);
        var demuxer = _demuxers.Select(header, sniffed, context);

        var report = new ProbeReport(
            Path.GetFileName(path),
            length,
            sniffed,
            demuxer?.Name,
            _demuxers.Factories.Select(f => f.Name).ToArray());

        if (json)
        {
            await _out.WriteLineAsync(JsonSerializer.Serialize(report, s_json)).ConfigureAwait(false);
        }
        else
        {
            await _out.WriteLineAsync($"file:      {report.File} ({report.Bytes.ToString(CultureInfo.InvariantCulture)} bytes)").ConfigureAwait(false);
            await _out.WriteLineAsync($"sniffed:   {report.Sniffed ?? "undefined"}").ConfigureAwait(false);
            await _out.WriteLineAsync($"demuxer:   {report.Demuxer ?? "none"}").ConfigureAwait(false);
            await _out.WriteLineAsync($"available: {(report.Registered.Length == 0 ? "(no demuxers registered yet)" : string.Join(", ", report.Registered))}").ConfigureAwait(false);
        }

        return demuxer is null ? ExitInputError : ExitOk;
    }

    public sealed record ProbeReport(string File, long Bytes, string? Sniffed, string? Demuxer, string[] Registered);

    /// <summary>
    /// Plays a file through the real pipeline and prints what the element would have seen:
    /// metadata, the position at the end, the number of position reports, the wall-clock
    /// time it took and, for video, the time to the first picture and the presented and
    /// dropped picture counts (the design §5 budgets).
    /// </summary>
    private async Task<int> PlayAsync(List<string> args, CancellationToken cancellationToken)
    {
        bool log = args.Remove("--log");
        bool nullSink = args.Remove("--null-sink");
        double rate = 1.0;
        double? seek = null;
        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == "--rate" && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out rate))
            {
                args.RemoveRange(i, 2);
                i--;
            }
            else if (args[i] == "--seek" && double.TryParse(args[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s))
            {
                seek = s;
                args.RemoveRange(i, 2);
                i--;
            }
        }

        if (args.Count != 1 || args[0].StartsWith('-'))
        {
            await _err.WriteLineAsync("usage: fenplay play <file> [--null-sink] [--rate R] [--seek S] [--log]").ConfigureAwait(false);
            return ExitUsage;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(args[0], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _err.WriteLineAsync($"fenplay: cannot read '{args[0]}': {ex.Message}").ConfigureAwait(false);
            return ExitInputError;
        }

        IMediaLogSink sink = log ? new TextMediaLogSink(_err) : NullMediaLogSink.Instance;
        var context = new MediaPipelineContext(PlayerId.Next(), MediaLimits.Default, sink);
        var client = new PlayClient();
        IAudioOutputFactory outputs = nullSink ? Audio.NullAudioOutputFactory.Realtime : Audio.Windows.PlatformAudioOutputFactory.Instance;
        var services = new MediaPlayerServices(_demuxers, _decoders, outputs, TimeProvider.System);
        var player = new MediaPlayer(new MemoryByteSource(bytes), null, client, action => action(), services, context);
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        TimeSpan? firstPictureAfterPlay = null;
        TimeSpan? playStartedAt = null;
        long presentedAfterPlay = 0;
        player.Presenter.FrameAvailable += () =>
        {
            if (playStartedAt is { } started)
            {
                firstPictureAfterPlay ??= stopwatch.Elapsed - started;
                presentedAfterPlay++;
            }
        };
        player.Start();
        try
        {
            while (!client.Done.IsSet)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (client.Metadata is not null && !client.Started && client.ReadyState >= MediaReadyState.HaveFutureData)
                {
                    client.Started = true;
                    if (seek is { } target)
                        player.Seek(MediaTime.FromSeconds(target), approximateForSpeed: false);
                    playStartedAt = stopwatch.Elapsed;
                    player.UpdatePlayback(potentiallyPlaying: true, playbackRate: rate, preservesPitch: true, effectiveVolume: 1.0);
                }

                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            player.Dispose();
        }

        stopwatch.Stop();
        if (client.Failure is not null)
        {
            await _err.WriteLineAsync($"fenplay: {client.Failure}").ConfigureAwait(false);
            return ExitInputError;
        }

        var metadata = client.Metadata!;
        var quality = player.GetVideoPlaybackQuality();
        await _out.WriteLineAsync($"file:       {Path.GetFileName(args[0])} ({bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes)").ConfigureAwait(false);
        await _out.WriteLineAsync($"duration:   {metadata.Duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} s").ConfigureAwait(false);
        await _out.WriteLineAsync($"tracks:     {string.Join(", ", metadata.Tracks.Select(t => t.Kind == MediaTrackKind.Video ? $"video {t.Config.Codec} {t.Config.Width}x{t.Config.Height}" : $"{t.Kind.ToString().ToLowerInvariant()} {t.Config.Codec} {t.Config.SampleRate}Hz {t.Config.Channels}ch"))}").ConfigureAwait(false);
        await _out.WriteLineAsync($"ended at:   {client.Position.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} s").ConfigureAwait(false);
        await _out.WriteLineAsync($"wall clock: {stopwatch.Elapsed.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} s at rate {rate.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
        await _out.WriteLineAsync($"timeupdate: {client.PositionReports.ToString(CultureInfo.InvariantCulture)} position reports").ConfigureAwait(false);
        if (quality is { } q)
        {
            await _out.WriteLineAsync($"video:      {metadata.VideoWidth.ToString(CultureInfo.InvariantCulture)}x{metadata.VideoHeight.ToString(CultureInfo.InvariantCulture)}, {q.TotalVideoFrames.ToString(CultureInfo.InvariantCulture)} decoded, {presentedAfterPlay.ToString(CultureInfo.InvariantCulture)} presented, {q.DroppedVideoFrames.ToString(CultureInfo.InvariantCulture)} dropped ({(q.TotalVideoFrames == 0 ? 0 : 100.0 * q.DroppedVideoFrames / q.TotalVideoFrames).ToString("F2", CultureInfo.InvariantCulture)} %)").ConfigureAwait(false);
            await _out.WriteLineAsync($"first pic:  {(firstPictureAfterPlay is { } ttff ? ttff.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture) + " ms after play" : "never")}").ConfigureAwait(false);
        }
        await _out.WriteLineAsync($"output:     {(nullSink ? "null sink" : "default device")}{(Audio.Windows.PlatformAudioOutputFactory.Instance.LastFailure is { } failure ? " (fell back to the null sink: " + failure + ")" : string.Empty)}").ConfigureAwait(false);
        return ExitOk;
    }

    /// <summary>
    /// The design §5 benchmark: demux and decode every picture without a clock, then convert
    /// each to BGRA, timing the two stages apart so a regression names its stage.
    /// </summary>
    private async Task<int> BenchAsync(List<string> args, CancellationToken cancellationToken)
    {
        if (args.Count != 1 || args[0].StartsWith('-'))
        {
            await _err.WriteLineAsync("usage: fenplay bench <file>").ConfigureAwait(false);
            return ExitUsage;
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(args[0], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _err.WriteLineAsync($"fenplay: cannot read '{args[0]}': {ex.Message}").ConfigureAwait(false);
            return ExitInputError;
        }

        var context = new MediaPipelineContext(PlayerId.Next(), MediaLimits.Default, NullMediaLogSink.Instance);
        await using var source = new LocalMediaDecodeSource(new MemoryByteSource(bytes), null, _demuxers, _decoders, context);
        MediaSourceInfo info;
        try
        {
            info = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaUnsupportedException or MediaFormatException or MediaDecoderException or MediaLimitExceededException)
        {
            await _err.WriteLineAsync($"fenplay: {ex.Message}").ConfigureAwait(false);
            return ExitInputError;
        }

        if (info.VideoTrack is null)
        {
            await _err.WriteLineAsync("fenplay: the file has no video track to benchmark.").ConfigureAwait(false);
            return ExitInputError;
        }

        var decode = System.Diagnostics.Stopwatch.StartNew();
        var convert = new System.Diagnostics.Stopwatch();
        long pictures = 0;
        long audioBlocks = 0;
        byte[]? bgra = null;
        int width = 0, height = 0;
        while (await source.ReadAsync(cancellationToken).ConfigureAwait(false) is { } item)
        {
            using (item)
            {
                if (item.Video is not { } frame)
                {
                    audioBlocks++;
                    continue;
                }

                pictures++;
                width = frame.Width;
                height = frame.Height;
                int stride = frame.Width * 4;
                if (bgra is null || bgra.Length < stride * frame.Height)
                    bgra = new byte[stride * frame.Height];
                decode.Stop();
                convert.Start();
                PixelConverter.ToBgra(frame, bgra, stride);
                convert.Stop();
                decode.Start();
            }
        }

        decode.Stop();
        double decodeMs = decode.Elapsed.TotalMilliseconds;
        double convertMs = convert.Elapsed.TotalMilliseconds;
        await _out.WriteLineAsync($"file:     {Path.GetFileName(args[0])} ({bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes)").ConfigureAwait(false);
        await _out.WriteLineAsync($"video:    {info.VideoTrack.Config.Codec} {width.ToString(CultureInfo.InvariantCulture)}x{height.ToString(CultureInfo.InvariantCulture)}, {pictures.ToString(CultureInfo.InvariantCulture)} pictures, {audioBlocks.ToString(CultureInfo.InvariantCulture)} audio blocks").ConfigureAwait(false);
        await _out.WriteLineAsync($"decode:   {decodeMs.ToString("F0", CultureInfo.InvariantCulture)} ms, {(pictures == 0 ? 0 : decodeMs / pictures).ToString("F2", CultureInfo.InvariantCulture)} ms/picture, {(decodeMs <= 0 ? 0 : pictures * 1000.0 / decodeMs).ToString("F1", CultureInfo.InvariantCulture)} pictures/s (demux + decode + audio)").ConfigureAwait(false);
        await _out.WriteLineAsync($"convert:  {convertMs.ToString("F0", CultureInfo.InvariantCulture)} ms, {(pictures == 0 ? 0 : convertMs / pictures).ToString("F2", CultureInfo.InvariantCulture)} ms/picture, {(convertMs <= 0 ? 0 : pictures * 1000.0 / convertMs).ToString("F1", CultureInfo.InvariantCulture)} pictures/s (YUV to BGRA)").ConfigureAwait(false);
        return ExitOk;
    }

    private sealed class PlayClient : IMediaResourceClient
    {
        public ManualResetEventSlim Done { get; } = new();
        public MediaResourceMetadata? Metadata { get; private set; }
        public MediaReadyState ReadyState { get; private set; }
        public MediaTime Position { get; private set; }
        public string? Failure { get; private set; }
        public bool Started { get; set; }
        public int PositionReports { get; private set; }

        public void Failed(MediaResourceFailure failure, string message)
        {
            Failure = $"{failure}: {message}";
            Done.Set();
        }

        public void MetadataAvailable(MediaResourceMetadata metadata) => Metadata = metadata;
        public void ReadyStateChanged(MediaReadyState state) => ReadyState = state;
        public void DurationChanged(MediaTime duration) { }
        public void VideoSizeChanged(int width, int height) { }

        public void PositionChanged(MediaTime position, bool monotonic)
        {
            Position = position;
            PositionReports++;
        }

        public void ReachedEnd() => Done.Set();
        public void SeekCompleted(MediaTime position) { }
        public void BufferedChanged(MediaTimeRanges buffered) { }
        public void SeekableChanged(MediaTimeRanges seekable) { }
        public void Progress() { }
        public void Suspended() { }
        public void Resumed() { }
        public void Stalled() { }
        public void FetchedEntirely() { }
    }
}

/// <summary>Writes media events as one line each, for <c>fenplay --log</c>.</summary>
public sealed class TextMediaLogSink(TextWriter writer) : IMediaLogSink
{
    public bool IsEnabled(MediaLogLevel level) => true;

    public void Log(in MediaLogEvent logEvent)
    {
        string fields = logEvent.Fields is { Count: > 0 } f
            ? " " + string.Join(" ", f.Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
        writer.WriteLine($"[{logEvent.Level}] {logEvent.Player} {logEvent.Kind}: {logEvent.Message}{fields}");
    }
}
