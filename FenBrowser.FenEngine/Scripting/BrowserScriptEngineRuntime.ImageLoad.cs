using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Load and error events for <c>img</c> elements.
/// </summary>
/// <remarks>
/// HTML "update the image data" (4.8.4.3.x): once the image is available the
/// user agent queues an element task that fires <c>load</c> at the element;
/// if the fetch or decode fails it fires <c>error</c>; and <c>complete</c> is
/// false only while a request is in flight. The engine fetches images from a
/// URL-keyed cache driven by layout, which knows nothing about elements, so
/// this is where an element's current request is remembered and matched to
/// the loader's completion. Only the top realm observes; frame realms hand
/// their documents up to it, and dispatch routes back down to the frame.
/// </remarks>
public sealed partial class FenJsBrowserScriptEngine
{
    private sealed class ImageRequestState
    {
        public string Url;
        public bool Pending;
    }

    private readonly object _imageLoadSync = new();
    private readonly Dictionary<string, List<Element>> _imagesAwaitingUrl = new(StringComparer.Ordinal);
    // Weak per element: a document's images must not outlive it through here.
    // The pending lists above hold the only strong references, and only in flight.
    private readonly ConditionalWeakTable<Element, ImageRequestState> _imageRequests = new();
    private bool _imageLoadObserverSubscribed;

    private FenJsBrowserScriptEngine ImageLoadOwner => _parentRealmOwner?.ImageLoadOwner ?? this;

    private void EnsureImageLoadObserver()
    {
        var owner = ImageLoadOwner;
        if (!ReferenceEquals(owner, this))
        {
            owner.EnsureImageLoadObserver();
            return;
        }

        if (_imageLoadObserverSubscribed || _realmAbandoned)
        {
            return;
        }

        _imageLoadObserverSubscribed = true;
        Node.OnMutation += OnDomMutationForImages;
        ImageLoader.ImageLoadFinished += OnImageLoadFinished;
    }

    private void UnsubscribeImageLoadObserver()
    {
        if (!_imageLoadObserverSubscribed)
        {
            return;
        }

        _imageLoadObserverSubscribed = false;
        Node.OnMutation -= OnDomMutationForImages;
        ImageLoader.ImageLoadFinished -= OnImageLoadFinished;
        lock (_imageLoadSync)
        {
            _imagesAwaitingUrl.Clear();
        }
    }

    private static bool IsImgElement(Element element) =>
        string.Equals(element?.TagName, "img", StringComparison.OrdinalIgnoreCase);

    // Node.OnMutation is process-wide: only act on documents this realm hosts,
    // which are its own document and the documents of frames nested in it.
    private bool OwnsDocumentForImageEvents(Document document)
    {
        var top = _currentDomRoot as Document ?? _currentDomRoot?.OwnerDocument;
        if (top == null)
        {
            return false;
        }

        for (var depth = 0; document != null && depth < 32; depth++)
        {
            if (ReferenceEquals(document, top))
            {
                return true;
            }

            var frame = TryGetFrameElementForDocument(document);
            if (frame == null)
            {
                return false;
            }

            document = frame.OwnerDocument;
        }

        return false;
    }

    private void OnDomMutationForImages(
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
                if (string.Equals(attributeName, "src", StringComparison.OrdinalIgnoreCase) &&
                    target is Element element &&
                    IsImgElement(element) &&
                    OwnsDocumentForImageEvents(element.OwnerDocument))
                {
                    StartImageRequest(element);
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
                    TrackImagesInSubtree(added);
                }
            }
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] Image mutation walk failed: {ex.GetType().Name}: {ex.Message}",
                LogCategory.JavaScript);
        }
    }

    private void TrackImagesInSubtree(Node root)
    {
        if (root == null)
        {
            return;
        }

        var owner = ImageLoadOwner;
        if (!ReferenceEquals(owner, this))
        {
            owner.TrackImagesInSubtree(root);
            return;
        }

        if (root is Element rootElement && IsImgElement(rootElement))
        {
            StartImageRequest(rootElement);
        }

        foreach (var descendant in root.Descendants())
        {
            if (descendant is Element element && IsImgElement(element))
            {
                StartImageRequest(element);
            }
        }
    }

    /// <summary>
    /// Begins (or notes the completion of) the request for an element's
    /// current <c>src</c>. Called whenever the element or its src appears.
    /// </summary>
    private void StartImageRequest(Element image)
    {
        var owner = ImageLoadOwner;
        if (!ReferenceEquals(owner, this))
        {
            owner.StartImageRequest(image);
            return;
        }

        var src = image.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(src))
        {
            lock (_imageLoadSync)
            {
                ForgetImageRequest(image);
            }

            return;
        }

        var url = ResolveElementUrlProperty(image, "src");
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        lock (_imageLoadSync)
        {
            // The same src seen again (an insertion after the src was set, a
            // rescan) is the same request, finished or not.
            if (_imageRequests.TryGetValue(image, out var current) &&
                string.Equals(current.Url, url, StringComparison.Ordinal))
            {
                return;
            }

            ForgetImageRequest(image);
            _imageRequests.AddOrUpdate(image, new ImageRequestState { Url = url, Pending = true });
            if (!_imagesAwaitingUrl.TryGetValue(url, out var waiting))
            {
                waiting = new List<Element>();
                _imagesAwaitingUrl[url] = waiting;
            }

            waiting.Add(image);
        }

        // Kick the fetch under the host's loader scope so it shares layout's
        // cache. A cached (or synchronously decoded data:) image completes
        // right away; anything else completes through ImageLoadFinished.
        bool? completed = null;
        try
        {
            using (ImageLoaderScope?.Invoke())
            {
                var document = image.OwnerDocument;
                if (ImageLoader.ContainsCachedImage(url, document))
                {
                    completed = true;
                }
                else
                {
                    var bitmap = ImageLoader.GetImage(url, ownerDocument: document);
                    if (bitmap != null)
                    {
                        completed = true;
                    }
                    else if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        completed = false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            FenBrowser.Core.EngineLogCompat.Warn(
                $"[FenJsBridge] Image request for {url} failed to start: {ex.Message}",
                LogCategory.JavaScript);
            completed = false;
        }

        if (completed.HasValue)
        {
            OnImageLoadFinished(url, completed.Value);
        }
    }

    // Caller holds _imageLoadSync.
    private void ForgetImageRequest(Element image)
    {
        if (!_imageRequests.TryGetValue(image, out var state))
        {
            return;
        }

        _imageRequests.Remove(image);
        if (state.Pending && _imagesAwaitingUrl.TryGetValue(state.Url, out var waiting))
        {
            waiting.Remove(image);
            if (waiting.Count == 0)
            {
                _imagesAwaitingUrl.Remove(state.Url);
            }
        }
    }

    private void OnImageLoadFinished(string url, bool success)
    {
        if (_realmAbandoned || string.IsNullOrEmpty(url))
        {
            return;
        }

        List<Element> images;
        lock (_imageLoadSync)
        {
            if (!_imagesAwaitingUrl.Remove(url, out images))
            {
                return;
            }

            foreach (var image in images)
            {
                if (_imageRequests.TryGetValue(image, out var current) &&
                    string.Equals(current.Url, url, StringComparison.Ordinal))
                {
                    current.Pending = false;
                }
            }
        }

        foreach (var image in images)
        {
            QueueImageEvent(image, success ? "load" : "error");
        }
    }

    // Always a queued task, never inline: the listener that wants this event
    // is typically attached in the same job that set src, and a dispatch from
    // the worker thread would run before it exists.
    private void QueueImageEvent(Element image, string type)
    {
        if (_realmAbandoned)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await DispatchEventForElementAsync(
                    image,
                    type,
                    new BrowserDomEventInit { Bubbles = false, Cancelable = false, Composed = false })
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                FenBrowser.Core.EngineLogCompat.Warn(
                    $"[FenJsBridge] Image {type} event failed: {ex.GetType().Name}: {ex.Message}",
                    LogCategory.JavaScript);
            }
        });
    }

    /// <summary>HTML: complete is false only while the current request is in flight.</summary>
    internal bool IsImageComplete(Element image)
    {
        var owner = ImageLoadOwner;
        if (!ReferenceEquals(owner, this))
        {
            return owner.IsImageComplete(image);
        }

        lock (_imageLoadSync)
        {
            return !_imageRequests.TryGetValue(image, out var state) || !state.Pending;
        }
    }

    /// <summary>Intrinsic size of the cached image, or 0x0 while unavailable.</summary>
    internal (int Width, int Height) GetImageNaturalSize(Element image)
    {
        var url = ResolveElementUrlProperty(image, "src");
        if (string.IsNullOrWhiteSpace(url))
        {
            return (0, 0);
        }

        try
        {
            using (ImageLoaderScope?.Invoke())
            {
                var document = image.OwnerDocument;
                if (!ImageLoader.ContainsCachedImage(url, document))
                {
                    return (0, 0);
                }

                var bitmap = ImageLoader.GetImage(url, ownerDocument: document);
                return bitmap == null ? (0, 0) : (bitmap.Width, bitmap.Height);
            }
        }
        catch
        {
            return (0, 0);
        }
    }
}
