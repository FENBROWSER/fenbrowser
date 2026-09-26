using System;

namespace FenBrowser.FenEngine.Media
{
    /// <summary>
    /// What the user agent lets start on its own (Autoplay Policy Detection §2.1
    /// <c>AutoplayPolicy</c>): everything, only inaudible playback, or nothing.
    /// </summary>
    public enum AutoplayPolicyMode
    {
        /// <summary>"allowed": any media element may start without user activation.</summary>
        Allowed,

        /// <summary>"allowed-muted": only muted (or silent) playback may start on its own.</summary>
        AllowedMuted,

        /// <summary>"disallowed": nothing starts without user activation.</summary>
        Disallowed,
    }

    /// <summary>
    /// The user agent's answer to HTML "allowed to play" for media elements (§4.8.11.6). The HTML
    /// spec leaves the policy to the user agent; this one is the same as Chromium's and Gecko's
    /// default: muted playback is always allowed and audible playback needs sticky user
    /// activation on the document, unless the mode says otherwise.
    /// </summary>
    /// <remarks>
    /// The mode comes from <c>FEN_MEDIA_AUTOPLAY</c> (<c>allowed</c>, <c>muted</c> or
    /// <c>disallowed</c>) and defaults to <see cref="AutoplayPolicyMode.AllowedMuted"/>. There is
    /// one policy per process; it is read on every decision so a test can change it.
    /// </remarks>
    public sealed class MediaAutoplayPolicy
    {
        public static readonly MediaAutoplayPolicy Default = new MediaAutoplayPolicy(ReadModeFromEnvironment());

        private volatile AutoplayPolicyMode _mode;

        public MediaAutoplayPolicy(AutoplayPolicyMode mode)
        {
            _mode = mode;
        }

        public AutoplayPolicyMode Mode
        {
            get => _mode;
            set => _mode = value;
        }

        /// <summary>
        /// Whether a media element is allowed to play. <paramref name="hasUserActivation"/> is the
        /// document's sticky activation (or a transient one in progress); <paramref name="inaudible"/>
        /// is true when the element is muted or its volume is zero.
        /// </summary>
        public bool IsAllowedToPlay(bool hasUserActivation, bool inaudible)
        {
            if (hasUserActivation)
                return true;

            return _mode switch
            {
                AutoplayPolicyMode.Allowed => true,
                AutoplayPolicyMode.AllowedMuted => inaudible,
                _ => false,
            };
        }

        /// <summary>
        /// The document-wide answer for <c>navigator.getAutoplayPolicy("mediaelement")</c>: with
        /// user activation everything is allowed, otherwise the configured mode.
        /// </summary>
        public AutoplayPolicyMode PolicyFor(bool hasUserActivation) =>
            hasUserActivation ? AutoplayPolicyMode.Allowed : _mode;

        /// <summary>The Autoplay Policy Detection enum value for a mode.</summary>
        public static string ToDomString(AutoplayPolicyMode mode) => mode switch
        {
            AutoplayPolicyMode.Allowed => "allowed",
            AutoplayPolicyMode.AllowedMuted => "allowed-muted",
            _ => "disallowed",
        };

        private static AutoplayPolicyMode ReadModeFromEnvironment()
        {
            var raw = Environment.GetEnvironmentVariable("FEN_MEDIA_AUTOPLAY");
            if (string.IsNullOrWhiteSpace(raw))
                return AutoplayPolicyMode.AllowedMuted;

            switch (raw.Trim().ToLowerInvariant())
            {
                case "allowed":
                case "all":
                    return AutoplayPolicyMode.Allowed;
                case "disallowed":
                case "blocked":
                case "none":
                    return AutoplayPolicyMode.Disallowed;
                default:
                    return AutoplayPolicyMode.AllowedMuted;
            }
        }
    }
}
