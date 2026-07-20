using System;
using System.Net;
using System.Net.Http;

namespace FenBrowser.Core.Network;

internal static class BrowserRequestHeaderPolicy
{
    internal const string DefaultAcceptLanguage = "en-US,en;q=0.9";

    internal static void Apply(
        HttpRequestMessage request,
        FetchContext context,
        ReferrerPolicyDirective referrerPolicy,
        string accept,
        Uri referrerCandidate = null,
        string acceptEncoding = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);

        Add(request, "Accept", accept);
        BrowserSettings.ApplyBrowserRequestHeaders(request, useMobile: false);
        Add(request, "Accept-Language", DefaultAcceptLanguage);
        Add(request, "Accept-Encoding", acceptEncoding ?? BrowserNetworkCapabilities.AcceptEncodingHeader);

        var destination = string.IsNullOrWhiteSpace(context.Destination) ? "empty" : context.Destination;
        var mode = string.IsNullOrWhiteSpace(context.Mode) ? DetermineMode(destination) : context.Mode;
        Add(request, "Sec-Fetch-Dest", destination);
        Add(request, "Sec-Fetch-Mode", mode);

        var initiator = context.InitiatorUri ?? context.FrameDocumentUri;
        Add(request, "Sec-Fetch-Site", DetermineSite(initiator, request.RequestUri));
        ApplyReferrer(request, referrerCandidate ?? initiator, request.RequestUri, referrerPolicy);

        if (context.IsTopLevelNavigation)
        {
            if (context.IsUserInitiated)
            {
                Add(request, "Sec-Fetch-User", "?1");
            }

            Add(request, "Upgrade-Insecure-Requests", "1");
        }
    }

    internal static string DetermineMode(string destination)
    {
        var normalized = (destination ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is "document" or "iframe") return "navigate";
        if (normalized is "style" or "script" or "image" or "font" or
            "audio" or "video" or "track" or "object" or "embed") return "no-cors";
        return "cors";
    }

    internal static string DetermineSite(Uri initiator, Uri request)
    {
        if (request == null || initiator == null) return "none";
        if (IsSameOrigin(initiator, request)) return "same-origin";
        return IsSameSite(initiator.Host, request.Host) ? "same-site" : "cross-site";
    }

    internal static Uri ComputeReferrer(
        Uri candidate,
        Uri requestUri,
        ReferrerPolicyDirective policy)
    {
        if (candidate == null || requestUri == null || !candidate.IsAbsoluteUri || !requestUri.IsAbsoluteUri)
        {
            return null;
        }

        var sameOrigin = IsSameOrigin(candidate, requestUri);
        var downgrade = string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(requestUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        var origin = ExtractOrigin(candidate);

        return policy switch
        {
            ReferrerPolicyDirective.NoReferrer => null,
            ReferrerPolicyDirective.NoReferrerWhenDowngrade => downgrade ? null : candidate,
            ReferrerPolicyDirective.SameOrigin => sameOrigin ? candidate : null,
            ReferrerPolicyDirective.Origin => origin,
            ReferrerPolicyDirective.StrictOrigin => downgrade ? null : origin,
            ReferrerPolicyDirective.OriginWhenCrossOrigin => sameOrigin ? candidate : origin,
            ReferrerPolicyDirective.UnsafeUrl => candidate,
            _ => sameOrigin ? candidate : downgrade ? null : origin
        };
    }

    private static void ApplyReferrer(
        HttpRequestMessage request,
        Uri candidate,
        Uri requestUri,
        ReferrerPolicyDirective policy)
    {
        var computed = ComputeReferrer(candidate, requestUri, policy);
        if (computed != null)
        {
            request.Headers.Referrer = computed;
        }
    }

    private static Uri ExtractOrigin(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return null;
        var port = uri.IsDefaultPort ? -1 : uri.Port;
        return new UriBuilder(uri.Scheme, uri.Host, port).Uri;
    }

    private static bool IsSameOrigin(Uri left, Uri right) =>
        left != null && right != null && left.IsAbsoluteUri && right.IsAbsoluteUri &&
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool IsSameSite(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(left, out _) || IPAddress.TryParse(right, out _)) return false;
        return string.Equals(SiteKey(left), SiteKey(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string SiteKey(string host)
    {
        var labels = host.Trim().TrimEnd('.').Split('.', StringSplitOptions.RemoveEmptyEntries);
        return labels.Length < 2
            ? host.ToLowerInvariant()
            : $"{labels[^2]}.{labels[^1]}".ToLowerInvariant();
    }

    private static void Add(HttpRequestMessage request, string name, string value)
    {
        if (!string.IsNullOrWhiteSpace(value) && !request.Headers.Contains(name))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
