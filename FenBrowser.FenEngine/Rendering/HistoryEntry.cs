using System;

namespace FenBrowser.FenEngine.Rendering
{
    public class HistoryEntry
    {
        public Uri Url { get; set; }
        public string Title { get; set; }
        public object State { get; set; } // JSON serializable state object
        public bool IsPushState { get; set; }

        public HistoryEntry(Uri url, string title = null, object state = null)
        {
            Url = url;
            Title = title;
            State = state;
        }
    }
}
