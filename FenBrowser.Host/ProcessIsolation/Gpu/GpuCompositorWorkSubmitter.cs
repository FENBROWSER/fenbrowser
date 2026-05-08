using FenBrowser.Host.ProcessIsolation.Targets;

namespace FenBrowser.Host.ProcessIsolation.Gpu
{
    public readonly record struct GpuCompositorWorkItem(
        long FrameSequence,
        int LogicalWidth,
        int LogicalHeight,
        int PixelWidth,
        int PixelHeight,
        float DpiScale,
        double ComposeDurationMs,
        double TargetFrameIntervalMs);

    public interface ICompositorWorkSubmitter
    {
        bool TrySubmit(in GpuCompositorWorkItem workItem);
        long LastSubmittedFrameSequence { get; }
        long LastAcknowledgedFrameSequence { get; }
    }

    /// <summary>
    /// Broker-side bridge that forwards compositor telemetry work items
    /// to the dedicated GPU target process when available.
    /// </summary>
    public sealed class GpuCompositorWorkSubmitter : ICompositorWorkSubmitter
    {
        public bool TrySubmit(in GpuCompositorWorkItem workItem)
        {
            var session = ProcessIsolationRuntime.CurrentGpuSession;
            if (session == null)
            {
                return false;
            }

            return session.TrySubmitCompositorFrame(new TargetCompositorFramePayload
            {
                FrameSequence = workItem.FrameSequence,
                LogicalWidth = workItem.LogicalWidth,
                LogicalHeight = workItem.LogicalHeight,
                PixelWidth = workItem.PixelWidth,
                PixelHeight = workItem.PixelHeight,
                DpiScale = workItem.DpiScale,
                ComposeDurationMs = workItem.ComposeDurationMs,
                TargetFrameIntervalMs = workItem.TargetFrameIntervalMs
            });
        }

        public long LastSubmittedFrameSequence
            => ProcessIsolationRuntime.CurrentGpuSession?.LastSubmittedCompositorFrameSequence ?? 0;

        public long LastAcknowledgedFrameSequence
            => ProcessIsolationRuntime.CurrentGpuSession?.LastAckedCompositorFrameSequence ?? 0;
    }
}
