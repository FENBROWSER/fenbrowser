using System.Collections.Concurrent;
using FenBrowser.Media.WebAudio;

namespace FenBrowser.Media.Streams;

/// <summary>One MediaStreamTrack as a media element playing its stream sees it.</summary>
public sealed record LiveTrack(
    string Id,
    MediaTrackKind Kind,
    AudioTrackPipe? AudioPipe,
    bool Live,
    bool Enabled,
    VideoTrackSource? VideoSource = null);

/// <summary>
/// The tracks of a MediaStream assigned as a media element's srcObject, kept current by the
/// realm as the stream gains and loses tracks and tracks end or are disabled. Read from the
/// audio thread, written from the realm's.
/// </summary>
public sealed class LiveStreamSource
{
    private volatile IReadOnlyList<LiveTrack> _tracks = [];

    /// <summary>Raised on the realm's thread after the track list changed.</summary>
    public event Action? Changed;

    public IReadOnlyList<LiveTrack> Tracks => _tracks;

    /// <summary>mediacapture-main "active": at least one track has not ended.</summary>
    public bool Active => _tracks.Any(t => t.Live);

    public bool HasAudio => _tracks.Any(t => t.Live && t.Kind == MediaTrackKind.Audio);

    public bool HasVideo => _tracks.Any(t => t.Live && t.Kind == MediaTrackKind.Video);

    public void Update(IReadOnlyList<LiveTrack> tracks)
    {
        _tracks = tracks ?? [];
        Changed?.Invoke();
    }
}

/// <summary>
/// Streams by the internal URL the srcObject setter hands the element for them. A stream
/// stays registered while some element may load it; the realm removes it when the stream
/// is no longer any element's provider object.
/// </summary>
public static class LiveStreamRegistry
{
    /// <summary>The scheme of the internal URL a MediaStream provider object loads from.</summary>
    public const string Scheme = "fen-mediastream:";

    private static readonly ConcurrentDictionary<string, LiveStreamSource> Sources = new(StringComparer.Ordinal);

    public static LiveStreamSource GetOrAdd(string url) => Sources.GetOrAdd(url, _ => new LiveStreamSource());

    public static LiveStreamSource? Find(string url) => Sources.TryGetValue(url, out var source) ? source : null;

    public static void Remove(string url) => Sources.TryRemove(url, out _);
}
