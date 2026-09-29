using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.FenEngine.Rendering;

namespace FenBrowser.FenEngine.Scripting;

/// <summary>
/// Load and error events for images: HTML &lt;img&gt; ("update the image data", HTML
/// §4.8.4.3.5) and the SVG &lt;image&gt; element (SVG 2 §5.7). Whenever an image
/// element's source changes, at document setup, on insertion or on any attribute
/// mutation, the resource is fetched through the document's fetch pipeline and
/// decoded into the image cache, and a non-bubbling load or error event is queued
/// at the element. Only the latest source of an element fires: a request that was
/// superseded by a later change is dropped.
/// </summary>
public sealed partial class FenJsBrowserScriptEngine
{
    private sealed class ImageLoadTracker
    {
        public ImageLoadTracker(Document document) => Document = document;

        public Document Document { get; }

        /// <summary>The source each element last requested, so repeats do not refire.</summary>
        public ConditionalWeakTable<Element, string> Sources { get; } = new();

        public Action<Node, string, string, string, List<Node>, List<Node>> Handler { get; set; }
    }

    private ImageLoadTracker _imageLoads;

    /// <summary>
    /// Starts tracking the images of the document that was just set up. The
    /// process-wide mutation event is subscribed through a weak reference, so an
    /// engine that is no longer used can be collected and its handler removes itself.
    /// </summary>
    private void BeginImageLoadTracking(Node domRoot)
    {
        var document = domRoot as Document ?? domRoot?.OwnerDocument;
        if (document == null)
        {
            return;
        }

        EndImageLoadTracking();
        var tracker = new ImageLoadTracker(document);
        var weakEngine = new WeakReference<FenJsBrowserScriptEngine>(this);
        Action<Node, string, string, string, List<Node>, List<Node>> handler = null;
        handler = (target, type, attributeName, _, added, _) =>
        {
            if (!weakEngine.TryGetTarget(out var engine) || !ReferenceEquals(engine._imageLoads, tracker))
            {
                Node.OnMutation -= handler;
                return;
            }

            engine.OnImageRelevantMutation(tracker, target, type, attributeName, added);
        };
        tracker.Handler = handler;
        _imageLoads = tracker;
        Node.OnMutation += handler;

        foreach (var element in document.Descendants().OfType<Element>())
        {
            if (IsLoadEventImage(element))
            {
                UpdateImageData(tracker, element);
            }
        }
    }

    private void EndImageLoadTracking()
    {
        var tracker = _imageLoads;
        _imageLoads = null;
        if (tracker?.Handler != null)
        {
            Node.OnMutation -= tracker.Handler;
        }
    }

    private void OnImageRelevantMutation(
        ImageLoadTracker tracker, Node target, string type, string attributeName, List<Node> added)
    {
        if (target == null ||
            !ReferenceEquals(target as Document ?? target.OwnerDocument, tracker.Document))
        {
            return;
        }

        if (type == "attributes")
        {
            if (target is Element element && IsLoadEventImage(element) && IsImageSourceAttribute(element, attributeName))
            {
                UpdateImageData(tracker, element);
            }
            return;
        }

        if (type == "childList" && added != null)
        {
            foreach (var node in added)
            {
                foreach (var element in node.SelfAndDescendants().OfType<Element>())
                {
                    if (IsLoadEventImage(element))
                    {
                        UpdateImageData(tracker, element);
                    }
                }
            }
        }
    }

    private void UpdateImageData(ImageLoadTracker tracker, Element element)
    {
        string source = ImageSourceOf(element);
        if (tracker.Sources.TryGetValue(element, out string previous) &&
            string.Equals(previous, source, StringComparison.Ordinal))
        {
            return;
        }

        tracker.Sources.AddOrUpdate(element, source);
        if (source == null)
        {
            return;
        }

        _ = LoadImageForEventAsync(tracker, element, source);
    }

    private async Task LoadImageForEventAsync(ImageLoadTracker tracker, Element element, string source)
    {
        // Never fetch or dispatch inside the mutation that triggered the request.
        await Task.Yield();

        bool loaded = false;
        try
        {
            loaded = await TryLoadImageAsync(tracker.Document, element, source).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            EngineLog.Write(LogSubsystem.Img, LogSeverity.Debug,
                "[ImageLoadEvents] image request failed: " + ex.GetType().Name);
        }

        if (!ReferenceEquals(_imageLoads, tracker) ||
            !tracker.Sources.TryGetValue(element, out string current) ||
            !string.Equals(current, source, StringComparison.Ordinal))
        {
            return;
        }

        await DispatchEventForElementAsync(element, loaded ? "load" : "error", new BrowserDomEventInit
        {
            Bubbles = false,
            Cancelable = false,
            Composed = false
        }).ConfigureAwait(false);
        RequestRender?.Invoke();
    }

    /// <summary>
    /// Fetches and decodes the image into the image cache so painting reuses it.
    /// An empty source is an error (HTML: the selected source is the empty string).
    /// </summary>
    private async Task<bool> TryLoadImageAsync(Document document, Element element, string source)
    {
        if (source.Length == 0)
        {
            return false;
        }

        if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return ImageLoader.GetImage(source, ownerDocument: document) != null;
        }

        Uri baseUri = TryCreateAbsoluteUri(document.BaseURI) ?? _currentBaseUri;
        if (!Uri.TryCreate(baseUri, source, out Uri uri) || !uri.IsAbsoluteUri)
        {
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return ImageLoader.GetImage(uri.AbsoluteUri, ownerDocument: document) != null;
        }

        byte[] bytes = await ImageLoader.FetchBytesForCurrentContextAsync(uri, document).ConfigureAwait(false);
        if (bytes == null || bytes.Length == 0)
        {
            return false;
        }

        using var stream = new MemoryStream(bytes, writable: false);
        return await ImageLoader.PrewarmImageAsync(uri.AbsoluteUri, stream, ownerDocument: document).ConfigureAwait(false);
    }

    private static Uri TryCreateAbsoluteUri(string value) =>
        !string.IsNullOrWhiteSpace(value) && Uri.TryCreate(value, UriKind.Absolute, out Uri uri) ? uri : null;

    private static bool IsLoadEventImage(Element element) =>
        (string.Equals(element.LocalName, "img", StringComparison.OrdinalIgnoreCase) &&
         (element.NamespaceUri == null || element.NamespaceUri == Namespaces.Html)) ||
        (string.Equals(element.LocalName, "image", StringComparison.Ordinal) &&
         element.NamespaceUri == Namespaces.Svg);

    private static bool IsImageSourceAttribute(Element element, string attributeName) =>
        element.NamespaceUri == Namespaces.Svg
            ? attributeName is "href" or "xlink:href"
            : string.Equals(attributeName, "src", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The image's current source: src for HTML (null when absent), href or the
    /// legacy xlink:href for SVG.
    /// </summary>
    private static string ImageSourceOf(Element element) =>
        element.NamespaceUri == Namespaces.Svg
            ? element.GetAttribute("href") ?? element.GetAttribute("xlink:href")
            : element.GetAttribute("src")?.Trim();
}
