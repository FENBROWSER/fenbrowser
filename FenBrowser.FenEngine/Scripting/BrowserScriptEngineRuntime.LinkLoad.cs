using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Load and error events for script-inserted <c>link</c> elements.
/// </summary>
/// <remarks>
/// HTML "fetch and process the linked resource" (4.6.7): once a stylesheet,
/// preload or module preload has been fetched the user agent queues an element
/// task that fires <c>load</c> at the link, or <c>error</c> when the fetch
/// failed. Bundlers wait on exactly that: webpack's mini-css chunk loader
/// inserts a <c>&lt;link rel=stylesheet&gt;</c> and resolves the chunk's promise
/// from <c>link.onload</c>, so a page whose links never fire leaves every lazy
/// route suspended on a blank fallback. Parser-inserted links are left to the
/// document's own stylesheet loading; this tracks links that scripts add or
/// re-point. Only the top realm observes, as for images.
/// </remarks>
public sealed partial class FenJsBrowserScriptEngine
{
    private readonly object _linkLoadSync = new();
    // The href each tracked link was last fetched for; a completion for an
    // older href is dropped. Weak per element so a document's links do not
    // outlive it through here.
    private readonly ConditionalWeakTable<Element, StrongBox<string>> _linkRequests = new();
    private bool _linkLoadObserverSubscribed;

    private void EnsureLinkLoadObserver()
    {
        var owner = ImageLoadOwner;
        if (!ReferenceEquals(owner, this))
        {
            owner.EnsureLinkLoadObserver();
            return;
        }

        if (_linkLoadObserverSubscribed || _realmAbandoned)
        {
            return;
        }

        _linkLoadObserverSubscribed = true;
        Node.OnMutation += OnDomMutationForLinks;
    }

    private void UnsubscribeLinkLoadObserver()
    {
        if (!_linkLoadObserverSubscribed)
        {
            return;
        }

        _linkLoadObserverSubscribed = false;
        Node.OnMutation -= OnDomMutationForLinks;
    }

    private static bool IsLoadableLinkElement(Element element)
    {
        if (!string.Equals(element?.TagName, "link", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var rel = element.GetAttribute("rel");
        if (string.IsNullOrWhiteSpace(rel))
        {
            return false;
        }

        foreach (var token in rel.Split((char[])null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, "stylesheet", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "preload", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "modulepreload", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void OnDomMutationForLinks(
        Node target,
        string type,
        string attributeName,
        string attributeNamespace,
        List<Node> addedNodes,
        List<Node> removedNodes)
    {
        if (_realmAbandoned)
        {
            return;
        }

        try
        {
            if (string.Equals(type, "attributes", StringComparison.Ordinal))
            {
                if ((string.Equals(attributeName, "href", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(attributeName, "rel", StringComparison.OrdinalIgnoreCase)) &&
                    target is Element element &&
                    IsLoadableLinkElement(element) &&
                    element.IsConnected &&
                    OwnsDocumentForImageEvents(element.OwnerDocument))
                {
                    StartLinkRequest(element);
                }

                return;
            }

            if (!string.Equals(type, "childList", StringComparison.Ordinal))
            {
                return;
            }

            // A link that leaves the document stops processing its resource: the
            // request is forgotten, so its completion fires nothing, and inserting
            // the link again starts a new one.
            if (removedNodes != null)
            {
                for (var i = 0; i < removedNodes.Count; i++)
                {
                    if (removedNodes[i] is Element removed)
                    {
                        ForgetLinkRequests(removed);
                    }
                }
            }

            if (addedNodes == null)
            {
                return;
            }

            for (var i = 0; i < addedNodes.Count; i++)
            {
                if (addedNodes[i] is not Element added || !OwnsDocumentForImageEvents(added.OwnerDocument))
                {
                    continue;
                }

                if (IsLoadableLinkElement(added))
                {
                    StartLinkRequest(added);
                }

                foreach (var descendant in added.Descendants())
                {
                    if (descendant is Element element && IsLoadableLinkElement(element))
                    {
                        StartLinkRequest(element);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] Link mutation walk failed: {ex.GetType().Name}: {ex.Message}",
                LogCategory.JavaScript);
        }
    }

    private void ForgetLinkRequests(Element root)
    {
        lock (_linkLoadSync)
        {
            if (IsLoadableLinkElement(root))
            {
                _linkRequests.Remove(root);
            }

            foreach (var descendant in root.Descendants())
            {
                if (descendant is Element element && IsLoadableLinkElement(element))
                {
                    _linkRequests.Remove(element);
                }
            }
        }
    }

    /// <summary>
    /// Fetches the link's resource and queues <c>load</c> or <c>error</c> at the
    /// element when it lands. The same href seen again is the same request.
    /// </summary>
    private void StartLinkRequest(Element link)
    {
        var owner = ImageLoadOwner;
        if (!ReferenceEquals(owner, this))
        {
            owner.StartLinkRequest(link);
            return;
        }

        var url = ResolveElementUrlProperty(link, "href");
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var requestUri))
        {
            return;
        }

        StrongBox<string> token;
        lock (_linkLoadSync)
        {
            if (_linkRequests.TryGetValue(link, out var current) &&
                string.Equals(current.Value, url, StringComparison.Ordinal))
            {
                return;
            }

            token = new StrongBox<string>(url);
            _linkRequests.AddOrUpdate(link, token);
        }

        _ = Task.Run(async () =>
        {
            var succeeded = await FetchLinkResourceAsync(link, requestUri).ConfigureAwait(false);
            QueueLinkEvent(link, token, succeeded ? "load" : "error");
        });
    }

    // A stylesheet link reports load only when the sheet and every sheet it
    // imports arrived; nesting is capped the way a cascade would cap it.
    private const int MaxLinkImportDepth = 8;

    private Task<bool> FetchLinkResourceAsync(Element link, Uri requestUri)
    {
        var destination = ResolveLinkFetchDestination(link);
        return FetchLinkedSheetAsync(
            link, requestUri, destination, depth: 0,
            new HashSet<string>(StringComparer.Ordinal));
    }

    private async Task<bool> FetchLinkedSheetAsync(
        Element link, Uri uri, string destination, int depth, HashSet<string> seen)
    {
        var isStylesheet = string.Equals(destination, "style", StringComparison.Ordinal);
        if (depth > MaxLinkImportDepth || !seen.Add(uri.AbsoluteUri))
        {
            // An import cycle or runaway nesting: the sheet is already accounted for.
            return true;
        }

        string text;
        string contentType;
        bool nosniff = false;
        bool sameOrigin;

        if (string.Equals(uri.Scheme, "data", StringComparison.OrdinalIgnoreCase))
        {
            // Fetch 4.2 "data: URL processor": no comma fails; the MIME type is what
            // precedes the comma, and ";base64" marks a base64 body.
            var body = uri.OriginalString.Substring(uri.Scheme.Length + 1);
            var comma = body.IndexOf(',');
            if (comma < 0)
            {
                return false;
            }

            var meta = Uri.UnescapeDataString(body.Substring(0, comma)).Trim();
            var payload = body.Substring(comma + 1);
            var isBase64 = meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase);
            if (isBase64)
            {
                meta = meta.Substring(0, meta.Length - ";base64".Length);
            }

            contentType = meta.Length == 0 ? "text/plain" : meta;
            try
            {
                text = isBase64
                    ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(Uri.UnescapeDataString(payload)))
                    : Uri.UnescapeDataString(payload);
            }
            catch (FormatException)
            {
                return false;
            }

            sameOrigin = true;
        }
        else
        {
            var handler = FetchHandler;
            if (handler == null)
            {
                return false;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", destination);
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
                if (isStylesheet)
                {
                    request.Headers.TryAddWithoutValidation("Accept", "text/css,*/*;q=0.1");
                }
                if (_currentBaseUri != null)
                {
                    request.Headers.Referrer = _currentBaseUri;
                }

                using var response = await handler(request).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return false;
                }

                // Drained either way, so the response cache holds the body for the
                // cascade's own fetch of the same stylesheet.
                text = response.Content == null
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                contentType = ReadRawHeader(response.Content?.Headers, "Content-Type") ??
                              ReadRawHeader(response.Headers, "Content-Type");
                var options = ReadRawHeader(response.Headers, "X-Content-Type-Options");
                nosniff = options != null &&
                          string.Equals(options.Split(',')[0].Trim(), "nosniff", StringComparison.OrdinalIgnoreCase);
                var finalUri = response.RequestMessage?.RequestUri ?? uri;
                sameOrigin = _currentBaseUri != null &&
                    Uri.Compare(finalUri, _currentBaseUri, UriComponents.SchemeAndServer,
                        UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn(
                    $"[FenJsBridge] link fetch failed for '{uri}': {ex.Message}",
                    LogCategory.JavaScript);
                return false;
            }
        }

        if (!isStylesheet)
        {
            return true;
        }

        if (!IsAcceptableStylesheetResponse(link, contentType, nosniff, sameOrigin))
        {
            return false;
        }

        // A data: sheet has no base of its own to resolve relative imports against,
        // so they resolve against the document.
        var importBase = string.Equals(uri.Scheme, "data", StringComparison.OrdinalIgnoreCase)
            ? _currentBaseUri ?? uri
            : uri;
        var imports = new List<Task<bool>>();
        foreach (var href in ReadImportUrls(text))
        {
            if (!Uri.TryCreate(importBase, href, out var importUri))
            {
                return false;
            }

            imports.Add(FetchLinkedSheetAsync(link, importUri, "style", depth + 1, seen));
        }

        foreach (var import in imports)
        {
            if (!await import.ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }

    private static string ReadRawHeader(System.Net.Http.Headers.HttpHeaders headers, string name) =>
        headers != null && headers.TryGetValues(name, out var values)
            ? string.Join(",", values)
            : null;

    // HTML 4.6.7 link type "stylesheet" and CSSOM "fetch a style resource":
    // - X-Content-Type-Options: nosniff admits only text/css (Fetch 3.5);
    // - a sheet with no parseable Content-Type takes the stylesheet default,
    //   text/css;
    // - any other type fails, unless the document is in quirks mode and the
    //   response is same-origin.
    private static bool IsAcceptableStylesheetResponse(
        Element link, string contentType, bool nosniff, bool sameOrigin)
    {
        var essence = ReadMimeEssence(contentType);
        if (string.Equals(essence, "text/css", StringComparison.Ordinal))
        {
            return true;
        }

        if (nosniff)
        {
            return false;
        }

        if (essence == null)
        {
            return true;
        }

        return sameOrigin && link.OwnerDocument?.Mode == QuirksMode.Quirks;
    }

    // MIME Sniffing 4.4 "parse a MIME type", essence only: type "/" subtype, both
    // HTTP tokens, lowercased; null when absent or unparseable.
    private static string ReadMimeEssence(string contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var essence = contentType.Split(';')[0].Trim();
        var slash = essence.IndexOf('/');
        if (slash <= 0 || slash == essence.Length - 1)
        {
            return null;
        }

        foreach (var c in essence)
        {
            if (c != '/' && !(char.IsAsciiLetterOrDigit(c) || "!#$%&'*+-.^_`|~".IndexOf(c) >= 0))
            {
                return null;
            }
        }

        return essence.IndexOf('/', slash + 1) >= 0 ? null : essence.ToLowerInvariant();
    }

    private static readonly System.Text.RegularExpressions.Regex ImportRule = new(
        @"@import\s+(?:url\(\s*(?:""([^""]*)""|'([^']*)'|([^)\s]*))\s*\)|""([^""]*)""|'([^']*)')",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex CssComment = new(
        @"/\*.*?\*/", System.Text.RegularExpressions.RegexOptions.Singleline | System.Text.RegularExpressions.RegexOptions.Compiled);

    // The @import rules of a sheet. They may only precede its other rules
    // (CSS Cascade 4 section 2), so only the part before the first block counts.
    private static IEnumerable<string> ReadImportUrls(string css)
    {
        if (string.IsNullOrEmpty(css) || css.IndexOf("@import", StringComparison.OrdinalIgnoreCase) < 0)
        {
            yield break;
        }

        var head = CssComment.Replace(css, " ");
        var brace = head.IndexOf('{');
        if (brace >= 0)
        {
            head = head.Substring(0, brace);
        }

        foreach (System.Text.RegularExpressions.Match match in ImportRule.Matches(head))
        {
            for (var group = 1; group <= 5; group++)
            {
                if (match.Groups[group].Success)
                {
                    yield return match.Groups[group].Value;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The links the parser inserted, and any a script inserted during parsing,
    /// as the page's scripts finish. HTML 4.6.7: each one's load or error fires
    /// once its resource is processed, and the window's load event waits for them
    /// (a stylesheet delays the load event). Their fetches start here, before
    /// DOMContentLoaded, and <see cref="FinishStartupLinkLoadsAsync"/> fires the
    /// events before window load. A request already in flight for one of these
    /// links is superseded, so each link reports once.
    /// </summary>
    private List<(Element Link, StrongBox<string> Token, Task<bool> Fetch)> StartStartupLinkLoads(Node root)
    {
        var loads = new List<(Element, StrongBox<string>, Task<bool>)>();
        if (root == null || !ReferenceEquals(ImageLoadOwner, this) || _realmAbandoned)
        {
            return loads;
        }

        var start = root is Element rootElement ? rootElement.OwnerDocument ?? (Node)root : root;
        foreach (var node in start.Descendants())
        {
            if (node is not Element link || !IsLoadableLinkElement(link) || !link.IsConnected ||
                !OwnsDocumentForImageEvents(link.OwnerDocument))
            {
                continue;
            }

            var url = ResolveElementUrlProperty(link, "href");
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var requestUri))
            {
                continue;
            }

            var token = new StrongBox<string>(url);
            lock (_linkLoadSync)
            {
                _linkRequests.AddOrUpdate(link, token);
            }

            loads.Add((link, token, FetchLinkResourceAsync(link, requestUri)));
        }

        return loads;
    }

    private async Task FinishStartupLinkLoadsAsync(List<(Element Link, StrongBox<string> Token, Task<bool> Fetch)> loads)
    {
        foreach (var (link, token, fetch) in loads)
        {
            var succeeded = await fetch.ConfigureAwait(false);
            if (!IsCurrentLinkRequest(link, token) || !link.IsConnected)
            {
                continue;
            }

            // A link's load/error is a networking task (HTML 4.6.7), queued behind
            // whatever the worker is already running - not user input with the
            // input path's 2s deadline. With that deadline a busy worker (github.com
            // hydrating React after DOMContentLoaded) threw here, and the throw
            // skipped the window load event altogether. A failed dispatch is
            // reported and the document still finishes loading (HTML 8.4 "the end").
            var type = succeeded ? "load" : "error";
            try
            {
                await RunFenJsWithLargeStackAsync(
                    () => DispatchEventForElement(
                        link,
                        type,
                        new BrowserDomEventInit { Bubbles = false, Cancelable = false, Composed = false }),
                    workKind: "LinkLoadEvent").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn(
                    $"[FenJsBridge] Link {type} event failed during startup: {ex.GetType().Name}: {ex.Message}",
                    LogCategory.JavaScript);
            }
        }
    }

    private bool IsCurrentLinkRequest(Element link, StrongBox<string> token)
    {
        lock (_linkLoadSync)
        {
            return _linkRequests.TryGetValue(link, out var current) && ReferenceEquals(current, token);
        }
    }

    private static string ResolveLinkFetchDestination(Element link)
    {
        var rel = link.GetAttribute("rel") ?? string.Empty;
        if (rel.IndexOf("stylesheet", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "style";
        }

        if (rel.IndexOf("modulepreload", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return "script";
        }

        var @as = link.GetAttribute("as")?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(@as) ? "empty" : @as;
    }

    // Always a queued task, never inline: the listener is attached in the same
    // job that inserted the link.
    private void QueueLinkEvent(Element link, StrongBox<string> token, string type)
    {
        if (_realmAbandoned)
        {
            return;
        }

        // The href moved on, the link was removed, or a later request for it took
        // over while this fetch was in flight: that request reports for itself.
        if (!IsCurrentLinkRequest(link, token))
        {
            return;
        }

        // HTML 4.6.7: a link that is no longer connected does not process its
        // resource, so neither load nor error reaches it.
        if (!link.IsConnected)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchEventForElementAsync(
                    link,
                    type,
                    new BrowserDomEventInit { Bubbles = false, Cancelable = false, Composed = false })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn(
                    $"[FenJsBridge] Link {type} event failed: {ex.GetType().Name}: {ex.Message}",
                    LogCategory.JavaScript);
            }
        });
    }
}
