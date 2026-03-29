using System;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core;

/// <summary>
/// Represents the core browser engine responsible for loading and rendering pages.
/// </summary>
public interface IBrowserEngine
{
    /// <summary>
    /// Loads a URL.
    /// </summary>
    /// <param name="url">The URL to load.</param>
    Task LoadAsync(string url);

    /// <summary>
    /// Loads an absolute URI with cancellation support.
    /// </summary>
    Task LoadAsync(Uri uri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current title of the page.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Gets the current URL.
    /// </summary>
    string Url { get; }

    /// <summary>
    /// Gets the current navigation state.
    /// </summary>
    BrowserEngineLoadState LoadState { get; }

    /// <summary>
    /// Gets the last load error message, if any.
    /// </summary>
    string LastError { get; }

    /// <summary>
    /// Returns whether a navigation is currently in progress.
    /// </summary>
    bool IsLoading { get; }
}

/// <summary>
/// Stable browser engine navigation states.
/// </summary>
public enum BrowserEngineLoadState
{
    Idle,
    Loading,
    Complete,
    Failed,
    Cancelled
}
