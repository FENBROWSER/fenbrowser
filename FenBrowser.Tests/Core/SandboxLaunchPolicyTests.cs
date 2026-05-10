using System;
using System.Diagnostics;
using FenBrowser.Core.Security.Sandbox;
using Xunit;

namespace FenBrowser.Tests.Core
{
    public class SandboxLaunchPolicyTests
    {
        [Fact]
        public void TryAcquire_WithMissingFactoryAndNoFallback_DeniesLaunch()
        {
            var allowed = SandboxLaunchPolicy.TryAcquire(
                "renderer test",
                sandboxFactory: null,
                profile: OsSandboxProfile.RendererMinimal,
                allowUnsandboxedFallback: false,
                overrideEnvKey: "FEN_RENDERER_ALLOW_UNSANDBOXED",
                out var sandbox);

            Assert.False(allowed);
            Assert.Null(sandbox);
        }

        [Fact]
        public void TryAcquire_WithMissingFactoryAndFallback_AllowsUnsandboxedLaunch()
        {
            var allowed = SandboxLaunchPolicy.TryAcquire(
                "renderer test",
                sandboxFactory: null,
                profile: OsSandboxProfile.RendererMinimal,
                allowUnsandboxedFallback: true,
                overrideEnvKey: "FEN_RENDERER_ALLOW_UNSANDBOXED",
                out var sandbox);

            Assert.True(allowed);
            Assert.Null(sandbox);
        }

        [Fact]
        public void TryAcquire_WithRealSandboxFactory_ReturnsSandbox()
        {
            var factory = new TestSandboxFactory(new TestSandbox("TestSandbox", OsSandboxCapabilities.RendererMinimal));

            var allowed = SandboxLaunchPolicy.TryAcquire(
                "renderer test",
                sandboxFactory: factory,
                profile: OsSandboxProfile.RendererMinimal,
                allowUnsandboxedFallback: false,
                overrideEnvKey: "FEN_RENDERER_ALLOW_UNSANDBOXED",
                out var sandbox);

            Assert.True(allowed);
            Assert.NotNull(sandbox);
            Assert.Equal("TestSandbox", sandbox.ProfileName);
            sandbox.Dispose();
        }

        private sealed class TestSandboxFactory : IOsSandboxFactory
        {
            private readonly ISandbox _sandbox;

            public TestSandboxFactory(ISandbox sandbox)
            {
                _sandbox = sandbox ?? throw new ArgumentNullException(nameof(sandbox));
            }

            public bool IsSandboxingSupported => true;

            public ISandbox Create(OsSandboxProfile profile)
            {
                return _sandbox;
            }
        }

        private sealed class TestSandbox : ISandbox
        {
            public TestSandbox(string profileName, OsSandboxCapabilities capabilities)
            {
                ProfileName = profileName;
                Capabilities = capabilities;
            }

            public string ProfileName { get; }
            public OsSandboxCapabilities Capabilities { get; }
            public bool IsActive => true;
            public bool RequiresCustomSpawn => false;

            public void ApplyToProcessStartInfo(ProcessStartInfo psi) { }
            public void AttachToProcess(Process process) { }
            public void Kill() { }
            public SandboxHealthStatus GetHealth() => new() { IsHealthy = true };
            public Process SpawnProcess(ProcessStartInfo psi) => throw new NotSupportedException();
            public void Dispose() { }
        }
    }
}
