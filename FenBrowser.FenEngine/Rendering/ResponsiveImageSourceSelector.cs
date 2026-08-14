using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;

namespace FenBrowser.FenEngine.Rendering
{
    internal static class ResponsiveImageSourceSelector
    {
        private sealed class Candidate
        {
            public string Url { get; init; }
            public int Width { get; init; }
            public double Density { get; init; }
        }

        public static string PickBestImageCandidate(string src, string srcset, double viewportWidth, double devicePixelRatio = 1.0)
        {
            if (string.IsNullOrWhiteSpace(srcset))
            {
                return src;
            }

            try
            {
                var candidates = ParseCandidates(srcset);
                if (candidates.Count == 0)
                {
                    return src;
                }

                var widthCandidates = candidates
                    .Where(static c => c.Width > 0)
                    .OrderBy(static c => c.Width)
                    .ToList();
                if (widthCandidates.Count > 0)
                {
                    // Width-descriptor srcsets require the CSS source size (`sizes`) for
                    // exact selection. Until that source-size parser is wired, viewport
                    // width is the conservative fallback used by the existing engine.
                    var requiredWidth = Math.Max(1.0, viewportWidth) * NormalizeDevicePixelRatio(devicePixelRatio);
                    return (widthCandidates.FirstOrDefault(c => c.Width >= requiredWidth) ?? widthCandidates[^1]).Url;
                }

                var densityCandidates = candidates
                    .Where(static c => c.Density > 0 && double.IsFinite(c.Density))
                    .ToList();

                // In a density-descriptor source set, `src` participates as an implicit
                // 1x candidate when the srcset did not already provide one.
                if (!string.IsNullOrWhiteSpace(src) &&
                    !densityCandidates.Any(static c => Math.Abs(c.Density - 1.0) < 0.000001))
                {
                    densityCandidates.Add(new Candidate
                    {
                        Url = src.Trim(),
                        Width = 0,
                        Density = 1.0
                    });
                }

                if (densityCandidates.Count > 0)
                {
                    densityCandidates.Sort(static (a, b) => a.Density.CompareTo(b.Density));
                    var requiredDensity = NormalizeDevicePixelRatio(devicePixelRatio);
                    return (densityCandidates.FirstOrDefault(c => c.Density >= requiredDensity) ?? densityCandidates[^1]).Url;
                }

                return candidates[0].Url;
            }
            catch
            {
                return src;
            }
        }

        public static string PickCurrentImageSource(Element image, double viewportWidth, double viewportHeight, double devicePixelRatio = 1.0)
        {
            if (image == null || !string.Equals(image.TagName, "IMG", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var pictureParent = image.ParentElement;
            if (pictureParent != null &&
                string.Equals(pictureParent.NodeName, "picture", StringComparison.OrdinalIgnoreCase))
            {
                var surface = new BrowserSurfaceProfile
                {
                    Viewport = BrowserViewportMetrics.Create(viewportWidth, viewportHeight, devicePixelRatio: devicePixelRatio)
                };

                foreach (var sibling in pictureParent.ChildNodes.OfType<Element>())
                {
                    // Only <source> elements before the <img> participate in picture
                    // source selection. A later <source> belongs after the fallback and
                    // must not retroactively override it.
                    if (ReferenceEquals(sibling, image))
                    {
                        break;
                    }

                    if (!string.Equals(sibling.NodeName, "source", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var mime = sibling.GetAttribute("type");
                    if (!string.IsNullOrEmpty(mime) &&
                        !mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var media = sibling.GetAttribute("media");
                    if (!string.IsNullOrWhiteSpace(media) && !surface.MatchesMediaQuery(media))
                    {
                        continue;
                    }

                    var sourceSrc = FirstNonEmpty(
                        sibling.GetAttribute("src"),
                        sibling.GetAttribute("data-src"),
                        sibling.GetAttribute("data-lazy"));
                    var sourceSrcset = FirstNonEmpty(
                        sibling.GetAttribute("srcset"),
                        sibling.GetAttribute("data-srcset"));
                    if (IsPlaceholderOnlyPictureSource(sibling, sourceSrc, sourceSrcset))
                    {
                        continue;
                    }

                    var candidate = PickBestImageCandidate(
                        sourceSrc,
                        sourceSrcset,
                        viewportWidth,
                        devicePixelRatio);

                    if (!string.IsNullOrWhiteSpace(candidate) && !IsKnownPlaceholderImageUrl(candidate))
                    {
                        return ResolveAgainstDocumentBase(image, candidate);
                    }
                }
            }

            var imageSrc = FirstNonEmpty(
                image.GetAttribute("src"),
                image.GetAttribute("data-src"),
                image.GetAttribute("data-lazy"));
            var imageSrcset = FirstNonEmpty(
                image.GetAttribute("srcset"),
                image.GetAttribute("data-srcset"));

            return ResolveAgainstDocumentBase(
                image,
                PickBestImageCandidate(
                    imageSrc,
                    imageSrcset,
                    viewportWidth,
                    devicePixelRatio));
        }

        private static string ResolveAgainstDocumentBase(Element image, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
                return candidate;

            var trimmed = candidate.Trim();
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out _))
                return trimmed;

            var document = image?.OwnerDocument;
            var baseText = !string.IsNullOrWhiteSpace(document?.BaseURI)
                ? document.BaseURI
                : document?.URL;

            if (string.IsNullOrWhiteSpace(baseText) ||
                !Uri.TryCreate(baseText, UriKind.Absolute, out var baseUri) ||
                !Uri.TryCreate(baseUri, trimmed, out var resolved))
            {
                return trimmed;
            }

            return resolved.AbsoluteUri;
        }

        private static string FirstNonEmpty(params string[] candidates)
        {
            if (candidates == null)
            {
                return null;
            }

            for (int i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Parses the common srcset candidate grammar without splitting blindly on
        /// commas. Commas are valid inside URL tokens (most visibly data: URLs), so a
        /// raw string.Split(',') corrupts valid candidates before descriptors are read.
        /// </summary>
        private static List<Candidate> ParseCandidates(string srcset)
        {
            var candidates = new List<Candidate>();
            if (string.IsNullOrWhiteSpace(srcset))
            {
                return candidates;
            }

            var index = 0;
            while (index < srcset.Length)
            {
                while (index < srcset.Length &&
                       (IsAsciiWhitespace(srcset[index]) || srcset[index] == ','))
                {
                    index++;
                }

                if (index >= srcset.Length)
                {
                    break;
                }

                var urlStart = index;
                while (index < srcset.Length && !IsAsciiWhitespace(srcset[index]))
                {
                    index++;
                }

                if (index <= urlStart)
                {
                    continue;
                }

                var rawUrl = srcset.Substring(urlStart, index - urlStart);
                var trailingCommaCount = 0;
                for (var i = rawUrl.Length - 1; i >= 0 && rawUrl[i] == ','; i--)
                {
                    trailingCommaCount++;
                }

                if (trailingCommaCount > 0)
                {
                    var url = rawUrl[..^trailingCommaCount].Trim();
                    if (url.Length > 0)
                    {
                        candidates.Add(new Candidate
                        {
                            Url = url,
                            Width = 0,
                            Density = 1.0
                        });
                    }
                    continue;
                }

                SkipAsciiWhitespace(srcset, ref index);
                var descriptorStart = index;
                while (index < srcset.Length && srcset[index] != ',')
                {
                    index++;
                }

                var descriptorText = srcset.Substring(descriptorStart, index - descriptorStart).Trim();
                if (index < srcset.Length && srcset[index] == ',')
                {
                    index++;
                }

                var parsed = ParseCandidate(rawUrl.Trim(), descriptorText);
                if (parsed != null)
                {
                    candidates.Add(parsed);
                }
            }

            return candidates;
        }

        private static Candidate ParseCandidate(string url, string descriptorText)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(descriptorText))
            {
                return new Candidate
                {
                    Url = url,
                    Width = 0,
                    Density = 1.0
                };
            }

            var descriptors = descriptorText.Split(
                (char[])null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (descriptors.Length != 1)
            {
                return null;
            }

            var descriptor = descriptors[0].ToLowerInvariant();
            if (descriptor.EndsWith("w", StringComparison.Ordinal) &&
                int.TryParse(descriptor[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var width) &&
                width > 0)
            {
                return new Candidate
                {
                    Url = url,
                    Width = width,
                    Density = 0
                };
            }

            if (descriptor.EndsWith("x", StringComparison.Ordinal) &&
                double.TryParse(descriptor[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var density) &&
                density > 0 && double.IsFinite(density))
            {
                return new Candidate
                {
                    Url = url,
                    Width = 0,
                    Density = density
                };
            }

            return null;
        }

        private static double NormalizeDevicePixelRatio(double devicePixelRatio)
        {
            return double.IsFinite(devicePixelRatio) && devicePixelRatio > 0
                ? devicePixelRatio
                : 1.0;
        }

        private static bool IsAsciiWhitespace(char c) =>
            c is ' ' or '\t' or '\n' or '\r' or '\f';

        private static void SkipAsciiWhitespace(string text, ref int index)
        {
            while (index < text.Length && IsAsciiWhitespace(text[index]))
            {
                index++;
            }
        }

        private static bool IsPlaceholderOnlyPictureSource(Element sourceElement, string src, string srcset)
        {
            if (sourceElement?.HasAttribute("data-empty") == true)
            {
                return true;
            }

            bool hasNonPlaceholder = false;

            if (!string.IsNullOrWhiteSpace(src) && !IsKnownPlaceholderImageUrl(src))
            {
                hasNonPlaceholder = true;
            }

            if (!string.IsNullOrWhiteSpace(srcset))
            {
                foreach (var candidate in ParseCandidates(srcset))
                {
                    if (!IsKnownPlaceholderImageUrl(candidate.Url))
                    {
                        hasNonPlaceholder = true;
                        break;
                    }
                }
            }

            return !hasNonPlaceholder;
        }

        private static bool IsKnownPlaceholderImageUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return true;
            }

            var trimmed = url.Trim();
            if (!trimmed.StartsWith("data:image/gif", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return trimmed.IndexOf("R0lGODlhAQAB", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
