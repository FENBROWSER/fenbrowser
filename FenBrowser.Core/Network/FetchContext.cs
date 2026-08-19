using System;
using System.Net.Http;
using FenBrowser.Core.Storage;
using FenBrowser.Core.Security;

namespace FenBrowser.Core.Network;

/// <summary>
/// Immutable browsing context carried with a network fetch.
/// Request, initiator, frame, and top-level document identities remain distinct.
/// </summary>
public sealed record FetchContext
{
    public required Uri RequestUri { get; init; }
    public Uri InitiatorUri { get; init; }
    public Uri FrameDocumentUri { get; init; }
    public Uri TopLevelDocumentUri { get; init; }
    public string Destination { get; init; } = "empty";
    public string Mode { get; init; } = "cors";
    public string CredentialsMode { get; init; } = "same-origin";
    public ReferrerPolicyDirective? ReferrerPolicy { get; init; }
    public CspPolicy ContentSecurityPolicy { get; init; }
    public bool IsTopLevelNavigation { get; init; }
    public bool IsUserInitiated { get; init; }
    public string Method { get; init; } = "GET";

    /// <summary>
    /// Immutable network/storage partition identity derived from the browsing
    /// context, never from the Referer header. Redirects do not change it.
    /// </summary>
    public StoragePartitionKey NetworkPartitionKey
    {
        get
        {
            var topLevel = TopLevelDocumentUri ?? FrameDocumentUri ?? InitiatorUri ?? RequestUri;
            var frame = FrameDocumentUri ?? InitiatorUri ?? RequestUri;
            return StoragePartitionKeyFactory.Compute(topLevel?.AbsoluteUri, frame?.AbsoluteUri);
        }
    }
}

/// <summary>
/// Internal transport bridge for preserving browser fetch state across the existing
/// INetworkClient(HttpRequestMessage) boundary. HttpRequestMessage.Options is process-
/// local metadata and is never serialized onto the wire.
/// </summary>
internal static class FetchContextRequestOptions
{
    private static readonly HttpRequestOptionsKey<FetchContext> ContextKey =
        new("FenBrowser.FetchContext");

    public static void Set(HttpRequestMessage request, FetchContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        request.Options.Set(ContextKey, context);
    }

    public static FetchContext Get(HttpRequestMessage request)
    {
        if (request == null)
        {
            return null;
        }

        return request.Options.TryGetValue(ContextKey, out var context)
            ? context
            : null;
    }
}
