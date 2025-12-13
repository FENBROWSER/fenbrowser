using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace FenBrowser.UI
{
    /// <summary>
    /// Manages user bookmarks with persistence
    /// </summary>
    public class BookmarkManager
    {
        private static BookmarkManager _instance;
        public static BookmarkManager Instance => _instance ??= new BookmarkManager();
        
        private readonly string _bookmarksFile;
        private Dictionary<string, BookmarkEntry> _bookmarks = new Dictionary<string, BookmarkEntry>(StringComparer.OrdinalIgnoreCase);
        
        private BookmarkManager()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var fenDir = Path.Combine(appData, "FenBrowser");
            Directory.CreateDirectory(fenDir);
            _bookmarksFile = Path.Combine(fenDir, "bookmarks.json");
            
            Load();
        }
        
        public void AddBookmark(string url, string title, string folder = "Favorites")
        {
            _bookmarks[url] = new BookmarkEntry
            {
                Url = url,
                Title = title,
                Folder = folder,
                DateAdded = DateTime.UtcNow
            };
            Save();
            System.Diagnostics.Debug.WriteLine($"[Bookmarks] Added: {title} ({url})");
        }
        
        public void RemoveBookmark(string url)
        {
            if (_bookmarks.Remove(url))
            {
                Save();
                System.Diagnostics.Debug.WriteLine($"[Bookmarks] Removed: {url}");
            }
        }
        
        public bool IsBookmarked(string url)
        {
            return _bookmarks.ContainsKey(url);
        }
        
        public IEnumerable<BookmarkEntry> GetAllBookmarks()
        {
            return _bookmarks.Values;
        }
        
        public IEnumerable<BookmarkEntry> GetBookmarksInFolder(string folder)
        {
            foreach (var bm in _bookmarks.Values)
            {
                if (string.Equals(bm.Folder, folder, StringComparison.OrdinalIgnoreCase))
                    yield return bm;
            }
        }
        
        private void Load()
        {
            try
            {
                if (File.Exists(_bookmarksFile))
                {
                    var json = File.ReadAllText(_bookmarksFile);
                    var list = JsonSerializer.Deserialize<List<BookmarkEntry>>(json);
                    if (list != null)
                    {
                        _bookmarks.Clear();
                        foreach (var bm in list)
                        {
                            _bookmarks[bm.Url] = bm;
                        }
                    }
                    System.Diagnostics.Debug.WriteLine($"[Bookmarks] Loaded {_bookmarks.Count} bookmarks");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Bookmarks] Load failed: {ex.Message}");
            }
        }
        
        private void Save()
        {
            try
            {
                var list = new List<BookmarkEntry>(_bookmarks.Values);
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_bookmarksFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Bookmarks] Save failed: {ex.Message}");
            }
        }
    }
    
    public class BookmarkEntry
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public string Folder { get; set; }
        public string Favicon { get; set; }
        public DateTime DateAdded { get; set; }
    }
}
