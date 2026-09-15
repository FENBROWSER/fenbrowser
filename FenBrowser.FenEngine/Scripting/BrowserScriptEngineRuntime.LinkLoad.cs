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

            if (!string.Equals(type, "childList", StringComparison.Ordinal) || addedNodes == null)
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

        lock (_linkLoadSync)
        {
            if (_linkRequests.TryGetValue(link, out var current) &&
                string.Equals(current.Value, url, StringComparison.Ordinal))
            {
                return;
            }

            _linkRequests.AddOrUpdate(link, new StrongBox<string>(url));
        }

        var handler = FetchHandler;
        if (handler == null)
        {
            QueueLinkEvent(link, url, "error");
            return;
        }

        var destination = ResolveLinkFetchDestination(link);
        _ = Task.Run(async () =>
        {
            var succeeded = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", destination);
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
                if (string.Equals(destination, "style", StringComparison.Ordinal))
                {
                    request.Headers.TryAddWithoutValidation("Accept", "text/css,*/*;q=0.1");
                }
                if (_currentBaseUri != null)
                {
                    request.Headers.Referrer = _currentBaseUri;
                }

                using var response = await handler(request).ConfigureAwait(false);
                succeeded = response.IsSuccessStatusCode;
                if (succeeded && response.Content != null)
                {
                    // Drain the body so the response cache holds it for the
                    // cascade's own fetch of the same stylesheet.
                    _ = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn(
                    $"[FenJsBridge] link fetch failed for '{requestUri}': {ex.Message}",
                    LogCategory.JavaScript);
            }

            QueueLinkEvent(link, url, succeeded ? "load" : "error");
        });
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
    private void QueueLinkEvent(Element link, string url, string type)
    {
        if (_realmAbandoned)
        {
            return;
        }

        lock (_linkLoadSync)
        {
            // The href moved on while this fetch was in flight: that request
            // reports for itself.
            if (!_linkRequests.TryGetValue(link, out var current) ||
                !string.Equals(current.Value, url, StringComparison.Ordinal))
            {
                return;
            }
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
