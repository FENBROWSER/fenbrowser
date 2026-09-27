using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text;
using System.Text.Json;

namespace FenBrowser.Media.Eme;

/// <summary>
/// One 'pssh' box out of "cenc" initialization data.
/// </summary>
/// <param name="SystemId">The DRM system this box addresses.</param>
/// <param name="Version">The FullBox version: 0 carries key IDs inside <paramref name="Data"/>, 1 lists them.</param>
/// <param name="KeyIds">The box's KID list, empty for version 0.</param>
/// <param name="Data">The system-specific payload.</param>
public sealed record PsshBox(Guid SystemId, byte Version, IReadOnlyList<KeyId> KeyIds, ReadOnlyMemory<byte> Data);

/// <summary>
/// Reads the initialization data formats in the EME Initialization Data Format Registry
/// (https://www.w3.org/TR/eme-initdata-registry/) and answers the key IDs they name.
/// </summary>
/// <remarks>
/// Initialization data comes from the page or from a media file, so it is hostile input:
/// every reader here is bounds-checked, rejects trailing or truncated bytes rather than
/// guessing, and never allocates ahead of a length it has not validated. A malformed
/// input is a <c>false</c> return, which the caller turns into the
/// <c>NotSupportedError</c> or <c>TypeError</c> the spec asks for.
/// </remarks>
public static class EmeInitData
{
    /// <summary>
    /// The W3C Common PSSH box SystemID, <c>1077efec-c0b2-4d02-ace3-3c1e52e2fb4b</c>. Clear
    /// Key takes its key IDs from this box, whatever other systems the data also addresses.
    /// </summary>
    public static readonly Guid CommonSystemId = new(
        (ReadOnlySpan<byte>)[0x10, 0x77, 0xEF, 0xEC, 0xC0, 0xB2, 0x4D, 0x02, 0xAC, 0xE3, 0x3C, 0x1E, 0x52, 0xE2, 0xFB, 0x4B],
        bigEndian: true);

    /// <summary>A 'pssh' box header is size, type, version and flags, then the SystemID.</summary>
    private const int PsshHeaderBytes = 4 + 4 + 4 + 16;

    /// <summary>The largest initialization data this engine will look at, matching Chromium's cap.</summary>
    public const int MaxInitDataBytes = 64 * 1024;

    /// <summary>Maps the registry's names to <see cref="EmeInitDataType"/>. The names are case sensitive.</summary>
    public static bool TryParseInitDataType(string? name, out EmeInitDataType type)
    {
        switch (name)
        {
            case "cenc":
                type = EmeInitDataType.Cenc;
                return true;
            case "keyids":
                type = EmeInitDataType.KeyIds;
                return true;
            case "webm":
                type = EmeInitDataType.WebM;
                return true;
            default:
                type = default;
                return false;
        }
    }

    public static string ToName(EmeInitDataType type) => type switch
    {
        EmeInitDataType.Cenc => "cenc",
        EmeInitDataType.KeyIds => "keyids",
        EmeInitDataType.WebM => "webm",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>
    /// Runs the registry's sanitization step for <paramref name="type"/> and answers the key
    /// IDs the data names. Returns false for data that is not a valid instance of the format.
    /// </summary>
    public static bool TryGetKeyIds(EmeInitDataType type, ReadOnlySpan<byte> initData, out IReadOnlyList<KeyId> keyIds)
    {
        keyIds = [];
        if (initData.Length is 0 or > MaxInitDataBytes)
            return false;

        return type switch
        {
            EmeInitDataType.Cenc => TryGetCencKeyIds(initData, out keyIds),
            EmeInitDataType.KeyIds => TryGetJsonKeyIds(initData, out keyIds),
            EmeInitDataType.WebM => TryGetWebMKeyIds(initData, out keyIds),
            _ => false,
        };
    }

    /// <summary>
    /// Walks concatenated 'pssh' boxes. The whole input must be boxes with nothing left
    /// over; the registry has no room for padding or for a partial trailing box.
    /// </summary>
    public static bool TryParsePsshBoxes(ReadOnlySpan<byte> initData, out IReadOnlyList<PsshBox> boxes)
    {
        boxes = [];
        var parsed = new List<PsshBox>();
        int offset = 0;

        while (offset < initData.Length)
        {
            if (initData.Length - offset < PsshHeaderBytes)
                return false;

            var box = initData[offset..];
            uint size = BinaryPrimitives.ReadUInt32BigEndian(box);

            // Sizes 0 (to end of data) and 1 (64-bit) are legal ISOBMFF but the registry
            // asks for a plain 32-bit size here, so anything else is malformed input.
            if (size < PsshHeaderBytes || size > (uint)(initData.Length - offset))
                return false;

            if (!box[4..8].SequenceEqual("pssh"u8))
                return false;

            byte version = box[8];
            if (version > 1)
            {
                // An unknown version is skipped rather than fatal: a future box must not
                // stop the systems in the boxes beside it from being offered a license.
                offset += (int)size;
                continue;
            }

            var body = box[PsshHeaderBytes..(int)size];
            var systemId = ReadBigEndianGuid(box[12..28]);

            var keyIds = new List<KeyId>();
            if (version == 1)
            {
                if (body.Length < 4)
                    return false;
                uint count = BinaryPrimitives.ReadUInt32BigEndian(body);
                if (count > (body.Length - 4) / 16)
                    return false;
                for (int i = 0; i < count; i++)
                    keyIds.Add(new KeyId(body.Slice(4 + (i * 16), 16)));
                body = body[(4 + ((int)count * 16))..];
            }

            if (body.Length < 4)
                return false;
            uint dataSize = BinaryPrimitives.ReadUInt32BigEndian(body);
            if (dataSize != body.Length - 4)
                return false;

            parsed.Add(new PsshBox(systemId, version, keyIds, body[4..].ToArray()));
            offset += (int)size;
        }

        if (parsed.Count == 0)
            return false;

        boxes = parsed;
        return true;
    }

    /// <summary>
    /// The key IDs Clear Key takes from "cenc" data: those listed by the Common SystemID
    /// boxes. Data that carries only other systems' boxes names no keys this engine can use.
    /// </summary>
    private static bool TryGetCencKeyIds(ReadOnlySpan<byte> initData, out IReadOnlyList<KeyId> keyIds)
    {
        keyIds = [];
        if (!TryParsePsshBoxes(initData, out var boxes))
            return false;

        var found = new List<KeyId>();
        foreach (var box in boxes)
        {
            if (box.SystemId != CommonSystemId)
                continue;
            foreach (var keyId in box.KeyIds)
            {
                if (!found.Contains(keyId))
                    found.Add(keyId);
            }
        }

        if (found.Count == 0)
            return false;

        keyIds = found;
        return true;
    }

    /// <summary>The "keyids" format: <c>{"kids":["base64url", ...]}</c> in UTF-8.</summary>
    private static bool TryGetJsonKeyIds(ReadOnlySpan<byte> initData, out IReadOnlyList<KeyId> keyIds)
    {
        keyIds = [];
        if (!TryDecodeStrictUtf8(initData, out string json))
            return false;

        var found = new List<KeyId>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (!document.RootElement.TryGetProperty("kids", out var kids) || kids.ValueKind != JsonValueKind.Array)
                return false;

            foreach (var element in kids.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.String
                    || !TryDecodeBase64Url(element.GetString(), out byte[] bytes)
                    || bytes.Length is 0 or > KeyId.MaxLength)
                {
                    return false;
                }

                var keyId = new KeyId(bytes);
                if (!found.Contains(keyId))
                    found.Add(keyId);
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (found.Count == 0)
            return false;

        keyIds = found;
        return true;
    }

    /// <summary>The "webm" format: the initialization data is the one key ID.</summary>
    private static bool TryGetWebMKeyIds(ReadOnlySpan<byte> initData, out IReadOnlyList<KeyId> keyIds)
    {
        keyIds = [];
        if (initData.Length > KeyId.MaxLength)
            return false;
        keyIds = [new KeyId(initData)];
        return true;
    }

    /// <summary>
    /// Decodes base64url without padding, as RFC 7515 and the Clear Key format use it.
    /// Padded or otherwise non-canonical input is rejected rather than repaired.
    /// </summary>
    public static bool TryDecodeBase64Url(string? text, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (char c in text)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
                return false;
        }

        // A base64 quantum of one character cannot encode anything.
        if (text.Length % 4 == 1)
            return false;

        try
        {
            bytes = Base64Url.DecodeFromChars(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Decodes UTF-8 that must be well formed. A lone surrogate or an invalid sequence is
    /// a decoding failure, not a replacement character, because the Clear Key messages are
    /// compared byte for byte against what the page sent.
    /// </summary>
    internal static bool TryDecodeStrictUtf8(ReadOnlySpan<byte> bytes, out string text)
    {
        try
        {
            text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = string.Empty;
            return false;
        }
    }

    /// <summary>
    /// Reads the 16 bytes of a SystemID, which ISOBMFF stores big-endian throughout while
    /// <see cref="Guid"/>'s byte constructor reads its first three fields little-endian.
    /// </summary>
    private static Guid ReadBigEndianGuid(ReadOnlySpan<byte> bytes) => new(bytes, bigEndian: true);
}
