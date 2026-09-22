using System.Globalization;

namespace FenBrowser.Media.Diagnostics;

/// <summary>
/// Identity of one media player, carried on every log event and IPC message.
/// </summary>
public readonly record struct PlayerId(long Value)
{
    private static long s_last;

    public static readonly PlayerId None = new(0);

    public static PlayerId Next() => new(Interlocked.Increment(ref s_last));

    public override string ToString() => "player-" + Value.ToString(CultureInfo.InvariantCulture);
}

public enum MediaLogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>
/// The typed events a player emits. Hot paths never log: they bump counters that the
/// once-per-second <see cref="PlaybackSummary"/> reports (docs/MEDIA_ENGINE_DESIGN.md §6).
/// </summary>
public enum MediaEventKind
{
    PlayerCreated,
    PlayerDestroyed,
    SourceSelected,
    SniffResult,
    DemuxerChosen,
    TrackAdded,
    DecoderAttempt,
    DecoderChosen,
    ReadyStateChanged,
    NetworkStateChanged,
    Buffering,
    Stall,
    SeekStart,
    SeekEnd,
    FrameDropBurst,
    AudioUnderrun,
    ClockDiscontinuity,
    LimitExceeded,
    MseAppend,
    Error,
    ProcessCrash,
    PlaybackSummary,

    /// <summary>A Clear Key session added or released keys.</summary>
    EmeKeysChanged,

    /// <summary>A packet could not be decrypted because its key is not available yet.</summary>
    EmeWaitingForKey,

    /// <summary>setSinkId moved playback to another audio output endpoint.</summary>
    AudioSinkChanged,
}

/// <summary>
/// One structured media log event. <see cref="Fields"/> holds machine-readable
/// key/value details; values must never contain credentials, media bytes or key material.
/// </summary>
public readonly record struct MediaLogEvent(
    PlayerId Player,
    MediaEventKind Kind,
    MediaLogLevel Level,
    string Message,
    IReadOnlyList<KeyValuePair<string, string>>? Fields = null);

/// <summary>
/// Where the media engine sends its events. The host adapts this to FenLogger with
/// <c>LogCategory.Media</c> (ADR-0006); tests use a recording sink.
/// </summary>
public interface IMediaLogSink
{
    bool IsEnabled(MediaLogLevel level);

    void Log(in MediaLogEvent logEvent);
}

public sealed class NullMediaLogSink : IMediaLogSink
{
    public static readonly NullMediaLogSink Instance = new();

    private NullMediaLogSink()
    {
    }

    public bool IsEnabled(MediaLogLevel level) => false;

    public void Log(in MediaLogEvent logEvent)
    {
    }
}

/// <summary>Keeps every event in memory. For tests and <c>fenplay</c>.</summary>
public sealed class RecordingMediaLogSink : IMediaLogSink
{
    private readonly Lock _gate = new();
    private readonly List<MediaLogEvent> _events = [];

    public IReadOnlyList<MediaLogEvent> Events
    {
        get
        {
            lock (_gate)
                return [.. _events];
        }
    }

    public bool IsEnabled(MediaLogLevel level) => true;

    public void Log(in MediaLogEvent logEvent)
    {
        lock (_gate)
            _events.Add(logEvent);
    }
}

public static class MediaLogSinkExtensions
{
    public static void Emit(
        this IMediaLogSink sink,
        PlayerId player,
        MediaEventKind kind,
        MediaLogLevel level,
        string message,
        params ReadOnlySpan<(string Key, string Value)> fields)
    {
        ArgumentNullException.ThrowIfNull(sink);
        if (!sink.IsEnabled(level))
            return;

        KeyValuePair<string, string>[]? pairs = null;
        if (fields.Length > 0)
        {
            pairs = new KeyValuePair<string, string>[fields.Length];
            for (int i = 0; i < fields.Length; i++)
                pairs[i] = new KeyValuePair<string, string>(fields[i].Key, fields[i].Value);
        }

        sink.Log(new MediaLogEvent(player, kind, level, message, pairs));
    }
}
