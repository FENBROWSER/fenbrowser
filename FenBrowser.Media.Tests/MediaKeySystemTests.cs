using System.Text;
using FenBrowser.Media.Codecs.Ffmpeg;
using FenBrowser.Media.Codecs.MediaFoundation;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Eme;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Types;

namespace FenBrowser.Media.Tests;

/// <summary>
/// EME §3.1.1 "Get Supported Configuration" and the §6 session state machine, against the
/// cases in <c>encrypted-media/scripts/requestmediakeysystemaccess.js</c> and
/// <c>keystatuses.js</c>.
/// </summary>
public class MediaKeySystemTests
{
    private const string VideoType = "video/mp4;codecs=\"avc1.4d401e\"";
    private const string AudioType = "audio/mp4;codecs=\"mp4a.40.2\"";

    private static MediaKeySystemSupport Support()
    {
        var demuxers = new DemuxerRegistry();
        var decoders = new DecoderRegistry();
        MediaFormats.RegisterBuiltIn(demuxers, decoders);
        FfmpegDecoders.TryRegister(decoders, NullMediaLogSink.Instance);
        MediaFoundationDecoders.TryRegister(decoders, NullMediaLogSink.Instance);
        return new MediaKeySystemSupport(new MediaTypeSupport(demuxers, decoders));
    }

    private static MediaKeySystemConfiguration Candidate(
        string[]? initDataTypes = null,
        string[]? audio = null,
        string[]? video = null,
        string label = "") =>
        new()
        {
            Label = label,
            InitDataTypes = initDataTypes ?? [],
            AudioCapabilities = [.. (audio ?? []).Select(t => new MediaKeySystemMediaCapability(t))],
            VideoCapabilities = [.. (video ?? []).Select(t => new MediaKeySystemMediaCapability(t))],
        };

    [Theory]
    [InlineData("org.w3.clearkey", true)]
    [InlineData("", false)]
    [InlineData("com.example.unsupported", false)]
    [InlineData("org.w3.clearkey.", false)]
    [InlineData("ORG.W3.CLEARKEY", false)]
    [InlineData("org.w3.ClearKey", false)]
    [InlineData("org.w3", false)]
    [InlineData("org.w3.", false)]
    [InlineData("org.w3.clearkey.foo", false)]
    [InlineData("webkit-org.w3.clearkey", false)]
    [InlineData(" org.w3.clearkey", false)]
    [InlineData("org.w3.clearkey ", false)]
    [InlineData(".org.w3.clearkey", false)]
    [InlineData("org.w3.clearke", false)]
    public void TheKeySystemNameIsMatchedExactly(string keySystem, bool supported) =>
        Assert.Equal(supported, MediaKeySystemSupport.IsSupportedKeySystem(keySystem));

    [Fact]
    public void ABasicConfigurationComesBackWhole()
    {
        var supported = Support().GetSupportedConfiguration(
            Candidate(initDataTypes: ["cenc"], audio: [AudioType], video: [VideoType], label: "abcd"));

        Assert.NotNull(supported);
        Assert.Equal("abcd", supported.Label);
        Assert.Equal(["cenc"], supported.InitDataTypes);
        Assert.Equal(AudioType, Assert.Single(supported.AudioCapabilities).ContentType);
        Assert.Equal(VideoType, Assert.Single(supported.VideoCapabilities).ContentType);
        Assert.Equal(["temporary"], supported.SessionTypes);
    }

    [Fact]
    public void APartiallySupportedConfigurationKeepsOnlyWhatWorks()
    {
        var supported = Support().GetSupportedConfiguration(
            Candidate(initDataTypes: ["fakeidt", "cenc"], audio: ["audio/fake", AudioType], video: ["video/fake", VideoType]));

        Assert.NotNull(supported);
        Assert.Equal(["cenc"], supported.InitDataTypes);
        Assert.Equal(AudioType, Assert.Single(supported.AudioCapabilities).ContentType);
        Assert.Equal(VideoType, Assert.Single(supported.VideoCapabilities).ContentType);
    }

    [Fact]
    public void AnEmptyConfigurationIsNotSupported() =>
        Assert.Null(Support().GetSupportedConfiguration(Candidate()));

    [Fact]
    public void AConfigurationWhoseInitDataTypesAreAllUnknownIsNotSupported() =>
        Assert.Null(Support().GetSupportedConfiguration(Candidate(initDataTypes: ["fakeidt"], video: [VideoType])));

    [Fact]
    public void TheFirstSupportedCandidateWins()
    {
        var support = Support();
        var supported = support.GetSupportedConfiguration(
        [
            Candidate(initDataTypes: ["fakeidt"], video: [VideoType]),
            Candidate(initDataTypes: ["cenc"], video: [VideoType]),
        ]);

        Assert.NotNull(supported);
        Assert.Equal(["cenc"], supported.InitDataTypes);
    }

    [Theory]
    // Formatting is preserved exactly, because getConfiguration() hands it back.
    [InlineData("video/mp4;  codecs=\"avc1.4d401e\"")]
    [InlineData(" video/mp4;codecs=\"avc1.4d401e\"")]
    [InlineData("video/mp4 ;codecs=\"avc1.4d401e\"")]
    [InlineData("video/mp4;codecs=\"avc1.4d401e\" ")]
    [InlineData("VIDEO/MP4;codecs=\"avc1.4d401e\"")]
    [InlineData("video/mp4;CODECS=\"avc1.4d401e\"")]
    public void ContentTypeFormattingIsPreserved(string contentType)
    {
        var supported = Support().GetSupportedConfiguration(Candidate(video: [contentType]));
        Assert.NotNull(supported);
        Assert.Equal(contentType, Assert.Single(supported.VideoCapabilities).ContentType);
    }

    [Theory]
    // An audio capability must name an audio container and audio codecs.
    [InlineData("audio/mp4;codecs=\"avc1.4d401e\"")]
    [InlineData("audio/webm; codecs=\"vp8\"")]
    [InlineData("audio/mp4; codecs=vorbis")]
    [InlineData("audio/webm; codecs=fake")]
    [InlineData("audio/webm; codecs=mp4a.40.2")]
    [InlineData("audio/fake")]
    [InlineData("video/mp4;codecs=\"avc1.4d401e\"")]
    public void AnAudioCapabilityMustNameAudio(string contentType) =>
        Assert.Null(Support().GetSupportedConfiguration(Candidate(audio: [contentType])));

    [Theory]
    // A video capability must name a video container and video codecs.
    [InlineData("video/webm; codecs=\"vorbis\"")]
    [InlineData("video/webm; codecs=\"vp8,vorbis\"")]
    [InlineData("video/mp4; codecs=\"mp4a.40.2\"")]
    [InlineData("video/webm; codecs=\"avc1\"")]
    [InlineData("audio/mp4;codecs=\"mp4a.40.2\"")]
    [InlineData("fake")]
    [InlineData("video/fake")]
    // A parameter we do not recognise may change what the bytes are.
    [InlineData("video/webm; foo=\"bar\"")]
    [InlineData("video/mp4;codecs=\"avc1.4d401e\"; foo=\"bar\"")]
    // Codec names are case sensitive, and the list may not carry an empty entry.
    [InlineData("video/mp4;codecs=\"AVC1.4D401E\"")]
    [InlineData("video/mp4;codecs=\",avc1.4d401e\"")]
    public void AVideoCapabilityMustNameVideo(string contentType) =>
        Assert.Null(Support().GetSupportedConfiguration(Candidate(video: [contentType])));

    [Fact]
    public void ADistinctiveIdentifierCannotBeRequired()
    {
        var candidate = Candidate(video: [VideoType]) with { DistinctiveIdentifier = MediaKeysRequirement.Required };
        Assert.Null(Support().GetSupportedConfiguration(candidate));
    }

    [Fact]
    public void PersistentStateCannotBeRequired()
    {
        var candidate = Candidate(video: [VideoType]) with { PersistentState = MediaKeysRequirement.Required };
        Assert.Null(Support().GetSupportedConfiguration(candidate));
    }

    [Fact]
    public void OnlyTemporarySessionsAreOffered()
    {
        var support = Support();
        Assert.Null(support.GetSupportedConfiguration(Candidate(video: [VideoType]) with { SessionTypes = ["persistent-license"] }));
        Assert.Null(support.GetSupportedConfiguration(Candidate(video: [VideoType]) with { SessionTypes = ["nonsense"] }));
        Assert.NotNull(support.GetSupportedConfiguration(Candidate(video: [VideoType]) with { SessionTypes = ["temporary"] }));
    }

    [Theory]
    [InlineData("cenc", true)]
    [InlineData("cbcs", true)]
    [InlineData("cbcs-1-9", true)]
    [InlineData("nonsense", false)]
    public void AnEncryptionSchemeIsEchoedBackWhenItIsOneWeDecrypt(string scheme, bool supported)
    {
        var candidate = Candidate(video: [VideoType]) with
        {
            VideoCapabilities = [new MediaKeySystemMediaCapability(VideoType, EncryptionScheme: scheme)],
        };

        var result = Support().GetSupportedConfiguration(candidate);
        if (!supported)
        {
            Assert.Null(result);
            return;
        }

        Assert.NotNull(result);
        Assert.Equal(scheme, Assert.Single(result.VideoCapabilities).EncryptionScheme);
    }

    [Fact]
    public void ARobustnessLevelIsNotOnOfferForClearKey()
    {
        var candidate = Candidate(video: [VideoType]) with
        {
            VideoCapabilities = [new MediaKeySystemMediaCapability(VideoType, Robustness: "HW_SECURE_ALL")],
        };
        Assert.Null(Support().GetSupportedConfiguration(candidate));
    }

    // ---- the session state machine ----

    private static readonly byte[] s_keyId =
        [0xad, 0x13, 0xf9, 0xea, 0x2b, 0xe6, 0x98, 0xb8, 0x75, 0xf5, 0x04, 0xa8, 0xe3, 0xcc, 0xea, 0x64];

    private static readonly byte[] s_secondKeyId = [.. Enumerable.Repeat((byte)0x22, 16)];

    private const string License =
        "{\"keys\":[{\"kty\":\"oct\",\"kid\":\"rRP56ivmmLh19QSo48zqZA\",\"k\":\"vn34o2Z6ao_VZNDtgTOalQ\"}]}";

    private static byte[] KeyIdsInitData(params byte[][] keyIds) =>
        Encoding.UTF8.GetBytes("{\"kids\":[" + string.Join(",", keyIds.Select(k => "\"" + new KeyId(k).ToBase64Url() + "\"")) + "]}");

    [Fact]
    public void GenerateRequestNamesTheKeysAndGivesTheSessionAnIdentity()
    {
        var cdm = new ClearKeyCdm();
        var session = cdm.CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);
        Assert.Equal(string.Empty, session.SessionId);

        byte[]? message = null;
        var type = default(MediaKeyMessageType);
        session.MessageGenerated += (t, m) => { type = t; message = m; };

        Assert.True(session.GenerateRequest(EmeInitDataType.KeyIds, KeyIdsInitData(s_keyId)).Succeeded);

        Assert.NotEqual(string.Empty, session.SessionId);
        Assert.Equal(MediaKeyMessageType.LicenseRequest, type);
        Assert.NotNull(message);
        Assert.Contains("rRP56ivmmLh19QSo48zqZA", Encoding.UTF8.GetString(message), StringComparison.Ordinal);
        Assert.Empty(session.KeyStatuses);
    }

    [Fact]
    public void GenerateRequestRefusesInitDataItCannotRead()
    {
        var session = new ClearKeyCdm().CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);

        Assert.Equal("TypeError", session.GenerateRequest(EmeInitDataType.KeyIds, []).ExceptionName);
        Assert.Equal("NotSupportedError", session.GenerateRequest(EmeInitDataType.KeyIds, "junk"u8).ExceptionName);
    }

    [Fact]
    public void GenerateRequestIsCallableOnlyOnce()
    {
        var session = new ClearKeyCdm().CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);
        Assert.True(session.GenerateRequest(EmeInitDataType.KeyIds, KeyIdsInitData(s_keyId)).Succeeded);
        Assert.Equal("InvalidStateError", session.GenerateRequest(EmeInitDataType.KeyIds, KeyIdsInitData(s_keyId)).ExceptionName);
    }

    [Fact]
    public void UpdateStoresTheLicenseKeysAndMarksThemUsable()
    {
        var cdm = new ClearKeyCdm();
        var session = cdm.CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);
        session.GenerateRequest(EmeInitDataType.KeyIds, KeyIdsInitData(s_keyId));

        int changes = 0;
        session.KeyStatusesChanged += () => changes++;

        Assert.True(session.Update(Encoding.ASCII.GetBytes(License)).Succeeded);

        Assert.Equal(1, changes);
        var status = Assert.Single(session.KeyStatuses);
        Assert.Equal(new KeyId(s_keyId), status.Key);
        Assert.Equal(MediaKeyStatus.Usable, status.Value);
        Assert.True(cdm.TryGetKey(new KeyId(s_keyId), out byte[] key));
        Assert.Equal(16, key.Length);
    }

    [Fact]
    public void UpdateRejectsALicenseItCannotRead()
    {
        var session = new ClearKeyCdm().CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);
        session.GenerateRequest(EmeInitDataType.KeyIds, KeyIdsInitData(s_keyId));

        Assert.Equal("TypeError", session.Update([0x00, 0x11, 0x22, 0x33]).ExceptionName);
        Assert.Equal("TypeError", session.Update([]).ExceptionName);
    }

    [Fact]
    public void UpdateBeforeGenerateRequestIsAnInvalidState()
    {
        var session = new ClearKeyCdm().CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);
        Assert.Equal("InvalidStateError", session.Update(Encoding.ASCII.GetBytes(License)).ExceptionName);
    }

    [Fact]
    public void KeyStatusesAreOrderedByKeyId()
    {
        var cdm = new ClearKeyCdm();
        var session = cdm.CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);
        session.GenerateRequest(EmeInitDataType.KeyIds, KeyIdsInitData(s_keyId, s_secondKeyId));

        // 0x22... sorts before 0xad..., whichever order the license lists them in.
        string twoKeys = "{\"keys\":["
            + "{\"kty\":\"oct\",\"kid\":\"rRP56ivmmLh19QSo48zqZA\",\"k\":\"vn34o2Z6ao_VZNDtgTOalQ\"},"
            + "{\"kty\":\"oct\",\"kid\":\"" + new KeyId(s_secondKeyId).ToBase64Url() + "\",\"k\":\"vn34o2Z6ao_VZNDtgTOalQ\"}]}";

        Assert.True(session.Update(Encoding.ASCII.GetBytes(twoKeys)).Succeeded);
        Assert.Equal(
            [new KeyId(s_secondKeyId), new KeyId(s_keyId)],
            session.KeyStatuses.Select(s => s.Key));
    }

    [Fact]
    public void ClosingASessionEmptiesItsStatusesAndDropsItsKeys()
    {
        var cdm = new ClearKeyCdm();
        var session = cdm.CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);
        session.GenerateRequest(EmeInitDataType.KeyIds, KeyIdsInitData(s_keyId));
        session.Update(Encoding.ASCII.GetBytes(License));

        string? reason = null;
        session.SessionClosed += r => reason = r;

        Assert.True(session.Close().Succeeded);

        Assert.True(session.IsClosed);
        Assert.Empty(session.KeyStatuses);
        Assert.Equal("closed-by-application", reason);
        Assert.False(cdm.HasKey(new KeyId(s_keyId)));
    }

    [Fact]
    public void ClosingOneSessionLeavesAKeyAnotherStillHolds()
    {
        var cdm = new ClearKeyCdm();
        var first = cdm.CreateSession(MediaKeySessionType.Temporary);
        var second = cdm.CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(first);
        Assert.NotNull(second);

        foreach (var session in new[] { first, second })
        {
            session.GenerateRequest(EmeInitDataType.KeyIds, KeyIdsInitData(s_keyId));
            session.Update(Encoding.ASCII.GetBytes(License));
        }

        first.Close();
        Assert.True(cdm.HasKey(new KeyId(s_keyId)));

        second.Close();
        Assert.False(cdm.HasKey(new KeyId(s_keyId)));
    }

    [Fact]
    public void RemoveReleasesTheKeysAndAsksForAnAcknowledgement()
    {
        var cdm = new ClearKeyCdm();
        var session = cdm.CreateSession(MediaKeySessionType.Temporary);
        Assert.NotNull(session);
        session.GenerateRequest(EmeInitDataType.KeyIds, KeyIdsInitData(s_keyId));
        session.Update(Encoding.ASCII.GetBytes(License));

        byte[]? release = null;
        var type = default(MediaKeyMessageType);
        session.MessageGenerated += (t, m) => { type = t; release = m; };

        Assert.True(session.Remove().Succeeded);

        Assert.Equal(MediaKeyMessageType.LicenseRelease, type);
        Assert.NotNull(release);
        Assert.False(cdm.HasKey(new KeyId(s_keyId)));
        Assert.Equal(MediaKeyStatus.Released, Assert.Single(session.KeyStatuses).Value);
        Assert.False(session.IsClosed);

        Assert.True(session.Update(release).Succeeded);
        Assert.True(session.IsClosed);
    }

    [Fact]
    public void TheCdmRefusesToGrowWithoutBound()
    {
        var cdm = new ClearKeyCdm();
        for (int i = 0; i < ClearKeyCdm.MaxSessions; i++)
            Assert.NotNull(cdm.CreateSession(MediaKeySessionType.Temporary));
        Assert.Null(cdm.CreateSession(MediaKeySessionType.Temporary));
    }

    [Fact]
    public void ClearKeyHasNoServerCertificate() =>
        Assert.False(ClearKeyCdm.SetServerCertificate([0x01, 0x02]));
}
