using FenBrowser.Media.Eme;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Containers.Mp4;

/// <summary>
/// ISO/IEC 23001-7 Common Encryption in an ISO base media file: the protection scheme on
/// a sample entry ('sinf'), the track's key and scheme defaults ('tenc'), the per-sample
/// initialization vectors and subsample maps ('senc', or 'saiz' and 'saio' when the
/// auxiliary information lives out in the media data), and the 'pssh' boxes that become
/// the initialization data an <c>encrypted</c> event carries.
/// </summary>
/// <remarks>
/// Nothing here decrypts. The demuxer only describes what is encrypted and how, so the
/// keys stay with the CDM and the description travels with the packet.
/// </remarks>
public sealed partial class Mp4Demuxer
{
    /// <summary>The 'tenc' TrackEncryptionBox: what every sample of a track defaults to.</summary>
    private sealed record TrackEncryption(
        bool IsProtected,
        int PerSampleIvSize,
        KeyId DefaultKeyId,
        CencScheme Scheme,
        int CryptByteBlock,
        int SkipByteBlock,
        byte[]? ConstantIv);

    /// <summary>
    /// Auxiliary sample information a fragment described but did not carry: 'saio' says
    /// where it is and 'saiz' says how long each sample's entry is. It is read when a
    /// sample in the range is about to be handed out, because it lives in the media data
    /// rather than in the moof we already have.
    /// </summary>
    private sealed record PendingAuxInfo(int FirstSample, int Count, long Offset, int[] Sizes)
    {
        public bool Resolved { get; set; }

        /// <summary>The key and scheme each sample of the run needs.</summary>
        public TrackEncryption[] PerSample { get; init; } = [];

        public long TotalBytes
        {
            get
            {
                long total = 0;
                foreach (int size in Sizes)
                    total += size;
                return total;
            }
        }
    }

    /// <summary>The largest auxiliary information block read in one go, well past any real fragment.</summary>
    private const int MaxAuxInfoBytes = 8 * 1024 * 1024;

    /// <summary>A ceiling on the sample group descriptions one box may declare.</summary>
    private const int MaxSampleGroupDescriptions = 4096;

    /// <summary>Initialization data gathered from the file's 'pssh' boxes, in file order.</summary>
    private readonly List<byte[]> _psshBoxes = [];

    /// <summary>
    /// §8.12 ProtectionSchemeInfoBox. Answers the format the sample entry would have had
    /// without encryption, and records the track's scheme and key defaults.
    /// </summary>
    private bool TryReadProtection(Track track, ReadOnlyMemory<byte> children, long dataPosition, out uint originalFormat)
    {
        originalFormat = 0;
        foreach (var (box, body) in Box.Children(children, dataPosition))
        {
            if (box.Type != BoxType.Sinf)
                continue;

            uint format = 0;
            var scheme = CencScheme.Cenc;
            TrackEncryption? encryption = null;

            foreach (var (child, childBody) in Box.Children(body, box.DataStart))
            {
                if (child.Type == BoxType.Frma && childBody.Length >= 4)
                {
                    format = Box.U32(childBody.Span, 0);
                }
                else if (child.Type == BoxType.Schm && childBody.Length >= 8)
                {
                    // The scheme_type follows the version and flags.
                    if (!CencDecryptor.TryParseScheme(childBody.Span.Slice(4, 4), out scheme))
                        return false;
                }
                else if (child.Type == BoxType.Schi)
                {
                    foreach (var (tenc, tencBody) in Box.Children(childBody, child.DataStart))
                    {
                        if (tenc.Type == BoxType.Tenc)
                            encryption = ReadTrackEncryption(tencBody.Span);
                    }
                }
            }

            if (format == 0 || encryption is null)
                return false;

            originalFormat = format;
            track.Encryption = encryption with { Scheme = scheme };
            return true;
        }

        return false;
    }

    /// <summary>ISO/IEC 23001-7 §8.2 TrackEncryptionBox.</summary>
    private static TrackEncryption? ReadTrackEncryption(ReadOnlySpan<byte> body)
    {
        // version and flags, a reserved byte, then either a reserved byte (version 0) or
        // the default crypt/skip pattern (version 1), isProtected, the IV size and the KID.
        if (body.Length < 24)
            return null;

        var (version, _) = Box.FullBox(body);
        int patternByte = Box.U8(body, 5);
        int cryptByteBlock = version == 0 ? 0 : patternByte >> 4;
        int skipByteBlock = version == 0 ? 0 : patternByte & 0x0F;
        bool isProtected = Box.U8(body, 6) == 1;
        int ivSize = Box.U8(body, 7);
        var keyId = new KeyId(body.Slice(8, 16));

        byte[]? constantIv = null;
        if (isProtected && ivSize == 0)
        {
            if (body.Length < 25)
                return null;
            int constantIvSize = Box.U8(body, 24);
            if (constantIvSize is not (8 or 16) || body.Length < 25 + constantIvSize)
                return null;
            constantIv = body.Slice(25, constantIvSize).ToArray();
        }
        else if (ivSize is not (0 or 8 or 16))
        {
            return null;
        }

        return new TrackEncryption(isProtected, ivSize, keyId, CencScheme.Cenc, cryptByteBlock, skipByteBlock, constantIv);
    }

    /// <summary>
    /// ISO/IEC 23001-7 §7.2 SampleEncryptionBox, and the 'saiz'/'saio' pair that replaces
    /// it when the auxiliary information sits in the media data. Applies to the samples a
    /// fragment just added, which are the last <paramref name="count"/> of the track.
    /// </summary>
    private void ApplySampleEncryption(
        Track track,
        int firstSample,
        int count,
        ReadOnlyMemory<byte> senc,
        ReadOnlyMemory<byte> saiz,
        ReadOnlyMemory<byte> saio,
        ReadOnlyMemory<byte> sbgp,
        ReadOnlyMemory<byte> sgpd,
        long baseDataOffset)
    {
        if (track.Encryption is not { IsProtected: true } || count == 0)
            return;

        // A 'seig' sample group overrides the track's key and scheme for the samples it
        // covers, which is how content encrypted with more than one key says which key
        // each of its samples needs.
        var perSample = ResolveSampleEncryption(track, count, sbgp, sgpd);

        if (!senc.IsEmpty && TryReadAuxInfo(track, senc.Span[8..], firstSample, count, SencSizes(perSample, senc.Span, count), perSample))
            return;

        if (saiz.IsEmpty || saio.IsEmpty)
            return;

        int[]? sizes = ReadAuxSizes(saiz.Span, count);
        long? offset = ReadAuxOffset(saio.Span, baseDataOffset);
        if (sizes is null || offset is null)
            return;

        track.PendingAux.Add(new PendingAuxInfo(firstSample, count, offset.Value, sizes) { PerSample = perSample });
    }

    /// <summary>
    /// The encryption description for each sample of a fragment: the track's 'tenc'
    /// defaults, overridden where a 'seig' sample group says otherwise.
    /// </summary>
    private static TrackEncryption[] ResolveSampleEncryption(Track track, int count, ReadOnlyMemory<byte> sbgp, ReadOnlyMemory<byte> sgpd)
    {
        var perSample = new TrackEncryption[count];
        Array.Fill(perSample, track.Encryption!);
        if (sbgp.IsEmpty || sgpd.IsEmpty)
            return perSample;

        var descriptions = ReadSeigDescriptions(sgpd.Span, track.Encryption!.Scheme);
        if (descriptions.Count == 0)
            return perSample;

        var span = sbgp.Span;
        var (version, _) = Box.FullBox(span);
        int at = 4;
        if (Box.U32(span, at) != BoxType.Seig)
            return perSample;
        at += 4;
        if (version == 1)
            at += 4;

        long entries = Box.U32(span, at);
        at += 4;
        if (entries * 8 > span.Length - at)
            return perSample;

        int sample = 0;
        for (long e = 0; e < entries && sample < count; e++)
        {
            long run = Box.U32(span, at);
            uint index = Box.U32(span, at + 4);
            at += 8;

            // An index of 65537 or above names a description written in this fragment;
            // 0 means the samples take the track's defaults.
            uint description = index >= 0x10001 ? index - 0x10000 : index;
            TrackEncryption? entry = description >= 1 && description <= descriptions.Count
                ? descriptions[(int)description - 1]
                : null;

            for (long i = 0; i < run && sample < count; i++, sample++)
            {
                if (entry is not null)
                    perSample[sample] = entry;
            }
        }

        return perSample;
    }

    /// <summary>
    /// ISO/IEC 23001-7 §6 CencSampleEncryptionInformationGroupEntry, out of a
    /// SampleGroupDescriptionBox whose grouping type is 'seig'.
    /// </summary>
    private static List<TrackEncryption> ReadSeigDescriptions(ReadOnlySpan<byte> body, CencScheme scheme)
    {
        var descriptions = new List<TrackEncryption>();
        var (version, _) = Box.FullBox(body);
        int at = 4;
        if (Box.U32(body, at) != BoxType.Seig)
            return descriptions;
        at += 4;

        int defaultLength = 0;
        if (version >= 1)
        {
            defaultLength = (int)Math.Min(int.MaxValue, Box.U32(body, at));
            at += 4;
        }

        if (version >= 2)
            at += 4;

        long count = Box.U32(body, at);
        at += 4;
        if (count > MaxSampleGroupDescriptions)
            return descriptions;

        for (long i = 0; i < count; i++)
        {
            int length = defaultLength;
            if (version >= 1 && defaultLength == 0)
            {
                if (body.Length - at < 4)
                    return descriptions;
                length = (int)Math.Min(int.MaxValue, Box.U32(body, at));
                at += 4;
            }

            // The entry body is a 'tenc' box without its version and flags, so it is read
            // by the same code with four bytes of header put back in front of it.
            if (length < 20 || body.Length - at < length)
                return descriptions;

            Span<byte> asTenc = new byte[4 + length];
            asTenc[0] = 1;   // version 1, so the crypt/skip pattern byte is read
            body.Slice(at, length).CopyTo(asTenc[4..]);
            at += length;

            if (ReadTrackEncryption(asTenc) is { } entry)
                descriptions.Add(entry with { Scheme = scheme });
        }

        return descriptions;
    }

    /// <summary>
    /// The per-sample entry sizes inside a 'senc' box. The box does not state them, so
    /// they are measured by walking it, which also validates its layout.
    /// </summary>
    private static int[]? SencSizes(TrackEncryption[] perSample, ReadOnlySpan<byte> body, int count)
    {
        var (_, flags) = Box.FullBox(body);
        bool hasSubsamples = (flags & 0x2) != 0;

        if ((long)Box.U32(body, 4) != count || perSample.Length != count)
            return null;

        var sizes = new int[count];
        int at = 8;
        for (int i = 0; i < count; i++)
        {
            int start = at;
            int ivSize = perSample[i].PerSampleIvSize;
            if (body.Length - at < ivSize)
                return null;
            at += ivSize;

            if (hasSubsamples)
            {
                if (body.Length - at < 2)
                    return null;
                int subsamples = Box.U16(body, at);
                at += 2;
                if ((long)subsamples * 6 > body.Length - at)
                    return null;
                at += subsamples * 6;
            }

            sizes[i] = at - start;
        }

        return sizes;
    }

    /// <summary>
    /// Reads one auxiliary information block - the same layout whether it came from a
    /// 'senc' box or from the media data - into the samples it describes.
    /// </summary>
    private static bool TryReadAuxInfo(Track track, ReadOnlySpan<byte> data, int firstSample, int count, int[]? sizes, TrackEncryption[] perSample)
    {
        if (sizes is null || sizes.Length != count || perSample.Length != count)
            return false;
        if (firstSample < 0 || firstSample + count > track.Samples.Count)
            return false;

        int at = 0;
        for (int i = 0; i < count; i++)
        {
            var encryption = perSample[i];
            if (sizes[i] < 0 || sizes[i] > data.Length - at)
                return false;

            var entry = data.Slice(at, sizes[i]);
            at += sizes[i];

            // A zero-length entry marks a sample that is not encrypted, which is how a
            // clear lead-in inside an encrypted track is carried.
            if (entry.Length == 0)
                continue;

            byte[] iv;
            int read = 0;
            if (encryption.PerSampleIvSize > 0)
            {
                if (entry.Length < encryption.PerSampleIvSize)
                    return false;
                iv = entry[..encryption.PerSampleIvSize].ToArray();
                read = encryption.PerSampleIvSize;
            }
            else if (encryption.ConstantIv is { } constant)
            {
                iv = constant;
            }
            else
            {
                return false;
            }

            var subsamples = new List<CencSubsample>();
            if (entry.Length - read >= 2)
            {
                int subsampleCount = Box.U16(entry, read);
                read += 2;
                if ((long)subsampleCount * 6 > entry.Length - read)
                    return false;
                for (int s = 0; s < subsampleCount; s++)
                {
                    int clear = Box.U16(entry, read);
                    uint encrypted = Box.U32(entry, read + 2);
                    read += 6;
                    if (encrypted > int.MaxValue)
                        return false;
                    subsamples.Add(new CencSubsample(clear, (int)encrypted));
                }
            }

            var sample = track.Samples[firstSample + i];
            sample.Encryption = new CencSampleInfo(
                encryption.DefaultKeyId,
                iv,
                subsamples,
                encryption.Scheme,
                encryption.CryptByteBlock,
                encryption.SkipByteBlock);
            track.Samples[firstSample + i] = sample;
        }

        return true;
    }

    /// <summary>§8.7.8 SampleAuxiliaryInformationSizesBox.</summary>
    private static int[]? ReadAuxSizes(ReadOnlySpan<byte> body, int count)
    {
        var (_, flags) = Box.FullBox(body);
        int at = 4;
        if ((flags & 1) != 0)
            at += 8;
        if (body.Length < at + 5)
            return null;

        int defaultSize = Box.U8(body, at);
        at += 1;
        long sampleCount = Box.U32(body, at);
        at += 4;
        if (sampleCount != count)
            return null;

        var sizes = new int[count];
        if (defaultSize != 0)
        {
            Array.Fill(sizes, defaultSize);
            return sizes;
        }

        if (body.Length - at < count)
            return null;
        for (int i = 0; i < count; i++)
            sizes[i] = Box.U8(body, at + i);
        return sizes;
    }

    /// <summary>
    /// §8.7.9 SampleAuxiliaryInformationOffsetsBox. Inside a track fragment the offset is
    /// relative to the fragment's base data offset, which defaults to the start of the moof.
    /// </summary>
    private static long? ReadAuxOffset(ReadOnlySpan<byte> body, long baseDataOffset)
    {
        var (version, flags) = Box.FullBox(body);
        int at = 4;
        if ((flags & 1) != 0)
            at += 8;
        if (body.Length < at + 4)
            return null;

        long entries = Box.U32(body, at);
        at += 4;
        if (entries < 1)
            return null;

        // Only the first entry is used: one run of auxiliary information per fragment is
        // what every muxer writes, and a second would describe samples we already covered.
        if (version == 0)
        {
            if (body.Length < at + 4)
                return null;
            return baseDataOffset + Box.U32(body, at);
        }

        if (body.Length < at + 8)
            return null;
        return baseDataOffset + Track.Clamp((Int128)Box.U64(body, at));
    }

    /// <summary>
    /// Reads the auxiliary information a fragment left in the media data, if the sample
    /// about to be handed out needs it. Returns false when it could not be read, which
    /// leaves the sample undecryptable rather than wrongly decrypted.
    /// </summary>
    private async ValueTask ResolvePendingAuxAsync(Track track, int sampleIndex, CancellationToken cancellationToken)
    {
        foreach (var pending in track.PendingAux)
        {
            if (pending.Resolved || sampleIndex < pending.FirstSample || sampleIndex >= pending.FirstSample + pending.Count)
                continue;

            pending.Resolved = true;
            long total = pending.TotalBytes;
            if (total is <= 0 or > MaxAuxInfoBytes)
                return;

            byte[] buffer = new byte[total];
            int got = await _source.ReadAtLeastAsync(pending.Offset, buffer, cancellationToken).ConfigureAwait(false);
            if (got == total)
                TryReadAuxInfo(track, buffer, pending.FirstSample, pending.Count, pending.Sizes, pending.PerSample);
            return;
        }
    }

    /// <summary>
    /// Collects a 'pssh' box as it is seen. The whole box is kept, because "cenc"
    /// initialization data is the concatenation of the boxes exactly as they appeared.
    /// </summary>
    private void CollectPssh(BoxHeader header, ReadOnlySpan<byte> body)
    {
        // The registry asks for plain 32-bit box sizes, so a 64-bit-sized box is not
        // initialization data we can hand on, and a run of them is not one either.
        if (_psshBoxes.Count >= 32 || header.HeaderLength != 8 || header.Size is < 8 or > EmeInitData.MaxInitDataBytes)
            return;
        if (body.Length != header.Size - 8)
            return;

        var box = new byte[header.Size];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box, (uint)header.Size);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(4), BoxType.Pssh);
        body.CopyTo(box.AsSpan(8));
        _psshBoxes.Add(box);
    }

    /// <summary>
    /// The initialization data this file carried, as the media element will announce it.
    /// A file with no 'pssh' box announces nothing, whether or not its tracks are
    /// protected: a key ID alone is not initialization data of any registered type.
    /// </summary>
    private IReadOnlyList<(string InitDataType, byte[] InitData)> CencInitDataEntries()
    {
        byte[] cenc = CencInitData();
        return cenc.Length == 0 ? [] : [("cenc", cenc)];
    }

    /// <summary>
    /// The "cenc" initialization data for this file: every 'pssh' box it carried, in
    /// order. Empty when the file named no protection system.
    /// </summary>
    private byte[] CencInitData()
    {
        if (_psshBoxes.Count == 0)
            return [];

        int total = 0;
        foreach (byte[] box in _psshBoxes)
            total += box.Length;

        var initData = new byte[total];
        int at = 0;
        foreach (byte[] box in _psshBoxes)
        {
            box.CopyTo(initData.AsSpan(at));
            at += box.Length;
        }

        return initData;
    }
}
