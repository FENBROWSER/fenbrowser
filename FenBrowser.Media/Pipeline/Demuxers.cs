using System.Globalization;
using FenBrowser.Media.Buffers;
using FenBrowser.Media.Diagnostics;

namespace FenBrowser.Media.Pipeline;

/// <summary>Per-player services handed to every pipeline component.</summary>
public sealed record MediaPipelineContext(PlayerId Player, MediaLimits Limits, IMediaLogSink Log)
{
    public static MediaPipelineContext ForTests(IMediaLogSink? log = null) =>
        new(PlayerId.Next(), MediaLimits.Default, log ?? NullMediaLogSink.Instance);
}

/// <summary>What a demuxer learned from the container headers.</summary>
/// <param name="Duration">Resource duration, or <see cref="MediaTime.PositiveInfinity"/> when unknown or live.</param>
public sealed record DemuxerInfo(IReadOnlyList<MediaTrackInfo> Tracks, MediaTime Duration, bool IsSeekable);

/// <summary>
/// Turns a container into compressed packets. Implementations are managed code, check
/// every size against <see cref="MediaLimits"/>, and never hand a codec library raw
/// container bytes (ADR-0001).
/// </summary>
public interface IDemuxer : IAsyncDisposable
{
    /// <summary>Parses headers. Throws <see cref="MediaFormatException"/> for malformed input.</summary>
    ValueTask<DemuxerInfo> InitializeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The next packet of any track in decode order, or null at the end of the stream.
    /// The caller owns the returned packet.
    /// </summary>
    ValueTask<EncodedPacket?> ReadPacketAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Repositions so that, for every track, the next packet is a keyframe at or before
    /// <paramref name="target"/>.
    /// </summary>
    ValueTask SeekAsync(MediaTime target, CancellationToken cancellationToken);
}

/// <summary>A container format the registry can choose.</summary>
public interface IDemuxerFactory
{
    /// <summary>Unique, stable name used in logs, for example <c>webm</c>.</summary>
    string Name { get; }

    /// <summary>Lower-case MIME type essences this format answers to.</summary>
    IReadOnlyList<string> MimeTypes { get; }

    /// <summary>
    /// How sure this format is that <paramref name="header"/> belongs to it, from 0 (no)
    /// to 100 (certain). Must be bounds-checked and must not throw.
    /// </summary>
    int Probe(ReadOnlySpan<byte> header);

    IDemuxer Create(IByteSource source, MediaPipelineContext context);
}

/// <summary>A container is malformed or uses an unsupported feature.</summary>
public sealed class MediaFormatException : Exception
{
    public MediaFormatException()
    {
    }

    public MediaFormatException(string message)
        : base(message)
    {
    }

    public MediaFormatException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The set of container formats, chosen by content rather than by the declared type.
/// Formats are registered once at startup; lookups after that are safe from any thread.
/// </summary>
public sealed class DemuxerRegistry
{
    private readonly List<IDemuxerFactory> _factories = [];

    public IReadOnlyList<IDemuxerFactory> Factories => _factories;

    public void Register(IDemuxerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        if (_factories.Any(f => string.Equals(f.Name, factory.Name, StringComparison.Ordinal)))
            throw new InvalidOperationException($"A demuxer named '{factory.Name}' is already registered.");
        _factories.Add(factory);
    }

    /// <summary>True when some registered format declares <paramref name="mimeEssence"/>.</summary>
    public bool SupportsMimeType(string mimeEssence)
    {
        ArgumentNullException.ThrowIfNull(mimeEssence);
        return _factories.Any(f => DeclaresMime(f, mimeEssence));
    }

    /// <summary>
    /// Picks the format whose probe scores <paramref name="header"/> highest. The declared
    /// MIME type only breaks ties: a server's Content-Type never selects a parser the bytes
    /// do not match. Returns null when nothing recognises the header.
    /// </summary>
    public IDemuxerFactory? Select(ReadOnlySpan<byte> header, string? declaredMimeEssence, MediaPipelineContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        IDemuxerFactory? best = null;
        int bestScore = 0;
        bool bestDeclared = false;
        foreach (var factory in _factories)
        {
            int score;
            try
            {
                score = Math.Clamp(factory.Probe(header), 0, 100);
            }
            catch (Exception ex)
            {
                // A probe must not throw; one that does on hostile bytes loses, it does not end the load.
                context.Log.Emit(context.Player, MediaEventKind.SniffResult, MediaLogLevel.Error,
                    $"The {factory.Name} probe threw {ex.GetType().Name}.",
                    ("demuxer", factory.Name), ("reason", ex.GetType().Name));
                continue;
            }

            if (score == 0)
                continue;

            bool declared = declaredMimeEssence is not null && DeclaresMime(factory, declaredMimeEssence);
            if (score > bestScore || (score == bestScore && declared && !bestDeclared))
            {
                best = factory;
                bestScore = score;
                bestDeclared = declared;
            }
        }

        if (best is null)
        {
            context.Log.Emit(context.Player, MediaEventKind.SniffResult, MediaLogLevel.Warn,
                "No demuxer recognises the resource.",
                ("declared", declaredMimeEssence ?? ""), ("headerBytes", header.Length.ToString(CultureInfo.InvariantCulture)));
            return null;
        }

        context.Log.Emit(context.Player, MediaEventKind.DemuxerChosen, MediaLogLevel.Info,
            $"Using the {best.Name} demuxer.",
            ("demuxer", best.Name), ("score", bestScore.ToString(CultureInfo.InvariantCulture)),
            ("declared", declaredMimeEssence ?? ""), ("declaredMatches", bestDeclared ? "true" : "false"));
        return best;
    }

    private static bool DeclaresMime(IDemuxerFactory factory, string essence) =>
        factory.MimeTypes.Any(m => string.Equals(m, essence, StringComparison.OrdinalIgnoreCase));
}
