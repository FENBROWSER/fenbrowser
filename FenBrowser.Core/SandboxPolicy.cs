using System;

namespace FenBrowser.Core
{
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
        All = Scripts | InlineScripts | ExternalScripts | Timers | Network | Storage | Navigation | DomMutation
    }

    public sealed class SandboxPolicy
    {
    public static SandboxPolicy AllowAll { get; } = new SandboxPolicy(SandboxFeature.All);
    public static SandboxPolicy NoScripts { get; } = new SandboxPolicy(SandboxFeature.All & ~(SandboxFeature.Scripts | SandboxFeature.InlineScripts | SandboxFeature.ExternalScripts | SandboxFeature.Timers));
    public static SandboxPolicy ReaderMode { get; } = new SandboxPolicy(SandboxFeature.All & ~(SandboxFeature.Scripts | SandboxFeature.InlineScripts | SandboxFeature.ExternalScripts | SandboxFeature.Timers | SandboxFeature.Network | SandboxFeature.Storage | SandboxFeature.Navigation));
    public static SandboxPolicy UntrustedContent { get; } = new SandboxPolicy(SandboxFeature.All & ~(SandboxFeature.Scripts | SandboxFeature.InlineScripts | SandboxFeature.ExternalScripts | SandboxFeature.Timers | SandboxFeature.Network | SandboxFeature.Storage | SandboxFeature.Navigation | SandboxFeature.DomMutation));

        public SandboxPolicy(SandboxFeature allowedFeatures)
        {
            AllowedFeatures = allowedFeatures;
        }

        public SandboxFeature AllowedFeatures { get; }

        public bool Allows(SandboxFeature feature)
        {
            return (AllowedFeatures & feature) == feature;
        }

        public static SandboxPolicy FromName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return AllowAll;
            var token = name.Trim();
            if (string.Equals(token, "allowall", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "all", StringComparison.OrdinalIgnoreCase))
                return AllowAll;
            if (string.Equals(token, "noscripts", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "no-scripts", StringComparison.OrdinalIgnoreCase))
                return NoScripts;
            if (string.Equals(token, "reader", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "reader-mode", StringComparison.OrdinalIgnoreCase))
                return ReaderMode;
            if (string.Equals(token, "untrusted", StringComparison.OrdinalIgnoreCase) || string.Equals(token, "untrusted-content", StringComparison.OrdinalIgnoreCase))
                return UntrustedContent;
            return AllowAll;
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
