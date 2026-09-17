using System;
using System.Threading;
using System.Threading.Tasks;
using FenBrowser.Core;
using FenBrowser.Core.Dom.V2;
using FenBrowser.Core.Logging;
using FenBrowser.Core.Network;
using FenBrowser.Media;
using FenBrowser.Media.Diagnostics;
using FenBrowser.Media.Element;
using FenBrowser.Media.Pipeline;

namespace FenBrowser.FenEngine.Media
{
    /// <summary>
    /// The network side of a media element's "resource fetch algorithm" (HTML §4.8.11.5):
    /// fetches the URL through the browser's network stack, then hands the bytes to a
    /// <see cref="MediaPlayer"/> that reports to the element on its own thread.
    /// </summary>
    /// <remarks>
    /// The element sees one resource for the whole load. Commands that arrive before the
    /// player exists (a <c>play()</c> during the fetch, a seek) are remembered and applied
    /// the moment it does. A failed fetch reports <see cref="MediaResourceFailure.Network"/>;
    /// before any media data arrived the element treats that as the dedicated media source
    /// failure steps, exactly like an unsupported container.
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
        private readonly object _gate = new();
        private MediaPlayer _player;
        private (bool Playing, double Rate, bool PreservesPitch, double Volume)? _pendingPlayback;
        private (MediaTime Target, bool Approximate)? _pendingSeek;
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
            BinaryFetchResult result;
            try
            {
                result = await fetcher(_request, _document).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ReportFailure(MediaResourceFailure.Network, $"The media fetch threw {ex.GetType().Name}: {ex.Message}");
                return;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (result == null || !result.Succeeded)
            {
                ReportFailure(
                    MediaResourceFailure.Network,
                    result == null
                        ? "The media fetch returned nothing."
                        : $"The media fetch failed: {result.FailureReason} {result.StatusCode} {result.FailureDetail}".TrimEnd());
                return;
            }

            // The declared type only breaks probe ties; the bytes choose the demuxer.
            var declared = MimeEssence(result.ContentType);
            var context = new MediaPipelineContext(PlayerId.Next(), MediaLimits.Default, MediaEngineServices.Log);
            context.Log.Emit(context.Player, MediaEventKind.SourceSelected, MediaLogLevel.Info,
                $"Fetched {result.ByteCount} bytes ({declared ?? "no declared type"}).",
                ("bytes", result.ByteCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("declared", declared ?? string.Empty),
                ("destination", _request.IsVideo ? "video" : "audio"));

            MediaPlayer player;
            try
            {
                player = new MediaPlayer(
                    new MemoryByteSource(result.Body),
                    declared,
                    _client,
                    _postToElementThread,
                    MediaEngineServices.PlayerServices,
                    context);
            }
            catch (Exception ex)
            {
                ReportFailure(MediaResourceFailure.Unsupported, $"The player could not be created: {ex.Message}");
                return;
            }

            (bool Playing, double Rate, bool PreservesPitch, double Volume)? playback;
            (MediaTime Target, bool Approximate)? seek;
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                {
                    player.Dispose();
                    return;
                }

                _player = player;
                playback = _pendingPlayback;
                seek = _pendingSeek;
                _pendingPlayback = null;
                _pendingSeek = null;
            }

            player.Start();
            if (playback is { } p)
            {
                player.UpdatePlayback(p.Playing, p.Rate, p.PreservesPitch, p.Volume);
            }

            if (seek is { } s)
            {
                player.Seek(s.Target, s.Approximate);
            }
        }

        private static string MimeEssence(string contentType)
        {
            if (string.IsNullOrWhiteSpace(contentType))
            {
                return null;
            }

            var semicolon = contentType.IndexOf(';');
            var essence = (semicolon >= 0 ? contentType.Substring(0, semicolon) : contentType).Trim().ToLowerInvariant();
            return essence.Length == 0 ? null : essence;
        }

        private void ReportFailure(MediaResourceFailure failure, string message)
        {
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
            MediaPlayer player;
            lock (_gate)
            {
                player = _player;
                if (player == null)
                {
                    _pendingPlayback = (potentiallyPlaying, playbackRate, preservesPitch, effectiveVolume);
                    return;
                }
            }

            player.UpdatePlayback(potentiallyPlaying, playbackRate, preservesPitch, effectiveVolume);
        }

        public void Seek(MediaTime target, bool approximateForSpeed)
        {
            MediaPlayer player;
            lock (_gate)
            {
                player = _player;
                if (player == null)
                {
                    _pendingSeek = (target, approximateForSpeed);
                    return;
                }
            }

            player.Seek(target, approximateForSpeed);
        }

        public void RequestFullLoad()
        {
            // The whole body is fetched in one request already.
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            MediaPlayer player;
            lock (_gate)
            {
                player = _player;
                _player = null;
            }

            player?.Dispose();
        }
    }
}
