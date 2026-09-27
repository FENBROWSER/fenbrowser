using System;
using System.Threading;

namespace FenBrowser.Host;

/// <summary>
/// Decides whether a wake-up of the window loop presents a frame.
/// </summary>
/// <remarks>
/// The window loop sleeps in the platform event wait and only presents when
/// something asked for it. Presenting unconditionally on every vsync kept a
/// whole core busy on an idle page: the NVIDIA OpenGL driver spins inside the
/// GL flush while it waits for the vertical blank, so an idle browser burned
/// the same CPU as one scrolling at 60 fps.
///
/// A request both records that a present is owed and wakes the loop; the loop
/// consumes it in <see cref="TryBeginPresent"/>. <see cref="PendingWork"/> is a
/// second, pull-based source: a change that reached the widget tree or the
/// compositor without calling <see cref="Request"/> is still presented on the
/// next wake-up instead of being lost.
/// </remarks>
internal sealed class PresentScheduler
{
    private readonly Action _wake;
    private int _requested;

    public PresentScheduler(Action wake, bool initiallyRequested = true)
    {
        _wake = wake ?? throw new ArgumentNullException(nameof(wake));
        _requested = initiallyRequested ? 1 : 0;
    }

    /// <summary>Reports visual state that changed without an explicit request.</summary>
    public Func<bool> PendingWork { get; set; }

    public bool IsRequested => Volatile.Read(ref _requested) != 0;

    /// <summary>
    /// Asks for one present. Safe from any thread. Only the request that finds
    /// none outstanding wakes the loop; later ones are covered by that wake.
    /// </summary>
    public void Request()
    {
        if (Interlocked.Exchange(ref _requested, 1) == 0)
        {
            _wake();
        }
    }

    /// <summary>
    /// Called by the loop on each wake-up. Returns true when this wake-up must
    /// present, and clears the outstanding request so a request made while the
    /// frame is being drawn schedules the next one.
    /// </summary>
    public bool TryBeginPresent()
    {
        if (Interlocked.Exchange(ref _requested, 0) != 0)
        {
            return true;
        }

        return PendingWork?.Invoke() ?? false;
    }
}
