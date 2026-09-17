using System.Globalization;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;

namespace FenBrowser.Media.Pipeline;

/// <summary>Receives decoder output. Ownership of each item moves to the receiver.</summary>
public interface IDecodeOutput<in T>
    where T : IDisposable
{
    void Emit(T item);
}

/// <summary>
/// A decoder for one track. Calls come from the player's media task queue, one at a time.
/// </summary>
public interface IMediaDecoder<TOutput> : IAsyncDisposable
    where TOutput : IDisposable
{
    string Name { get; }

    /// <summary>Prepares for <paramref name="config"/>. Throws <see cref="MediaDecoderException"/> when it cannot.</summary>
    ValueTask ConfigureAsync(CodecConfig config, CancellationToken cancellationToken);

    /// <summary>Decodes one packet and emits zero or more outputs. The caller keeps ownership of the packet.</summary>
    ValueTask DecodeAsync(EncodedPacket packet, IDecodeOutput<TOutput> output, CancellationToken cancellationToken);

    /// <summary>At end of stream, emits everything still buffered inside the decoder.</summary>
    ValueTask DrainAsync(IDecodeOutput<TOutput> output, CancellationToken cancellationToken);

    /// <summary>Drops all internal state so decoding can restart at the next keyframe (seek).</summary>
    ValueTask ResetAsync(CancellationToken cancellationToken);
}

public enum DecoderSupport
{
    Unsupported,

    /// <summary>The codec is handled, but this particular configuration may fail at configure time.</summary>
    Maybe,

    Supported,
}

public interface IDecoderFactory<TOutput>
    where TOutput : IDisposable
{
    /// <summary>Unique, stable name used in logs and for quarantine, for example <c>libavcodec-vp9</c>.</summary>
    string Name { get; }

    bool IsHardwareAccelerated { get; }

    /// <summary>Higher runs first among factories of the same kind and support level.</summary>
    int Priority { get; }

    DecoderSupport Supports(CodecConfig config);

    IMediaDecoder<TOutput> Create(MediaPipelineContext context);
}

/// <summary>A decoder cannot handle its input.</summary>
public sealed class MediaDecoderException : Exception
{
    public MediaDecoderException()
    {
    }

    public MediaDecoderException(string message)
        : base(message)
    {
    }

    public MediaDecoderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The decoders available to this process, with a codec kill switch and per-decoder
/// quarantine (docs/MEDIA_ENGINE_DESIGN.md §4).
/// </summary>
public sealed class DecoderRegistry
{
    private readonly Lock _gate = new();
    private readonly List<IDecoderFactory<VideoFrame>> _video = [];
    private readonly List<IDecoderFactory<AudioBlock>> _audio = [];
    private readonly HashSet<MediaCodec> _disabledCodecs = [];
    private readonly Dictionary<string, string> _quarantined = new(StringComparer.Ordinal);

    public void Register(IDecoderFactory<VideoFrame> factory) => Add(_video, factory);

    public void Register(IDecoderFactory<AudioBlock> factory) => Add(_audio, factory);

    /// <summary>Turns a codec off for this process, for example from <c>FEN_MEDIA_DISABLE_CODECS</c>.</summary>
    public void DisableCodec(MediaCodec codec)
    {
        lock (_gate)
            _disabledCodecs.Add(codec);
    }

    /// <summary>Stops offering a decoder for the rest of the session, for example after repeated crashes.</summary>
    public void Quarantine(string factoryName, string reason)
    {
        ArgumentNullException.ThrowIfNull(factoryName);
        lock (_gate)
            _quarantined[factoryName] = reason;
    }

    public bool IsQuarantined(string factoryName)
    {
        lock (_gate)
            return _quarantined.ContainsKey(factoryName);
    }

    public IReadOnlyList<IDecoderFactory<VideoFrame>> GetVideoCandidates(CodecConfig config) => Candidates(_video, config);

    public IReadOnlyList<IDecoderFactory<AudioBlock>> GetAudioCandidates(CodecConfig config) => Candidates(_audio, config);

    /// <summary>The best support any usable decoder offers; this is what <c>canPlayType</c> builds on.</summary>
    public DecoderSupport GetSupport(CodecConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return config.Kind switch
        {
            MediaTrackKind.Video => BestSupport(_video, config),
            MediaTrackKind.Audio => BestSupport(_audio, config),
            _ => DecoderSupport.Unsupported,
        };
    }

    private void Add<T>(List<IDecoderFactory<T>> list, IDecoderFactory<T> factory)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (_gate)
        {
            if (_video.Any(f => f.Name == factory.Name) || _audio.Any(f => f.Name == factory.Name))
                throw new InvalidOperationException($"A decoder named '{factory.Name}' is already registered.");
            list.Add(factory);
        }
    }

    private List<IDecoderFactory<T>> Candidates<T>(List<IDecoderFactory<T>> list, CodecConfig config)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(config);
        var usable = new List<(IDecoderFactory<T> Factory, DecoderSupport Support, int Order)>();
        lock (_gate)
        {
            if (_disabledCodecs.Contains(config.Codec))
                return [];

            for (int i = 0; i < list.Count; i++)
            {
                var factory = list[i];
                if (_quarantined.ContainsKey(factory.Name))
                    continue;
                var support = factory.Supports(config);
                if (support != DecoderSupport.Unsupported)
                    usable.Add((factory, support, i));
            }
        }

        // Hardware first, then definite support, then priority, then registration order.
        usable.Sort(static (a, b) =>
        {
            int c = b.Factory.IsHardwareAccelerated.CompareTo(a.Factory.IsHardwareAccelerated);
            if (c == 0)
                c = b.Support.CompareTo(a.Support);
            if (c == 0)
                c = b.Factory.Priority.CompareTo(a.Factory.Priority);
            if (c == 0)
                c = a.Order.CompareTo(b.Order);
            return c;
        });
        return usable.ConvertAll(u => u.Factory);
    }

    private DecoderSupport BestSupport<T>(List<IDecoderFactory<T>> list, CodecConfig config)
        where T : IDisposable
    {
        var best = DecoderSupport.Unsupported;
        foreach (var factory in Candidates(list, config))
        {
            var support = factory.Supports(config);
            if (support > best)
                best = support;
        }

        return best;
    }
}

/// <summary>
/// Tries decoder candidates in order and keeps the first one that configures. A decoder
/// that throws is disposed and the next one is tried (Chromium's DecoderSelector model).
/// </summary>
public static class DecoderSelector
{
    public static async ValueTask<IMediaDecoder<T>?> SelectAsync<T>(
        IReadOnlyList<IDecoderFactory<T>> candidates,
        CodecConfig config,
        MediaPipelineContext context,
        CancellationToken cancellationToken)
        where T : IDisposable
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(context);

        string codec = config.Codec.ToString();
        for (int i = 0; i < candidates.Count; i++)
        {
            var factory = candidates[i];
            IMediaDecoder<T>? decoder = null;
            try
            {
                decoder = factory.Create(context);
                await decoder.ConfigureAsync(config, cancellationToken).ConfigureAwait(false);

                context.Log.Emit(context.Player, MediaEventKind.DecoderChosen, MediaLogLevel.Info,
                    $"Decoding {codec} with {factory.Name}.",
                    ("decoder", factory.Name), ("codec", codec),
                    ("hardware", factory.IsHardwareAccelerated ? "true" : "false"),
                    ("attempt", (i + 1).ToString(CultureInfo.InvariantCulture)));
                return decoder;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (decoder is not null)
                    await decoder.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                context.Log.Emit(context.Player, MediaEventKind.DecoderAttempt, MediaLogLevel.Warn,
                    $"{factory.Name} could not decode {codec}: {ex.Message}",
                    ("decoder", factory.Name), ("codec", codec), ("result", "failed"), ("reason", ex.GetType().Name));
                if (decoder is not null)
                    await decoder.DisposeAsync().ConfigureAwait(false);
            }
        }

        context.Log.Emit(context.Player, MediaEventKind.Error, MediaLogLevel.Error,
            $"No decoder can handle {codec}.",
            ("codec", codec), ("candidates", candidates.Count.ToString(CultureInfo.InvariantCulture)));
        return null;
    }
}
