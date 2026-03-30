using System;

namespace FenBrowser.FenEngine.Rendering
{
    public class HistoryEntry
    {
        private Uri _url;
        private string _title = string.Empty;

        public Uri Url
        {
            get => _url;
            set => _url = value ?? throw new ArgumentNullException(nameof(value));
        }

        public string Title
        {
            get => _title;
            set => _title = string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        public object State { get; set; } // JSON serializable state object
        public bool IsPushState { get; set; }

        public bool HasState => State != null;

        public HistoryEntry(Uri url, string title = null, object state = null)
        {
            Url = url ?? throw new ArgumentNullException(nameof(url));
            Title = title;
            State = state;
        }

        public override string ToString()
        {
            return $"{Url} ({Title})";
        }
    }
}
