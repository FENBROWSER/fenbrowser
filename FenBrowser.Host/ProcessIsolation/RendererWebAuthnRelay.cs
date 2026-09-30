using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.WebAPIs.WebAuthn;

namespace FenBrowser.Host.ProcessIsolation
{
    /// <summary>A Web Authentication ceremony a renderer asks the broker to run.</summary>
    public sealed class RendererWebAuthnRequestPayload
    {
        /// <summary>"get", "create" or "uvpaa" (isUserVerifyingPlatformAuthenticatorAvailable).</summary>
        public string Kind { get; set; }
        public WebAuthnGetRequest Get { get; set; }
        public WebAuthnCreateRequest Create { get; set; }
    }

    public sealed class RendererWebAuthnResultPayload
    {
        public bool Available { get; set; }
        public WebAuthnResult Result { get; set; }
    }

    /// <summary>
    /// Broker end of Web Authentication for brokered tabs. The renderer is sandboxed and
    /// holds no window, so it cannot reach webauthn.dll; it sends the ceremony here and the
    /// broker runs it on its own authenticator (Windows Hello) against its window.
    /// <para>
    /// The broker does not take the renderer's word for the origin blindly: the claimed
    /// origin must be the origin of the document this tab last committed (the URL the
    /// broker navigated it to, or the one the renderer reported committing), the RP ID must
    /// be that host or a registrable parent of it, and the client data is built here. The
    /// platform dialog itself names the relying party to the user, who has to confirm
    /// every ceremony. One ceremony per tab runs at a time.
    /// </para>
    /// </summary>
    internal sealed class RendererWebAuthnRelay
    {
        private readonly int _tabId;
        private readonly Action<RendererIpcEnvelope> _send;
        private readonly Func<string> _committedUrl;
        private int _busy;

        public RendererWebAuthnRelay(int tabId, Action<RendererIpcEnvelope> send, Func<string> committedUrl)
        {
            _tabId = tabId;
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _committedUrl = committedUrl ?? throw new ArgumentNullException(nameof(committedUrl));
        }

        public void Handle(RendererIpcEnvelope envelope)
        {
            var request = RendererIpc.DeserializePayload<RendererWebAuthnRequestPayload>(envelope);
            string correlationId = envelope.CorrelationId;
            _ = Task.Run(async () =>
            {
                RendererWebAuthnResultPayload reply;
                try
                {
                    reply = await RunAsync(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                        $"[WebAuthn] Ceremony for tab {_tabId} failed: {ex.GetType().Name}: {ex.Message}");
                    reply = new RendererWebAuthnResultPayload { Result = WebAuthnResult.NotAllowed() };
                }

                _send(new RendererIpcEnvelope
                {
                    Type = RendererIpcMessageType.WebAuthnResult.ToString(),
                    TabId = _tabId,
                    CorrelationId = correlationId,
                    Payload = RendererIpc.SerializePayload(reply),
                });
            });
        }

        internal async Task<RendererWebAuthnResultPayload> RunAsync(RendererWebAuthnRequestPayload request)
        {
            var authenticator = WebAuthnPlatform.Authenticator;
            if (request == null || authenticator == null)
            {
                return new RendererWebAuthnResultPayload { Result = WebAuthnResult.NotAllowed() };
            }

            if (string.Equals(request.Kind, "uvpaa", StringComparison.Ordinal))
            {
                return new RendererWebAuthnResultPayload
                {
                    Available = await authenticator.IsUserVerifyingPlatformAuthenticatorAvailableAsync().ConfigureAwait(false),
                };
            }

            string claimedOrigin = request.Kind == "create" ? request.Create?.Origin : request.Get?.Origin;
            if (!IsCommittedOrigin(claimedOrigin))
            {
                EngineLog.Write(LogSubsystem.ProcessIsolation, LogSeverity.Warn,
                    $"[WebAuthn] Rejected a ceremony for tab {_tabId}: the claimed origin is not the tab's committed origin.");
                return new RendererWebAuthnResultPayload { Result = WebAuthnResult.NotAllowed() };
            }

            if (Interlocked.Exchange(ref _busy, 1) != 0)
            {
                return new RendererWebAuthnResultPayload { Result = WebAuthnResult.Failure("NotAllowedError", "Another request is already in progress.") };
            }

            try
            {
                var result = request.Kind == "create"
                    ? await authenticator.MakeCredentialAsync(request.Create, CancellationToken.None).ConfigureAwait(false)
                    : await authenticator.GetAssertionAsync(request.Get, CancellationToken.None).ConfigureAwait(false);
                return new RendererWebAuthnResultPayload { Result = result ?? WebAuthnResult.NotAllowed() };
            }
            finally
            {
                Volatile.Write(ref _busy, 0);
            }
        }

        private bool IsCommittedOrigin(string claimedOrigin)
        {
            string committed = _committedUrl();
            if (string.IsNullOrEmpty(claimedOrigin) || string.IsNullOrEmpty(committed))
            {
                return false;
            }

            return string.Equals(
                WebAuthnClient.SerializeOrigin(claimedOrigin),
                WebAuthnClient.SerializeOrigin(committed),
                StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Renderer end: the <see cref="IWebAuthnAuthenticator"/> installed inside a renderer
    /// child, forwarding each ceremony to the broker and waiting for its answer (a user
    /// may take minutes in the Windows Hello dialog).
    /// </summary>
    internal sealed class RendererWebAuthnClient : IWebAuthnAuthenticator
    {
        private static readonly TimeSpan CeremonyWait = TimeSpan.FromMinutes(11);
        private static readonly TimeSpan QueryWait = TimeSpan.FromSeconds(10);

        private readonly int _tabId;
        private readonly Action<RendererIpcEnvelope> _send;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<RendererWebAuthnResultPayload>> _pending = new(StringComparer.Ordinal);

        public RendererWebAuthnClient(int tabId, Action<RendererIpcEnvelope> send)
        {
            _tabId = tabId;
            _send = send ?? throw new ArgumentNullException(nameof(send));
        }

        public async Task<bool> IsUserVerifyingPlatformAuthenticatorAvailableAsync()
        {
            var reply = await RoundTripAsync(new RendererWebAuthnRequestPayload { Kind = "uvpaa" }, QueryWait).ConfigureAwait(false);
            return reply?.Available == true;
        }

        public async Task<WebAuthnResult> GetAssertionAsync(WebAuthnGetRequest request, CancellationToken cancellationToken)
        {
            var reply = await RoundTripAsync(new RendererWebAuthnRequestPayload { Kind = "get", Get = request }, CeremonyWait).ConfigureAwait(false);
            return reply?.Result ?? WebAuthnResult.NotAllowed();
        }

        public async Task<WebAuthnResult> MakeCredentialAsync(WebAuthnCreateRequest request, CancellationToken cancellationToken)
        {
            var reply = await RoundTripAsync(new RendererWebAuthnRequestPayload { Kind = "create", Create = request }, CeremonyWait).ConfigureAwait(false);
            return reply?.Result ?? WebAuthnResult.NotAllowed();
        }

        public void OnResult(RendererIpcEnvelope envelope)
        {
            if (envelope?.CorrelationId != null && _pending.TryRemove(envelope.CorrelationId, out var pending))
            {
                pending.TrySetResult(RendererIpc.DeserializePayload<RendererWebAuthnResultPayload>(envelope));
            }
        }

        private async Task<RendererWebAuthnResultPayload> RoundTripAsync(RendererWebAuthnRequestPayload request, TimeSpan wait)
        {
            string correlationId = Guid.NewGuid().ToString("N");
            var pending = new TaskCompletionSource<RendererWebAuthnResultPayload>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[correlationId] = pending;
            try
            {
                _send(new RendererIpcEnvelope
                {
                    Type = RendererIpcMessageType.WebAuthn.ToString(),
                    TabId = _tabId,
                    CorrelationId = correlationId,
                    Payload = RendererIpc.SerializePayload(request),
                });
                return await pending.Task.WaitAsync(wait).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return null;
            }
            finally
            {
                _pending.TryRemove(correlationId, out _);
            }
        }
    }
}
