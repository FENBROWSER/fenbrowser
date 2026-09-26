using FenBrowser.Media.Diagnostics;

namespace FenBrowser.Media.PictureInPicture;

/// <summary>
/// Why a <c>requestPictureInPicture()</c> or <c>exitPictureInPicture()</c> call could not
/// do what it was asked; the realm turns each one into the DOMException the specification
/// names for it.
/// </summary>
public enum PictureInPictureRefusal
{
    /// <summary>Nothing refused it.</summary>
    None,

    /// <summary>The user agent does not offer Picture-in-Picture here - <c>NotSupportedError</c>.</summary>
    NotSupported,

    /// <summary>
    /// The element has no metadata yet, has no video track, or its author turned
    /// Picture-in-Picture off with <c>disablePictureInPicture</c> - <c>InvalidStateError</c>.
    /// Also what <c>exitPictureInPicture()</c> gets when nothing is in Picture-in-Picture.
    /// </summary>
    InvalidState,

    /// <summary>The page has no transient activation to spend - <c>NotAllowedError</c>.</summary>
    NotAllowed,

    /// <summary>
    /// The document is not allowed to use the "picture-in-picture" policy-controlled
    /// feature - <c>SecurityError</c>.
    /// </summary>
    Security,
}

/// <summary>
/// What a request asks its caller to do once the state has changed. The controller never
/// fires an event itself: it says what happened, and the realm queues the tasks, so the
/// algorithms here stay testable without a document.
/// </summary>
/// <param name="Refusal">Why the request failed, or <see cref="PictureInPictureRefusal.None"/>.</param>
/// <param name="Window">The window the promise resolves with, when it resolves.</param>
/// <param name="Entered">The element that just entered Picture-in-Picture, if any.</param>
/// <param name="Left">The element that just left it, if any.</param>
/// <param name="LeftWindow">The window that element had while it was in Picture-in-Picture.</param>
public readonly record struct PictureInPictureChange(
    PictureInPictureRefusal Refusal,
    PictureInPictureWindowState? Window = null,
    object? Entered = null,
    object? Left = null,
    PictureInPictureWindowState? LeftWindow = null)
{
    public bool Succeeded => Refusal == PictureInPictureRefusal.None;
}

/// <summary>
/// The size of one Picture-in-Picture window. One of these belongs to each element that
/// has ever been in Picture-in-Picture, and the page keeps hold of it: the same object is
/// handed back for a repeated request, and it reads 0 by 0 once its element is no longer
/// the one being shown, which is how a page can tell a stale window from a live one.
/// </summary>
public sealed class PictureInPictureWindowState
{
    private int _width;
    private int _height;

    public int Width => Volatile.Read(ref _width);

    public int Height => Volatile.Read(ref _height);

    /// <summary>True while this window is the one showing its element.</summary>
    public bool IsOpen { get; private set; }

    internal void Open(int width, int height)
    {
        IsOpen = true;
        Volatile.Write(ref _width, width);
        Volatile.Write(ref _height, height);
    }

    /// <summary>Reports a new size; false when nothing changed, so no resize event is due.</summary>
    internal bool Resize(int width, int height)
    {
        if (!IsOpen || (Width == width && Height == height))
            return false;
        Volatile.Write(ref _width, width);
        Volatile.Write(ref _height, height);
        return true;
    }

    internal void Close()
    {
        IsOpen = false;
        Volatile.Write(ref _width, 0);
        Volatile.Write(ref _height, 0);
    }
}

/// <summary>
/// What the controller needs to know about one video element to answer a request. The realm
/// fills this in from the element it was called on.
/// </summary>
/// <param name="HasMetadata">readyState is past HAVE_NOTHING.</param>
/// <param name="HasVideoTrack">The resource has a video track - an audio file in a video element does not.</param>
/// <param name="DisablePictureInPicture">The <c>disablepictureinpicture</c> content attribute is present.</param>
/// <param name="VideoWidth">The intrinsic width, which gives the window its aspect ratio.</param>
/// <param name="VideoHeight">The intrinsic height.</param>
public readonly record struct PictureInPictureCandidate(
    bool HasMetadata,
    bool HasVideoTrack,
    bool DisablePictureInPicture,
    int VideoWidth,
    int VideoHeight);

/// <summary>
/// One document's Picture-in-Picture state (https://w3c.github.io/picture-in-picture/): at
/// most one element is in Picture-in-Picture at a time, and the algorithms that put it
/// there and take it away again.
/// </summary>
/// <remarks>
/// The controller holds elements as opaque objects and never touches the DOM or the event
/// loop, in the same way <see cref="Element.HtmlMediaElementController"/> does not: the
/// realm owns the elements, the events and the promises, and this owns the state machine.
/// </remarks>
public sealed class PictureInPictureController
{
    /// <summary>The longest edge a Picture-in-Picture window is given, before aspect ratio.</summary>
    private const int MaximumEdge = 640;

    /// <summary>The shortest, so a very wide or very tall video is still visible.</summary>
    private const int MinimumEdge = 160;

    private readonly IMediaLogSink _log;
    private readonly Dictionary<object, PictureInPictureWindowState> _windows = [];

    public PictureInPictureController(IMediaLogSink? log = null) => _log = log ?? NullMediaLogSink.Instance;

    /// <summary>
    /// Whether the document allows Picture-in-Picture at all. False makes every request
    /// fail with NotSupportedError and is what <c>document.pictureInPictureEnabled</c>
    /// reads back; a permissions policy or a user preference turns it off.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Whether the document's permissions policy lets it use "picture-in-picture". False
    /// makes a request fail with SecurityError (§4.1 step 2) and turns
    /// <c>document.pictureInPictureEnabled</c> off.
    /// </summary>
    public bool AllowedByPolicy { get; set; } = true;

    /// <summary>The element being shown in Picture-in-Picture, or null.</summary>
    public object? Element { get; private set; }

    /// <summary>The window of the element in Picture-in-Picture, or null.</summary>
    public PictureInPictureWindowState? Window { get; private set; }

    /// <summary>
    /// §4.1 <c>requestPictureInPicture()</c> steps 1-5: everything that has to be decided
    /// the moment the page calls, before any task runs. <paramref name="activationNeeded"/>
    /// says whether an activation would be spent, so the caller can consume one.
    /// </summary>
    public PictureInPictureRefusal CheckRequest(
        object element,
        in PictureInPictureCandidate candidate,
        bool hasTransientActivation,
        out bool activationNeeded)
    {
        ArgumentNullException.ThrowIfNull(element);
        activationNeeded = false;

        if (!Enabled)
            return PictureInPictureRefusal.NotSupported;
        if (!AllowedByPolicy)
            return PictureInPictureRefusal.Security;

        // Steps 2-4: an element with nothing to show, or one whose author asked for no
        // Picture-in-Picture, cannot be put in it.
        if (!candidate.HasMetadata || !candidate.HasVideoTrack || candidate.DisablePictureInPicture)
            return PictureInPictureRefusal.InvalidState;

        // Step 5: a request needs an activation to spend, unless this document is already
        // showing something - swapping between videos is not a new intrusion. The element
        // being shown is the one at the moment of the call, so two requests made inside one
        // gesture need two activations, and there is only ever one.
        if (ReferenceEquals(Element, element) || Element is not null)
            return PictureInPictureRefusal.None;

        activationNeeded = true;
        return hasTransientActivation ? PictureInPictureRefusal.None : PictureInPictureRefusal.NotAllowed;
    }

    /// <summary>
    /// The rest of §4.1, which the specification runs in a task: the element becomes the
    /// one being shown. Anything that was being shown must have been taken away first with
    /// <see cref="Exit"/>, so a page watching <c>pictureInPictureElement</c> from the
    /// displaced element's handler sees it unset.
    /// </summary>
    public PictureInPictureChange Enter(object element, in PictureInPictureCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(element);

        if (!Enabled)
            return new PictureInPictureChange(PictureInPictureRefusal.NotSupported);
        if (!AllowedByPolicy)
            return new PictureInPictureChange(PictureInPictureRefusal.Security);
        if (!candidate.HasMetadata || !candidate.HasVideoTrack || candidate.DisablePictureInPicture)
            return new PictureInPictureChange(PictureInPictureRefusal.InvalidState);

        // The element is already the one being shown: nothing changes, and the page gets
        // the same window object back with whatever it hung on it.
        if (ReferenceEquals(Element, element))
            return new PictureInPictureChange(PictureInPictureRefusal.None, Window);

        object? left = Element;
        var leftWindow = Window;
        leftWindow?.Close();

        var window = WindowFor(element);
        var (width, height) = SizeFor(candidate);
        window.Open(width, height);
        Element = element;
        Window = window;

        _log.Emit(PlayerId.None, MediaEventKind.PictureInPicture, MediaLogLevel.Info,
            "An element entered Picture-in-Picture.", [("size", $"{width}x{height}")]);

        return new PictureInPictureChange(PictureInPictureRefusal.None, window, element, left, leftWindow);
    }

    /// <summary>
    /// Both halves at once: the check and, if it passes, the entry. This is the whole of
    /// <c>requestPictureInPicture()</c> for a caller that has no task queue of its own.
    /// </summary>
    public PictureInPictureChange Request(object element, in PictureInPictureCandidate candidate, bool hasTransientActivation)
    {
        var refusal = CheckRequest(element, candidate, hasTransientActivation, out _);
        return refusal == PictureInPictureRefusal.None
            ? Enter(element, candidate)
            : new PictureInPictureChange(refusal);
    }

    /// <summary>
    /// §4.2 <c>exitPictureInPicture()</c>: InvalidStateError when nothing is being shown,
    /// otherwise the element leaves and its window reads 0 by 0 from then on.
    /// </summary>
    public PictureInPictureChange Exit()
    {
        if (Element is not { } element)
            return new PictureInPictureChange(PictureInPictureRefusal.InvalidState);

        var window = Window;
        window?.Close();
        Element = null;
        Window = null;
        _log.Emit(PlayerId.None, MediaEventKind.PictureInPicture, MediaLogLevel.Info, "An element left Picture-in-Picture.");
        return new PictureInPictureChange(PictureInPictureRefusal.None, null, null, element, window);
    }

    /// <summary>
    /// The element leaves Picture-in-Picture for a reason that is not a page asking it to:
    /// its document went away, or <c>disablePictureInPicture</c> became true. Nothing
    /// happens when that element was not the one being shown.
    /// </summary>
    public PictureInPictureChange ExitIfShowing(object element) =>
        ReferenceEquals(Element, element) ? Exit() : new PictureInPictureChange(PictureInPictureRefusal.InvalidState);

    /// <summary>
    /// The window changed size, either because the person resized it or because the video's
    /// intrinsic size changed. True when a <c>resize</c> event is due on the window.
    /// </summary>
    public bool Resize(int width, int height) => Window?.Resize(width, height) ?? false;

    /// <summary>The size this element's window takes, from the video's aspect ratio.</summary>
    public bool TryGetPreferredSize(in PictureInPictureCandidate candidate, out int width, out int height)
    {
        (width, height) = SizeFor(candidate);
        return width > 0 && height > 0;
    }

    /// <summary>
    /// The window object this element has had, or a new one. A page holds on to the window
    /// it was given, and a second request for the same element has to hand back that same
    /// object with the listeners it has on it.
    /// </summary>
    private PictureInPictureWindowState WindowFor(object element)
    {
        if (_windows.TryGetValue(element, out var existing))
            return existing;
        var window = new PictureInPictureWindowState();
        _windows[element] = window;
        return window;
    }

    /// <summary>
    /// A window that keeps the video's aspect ratio, bounded so that neither edge is absurd.
    /// A video with no intrinsic size yet gets a 16:9 window of the default size.
    /// </summary>
    private static (int Width, int Height) SizeFor(in PictureInPictureCandidate candidate)
    {
        int videoWidth = candidate.VideoWidth;
        int videoHeight = candidate.VideoHeight;
        if (videoWidth <= 0 || videoHeight <= 0)
            return (MaximumEdge, MaximumEdge * 9 / 16);

        double scale = (double)MaximumEdge / Math.Max(videoWidth, videoHeight);
        int width = (int)Math.Round(videoWidth * scale);
        int height = (int)Math.Round(videoHeight * scale);

        // Keep the aspect ratio when the short edge is raised to the minimum: the window
        // tests compare the window's ratio with the video's to a hundredth.
        if (width < MinimumEdge || height < MinimumEdge)
        {
            double up = (double)MinimumEdge / Math.Min(width, height);
            width = (int)Math.Round(width * up);
            height = (int)Math.Round(height * up);
        }

        return (Math.Max(width, 1), Math.Max(height, 1));
    }
}
