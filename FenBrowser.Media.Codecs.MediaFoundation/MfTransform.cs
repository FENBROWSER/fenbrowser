using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using FenBrowser.Media.Pipeline;
using static FenBrowser.Media.Codecs.MediaFoundation.MfInterop;

namespace FenBrowser.Media.Codecs.MediaFoundation;

/// <summary>
/// One Media Foundation transform driven synchronously: input samples in, output samples
/// pulled until the transform asks for more input, with drain and flush. The output type
/// is chosen by the caller among the transform's offers and renegotiated on a stream
/// change. Every native object is released with the decoder; no pointer outlives a call.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class MfTransform : IDisposable
{
    private static readonly Lock s_startupGate = new();
    private static int s_startups;

    private readonly IMFTransform _transform;
    private IMFSample? _outputSample;
    private IMFMediaBuffer? _outputBuffer;
    private uint _outputBufferSize;
    private bool _providesSamples;
    private bool _streaming;
    private bool _disposed;

    private MfTransform(IMFTransform transform)
    {
        _transform = transform;
    }

    public IMFMediaType? OutputType { get; private set; }

    /// <summary>Starts Media Foundation for the process (counted) and creates the transform with <paramref name="clsid"/>.</summary>
    public static MfTransform Create(Guid clsid, bool lowLatency)
    {
        lock (s_startupGate)
        {
            if (s_startups == 0)
            {
                int hr = MFStartup(MfVersion, MfStartupFull);
                if (hr < 0)
                    throw new MediaDecoderException($"MFStartup failed: {Describe(hr)}");
            }

            s_startups++;
        }

        try
        {
            var iid = IidIMFTransform;
            int hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out object created);
            if (hr < 0)
                throw new MediaDecoderException($"The Media Foundation transform {clsid} could not be created: {Describe(hr)}");
            var transform = (IMFTransform)created;
            if (lowLatency && transform.GetAttributes(out var attributes) >= 0 && attributes is not null)
            {
                var key = MfLowLatency;
                _ = attributes.SetUINT32(ref key, 1);
                Marshal.ReleaseComObject(attributes);
            }

            return new MfTransform(transform);
        }
        catch
        {
            ReleaseStartup();
            throw;
        }
    }

    private static void ReleaseStartup()
    {
        lock (s_startupGate)
        {
            if (s_startups > 0 && --s_startups == 0)
                _ = MFShutdown();
        }
    }

    public IMFTransform Transform => _transform;

    /// <summary>
    /// Hands the transform the process's Direct3D 11 device so it decodes on the GPU and
    /// returns DXGI-backed samples. False, with the reason, when the transform is not
    /// D3D11-aware or refuses the manager; the caller then decodes in software.
    /// </summary>
    public bool TrySetD3DManager(IMFDXGIDeviceManager manager, out string reason)
    {
        ArgumentNullException.ThrowIfNull(manager);
        if (_transform.GetAttributes(out var attributes) < 0 || attributes is null)
        {
            reason = "the transform has no attributes";
            return false;
        }

        try
        {
            var key = MfSaD3D11Aware;
            if (attributes.GetUINT32(ref key, out uint aware) < 0 || aware == 0)
            {
                reason = "the transform is not D3D11-aware";
                return false;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(attributes);
        }

        IntPtr unknown = Marshal.GetIUnknownForObject(manager);
        try
        {
            int hr = _transform.ProcessMessage(MftMessageSetD3DManager, unchecked((UIntPtr)(ulong)(long)unknown));
            if (hr < 0)
            {
                reason = $"MFT_MESSAGE_SET_D3D_MANAGER failed: {Describe(hr)}";
                return false;
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }

        reason = string.Empty;
        return true;
    }

    public void SetInputType(IMFMediaType type)
    {
        Check(_transform.SetInputType(0, type, 0), "IMFTransform::SetInputType");
    }

    /// <summary>
    /// Picks the first offered output type whose subtype is in <paramref name="preferred"/>
    /// (in that order of preference) and sets it. Returns the subtype chosen.
    /// </summary>
    public Guid NegotiateOutputType(ReadOnlySpan<Guid> preferred)
    {
        IMFMediaType? best = null;
        int bestRank = int.MaxValue;
        for (uint index = 0; ; index++)
        {
            int hr = _transform.GetOutputAvailableType(0, index, out var candidate);
            if (hr == MfENoMoreTypes || candidate is null)
                break;
            if (hr < 0)
                throw new MediaDecoderException($"IMFTransform::GetOutputAvailableType failed: {Describe(hr)}");

            var key = MfMtSubtype;
            int rank = int.MaxValue;
            if (candidate.GetGUID(ref key, out var subtype) >= 0)
            {
                for (int i = 0; i < preferred.Length; i++)
                {
                    if (preferred[i] == subtype)
                    {
                        rank = i;
                        break;
                    }
                }
            }

            if (rank < bestRank)
            {
                if (best is not null)
                    Marshal.ReleaseComObject(best);
                best = candidate;
                bestRank = rank;
                if (rank == 0)
                    break;
            }
            else
            {
                Marshal.ReleaseComObject(candidate);
            }
        }

        if (best is null || bestRank == int.MaxValue)
        {
            if (best is not null)
                Marshal.ReleaseComObject(best);
            throw new MediaDecoderException("The transform offers no output type this engine can carry.");
        }

        Check(_transform.SetOutputType(0, best, 0), "IMFTransform::SetOutputType");
        if (OutputType is not null)
            Marshal.ReleaseComObject(OutputType);
        OutputType = best;
        ReleaseOutputSample();

        Check(_transform.GetOutputStreamInfo(0, out var info), "IMFTransform::GetOutputStreamInfo");
        _providesSamples = (info.Flags & (MftOutputStreamProvidesSamples | MftOutputStreamCanProvideSamples)) != 0;
        _outputBufferSize = Math.Max(info.Size, 1);
        var subtypeKey = MfMtSubtype;
        return best.GetGUID(ref subtypeKey, out var chosen) >= 0 ? chosen : Guid.Empty;
    }

    public void StartStreaming()
    {
        if (_streaming)
            return;
        Check(_transform.ProcessMessage(MftMessageNotifyBeginStreaming, UIntPtr.Zero), "MFT_MESSAGE_NOTIFY_BEGIN_STREAMING");
        Check(_transform.ProcessMessage(MftMessageNotifyStartOfStream, UIntPtr.Zero), "MFT_MESSAGE_NOTIFY_START_OF_STREAM");
        _streaming = true;
    }

    /// <summary>Hands one compressed access unit to the transform. Returns false when it is not accepting input until output is drained.</summary>
    public bool ProcessInput(ReadOnlySpan<byte> data, MediaTime pts, MediaTime duration)
    {
        Check(MFCreateSample(out var sample), "MFCreateSample");
        try
        {
            Check(MFCreateMemoryBuffer((uint)data.Length, out var buffer), "MFCreateMemoryBuffer");
            try
            {
                Check(buffer.Lock(out var pointer, out _, out _), "IMFMediaBuffer::Lock");
                try
                {
                    Marshal.Copy(data.ToArray(), 0, pointer, data.Length);
                }
                finally
                {
                    _ = buffer.Unlock();
                }

                Check(buffer.SetCurrentLength((uint)data.Length), "IMFMediaBuffer::SetCurrentLength");
                Check(sample.AddBuffer(buffer), "IMFSample::AddBuffer");
            }
            finally
            {
                Marshal.ReleaseComObject(buffer);
            }

            _ = sample.SetSampleTime(Math.Max(0, ToMfTime(pts)));
            if (duration > MediaTime.Zero)
                _ = sample.SetSampleDuration(ToMfTime(duration));

            int hr = _transform.ProcessInput(0, sample, 0);
            if (hr == unchecked((int)0xC00D36B5)) // MF_E_NOTACCEPTING
                return false;
            Check(hr, "IMFTransform::ProcessInput");
            return true;
        }
        finally
        {
            Marshal.ReleaseComObject(sample);
        }
    }

    public void Drain() => Check(_transform.ProcessMessage(MftMessageCommandDrain, UIntPtr.Zero), "MFT_MESSAGE_COMMAND_DRAIN");

    public void Flush()
    {
        Check(_transform.ProcessMessage(MftMessageCommandFlush, UIntPtr.Zero), "MFT_MESSAGE_COMMAND_FLUSH");
        _streaming = false;
    }

    /// <summary>What one ProcessOutput call produced.</summary>
    public enum OutputResult
    {
        Sample,
        NeedMoreInput,
        StreamChanged,
    }

    /// <summary>
    /// Pulls one output sample. On <see cref="OutputResult.Sample"/> the callback receives it
    /// (valid only during the call). A stream change is reported so the caller can
    /// renegotiate the output type and call again.
    /// </summary>
    public OutputResult ProcessOutput(Action<IMFSample> onSample)
    {
        var output = new MftOutputDataBuffer { StreamId = 0 };
        IMFSample? ours = null;
        if (!_providesSamples)
        {
            ours = EnsureOutputSample();
            output.Sample = Marshal.GetIUnknownForObject(ours);
        }

        try
        {
            int hr = _transform.ProcessOutput(0, 1, ref output, out _);
            if (hr == MfETransformNeedMoreInput)
                return OutputResult.NeedMoreInput;
            if (hr == MfETransformStreamChange || (output.Status & MftOutputDataBufferFormatChange) != 0)
                return OutputResult.StreamChanged;

            Check(hr, "IMFTransform::ProcessOutput");
            if (output.Sample == IntPtr.Zero)
                return OutputResult.NeedMoreInput;

            var sample = ours ?? (IMFSample)Marshal.GetObjectForIUnknown(output.Sample);
            try
            {
                onSample(sample);
            }
            finally
            {
                if (ours is null)
                    Marshal.ReleaseComObject(sample);
                if (ours is not null)
                    _ = _outputBuffer?.SetCurrentLength(0);
            }

            return OutputResult.Sample;
        }
        finally
        {
            if (output.Sample != IntPtr.Zero)
                Marshal.Release(output.Sample);
            if (output.Events != IntPtr.Zero)
                Marshal.Release(output.Events);
        }
    }

    private IMFSample EnsureOutputSample()
    {
        if (_outputSample is not null && _outputBuffer is not null)
            return _outputSample;
        ReleaseOutputSample();
        Check(MFCreateSample(out var sample), "MFCreateSample");
        Check(MFCreateMemoryBuffer(_outputBufferSize, out var buffer), "MFCreateMemoryBuffer");
        Check(sample.AddBuffer(buffer), "IMFSample::AddBuffer");
        _outputSample = sample;
        _outputBuffer = buffer;
        return sample;
    }

    private void ReleaseOutputSample()
    {
        if (_outputBuffer is not null)
        {
            Marshal.ReleaseComObject(_outputBuffer);
            _outputBuffer = null;
        }

        if (_outputSample is not null)
        {
            Marshal.ReleaseComObject(_outputSample);
            _outputSample = null;
        }
    }

    /// <summary>Copies a sample's contiguous payload into a pooled array; the caller returns it.</summary>
    public static byte[] ReadContiguous(IMFSample sample, out int length)
    {
        Check(sample.ConvertToContiguousBuffer(out var buffer), "IMFSample::ConvertToContiguousBuffer");
        try
        {
            Check(buffer.Lock(out var pointer, out _, out uint current), "IMFMediaBuffer::Lock");
            try
            {
                length = (int)current;
                var bytes = Buffers.MediaBufferPool.Bytes.Rent(Math.Max(length, 1));
                Marshal.Copy(pointer, bytes, 0, length);
                return bytes;
            }
            finally
            {
                _ = buffer.Unlock();
            }
        }
        finally
        {
            Marshal.ReleaseComObject(buffer);
        }
    }

    public static void Check(int hr, string call)
    {
        if (hr < 0)
            throw new MediaDecoderException($"{call} failed: {Describe(hr)}");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        ReleaseOutputSample();
        if (OutputType is not null)
        {
            Marshal.ReleaseComObject(OutputType);
            OutputType = null;
        }

        Marshal.ReleaseComObject(_transform);
        ReleaseStartup();
    }
}
