using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Load and error events for <c>object</c> elements.
/// </summary>
/// <remarks>
/// HTML 4.8.7 "update the object element's representation": an object with a
/// data URL fetches it. If the fetch fails the user agent fires <c>error</c> at
/// the element; once the resource has completely loaded it fires <c>load</c>,
/// whether the object ends up showing a nested document or an image. Nothing
/// in the engine loads object elements, so neither event ever fired. Acid3
/// test 65 counts seven onload callbacks from its support files, one of them
/// an object's, and test 69 waited out its whole retry budget for the seventh.
/// The element's fallback content is left alone. Only the top realm observes,
/// as for images and links.
/// </remarks>
public sealed partial class FenJsBrowserScriptEngine
{
    private readonly object _objectLoadSync = new();
    // The data URL each tracked object was last fetched for; a completion for
    // an older URL is dropped. Weak per element so a document's objects do not
    // outlive it through here.
    private readonly ConditionalWeakTable<Element, StrongBox<string>> _objectRequests = new();
    private bool _objectLoadObserverSubscribed;

    private void EnsureObjectLoadObserver()
    {
        var owner = ImageLoadOwner;
        if (!ReferenceEquals(owner, this))
        {
            owner.EnsureObjectLoadObserver();
            return;
        }

        if (_objectLoadObserverSubscribed || _realmAbandoned)
        {
            return;
        }

        _objectLoadObserverSubscribed = true;
        Node.OnMutation += OnDomMutationForObjects;
    }

    private void UnsubscribeObjectLoadObserver()
    {
        if (!_objectLoadObserverSubscribed)
        {
            return;
        }

        _objectLoadObserverSubscribed = false;
        Node.OnMutation -= OnDomMutationForObjects;
    }

    private static bool IsObjectElement(Element element) =>
        string.Equals(element?.TagName, "object", StringComparison.OrdinalIgnoreCase);

    private void OnDomMutationForObjects(
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
                if (string.Equals(attributeName, "data", StringComparison.OrdinalIgnoreCase) &&
                    target is Element element &&
                    IsObjectElement(element) &&
                    OwnsDocumentForImageEvents(element.OwnerDocument))
                {
                    StartObjectRequest(element);
                }

                return;
            }

            if (!string.Equals(type, "childList", StringComparison.Ordinal) || addedNodes == null)
            {
                return;
            }

            for (var i = 0; i < addedNodes.Count; i++)
            {
                if (addedNodes[i] is Element added && OwnsDocumentForImageEvents(added.OwnerDocument))
                {
                    TrackObjectsInSubtree(added);
                }
            }
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] Object mutation walk failed: {ex.GetType().Name}: {ex.Message}",
                LogCategory.JavaScript);
        }
    }

    private void TrackObjectsInSubtree(Node root)
    {
        if (root == null)
        {
            return;
        }

        var owner = ImageLoadOwner;
        if (!ReferenceEquals(owner, this))
        {
            owner.TrackObjectsInSubtree(root);
            return;
        }

        if (root is Element rootElement && IsObjectElement(rootElement))
        {
            StartObjectRequest(rootElement);
        }

        foreach (var descendant in root.Descendants())
        {
            if (descendant is Element element && IsObjectElement(element))
            {
                StartObjectRequest(element);
            }
        }
    }

    /// <summary>
    /// Fetches the object's data resource and queues <c>load</c> or <c>error</c>
    /// at the element when it lands. The same URL seen again is the same request.
    /// </summary>
    private void StartObjectRequest(Element element)
    {
        var owner = ImageLoadOwner;
        if (!ReferenceEquals(owner, this))
        {
            owner.StartObjectRequest(element);
            return;
        }

        // A disconnected object has no representation to update; it is picked
        // up again when inserted.
        if (!element.IsConnected)
        {
            return;
        }

        // No data attribute: the object shows its fallback and fires nothing.
        var url = ResolveElementUrlProperty(element, "data");
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        lock (_objectLoadSync)
        {
            if (_objectRequests.TryGetValue(element, out var current) &&
                string.Equals(current.Value, url, StringComparison.Ordinal))
            {
                return;
            }

            _objectRequests.AddOrUpdate(element, new StrongBox<string>(url));
        }

        var handler = FetchHandler;
        if (handler == null || !Uri.TryCreate(url, UriKind.Absolute, out var requestUri))
        {
            // A data URL that does not parse fails the same way a fetch does.
            QueueObjectEvent(element, url, "error");
            return;
        }

        _ = Task.Run(async () =>
        {
            var succeeded = false;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "object");
                request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "no-cors");
                if (_currentBaseUri != null)
                {
                    request.Headers.Referrer = _currentBaseUri;
                }

                using var response = await handler(request).ConfigureAwait(false);
                // "If the load failed (e.g. there was an HTTP 404 error, there
                // was a DNS error), fire an event named error at the element."
                succeeded = response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn(
                    $"[FenJsBridge] object fetch failed for '{requestUri}': {ex.Message}",
                    LogCategory.JavaScript);
            }

            QueueObjectEvent(element, url, succeeded ? "load" : "error");
        });
    }

    // Always a queued task, never inline: the listener is attached in the same
    // job that set data or inserted the object.
    private void QueueObjectEvent(Element element, string url, string type)
    {
        if (_realmAbandoned)
        {
            return;
        }

        lock (_objectLoadSync)
        {
            // data moved on while this fetch was in flight: that request
            // reports for itself.
            if (!_objectRequests.TryGetValue(element, out var current) ||
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
                    element,
                    type,
                    new BrowserDomEventInit { Bubbles = false, Cancelable = false, Composed = false })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn(
                    $"[FenJsBridge] Object {type} event failed: {ex.GetType().Name}: {ex.Message}",
                    LogCategory.JavaScript);
            }
        });
    }
}
