using FenBrowser.FenEngine.Adapters;

namespace FenBrowser.FenEngine.Svg;

/// <summary>
/// Bounded, side-effect-free discovery used by the asynchronous image pipeline
/// to preload resources before entering the synchronous renderer.
/// </summary>
internal static class SvgResourceDiscovery
{
    public static IReadOnlyList<Uri> DiscoverImages(
        string source,
        Uri baseUri,
        SvgRenderLimits limits)
    {
        var resources = new List<Uri>();
        if (string.IsNullOrWhiteSpace(source) || baseUri == null || !baseUri.IsAbsoluteUri ||
            (!baseUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
             !baseUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            return resources;
        limits = SvgRenderLimits.Normalize(limits);
        if (!SvgMarkupParser.TryParse(source, limits, out var document, out _)) return resources;

        var seen = new HashSet<Uri>();
        var stack = new Stack<SvgElement>();
        stack.Push(document.Root);
        int remaining = Math.Max(1, Math.Min(limits.MaxResourceCount, 512));
        while (stack.Count > 0 && resources.Count < remaining)
        {
            SvgElement element = stack.Pop();
            // Images, and the documents that use elements reference with a URL
            // (same-origin sprite sheets): both are preloaded before rendering.
            if (element.Name == "image" ||
                (element.Name == "use" && IsExternalDocumentReference(element)))
            {
                string href = element.GetAttribute("href") ?? element.GetLookup("xlink:href");
                if (!string.IsNullOrWhiteSpace(href) &&
                    !href.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                    Uri.TryCreate(baseUri, href, out Uri? absolute) && absolute.IsAbsoluteUri)
                {
                    var requested = new UriBuilder(absolute) { Fragment = string.Empty }.Uri;
                    if (IsSameOrigin(baseUri, requested) && seen.Add(requested))
                        resources.Add(requested);
                }
            }
            for (int i = element.Children.Count - 1; i >= 0; i--)
                stack.Push(element.Children[i]);
        }
        return resources;
    }

    private static bool IsExternalDocumentReference(SvgElement use)
    {
        string href = use.GetAttribute("href") ?? use.GetLookup("xlink:href");
        return !string.IsNullOrWhiteSpace(href) && !href.TrimStart().StartsWith("#", StringComparison.Ordinal);
    }

    internal static bool IsSameOrigin(Uri first, Uri second) =>
        first.Scheme.Equals(second.Scheme, StringComparison.OrdinalIgnoreCase) &&
        first.Host.Equals(second.Host, StringComparison.OrdinalIgnoreCase) &&
        first.Port == second.Port;
}
