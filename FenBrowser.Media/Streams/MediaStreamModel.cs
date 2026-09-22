using FenBrowser.Media.Types;

namespace FenBrowser.Media.Streams;

/// <summary>What a track carries. A stream's <c>getAudioTracks</c>/<c>getVideoTracks</c> split on this.</summary>
public enum MediaStreamTrackKind
{
    Audio,
    Video,
}

/// <summary>
/// One track of a <see cref="MediaStreamModel"/>: live until it is stopped or its source
/// goes away, after which it stays ended for good.
/// </summary>
/// <remarks>
/// The realm owns the <c>MediaStreamTrack</c> object a page sees; this owns what that
/// object reads back, so the lifetime rules are testable without a document.
/// </remarks>
public sealed class MediaStreamTrackModel
{
    internal MediaStreamTrackModel(string id, MediaStreamTrackKind kind, string? label)
    {
        Id = id;
        Kind = kind;
        Label = label ?? string.Empty;
    }

    /// <summary>A unique identifier, as the specification's "generate an identifier" produces.</summary>
    public string Id { get; }

    public MediaStreamTrackKind Kind { get; }

    /// <summary>What the source is, for a person reading a device list. Empty for a captured element.</summary>
    public string Label { get; }

    /// <summary>A page may silence a track without stopping it; the track stays live.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True while the source is not delivering - a paused or stalled element.</summary>
    public bool Muted { get; internal set; }

    /// <summary>False once the track has ended, which it never comes back from.</summary>
    public bool Live { get; private set; } = true;

    /// <summary>
    /// Ends the track. True the first time, so the caller knows to fire <c>ended</c> once.
    /// </summary>
    public bool End()
    {
        if (!Live)
            return false;
        Live = false;
        return true;
    }
}

/// <summary>
/// A <c>MediaStream</c>: a set of tracks, and whether any of them is still live.
/// </summary>
/// <remarks>
/// This is the model behind <c>HTMLMediaElement.captureStream()</c>. It knows nothing about
/// where the media comes from: the caller adds a track when the element grows one and
/// removes it when the element loses it, and reports what changed so the realm can fire
/// <c>addtrack</c> and <c>removetrack</c>.
/// </remarks>
public sealed class MediaStreamModel
{
    private readonly List<MediaStreamTrackModel> _tracks = [];
    private int _nextTrackId;

    public MediaStreamModel(string? id = null) => Id = id ?? Guid.NewGuid().ToString();

    public string Id { get; }

    /// <summary>The tracks, in the order they were added.</summary>
    public IReadOnlyList<MediaStreamTrackModel> Tracks => _tracks;

    /// <summary>
    /// §"active": true while at least one track has not ended. A stream that has never had
    /// a track is not active either.
    /// </summary>
    public bool Active
    {
        get
        {
            foreach (var track in _tracks)
            {
                if (track.Live)
                    return true;
            }

            return false;
        }
    }

    /// <summary>Adds a track for one of the element's tracks and returns it.</summary>
    public MediaStreamTrackModel AddTrack(MediaStreamTrackKind kind, string label = "")
    {
        var track = new MediaStreamTrackModel($"{Id}-{_nextTrackId++}", kind, label);
        _tracks.Add(track);
        return track;
    }

    /// <summary>
    /// Takes a track out of the stream and ends it, which is what the element does when it
    /// loses the track the capture was following. False when it was not in this stream.
    /// </summary>
    public bool RemoveTrack(MediaStreamTrackModel? track)
    {
        if (track is null || !_tracks.Remove(track))
            return false;
        track.End();
        return true;
    }

    /// <summary>
    /// Every track leaves at once: the element's resource was replaced, or it reached its
    /// end. The removed tracks come back in the order they were added, so the realm can
    /// fire one <c>removetrack</c> for each.
    /// </summary>
    public IReadOnlyList<MediaStreamTrackModel> RemoveAllTracks()
    {
        if (_tracks.Count == 0)
            return [];
        var removed = _tracks.ToArray();
        _tracks.Clear();
        foreach (var track in removed)
            track.End();
        return removed;
    }

    /// <summary>
    /// Brings the stream in line with the tracks an element now has, reporting what a page
    /// has to be told about. A track of a kind the element still has is left alone, so a
    /// page keeps the track object it was given.
    /// </summary>
    public MediaStreamTrackChange Follow(IReadOnlyList<MediaTrackInfo>? elementTracks, bool muted)
    {
        bool wantsAudio = false;
        bool wantsVideo = false;
        if (elementTracks is not null)
        {
            foreach (var track in elementTracks)
            {
                if (track.Kind == MediaTrackKind.Audio)
                    wantsAudio = true;
                else if (track.Kind == MediaTrackKind.Video)
                    wantsVideo = true;
            }
        }

        var added = new List<MediaStreamTrackModel>();
        var removed = new List<MediaStreamTrackModel>();

        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            var track = _tracks[i];
            bool wanted = track.Kind == MediaStreamTrackKind.Audio ? wantsAudio : wantsVideo;
            if (wanted)
                continue;
            _tracks.RemoveAt(i);
            track.End();
            removed.Insert(0, track);
        }

        if (wantsAudio && !Has(MediaStreamTrackKind.Audio))
            added.Add(AddTrack(MediaStreamTrackKind.Audio));
        if (wantsVideo && !Has(MediaStreamTrackKind.Video))
            added.Add(AddTrack(MediaStreamTrackKind.Video));

        foreach (var track in _tracks)
            track.Muted = muted;

        return new MediaStreamTrackChange(added, removed);
    }

    private bool Has(MediaStreamTrackKind kind)
    {
        foreach (var track in _tracks)
        {
            if (track.Kind == kind)
                return true;
        }

        return false;
    }
}

/// <summary>What changed in a stream, so the realm knows which events are due.</summary>
public readonly record struct MediaStreamTrackChange(
    IReadOnlyList<MediaStreamTrackModel> Added,
    IReadOnlyList<MediaStreamTrackModel> Removed)
{
    public bool AnythingChanged => Added.Count > 0 || Removed.Count > 0;
}
