using System.Numerics;
using System.Runtime.InteropServices;
using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Video;

/// <summary>
/// Converts decoded pictures to packed BGRA for the software compositor. Limited-range
/// (16..235) YUV is assumed, with the BT.601 matrix for standard definition and the
/// BT.709 matrix from 720 lines up, as the other engines do when the stream carries no
/// colour description. GPU conversion (design §5) replaces this on the hardware path.
/// </summary>
/// <remarks>
/// The chroma terms of each pixel are expanded once per pair of rows into three integer
/// rows, then each luma row is converted <see cref="Vector{T}"/>-wide: widen the Y bytes,
/// one multiply for the luma gain, three adds, three shifts, clamps, and the four channels
/// packed into one 32-bit lane that is stored as little-endian B, G, R, A. Fixed point at
/// 1 &lt;&lt; 16 keeps every intermediate within an int.
/// </remarks>
public static class PixelConverter
{
    // Fixed-point coefficients scaled by 1 << 16.
    private const int Scale = 16;
    private const int Half = 1 << (Scale - 1);
    private const int YGain = 76309;                 // 1.164
    private static readonly Matrix s_bt601 = new(104597, 25675, 53279, 132201);   // 1.596, 0.392, 0.813, 2.017
    private static readonly Matrix s_bt709 = new(117506, 13959, 34931, 138413);   // 1.793, 0.213, 0.533, 2.112

    private readonly record struct Matrix(int RV, int GU, int GV, int BU);

    [ThreadStatic]
    private static int[]? t_chromaRows;

    /// <summary>Which matrix a picture of <paramref name="height"/> lines gets without metadata.</summary>
    public static bool UsesBt709(int height) => height >= 720;

    /// <summary>
    /// Writes <paramref name="frame"/> as BGRA rows of <paramref name="stride"/> bytes into
    /// <paramref name="destination"/>, which must hold <c>stride * Height</c> bytes.
    /// </summary>
    public static void ToBgra(VideoFrame frame, Span<byte> destination, int stride)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (stride < frame.Width * 4)
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "The stride must hold a row of BGRA pixels.");
        if (destination.Length < stride * frame.Height)
            throw new ArgumentException("The destination is too small for the picture.", nameof(destination));

        switch (frame.Format)
        {
            case VideoPixelFormat.Bgra32:
                CopyBgra(frame, destination, stride);
                return;
            case VideoPixelFormat.I420:
            case VideoPixelFormat.Nv12:
                ConvertYuv420(frame, destination, stride, UsesBt709(frame.Height) ? s_bt709 : s_bt601);
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(frame), frame.Format, "Unsupported pixel format.");
        }
    }

    private static void CopyBgra(VideoFrame frame, Span<byte> destination, int stride)
    {
        var source = frame.GetPlane(0);
        int sourceStride = frame.GetStride(0);
        int rowBytes = frame.Width * 4;
        for (int y = 0; y < frame.Height; y++)
            source.Slice(y * sourceStride, rowBytes).CopyTo(destination.Slice(y * stride, rowBytes));
    }

    private static void ConvertYuv420(VideoFrame frame, Span<byte> destination, int stride, Matrix m)
    {
        var yPlane = frame.GetPlane(0);
        int yStride = frame.GetStride(0);
        bool nv12 = frame.Format == VideoPixelFormat.Nv12;
        var uPlane = frame.GetPlane(1);
        int uStride = frame.GetStride(1);
        var vPlane = nv12 ? uPlane : frame.GetPlane(2);
        int vStride = nv12 ? uStride : frame.GetStride(2);
        int uStep = nv12 ? 2 : 1;
        int vOffset = nv12 ? 1 : 0;

        int width = frame.Width;
        int height = frame.Height;

        // Three per-pixel chroma rows (RV·v, −(GU·u + GV·v), BU·u), padded to whole vectors.
        int lanes = Vector<int>.Count;
        int padded = (width + lanes - 1) / lanes * lanes;
        var rows = t_chromaRows;
        if (rows is null || rows.Length < padded * 3)
            t_chromaRows = rows = new int[padded * 3];
        var rv = rows.AsSpan(0, padded);
        var guv = rows.AsSpan(padded, padded);
        var bu = rows.AsSpan(padded * 2, padded);

        for (int y = 0; y < height; y += 2)
        {
            var uRow = uPlane.Slice((y >> 1) * uStride);
            var vRow = vPlane.Slice((y >> 1) * vStride);
            for (int x = 0; x < width; x++)
            {
                int chroma = x >> 1;
                int u = uRow[chroma * uStep] - 128;
                int v = vRow[chroma * uStep + vOffset] - 128;
                rv[x] = m.RV * v + Half;
                guv[x] = -(m.GU * u + m.GV * v) + Half;
                bu[x] = m.BU * u + Half;
            }

            for (int row = y; row < Math.Min(y + 2, height); row++)
            {
                var yRow = yPlane.Slice(row * yStride, width);
                var outRow = destination.Slice(row * stride, width * 4);
                ConvertRow(yRow, rv, guv, bu, outRow);
            }
        }
    }

    private static void ConvertRow(ReadOnlySpan<byte> luma, ReadOnlySpan<int> rv, ReadOnlySpan<int> guv, ReadOnlySpan<int> bu, Span<byte> outRow)
    {
        int width = luma.Length;
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int lanes = Vector<int>.Count;
            int bytesPerStep = Vector<byte>.Count;           // pixels handled per widen chain
            var gain = new Vector<int>(YGain);
            var black = new Vector<int>(16);
            var zero = Vector<int>.Zero;
            var max = new Vector<int>(255);
            var alpha = new Vector<int>(unchecked((int)0xFF000000));
            var pixels = MemoryMarshal.Cast<byte, int>(outRow);

            for (; x + bytesPerStep <= width; x += bytesPerStep)
            {
                var bytes = new Vector<byte>(luma.Slice(x, bytesPerStep));
                Vector.Widen(bytes, out Vector<ushort> low16, out Vector<ushort> high16);
                Vector.Widen(low16, out Vector<uint> w0, out Vector<uint> w1);
                Vector.Widen(high16, out Vector<uint> w2, out Vector<uint> w3);
                ConvertLanes(Vector.AsVectorInt32(w0), x, gain, black, zero, max, alpha, rv, guv, bu, pixels);
                ConvertLanes(Vector.AsVectorInt32(w1), x + lanes, gain, black, zero, max, alpha, rv, guv, bu, pixels);
                ConvertLanes(Vector.AsVectorInt32(w2), x + lanes * 2, gain, black, zero, max, alpha, rv, guv, bu, pixels);
                ConvertLanes(Vector.AsVectorInt32(w3), x + lanes * 3, gain, black, zero, max, alpha, rv, guv, bu, pixels);
            }
        }

        for (; x < width; x++)
        {
            int l = YGain * (luma[x] - 16);
            int at = x * 4;
            outRow[at] = Clamp((l + bu[x]) >> Scale);
            outRow[at + 1] = Clamp((l + guv[x]) >> Scale);
            outRow[at + 2] = Clamp((l + rv[x]) >> Scale);
            outRow[at + 3] = 255;
        }
    }

    private static void ConvertLanes(
        Vector<int> y, int x,
        Vector<int> gain, Vector<int> black, Vector<int> zero, Vector<int> max, Vector<int> alpha,
        ReadOnlySpan<int> rv, ReadOnlySpan<int> guv, ReadOnlySpan<int> bu, Span<int> pixels)
    {
        var luma = (y - black) * gain;
        var r = Vector.Min(Vector.Max(Vector.ShiftRightArithmetic(luma + new Vector<int>(rv.Slice(x)), Scale), zero), max);
        var g = Vector.Min(Vector.Max(Vector.ShiftRightArithmetic(luma + new Vector<int>(guv.Slice(x)), Scale), zero), max);
        var b = Vector.Min(Vector.Max(Vector.ShiftRightArithmetic(luma + new Vector<int>(bu.Slice(x)), Scale), zero), max);
        var packed = b | Vector.ShiftLeft(g, 8) | Vector.ShiftLeft(r, 16) | alpha;
        packed.CopyTo(pixels.Slice(x));
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
