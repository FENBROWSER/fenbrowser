using System;
using System.Net;
using System.Net.Http;
using FenBrowser.Core.Network.Handlers;

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

        FetchContextRequestOptions.Set(request, context);
        CorsHandler.SetCredentialsMode(request, context.CredentialsMode);

        Add(request, "Accept", accept);
        BrowserSettings.ApplyBrowserRequestHeaders(request, useMobile: false);
        Add(request, "Accept-Language", DefaultAcceptLanguage);
        Add(request, "Accept-Encoding", acceptEncoding ?? BrowserNetworkCapabilities.AcceptEncodingHeader);

        var destination = string.IsNullOrWhiteSpace(context.Destination) ? "empty" : context.Destination;
        var mode = string.IsNullOrWhiteSpace(context.Mode) ? DetermineMode(destination) : context.Mode;

        SetBrowserHeader(request, "Sec-Fetch-Dest", destination);
        SetBrowserHeader(request, "Sec-Fetch-Mode", mode);

        var initiator = context.InitiatorUri ?? context.FrameDocumentUri;
        SetBrowserHeader(request, "Sec-Fetch-Site", DetermineSite(initiator, request.RequestUri));
        ApplyReferrer(request, referrerCandidate ?? initiator, request.RequestUri, referrerPolicy);

        if (context.IsTopLevelNavigation && context.IsUserInitiated)
        {
            SetBrowserHeader(request, "Sec-Fetch-User", "?1");
        }
        else
        {
            request.Headers.Remove("Sec-Fetch-User");
        }

        if (context.IsTopLevelNavigation)
        {
            SetBrowserHeader(request, "Upgrade-Insecure-Requests", "1");
        }
        else
        {
            request.Headers.Remove("Upgrade-Insecure-Requests");
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
        if (request == null || initiator == null || !request.IsAbsoluteUri || !initiator.IsAbsoluteUri)
            return "none";

        if (IsSameOrigin(initiator, request))
            return "same-origin";

        // Fetch Metadata uses a schemeful site boundary. A host-only comparison
        // incorrectly labels http↔https transitions as same-site.
        return IsSameSite(initiator, request) ? "same-site" : "cross-site";
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

        var referrerUrl = StripForReferrer(candidate);
        if (referrerUrl == null)
        {
            return null;
        }

        var origin = ExtractOrigin(referrerUrl);
        if (origin == null)
        {
            return null;
        }

        if (referrerUrl.AbsoluteUri.Length > 4096)
        {
            referrerUrl = origin;
        }

        var sameOrigin = IsSameOrigin(referrerUrl, requestUri);
        var downgrade = IsPotentiallyTrustworthy(referrerUrl) && !IsPotentiallyTrustworthy(requestUri);

        return policy switch
        {
            ReferrerPolicyDirective.NoReferrer => null,
            ReferrerPolicyDirective.NoReferrerWhenDowngrade => downgrade ? null : referrerUrl,
            ReferrerPolicyDirective.SameOrigin => sameOrigin ? referrerUrl : null,
            ReferrerPolicyDirective.Origin => origin,
            ReferrerPolicyDirective.StrictOrigin => downgrade ? null : origin,
            ReferrerPolicyDirective.OriginWhenCrossOrigin => sameOrigin ? referrerUrl : origin,
            ReferrerPolicyDirective.UnsafeUrl => referrerUrl,
            _ => sameOrigin ? referrerUrl : downgrade ? null : origin
        };
    }

    private static void ApplyReferrer(
        HttpRequestMessage request,
        Uri candidate,
        Uri requestUri,
        ReferrerPolicyDirective policy)
    {
        request.Headers.Referrer = ComputeReferrer(candidate, requestUri, policy);
    }

    private static Uri StripForReferrer(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return null;
        }

        if (uri.Scheme.Equals("about", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("blob", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("data", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Fragment = string.Empty
            };
            return builder.Uri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static Uri ExtractOrigin(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri) return null;

        try
        {
            var port = uri.IsDefaultPort ? -1 : uri.Port;
            return new UriBuilder(uri.Scheme, uri.Host, port).Uri;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    private static bool IsPotentiallyTrustworthy(Uri uri)
    {
        if (uri == null || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ||
            uri.Scheme.Equals(Uri.UriSchemeFile, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var host = uri.Host?.TrimEnd('.').Trim('[', ']');
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }

    private static bool IsSameOrigin(Uri left, Uri right) =>
        left != null && right != null && left.IsAbsoluteUri && right.IsAbsoluteUri &&
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool IsSameSite(Uri left, Uri right)
    {
        if (left == null || right == null || !left.IsAbsoluteUri || !right.IsAbsoluteUri)
            return false;

        if (!string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase))
            return false;

        return IsSameSiteHost(left.Host, right.Host);
    }

    private static bool IsSameSiteHost(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) return true;
        if (IPAddress.TryParse(left.Trim('[', ']'), out _) || IPAddress.TryParse(right.Trim('[', ']'), out _)) return false;
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

    private static void SetBrowserHeader(HttpRequestMessage request, string name, string value)
    {
        request.Headers.Remove(name);
        if (!string.IsNullOrWhiteSpace(value))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }
}
