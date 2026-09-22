using System.Security.Cryptography;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.Media.Eme;

/// <summary>
/// What an EME operation owes its caller: success, or the DOM exception the spec names.
/// </summary>
/// <param name="ExceptionName">Empty on success, otherwise a DOM exception name such as <c>TypeError</c>.</param>
/// <param name="Message">A human-readable reason, carried into the rejection and the log.</param>
public readonly record struct EmeResult(string ExceptionName, string Message)
{
    public static EmeResult Ok => new(string.Empty, string.Empty);

    public bool Succeeded => ExceptionName.Length == 0;

    public static EmeResult TypeError(string message) => new("TypeError", message);

    public static EmeResult NotSupported(string message) => new("NotSupportedError", message);

    public static EmeResult InvalidState(string message) => new("InvalidStateError", message);

    public static EmeResult QuotaExceeded(string message) => new("QuotaExceededError", message);
}

/// <summary>Orders key IDs by their bytes, which is the order <c>keyStatuses</c> iterates in.</summary>
internal sealed class KeyIdOrder : IComparer<KeyId>
{
    public static readonly KeyIdOrder Instance = new();

    public int Compare(KeyId x, KeyId y) => x.Span.SequenceCompareTo(y.Span);
}

/// <summary>
/// The Clear Key content decryption module (EME §9.1, ADR-0005). It holds the keys the
/// page has given us and answers the decryptor's lookups.
/// </summary>
/// <remarks>
/// Clear Key is the only key system this engine implements. Widevine and PlayReady need
/// commercial licensing and a binary CDM, which ADR-0005 rules out, so a page asking for
/// one is told "no" rather than being led on.
///
/// The key store is read by the decryption thread and written by the session's own thread,
/// so it is guarded; everything else on a session belongs to whoever created it.
/// </remarks>
public sealed class ClearKeyCdm : IMediaKeySource
{
    /// <summary>The key system string this CDM answers to.</summary>
    public const string KeySystem = "org.w3.clearkey";

    /// <summary>A ceiling on live sessions, so a page cannot grow the key store without bound.</summary>
    public const int MaxSessions = 128;

    private readonly Lock _gate = new();
    private readonly Dictionary<KeyId, byte[]> _keys = [];
    private readonly List<ClearKeySession> _sessions = [];
    private readonly IMediaLogSink? _log;

    public ClearKeyCdm(IMediaLogSink? log = null) => _log = log;

    /// <summary>Raised whenever a key becomes available, so a player waiting for one can retry.</summary>
    public event Action? KeysChanged;

    public IReadOnlyList<ClearKeySession> Sessions
    {
        get
        {
            lock (_gate)
                return [.. _sessions];
        }
    }

    /// <summary>EME §5.2 <c>createSession()</c>.</summary>
    public ClearKeySession? CreateSession(MediaKeySessionType sessionType)
    {
        lock (_gate)
        {
            if (_sessions.Count >= MaxSessions)
                return null;
            var session = new ClearKeySession(this, sessionType);
            _sessions.Add(session);
            return session;
        }
    }

    /// <summary>
    /// Clear Key has no server certificate: EME §5.2 says to resolve with false when the
    /// key system does not use one.
    /// </summary>
    public static bool SetServerCertificate(ReadOnlySpan<byte> certificate) => certificate.Length != 0 && false;

    /// <summary>
    /// The decryptor's lookup. It runs on the decode thread, so it copies the key out
    /// under the lock rather than handing back the stored array.
    /// </summary>
    public bool TryGetKey(KeyId keyId, out byte[] key)
    {
        lock (_gate)
        {
            if (_keys.TryGetValue(keyId, out byte[]? stored))
            {
                key = (byte[])stored.Clone();
                return true;
            }
        }

        key = [];
        return false;
    }

    public bool HasKey(KeyId keyId)
    {
        lock (_gate)
            return _keys.ContainsKey(keyId);
    }

    internal void StoreKeys(IReadOnlyList<ClearKeyEntry> entries)
    {
        lock (_gate)
        {
            foreach (var entry in entries)
                _keys[entry.KeyId] = entry.Key;
        }

        // The count is safe to log; key material and key IDs never are.
        _log?.Emit(PlayerId.None, MediaEventKind.EmeKeysChanged, MediaLogLevel.Info, "Clear Key session added keys",
            ("count", entries.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        KeysChanged?.Invoke();
    }

    /// <summary>
    /// Drops the keys a session held, unless another live session still carries them: two
    /// sessions may legitimately have been given the same key.
    /// </summary>
    internal void ReleaseKeys(ClearKeySession session, IReadOnlyList<KeyId> keyIds)
    {
        lock (_gate)
        {
            foreach (var keyId in keyIds)
            {
                bool heldElsewhere = false;
                foreach (var other in _sessions)
                {
                    if (!ReferenceEquals(other, session) && !other.IsClosed && other.HasKeyId(keyId))
                    {
                        heldElsewhere = true;
                        break;
                    }
                }

                if (!heldElsewhere)
                    _keys.Remove(keyId);
            }
        }
    }

    internal void Forget(ClearKeySession session)
    {
        lock (_gate)
            _sessions.Remove(session);
    }

    /// <summary>
    /// EME §5.2 <c>getStatusForPolicy()</c>. Clear Key decrypts into the page, where no
    /// output protection applies, so any HDCP level a page asks about is usable.
    /// </summary>
    public static MediaKeyStatus GetStatusForPolicy(string? minHdcpVersion) =>
        string.IsNullOrEmpty(minHdcpVersion) ? MediaKeyStatus.Usable : MediaKeyStatus.Usable;
}

/// <summary>
/// One <c>MediaKeySession</c> (EME §6): its lifetime, its key statuses, and the messages
/// it asks the page to carry to a license server.
/// </summary>
/// <remarks>
/// The state machine is here rather than in the realm so the rules are testable without a
/// browser. The realm owns the promises, the event queue and the WebIDL shapes, and calls
/// in; every entry point returns an <see cref="EmeResult"/> naming the exception it owes.
/// </remarks>
public sealed class ClearKeySession
{
    private readonly ClearKeyCdm _cdm;
    private readonly SortedDictionary<KeyId, MediaKeyStatus> _statuses = new(KeyIdOrder.Instance);
    private bool _callable = true;
    private bool _requested;

    internal ClearKeySession(ClearKeyCdm cdm, MediaKeySessionType sessionType)
    {
        _cdm = cdm;
        SessionType = sessionType;
    }

    /// <summary>Empty until <c>generateRequest()</c> or <c>load()</c> gives the session an identity.</summary>
    public string SessionId { get; private set; } = string.Empty;

    public MediaKeySessionType SessionType { get; }

    /// <summary>NaN: Clear Key licenses do not expire.</summary>
    public double Expiration => double.NaN;

    public bool IsClosed { get; private set; }

    /// <summary>The key statuses in key ID order, which is the order the map iterates in.</summary>
    public IReadOnlyList<KeyValuePair<KeyId, MediaKeyStatus>> KeyStatuses => [.. _statuses];

    /// <summary>A message for the page to carry to a license server.</summary>
    public event Action<MediaKeyMessageType, byte[]>? MessageGenerated;

    /// <summary>The statuses changed; the realm queues <c>keystatuseschange</c>.</summary>
    public event Action? KeyStatusesChanged;

    /// <summary>The session reached its closed state, with the reason EME §6.2 names.</summary>
    public event Action<string>? SessionClosed;

    internal bool HasKeyId(KeyId keyId) => _statuses.ContainsKey(keyId);

    /// <summary>
    /// EME §6.4.3 <c>generateRequest()</c>. The initialization data names the keys; the
    /// message asking for them is generated synchronously, since Clear Key needs no
    /// round trip to build one.
    /// </summary>
    public EmeResult GenerateRequest(EmeInitDataType initDataType, ReadOnlySpan<byte> initData)
    {
        if (IsClosed || !_callable)
            return EmeResult.InvalidState("the session is closed or has already been used");

        if (initData.IsEmpty)
            return EmeResult.TypeError("the initialization data is empty");

        if (!EmeInitData.TryGetKeyIds(initDataType, initData, out var keyIds))
            return EmeResult.NotSupported("the initialization data is not a valid instance of its type");

        _callable = false;
        _requested = true;
        SessionId = NewSessionId();

        byte[] message = ClearKeyLicense.CreateLicenseRequest(keyIds, SessionType);
        MessageGenerated?.Invoke(MediaKeyMessageType.LicenseRequest, message);
        return EmeResult.Ok;
    }

    /// <summary>
    /// EME §6.4.4 <c>load()</c>. Clear Key keeps nothing across a page load, so there is
    /// never a stored session to load; the caller resolves with false.
    /// </summary>
    public EmeResult Load(string sessionId, out bool loaded)
    {
        loaded = false;
        if (IsClosed || !_callable)
            return EmeResult.InvalidState("the session is closed or has already been used");
        if (string.IsNullOrEmpty(sessionId))
            return EmeResult.TypeError("the session ID is empty");
        if (SessionType != MediaKeySessionType.PersistentLicense)
            return EmeResult.InvalidState("only a persistent session can be loaded");

        _callable = false;
        return EmeResult.Ok;
    }

    /// <summary>
    /// EME §6.4.5 <c>update()</c>. A license adds its keys; a release acknowledgement
    /// closes a session that asked to be released.
    /// </summary>
    public EmeResult Update(ReadOnlySpan<byte> response)
    {
        if (IsClosed)
            return EmeResult.InvalidState("the session is closed");
        if (SessionId.Length == 0)
            return EmeResult.InvalidState("the session has no ID yet");
        if (response.IsEmpty)
            return EmeResult.TypeError("the response is empty");

        if (Releasing)
        {
            if (!ClearKeyLicense.TryParseLicenseReleaseAcknowledgement(response, out _))
                return EmeResult.TypeError("the response is not a release acknowledgement");
            CloseInternal("release-acknowledged");
            return EmeResult.Ok;
        }

        if (!ClearKeyLicense.TryParseLicense(response, SessionType, out var keys, out string failure))
            return EmeResult.TypeError($"the response is not a Clear Key license: {failure}");

        _cdm.StoreKeys(keys);
        foreach (var entry in keys)
            _statuses[entry.KeyId] = MediaKeyStatus.Usable;

        KeyStatusesChanged?.Invoke();
        return EmeResult.Ok;
    }

    /// <summary>Set once <c>remove()</c> has asked for a release and is waiting to be acknowledged.</summary>
    public bool Releasing { get; private set; }

    /// <summary>
    /// EME §6.4.6 <c>close()</c>. The session's keys go away with it, so the statuses
    /// empty and a final <c>keystatuseschange</c> tells the page they did.
    /// </summary>
    public EmeResult Close()
    {
        if (IsClosed)
            return EmeResult.Ok;
        if (SessionId.Length == 0 && !_requested)
            return EmeResult.InvalidState("the session has no ID yet");

        CloseInternal("closed-by-application");
        return EmeResult.Ok;
    }

    /// <summary>
    /// EME §6.4.7 <c>remove()</c>. The keys are dropped at once and the page is handed a
    /// release message; the session stays open until that release is acknowledged.
    /// </summary>
    public EmeResult Remove()
    {
        if (IsClosed)
            return EmeResult.InvalidState("the session is closed");
        if (SessionId.Length == 0)
            return EmeResult.InvalidState("the session has no ID yet");

        var keyIds = _statuses.Keys.ToArray();
        _cdm.ReleaseKeys(this, keyIds);

        foreach (var keyId in keyIds)
            _statuses[keyId] = MediaKeyStatus.Released;

        Releasing = true;
        KeyStatusesChanged?.Invoke();
        MessageGenerated?.Invoke(MediaKeyMessageType.LicenseRelease, ClearKeyLicense.CreateLicenseRelease(keyIds, SessionType));
        return EmeResult.Ok;
    }

    private void CloseInternal(string reason)
    {
        var keyIds = _statuses.Keys.ToArray();
        _cdm.ReleaseKeys(this, keyIds);
        _statuses.Clear();
        IsClosed = true;
        _cdm.Forget(this);

        KeyStatusesChanged?.Invoke();
        SessionClosed?.Invoke(reason);
    }

    /// <summary>
    /// A session ID must be unique and must not carry information about the origin or the
    /// content, so it is simply random.
    /// </summary>
    private static string NewSessionId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
}
