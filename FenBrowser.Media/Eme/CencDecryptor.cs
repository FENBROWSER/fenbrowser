using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FenBrowser.Media.Eme;

/// <summary>
/// The four ISO/IEC 23001-7 Common Encryption schemes, as named by the 'schm' box and by
/// the EME <c>encryptionScheme</c> member.
/// </summary>
public enum CencScheme
{
    /// <summary>'cenc': AES-128 counter mode over every protected byte.</summary>
    Cenc,

    /// <summary>'cens': AES-128 counter mode over a crypt/skip block pattern.</summary>
    Cens,

    /// <summary>'cbc1': AES-128 CBC, restarting at each protected range.</summary>
    Cbc1,

    /// <summary>'cbcs': AES-128 CBC over a crypt/skip block pattern, with a constant IV.</summary>
    Cbcs,
}

/// <summary>
/// One subsample: a clear run followed by a protected run, as the 'senc' box records them.
/// </summary>
public readonly record struct CencSubsample(int ClearBytes, int ProtectedBytes);

/// <summary>
/// What is needed to decrypt one sample: which key, which initialization vector, and which
/// bytes of the sample are actually encrypted.
/// </summary>
/// <remarks>
/// An empty <see cref="Subsamples"/> list means the whole sample is protected, which is
/// how audio is usually carried.
/// </remarks>
public sealed record CencSampleInfo(
    KeyId KeyId,
    byte[] Iv,
    IReadOnlyList<CencSubsample> Subsamples,
    CencScheme Scheme = CencScheme.Cenc,
    int CryptByteBlock = 0,
    int SkipByteBlock = 0)
{
    /// <summary>AES block size, which is also the pattern's block unit.</summary>
    public const int BlockBytes = 16;

    /// <summary>True when the scheme encrypts a crypt/skip pattern rather than every byte.</summary>
    public bool HasPattern => Scheme is CencScheme.Cens or CencScheme.Cbcs && CryptByteBlock > 0;
}

/// <summary>
/// Decrypts Common Encryption samples in place (ISO/IEC 23001-7). The keys come from the
/// Clear Key CDM; nothing here ever reads a key from the media itself.
/// </summary>
/// <remarks>
/// Decryption runs where decoding runs - inside the media process - so the key travels to
/// the sandbox rather than the media bytes travelling out of it, which is the arrangement
/// ADR-0004 asks for.
///
/// The counter-mode schemes treat the protected bytes of a whole sample as one continuous
/// stream: a protected range that does not end on a block boundary carries its keystream
/// position into the next one. Getting that wrong corrupts every sample after the first
/// ragged subsample, which is why it is stated here rather than left to the reader.
/// </remarks>
public sealed class CencDecryptor : IDisposable
{
    private readonly Aes _aes = CreateAes();
    private readonly byte[] _counter = new byte[CencSampleInfo.BlockBytes];
    private readonly byte[] _keystream = new byte[CencSampleInfo.BlockBytes];
    private KeyId _loadedKey;

    private static Aes CreateAes()
    {
        var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        return aes;
    }

    /// <summary>
    /// Decrypts <paramref name="sample"/> in place with <paramref name="key"/>. Returns
    /// false, leaving the bytes untouched, when the sample's own description does not fit
    /// the bytes it came with.
    /// </summary>
    public bool TryDecrypt(Span<byte> sample, CencSampleInfo info, ReadOnlySpan<byte> key)
    {
        ArgumentNullException.ThrowIfNull(info);

        if (key.Length != ClearKeyLicense.KeyBytes)
            return false;
        if (info.Iv.Length is not (8 or 16))
            return false;
        if (info.HasPattern && (info.CryptByteBlock <= 0 || info.SkipByteBlock < 0))
            return false;

        if (!TryLayOutRanges(sample.Length, info, out var ranges))
            return false;

        LoadKey(info.KeyId, key);

        return info.Scheme switch
        {
            CencScheme.Cenc or CencScheme.Cens => DecryptCounterMode(sample, info, ranges),
            CencScheme.Cbc1 or CencScheme.Cbcs => DecryptCbcMode(sample, info, ranges),
            _ => false,
        };
    }

    /// <summary>
    /// Turns the subsample list into the protected byte ranges of the sample, checking as
    /// it goes that every range is inside the bytes we actually have. A sample whose
    /// description runs past its own end is hostile input, not a decryption failure.
    /// </summary>
    private static bool TryLayOutRanges(int sampleLength, CencSampleInfo info, out List<(int Offset, int Length)> ranges)
    {
        ranges = [];

        if (info.Subsamples.Count == 0)
        {
            if (sampleLength > 0)
                ranges.Add((0, sampleLength));
            return true;
        }

        int offset = 0;
        foreach (var subsample in info.Subsamples)
        {
            if (subsample.ClearBytes < 0 || subsample.ProtectedBytes < 0)
                return false;
            if (subsample.ClearBytes > sampleLength - offset)
                return false;
            offset += subsample.ClearBytes;
            if (subsample.ProtectedBytes > sampleLength - offset)
                return false;
            if (subsample.ProtectedBytes > 0)
                ranges.Add((offset, subsample.ProtectedBytes));
            offset += subsample.ProtectedBytes;
        }

        // Trailing bytes the subsample list does not describe stay clear, which is legal:
        // some muxers do not describe a final clear run.
        return true;
    }

    private void LoadKey(KeyId keyId, ReadOnlySpan<byte> key)
    {
        if (_loadedKey == keyId && !_loadedKey.IsEmpty)
            return;
        _aes.Key = key.ToArray();
        _loadedKey = keyId;
    }

    /// <summary>
    /// AES-CTR. The counter starts from the sample's IV and advances across every
    /// protected range of the sample without resetting, including through a range that
    /// ends part way into a block.
    /// </summary>
    private bool DecryptCounterMode(Span<byte> sample, CencSampleInfo info, List<(int Offset, int Length)> ranges)
    {
        info.Iv.CopyTo(_counter, 0);
        if (info.Iv.Length == 8)
            Array.Clear(_counter, 8, 8);

        int keystreamOffset = CencSampleInfo.BlockBytes;   // forces a fresh block on first use
        int cryptBytes = info.HasPattern ? info.CryptByteBlock * CencSampleInfo.BlockBytes : 0;
        int skipBytes = info.HasPattern ? info.SkipByteBlock * CencSampleInfo.BlockBytes : 0;

        foreach (var (offset, length) in ranges)
        {
            int position = 0;
            while (position < length)
            {
                int run = length - position;
                if (info.HasPattern)
                {
                    // 'cens' encrypts crypt_byte_block blocks, then leaves skip_byte_block
                    // blocks clear, repeating to the end of the range.
                    run = Math.Min(run, cryptBytes);
                }

                for (int i = 0; i < run; i++)
                {
                    if (keystreamOffset == CencSampleInfo.BlockBytes)
                    {
                        _aes.EncryptEcb(_counter, _keystream, PaddingMode.None);
                        IncrementCounter(_counter);
                        keystreamOffset = 0;
                    }

                    sample[offset + position + i] ^= _keystream[keystreamOffset++];
                }

                position += run;
                if (info.HasPattern)
                    position += Math.Min(skipBytes, length - position);
            }
        }

        return true;
    }

    /// <summary>
    /// AES-CBC. The chain starts from the sample's IV at the beginning of each protected
    /// range and then continues across that range's encrypted block groups, stepping over
    /// the pattern's skipped blocks; bytes past the last whole block of a range are left
    /// clear, as 23001-7 requires.
    /// </summary>
    private bool DecryptCbcMode(Span<byte> sample, CencSampleInfo info, List<(int Offset, int Length)> ranges)
    {
        if (info.Iv.Length != 16)
            return false;

        int cryptBytes = info.HasPattern ? info.CryptByteBlock * CencSampleInfo.BlockBytes : 0;
        int skipBytes = info.HasPattern ? info.SkipByteBlock * CencSampleInfo.BlockBytes : 0;

        // One scratch block each, outside the loops: a stackalloc inside a loop would grow
        // the frame once per iteration.
        Span<byte> chain = stackalloc byte[CencSampleInfo.BlockBytes];
        Span<byte> cipher = stackalloc byte[CencSampleInfo.BlockBytes];
        Span<byte> plain = stackalloc byte[CencSampleInfo.BlockBytes];

        foreach (var (offset, length) in ranges)
        {
            info.Iv.CopyTo(chain);

            int position = 0;
            while (position + CencSampleInfo.BlockBytes <= length)
            {
                int run = length - position;
                if (info.HasPattern)
                    run = Math.Min(run, cryptBytes);
                run -= run % CencSampleInfo.BlockBytes;

                for (int i = 0; i < run; i += CencSampleInfo.BlockBytes)
                {
                    var block = sample.Slice(offset + position + i, CencSampleInfo.BlockBytes);
                    block.CopyTo(cipher);

                    _aes.DecryptEcb(cipher, plain, PaddingMode.None);
                    for (int b = 0; b < CencSampleInfo.BlockBytes; b++)
                        block[b] = (byte)(plain[b] ^ chain[b]);

                    cipher.CopyTo(chain);
                }

                position += run;
                if (!info.HasPattern)
                    break;

                position += Math.Min(skipBytes, length - position);
            }
        }

        return true;
    }

    /// <summary>
    /// The CTR counter is the whole 16-byte block read as a big-endian integer, so it
    /// carries from the low 64 bits into the high ones.
    /// </summary>
    private static void IncrementCounter(Span<byte> counter)
    {
        ulong low = BinaryPrimitives.ReadUInt64BigEndian(counter[8..]);
        BinaryPrimitives.WriteUInt64BigEndian(counter[8..], low + 1);
        if (low == ulong.MaxValue)
        {
            ulong high = BinaryPrimitives.ReadUInt64BigEndian(counter[..8]);
            BinaryPrimitives.WriteUInt64BigEndian(counter[..8], high + 1);
        }
    }

    public void Dispose() => _aes.Dispose();

    /// <summary>Maps the 'schm' scheme_type four-character code to a scheme.</summary>
    public static bool TryParseScheme(ReadOnlySpan<byte> fourCc, out CencScheme scheme)
    {
        if (fourCc.SequenceEqual("cenc"u8))
        {
            scheme = CencScheme.Cenc;
            return true;
        }

        if (fourCc.SequenceEqual("cens"u8))
        {
            scheme = CencScheme.Cens;
            return true;
        }

        if (fourCc.SequenceEqual("cbc1"u8))
        {
            scheme = CencScheme.Cbc1;
            return true;
        }

        if (fourCc.SequenceEqual("cbcs"u8))
        {
            scheme = CencScheme.Cbcs;
            return true;
        }

        scheme = default;
        return false;
    }
}
