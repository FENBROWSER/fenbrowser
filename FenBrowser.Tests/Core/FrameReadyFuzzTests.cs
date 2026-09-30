using FenBrowser.Host.ProcessIsolation.Fuzz;
using Xunit;

namespace FenBrowser.Tests.Core;

/// <summary>
/// The frame-ready message carries the overdraw band's surface offset, which the host
/// uses to place the shared-memory surface on screen. Mutated messages must never
/// crash the renderer IPC endpoint or yield an offset outside the surface.
/// </summary>
public sealed class FrameReadyFuzzTests
{
    [Fact]
    public void MutatedFrameReadyMessages_NeverCrashOrEscapeTheSurface()
    {
        var endpoint = new RendererIpcFuzzEndpoint();
        var mutator = new StructuredMutator(seed: 20260930);
        var seed = IpcFuzzHarness.FrameReadySeed();

        Assert.True(endpoint.Fuzz(System.Text.Encoding.UTF8.GetBytes(seed)), "the valid seed was rejected");
        for (var i = 0; i < 10_000; i++)
        {
            var input = mutator.MutateJson(seed);
            Assert.True(endpoint.Fuzz(input), $"iteration {i}: {System.Convert.ToBase64String(input)}");
        }
    }
}
