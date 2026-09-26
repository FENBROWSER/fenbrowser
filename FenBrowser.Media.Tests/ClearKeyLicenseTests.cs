using System.Text;
using FenBrowser.Media.Eme;

namespace FenBrowser.Media.Tests;

/// <summary>
/// The Clear Key message formats in EME §9.1, and the initialization data formats in the
/// EME Initialization Data Format Registry. The cases mirror what the
/// <c>encrypted-media</c> WPT helpers build and what its negative tests feed to
/// <c>update()</c>.
/// </summary>
public class ClearKeyLicenseTests
{
    /// <summary>The key ID and key the WPT <c>mp4-basic</c> content is encrypted with.</summary>
    private static readonly byte[] s_wptKeyId =
        [0xad, 0x13, 0xf9, 0xea, 0x2b, 0xe6, 0x98, 0xb8, 0x75, 0xf5, 0x04, 0xa8, 0xe3, 0xcc, 0xea, 0x64];

    private static readonly byte[] s_wptKey =
        [0xbe, 0x7d, 0xf8, 0xa3, 0x66, 0x7a, 0x6a, 0x8f, 0xd5, 0x64, 0xd0, 0xed, 0x81, 0x33, 0x9a, 0x95];

    /// <summary>Builds the Common SystemID version 1 'pssh' box the WPT helper sends.</summary>
    private static byte[] CommonPssh(params byte[][] keyIds)
    {
        var box = new List<byte>();
        int size = 36 + (keyIds.Length * 16);
        box.AddRange([0x00, 0x00, (byte)(size / 256), (byte)(size % 256)]);
        box.AddRange("pssh"u8);
        box.AddRange([0x01, 0x00, 0x00, 0x00]);
        box.AddRange([0x10, 0x77, 0xEF, 0xEC, 0xC0, 0xB2, 0x4D, 0x02, 0xAC, 0xE3, 0x3C, 0x1E, 0x52, 0xE2, 0xFB, 0x4B]);
        box.AddRange([0x00, 0x00, 0x00, (byte)keyIds.Length]);
        foreach (byte[] keyId in keyIds)
            box.AddRange(keyId);
        box.AddRange([0x00, 0x00, 0x00, 0x00]);
        return [.. box];
    }

    [Fact]
    public void CencInitDataNamesTheKeyIdsInItsCommonPsshBox()
    {
        Assert.True(EmeInitData.TryGetKeyIds(EmeInitDataType.Cenc, CommonPssh(s_wptKeyId), out var keyIds));
        var keyId = Assert.Single(keyIds);
        Assert.Equal(s_wptKeyId, keyId.ToArray());
    }

    [Fact]
    public void CencInitDataCarriesEveryKeyIdInTheBox()
    {
        byte[] second = [.. Enumerable.Repeat((byte)0x11, 16)];
        Assert.True(EmeInitData.TryGetKeyIds(EmeInitDataType.Cenc, CommonPssh(s_wptKeyId, second), out var keyIds));
        Assert.Equal(2, keyIds.Count);
        Assert.Equal(second, keyIds[1].ToArray());
    }

    [Fact]
    public void CencInitDataIgnoresBoxesForOtherSystems()
    {
        // A Widevine box beside the common one: its key IDs are not ours to use, but its
        // presence must not stop the common box from being read.
        byte[] widevine = CommonPssh(s_wptKeyId);
        byte[] widevineSystemId =
            [0xED, 0xEF, 0x8B, 0xA9, 0x79, 0xD6, 0x4A, 0xCE, 0xA3, 0xC8, 0x27, 0xDC, 0xD5, 0x1D, 0x21, 0xED];
        widevineSystemId.CopyTo(widevine, 12);

        byte[] initData = [.. widevine, .. CommonPssh(s_wptKeyId)];
        Assert.True(EmeInitData.TryGetKeyIds(EmeInitDataType.Cenc, initData, out var keyIds));
        Assert.Single(keyIds);
    }

    [Fact]
    public void CencInitDataWithOnlyForeignSystemsNamesNoKeys()
    {
        byte[] box = CommonPssh(s_wptKeyId);
        box[12] = 0xED;
        Assert.False(EmeInitData.TryGetKeyIds(EmeInitDataType.Cenc, box, out _));
    }

    [Theory]
    [InlineData(0)]     // a truncated box
    [InlineData(4)]
    [InlineData(20)]
    public void TruncatedCencInitDataIsRejected(int keep)
    {
        byte[] box = CommonPssh(s_wptKeyId);
        Assert.False(EmeInitData.TryGetKeyIds(EmeInitDataType.Cenc, box.AsSpan(0, keep), out _));
    }

    [Fact]
    public void CencInitDataWithTrailingBytesIsRejected()
    {
        byte[] initData = [.. CommonPssh(s_wptKeyId), 0x00];
        Assert.False(EmeInitData.TryGetKeyIds(EmeInitDataType.Cenc, initData, out _));
    }

    [Fact]
    public void ACencBoxWhoseKeyCountRunsPastItsEndIsRejected()
    {
        byte[] box = CommonPssh(s_wptKeyId);
        box[31] = 0x40;
        Assert.False(EmeInitData.TryGetKeyIds(EmeInitDataType.Cenc, box, out _));
    }

    [Fact]
    public void ACencBoxWhoseDataSizeDisagreesWithItsLengthIsRejected()
    {
        byte[] box = CommonPssh(s_wptKeyId);
        box[^1] = 0x08;
        Assert.False(EmeInitData.TryGetKeyIds(EmeInitDataType.Cenc, box, out _));
    }

    [Fact]
    public void KeyIdsInitDataIsAJsonKidsArray()
    {
        byte[] initData = Encoding.UTF8.GetBytes("{\"kids\":[\"rRP56ivmmLh19QSo48zqZA\"]}");
        Assert.True(EmeInitData.TryGetKeyIds(EmeInitDataType.KeyIds, initData, out var keyIds));
        Assert.Equal(s_wptKeyId, Assert.Single(keyIds).ToArray());
    }

    [Theory]
    [InlineData("{}")]                                // no "kids"
    [InlineData("{\"kids\":\"rRP56ivmmLh19QSo48zqZA\"}")]  // "kids" is not an array
    [InlineData("{\"kids\":[]}")]                     // no key IDs at all
    [InlineData("{\"kids\":[1]}")]                    // not a string
    [InlineData("{\"kids\":[\"rRP5+ivmmLh19QSo48zqZA\"]}")]  // base64, not base64url
    [InlineData("{\"kids\":[\"rRP56ivmmLh19QSo48zqZA==\"]}")] // padded
    [InlineData("[]")]
    [InlineData("not json")]
    public void MalformedKeyIdsInitDataIsRejected(string json)
    {
        Assert.False(EmeInitData.TryGetKeyIds(EmeInitDataType.KeyIds, Encoding.UTF8.GetBytes(json), out _));
    }

    [Fact]
    public void WebMInitDataIsTheKeyIdItself()
    {
        Assert.True(EmeInitData.TryGetKeyIds(EmeInitDataType.WebM, s_wptKeyId, out var keyIds));
        Assert.Equal(s_wptKeyId, Assert.Single(keyIds).ToArray());
    }

    [Fact]
    public void EmptyInitDataIsRejectedForEveryType()
    {
        foreach (var type in Enum.GetValues<EmeInitDataType>())
            Assert.False(EmeInitData.TryGetKeyIds(type, [], out _));
    }

    [Fact]
    public void TheLicenseRequestNamesTheKeyIdsAndTheSessionType()
    {
        byte[] request = ClearKeyLicense.CreateLicenseRequest([new KeyId(s_wptKeyId)], MediaKeySessionType.Temporary);
        Assert.Equal("{\"kids\":[\"rRP56ivmmLh19QSo48zqZA\"],\"type\":\"temporary\"}", Encoding.UTF8.GetString(request));
    }

    [Fact]
    public void TheLicenseRequestForAPersistentSessionSaysSo()
    {
        byte[] request = ClearKeyLicense.CreateLicenseRequest([new KeyId(s_wptKeyId)], MediaKeySessionType.PersistentLicense);
        Assert.Contains("\"type\":\"persistent-license\"", Encoding.UTF8.GetString(request), StringComparison.Ordinal);
    }

    [Fact]
    public void AJwkSetLicenseYieldsItsKeys()
    {
        byte[] license = Encoding.ASCII.GetBytes(
            "{\"keys\":[{\"kty\":\"oct\",\"kid\":\"rRP56ivmmLh19QSo48zqZA\",\"k\":\"vn34o2Z6ao_VZNDtgTOalQ\"}]}");

        Assert.True(ClearKeyLicense.TryParseLicense(license, MediaKeySessionType.Temporary, out var keys, out string failure));
        Assert.Equal(string.Empty, failure);
        var entry = Assert.Single(keys);
        Assert.Equal(s_wptKeyId, entry.KeyId.ToArray());
        Assert.Equal(s_wptKey, entry.Key);
    }

    [Fact]
    public void ALicenseMayDeclareItsSessionType()
    {
        byte[] license = Encoding.ASCII.GetBytes(
            "{\"type\":\"temporary\",\"keys\":[{\"kty\":\"oct\",\"kid\":\"rRP56ivmmLh19QSo48zqZA\",\"k\":\"vn34o2Z6ao_VZNDtgTOalQ\"}]}");
        Assert.True(ClearKeyLicense.TryParseLicense(license, MediaKeySessionType.Temporary, out _, out _));
        Assert.False(ClearKeyLicense.TryParseLicense(license, MediaKeySessionType.PersistentLicense, out _, out _));
    }

    [Theory]
    // The shape of the bytes the WPT invalid-license test sends: not JSON at all.
    [InlineData("\u0000\u00113D")]
    [InlineData("{}")]
    [InlineData("{\"keys\":[]}")]
    [InlineData("{\"keys\":{}}")]
    [InlineData("{\"keys\":[{\"kty\":\"RSA\",\"kid\":\"rRP56ivmmLh19QSo48zqZA\",\"k\":\"vn34o2Z6ao_VZNDtgTOalQ\"}]}")]
    [InlineData("{\"keys\":[{\"kty\":\"oct\",\"k\":\"vn34o2Z6ao_VZNDtgTOalQ\"}]}")]
    [InlineData("{\"keys\":[{\"kty\":\"oct\",\"kid\":\"rRP56ivmmLh19QSo48zqZA\"}]}")]
    // A key that is not 16 bytes is not an AES-128 key.
    [InlineData("{\"keys\":[{\"kty\":\"oct\",\"kid\":\"rRP56ivmmLh19QSo48zqZA\",\"k\":\"dHdvIGJ5dGVz\"}]}")]
    public void AMalformedLicenseIsRejected(string license)
    {
        Assert.False(ClearKeyLicense.TryParseLicense(
            Encoding.ASCII.GetBytes(license), MediaKeySessionType.Temporary, out _, out string failure));
        Assert.NotEqual(string.Empty, failure);
    }

    [Fact]
    public void ALicenseWithNonAsciiBytesIsRejected()
    {
        // The WPT non-ASCII test encodes a lone surrogate into the "kid".
        byte[] license = Encoding.UTF8.GetBytes(
            "{\"keys\":[{\"kty\":\"oct\",\"k\":\"MDEyMzQ1Njc4OTAxMjM0NQ\",\"kid\":\"MDEyMzQ1Njc4O�TAxMjM0NQ\"}]}");
        Assert.False(ClearKeyLicense.TryParseLicense(license, MediaKeySessionType.Temporary, out _, out _));
    }

    [Fact]
    public void ALicenseLargerThanTheCeilingIsRejected()
    {
        Assert.False(ClearKeyLicense.TryParseLicense(
            new byte[ClearKeyLicense.MaxLicenseBytes + 1], MediaKeySessionType.Temporary, out _, out _));
    }

    [Fact]
    public void AReleaseAcknowledgementNamesItsKeyIds()
    {
        Assert.True(ClearKeyLicense.TryParseLicenseReleaseAcknowledgement(
            Encoding.ASCII.GetBytes("{\"kids\":[\"rRP56ivmmLh19QSo48zqZA\"]}"), out var keyIds));
        Assert.Equal(s_wptKeyId, Assert.Single(keyIds).ToArray());
    }

    [Fact]
    public void KeyIdsCompareByValue()
    {
        Assert.Equal(new KeyId(s_wptKeyId), new KeyId([.. s_wptKeyId]));
        Assert.NotEqual(new KeyId(s_wptKeyId), new KeyId(s_wptKey));

        var set = new HashSet<KeyId> { new(s_wptKeyId) };
        Assert.Contains(new KeyId([.. s_wptKeyId]), set);
    }
}
