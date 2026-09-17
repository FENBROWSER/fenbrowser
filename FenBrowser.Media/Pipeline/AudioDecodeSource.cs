using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Pipeline;

/// <summary>What opening a resource found: the container's tracks and the audio track that will be decoded.</summary>
public sealed record AudioSourceInfo(
    IReadOnlyList<MediaTrackInfo> Tracks,
    MediaTrackInfo AudioTrack,
    MediaTime Duration,
    bool IsSeekable);

/// <summary>
/// The demux-and-decode half of the §2.3 pipeline as one pull source: bytes in, decoded
/// audio out. The player does not know where it runs. <see cref="LocalAudioDecodeSource"/>
/// runs it on the caller's thread; the host's remote source runs the same contract in the
/// media process and moves blocks over shared memory (ADR-0004).
/// </summary>
/// <remarks>
/// Calls come from the player's media task, one at a time. <see cref="OpenAsync"/> throws
/// <see cref="MediaUnsupportedException"/> when nothing can play the resource,
/// <see cref="MediaFormatException"/>, <see cref="MediaDecoderException"/> or
/// <see cref="MediaLimitExceededException"/> when the resource is broken, and
/// <see cref="MediaProcessLostException"/> when the process doing the work went away.
/// </remarks>
public interface IAudioDecodeSource : IAsyncDisposable
{
    ValueTask<AudioSourceInfo> OpenAsync(CancellationToken cancellationToken);

    /// <summary>The next decoded block in stream order, or null at the end of the stream.</summary>
    ValueTask<AudioBlock?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Repositions at or before <paramref name="target"/>; the next read starts at the
    /// container's nearest earlier boundary and the player trims the rest.
    /// </summary>
    ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken);
}

public interface IAudioDecodeSourceFactory
{
    IAudioDecodeSource Create(IByteSource source, string? declaredMime, MediaPipelineContext context);
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

/// <summary>Demuxer and decoder from the registries, in this process.</summary>
public sealed class LocalAudioDecodeSourceFactory(DemuxerRegistry demuxers, DecoderRegistry decoders) : IAudioDecodeSourceFactory
{
    public IAudioDecodeSource Create(IByteSource source, string? declaredMime, MediaPipelineContext context) =>
        new LocalAudioDecodeSource(source, declaredMime, demuxers, decoders, context);
}

/// <summary>
/// Sniffs the resource (MIME Sniffing §6.2), picks the container and the first audio
/// track, selects a decoder and pulls packets through it on demand.
/// </summary>
public sealed class LocalAudioDecodeSource : IAudioDecodeSource
{
    private readonly IByteSource _source;
    private readonly string? _declaredMime;
    private readonly DemuxerRegistry _demuxers;
    private readonly DecoderRegistry _decoders;
    private readonly MediaPipelineContext _context;
    private readonly Queue<AudioBlock> _decoded = new();
    private readonly QueueOutput _output;
    private IDemuxer? _demuxer;
    private IMediaDecoder<AudioBlock>? _decoder;
    private int _trackId;
    private bool _endOfStream;

    public LocalAudioDecodeSource(
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
        _output = new QueueOutput(_decoded);
    }

    public async ValueTask<AudioSourceInfo> OpenAsync(CancellationToken cancellationToken)
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

        var track = info.Tracks.FirstOrDefault(t => t.Kind == MediaTrackKind.Audio)
            ?? throw new MediaUnsupportedException("The resource has no audio track; video playback is not available yet.");

        var candidates = _decoders.GetAudioCandidates(track.Config);
        _decoder = await DecoderSelector.SelectAsync(candidates, track.Config, _context, cancellationToken).ConfigureAwait(false)
            ?? throw new MediaUnsupportedException($"No decoder for {track.Config.Codec}.");

        _trackId = track.Id;
        return new AudioSourceInfo(info.Tracks, track, info.Duration, info.IsSeekable);
    }

    public async ValueTask<AudioBlock?> ReadAsync(CancellationToken cancellationToken)
    {
        var demuxer = _demuxer ?? throw new InvalidOperationException("The source is not open.");
        var decoder = _decoder!;
        while (_decoded.Count == 0 && !_endOfStream)
        {
            var packet = await demuxer.ReadPacketAsync(cancellationToken).ConfigureAwait(false);
            if (packet is null)
            {
                await decoder.DrainAsync(_output, cancellationToken).ConfigureAwait(false);
                _endOfStream = true;
                break;
            }

            using (packet)
            {
                if (packet.TrackId != _trackId)
                    continue;
                await decoder.DecodeAsync(packet, _output, cancellationToken).ConfigureAwait(false);
            }
        }

        return _decoded.Count > 0 ? _decoded.Dequeue() : null;
    }

    public async ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken)
    {
        var demuxer = _demuxer ?? throw new InvalidOperationException("The source is not open.");
        DropDecoded();
        _endOfStream = false;
        await _decoder!.ResetAsync(cancellationToken).ConfigureAwait(false);
        await demuxer.SeekAsync(target, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        DropDecoded();
        if (_decoder is not null)
            await _decoder.DisposeAsync().ConfigureAwait(false);
        if (_demuxer is not null)
            await _demuxer.DisposeAsync().ConfigureAwait(false);
        await _source.DisposeAsync().ConfigureAwait(false);
    }

    private void DropDecoded()
    {
        while (_decoded.TryDequeue(out var block))
            block.Dispose();
    }

    private sealed class QueueOutput(Queue<AudioBlock> queue) : IDecodeOutput<AudioBlock>
    {
        public void Emit(AudioBlock item) => queue.Enqueue(item);
    }
}
