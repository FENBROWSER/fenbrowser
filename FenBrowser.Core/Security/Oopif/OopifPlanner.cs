using System;
using System.Collections.Generic;
using System.Threading;
using FenBrowser.Core.Network;

namespace FenBrowser.Core.Security.Oopif
{
    // ── Out-of-Process iFrames (OOPIF) Planning Layer ─────────────────────────
    // Per the guide §F: OOPIF planning.
    //
    // OOPIF is the mechanism that isolates cross-site frames into separate renderer
    // processes (origin-locked). This module implements the decision logic for
    // when a frame must become out-of-process, and the proxy infrastructure to
    // represent remote frames in a local renderer's tree.
    //
    // Design:
    //  - OopifPolicy      — determines per-frame whether isolation is required
    //  - FrameProxy       — stub object in the local renderer representing a remote frame
    //  - FrameTree        — tracks the frame hierarchy and site assignments
    //  - OopifCommitResult — outcome of a navigation that may trigger OOPIF creation
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Site isolation mode governing OOPIF decisions.</summary>
    public enum SiteIsolationMode
    {
        /// <summary>No OOPIF; all frames run in the same renderer (insecure, development only).</summary>
        Disabled,
        /// <summary>Cross-site iframes go out-of-process. (Default secure mode.)</summary>
        CrossSiteIsolation,
        /// <summary>Each frame gets its own process (maximum isolation, high memory cost).</summary>
        PerFrameIsolation,
    }

    /// <summary>Describes the site lock that constrains a renderer process.</summary>
    public sealed class SiteLock
    {
        /// <summary>The scheme (e.g. "https").</summary>
        public string Scheme { get; }
        /// <summary>The registrable domain (eTLD+1), e.g. "example.com".</summary>
        public string RegistrableDomain { get; }
        /// <summary>Full origin string for strict origin isolation, e.g. "https://sub.example.com".</summary>
        public string Origin { get; }
        public bool IsStrict { get; }

        public SiteLock(string scheme, string registrableDomain, string origin, bool strict = false)
        {
            Scheme = scheme ?? throw new ArgumentNullException(nameof(scheme));
            RegistrableDomain = registrableDomain ?? throw new ArgumentNullException(nameof(registrableDomain));
            Origin = origin ?? throw new ArgumentNullException(nameof(origin));
            IsStrict = strict;
        }

        public bool Allows(string requestOrigin)
        {
            if (string.IsNullOrEmpty(requestOrigin)) return false;
            if (IsStrict) return requestOrigin == Origin;
            // eTLD+1 match
            var parsed = WhatwgUrl.Parse(requestOrigin);
            if (parsed == null) return false;
            return parsed.Scheme == Scheme &&
                   (parsed.Hostname == RegistrableDomain ||
                    parsed.Hostname.EndsWith("." + RegistrableDomain, StringComparison.OrdinalIgnoreCase));
        }

        public static SiteLock FromUrl(string url, bool strict = false)
        {
            var parsed = WhatwgUrl.Parse(url);
            if (parsed == null) throw new ArgumentException($"Cannot parse URL: {url}", nameof(url));
            var origin = parsed.ComputeOrigin();
            if (origin.Kind == UrlOriginKind.Opaque)
                throw new ArgumentException("Cannot create SiteLock for opaque origin URL.", nameof(url));
            var host = parsed.Hostname;
            var domain = GetEtldPlusOne(host) ?? host;
            return new SiteLock(parsed.Scheme, domain, origin.Serialize(), strict);
        }

        private static string GetEtldPlusOne(string host)
        {
            // Simplified eTLD+1 extraction (production would use a public suffix list).
            var parts = host.Split('.');
            if (parts.Length >= 2) return parts[parts.Length - 2] + "." + parts[parts.Length - 1];
            return host;
        }

        public override string ToString() => IsStrict ? $"SiteLock(strict:{Origin})" : $"SiteLock({Scheme}://{RegistrableDomain})";
    }

    /// <summary>Represents a frame node in the page's frame tree.</summary>
    public sealed class FrameNode
    {
        public Guid FrameId { get; } = Guid.NewGuid();
        public int RendererId { get; set; }     // which renderer process hosts this frame
        public string Url { get; set; }
        public string Origin { get; set; }
        public SiteLock SiteLock { get; set; }
        public bool IsMainFrame { get; set; }
        public bool IsOutOfProcess { get; set; }   // true = OOPIF
        public FrameProxy LocalProxy { get; set; } // proxy in the parent renderer (if OOPIF)
        public List<FrameNode> Children { get; } = new();
        public FrameNode Parent { get; set; }
        public string Sandbox { get; set; }        // CSP sandbox flags
    }

    /// <summary>
    /// Proxy for a remote frame in the local renderer's frame tree.
    /// The local renderer holds a FrameProxy when a child frame is out-of-process.
    /// The proxy receives hit-test results and scroll events from the compositor.
    /// </summary>
    public sealed class FrameProxy
    {
        public Guid RemoteFrameId { get; }
        public int RemoteRendererId { get; }
        public string RemoteOrigin { get; }
        public string HandoffToken { get; }
        public FrameProxyGeometry Geometry { get; set; }
        public RemoteFramePresentationState PresentationState { get; } = new();

        public FrameProxy(Guid remoteFrameId, int remoteRendererId, string remoteOrigin)
        {
            RemoteFrameId = remoteFrameId;
            RemoteRendererId = remoteRendererId;
            RemoteOrigin = remoteOrigin;
            HandoffToken = Guid.NewGuid().ToString("N");
        }
    }

    public sealed class RemoteFramePresentationState
    {
        public string LastCommittedUrl { get; set; }
        public string LastCommittedOrigin { get; set; }
        public uint LastFrameSequenceNumber { get; set; }
        public float SurfaceWidth { get; set; }
        public float SurfaceHeight { get; set; }
        public DateTimeOffset? LastCommittedAtUtc { get; set; }
    }

    public sealed class OopifHandoffTicket
    {
        public Guid FrameId { get; init; }
        public int RendererId { get; init; }
        public string Url { get; init; }
        public string Origin { get; init; }
        public SiteLock SiteLock { get; init; }
        public string HandoffToken { get; init; }
    }

    public sealed class FrameProxyGeometry
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Width { get; set; }
        public float Height { get; set; }
        public float DevicePixelRatio { get; set; } = 1f;
    }

    /// <summary>
    /// OOPIF policy engine: decides whether a given frame navigation requires
    /// a new out-of-process renderer.
    /// </summary>
    public sealed class OopifPolicy
    {
        private readonly SiteIsolationMode _mode;

        public OopifPolicy(SiteIsolationMode mode = SiteIsolationMode.CrossSiteIsolation)
        {
            _mode = mode;
        }

        /// <summary>
        /// Returns true if a navigation from <paramref name="currentFrame"/> to
        /// <paramref name="newUrl"/> requires creating a new out-of-process renderer.
        /// </summary>
        public OopifDecision ShouldIsolate(FrameNode currentFrame, string newUrl)
        {
            if (currentFrame == null) throw new ArgumentNullException(nameof(currentFrame));
            if (string.IsNullOrEmpty(newUrl))
                return OopifDecision.SameProcess("Empty URL");

            if (_mode == SiteIsolationMode.Disabled)
                return OopifDecision.SameProcess("OOPIF disabled");

            if (_mode == SiteIsolationMode.PerFrameIsolation && !currentFrame.IsMainFrame)
                return OopifDecision.NewProcess("Per-frame isolation mode");

            // Cross-site isolation: compare current renderer's site lock with new URL
            if (currentFrame.SiteLock == null)
                return OopifDecision.SameProcess("No site lock on current frame");

            try
            {
                var newLock = SiteLock.FromUrl(newUrl);
                bool sameSite = string.Equals(
                    currentFrame.SiteLock.RegistrableDomain,
                    newLock.RegistrableDomain,
                    StringComparison.OrdinalIgnoreCase) &&
                    currentFrame.SiteLock.Scheme == newLock.Scheme;

                if (sameSite)
                    return OopifDecision.SameProcess("Same site");

                return OopifDecision.NewProcess($"Cross-site: {currentFrame.SiteLock.RegistrableDomain} → {newLock.RegistrableDomain}");
            }
            catch
            {
                // Unparseable URL or opaque origin: use separate process for safety
                return OopifDecision.NewProcess("Unknown origin → isolated for safety");
            }
        }

        /// <summary>
        /// Compute site lock for a new renderer that will host <paramref name="url"/>.
        /// </summary>
        public SiteLock ComputeSiteLock(string url, bool strictOriginIsolation = false)
        {
            return SiteLock.FromUrl(url, strictOriginIsolation);
        }
    }

    public sealed class OopifDecision
    {
        public bool RequiresNewProcess { get; }
        public string Reason { get; }

        private OopifDecision(bool newProc, string reason)
        {
            RequiresNewProcess = newProc;
            Reason = reason;
        }

        public static OopifDecision NewProcess(string reason) => new(true, reason);
        public static OopifDecision SameProcess(string reason) => new(false, reason);
    }

    /// <summary>
    /// Maintains the frame tree for a browser tab.
    /// The broker owns this; renderers have read-only views.
    /// </summary>
    public sealed class FrameTree
    {
        private readonly Dictionary<Guid, FrameNode> _frames = new();
        private readonly OopifPolicy _policy;
        private FrameNode _mainFrame;
        private int _nextRendererId = 1;

        public FrameNode MainFrame => _mainFrame;
        public IReadOnlyDictionary<Guid, FrameNode> Frames => _frames;

        public FrameTree(OopifPolicy policy)
        {
            _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        }

        /// <summary>Initialize the main frame with a URL.</summary>
        public FrameNode CreateMainFrame(string url, int rendererId)
        {
            var frame = new FrameNode
            {
                Url = url,
                RendererId = rendererId,
                IsMainFrame = true,
                IsOutOfProcess = false,
            };

            try { frame.SiteLock = _policy.ComputeSiteLock(url); } catch { }

            _mainFrame = frame;
            _frames[frame.FrameId] = frame;
            return frame;
        }

        /// <summary>
        /// Register a child frame navigation. Returns the frame node and OOPIF decision.
        /// </summary>
        public (FrameNode frame, OopifDecision decision) NavigateChildFrame(
            FrameNode parent, string url)
        {
            if (parent == null) throw new ArgumentNullException(nameof(parent));

            var decision = _policy.ShouldIsolate(parent, url);

            int rendererId = decision.RequiresNewProcess
                ? Interlocked.Increment(ref _nextRendererId)
                : parent.RendererId;

            var frame = new FrameNode
            {
                Url = url,
                RendererId = rendererId,
                IsMainFrame = false,
                IsOutOfProcess = decision.RequiresNewProcess,
                Parent = parent,
            };

            try
            {
                frame.SiteLock = _policy.ComputeSiteLock(url);
                frame.Origin = frame.SiteLock?.Origin;
            }
            catch
            {
            }

            if (decision.RequiresNewProcess)
            {
                frame.LocalProxy = new FrameProxy(frame.FrameId, rendererId, frame.Origin ?? "");
                frame.LocalProxy.PresentationState.LastCommittedUrl = url;
                frame.LocalProxy.PresentationState.LastCommittedOrigin = frame.Origin ?? string.Empty;
                frame.LocalProxy.PresentationState.LastCommittedAtUtc = DateTimeOffset.UtcNow;
            }

            parent.Children.Add(frame);
            _frames[frame.FrameId] = frame;
            return (frame, decision);
        }

        public OopifHandoffTicket CreateHandoffTicket(Guid frameId)
        {
            if (!_frames.TryGetValue(frameId, out var frame) || !frame.IsOutOfProcess || frame.LocalProxy == null)
                return null;

            return new OopifHandoffTicket
            {
                FrameId = frame.FrameId,
                RendererId = frame.RendererId,
                Url = frame.Url,
                Origin = frame.Origin,
                SiteLock = frame.SiteLock,
                HandoffToken = frame.LocalProxy.HandoffToken
            };
        }

        public bool CommitRemoteFrame(Guid frameId, string committedUrl, uint frameSequenceNumber, float surfaceWidth, float surfaceHeight)
        {
            if (!_frames.TryGetValue(frameId, out var frame) || !frame.IsOutOfProcess || frame.LocalProxy == null)
                return false;

            frame.Url = committedUrl ?? frame.Url;
            try
            {
                frame.SiteLock = _policy.ComputeSiteLock(frame.Url);
                frame.Origin = frame.SiteLock?.Origin;
            }
            catch
            {
            }

            frame.LocalProxy.PresentationState.LastCommittedUrl = frame.Url;
            frame.LocalProxy.PresentationState.LastCommittedOrigin = frame.Origin ?? string.Empty;
            frame.LocalProxy.PresentationState.LastFrameSequenceNumber = frameSequenceNumber;
            frame.LocalProxy.PresentationState.SurfaceWidth = surfaceWidth;
            frame.LocalProxy.PresentationState.SurfaceHeight = surfaceHeight;
            frame.LocalProxy.PresentationState.LastCommittedAtUtc = DateTimeOffset.UtcNow;
            return true;
        }

        public void RemoveFrame(Guid frameId)
        {
            if (_frames.TryGetValue(frameId, out var frame))
            {
                frame.Parent?.Children.Remove(frame);
                _frames.Remove(frameId);
            }
        }
    }
}
