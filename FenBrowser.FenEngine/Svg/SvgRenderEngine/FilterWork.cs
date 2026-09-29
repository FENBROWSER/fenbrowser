using System;
using SkiaSharp;

namespace FenBrowser.FenEngine.Svg
{
    /// <summary>
    /// Pre-admission cost model for filter primitives (Filter Effects 1 §9).
    ///
    /// Filters run inside Skia when the recorded picture is rasterized, after the
    /// last cooperative deadline check, so the render deadline cannot stop them.
    /// Every primitive is therefore charged before its Skia filter is built: its
    /// filter region in device space (bounded by the device clip) times a
    /// per-pixel cost for its kind. The charge accumulates across the whole
    /// render, nested documents included, and exceeding
    /// <see cref="Adapters.SvgRenderLimits.MaxFilterWorkUnits"/> is a sandbox
    /// violation, so the document fails closed like any other budget.
    /// </summary>
    internal sealed partial class SvgRenderEngine
    {
        // Work is counted in morphology taps (about 1 ns each on the reference
        // machine). Calibrated on 4096x4096 regions: a radius-256 morphology
        // (1025 taps/px) took 14.6 s, 16-octave turbulence 2.7 s (~12 taps per
        // octave per pixel), specular lighting 2.3 s (~160 taps/px). Skia's blur
        // costs the same per pixel at any deviation.
        internal const long BaseFilterWorkPerPixel = 2;
        internal const long BlurWorkPerPixel = 4;
        internal const long DropShadowWorkPerPixel = 6;
        internal const long LightingWorkPerPixel = 160;
        internal const long TurbulenceWorkPerOctavePerPixel = 12;
        private const float MaxDeviceRadius = 8192f;

        private SKMatrix _filterDeviceMatrix = SKMatrix.Identity;
        private long _filterDevicePixelCap = long.MaxValue;

        /// <summary>Captures the device mapping that the next filter will run under.</summary>
        private void BeginFilterWorkAccounting(SKCanvas canvas)
        {
            _filterDeviceMatrix = canvas.TotalMatrix;
            SKRectI clip = canvas.DeviceClipBounds;
            _filterDevicePixelCap = clip.IsEmpty ? 0 : (long)clip.Width * clip.Height;
        }

        private void ChargeFilterPrimitiveWork(
            SvgElement primitive,
            SKRect filterRegion,
            float primitiveScaleX,
            float primitiveScaleY)
        {
            long pixels = DeviceFilterPixels(filterRegion);
            long perPixel = FilterWorkPerPixel(primitive, primitiveScaleX, primitiveScaleY);
            long units = pixels > 0 && perPixel > long.MaxValue / pixels ? long.MaxValue : pixels * perPixel;
            if (_resources.TryChargeFilterWork(units))
            {
                return;
            }

            long attempted = units > long.MaxValue - _resources.FilterWorkUnits
                ? long.MaxValue
                : _resources.FilterWorkUnits + units;
            throw new SvgSandboxViolationException(
                $"SVG filter work ({attempted}) exceeds limit ({_resources.MaxFilterWorkUnits})");
        }

        private long DeviceFilterPixels(SKRect filterRegion)
        {
            SKRect device = _filterDeviceMatrix.MapRect(filterRegion);
            double area = Math.Ceiling((double)device.Width) * Math.Ceiling((double)device.Height);
            if (double.IsNaN(area) || area < 0 || area > _filterDevicePixelCap)
            {
                return _filterDevicePixelCap;
            }
            return (long)area;
        }

        private long FilterWorkPerPixel(SvgElement primitive, float primitiveScaleX, float primitiveScaleY)
        {
            switch (primitive.Name)
            {
                case "feMorphology":
                    if (!TryReadScaledNumberPair(
                            primitive.GetAttribute("radius"), primitiveScaleX, primitiveScaleY, 256f,
                            out float rx, out float ry))
                    {
                        return BaseFilterWorkPerPixel;
                    }
                    DeviceScale(out float sx, out float sy);
                    return 2L * ((long)MathF.Ceiling(DeviceRadius(rx * sx)) +
                                 (long)MathF.Ceiling(DeviceRadius(ry * sy))) + 1;

                case "feConvolveMatrix":
                    return TryReadIntegerPair(primitive.GetAttribute("order"), 3, 25, out int ox, out int oy)
                        ? (long)ox * oy + 1
                        : BaseFilterWorkPerPixel;

                case "feTurbulence":
                    return TryReadBoundedInteger(primitive.GetAttribute("numOctaves"), 1, 16, out int octaves)
                        ? TurbulenceWorkPerOctavePerPixel * Math.Max(1, octaves)
                        : BaseFilterWorkPerPixel;

                case "feDiffuseLighting":
                case "feSpecularLighting":
                    return LightingWorkPerPixel;

                case "feGaussianBlur":
                    return BlurWorkPerPixel;

                case "feDropShadow":
                    return DropShadowWorkPerPixel;

                default:
                    return BaseFilterWorkPerPixel;
            }
        }

        private void DeviceScale(out float scaleX, out float scaleY)
        {
            SKMatrix m = _filterDeviceMatrix;
            scaleX = MathF.Sqrt(m.ScaleX * m.ScaleX + m.SkewY * m.SkewY);
            scaleY = MathF.Sqrt(m.SkewX * m.SkewX + m.ScaleY * m.ScaleY);
        }

        private static float DeviceRadius(float radius) =>
            float.IsFinite(radius) ? Math.Clamp(radius, 0f, MaxDeviceRadius) : MaxDeviceRadius;
    }
}
