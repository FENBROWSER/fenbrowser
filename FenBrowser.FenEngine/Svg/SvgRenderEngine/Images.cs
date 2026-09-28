using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using SkiaSharp;
using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg
{
    internal sealed partial class SvgRenderEngine
    {
        // --------------------------------------------------------------- images

        /// <summary>
        /// Renders raster images embedded as data: URIs. SECURITY: this is the
        /// ONLY image source - there is no fetch code path here, so external
        /// hrefs are logged and ignored (fail closed). Decoded pixel counts are
        /// bounded by MaxDecodedImagePixels before drawing and charged against the
        /// cumulative decoded budget that also covers every other image in the
        /// render, nested documents included.
        /// </summary>
        private void DrawImageElement(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle style)
        {
            var href = el.GetAttribute("href") ?? el.GetLookup("xlink:href");
            if (string.IsNullOrEmpty(href))
            {
                return; // No source: renders nothing (spec).
            }
            if (!href.StartsWith("data:", System.StringComparison.OrdinalIgnoreCase))
            {
                SplitReferenceFragment(href, out string target, out string referenceFragment);
                if (target.Length == 0)
                {
                    return;
                }
                DrawResolvedImage(el, canvas, viewport, style, target, referenceFragment);
                return;
            }
            if (IsSvgDataUri(href))
            {
                SplitReferenceFragment(href, out string payload, out string svgFragment);
                if (payload.Length == 0)
                {
                    return;
                }
                DrawEmbeddedSvgImage(el, canvas, viewport, style, payload, svgFragment);
                return;
            }

            CheckDeadline();
            var bytes = DecodeDataUriBytes(href, _resources.MaxDecodedImageBytes, out var decodeError);
            if (bytes == null)
            {
                _report.RejectResource(decodeError);
                return;
            }
            if (!_resources.TryAdmit(bytes.Length))
            {
                _report.RejectResource("embedded raster image cumulative resource budget exceeded");
                return;
            }

            if (!TryBorrowOrDecodeImage(bytes, _resources, out var image, out var imageError))
            {
                _report.RejectResource(imageError);
                return;
            }

            DrawBorrowedImage(el, canvas, viewport, style, image);
        }

        private static void SplitReferenceFragment(string reference, out string target, out string fragment)
        {
            int hash = reference.IndexOf('#');
            if (hash < 0)
            {
                target = reference;
                fragment = null;
                return;
            }
            target = reference.Substring(0, hash);
            fragment = reference.Substring(hash + 1);
            if (fragment.Length == 0) fragment = null;
        }

        private void DrawEmbeddedSvgImage(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle style,
            string href,
            string fragment)
        {
            CheckDeadline();
            byte[] bytes = DecodeSvgDataUriBytes(href, _resources.MaxDecodedImageBytes, out string decodeError);
            if (bytes == null)
            {
                _report.RejectResource(decodeError);
                return;
            }

            if (!_resources.TryAdmit(bytes.Length))
            {
                _report.RejectResource("embedded SVG cumulative byte budget exceeded");
                return;
            }

            DrawSvgImageBytes(element, canvas, viewport, style, bytes, _baseUri, fragment);
        }

        private void DrawSvgImageBytes(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle style,
            byte[] bytes,
            Uri resourceUri,
            string fragment)
        {
            int maxDepth = Math.Min(16, _limits.MaxReferenceDepth);
            if (_resourceDepth >= maxDepth)
            {
                _report.RejectResource("embedded SVG resource depth budget exceeded");
                return;
            }

            string source;
            try
            {
                source = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                _report.RejectResource("embedded SVG is not valid UTF-8");
                return;
            }

            ReferencedSvgRender referenced =
                RenderReferencedSvg(source, fragment, element, viewport, resourceUri);
            if (referenced.ViewFragmentRejected)
            {
                return;
            }
            if (referenced.Picture == null)
            {
                _report.RejectResource(
                    "embedded SVG failed bounded first-party rendering: " +
                    (string.IsNullOrWhiteSpace(referenced.Error) ? "unknown nested failure" : referenced.Error));
                return;
            }

            using (referenced.Picture)
            {
                if (referenced.RequiresFallback || referenced.ResourceRejected)
                {
                    _report.RejectResource(
                        referenced.RequiresFallback
                            ? "embedded SVG requires unsupported compatibility rendering"
                            : "embedded SVG contained a rejected nested resource");
                    return;
                }

                if (referenced.Width <= 0f || referenced.Height <= 0f ||
                    !TryResolveImageBox(
                        element, viewport, referenced.Width, referenced.Height, out var imageViewport))
                    return;

                if (!style.Visibility) return;
                bool layered = TryBeginGroupOpacity(element, canvas, out var layerPaint);
                try
                {
                    using var state = new CanvasState(canvas);
                    string preserveAspectRatio = element.GetAttribute("preserveAspectRatio");
                    if (string.IsNullOrWhiteSpace(preserveAspectRatio))
                        preserveAspectRatio = referenced.RootPreserveAspectRatio;
                    var destination = ResolveImageDestination(
                        imageViewport, referenced.Width, referenced.Height,
                        preserveAspectRatio);
                    if (destination.IsEmpty) return;
                    canvas.ClipRect(imageViewport);
                    canvas.SetMatrix(PlaceImageContent(
                        canvas.TotalMatrix, destination, referenced.Width, referenced.Height));
                    canvas.DrawPicture(referenced.Picture);
                }
                finally
                {
                    if (layered)
                    {
                        canvas.Restore();
                        _activeLayers--;
                        layerPaint.Dispose();
                    }
                }
            }
        }

        private sealed class ReferencedSvgRender
        {
            public SKPicture Picture;
            public float Width;
            public float Height;
            public string RootPreserveAspectRatio;
            public string Error;
            public bool RequiresFallback;
            public bool ResourceRejected;
            public bool ViewFragmentRejected;
        }

        private ReferencedSvgRender RenderReferencedSvg(
            string source,
            string fragment,
            SvgElement element,
            ViewportContext viewport,
            Uri resourceUri)
        {
            var result = new ReferencedSvgRender();
            if (!SvgMarkupParser.TryParse(source, _limits, out SvgParsedDocument doc, out string error))
            {
                result.Error = error;
                return result;
            }

            if (!EstablishReferencedViewport(doc, fragment, element, viewport))
            {
                result.ViewFragmentRejected = true;
                return result;
            }
            result.RootPreserveAspectRatio = doc.Root.GetAttribute("preserveAspectRatio");

            var nested = new SvgRenderEngine(
                doc, _limits, _resources, _resourceDepth + 1,
                resourceUri, _resourceResolver, _documentTimeSeconds);
            SKPicture picture = null;
            float width = 0f;
            float height = 0f;
            try
            {
                nested.ApplySmilSnapshot(doc.Root);
                nested.RenderRoot(out picture, out width, out height);
            }
            catch (SvgTimeBudgetExceededException)
            {
                picture = null;
                result.Error = $"SVG render exceeded time limit ({_limits.MaxRenderTimeMs}ms)";
            }
            catch (SvgSandboxViolationException ex)
            {
                picture = null;
                result.Error = ex.Message;
            }
            result.Picture = picture;
            result.Width = width;
            result.Height = height;
            result.RequiresFallback = nested._report.UnsupportedFeatureIgnored;
            result.ResourceRejected = nested._report.ResourceRejected;
            return result;
        }

        private bool EstablishReferencedViewport(
            SvgParsedDocument doc,
            string fragment,
            SvgElement element,
            ViewportContext viewport)
        {
            var root = doc.Root;
            if (!string.IsNullOrEmpty(fragment))
            {
                if (!doc.ElementsById.TryGetValue(fragment, out SvgElement view) ||
                    view.Name != "view")
                {
                    _report.RequireFallback(
                        "referenced SVG view fragment is unresolved or invalid");
                    return false;
                }
                string viewBox = view.GetAttribute("viewBox");
                if (!TryParseViewBox(
                        viewBox, out _, out _, out _, out _, out bool viewBoxDisablesRendering))
                {
                    if (!viewBoxDisablesRendering)
                    {
                        return true;
                    }
                    _report.RequireFallback(
                        "referenced SVG view fragment is unresolved or invalid");
                    return false;
                }
                SetReferencedRootAttribute(root, "viewBox", viewBox);
                string viewPreserveAspectRatio = view.GetAttribute("preserveAspectRatio");
                if (!string.IsNullOrWhiteSpace(viewPreserveAspectRatio))
                    SetReferencedRootAttribute(root, "preserveAspectRatio", viewPreserveAspectRatio);
                return true;
            }

            if (!string.IsNullOrWhiteSpace(root.GetAttribute("viewBox")) ||
                !string.IsNullOrWhiteSpace(root.GetAttribute("width")) ||
                !string.IsNullOrWhiteSpace(root.GetAttribute("height")) ||
                !TryResolveImageBox(element, viewport, 0f, 0f, out SKRect imageViewport))
                return true;
            SetReferencedRootSize(root, imageViewport.Width, imageViewport.Height);
            return true;
        }

        private static void SetReferencedRootSize(SvgElement root, float width, float height)
        {
            SetReferencedRootAttribute(root, "width", FormatReferencedRootLength(width));
            SetReferencedRootAttribute(root, "height", FormatReferencedRootLength(height));
        }

        private static string FormatReferencedRootLength(float value) =>
            NormalizeGeometryValue(SvgValues.ClampCoord(value))
                .ToString("R", System.Globalization.CultureInfo.InvariantCulture);

        private static void SetReferencedRootAttribute(SvgElement root, string name, string value)
        {
            var attributes = root.Attributes;
            if (attributes != null)
            {
                for (int i = 0; i < attributes.Length; i++)
                {
                    if (!string.Equals(
                            attributes[i].Key, name, StringComparison.OrdinalIgnoreCase))
                        continue;
                    attributes[i] = new KeyValuePair<string, string>(attributes[i].Key, value);
                    return;
                }
                var grown = new KeyValuePair<string, string>[attributes.Length + 1];
                System.Array.Copy(attributes, grown, attributes.Length);
                attributes = grown;
                root.Attributes = grown;
            }
            else
            {
                attributes = new KeyValuePair<string, string>[1];
                root.Attributes = attributes;
            }
            attributes[attributes.Length - 1] = new KeyValuePair<string, string>(name, value);
        }

        private void DrawResolvedImage(
            SvgElement element,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle style,
            string reference,
            string fragment)
        {
            if (!TryResolveResource(reference, SvgResourceKind.Image, out var resource)) return;
            byte[] bytes = resource.Content.ToArray();
            if (!_resources.TryAdmit(bytes.Length))
            {
                _report.RejectResource("resolved image cumulative resource budget exceeded");
                return;
            }

            bool isSvg = string.Equals(
                             resource.ContentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase) ||
                         resource.Uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                         LooksLikeSvg(bytes);
            if (isSvg)
            {
                DrawSvgImageBytes(element, canvas, viewport, style, bytes, resource.Uri, fragment);
                return;
            }

            if (!TryBorrowOrDecodeImage(
                    bytes, _resources, out var image, out var imageError))
            {
                _report.RejectResource(imageError);
                return;
            }
            DrawBorrowedImage(element, canvas, viewport, style, image);
        }

        private bool TryResolveResource(
            string reference,
            SvgResourceKind kind,
            out SvgResolvedResource resource)
        {
            resource = default;
            if (!_limits.AllowExternalReferences)
            {
                _report.RejectResource(
                    "external SVG reference rejected because external references are disabled");
                return false;
            }
            if (_baseUri == null || _resourceResolver == null ||
                !Uri.TryCreate(_baseUri, reference, out var absolute) || !absolute.IsAbsoluteUri)
            {
                _report.RejectResource("external SVG resource has no authorized resolver context");
                return false;
            }
            var fetchUri = new UriBuilder(absolute) { Fragment = string.Empty }.Uri;
            if (!IsSameOrigin(_baseUri, fetchUri))
            {
                _report.RejectResource("cross-origin SVG resource rejected by renderer policy");
                return false;
            }
            if (!_resourceResolver.TryResolve(fetchUri, kind, out resource, out string error))
            {
                _report.RejectResource(string.IsNullOrWhiteSpace(error)
                    ? "authorized SVG resource resolver returned no resource"
                    : error);
                return false;
            }
            if (resource.Uri == null || resource.Uri != fetchUri)
            {
                _report.RejectResource("SVG resource resolver returned mismatched URI");
                resource = default;
                return false;
            }
            if (resource.Content.Length <= 0 || resource.Content.Length > _resources.MaxDecodedImageBytes)
            {
                _report.RejectResource("resolved SVG resource exceeds byte budget");
                resource = default;
                return false;
            }
            return true;
        }

        private static bool IsSameOrigin(Uri first, Uri second) =>
            first.Scheme.Equals(second.Scheme, StringComparison.OrdinalIgnoreCase) &&
            first.Host.Equals(second.Host, StringComparison.OrdinalIgnoreCase) &&
            first.Port == second.Port;

        private static bool LooksLikeSvg(byte[] bytes)
        {
            int length = Math.Min(bytes.Length, 512);
            string prefix;
            try { prefix = Encoding.UTF8.GetString(bytes, 0, length); }
            catch { return false; }
            return prefix.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void DrawBorrowedImage(
            SvgElement el,
            SKCanvas canvas,
            ViewportContext viewport,
            InheritedStyle style,
            SKImage image)
        {
            if (!TryResolveImageBox(
                    el, viewport, image.Width, image.Height, out var imageViewport))
            {
                return;
            }

            bool layered = TryBeginGroupOpacity(el, canvas, out var layerPaint);
            try
            {
                using var state = new CanvasState(canvas);
                using var paint = new SKPaint { IsAntialias = true };
                paint.Color = style.Visibility ? SKColors.Black : SKColors.Transparent;
                var source = new SKRect(0f, 0f, image.Width, image.Height);
                var destination = ResolveImageDestination(
                    imageViewport, image.Width, image.Height,
                    el.GetAttribute("preserveAspectRatio"));
                if (destination.IsEmpty) return;
                canvas.ClipRect(imageViewport);
                canvas.DrawImage(image, source, destination, SKSamplingOptions.Default, paint);
            }
            finally
            {
                if (layered)
                {
                    canvas.Restore();
                    _activeLayers--;
                    layerPaint.Dispose();
                }
            }
        }

        private static byte[] DecodeSvgDataUriBytes(string href, int maxBytes, out string error)
        {
            int comma = href.IndexOf(',');
            if (comma < 0)
            {
                error = "embedded SVG data URI has no payload separator";
                return null;
            }
            string header = href.Substring(5, comma - 5);
            if (header.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) >= 0)
                return DecodeDataUriBytes(href, maxBytes, out error);

            string payload = href.Substring(comma + 1);
            if ((long)payload.Length > (long)maxBytes * 3L)
            {
                error = "embedded SVG payload exceeds admission budget";
                return null;
            }
            var bytes = new byte[Math.Min(payload.Length, maxBytes)];
            int count = 0;
            for (int i = 0; i < payload.Length; i++)
            {
                if (count >= maxBytes)
                {
                    error = "embedded SVG payload exceeds admission budget";
                    return null;
                }
                char c = payload[i];
                if (c == '%')
                {
                    if (i + 2 >= payload.Length ||
                        !TryHex(payload[i + 1], out int high) || !TryHex(payload[i + 2], out int low))
                    {
                        error = "embedded SVG data URI has invalid percent encoding";
                        return null;
                    }
                    bytes[count++] = (byte)((high << 4) | low);
                    i += 2;
                }
                else if (c <= 0x7f)
                {
                    bytes[count++] = (byte)c;
                }
                else
                {
                    error = "embedded SVG data URI must percent-encode non-ASCII bytes";
                    return null;
                }
            }
            if (count == bytes.Length)
            {
                error = null;
                return bytes;
            }
            Array.Resize(ref bytes, count);
            error = null;
            return bytes;

            static bool TryHex(char c, out int value)
            {
                if (c is >= '0' and <= '9') { value = c - '0'; return true; }
                if (c is >= 'a' and <= 'f') { value = c - 'a' + 10; return true; }
                if (c is >= 'A' and <= 'F') { value = c - 'A' + 10; return true; }
                value = 0;
                return false;
            }
        }

        /// <summary>
        /// Decodes an embedded raster only after the codec header has passed the
        /// dimension and pixel budgets. This prevents compressed image bombs from
        /// allocating their advertised surface before admission control runs.
        /// </summary>
        internal static bool TryDecodeEmbeddedBitmap(
            byte[] bytes,
            long maxPixels,
            int maxDimension,
            out SKBitmap bitmap,
            out string error) =>
            TryDecodeEmbeddedSurface(
                bytes, maxPixels, maxDimension, null, out bitmap, out error);

        private static bool TryBorrowOrDecodeImage(
            byte[] bytes,
            SvgRenderResources resources,
            out SKImage image,
            out string error)
        {
            image = null;
            if (resources == null)
            {
                error = "image decode has no render resource scope";
                return false;
            }

            if (resources.TryBorrowDecodedImage(bytes, out image, out _))
            {
                error = null;
                return true;
            }

            if (!TryDecodeEmbeddedSurface(
                    bytes,
                    resources.MaxDecodedImagePixels,
                    resources.MaxRasterDimension,
                    resources,
                    out var bitmap,
                    out error))
            {
                return false;
            }

            try
            {
                image = SKImage.FromBitmap(bitmap);
            }
            finally
            {
                bitmap.Dispose();
            }

            if (image == null)
            {
                error = "image raster allocation refused";
                return false;
            }

            resources.RetainDecodedImage(bytes, image);
            return true;
        }

        private static bool TryDecodeEmbeddedSurface(
            byte[] bytes,
            long maxPixels,
            int maxDimension,
            SvgRenderResources resources,
            out SKBitmap bitmap,
            out string error)
        {
            bitmap = null;
            error = null;

            if (bytes == null || bytes.Length == 0)
            {
                error = "image data URI has an empty payload";
                return false;
            }

            try
            {
                using var data = SKData.CreateCopy(bytes);
                using var codec = SKCodec.Create(data);
                if (codec == null)
                {
                    error = "image data URI failed to decode";
                    return false;
                }

                var sourceInfo = codec.Info;
                long pixels = (long)sourceInfo.Width * sourceInfo.Height;
                if (sourceInfo.Width <= 0 || sourceInfo.Height <= 0 ||
                    sourceInfo.Width > maxDimension || sourceInfo.Height > maxDimension ||
                    pixels <= 0 || pixels > maxPixels)
                {
                    error = "image decoded size exceeds raster budget; rejected";
                    return false;
                }

                if (resources != null && !resources.TryAdmitDecodedPixels(pixels))
                {
                    error = "image cumulative decoded raster budget exceeded";
                    return false;
                }

                var decodeInfo = new SKImageInfo(
                    sourceInfo.Width,
                    sourceInfo.Height,
                    SKColorType.Bgra8888,
                    SKAlphaType.Premul);
                var candidate = new SKBitmap();
                if (!candidate.TryAllocPixels(decodeInfo))
                {
                    candidate.Dispose();
                    error = "image raster allocation refused";
                    return false;
                }

                var decodeResult = codec.GetPixels(decodeInfo, candidate.GetPixels());
                if (decodeResult != SKCodecResult.Success)
                {
                    candidate.Dispose();
                    error = $"image data URI decode failed ({decodeResult})";
                    return false;
                }

                bitmap = candidate;
                return true;
            }
            catch (Exception)
            {
                error = "image data URI failed to decode";
                return false;
            }
        }

        /// <summary>
        /// Maps image content of the given natural size onto <paramref name="destination"/>
        /// under <paramref name="current"/>. Composing a viewBox scale and an image-fit scale
        /// in float32 lands edges a few ulps short of whole device pixels (1.5 user units at
        /// 200/3 px each is 99.999996 px), which analytic coverage paints as a 254-alpha
        /// seam. For axis-aligned placements, device edges within 1/1000 px of a pixel
        /// boundary snap onto it; anything else composes normally.
        /// </summary>
        private static SKMatrix PlaceImageContent(
            SKMatrix current, SKRect destination, float contentWidth, float contentHeight)
        {
            var local = SKMatrix.CreateScaleTranslation(
                destination.Width / contentWidth, destination.Height / contentHeight,
                destination.Left, destination.Top);
            var placed = SKMatrix.Concat(current, local);
            if (placed.SkewX != 0f || placed.SkewY != 0f || placed.Persp0 != 0f ||
                placed.Persp1 != 0f || placed.Persp2 != 1f ||
                placed.ScaleX <= 0f || placed.ScaleY <= 0f)
                return placed;

            double left = placed.TransX;
            double top = placed.TransY;
            double right = left + (double)placed.ScaleX * contentWidth;
            double bottom = top + (double)placed.ScaleY * contentHeight;
            left = SnapToPixel(left);
            top = SnapToPixel(top);
            right = SnapToPixel(right);
            bottom = SnapToPixel(bottom);
            var snapped = SKMatrix.CreateScaleTranslation(
                (float)((right - left) / contentWidth), (float)((bottom - top) / contentHeight),
                (float)left, (float)top);
            return SvgValues.IsFinite(snapped) ? snapped : placed;

            static double SnapToPixel(double edge)
            {
                double nearest = Math.Round(edge);
                return Math.Abs(edge - nearest) < 1e-3 ? nearest : edge;
            }
        }

        private static SKRect ResolveImageDestination(
            SKRect viewport,
            float sourceWidth,
            float sourceHeight,
            string preserveAspectRatio)
        {
            ParsePreserveAspectRatio(preserveAspectRatio, out var align, out var meet);
            if (!SvgValues.IsFinite(viewport.Left) || !SvgValues.IsFinite(viewport.Top) ||
                !SvgValues.IsFinite(viewport.Right) || !SvgValues.IsFinite(viewport.Bottom) ||
                !SvgValues.IsFinite(sourceWidth) || !SvgValues.IsFinite(sourceHeight) ||
                sourceWidth <= 0f || sourceHeight <= 0f || viewport.Width <= 0f || viewport.Height <= 0f)
            {
                return SKRect.Empty;
            }
            if (align == ParAlign.None)
            {
                return viewport;
            }

            float scaleX = viewport.Width / sourceWidth;
            float scaleY = viewport.Height / sourceHeight;
            float scale = meet == ParMeet.Slice
                ? System.Math.Max(scaleX, scaleY)
                : System.Math.Min(scaleX, scaleY);
            float width = sourceWidth * scale;
            float height = sourceHeight * scale;
            if (!SvgValues.IsFinite(scaleX) || !SvgValues.IsFinite(scaleY) ||
                !SvgValues.IsFinite(scale) || !SvgValues.IsFinite(width) ||
                !SvgValues.IsFinite(height) || width <= 0f || height <= 0f)
                return SKRect.Empty;
            float x = viewport.Left;
            float y = viewport.Top;

            if ((align & ParAlign.XMid) != 0) x += (viewport.Width - width) / 2f;
            else if ((align & ParAlign.XMax) != 0) x += viewport.Width - width;
            if ((align & ParAlign.YMid) != 0) y += (viewport.Height - height) / 2f;
            else if ((align & ParAlign.YMax) != 0) y += viewport.Height - height;

            if (!SvgValues.IsFinite(x) || !SvgValues.IsFinite(y) ||
                !SvgValues.IsFinite(x + width) || !SvgValues.IsFinite(y + height))
                return SKRect.Empty;
            return new SKRect(x, y, x + width, y + height);
        }

        /// <summary>
        /// Strict data-URI byte extraction: requires an explicit base64 flag,
        /// bounds the payload length, and never throws on malformed input.
        /// </summary>
        internal static byte[] DecodeDataUriBytes(string href, int maxDecodedBytes, out string error)
        {
            error = null;
            // data:[<mime>][;base64],<payload>
            int comma = href.IndexOf(',');
            if (comma < 0)
            {
                error = "image data URI missing payload separator";
                return null;
            }

            var header = href.Substring(5, comma - 5); // skip "data:"
            bool isBase64 = header.EndsWith(";base64", System.StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(header, "base64", System.StringComparison.OrdinalIgnoreCase);
            if (!isBase64)
            {
                error = "only base64 image payloads are supported";
                return null;
            }

            string payload = href.Substring(comma + 1);
            // Base64 expands decoded data by 4/3. Reject from encoded length
            // before allocating the decoded byte array.
            long maxEncodedChars = ((long)maxDecodedBytes + 2L) / 3L * 4L;
            if (payload.Length > maxEncodedChars)
            {
                error = "image payload exceeds admission budget";
                return null;
            }

            try
            {
                return Convert.FromBase64String(payload);
            }
            catch (FormatException)
            {
                error = "image payload is not valid base64";
                return null;
            }
        }

        private static bool IsSvgDataUri(string href)
        {
            int comma = href.IndexOf(',');
            if (comma < 5) return false;
            var header = href.AsSpan(5, comma - 5);
            int semicolon = header.IndexOf(';');
            var mediaType = (semicolon < 0 ? header : header.Slice(0, semicolon)).Trim();
            return mediaType.Equals("image/svg+xml".AsSpan(), System.StringComparison.OrdinalIgnoreCase);
        }
    }
}
