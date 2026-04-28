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

                var widthCandidates = candidates.Where(c => c.Width > 0).OrderBy(c => c.Width).ToList();
                if (widthCandidates.Count > 0)
                {
                    var requiredWidth = Math.Max(1.0, viewportWidth) * Math.Max(1.0, devicePixelRatio);
                    return (widthCandidates.FirstOrDefault(c => c.Width >= requiredWidth) ?? widthCandidates[^1]).Url;
                }

                var densityCandidates = candidates.Where(c => c.Density > 0).OrderBy(c => c.Density).ToList();
                if (densityCandidates.Count > 0)
                {
                    var requiredDensity = Math.Max(1.0, devicePixelRatio);
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

                    var candidate = PickBestImageCandidate(
                        sibling.GetAttribute("src"),
                        sibling.GetAttribute("srcset"),
                        viewportWidth,
                        devicePixelRatio);

                    if (!string.IsNullOrWhiteSpace(candidate))
                    {
                        return candidate;
                    }
                }
            }

            return PickBestImageCandidate(
                image.GetAttribute("src"),
                image.GetAttribute("srcset"),
                viewportWidth,
                devicePixelRatio);
        }

        private static List<Candidate> ParseCandidates(string srcset)
        {
            var candidates = new List<Candidate>();
            foreach (var rawCandidate in srcset.Split(','))
            {
                var candidateText = rawCandidate?.Trim();
                if (string.IsNullOrWhiteSpace(candidateText))
                {
                    continue;
                }

                var parts = candidateText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
                {
                    continue;
                }

                var candidate = new Candidate
                {
                    Url = parts[0],
                    Width = 0,
                    Density = 0
                };

                if (parts.Length > 1)
                {
                    var descriptor = parts[1].Trim().ToLowerInvariant();
                    if (descriptor.EndsWith("w", StringComparison.Ordinal) &&
                        int.TryParse(descriptor[..^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var width))
                    {
                        candidate = new Candidate
                        {
                            Url = candidate.Url,
                            Width = width,
                            Density = 0
                        };
                    }
                    else if (descriptor.EndsWith("x", StringComparison.Ordinal) &&
                             double.TryParse(descriptor[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var density))
                    {
                        candidate = new Candidate
                        {
                            Url = candidate.Url,
                            Width = 0,
                            Density = density
                        };
                    }
                }

                candidates.Add(candidate);
            }

            return candidates;
        }
    }
}
