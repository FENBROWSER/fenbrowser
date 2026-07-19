using System;
using System.Collections.Generic;

namespace FenBrowser.Core.Network;

public enum BinaryFetchFailureReason
{
    None,
    InvalidRequest,
    UnsupportedScheme,
    MixedContentBlocked,
    CspBlocked,
    TransportFailure,
    Timeout,
    RedirectLimitExceeded,
    HttpError,
    BodySizeLimitExceeded,
    CorbBlocked,
    BodyReadFailed
}

public sealed record BinaryFetchResult
{
    public byte[] Body { get; init; }
    public int StatusCode { get; init; }
    public Uri FinalUri { get; init; }
    public IReadOnlyList<Uri> RedirectChain { get; init; } = Array.Empty<Uri>();
    public string ContentType { get; init; }
    public IReadOnlyDictionary<string, string> ResponseHeaders { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public long ByteCount => Body?.LongLength ?? 0;
    public BinaryFetchFailureReason FailureReason { get; init; }
    public string FailureDetail { get; init; }
    public bool CspAllowed { get; init; } = true;
    public bool CorbAllowed { get; init; } = true;
    public bool BodySizeAllowed { get; init; } = true;
    public string DecodeFormat { get; init; }
    public string DecodeFailureReason { get; init; }
    public bool Succeeded => FailureReason == BinaryFetchFailureReason.None && Body != null;
}
