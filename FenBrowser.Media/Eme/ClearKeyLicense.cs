using System.Text;
using System.Text.Json;

namespace FenBrowser.Media.Eme;

/// <summary>One key out of a Clear Key license: a key ID and the AES-128 key for it.</summary>
public sealed record ClearKeyEntry(KeyId KeyId, byte[] Key);

/// <summary>
/// The Clear Key message formats in EME §9.1
/// (https://w3c.github.io/encrypted-media/#clear-key): the license request, the JSON Web
/// Key Set license, and the release acknowledgement.
/// </summary>
/// <remarks>
/// Every reader here is strict. A license arrives from the page, and a key that was
/// misread is a key that decrypts nothing, so the failure the spec asks for
/// (<c>TypeError</c> from <c>update()</c>) is better than a repaired guess. The format is
/// also restricted to ASCII: a license carrying anything else is rejected outright.
/// </remarks>
public static class ClearKeyLicense
{
    /// <summary>Clear Key is AES-128, so every key is 16 bytes.</summary>
    public const int KeyBytes = 16;

    /// <summary>The largest license this engine will read, well past any real key set.</summary>
    public const int MaxLicenseBytes = 64 * 1024;

    /// <summary>
    /// EME §9.1.3: the license request naming the key IDs a session wants, and the session
    /// type it wants them for.
    /// </summary>
    public static byte[] CreateLicenseRequest(IReadOnlyList<KeyId> keyIds, MediaKeySessionType sessionType)
    {
        ArgumentNullException.ThrowIfNull(keyIds);

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("kids");
            foreach (var keyId in keyIds)
                writer.WriteStringValue(keyId.ToBase64Url());
            writer.WriteEndArray();
            writer.WriteString("type", ToName(sessionType));
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// EME §9.1.5: the release message a persistent session sends when its keys are
    /// dropped. It names the keys being released and has the same shape as a request.
    /// </summary>
    public static byte[] CreateLicenseRelease(IReadOnlyList<KeyId> keyIds, MediaKeySessionType sessionType) =>
        CreateLicenseRequest(keyIds, sessionType);

    /// <summary>
    /// EME §9.1.4: reads a JSON Web Key Set license. Returns false, with a reason for the
    /// log, for anything that is not a well-formed license for <paramref name="sessionType"/>.
    /// </summary>
    public static bool TryParseLicense(
        ReadOnlySpan<byte> license,
        MediaKeySessionType sessionType,
        out IReadOnlyList<ClearKeyEntry> keys,
        out string failure)
    {
        keys = [];
        failure = string.Empty;

        if (license.Length is 0 or > MaxLicenseBytes)
        {
            failure = "the license is empty or larger than the ceiling";
            return false;
        }

        if (!TryDecodeAscii(license, out string json))
        {
            failure = "the license is not ASCII";
            return false;
        }

        var parsed = new List<ClearKeyEntry>();
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                failure = "the license is not a JSON object";
                return false;
            }

            // "type" is optional, but when it is there it has to be the session's own type.
            if (root.TryGetProperty("type", out var type))
            {
                if (type.ValueKind != JsonValueKind.String || type.GetString() != ToName(sessionType))
                {
                    failure = "the license type does not match the session type";
                    return false;
                }
            }

            if (!root.TryGetProperty("keys", out var keyArray) || keyArray.ValueKind != JsonValueKind.Array)
            {
                failure = "the license has no \"keys\" array";
                return false;
            }

            foreach (var element in keyArray.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    failure = "a JWK is not an object";
                    return false;
                }

                if (!element.TryGetProperty("kty", out var kty) || kty.ValueKind != JsonValueKind.String || kty.GetString() != "oct")
                {
                    failure = "a JWK is not a symmetric (\"oct\") key";
                    return false;
                }

                if (!element.TryGetProperty("kid", out var kid) || kid.ValueKind != JsonValueKind.String
                    || !EmeInitData.TryDecodeBase64Url(kid.GetString(), out byte[] keyIdBytes)
                    || keyIdBytes.Length is 0 or > KeyId.MaxLength)
                {
                    failure = "a JWK has no readable \"kid\"";
                    return false;
                }

                if (!element.TryGetProperty("k", out var k) || k.ValueKind != JsonValueKind.String
                    || !EmeInitData.TryDecodeBase64Url(k.GetString(), out byte[] keyBytes)
                    || keyBytes.Length != KeyBytes)
                {
                    failure = $"a JWK has no readable {KeyBytes}-byte \"k\"";
                    return false;
                }

                parsed.Add(new ClearKeyEntry(new KeyId(keyIdBytes), keyBytes));
            }
        }
        catch (JsonException ex)
        {
            failure = $"the license is not valid JSON: {ex.Message}";
            return false;
        }

        if (parsed.Count == 0)
        {
            failure = "the license carries no keys";
            return false;
        }

        keys = parsed;
        return true;
    }

    /// <summary>
    /// EME §9.1.5: reads a release acknowledgement, <c>{"kids":[...]}</c>, and answers the
    /// key IDs it acknowledges.
    /// </summary>
    public static bool TryParseLicenseReleaseAcknowledgement(ReadOnlySpan<byte> message, out IReadOnlyList<KeyId> keyIds)
    {
        keyIds = [];
        if (message.Length is 0 or > MaxLicenseBytes || !TryDecodeAscii(message, out string json))
            return false;

        var found = new List<KeyId>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("kids", out var kids)
                || kids.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var element in kids.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String
                    || !EmeInitData.TryDecodeBase64Url(element.GetString(), out byte[] bytes)
                    || bytes.Length is 0 or > KeyId.MaxLength)
                {
                    return false;
                }
                found.Add(new KeyId(bytes));
            }
        }
        catch (JsonException)
        {
            return false;
        }

        keyIds = found;
        return found.Count > 0;
    }

    public static string ToName(MediaKeySessionType type) => type switch
    {
        MediaKeySessionType.Temporary => "temporary",
        MediaKeySessionType.PersistentLicense => "persistent-license",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static bool TryParseSessionType(string? name, out MediaKeySessionType type)
    {
        switch (name)
        {
            case "temporary":
                type = MediaKeySessionType.Temporary;
                return true;
            case "persistent-license":
                type = MediaKeySessionType.PersistentLicense;
                return true;
            default:
                type = default;
                return false;
        }
    }

    /// <summary>
    /// The Clear Key formats are ASCII JSON. Decoding rejects anything with the high bit
    /// set, which also rules out the lone surrogates a page can produce by encoding a
    /// malformed JavaScript string.
    /// </summary>
    private static bool TryDecodeAscii(ReadOnlySpan<byte> bytes, out string text)
    {
        foreach (byte b in bytes)
        {
            if (b > 0x7F)
            {
                text = string.Empty;
                return false;
            }
        }

        text = Encoding.ASCII.GetString(bytes);
        return true;
    }
}
