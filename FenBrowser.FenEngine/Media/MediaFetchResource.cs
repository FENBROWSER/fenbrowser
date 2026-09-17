using System;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
using FenBrowser.Media;
using FenBrowser.Media.Element;

namespace FenBrowser.FenEngine.Media
{
    /// <summary>
    /// The network side of a media element's "resource fetch algorithm" (HTML §4.8.11.5):
    /// fetches the URL through the browser's network stack and reports the outcome to the
    /// element on its own thread.
    /// </summary>
    /// <remarks>
    /// Until a demuxer is registered (design §8, M2) a fetched body cannot be played, so a
    /// successful fetch still ends in <see cref="MediaResourceFailure.Unsupported"/> - after
    /// the response, which is what keeps the element's timing honest: the selection pointer
    /// waits on the network like the spec says, rather than failing every candidate in the
    /// same task. A failed fetch reports <see cref="MediaResourceFailure.Network"/>; before
    /// any media data arrived the element treats both the same way (the dedicated media
    /// source failure steps).
    /// </remarks>
    public sealed class MediaFetchResource : IMediaResource
    {
        /// <summary>
        /// The browser's fetch for media requests (destination "audio" or "video"), wired by
        /// the host next to the image and font fetchers. Null outside a browser.
        /// </summary>
        public static Func<MediaFetchRequest, Document, Task<BinaryFetchResult>> FetchDetailedAsync { get; set; }

        private readonly MediaFetchRequest _request;
        private readonly IMediaResourceClient _client;
        private readonly Document _document;
        private readonly Action<Action> _postToElementThread;
        private int _disposed;

        private MediaFetchResource(
            MediaFetchRequest request,
            IMediaResourceClient client,
            Document document,
            Action<Action> postToElementThread)
        {
            _request = request;
            _client = client;
            _document = document;
            _postToElementThread = postToElementThread;
        }

        /// <summary>
        /// Starts a fetch for <paramref name="request"/>, or returns null when there is no
        /// fetcher (the element then treats the resource as unsupported at once).
        /// </summary>
        public static MediaFetchResource Start(
            MediaFetchRequest request,
            IMediaResourceClient client,
            Document document,
            Action<Action> postToElementThread)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(postToElementThread);

            var fetcher = FetchDetailedAsync;
            if (fetcher == null)
            {
                return null;
            }

            var resource = new MediaFetchResource(request, client, document, postToElementThread);
            _ = Task.Run(() => resource.RunAsync(fetcher));
            return resource;
        }

        private async Task RunAsync(Func<MediaFetchRequest, Document, Task<BinaryFetchResult>> fetcher)
        {
            MediaResourceFailure failure;
            string message;
            try
            {
                var result = await fetcher(_request, _document).ConfigureAwait(false);
                if (result == null || !result.Succeeded)
                {
                    failure = MediaResourceFailure.Network;
                    message = result == null
                        ? "The media fetch returned nothing."
                        : $"The media fetch failed: {result.FailureReason} {result.StatusCode} {result.FailureDetail}".TrimEnd();
                }
                else
                {
                    failure = MediaResourceFailure.Unsupported;
                    message = $"No demuxer can open this resource ({result.ContentType ?? "unknown type"}, {result.ByteCount} bytes).";
                }
            }
            catch (Exception ex)
            {
                failure = MediaResourceFailure.Network;
                message = $"The media fetch threw {ex.GetType().Name}: {ex.Message}";
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            try
            {
                _postToElementThread(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        _client.Failed(failure, message);
                    }
                });
            }
            catch (Exception ex)
            {
                EngineLogCompat.Warn(
                    $"[Media] Could not report a media fetch result: {ex.GetType().Name}: {ex.Message}",
                    LogCategory.Media);
            }
        }

        public void UpdatePlayback(bool potentiallyPlaying, double playbackRate, bool preservesPitch, double effectiveVolume)
        {
            // Nothing plays yet.
        }

        public void Seek(MediaTime target, bool approximateForSpeed)
        {
            // There is no timeline to seek in until media data can be read.
        }

        public void RequestFullLoad()
        {
            // The whole body is fetched in one request already.
        }

        public void Dispose() => Volatile.Write(ref _disposed, 1);
    }
}
