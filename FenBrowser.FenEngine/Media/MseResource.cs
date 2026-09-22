using System;
using System.Threading;
using FenBrowser.Media;
using FenBrowser.Media.Eme;
using FenBrowser.Media.Element;
using FenBrowser.Media.Mse;
using FenBrowser.Media.Pipeline;
using FenBrowser.Media.Video;

namespace FenBrowser.FenEngine.Media
{
    /// <summary>
    /// A media element's resource when its <c>src</c> is a blob URL for a <c>MediaSource</c>
    /// (MSE §2.4.2 "attaching to a media element"): a <see cref="MediaPlayer"/> over the
    /// MediaSource's source buffers. Disposing it runs the detach steps (§2.4.3) on the
    /// model and tells the script side, which closes the MediaSource object.
    /// </summary>
    public sealed class MseResource : IMediaResource
    {
        private readonly MediaPlayer _player;
        private readonly MediaSourceModel _model;
        private readonly IMediaResourceClient _client;
        private readonly Action<Action> _postToElementThread;
        private readonly Action _onDetached;
        private int _disposed;

        public MseResource(
            MediaSourceModel model,
            IMediaResourceClient client,
            Action<Action> postToElementThread,
            MediaPipelineContext context,
            VideoPresenter presenter,
            Action onDetached)
        {
            ArgumentNullException.ThrowIfNull(model);
            ArgumentNullException.ThrowIfNull(client);
            ArgumentNullException.ThrowIfNull(postToElementThread);
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(onDetached);
            _model = model;
            _client = client;
            _postToElementThread = postToElementThread;
            _onDetached = onDetached;
            _player = new MediaPlayer(model, client, postToElementThread, MediaEngineServices.PlayerServices, context, presenter);
        }

        public MediaSourceModel Model => _model;

        public void Start() => _player.Start();

        public void UpdatePlayback(bool potentiallyPlaying, double playbackRate, bool preservesPitch, double effectiveVolume) =>
            _player.UpdatePlayback(potentiallyPlaying, playbackRate, preservesPitch, effectiveVolume);

        public void Seek(MediaTime target, bool approximateForSpeed) => _player.Seek(target, approximateForSpeed);

        public void RequestFullLoad()
        {
            // Script appends the data; there is nothing to fetch.
        }

        public VideoPresenter Presenter => _player.Presenter;

        public void SetMediaKeys(IMediaKeySource keys) => _player.SetMediaKeys(keys);

        public bool IsProviderObject => true;

        public VideoPlaybackQuality? GetVideoPlaybackQuality() => _player.GetVideoPlaybackQuality();

        /// <summary>
        /// §2.4.7 "end of stream" with an error: the element runs its network or decode
        /// error steps (or, before any media data, the "cannot be fetched at all" steps).
        /// </summary>
        public void ReportEndOfStreamError(EndOfStreamError error)
        {
            if (Volatile.Read(ref _disposed) != 0 || error == EndOfStreamError.None)
            {
                return;
            }

            var failure = error == EndOfStreamError.Network ? MediaResourceFailure.Network : MediaResourceFailure.Decode;
            var message = error == EndOfStreamError.Network
                ? "MediaSource.endOfStream(\"network\")."
                : "MediaSource.endOfStream(\"decode\").";
            _postToElementThread(() =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    _client.Failed(failure, message);
                }
            });
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _player.Dispose();
            _model.Detach();
            _onDetached();
        }
    }
}
