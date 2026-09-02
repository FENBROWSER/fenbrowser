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

    /// <summary>
    /// Nonce of the authorizing HTML element (e.g. &lt;script nonce&gt;) that triggered
    /// this fetch. CSP Level 3 §6.2.2.9: under 'strict-dynamic' host-source fallbacks
    /// are ignored, so a nonce-authorized element must carry its nonce into the
    /// resource layer or a correctly allowed load gets rejected twice.
    /// </summary>
    public string CspNonce { get; init; }

    /// <summary>
    /// How this request came to be made. CSP Level 3 §6.6.3.4: 'strict-dynamic'
    /// discards host-source expressions and instead propagates trust from the
    /// script that asked for the resource, so a request made by an already
    /// trusted script -- new Worker(url), a script-injected script element --
    /// is allowed where the same URL from the parser would not be.
    /// </summary>
    public FenBrowser.Core.Security.CspScriptProvenance ScriptProvenance { get; init; }
        = FenBrowser.Core.Security.CspScriptProvenance.Unknown;
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
