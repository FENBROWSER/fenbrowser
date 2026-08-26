namespace FenBrowser.FenEngine.Scripting
{
    /// <summary>
    /// JS-facing TextMetrics for canvas measureText. Only width is exposed;
    /// the remaining metrics are accepted as future surface area.
    /// </summary>
    public sealed class FenJsTextMetricsHost
    {
        public FenJsTextMetricsHost(double width)
        {
            Width = width;
        }

        public double Width { get; }
    }

    /// <summary>
    /// JS-facing ImageData for canvas getImageData/putImageData. Pixels are
    /// RGBA byte order per the canvas spec.
    /// </summary>
    public sealed class FenJsImageDataHost
    {
        public FenJsImageDataHost(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        public int Width { get; }

        public int Height { get; }

        public byte[] Pixels { get; }
    }
}
