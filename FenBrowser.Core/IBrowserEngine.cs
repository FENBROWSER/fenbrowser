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
    /// Gets the current title of the page.
    /// </summary>
    string Title { get; }

    /// <summary>
    /// Gets the current URL.
    /// </summary>
    string Url { get; }
}
