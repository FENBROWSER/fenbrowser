using FenBrowser.Media.Buffers;

namespace FenBrowser.Media.Video;

/// <summary>
/// Converts decoded pictures to packed BGRA for the software compositor. Limited-range
/// (16..235) YUV is assumed, with the BT.601 matrix for standard definition and the
/// BT.709 matrix from 720 lines up, as the other engines do when the stream carries no
/// colour description. GPU conversion (design §5) replaces this on the hardware path.
/// </summary>
public static class PixelConverter
{
    // Fixed-point coefficients scaled by 1 << 16.
    private const int Scale = 16;
    private const int YGain = 76309;                 // 1.164
    private static readonly Matrix s_bt601 = new(104597, 25675, 53279, 132201);   // 1.596, 0.392, 0.813, 2.017
    private static readonly Matrix s_bt709 = new(117506, 13959, 34931, 138413);   // 1.793, 0.213, 0.533, 2.112

    private readonly record struct Matrix(int RV, int GU, int GV, int BU);

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
        for (int y = 0; y < height; y++)
        {
            var yRow = yPlane.Slice(y * yStride, width);
            var uRow = uPlane.Slice((y >> 1) * uStride);
            var vRow = vPlane.Slice((y >> 1) * vStride);
            var outRow = destination.Slice(y * stride, width * 4);
            for (int x = 0; x < width; x++)
            {
                int chroma = x >> 1;
                int luma = YGain * (yRow[x] - 16);
                int u = uRow[chroma * uStep] - 128;
                int v = vRow[chroma * uStep + vOffset] - 128;
                int r = (luma + m.RV * v + (1 << (Scale - 1))) >> Scale;
                int g = (luma - m.GU * u - m.GV * v + (1 << (Scale - 1))) >> Scale;
                int b = (luma + m.BU * u + (1 << (Scale - 1))) >> Scale;
                int at = x * 4;
                outRow[at] = Clamp(b);
                outRow[at + 1] = Clamp(g);
                outRow[at + 2] = Clamp(r);
                outRow[at + 3] = 255;
            }
        }
    }

    private static byte Clamp(int value) => (byte)Math.Clamp(value, 0, 255);
}
