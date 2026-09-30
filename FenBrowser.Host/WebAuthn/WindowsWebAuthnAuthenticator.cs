using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.FenEngine.WebAPIs.WebAuthn;

namespace FenBrowser.Host.WebAuthn;

/// <summary>
/// Web Authentication ceremonies through Windows' platform WebAuthn API (webauthn.dll),
/// which owns Windows Hello, security keys and phone (hybrid) passkeys and shows its own
/// dialog naming the relying party. The browser side of the client contract stays here:
/// the RP ID is checked against the origin and the client data is built from the origin
/// this process was handed, never from the page (WebAuthn L3 §5.1.3 / §5.1.4.1).
/// </summary>
internal sealed unsafe class WindowsWebAuthnAuthenticator : IWebAuthnAuthenticator
{
    private const string Dll = "webauthn.dll";
    private const int DefaultTimeoutMs = 120_000;

    public static bool IsSupported
    {
        get
        {
            if (!OperatingSystem.IsWindows())
            {
                return false;
            }

            try
            {
                return WebAuthNGetApiVersionNumber() >= 1;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }
    }

    public Task<bool> IsUserVerifyingPlatformAuthenticatorAvailableAsync() => Task.Run(() =>
    {
        try
        {
            return WebAuthNIsUserVerifyingPlatformAuthenticatorAvailable(out int available) == 0 && available != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    });

    public Task<WebAuthnResult> GetAssertionAsync(WebAuthnGetRequest request, CancellationToken cancellationToken)
    {
        if (request == null || !WebAuthnClient.TryResolveRpId(request.Origin, request.RpId, out var rpId, out var error))
        {
            return Task.FromResult(WebAuthnResult.Failure("SecurityError", "The relying party ID is not a registrable domain suffix of, nor equal to, the current domain."));
        }

        byte[] clientData = WebAuthnClient.BuildClientDataJson("webauthn.get", request.Challenge ?? string.Empty, request.Origin);
        return Task.Run(() => RunGetAssertion(request, rpId, clientData), cancellationToken);
    }

    public Task<WebAuthnResult> MakeCredentialAsync(WebAuthnCreateRequest request, CancellationToken cancellationToken)
    {
        if (request == null || !WebAuthnClient.TryResolveRpId(request.Origin, request.RpId, out var rpId, out _))
        {
            return Task.FromResult(WebAuthnResult.Failure("SecurityError", "The relying party ID is not a registrable domain suffix of, nor equal to, the current domain."));
        }

        byte[] userId = WebAuthnClient.FromBase64Url(request.UserId);
        if (userId.Length is < 1 or > 64)
        {
            return Task.FromResult(WebAuthnResult.Failure("TypeError", "user.id must be between 1 and 64 bytes."));
        }

        byte[] clientData = WebAuthnClient.BuildClientDataJson("webauthn.create", request.Challenge ?? string.Empty, request.Origin);
        return Task.Run(() => RunMakeCredential(request, rpId, userId, clientData), cancellationToken);
    }

    private static WebAuthnResult RunGetAssertion(WebAuthnGetRequest request, string rpId, byte[] clientData)
    {
        using var pins = new PinSet();
        var clientDataStruct = new ClientData
        {
            dwVersion = 1,
            cbClientDataJSON = (uint)clientData.Length,
            pbClientDataJSON = pins.Pin(clientData),
            pwszHashAlgId = pins.String("SHA-256"),
        };

        var options = new GetAssertionOptions
        {
            dwVersion = 4,
            dwTimeoutMilliseconds = (uint)ResolveTimeout(request.TimeoutMs),
            dwAuthenticatorAttachment = 0,
            dwUserVerificationRequirement = UserVerification(request.UserVerification),
            pAllowCredentialList = BuildCredentialList(request.AllowCredentials, pins),
        };

        IntPtr assertionPtr = IntPtr.Zero;
        int hr;
        try
        {
            hr = WebAuthNAuthenticatorGetAssertion(OwnerWindow(), pins.String(rpId), &clientDataStruct, &options, out assertionPtr);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return WebAuthnResult.NotAllowed();
        }

        if (hr != 0 || assertionPtr == IntPtr.Zero)
        {
            return ErrorFor(hr);
        }

        try
        {
            var assertion = (Assertion*)assertionPtr;
            byte[] userHandle = Copy(assertion->pbUserId, assertion->cbUserId);
            return new WebAuthnResult
            {
                ClientDataJson = WebAuthnClient.ToBase64Url(clientData),
                CredentialId = WebAuthnClient.ToBase64Url(Copy(assertion->Credential.pbId, assertion->Credential.cbId)),
                AuthenticatorData = WebAuthnClient.ToBase64Url(Copy(assertion->pbAuthenticatorData, assertion->cbAuthenticatorData)),
                Signature = WebAuthnClient.ToBase64Url(Copy(assertion->pbSignature, assertion->cbSignature)),
                UserHandle = userHandle.Length > 0 ? WebAuthnClient.ToBase64Url(userHandle) : null,
            };
        }
        finally
        {
            WebAuthNFreeAssertion(assertionPtr);
        }
    }

    private static WebAuthnResult RunMakeCredential(WebAuthnCreateRequest request, string rpId, byte[] userId, byte[] clientData)
    {
        using var pins = new PinSet();
        var rp = new RpEntity
        {
            dwVersion = 1,
            pwszId = pins.String(rpId),
            pwszName = pins.String(string.IsNullOrWhiteSpace(request.RpName) ? rpId : request.RpName),
        };
        var user = new UserEntity
        {
            dwVersion = 1,
            cbId = (uint)userId.Length,
            pbId = pins.Pin(userId),
            pwszName = pins.String(request.UserName ?? string.Empty),
            pwszDisplayName = pins.String(request.UserDisplayName ?? request.UserName ?? string.Empty),
        };

        // §5.4.3 step: an empty pubKeyCredParams means ES256 then RS256.
        var algorithms = request.Algorithms is { Count: > 0 } ? request.Algorithms : new List<int> { -7, -257 };
        var parameters = new CoseParameter[algorithms.Count];
        IntPtr publicKey = pins.String("public-key");
        for (int i = 0; i < algorithms.Count; i++)
        {
            parameters[i] = new CoseParameter { dwVersion = 1, pwszCredentialType = publicKey, lAlg = algorithms[i] };
        }

        var coseParameters = new CoseParameters
        {
            cCredentialParameters = (uint)parameters.Length,
            pCredentialParameters = pins.Pin(parameters),
        };
        var clientDataStruct = new ClientData
        {
            dwVersion = 1,
            cbClientDataJSON = (uint)clientData.Length,
            pbClientDataJSON = pins.Pin(clientData),
            pwszHashAlgId = pins.String("SHA-256"),
        };
        var options = new MakeCredentialOptions
        {
            dwVersion = 3,
            dwTimeoutMilliseconds = (uint)ResolveTimeout(request.TimeoutMs),
            dwAuthenticatorAttachment = request.AuthenticatorAttachment switch
            {
                "platform" => 1u,
                "cross-platform" => 2u,
                _ => 0u,
            },
            bRequireResidentKey = string.Equals(request.ResidentKey, "required", StringComparison.Ordinal) ? 1 : 0,
            dwUserVerificationRequirement = UserVerification(request.UserVerification),
            dwAttestationConveyancePreference = request.Attestation switch
            {
                "indirect" => 2u,
                "direct" or "enterprise" => 3u,
                _ => 1u,
            },
            pExcludeCredentialList = BuildCredentialList(request.ExcludeCredentials, pins),
        };

        IntPtr attestationPtr = IntPtr.Zero;
        int hr;
        try
        {
            hr = WebAuthNAuthenticatorMakeCredential(OwnerWindow(), &rp, &user, &coseParameters, &clientDataStruct, &options, out attestationPtr);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return WebAuthnResult.NotAllowed();
        }

        if (hr != 0 || attestationPtr == IntPtr.Zero)
        {
            return ErrorFor(hr);
        }

        try
        {
            var attestation = (CredentialAttestation*)attestationPtr;
            uint usedTransport = attestation->dwVersion >= 3 ? attestation->dwUsedTransport : 0;
            return new WebAuthnResult
            {
                ClientDataJson = WebAuthnClient.ToBase64Url(clientData),
                CredentialId = WebAuthnClient.ToBase64Url(Copy(attestation->pbCredentialId, attestation->cbCredentialId)),
                AuthenticatorData = WebAuthnClient.ToBase64Url(Copy(attestation->pbAuthenticatorData, attestation->cbAuthenticatorData)),
                AttestationObject = WebAuthnClient.ToBase64Url(Copy(attestation->pbAttestationObject, attestation->cbAttestationObject)),
                Transports = TransportNames(usedTransport),
                AuthenticatorAttachment = usedTransport == 0 ? null : (usedTransport & TransportInternal) != 0 ? "platform" : "cross-platform",
            };
        }
        finally
        {
            WebAuthNFreeCredentialAttestation(attestationPtr);
        }
    }

    private static int ResolveTimeout(int requested) =>
        requested <= 0 ? DefaultTimeoutMs : Math.Clamp(requested, 30_000, 600_000);

    private static uint UserVerification(string value) => value switch
    {
        "required" => 1u,
        "discouraged" => 3u,
        _ => 2u,
    };

    private const uint TransportUsb = 0x1, TransportNfc = 0x2, TransportBle = 0x4, TransportInternal = 0x10, TransportHybrid = 0x20;

    private static List<string> TransportNames(uint flags)
    {
        var names = new List<string>();
        if ((flags & TransportUsb) != 0) names.Add("usb");
        if ((flags & TransportNfc) != 0) names.Add("nfc");
        if ((flags & TransportBle) != 0) names.Add("ble");
        if ((flags & TransportInternal) != 0) names.Add("internal");
        if ((flags & TransportHybrid) != 0) names.Add("hybrid");
        return names;
    }

    private static uint TransportFlags(IEnumerable<string> names)
    {
        uint flags = 0;
        foreach (var name in names ?? Array.Empty<string>())
        {
            flags |= name switch
            {
                "usb" => TransportUsb,
                "nfc" => TransportNfc,
                "ble" => TransportBle,
                "internal" => TransportInternal,
                "hybrid" => TransportHybrid,
                _ => 0u,
            };
        }

        return flags;
    }

    private static IntPtr BuildCredentialList(List<WebAuthnCredentialDescriptor> descriptors, PinSet pins)
    {
        if (descriptors == null || descriptors.Count == 0)
        {
            return IntPtr.Zero;
        }

        var pointers = new IntPtr[descriptors.Count];
        IntPtr publicKey = pins.String("public-key");
        for (int i = 0; i < descriptors.Count; i++)
        {
            byte[] id = WebAuthnClient.FromBase64Url(descriptors[i].Id);
            var credential = new[]
            {
                new CredentialEx
                {
                    dwVersion = 1,
                    cbId = (uint)id.Length,
                    pbId = pins.Pin(id),
                    pwszCredentialType = publicKey,
                    dwTransports = TransportFlags(descriptors[i].Transports),
                },
            };
            pointers[i] = pins.Pin(credential);
        }

        var list = new[] { new CredentialList { cCredentials = (uint)pointers.Length, ppCredentials = pins.Pin(pointers) } };
        return pins.Pin(list);
    }

    private static WebAuthnResult ErrorFor(int hr)
    {
        string name;
        try
        {
            name = Marshal.PtrToStringUni(WebAuthNGetErrorName(hr));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            name = null;
        }

        // webauthn.dll names the DOMException itself; cancellation and timeouts are NotAllowedError.
        return name switch
        {
            "InvalidStateError" => WebAuthnResult.Failure(name, "The authenticator already holds a credential for this account."),
            "ConstraintError" or "NotSupportedError" => WebAuthnResult.Failure(name, "The authenticator does not support the requested options."),
            _ => WebAuthnResult.NotAllowed(),
        };
    }

    private static IntPtr OwnerWindow()
    {
        IntPtr hwnd = Process.GetCurrentProcess().MainWindowHandle;
        return hwnd != IntPtr.Zero ? hwnd : GetForegroundWindow();
    }

    private static byte[] Copy(IntPtr data, uint length)
    {
        if (data == IntPtr.Zero || length == 0)
        {
            return Array.Empty<byte>();
        }

        var bytes = new byte[length];
        Marshal.Copy(data, bytes, 0, (int)length);
        return bytes;
    }

    /// <summary>Keeps every buffer handed to webauthn.dll pinned until the call returns.</summary>
    private sealed class PinSet : IDisposable
    {
        private readonly List<GCHandle> _handles = new();

        public IntPtr Pin(object value)
        {
            var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
            _handles.Add(handle);
            return handle.AddrOfPinnedObject();
        }

        public IntPtr String(string value) => Pin((value ?? string.Empty) + '\0');

        public void Dispose()
        {
            foreach (var handle in _handles)
            {
                handle.Free();
            }
        }
    }

    // webauthn.h (API version 1 layouts).
    [StructLayout(LayoutKind.Sequential)]
    private struct RpEntity { public uint dwVersion; public IntPtr pwszId; public IntPtr pwszName; public IntPtr pwszIcon; }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserEntity { public uint dwVersion; public uint cbId; public IntPtr pbId; public IntPtr pwszName; public IntPtr pwszIcon; public IntPtr pwszDisplayName; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CoseParameter { public uint dwVersion; public IntPtr pwszCredentialType; public int lAlg; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CoseParameters { public uint cCredentialParameters; public IntPtr pCredentialParameters; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ClientData { public uint dwVersion; public uint cbClientDataJSON; public IntPtr pbClientDataJSON; public IntPtr pwszHashAlgId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential { public uint dwVersion; public uint cbId; public IntPtr pbId; public IntPtr pwszCredentialType; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Credentials { public uint cCredentials; public IntPtr pCredentials; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CredentialEx { public uint dwVersion; public uint cbId; public IntPtr pbId; public IntPtr pwszCredentialType; public uint dwTransports; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CredentialList { public uint cCredentials; public IntPtr ppCredentials; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Extensions { public uint cExtensions; public IntPtr pExtensions; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GetAssertionOptions
    {
        public uint dwVersion;
        public uint dwTimeoutMilliseconds;
        public Credentials CredentialList;
        public Extensions Extensions;
        public uint dwAuthenticatorAttachment;
        public uint dwUserVerificationRequirement;
        public uint dwFlags;
        public IntPtr pwszU2fAppId;          // v2
        public IntPtr pbU2fAppId;            // v3
        public IntPtr pCancellationId;       // v4
        public IntPtr pAllowCredentialList;  // v4
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MakeCredentialOptions
    {
        public uint dwVersion;
        public uint dwTimeoutMilliseconds;
        public Credentials CredentialList;
        public Extensions Extensions;
        public uint dwAuthenticatorAttachment;
        public int bRequireResidentKey;
        public uint dwUserVerificationRequirement;
        public uint dwAttestationConveyancePreference;
        public uint dwFlags;
        public IntPtr pCancellationId;         // v2
        public IntPtr pExcludeCredentialList;  // v3
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Assertion
    {
        public uint dwVersion;
        public uint cbAuthenticatorData;
        public IntPtr pbAuthenticatorData;
        public uint cbSignature;
        public IntPtr pbSignature;
        public Credential Credential;
        public uint cbUserId;
        public IntPtr pbUserId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CredentialAttestation
    {
        public uint dwVersion;
        public IntPtr pwszFormatType;
        public uint cbAuthenticatorData;
        public IntPtr pbAuthenticatorData;
        public uint cbAttestation;
        public IntPtr pbAttestation;
        public uint dwAttestationDecodeType;
        public IntPtr pvAttestationDecode;
        public uint cbAttestationObject;
        public IntPtr pbAttestationObject;
        public uint cbCredentialId;
        public IntPtr pbCredentialId;
        public Extensions Extensions;   // v2
        public uint dwUsedTransport;    // v3
    }

    [DllImport(Dll)]
    private static extern uint WebAuthNGetApiVersionNumber();

    [DllImport(Dll)]
    private static extern int WebAuthNIsUserVerifyingPlatformAuthenticatorAvailable(out int isAvailable);

    [DllImport(Dll)]
    private static extern int WebAuthNAuthenticatorGetAssertion(
        IntPtr hWnd, IntPtr pwszRpId, ClientData* clientData, GetAssertionOptions* options, out IntPtr assertion);

    [DllImport(Dll)]
    private static extern int WebAuthNAuthenticatorMakeCredential(
        IntPtr hWnd, RpEntity* rp, UserEntity* user, CoseParameters* parameters, ClientData* clientData,
        MakeCredentialOptions* options, out IntPtr attestation);

    [DllImport(Dll)]
    private static extern void WebAuthNFreeAssertion(IntPtr assertion);

    [DllImport(Dll)]
    private static extern void WebAuthNFreeCredentialAttestation(IntPtr attestation);

    [DllImport(Dll)]
    private static extern IntPtr WebAuthNGetErrorName(int hr);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
