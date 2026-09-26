using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;

namespace FenBrowser.Media.Eme;

/// <summary>
/// The initialization data types this engine understands, from the EME Initialization
/// Data Format Registry (https://www.w3.org/TR/eme-initdata-registry/).
/// </summary>
public enum EmeInitDataType
{
    /// <summary>"cenc": one or more concatenated ISO Common Encryption 'pssh' boxes.</summary>
    Cenc,

    /// <summary>"keyids": a UTF-8 JSON object with a "kids" array of base64url key IDs.</summary>
    KeyIds,

    /// <summary>"webm": the raw bytes of a single key ID.</summary>
    WebM,
}

/// <summary>EME §5 <c>MediaKeySessionType</c>.</summary>
public enum MediaKeySessionType
{
    Temporary,
    PersistentLicense,
}

/// <summary>EME §5 <c>MediaKeyStatus</c>.</summary>
public enum MediaKeyStatus
{
    Usable,
    Expired,
    Released,
    OutputRestricted,
    OutputDownscaled,
    StatusPending,
    InternalError,
}

/// <summary>EME §5 <c>MediaKeyMessageType</c>.</summary>
public enum MediaKeyMessageType
{
    LicenseRequest,
    LicenseRenewal,
    LicenseRelease,
    IndividualizationRequest,
}

/// <summary>
/// A content decryption key identifier: an opaque byte string, compared by value.
/// </summary>
/// <remarks>
/// Key IDs arrive from containers, from initialization data and from licenses, and they
/// are matched against each other constantly, so this is a value type with structural
/// equality rather than a bare <c>byte[]</c> that would compare by reference.
/// </remarks>
public readonly struct KeyId : IEquatable<KeyId>
{
    /// <summary>The registry bounds a key ID at 16 bytes for 'cenc' and 'webm'; "keyids" allows 1-512.</summary>
    public const int MaxLength = 512;

    private readonly byte[]? _bytes;

    public KeyId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxLength)
            throw new ArgumentOutOfRangeException(nameof(bytes), bytes.Length, $"a key ID is 1 to {MaxLength} bytes");
        _bytes = bytes.ToArray();
    }

    public bool IsEmpty => _bytes is null;

    public int Length => _bytes?.Length ?? 0;

    public ReadOnlySpan<byte> Span => _bytes ?? [];

    public byte[] ToArray() => _bytes is null ? [] : (byte[])_bytes.Clone();

    /// <summary>The base64url form used by the Clear Key license and request messages.</summary>
    public string ToBase64Url() => _bytes is null ? string.Empty : Base64Url.EncodeToString(_bytes);

    public bool Equals(KeyId other) => Span.SequenceEqual(other.Span);

    public override bool Equals([NotNullWhen(true)] object? obj) => obj is KeyId other && Equals(other);

    public override int GetHashCode()
    {
        // A key ID is short and already high-entropy, so a simple FNV-1a over its bytes
        // spreads well enough for the small dictionaries it lands in.
        var hash = new HashCode();
        hash.AddBytes(Span);
        return hash.ToHashCode();
    }

    public override string ToString() => _bytes is null ? "(none)" : Convert.ToHexStringLower(_bytes);

    public static bool operator ==(KeyId left, KeyId right) => left.Equals(right);

    public static bool operator !=(KeyId left, KeyId right) => !left.Equals(right);
}

/// <summary>
/// Where the decrypting side of the pipeline gets its keys. The Clear Key CDM implements
/// it; nothing else in the pipeline knows what a key system is.
/// </summary>
public interface IMediaKeySource
{
    /// <summary>
    /// Copies out the key for <paramref name="keyId"/>, if the page has given it to us.
    /// Called from the decode thread.
    /// </summary>
    bool TryGetKey(KeyId keyId, out byte[] key);

    /// <summary>
    /// A key became available. A player stalled on a missing key listens for this: the
    /// licence that unblocks it usually arrives long after the keys were handed over, so
    /// nothing else would wake it.
    /// </summary>
    event Action? KeysChanged;
}
