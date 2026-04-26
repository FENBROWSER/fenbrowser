using System;
using System.Collections.Generic;
using System.Linq;
using FenBrowser.FenEngine.Core;
using FenBrowser.FenEngine.Core.Interfaces;

namespace FenBrowser.FenEngine.WebAPIs
{
    /// <summary>
    /// MediaStreamTrack per W3C Media Capture and Streams spec.
    /// https://www.w3.org/TR/mediacapture-streams/#mediastreamtrack
    /// </summary>
    public sealed class MediaStreamTrack : FenObject
    {
        public string Kind { get; }
        public string Id { get; }
        public string Label { get; }
        public bool Enabled { get; set; }
        public bool Muted { get; private set; }
        public string ReadyState { get; private set; }

        public MediaStreamTrack(string kind, string label)
        {
            Kind = kind;
            Id = Guid.NewGuid().ToString("N");
            Label = label;
            Enabled = true;
            Muted = false;
            ReadyState = "live";
            InternalClass = "MediaStreamTrack";
        }

        public void Stop()
        {
            ReadyState = "ended";
        }
    }

    /// <summary>
    /// MediaStream per W3C Media Capture and Streams spec.
    /// https://www.w3.org/TR/mediacapture-streams/#mediastream
    /// </summary>
    public sealed class MediaStream : FenObject
    {
        private readonly List<MediaStreamTrack> _audioTracks = new();
        private readonly List<MediaStreamTrack> _videoTracks = new();
        private readonly List<(string type, FenValue listener, bool capture)> _listeners = new();

        public string Id { get; }

        public MediaStream()
        {
            Id = GenerateId();
            InternalClass = "MediaStream";
        }

        private static string GenerateId()
        {
            return $"{(ulong)DateTime.UtcNow.Ticks:X16}-{Guid.NewGuid().ToString("N")[..8]}";
        }

        public FenValue Clone()
        {
            var clone = new MediaStream();
            foreach (var track in _audioTracks)
                clone.AddTrack(track);
            foreach (var track in _videoTracks)
                clone.AddTrack(track);
            return FenValue.FromObject(clone);
        }

        public void AddTrack(MediaStreamTrack track)
        {
            if (track.Kind == "audio")
                _audioTracks.Add(track);
            else if (track.Kind == "video")
                _videoTracks.Add(track);
        }

        public void RemoveTrack(MediaStreamTrack track)
        {
            _audioTracks.Remove(track);
            _videoTracks.Remove(track);
        }

        public FenObject GetAudioTracks()
        {
            var arr = new FenObject();
            arr.Set("length", FenValue.FromNumber(_audioTracks.Count));
            for (int i = 0; i < _audioTracks.Count; i++)
                arr.Set(i.ToString(), FenValue.FromObject(_audioTracks[i]));
            return arr;
        }

        public FenObject GetVideoTracks()
        {
            var arr = new FenObject();
            arr.Set("length", FenValue.FromNumber(_videoTracks.Count));
            for (int i = 0; i < _videoTracks.Count; i++)
                arr.Set(i.ToString(), FenValue.FromObject(_videoTracks[i]));
            return arr;
        }

        public FenObject GetTracks()
        {
            var arr = new FenObject();
            var allTracks = _audioTracks.Concat(_videoTracks).ToList();
            arr.Set("length", FenValue.FromNumber(allTracks.Count));
            for (int i = 0; i < allTracks.Count; i++)
                arr.Set(i.ToString(), FenValue.FromObject(allTracks[i]));
            return arr;
        }

        public FenValue GetTrackById(string id)
        {
            var allTracks = _audioTracks.Concat(_videoTracks);
            foreach (var track in allTracks)
            {
                if (track.Id == id)
                    return FenValue.FromObject(track);
            }
            return FenValue.Undefined;
        }

        public void AddEventListener(string type, FenValue listener, bool capture = false)
        {
            _listeners.Add((type, listener, capture));
        }

        public void RemoveEventListener(string type, FenValue listener, bool capture = false)
        {
            _listeners.RemoveAll(l => l.type == type && l.listener.Equals(listener) && l.capture == capture);
        }

        public FenValue DispatchEvent(FenValue eventArg)
        {
            return FenValue.FromBoolean(true);
        }
    }
}