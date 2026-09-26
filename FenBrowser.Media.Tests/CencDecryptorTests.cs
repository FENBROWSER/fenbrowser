using System.Security.Cryptography;
using FenBrowser.Media.Eme;

namespace FenBrowser.Media.Tests;

/// <summary>
/// ISO/IEC 23001-7 Common Encryption subsample decryption. The counter-mode cases are
/// checked against a keystream built straight from AES-ECB over incrementing counter
/// blocks, which is the definition of CTR and shares no code with the decryptor; the CBC
/// cases are checked by encrypting with the BCL's own CBC.
/// </summary>
public class CencDecryptorTests
{
    private static readonly byte[] s_key =
        [0xbe, 0x7d, 0xf8, 0xa3, 0x66, 0x7a, 0x6a, 0x8f, 0xd5, 0x64, 0xd0, 0xed, 0x81, 0x33, 0x9a, 0x95];

    private static readonly KeyId s_keyId = new([.. Enumerable.Range(0, 16).Select(i => (byte)i)]);

    private static byte[] Iv8() => [0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08];

    private static byte[] Iv16() => [.. Iv8(), .. new byte[8]];

    private static byte[] Pattern(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 % 251))];

    /// <summary>The CTR keystream for a sample, built the long way round.</summary>
    private static byte[] Keystream(byte[] iv, int length)
    {
        using var aes = Aes.Create();
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.Key = s_key;

        byte[] counter = new byte[16];
        iv.CopyTo(counter, 0);

        var stream = new List<byte>();
        while (stream.Count < length)
        {
            byte[] block = new byte[16];
            aes.EncryptEcb(counter, block, PaddingMode.None);
            stream.AddRange(block);

            for (int i = 15; i >= 0; i--)
            {
                if (++counter[i] != 0)
                    break;
            }
        }

        return [.. stream.Take(length)];
    }

    private static byte[] Xor(byte[] a, byte[] b) => [.. a.Zip(b, (x, y) => (byte)(x ^ y))];

    [Fact]
    public void CencDecryptsAWholeSampleAsOneCounterStream()
    {
        byte[] plain = Pattern(64);
        byte[] cipher = Xor(plain, Keystream(Iv16(), plain.Length));

        using var decryptor = new CencDecryptor();
        var info = new CencSampleInfo(s_keyId, Iv8(), []);
        Assert.True(decryptor.TryDecrypt(cipher, info, s_key));

        Assert.Equal(plain, cipher);
    }

    [Fact]
    public void AnEightByteIvIsTheHighHalfOfTheCounter()
    {
        byte[] plain = Pattern(32);
        byte[] viaEight = Xor(plain, Keystream(Iv16(), plain.Length));
        byte[] viaSixteen = (byte[])viaEight.Clone();

        using var decryptor = new CencDecryptor();
        Assert.True(decryptor.TryDecrypt(viaEight, new CencSampleInfo(s_keyId, Iv8(), []), s_key));
        Assert.True(decryptor.TryDecrypt(viaSixteen, new CencSampleInfo(s_keyId, Iv16(), []), s_key));

        Assert.Equal(plain, viaEight);
        Assert.Equal(plain, viaSixteen);
    }

    [Fact]
    public void CencLeavesTheClearBytesOfASubsampleAlone()
    {
        // 10 clear, 20 protected, then 5 clear, 16 protected.
        byte[] clearSample = Pattern(51);
        byte[] protectedBytes = [.. clearSample[10..30], .. clearSample[35..51]];
        byte[] encryptedProtected = Xor(protectedBytes, Keystream(Iv16(), protectedBytes.Length));

        byte[] sample = (byte[])clearSample.Clone();
        encryptedProtected.AsSpan(0, 20).CopyTo(sample.AsSpan(10));
        encryptedProtected.AsSpan(20, 16).CopyTo(sample.AsSpan(35));

        using var decryptor = new CencDecryptor();
        var info = new CencSampleInfo(s_keyId, Iv8(), [new CencSubsample(10, 20), new CencSubsample(5, 16)]);
        Assert.True(decryptor.TryDecrypt(sample, info, s_key));

        Assert.Equal(clearSample, sample);
    }

    [Fact]
    public void CencCarriesTheKeystreamAcrossARaggedSubsampleBoundary()
    {
        // The first protected run is 20 bytes: it ends four bytes into a block, and the
        // next run must continue from there rather than from a fresh block.
        byte[] clearSample = Pattern(64);
        byte[] protectedBytes = [.. clearSample[4..24], .. clearSample[28..60]];
        byte[] encrypted = Xor(protectedBytes, Keystream(Iv16(), protectedBytes.Length));

        byte[] sample = (byte[])clearSample.Clone();
        encrypted.AsSpan(0, 20).CopyTo(sample.AsSpan(4));
        encrypted.AsSpan(20, 32).CopyTo(sample.AsSpan(28));

        using var decryptor = new CencDecryptor();
        var info = new CencSampleInfo(s_keyId, Iv8(), [new CencSubsample(4, 20), new CencSubsample(4, 32)]);
        Assert.True(decryptor.TryDecrypt(sample, info, s_key));

        Assert.Equal(clearSample, sample);
    }

    [Fact]
    public void CensEncryptsOnlyThePatternsCryptBlocks()
    {
        // 1:9 over a 160-byte run: the first 16 bytes are encrypted, the next 144 are not.
        byte[] clearSample = Pattern(160);
        byte[] sample = (byte[])clearSample.Clone();
        byte[] keystream = Keystream(Iv16(), 16);
        for (int i = 0; i < 16; i++)
            sample[i] ^= keystream[i];

        using var decryptor = new CencDecryptor();
        var info = new CencSampleInfo(s_keyId, Iv8(), [], CencScheme.Cens, CryptByteBlock: 1, SkipByteBlock: 9);
        Assert.True(decryptor.TryDecrypt(sample, info, s_key));

        Assert.Equal(clearSample, sample);
    }

    [Fact]
    public void Cbc1DecryptsEveryWholeBlockAndLeavesTheTail()
    {
        byte[] clearSample = Pattern(70);
        byte[] sample = (byte[])clearSample.Clone();

        using (var aes = Aes.Create())
        {
            aes.Key = s_key;
            // 64 bytes is four whole blocks; the last six bytes stay clear.
            byte[] encrypted = aes.EncryptCbc(clearSample[..64], Iv16(), PaddingMode.None);
            encrypted.CopyTo(sample, 0);
        }

        using var decryptor = new CencDecryptor();
        var info = new CencSampleInfo(s_keyId, Iv16(), [], CencScheme.Cbc1);
        Assert.True(decryptor.TryDecrypt(sample, info, s_key));

        Assert.Equal(clearSample, sample);
    }

    [Fact]
    public void CbcsDecryptsOneBlockInTen()
    {
        byte[] clearSample = Pattern(320);
        byte[] sample = (byte[])clearSample.Clone();

        using (var aes = Aes.Create())
        {
            aes.Key = s_key;
            // Two pattern cycles: bytes 0-15 and 160-175 are encrypted and 16-159 and
            // 176-319 are not. The chain starts at the constant IV and carries from the
            // first encrypted block into the second, stepping over the skipped ones.
            byte[] first = aes.EncryptCbc(clearSample[..16], Iv16(), PaddingMode.None);
            byte[] second = aes.EncryptCbc(clearSample[160..176], first, PaddingMode.None);
            first.CopyTo(sample, 0);
            second.CopyTo(sample, 160);
        }

        using var decryptor = new CencDecryptor();
        var info = new CencSampleInfo(s_keyId, Iv16(), [], CencScheme.Cbcs, CryptByteBlock: 1, SkipByteBlock: 9);
        Assert.True(decryptor.TryDecrypt(sample, info, s_key));

        Assert.Equal(clearSample, sample);
    }

    [Fact]
    public void TheCounterCarriesOutOfItsLowHalf()
    {
        byte[] iv = [0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        byte[] plain = Pattern(48);
        byte[] sample = Xor(plain, Keystream(iv, plain.Length));

        using var decryptor = new CencDecryptor();
        Assert.True(decryptor.TryDecrypt(sample, new CencSampleInfo(s_keyId, iv, []), s_key));

        Assert.Equal(plain, sample);
    }

    [Theory]
    [InlineData(4)]     // not an AES key
    [InlineData(32)]
    public void AKeyThatIsNotAes128IsRefused(int keyLength)
    {
        using var decryptor = new CencDecryptor();
        Assert.False(decryptor.TryDecrypt(new byte[16], new CencSampleInfo(s_keyId, Iv8(), []), new byte[keyLength]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(12)]
    public void AnIvThatIsNotEightOrSixteenBytesIsRefused(int ivLength)
    {
        using var decryptor = new CencDecryptor();
        Assert.False(decryptor.TryDecrypt(new byte[16], new CencSampleInfo(s_keyId, new byte[ivLength], []), s_key));
    }

    [Fact]
    public void ASubsampleDescriptionThatRunsPastTheSampleIsRefused()
    {
        using var decryptor = new CencDecryptor();
        byte[] sample = new byte[32];

        Assert.False(decryptor.TryDecrypt(sample, new CencSampleInfo(s_keyId, Iv8(), [new CencSubsample(0, 33)]), s_key));
        Assert.False(decryptor.TryDecrypt(sample, new CencSampleInfo(s_keyId, Iv8(), [new CencSubsample(40, 0)]), s_key));
        Assert.False(decryptor.TryDecrypt(sample, new CencSampleInfo(s_keyId, Iv8(), [new CencSubsample(16, 8), new CencSubsample(16, 0)]), s_key));
        Assert.False(decryptor.TryDecrypt(sample, new CencSampleInfo(s_keyId, Iv8(), [new CencSubsample(-1, 8)]), s_key));

        // The bytes were left exactly as they came.
        Assert.All(sample, b => Assert.Equal(0, b));
    }

    [Fact]
    public void TrailingBytesNoSubsampleDescribesStayClear()
    {
        byte[] clearSample = Pattern(48);
        byte[] sample = (byte[])clearSample.Clone();
        byte[] keystream = Keystream(Iv16(), 16);
        for (int i = 0; i < 16; i++)
            sample[i] ^= keystream[i];

        using var decryptor = new CencDecryptor();
        Assert.True(decryptor.TryDecrypt(sample, new CencSampleInfo(s_keyId, Iv8(), [new CencSubsample(0, 16)]), s_key));

        Assert.Equal(clearSample, sample);
    }

    [Theory]
    [InlineData("cenc", CencScheme.Cenc)]
    [InlineData("cens", CencScheme.Cens)]
    [InlineData("cbc1", CencScheme.Cbc1)]
    [InlineData("cbcs", CencScheme.Cbcs)]
    public void TheSchemeFourCcIsRecognised(string fourCc, CencScheme expected)
    {
        Assert.True(CencDecryptor.TryParseScheme(System.Text.Encoding.ASCII.GetBytes(fourCc), out var scheme));
        Assert.Equal(expected, scheme);
    }

    [Fact]
    public void AnUnknownSchemeFourCcIsRejected() =>
        Assert.False(CencDecryptor.TryParseScheme("dumb"u8, out _));
}
