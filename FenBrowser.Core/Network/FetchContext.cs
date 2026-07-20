using System;

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
    public bool IsTopLevelNavigation { get; init; }
    public bool IsUserInitiated { get; init; }
    public string Method { get; init; } = "GET";
}
