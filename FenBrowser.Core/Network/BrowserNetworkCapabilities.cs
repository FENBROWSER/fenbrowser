using System;
using SkiaSharp;

namespace FenBrowser.Core.Network;

public static class BrowserNetworkCapabilities
{
    private static readonly Lazy<bool> WebPDecoderAvailable = new(ProbeWebPDecoder);

    public static bool SupportsWebP => WebPDecoderAvailable.Value;
    public static bool SupportsAvif => false;

    public static string ImageAcceptHeader => SupportsWebP
        ? "image/webp,image/apng,image/svg+xml,image/png,image/jpeg,image/gif,image/*,*/*;q=0.8"
        : "image/apng,image/svg+xml,image/png,image/jpeg,image/gif,image/*,*/*;q=0.8";

    public static string DocumentAcceptHeader => SupportsWebP
        ? "text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/apng,*/*;q=0.8"
        : "text/html,application/xhtml+xml,application/xml;q=0.9,image/apng,*/*;q=0.8";

    public static string AcceptEncodingHeader => NetworkConfiguration.Instance.GetAcceptEncodingHeader();

    private static bool ProbeWebPDecoder()
    {
        try
        {
            using var bitmap = new SKBitmap(1, 1);
            bitmap.SetPixel(0, 0, SKColors.Black);
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(SKEncodedImageFormat.Webp, 80);
            if (encoded == null || encoded.Size == 0) return false;
            using var decoded = SKBitmap.Decode(encoded.ToArray());
            return decoded != null && decoded.Width == 1 && decoded.Height == 1;
        }
        catch
        {
            return false;
        }
    }
}
