using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FenBrowser.Core;

/// <summary>
/// Abstraction for network operations.
/// </summary>
public interface INetworkService
{
    /// <summary>
    /// Fetches content from a URL.
    /// </summary>
    /// <param name="url">The URL to fetch.</param>
    /// <returns>A stream containing the content.</returns>
    Task<Stream> GetStreamAsync(string url);

    /// <summary>
    /// Fetches content as string.
    /// </summary>
    Task<string> GetStringAsync(string url);

    /// <summary>
    /// Fetches content from an already-validated absolute URI.
    /// </summary>
    Task<Stream> GetStreamAsync(Uri uri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches textual content from an already-validated absolute URI.
    /// </summary>
    Task<string> GetStringAsync(Uri uri, CancellationToken cancellationToken = default);
}
