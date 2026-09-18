using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Pipeline;

/// <summary>
/// What opening a resource found: the container's tracks and the audio and video tracks
/// that will be decoded (at least one of them).
/// </summary>
public sealed record MediaSourceInfo(
    IReadOnlyList<MediaTrackInfo> Tracks,
    MediaTrackInfo? AudioTrack,
    MediaTrackInfo? VideoTrack,
    MediaTime Duration,
    bool IsSeekable);

/// <summary>One decoded item in stream order: a block of audio or a picture, never both.</summary>
public readonly struct DecodedMedia : IDisposable
{
    public DecodedMedia(AudioBlock audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        Audio = audio;
    }

    public DecodedMedia(VideoFrame video)
    {
        ArgumentNullException.ThrowIfNull(video);
        Video = video;
    }

    public AudioBlock? Audio { get; }

    public VideoFrame? Video { get; }

    public MediaTime Timestamp => Audio?.Timestamp ?? Video!.Timestamp;

    public void Dispose()
    {
        Audio?.Dispose();
        Video?.Dispose();
    }
}

/// <summary>
/// The demux-and-decode half of the §2.3 pipeline as one pull source: bytes in, decoded
/// audio and pictures out in stream order. The player does not know where it runs.
/// <see cref="LocalMediaDecodeSource"/> runs it on the caller's thread; the host's remote
/// source runs the same contract in the media process and moves the output over shared
/// memory (ADR-0004).
/// </summary>
/// <remarks>
/// Calls come from the player's media task, one at a time. <see cref="OpenAsync"/> throws
/// <see cref="MediaUnsupportedException"/> when nothing can play the resource,
/// <see cref="MediaFormatException"/>, <see cref="MediaDecoderException"/> or
/// <see cref="MediaLimitExceededException"/> when the resource is broken, and
/// <see cref="MediaProcessLostException"/> when the process doing the work went away.
/// </remarks>
public interface IMediaDecodeSource : IAsyncDisposable
{
    ValueTask<MediaSourceInfo> OpenAsync(CancellationToken cancellationToken);

    /// <summary>The next decoded item in stream order, or null at the end of the stream.</summary>
    ValueTask<DecodedMedia?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Repositions at or before <paramref name="target"/>; the next read starts at the
    /// container's nearest earlier keyframe and the player discards the rest.
    /// </summary>
    ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken);
}

public interface IMediaDecodeSourceFactory
{
    IMediaDecodeSource Create(IByteSource source, string? declaredMime, MediaPipelineContext context);
}

/// <summary>No demuxer, track or decoder for the resource (the element reports MEDIA_ERR_SRC_NOT_SUPPORTED).</summary>
public sealed class MediaUnsupportedException : Exception
{
    public MediaUnsupportedException()
    {
    }

    public MediaUnsupportedException(string message)
        : base(message)
    {
    }

    public MediaUnsupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The process running the demuxer and decoder exited or stopped answering. Only the
/// players on it fail, with MEDIA_ERR_DECODE; the tab survives (design §2.2).
/// </summary>
public sealed class MediaProcessLostException : Exception
{
    public MediaProcessLostException()
    {
    }

    public MediaProcessLostException(string message)
        : base(message)
    {
    }

    public MediaProcessLostException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Demuxers and decoders from the registries, in this process.</summary>
public sealed class LocalMediaDecodeSourceFactory(DemuxerRegistry demuxers, DecoderRegistry decoders) : IMediaDecodeSourceFactory
{
    public IMediaDecodeSource Create(IByteSource source, string? declaredMime, MediaPipelineContext context) =>
        new LocalMediaDecodeSource(source, declaredMime, demuxers, decoders, context);
}

/// <summary>
/// Sniffs the resource (MIME Sniffing §6.2), picks the container, the first audio track
/// and the first video track, selects a decoder for each and pulls packets through them
/// on demand. A track with no decoder is left out; a resource with neither is unsupported.
/// </summary>
public sealed class LocalMediaDecodeSource : IMediaDecodeSource
{
    private readonly IByteSource _source;
    private readonly string? _declaredMime;
    private readonly DemuxerRegistry _demuxers;
    private readonly DecoderRegistry _decoders;
    private readonly MediaPipelineContext _context;
    private readonly Queue<DecodedMedia> _decoded = new();
    private readonly AudioOutput _audioOutput;
    private readonly VideoOutput _videoOutput;
    private IDemuxer? _demuxer;
    private IMediaDecoder<AudioBlock>? _audioDecoder;
    private IMediaDecoder<VideoFrame>? _videoDecoder;
    private int _audioTrackId = -1;
    private int _videoTrackId = -1;
    private bool _endOfStream;

    public LocalMediaDecodeSource(
        IByteSource source,
        string? declaredMime,
        DemuxerRegistry demuxers,
        DecoderRegistry decoders,
        MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(demuxers);
        ArgumentNullException.ThrowIfNull(decoders);
        ArgumentNullException.ThrowIfNull(context);
        _source = source;
        _declaredMime = declaredMime;
        _demuxers = demuxers;
        _decoders = decoders;
        _context = context;
        _audioOutput = new AudioOutput(_decoded);
        _videoOutput = new VideoOutput(_decoded);
    }

    public async ValueTask<MediaSourceInfo> OpenAsync(CancellationToken cancellationToken)
    {
        if (_demuxer is not null)
            throw new InvalidOperationException("The source is already open.");

        var header = new byte[Sniffing.MediaSniffer.ResourceHeaderLength];
        int headerLength = await _source.ReadAtLeastAsync(0, header, cancellationToken).ConfigureAwait(false);
        var factory = _demuxers.Select(header.AsSpan(0, headerLength), _declaredMime, _context)
            ?? throw new MediaUnsupportedException("No demuxer recognises the resource.");

        DemuxerInfo info;
        try
        {
            _demuxer = factory.Create(_source, _context);
            info = await _demuxer.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is MediaFormatException or MediaLimitExceededException)
        {
            throw new MediaUnsupportedException($"The {factory.Name} container could not be read: {ex.Message}", ex);
        }

        var audio = info.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio);
        var video = info.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Video);
        if (audio is null && video is null)
            throw new MediaUnsupportedException("The resource has no audio or video track.");

        if (audio is not null)
        {
            _audioDecoder = await DecoderSelector.SelectAsync(_decoders.GetAudioCandidates(audio.Config), audio.Config, _context, cancellationToken).ConfigureAwait(false);
            if (_audioDecoder is null)
                audio = null;
        }

        if (video is not null)
        {
            _videoDecoder = await DecoderSelector.SelectAsync(_decoders.GetVideoCandidates(video.Config), video.Config, _context, cancellationToken).ConfigureAwait(false);
            if (_videoDecoder is null)
                video = null;
        }

        if (audio is null && video is null)
            throw new MediaUnsupportedException($"No decoder for {string.Join(", ", info.Tracks.Select(t => t.Config.Codec))}.");

        _audioTrackId = audio?.Id ?? -1;
        _videoTrackId = video?.Id ?? -1;
        return new MediaSourceInfo(info.Tracks, audio, video, info.Duration, info.IsSeekable);
    }

    public async ValueTask<DecodedMedia?> ReadAsync(CancellationToken cancellationToken)
    {
        var demuxer = _demuxer ?? throw new InvalidOperationException("The source is not open.");
        while (_decoded.Count == 0 && !_endOfStream)
        {
            var packet = await demuxer.ReadPacketAsync(cancellationToken).ConfigureAwait(false);
            if (packet is null)
            {
                if (_audioDecoder is not null)
                    await _audioDecoder.DrainAsync(_audioOutput, cancellationToken).ConfigureAwait(false);
                if (_videoDecoder is not null)
                    await _videoDecoder.DrainAsync(_videoOutput, cancellationToken).ConfigureAwait(false);
                _endOfStream = true;
                break;
            }

            using (packet)
            {
                if (packet.TrackId == _audioTrackId)
                    await _audioDecoder!.DecodeAsync(packet, _audioOutput, cancellationToken).ConfigureAwait(false);
                else if (packet.TrackId == _videoTrackId)
                    await _videoDecoder!.DecodeAsync(packet, _videoOutput, cancellationToken).ConfigureAwait(false);
            }
        }

        return _decoded.Count > 0 ? _decoded.Dequeue() : null;
    }

    public async ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken)
    {
        var demuxer = _demuxer ?? throw new InvalidOperationException("The source is not open.");
        DropDecoded();
        _endOfStream = false;
        if (_audioDecoder is not null)
            await _audioDecoder.ResetAsync(cancellationToken).ConfigureAwait(false);
        if (_videoDecoder is not null)
            await _videoDecoder.ResetAsync(cancellationToken).ConfigureAwait(false);
        await demuxer.SeekAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        DropDecoded();
        if (_audioDecoder is not null)
            await _audioDecoder.DisposeAsync().ConfigureAwait(false);
        if (_videoDecoder is not null)
            await _videoDecoder.DisposeAsync().ConfigureAwait(false);
        if (_demuxer is not null)
            await _demuxer.DisposeAsync().ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
    }

    private void DropDecoded()
    {
        while (_decoded.TryDequeue(out var item))
            item.Dispose();
    }

    private sealed class AudioOutput(Queue<DecodedMedia> queue) : IDecodeOutput<AudioBlock>
    {
        public void Emit(AudioBlock item) => queue.Enqueue(new DecodedMedia(item));
    }

    private sealed class VideoOutput(Queue<DecodedMedia> queue) : IDecodeOutput<VideoFrame>
    {
        public void Emit(VideoFrame item) => queue.Enqueue(new DecodedMedia(item));
    }
}
