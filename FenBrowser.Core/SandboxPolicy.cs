using System;

namespace FenBrowser.Core
{
    /// <summary>
    /// Parsed allow-* tokens from an iframe sandbox attribute.
    /// These flags describe HTML browsing-context permissions; they are not interchangeable
    /// with the host's generic <see cref="SandboxFeature"/> capability set.
    /// </summary>
    [Flags]
    public enum IframeSandboxFlags
    {
        None = 0,
        Scripts = 1 << 0,
        SameOrigin = 1 << 1,
        Forms = 1 << 2,
        Popups = 1 << 3,
        TopNavigation = 1 << 4,
        PointerLock = 1 << 5,
        Modals = 1 << 6,
        Downloads = 1 << 7,
        Presentation = 1 << 8,
        PopupsToEscapeSandbox = 1 << 9,
        TopNavigationByUserActivation = 1 << 10,
        TopNavigationToCustomProtocols = 1 << 11
    }

    [Flags]
    public enum SandboxFeature
    {
        None = 0,
        Scripts = 1 << 0,
        InlineScripts = 1 << 1,
        ExternalScripts = 1 << 2,
        Timers = 1 << 3,
        Network = 1 << 4,
        Storage = 1 << 5,
        Navigation = 1 << 6,
        DomMutation = 1 << 7,
        SharedArrayBuffer = 1 << 8,
        CrossOriginIsolated = 1 << 9,
        DocumentDomain = 1 << 10,
        All = Scripts | InlineScripts | ExternalScripts | Timers | Network | Storage |
              Navigation | DomMutation | SharedArrayBuffer | CrossOriginIsolated | DocumentDomain
    }

    public sealed class SandboxPolicy
    {
        public static SandboxPolicy AllowAll { get; } =
            new SandboxPolicy(SandboxFeature.All);

        public static SandboxPolicy StandardPage { get; } =
            new SandboxPolicy(
                SandboxFeature.Scripts |
                SandboxFeature.InlineScripts |
                SandboxFeature.ExternalScripts |
                SandboxFeature.Timers |
                SandboxFeature.Network |
                SandboxFeature.Storage |
                SandboxFeature.Navigation |
                SandboxFeature.DomMutation |
                SandboxFeature.DocumentDomain);

        public static SandboxPolicy NoScripts { get; } =
            new SandboxPolicy(
                SandboxFeature.All &
                ~(SandboxFeature.Scripts |
                  SandboxFeature.InlineScripts |
                  SandboxFeature.ExternalScripts |
                  SandboxFeature.Timers));

        public static SandboxPolicy ReaderMode { get; } =
            new SandboxPolicy(
                SandboxFeature.All &
                ~(SandboxFeature.Scripts |
                  SandboxFeature.InlineScripts |
                  SandboxFeature.ExternalScripts |
                  SandboxFeature.Timers |
                  SandboxFeature.Network |
                  SandboxFeature.Storage |
                  SandboxFeature.Navigation));

        public static SandboxPolicy UntrustedContent { get; } =
            new SandboxPolicy(
                SandboxFeature.All &
                ~(SandboxFeature.Scripts |
                  SandboxFeature.InlineScripts |
                  SandboxFeature.ExternalScripts |
                  SandboxFeature.Timers |
                  SandboxFeature.Network |
                  SandboxFeature.Storage |
                  SandboxFeature.Navigation |
                  SandboxFeature.DomMutation));

        public SandboxPolicy(SandboxFeature allowedFeatures)
        {
            AllowedFeatures = allowedFeatures;
        }

        public SandboxFeature AllowedFeatures { get; }

        public bool Allows(SandboxFeature feature)
        {
            return (AllowedFeatures & feature) == feature;
        }

        public static bool HasIframeSandboxAttribute(string attributeValue)
        {
            return attributeValue != null;
        }

        public static IframeSandboxFlags ParseIframeSandboxFlags(string attributeValue)
        {
            if (attributeValue == null)
            {
                return IframeSandboxFlags.None;
            }

            var flags = IframeSandboxFlags.None;
            var tokens = attributeValue.Split(
                new[] { ' ', '\t', '\r', '\n', '\f' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawToken in tokens)
            {
                switch (rawToken.Trim().ToLowerInvariant())
                {
                    case "allow-scripts":
                        flags |= IframeSandboxFlags.Scripts;
                        break;
                    case "allow-same-origin":
                        flags |= IframeSandboxFlags.SameOrigin;
                        break;
                    case "allow-forms":
                        flags |= IframeSandboxFlags.Forms;
                        break;
                    case "allow-popups":
                        flags |= IframeSandboxFlags.Popups;
                        break;
                    case "allow-top-navigation":
                        flags |= IframeSandboxFlags.TopNavigation;
                        break;
                    case "allow-pointer-lock":
                        flags |= IframeSandboxFlags.PointerLock;
                        break;
                    case "allow-modals":
                        flags |= IframeSandboxFlags.Modals;
                        break;
                    case "allow-downloads":
                        flags |= IframeSandboxFlags.Downloads;
                        break;
                    case "allow-presentation":
                        flags |= IframeSandboxFlags.Presentation;
                        break;
                    case "allow-popups-to-escape-sandbox":
                        flags |= IframeSandboxFlags.PopupsToEscapeSandbox;
                        break;
                    case "allow-top-navigation-by-user-activation":
                        flags |= IframeSandboxFlags.TopNavigationByUserActivation;
                        break;
                    case "allow-top-navigation-to-custom-protocols":
                        flags |= IframeSandboxFlags.TopNavigationToCustomProtocols;
                        break;
                }
            }

            return flags;
        }

        /// <summary>
        /// Produces the conservative generic capability subset that can be derived from
        /// HTML sandbox tokens without changing unrelated browser behavior.
        ///
        /// The returned policy is intentionally NOT a complete implementation of HTML
        /// iframe sandboxing. Browsing-context code must retain the parsed
        /// <see cref="IframeSandboxFlags"/> and enforce forms, popups, top-level navigation,
        /// downloads, modals, pointer lock, presentation, and sandboxed-origin semantics
        /// at their owning algorithms.
        /// </summary>
        public static SandboxPolicy FromIframeSandboxAttribute(string attributeValue)
        {
            if (!HasIframeSandboxAttribute(attributeValue))
            {
                return AllowAll;
            }

            var flags = ParseIframeSandboxFlags(attributeValue);
            var allowed = SandboxFeature.All;

            // A sandbox without allow-scripts blocks script execution. It does not make
            // the document incapable of networking, DOM construction/mutation, or timers
            // owned by other browser algorithms, so do not conflate those capabilities.
            if ((flags & IframeSandboxFlags.Scripts) == 0)
            {
                allowed &= ~(SandboxFeature.Scripts |
                             SandboxFeature.InlineScripts |
                             SandboxFeature.ExternalScripts);
            }

            // Without allow-same-origin the framed document gets a sandboxed/opaque origin.
            // This generic capability policy cannot manufacture an opaque Origin object,
            // but it can conservatively withhold origin-sensitive capabilities. The actual
            // origin replacement still belongs to browsing-context/navigation code.
            if ((flags & IframeSandboxFlags.SameOrigin) == 0)
            {
                allowed &= ~(SandboxFeature.Storage |
                             SandboxFeature.DocumentDomain |
                             SandboxFeature.SharedArrayBuffer |
                             SandboxFeature.CrossOriginIsolated);
            }
            else
            {
                // A sandboxed document still cannot use document.domain to escape its
                // sandbox boundary, even when same-origin identity is retained.
                allowed &= ~SandboxFeature.DocumentDomain;
            }

            // Do not remove generic Navigation here. HTML sandboxing restricts specific
            // auxiliary/top-level navigation algorithms; it does not mean a framed
            // document is unable to navigate its own browsing context.
            return new SandboxPolicy(allowed);
        }

        /// <summary>
        /// Resolve a named host security profile. Unknown or missing names fail closed
        /// instead of silently becoming an allow-all policy.
        /// </summary>
        public static SandboxPolicy FromName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Sandbox policy name is required.", nameof(name));
            }

            var token = name.Trim();
            if (string.Equals(token, "allowall", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "all", StringComparison.OrdinalIgnoreCase))
            {
                return AllowAll;
            }

            if (string.Equals(token, "noscripts", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "no-scripts", StringComparison.OrdinalIgnoreCase))
            {
                return NoScripts;
            }

            if (string.Equals(token, "reader", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "reader-mode", StringComparison.OrdinalIgnoreCase))
            {
                return ReaderMode;
            }

            if (string.Equals(token, "untrusted", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "untrusted-content", StringComparison.OrdinalIgnoreCase))
            {
                return UntrustedContent;
            }

            throw new ArgumentException($"Unknown sandbox policy '{name}'.", nameof(name));
        }

        public SandboxPolicy WithFeature(SandboxFeature feature)
        {
            return new SandboxPolicy(AllowedFeatures | feature);
        }

        public SandboxPolicy WithoutFeature(SandboxFeature feature)
        {
            return new SandboxPolicy(AllowedFeatures & ~feature);
        }
    }
}
