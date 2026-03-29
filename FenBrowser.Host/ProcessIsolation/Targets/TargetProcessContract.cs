using System;

namespace FenBrowser.Host.ProcessIsolation.Targets
{
    /// <summary>
    /// Immutable startup contract for a brokered child target.
    /// This prevents capability/profile drift between host launchers and child loops.
    /// </summary>
    public sealed class TargetProcessContract
    {
        public TargetProcessContract(
            TargetProcessKind kind,
            string profileName,
            string capabilitySet,
            string launchArgument,
            string readyTimeoutEnvKey,
            string allowUnsandboxedEnvKey,
            string childEnvironmentFlag)
        {
            Kind = kind;
            ProfileName = profileName ?? throw new ArgumentNullException(nameof(profileName));
            CapabilitySet = capabilitySet ?? throw new ArgumentNullException(nameof(capabilitySet));
            LaunchArgument = launchArgument ?? throw new ArgumentNullException(nameof(launchArgument));
            ReadyTimeoutEnvKey = readyTimeoutEnvKey ?? throw new ArgumentNullException(nameof(readyTimeoutEnvKey));
            AllowUnsandboxedEnvKey = allowUnsandboxedEnvKey ?? throw new ArgumentNullException(nameof(allowUnsandboxedEnvKey));
            ChildEnvironmentFlag = childEnvironmentFlag ?? throw new ArgumentNullException(nameof(childEnvironmentFlag));
        }

        public TargetProcessKind Kind { get; }
        public string ProfileName { get; }
        public string CapabilitySet { get; }
        public string LaunchArgument { get; }
        public string ReadyTimeoutEnvKey { get; }
        public string AllowUnsandboxedEnvKey { get; }
        public string ChildEnvironmentFlag { get; }

        public bool Matches(string sandboxProfile, string capabilitySet)
        {
            return string.Equals(ProfileName, sandboxProfile, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(CapabilitySet, capabilitySet, StringComparison.OrdinalIgnoreCase);
        }
    }
}
